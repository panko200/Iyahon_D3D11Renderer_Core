using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Iyahon_D3D11Renderer_Core.Lighting;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

internal enum ObjRenderMode
{
    Opaque,
    SemiTransDepthOnly,
    SemiTransColor,
    OIT,
    Shadow,
    TransmissiveDepthOnly,
    Transmissive,
    Silhouette,
}

internal sealed class ObjModelRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CbObjModel
    {
        public Matrix4x4 WorldMatrix;    // 64
        public Matrix4x4 ViewProjMatrix; // 64 -> 128
        public Matrix4x4 NormalMatrix;   // 64 -> 192
        public float HalfWidth;          // 4
        public float HalfHeight;         // 4
        public float Opacity;            // 4
        public float MinAlphaVal;        // 4  -> 208
        public Vector4 BaseColor;        // 16 -> 224
        public Vector4 MaterialParams;   // 16 -> 240 : x = Metallic, y = Roughness (<0 => DefaultRoughness), z = Clearcoat, w = HasEmissiveMap
        public Vector4 EmissiveColor;    // 16 -> 256 : xyz = linear emissive, w = intensity
        public Vector4 DetailParams;     // 16 -> 272 : x = NormalScale, y = HasNormalMap, z = RoughMetalPacking, w = UvScale
        public Vector4 GlassParams;      // 16 -> 288 : x = Transmission, y = IOR, z = HasTransmissionMap, w = HasSceneBg
        public Vector4 ScreenParams;     // 16 -> 304 : x = RcpBgWidth, y = RcpBgHeight, z = BgMaxMip, w = RefractFullEnv
        public Vector4 ExtendedPbr0;     // 16 -> 320 : x = CoatWeight, y = CoatRoughness, z = CoatIor, w = DiffuseRoughness
        public Vector4 ExtendedPbr1;     // 16 -> 336 : x = ThinFilmThickness, y = ThinFilmIor, z = Anisotropic, w = AnisoRotationRad
        public Vector4 ExtendedPbr2;     // 16 -> 352 : x = SubsurfaceWeight, y = SubsurfaceScale, z = SheenWeight, w = SheenRoughness
        public Vector4 SubsurfaceColor;  // 16 -> 368 : xyz = linear SSS tint, w = IsDiffuseOnly
        public Vector4 SheenTint;        // 16 -> 384 : xyz = linear sheen tint, w = pad
        public Vector4 ShaderLutParams;  // 16 -> 400 : x = LutMode (0=Off,1=Toon,2=View,3=AO), y = ParamA, z = ParamB, w = EmissiveMax
        public Vector4 WireframeParams;  // 16 -> 416 : x = WireMode, y = Size, z = PixelSize, w = pad
        public Vector4 WireBaseColor;    // 16 -> 432 : rgb = sRGB wire color, w = WireEmissiveStrength
        public Vector4 WireEmissiveColor;// 16 -> 448 : rgb = sRGB wire emissive, w = pad
        public Vector4 LayerUvParams;    // 16 -> 464 : x = ScaleX, y = ScaleY, z = OffsetX, w = OffsetY
        public Vector4 LayerUvExtra;     // 16 -> 480 bytes (16B aligned) : x = RotRad, y = FlipV, z = HasLayerAlbedo, w = HasLayerEmissive
    }

    private const int ObjVertexStride = 48;

    private ID3D11VertexShader? _vs;
    private ID3D11GeometryShader? _gsWireframe;
    private ID3D11PixelShader? _psOpaque;
    private ID3D11PixelShader? _psSemiTrans;
    private ID3D11PixelShader? _psSemiDepthOnly;
    private ID3D11PixelShader? _psOIT;
    private ID3D11PixelShader? _psShadowAlpha;
    private ID3D11PixelShader? _psTransmissive;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _cbBuffer;
    private ID3D11SamplerState? _sampler;
    private ID3D11SamplerState? _detailSampler;
    private ID3D11RasterizerState? _rasterizerCullBack;
    [StructLayout(LayoutKind.Sequential)]
    private struct CbObjOutline
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 ViewProjMatrix;
        public Matrix4x4 NormalMatrix;
        public Vector4 OutlineColor;
        public Vector4 OutlineParams;   // x=ProjHalfW, y=ProjHalfH, z=Thickness, w=WorldSpace
        public Vector4 ViewportParams;  // x=TargetHalfW, y=TargetHalfH, z=DepthBias, w=UseSmoothNormals
        public Vector4 ExtraParams;     // x=Opacity
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbObjSil
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 ViewProjMatrix;
        public Vector4 BaseColor;
        public Vector4 Params; // x=ProjHalfW, y=ProjHalfH, z=Opacity, w=MinAlpha
    }

    private const string OutlineShaderSource = @"
cbuffer CbObjOutline : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    row_major float4x4 NormalMatrix;
    float4 OutlineColor;
    float4 OutlineParams;
    float4 ViewportParams;
    float4 ExtraParams;
};

struct VSOutlineIn
{
    float3 Pos        : POSITION;
    float3 Normal     : NORMAL;
    float3 SmoothNorm : TEXCOORD1;
};

struct PSOutlineIn
{
    float4 Pos : SV_POSITION;
};

float4 ProjectOutlineClip(float4 wp)
{
    float4 pos = mul(wp, ViewProjMatrix);
    if (OutlineParams.x > 0.0)
    {
        float4 o;
        o.x =  pos.x / OutlineParams.x;
        o.y = -pos.y / OutlineParams.y;
        o.z = -pos.z / 200000.0 + 0.5 * pos.w;
        o.w =  pos.w;
        return o;
    }
    return pos;
}

PSOutlineIn VS_ObjOutline(VSOutlineIn input)
{
    PSOutlineIn o;
    float3 rawN = (ViewportParams.w > 0.5 && dot(input.SmoothNorm, input.SmoothNorm) > 1e-8)
        ? input.SmoothNorm
        : input.Normal;
    if (dot(rawN, rawN) <= 1e-8)
    {
        rawN = dot(input.Pos, input.Pos) > 1e-8 ? input.Pos : float3(0.0, 1.0, 0.0);
    }
    rawN = normalize(rawN);

    float4 worldPos = mul(float4(input.Pos, 1.0), WorldMatrix);
    float3 worldN = mul(rawN, (float3x3)NormalMatrix);
    float wnLenSq = dot(worldN, worldN);
    worldN = (wnLenSq > 1e-10) ? (worldN * rsqrt(wnLenSq)) : float3(0.0, 1.0, 0.0);

    float4 clipPos;
    if (OutlineParams.w > 0.5)
    {
        worldPos.xyz += worldN * OutlineParams.z;
        clipPos = ProjectOutlineClip(worldPos);
    }
    else
    {
        clipPos = ProjectOutlineClip(worldPos);
        float4 clipPosN = ProjectOutlineClip(float4(worldPos.xyz + worldN * 0.5, 1.0));
        float w0 = max(abs(clipPos.w), 1e-4);
        float w1 = max(abs(clipPosN.w), 1e-4);
        float2 ndcDir = (clipPosN.xy / w1) - (clipPos.xy / w0);
        float thW = max(ViewportParams.x, 1.0);
        float thH = max(ViewportParams.y, 1.0);
        float2 pxDir = float2(ndcDir.x * thW, ndcDir.y * thH);
        float pxLenSq = dot(pxDir, pxDir);
        if (pxLenSq > 1e-10 && clipPos.w > 0.0)
        {
            pxDir *= rsqrt(pxLenSq);
            float2 ndcOffset = float2(pxDir.x / thW, pxDir.y / thH) * OutlineParams.z;
            clipPos.xy += ndcOffset * clipPos.w;
        }
    }
    float depthBias = (OutlineParams.x > 0.0) ? (ViewportParams.z * 0.1) : ViewportParams.z;
    clipPos.z += depthBias * clipPos.w;
    o.Pos = clipPos;
    return o;
}

float4 PS_ObjOutline(PSOutlineIn input) : SV_Target
{
    float a = saturate(OutlineColor.a * ExtraParams.x);
    clip(a - 0.004);
    return float4(OutlineColor.rgb * a, a);
}
";

    private const string SilhouetteShaderSource = @"
cbuffer CbObjSil : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    float4 BaseColor;
    float4 Params;
};

Texture2D    gTex     : register(t0);
SamplerState gSampler : register(s0);

struct VSSilIn
{
    float3 Pos    : POSITION;
    float3 Normal : NORMAL;
    float2 UV     : TEXCOORD0;
    float4 Color  : COLOR0;
};

struct PSSilIn
{
    float4 Pos   : SV_POSITION;
    float2 UV    : TEXCOORD0;
    float4 Color : COLOR0;
};

PSSilIn VS_ObjSil(VSSilIn input)
{
    PSSilIn o;
    float4 wp = mul(float4(input.Pos, 1.0), WorldMatrix);
    float4 pos = mul(wp, ViewProjMatrix);
    if (Params.x > 0.0)
    {
        o.Pos.x =  pos.x / Params.x;
        o.Pos.y = -pos.y / Params.y;
        o.Pos.z = -pos.z / 200000.0 + 0.5 * pos.w;
        o.Pos.w =  pos.w;
    }
    else
    {
        o.Pos = pos;
    }
    o.UV = input.UV;
    o.Color = input.Color;
    return o;
}

float4 PS_ObjSil(PSSilIn input) : SV_Target
{
    float4 texColor = gTex.Sample(gSampler, input.UV);
    float4 vc = input.Color;
    if (vc.r == 0.0 && vc.g == 0.0 && vc.b == 0.0 && vc.a == 0.0)
        vc = float4(1.0, 1.0, 1.0, 1.0);
    float a = saturate(texColor.a * vc.a * BaseColor.a * Params.z);
    clip(a - Params.w);
    return float4(a, a, a, a);
}
";

    private sealed class SmoothNormalFallbackEntry : IDisposable
    {
        public ID3D11Buffer Buffer;
        public IntPtr DevicePtr;
        private bool _disposed;

        public SmoothNormalFallbackEntry(ID3D11Buffer buffer, IntPtr devicePtr)
        {
            Buffer = buffer;
            DevicePtr = devicePtr;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Buffer?.Dispose(); } catch { }
        }

        ~SmoothNormalFallbackEntry() => Dispose();
    }

    private ID3D11RasterizerState? _rasterizerCullBackCw;
    private ID3D11RasterizerState? _rasterizerCullNone;
    private ID3D11RasterizerState? _rasterizerCullFrontCcw;
    private ID3D11RasterizerState? _rasterizerCullFrontCw;
    private ID3D11VertexShader? _vsOutline;
    private ID3D11PixelShader? _psOutline;
    private ID3D11InputLayout? _inputLayoutOutline;
    private ID3D11Buffer? _cbOutline;
    private ID3D11VertexShader? _vsSil;
    private ID3D11PixelShader? _psSil;
    private ID3D11Buffer? _cbSil;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ID3D11Buffer, SmoothNormalFallbackEntry> _fallbackSmoothNormals = new();
    private ID3D11Texture2D? _whiteTexture;
    private ID3D11ShaderResourceView? _whiteSrv;
    private ID3D11Texture2D? _flatNormalTexture;
    private ID3D11ShaderResourceView? _flatNormalSrv;
    private IntPtr _devicePointer = IntPtr.Zero;
    private bool _initialized;
    private bool _disposed;

    private const string ShaderSourcePrefix = @"
cbuffer CbObjModel : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    row_major float4x4 NormalMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float MinAlphaVal;
    float4 BaseColor;
    float4 MaterialParams;
    float4 EmissiveColor;
    float4 DetailParams;
    float4 GlassParams;
    float4 ScreenParams;
    float4 ExtendedPbr0;
    float4 ExtendedPbr1;
    float4 ExtendedPbr2;
    float4 SubsurfaceColor;
    float4 SheenTint;
    float4 ShaderLutParams;
    float4 WireframeParams;
    float4 WireBaseColor;
    float4 WireEmissiveColor;
    float4 LayerUvParams;
    float4 LayerUvExtra;
};
";

    private static readonly string ShaderSource = ShaderSourcePrefix + LightingShaderCode.HlslCode + @"
struct VSInput
{
    float3 Pos    : POSITION;
    float3 Normal : NORMAL;
    float2 UV     : TEXCOORD0;
    float4 Color  : COLOR;
};

struct PSInput
{
    float4 Pos      : SV_POSITION;
    float3 Normal   : TEXCOORD0;
    float2 UV       : TEXCOORD1;
    float4 Color    : TEXCOORD2;
    float  Op       : TEXCOORD3;
    float3 WorldPos : TEXCOORD4;
    float3 Bary     : TEXCOORD5;
};

struct OITOutput
{
    float4 Accum  : SV_Target0;
    float  Reveal : SV_Target1;
};

Texture2D    gTex          : register(t0);
Texture2D    gNormalMap    : register(t1);
Texture2D    gRoughMetal   : register(t6);
Texture2D    gEmissiveMap  : register(t7);
Texture2D    gSceneBg      : register(t8);
Texture2D    gTransMap     : register(t9);
Texture2D    gShaderLut    : register(t10);
SamplerState gSampler      : register(s0);
SamplerState gDetailSampler: register(s4);

PSInput VS_Obj(VSInput input)
{
    PSInput o;
    float4 worldPos = mul(float4(input.Pos, 1.0), WorldMatrix);

    if (HalfWidth > 0.0)
    {
        float4 pos = mul(worldPos, ViewProjMatrix);
        o.Pos.x =  pos.x / HalfWidth;
        o.Pos.y = -pos.y / HalfHeight;
        o.Pos.z = -pos.z / 200000.0 + 0.5 * pos.w;
        o.Pos.w =  pos.w;
    }
    else
    {
        o.Pos = mul(worldPos, ViewProjMatrix);
    }

    o.Normal = normalize(mul(input.Normal, (float3x3)NormalMatrix));
    o.UV = input.UV;
    o.Color = input.Color;
    o.Op = Opacity;
    o.WorldPos = worldPos.xyz;
    o.Bary = float3(1.0, 1.0, 1.0);
    return o;
}

[maxvertexcount(3)]
void GS_Obj_Wireframe(triangle PSInput input[3], inout TriangleStream<PSInput> stream)
{
    PSInput v0 = input[0]; v0.Bary = float3(1.0, 0.0, 0.0); stream.Append(v0);
    PSInput v1 = input[1]; v1.Bary = float3(0.0, 1.0, 0.0); stream.Append(v1);
    PSInput v2 = input[2]; v2.Bary = float3(0.0, 0.0, 1.0); stream.Append(v2);
    stream.RestartStrip();
}

float2 ApplyLayerUv(float2 uv)
{
    float vy = LayerUvExtra.y > 0.5 ? (1.0 - uv.y) : uv.y;
    float px = uv.x * LayerUvParams.x;
    float py = vy * LayerUvParams.y;
    float s, c;
    sincos(LayerUvExtra.x, s, c);
    return float2(px * c - py * s + LayerUvParams.z, px * s + py * c + LayerUvParams.w);
}

float ComputeWireframeFactor(float3 bary, float sizeVal, float isPixelSize)
{
    if (isPixelSize > 0.5)
    {
        float3 d = max(fwidth(bary), 1e-6);
        float3 distPx = bary / d;
        float minDistPx = min(distPx.x, min(distPx.y, distPx.z));
        float halfW = max(sizeVal, 0.25) * 0.5;
        return 1.0 - smoothstep(max(halfW - 0.75, 0.0), halfW + 0.75, minDistPx);
    }
    else
    {
        float3 d = max(fwidth(bary), 1e-5);
        float thresh = clamp(sizeVal * 0.08, 0.005, 0.45);
        float minB = min(bary.x, min(bary.y, bary.z));
        float aa = min(d.x, min(d.y, d.z)) * 1.25;
        return 1.0 - smoothstep(max(thresh - aa, 0.0), thresh + aa, minB);
    }
}

float3 CalcSimpleLgt(float3 normal)
{
    float3 lightDir = normalize(float3(0.3, 0.7, -1.0));
    float ndl = saturate(dot(normal, -lightDir));
    float3 diffuse = float3(1.0, 0.95, 0.9) * ndl;
    float3 ambient = float3(0.3, 0.3, 0.35);
    return ambient + diffuse;
}

float4 SampleObjBase(PSInput input)
{
    float2 baseUv = LayerUvExtra.z > 0.5 ? ApplyLayerUv(input.UV) : input.UV;
    float4 texColor = gTex.Sample(gSampler, baseUv);
    if (LayerUvExtra.z > 0.5 && texColor.a > 1e-4 && texColor.a < 0.999)
    {
        texColor.rgb = texColor.rgb / texColor.a;
    }
    float4 vertexColor = input.Color;
    if (vertexColor.r == 0.0 && vertexColor.g == 0.0 && vertexColor.b == 0.0 && vertexColor.a == 0.0)
    {
        vertexColor = float4(1.0, 1.0, 1.0, 1.0);
    }
    return texColor * vertexColor * BaseColor;
}

float2 ShaderLutUv(float x, float rowV)
{
    return float2(saturate(x) * (1023.0 / 1024.0) + (0.5 / 1024.0), rowV);
}

float3 ComputeObjLighting(
    PSInput input,
    inout float3 albedoLinear,
    out float3 outNormal,
    out float outMetallic,
    out float outRoughness,
    out float outClearcoat)
{
    float3 n = normalize(input.Normal);
    float3 V = CameraWorldPosAndPageSize.xyz - input.WorldPos;
    float vLenSq = dot(V, V);
    V = (vLenSq > 1e-6) ? (V * rsqrt(vLenSq)) : float3(0.0, 0.0, 1.0);
    if (dot(n, V) < 0.0) n = -n;

    float2 dUv = input.UV * max(DetailParams.w, 0.001);
    if (DetailParams.y > 0.5)
    {
        float4 normSample = gNormalMap.Sample(gDetailSampler, dUv);
        n = PerturbNormalCotangent(n, input.WorldPos, dUv, normSample, DetailParams.x);
    }

    float metallic = MaterialParams.x;
    float roughness = (MaterialParams.y >= 0.0) ? MaterialParams.y : DefaultRoughness;
    float clearcoat = MaterialParams.z;
    if (DetailParams.z > 0.5)
    {
        float4 rmSample = gRoughMetal.Sample(gDetailSampler, dUv);
        ApplyRoughMetalSample(rmSample, DetailParams.z, roughness, metallic);
    }

    float lutMode = ShaderLutParams.x;
    float3 lutExtraEmissive = float3(0.0, 0.0, 0.0);

    // LayerWeight / Fresnel View-Dependent 1D LUT
    if (abs(lutMode - 2.0) < 0.25)
    {
        float ndvView = saturate(dot(n, V));
        float4 lutCol = gShaderLut.SampleLevel(gSampler, ShaderLutUv(ndvView, 0.25), 0);
        float4 lutEm = gShaderLut.SampleLevel(gSampler, ShaderLutUv(ndvView, 0.75), 0);
        if (ShaderLutParams.y > 0.5)
        {
            float3 colLin = SrgbToLinearUnclamped(lutCol.rgb);
            albedoLinear = (ShaderLutParams.z > 0.5) ? (albedoLinear * colLin) : colLin;
        }
        if (ShaderLutParams.w > 1e-4)
        {
            lutExtraEmissive = SrgbToLinearUnclamped(lutEm.rgb) * ShaderLutParams.w;
        }
    }
    // Ambient Occlusion Real-Time Cavity + Hemisphere LUT
    else if (abs(lutMode - 3.0) < 0.25)
    {
        float3 dPdx = ddx(input.WorldPos);
        float3 dPdy = ddy(input.WorldPos);
        float3 dNdx = ddx(n);
        float3 dNdy = ddy(n);
        float curv = dot(dNdx, dPdx) / max(dot(dPdx, dPdx), 1e-5)
                   + dot(dNdy, dPdy) / max(dot(dPdy, dPdy), 1e-5);
        float aoScale = max(ShaderLutParams.z, 0.05) * 40.0;
        // Normal concavity gives negative curvature on concave corners; flip for Inside
        float signedCurv = (ShaderLutParams.w > 0.5) ? -curv : curv;
        float hemiAo = saturate(0.76 + 0.24 * (-n.y));
        float aoVal = saturate(1.0 + min(signedCurv * aoScale, 0.0)) * hemiAo;
        float4 lutAo = gShaderLut.SampleLevel(gSampler, ShaderLutUv(aoVal, 0.25), 0);
        float3 aoLin = SrgbToLinearUnclamped(lutAo.rgb);
        albedoLinear = (ShaderLutParams.y > 0.5) ? (albedoLinear * aoLin) : aoLin;
    }

    // Wireframe Overlay on BaseColor + Emissive
    float wireFac = 0.0;
    if (WireframeParams.x > 0.5)
    {
        wireFac = ComputeWireframeFactor(input.Bary, WireframeParams.y, WireframeParams.z);
        if (wireFac > 0.001)
        {
            albedoLinear = lerp(albedoLinear, SrgbToLinearUnclamped(WireBaseColor.rgb), wireFac);
            lutExtraEmissive += SrgbToLinearUnclamped(WireEmissiveColor.rgb) * WireBaseColor.w * wireFac;
        }
    }

    ExtendedPbrParams ext;
    ext.CoatWeight = max(clearcoat, ExtendedPbr0.x);
    ext.CoatRoughness = clamp(ExtendedPbr0.y > 0.001 ? ExtendedPbr0.y : 0.08, 0.03, 1.0);
    ext.CoatIor = ExtendedPbr0.z > 1.0 ? ExtendedPbr0.z : 1.5;
    ext.DiffuseRoughness = saturate(ExtendedPbr0.w);
    ext.ThinFilmThickness = max(ExtendedPbr1.x, 0.0);
    ext.ThinFilmIor = ExtendedPbr1.y > 1.0 ? ExtendedPbr1.y : 1.33;
    ext.Anisotropic = ExtendedPbr1.z;
    ext.AnisoRotationRad = ExtendedPbr1.w;
    ext.SubsurfaceWeight = saturate(ExtendedPbr2.x);
    ext.SubsurfaceScale = max(ExtendedPbr2.y, 0.0);
    ext.SheenWeight = saturate(ExtendedPbr2.z);
    ext.SheenRoughness = clamp(ExtendedPbr2.w > 0.001 ? ExtendedPbr2.w : 0.5, 0.05, 1.0);
    ext.SubsurfaceColorLinear = max(SubsurfaceColor.rgb, 0.0);
    ext.IsDiffuseOnly = SubsurfaceColor.w;
    ext.SheenTintLinear = max(SheenTint.rgb, 0.0);
    if (abs(ext.Anisotropic) > 0.001)
    {
        ComputeAnisoTangentFrame(input.WorldPos, n, input.UV, ext.AnisoRotationRad, ext.Tangent, ext.Bitangent);
    }
    else
    {
        ext.Tangent = float3(1.0, 0.0, 0.0);
        ext.Bitangent = float3(0.0, 1.0, 0.0);
    }

    float3 lit;
    if (abs(lutMode - 1.0) < 0.25)
    {
        // ShaderToRgb (Toon / Cel Shading): capture real scene lights + shadows then sample LUT
        float3 dDiff, dSpec, amb;
        if (UseSimpleLight > 0.5)
        {
            CalcSimpleLgtPbrExtended(albedoLinear, n, input.WorldPos, metallic, roughness, 0.75, 0.04, ext, dDiff, dSpec);
            amb = float3(0.0, 0.0, 0.0);
        }
        else
        {
            CalcDynamicPbrPartsExtended(albedoLinear, n, input.WorldPos, input.Pos.xy, metallic, roughness, 0.04, ext, dDiff, dSpec, amb);
        }
        float3 rawLit = dDiff + dSpec + amb;
        float litLum = dot(max(rawLit, 0.0), float3(0.2126, 0.7152, 0.0722));
        float rampCoord = pow(saturate(litLum), 0.55);
        float4 toonCol = gShaderLut.SampleLevel(gSampler, ShaderLutUv(rampCoord, 0.25), 0);
        float4 toonEm = gShaderLut.SampleLevel(gSampler, ShaderLutUv(rampCoord, 0.75), 0);
        if (ShaderLutParams.y > 0.5)
        {
            lit = SrgbToLinearUnclamped(toonCol.rgb) * max(ShaderLutParams.z, 1.0)
                + SrgbToLinearUnclamped(toonEm.rgb) * ShaderLutParams.w
                + lutExtraEmissive;
        }
        else
        {
            lit = rawLit * SrgbToLinearUnclamped(toonCol.rgb)
                + SrgbToLinearUnclamped(toonEm.rgb) * ShaderLutParams.w
                + lutExtraEmissive;
        }
    }
    else
    {
        bool useExt = (ext.CoatWeight > 0.001 || ext.ThinFilmThickness > 1.0 || abs(ext.Anisotropic) > 0.001 ||
                       ext.SubsurfaceWeight > 0.001 || ext.SheenWeight > 0.001 || ext.DiffuseRoughness > 0.001 ||
                       ext.IsDiffuseOnly > 0.5);
        if (useExt)
        {
            float3 dDiff, dSpec, amb;
            if (UseSimpleLight > 0.5)
            {
                CalcSimpleLgtPbrExtended(albedoLinear, n, input.WorldPos, metallic, roughness, 0.75, 0.04, ext, dDiff, dSpec);
                lit = dDiff + dSpec;
            }
            else
            {
                CalcDynamicPbrPartsExtended(albedoLinear, n, input.WorldPos, input.Pos.xy, metallic, roughness, 0.04, ext, dDiff, dSpec, amb);
                lit = dDiff + dSpec + amb;
            }
        }
        else
        {
            lit = (UseSimpleLight > 0.5)
                ? CalcSimpleLgtPbrEx(albedoLinear, n, input.WorldPos, metallic, roughness, clearcoat, 0.75)
                : CalcDynamicLgtPbrEx(albedoLinear, n, input.WorldPos, input.Pos.xy, metallic, roughness, clearcoat);
        }

        if (EmissiveColor.w > 0.0)
        {
            float2 emUv = LayerUvExtra.w > 0.5 ? ApplyLayerUv(input.UV) : input.UV;
            float3 emVal = EmissiveColor.rgb * EmissiveColor.w;
            if (MaterialParams.w > 0.5)
            {
                float4 emSample = gEmissiveMap.Sample(gSampler, emUv);
                if (LayerUvExtra.w > 0.5 && emSample.a > 1e-4 && emSample.a < 0.999)
                    emSample.rgb /= emSample.a;
                lit += SrgbToLinearUnclamped(emSample.rgb) * emVal;
            }
            else
            {
                lit += emVal;
            }
        }
        lit += lutExtraEmissive;
    }

    outNormal = n;
    outMetallic = metallic;
    outRoughness = roughness;
    outClearcoat = max(clearcoat, ext.CoatWeight);
    return lit;
}

OpaqueMrtOutput PS_Obj(PSInput input)
{
    float4 c = SampleObjBase(input);
    float finalAlpha = c.a * input.Op;
    clip(finalAlpha - MinAlphaVal);

    float3 albedoLinear = SrgbToLinearUnclamped(c.rgb);
    float3 n;
    float m, r, cc;
    float3 lit = ComputeObjLighting(input, albedoLinear, n, m, r, cc);
    return PackOpaqueMrt(float4(lit * finalAlpha, finalAlpha), n, albedoLinear, r, m, cc);
}

float4 PS_Obj_SemiTrans(PSInput input) : SV_Target
{
    float4 c = SampleObjBase(input);
    float finalAlpha = c.a * input.Op;
    clip(finalAlpha - 0.004);
    clip(0.999 - finalAlpha);

    float3 albedoLinear = SrgbToLinearUnclamped(c.rgb);
    float3 n;
    float m, r, cc;
    float3 lit = ComputeObjLighting(input, albedoLinear, n, m, r, cc);
    return float4(lit * finalAlpha, finalAlpha);
}

void PS_Obj_SemiTransDepthOnly(PSInput input)
{
    float4 c = SampleObjBase(input);
    float finalAlpha = c.a * input.Op;
    clip(finalAlpha - 0.004);
    clip(0.999 - finalAlpha);
}

void PS_Obj_ShadowAlpha(PSInput input)
{
    float4 c = SampleObjBase(input);
    float finalAlpha = c.a * input.Op;
    clip(finalAlpha - MinAlphaVal);
}

OITOutput PS_Obj_OIT(PSInput input)
{
    float4 c = SampleObjBase(input);
    float finalAlpha = c.a * input.Op;
    clip(finalAlpha - 0.004);
    clip(0.999 - finalAlpha);

    float3 albedoLinear = SrgbToLinearUnclamped(c.rgb);
    float3 n;
    float m, r, cc;
    float3 lit = ComputeObjLighting(input, albedoLinear, n, m, r, cc);
    float z = input.Pos.z;
    float weight = clamp(
        pow(min(1.0, finalAlpha * 10.0) + 0.01, 3.0) *
        100000000.0 * pow(1.0 - z * 0.9, 3.0),
        0.01, 3000.0
    );

    OITOutput o;
    o.Accum = float4(lit * finalAlpha, finalAlpha) * weight;
    o.Reveal = finalAlpha;
    return o;
}

float2 ProjectWorldToScreenUV(float3 worldPos)
{
    float4 clipP = mul(float4(worldPos, 1.0), ViewProjMatrix);
    if (HalfWidth > 0.0)
    {
        clipP.x =  clipP.x / HalfWidth;
        clipP.y = -clipP.y / HalfHeight;
    }
    float invW = 1.0 / max(abs(clipP.w), 1e-4);
    float2 ndc = clipP.xy * invW;
    return float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
}

float4 PS_Obj_Transmissive(PSInput input) : SV_Target
{
    float4 c = SampleObjBase(input);
    float finalAlpha = c.a * input.Op;
    clip(finalAlpha - 0.004);

    float3 albedoLinear = SrgbToLinearUnclamped(c.rgb);
    float3 n = normalize(input.Normal);
    float3 V = normalize(CameraWorldPosAndPageSize.xyz - input.WorldPos);
    if (dot(V, V) < 1e-6) V = float3(0.0, 0.0, 1.0);
    if (dot(n, V) < 0.0) n = -n;

    float2 dUv = input.UV * max(DetailParams.w, 0.001);
    if (DetailParams.y > 0.5)
    {
        float4 normSample = gNormalMap.Sample(gDetailSampler, dUv);
        n = PerturbNormalCotangent(n, input.WorldPos, dUv, normSample, DetailParams.x);
    }

    float metallic = MaterialParams.x;
    float roughness = (MaterialParams.y >= 0.0) ? MaterialParams.y : DefaultRoughness;
    float clearcoat = MaterialParams.z;
    if (DetailParams.z > 0.5)
    {
        float4 rmSample = gRoughMetal.Sample(gDetailSampler, dUv);
        ApplyRoughMetalSample(rmSample, DetailParams.z, roughness, metallic);
    }

    float m = saturate(metallic);
    float r = clamp(roughness >= 0.0 ? roughness : DefaultRoughness, 0.04, 1.0);
    float tr = saturate(GlassParams.x);
    if (GlassParams.z > 0.5)
    {
        tr *= gTransMap.Sample(gDetailSampler, dUv).r;
    }
    tr *= (1.0 - m);

    float ior = clamp(GlassParams.y > 1.0 ? GlassParams.y : 1.5, 1.01, 3.0);

    float3 specReflect, diffuseLit, fresnelF;
    CalcGlassSurfaceLighting(
        albedoLinear, n, input.WorldPos, input.Pos.xy,
        m, r, clearcoat, ior,
        specReflect, diffuseLit, fresnelF);

    float3 refrDir = ComputeSolidRefractDir(V, n, ior);
    float viewDist = length(CameraWorldPosAndPageSize.xyz - input.WorldPos);
    float refractStep = clamp(viewDist * 0.14, 15.0, 220.0);
    float2 refrUv = clamp(ProjectWorldToScreenUV(input.WorldPos + refrDir * refractStep), float2(0.002, 0.002), float2(0.998, 0.998));

    float4 bgSample = float4(0.0, 0.0, 0.0, 0.0);
    if (GlassParams.w > 0.5)
    {
        float bgMip = clamp(pow(saturate(r), 1.25) * ScreenParams.z, 0.0, ScreenParams.z);
        bgSample = gSceneBg.SampleLevel(gSampler, refrUv, bgMip);
        bgSample.rgb = max(bgSample.rgb, 0.0);
    }

    float3 envRefract = (CamBasisX.w > 0.5)
        ? EvaluateEnv(refrDir, r)
        : (EvaluateEnv(refrDir, r) * ((ScreenParams.w > 0.5 && HalfWidth > 0.0) ? max(EnvParams.x, 0.0) : max(IblIntensity, 0.65)));

    float3 emTerm = float3(0.0, 0.0, 0.0);
    if (EmissiveColor.w > 0.0)
    {
        float3 emVal = EmissiveColor.rgb * EmissiveColor.w;
        if (MaterialParams.w > 0.5)
        {
            float3 emTex = SrgbToLinearUnclamped(gEmissiveMap.Sample(gSampler, input.UV).rgb);
            emTerm = emTex * emVal;
        }
        else
        {
            emTerm = emVal;
        }
    }

    if (CamBasisX.w > 0.5 || HalfWidth <= 0.0 || ScreenParams.w > 0.5)
    {
        float3 behindCol = bgSample.rgb + envRefract * (1.0 - saturate(bgSample.a));
        float3 transmitted = (1.0 - fresnelF) * albedoLinear * behindCol * tr;
        float3 lit = diffuseLit * (1.0 - tr) + transmitted + specReflect + emTerm;
        return float4(lit * finalAlpha, finalAlpha);
    }
    else
    {
        float fresMax = max(max(fresnelF.r, fresnelF.g), fresnelF.b);
        float glassAlpha = saturate(max(1.0 - tr, max(saturate(bgSample.a), fresMax + 0.08 * tr))) * finalAlpha;
        float3 transmitted = (1.0 - fresnelF) * albedoLinear * bgSample.rgb * tr;
        float3 litPremul = (diffuseLit * (1.0 - tr) + transmitted + specReflect + emTerm) * finalAlpha;
        return float4(litPremul, glassAlpha);
    }
}
";

    public void Initialize(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        if (_disposed) return;
        if (_initialized && _devicePointer == device.NativePointer) return;

        if (_initialized)
        {
            ReleaseResources();
        }

        try
        {
            var vsBytes = ShaderBytecodeCache.GetOrCompile("Obj_VS", ShaderSource, "VS_Obj", "vs_5_0", "obj_shader");
            var gsWireBytes = ShaderBytecodeCache.GetOrCompile("Obj_GS_Wire", ShaderSource, "GS_Obj_Wireframe", "gs_5_0", "obj_shader");
            var psOpaqueBytes = ShaderBytecodeCache.GetOrCompile("Obj_PS_Opaque", ShaderSource, "PS_Obj", "ps_5_0", "obj_shader");
            var psSemiBytes = ShaderBytecodeCache.GetOrCompile("Obj_PS_Semi", ShaderSource, "PS_Obj_SemiTrans", "ps_5_0", "obj_shader");
            var psSemiDepthBytes = ShaderBytecodeCache.GetOrCompile("Obj_PS_SemiDepth", ShaderSource, "PS_Obj_SemiTransDepthOnly", "ps_5_0", "obj_shader");
            var psOitBytes = ShaderBytecodeCache.GetOrCompile("Obj_PS_OIT", ShaderSource, "PS_Obj_OIT", "ps_5_0", "obj_shader");
            var psShadowAlphaBytes = ShaderBytecodeCache.GetOrCompile("Obj_PS_ShadowAlpha", ShaderSource, "PS_Obj_ShadowAlpha", "ps_5_0", "obj_shader");
            var psTransBytes = ShaderBytecodeCache.GetOrCompile("Obj_PS_Transmissive", ShaderSource, "PS_Obj_Transmissive", "ps_5_0", "obj_shader");

            if (vsBytes == null || psOpaqueBytes == null || psSemiBytes == null ||
                psSemiDepthBytes == null || psOitBytes == null || psShadowAlphaBytes == null || psTransBytes == null)
            {
                return;
            }

            _vs = device.CreateVertexShader(vsBytes);
            if (gsWireBytes != null)
                _gsWireframe = device.CreateGeometryShader(gsWireBytes);
            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,     0, 0),
                new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float,    12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,       24, 0),
                new InputElementDescription("COLOR",    0, Format.R32G32B32A32_Float, 32, 0),
            };
            _inputLayout = device.CreateInputLayout(inputElements, vsBytes);

            _psOpaque = device.CreatePixelShader(psOpaqueBytes);
            _psSemiTrans = device.CreatePixelShader(psSemiBytes);
            _psSemiDepthOnly = device.CreatePixelShader(psSemiDepthBytes);
            _psOIT = device.CreatePixelShader(psOitBytes);
            _psShadowAlpha = device.CreatePixelShader(psShadowAlphaBytes);
            _psTransmissive = device.CreatePixelShader(psTransBytes);

            _cbBuffer = device.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<CbObjModel>(), BindFlags.ConstantBuffer));

            _sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.Anisotropic,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                MaxAnisotropy = 8,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            _detailSampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            _rasterizerCullBack = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Back,
                FrontCounterClockwise = true,
                DepthClipEnable = false,
            });
            _rasterizerCullBackCw = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Back,
                FrontCounterClockwise = false,
                DepthClipEnable = false,
            });
            _rasterizerCullNone = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                FrontCounterClockwise = true,
                DepthClipEnable = false,
            });
            _rasterizerCullFrontCcw = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Front,
                FrontCounterClockwise = true,
                DepthClipEnable = false,
            });
            _rasterizerCullFrontCw = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Front,
                FrontCounterClockwise = false,
                DepthClipEnable = false,
            });

            var outVsBytes = ShaderBytecodeCache.GetOrCompile("ObjOutline_VS", OutlineShaderSource, "VS_ObjOutline", "vs_5_0", "obj_outline");
            var outPsBytes = ShaderBytecodeCache.GetOrCompile("ObjOutline_PS", OutlineShaderSource, "PS_ObjOutline", "ps_5_0", "obj_outline");
            if (outVsBytes != null && outPsBytes != null)
            {
                _vsOutline = device.CreateVertexShader(outVsBytes);
                _psOutline = device.CreatePixelShader(outPsBytes);
                _inputLayoutOutline = device.CreateInputLayout(new[]
                {
                    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                    new InputElementDescription("TEXCOORD", 1, Format.R32G32B32_Float, 0, 1),
                }, outVsBytes);
                _cbOutline = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbObjOutline>(), BindFlags.ConstantBuffer));
            }

            var silVsBytes = ShaderBytecodeCache.GetOrCompile("ObjSil_VS", SilhouetteShaderSource, "VS_ObjSil", "vs_5_0", "obj_sil");
            var silPsBytes = ShaderBytecodeCache.GetOrCompile("ObjSil_PS", SilhouetteShaderSource, "PS_ObjSil", "ps_5_0", "obj_sil");
            if (silVsBytes != null && silPsBytes != null)
            {
                _vsSil = device.CreateVertexShader(silVsBytes);
                _psSil = device.CreatePixelShader(silPsBytes);
                _cbSil = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbObjSil>(), BindFlags.ConstantBuffer));
            }

            CreateWhiteTexture(device);

            _devicePointer = device.NativePointer;
            _initialized = true;
        }
        catch (Exception ex)
        {
            Log($"Initialize エラー: {ex.Message}");
            ReleaseResources();
        }
    }

    private unsafe void CreateWhiteTexture(ID3D11Device device)
    {
        uint white = 0xFFFFFFFF;
        _whiteTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        }, new SubresourceData[] { new SubresourceData((IntPtr)(&white), 4) });

        _whiteSrv = device.CreateShaderResourceView(_whiteTexture);

        // Flat tangent-space normal: R=128, G=128, B=255, A=255 in BGRA little-endian
        uint flatNormal = 0xFF8080FF;
        _flatNormalTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        }, new SubresourceData[] { new SubresourceData((IntPtr)(&flatNormal), 4) });

        _flatNormalSrv = device.CreateShaderResourceView(_flatNormalTexture);
    }

    private static bool IsSameDevice(ID3D11DeviceChild? resource, IntPtr devicePtr)
    {
        if (resource == null || resource.NativePointer == IntPtr.Zero) return false;
        try
        {
            // Vortice の ID3D11DeviceChild.Device は子リソース所有のキャッシュ済ラッパーを返すため Dispose してはならない
            var dev = resource.Device;
            return dev != null && dev.NativePointer == devicePtr;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasTransmissiveParts(ObjModelData model, in MaterialOverrideParameters matOverride)
    {
        if (model.Noise3D.Enabled)
            return false;
        if (matOverride.Enabled && matOverride.Transmission > 0.001f)
            return true;
        foreach (var part in model.Parts)
        {
            if (part.IndexCount > 0 && (part.Transmission > 0.001f || part.TransmissionTexture != null))
                return true;
        }
        return false;
    }

    public void RenderOutline(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        ObjModelData model,
        in Matrix4x4 worldMatrix,
        in Matrix4x4 normalMatrix,
        in Matrix4x4 viewProjMatrix,
        float projHalfWidth,
        float projHalfHeight,
        float targetHalfWidth,
        float targetHalfHeight,
        float opacity,
        in D3DEffect.Effects.OutlineParameters outline)
    {
        if (!_initialized || _vsOutline == null || _psOutline == null || _inputLayoutOutline == null || _cbOutline == null)
            return;
        if (!outline.Enabled || outline.Thickness <= 0.001f || outline.Opacity <= 0.001f || model.Noise3D.Enabled)
            return;

        IntPtr d3dPtr = d3d.NativePointer;
        if (!IsSameDevice(model.VertexBuffer, d3dPtr) || !IsSameDevice(model.IndexBuffer, d3dPtr))
            return;

        ID3D11Buffer? smoothVb = null;
        if (outline.SmoothNormals)
        {
            if (IsSameDevice(model.SmoothNormalBuffer, d3dPtr))
            {
                smoothVb = model.SmoothNormalBuffer;
            }
            else
            {
                smoothVb = GetOrCreateFallbackSmoothNormals(d3d, ctx, model.VertexBuffer);
                if (smoothVb != null)
                    model.SmoothNormalBuffer = smoothVb;
            }
        }

        ctx.IASetInputLayout(_inputLayoutOutline);
        ctx.IASetVertexBuffer(0, model.VertexBuffer, ObjVertexStride, 0);
        if (smoothVb != null && IsSameDevice(smoothVb, d3dPtr))
            ctx.IASetVertexBuffer(1, smoothVb, 12, 0);
        else
            ctx.IASetVertexBuffer(1, model.VertexBuffer, ObjVertexStride, 12);

        ctx.IASetIndexBuffer(model.IndexBuffer, Format.R32_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.GSSetShader(null);
        ctx.VSSetShader(_vsOutline);
        ctx.PSSetShader(_psOutline);

        bool flipWinding = worldMatrix.GetDeterminant() > 0f;
        var hullRs = flipWinding ? _rasterizerCullFrontCw : _rasterizerCullFrontCcw;
        var prevRs = ctx.RSGetState();
        if (hullRs != null)
            ctx.RSSetState(hullRs);

        var cb = new CbObjOutline
        {
            WorldMatrix = worldMatrix,
            ViewProjMatrix = viewProjMatrix,
            NormalMatrix = normalMatrix,
            OutlineColor = new Vector4(
                SrgbToLinear(outline.Color.X),
                SrgbToLinear(outline.Color.Y),
                SrgbToLinear(outline.Color.Z),
                outline.Color.W),
            OutlineParams = new Vector4(
                projHalfWidth,
                projHalfHeight,
                outline.Thickness,
                outline.WorldSpaceThickness ? 1f : 0f),
            ViewportParams = new Vector4(
                MathF.Max(targetHalfWidth, 1f),
                MathF.Max(targetHalfHeight, 1f),
                outline.DepthBias,
                (outline.SmoothNormals && smoothVb != null) ? 1f : 0f),
            ExtraParams = new Vector4(opacity * outline.Opacity, 0f, 0f, 0f),
        };
        ctx.UpdateSubresource(in cb, _cbOutline);
        ctx.VSSetConstantBuffer(0, _cbOutline);
        ctx.PSSetConstantBuffer(0, _cbOutline);

        try
        {
            foreach (var part in model.Parts)
            {
                if (part.IndexCount <= 0) continue;
                ctx.DrawIndexed(part.IndexCount, part.IndexOffset, 0);
            }
        }
        finally
        {
            ctx.IASetVertexBuffer(1, null!, 0, 0);
            if (prevRs != null)
            {
                ctx.RSSetState(prevRs);
                prevRs.Dispose();
            }
        }
    }

    private ID3D11Buffer? GetOrCreateFallbackSmoothNormals(ID3D11Device d3d, ID3D11DeviceContext ctx, ID3D11Buffer vb)
    {
        if (_fallbackSmoothNormals.TryGetValue(vb, out var entry) &&
            entry.DevicePtr == d3d.NativePointer &&
            IsSameDevice(entry.Buffer, d3d.NativePointer))
        {
            return entry.Buffer;
        }
        var created = Model3D.ModelGpuCache.CreateSmoothNormalBufferFromGpuVb(d3d, ctx, vb);
        if (created == null) return null;
        var next = new SmoothNormalFallbackEntry(created, d3d.NativePointer);
        _fallbackSmoothNormals.AddOrUpdate(vb, next);
        return created;
    }

    private void RenderSilhouetteInternal(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        ObjModelData model,
        in Matrix4x4 worldMatrix,
        in Matrix4x4 viewProjMatrix,
        float halfScreenWidth,
        float halfScreenHeight,
        float opacity,
        float alphaThreshold,
        in MaterialOverrideParameters matOverride)
    {
        if (_vsSil == null || _psSil == null || _cbSil == null || _inputLayout == null) return;
        IntPtr d3dPtr = d3d.NativePointer;

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, model.VertexBuffer, ObjVertexStride, 0);
        ctx.IASetIndexBuffer(model.IndexBuffer, Format.R32_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.GSSetShader(null);
        ctx.VSSetShader(_vsSil);
        ctx.PSSetShader(_psSil);
        ctx.PSSetSampler(0, _sampler);

        bool flipWinding = worldMatrix.GetDeterminant() > 0f;
        ID3D11RasterizerState? cullBackRs = flipWinding ? (_rasterizerCullBackCw ?? _rasterizerCullBack) : _rasterizerCullBack;
        ID3D11RasterizerState? prevRs = ctx.RSGetState();

        Vector4 overrideTintSrgb = matOverride.Enabled
            ? new Vector4(
                LinearToSrgb(matOverride.TintColor.X),
                LinearToSrgb(matOverride.TintColor.Y),
                LinearToSrgb(matOverride.TintColor.Z),
                matOverride.TintColor.W)
            : Vector4.One;

        try
        {
            foreach (var part in model.Parts)
            {
                if (part.IndexCount <= 0) continue;
                bool wantCullBack = model.CullBackface || part.CullBackface;
                var nextRs = wantCullBack ? cullBackRs : _rasterizerCullNone;
                if (nextRs != null) ctx.RSSetState(nextRs);

                ID3D11ShaderResourceView? layerAlbedoSrv = part.LayerTextureAlbedoLayerPlusOne > 0
                    ? ShadingSrvIfValid(Viewport3D.Shading.LayerTextureRegistry.TryGetSrv(d3d, part.LayerTextureAlbedoLayerPlusOne - 1), d3dPtr)
                    : null;
                ID3D11ShaderResourceView? activeTexture = layerAlbedoSrv
                    ?? (IsSameDevice(part.Texture, d3dPtr) ? part.Texture : null);

                var cb = new CbObjSil
                {
                    WorldMatrix = worldMatrix,
                    ViewProjMatrix = viewProjMatrix,
                    BaseColor = part.BaseColor * model.TintColor * overrideTintSrgb,
                    Params = new Vector4(halfScreenWidth, halfScreenHeight, opacity, alphaThreshold),
                };
                ctx.UpdateSubresource(in cb, _cbSil);
                ctx.VSSetConstantBuffer(0, _cbSil);
                ctx.PSSetConstantBuffer(0, _cbSil);
                ctx.PSSetShaderResource(0, (activeTexture ?? _whiteSrv)!);
                ctx.DrawIndexed(part.IndexCount, part.IndexOffset, 0);
            }
        }
        finally
        {
            if (prevRs != null)
            {
                ctx.RSSetState(prevRs);
                prevRs.Dispose();
            }
        }
    }

    public void RenderModel(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        ObjModelData model,
        in Matrix4x4 worldMatrix,
        in Matrix4x4 normalMatrix,
        in Matrix4x4 viewProjMatrix,
        float halfScreenWidth,
        float halfScreenHeight,
        float opacity,
        float alphaThreshold,
        ObjRenderMode mode)
    {
        MaterialOverrideParameters noOverride = default;
        RenderModel(
            d3d, ctx, model,
            in worldMatrix, in normalMatrix, in viewProjMatrix,
            halfScreenWidth, halfScreenHeight, opacity, alphaThreshold, mode,
            in noOverride, null, null);
    }

    public void RenderModel(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        ObjModelData model,
        in Matrix4x4 worldMatrix,
        in Matrix4x4 normalMatrix,
        in Matrix4x4 viewProjMatrix,
        float halfScreenWidth,
        float halfScreenHeight,
        float opacity,
        float alphaThreshold,
        ObjRenderMode mode,
        in MaterialOverrideParameters matOverride,
        ID3D11ShaderResourceView? overrideNormalSrv,
        ID3D11ShaderResourceView? overrideRoughSrv,
        ID3D11ShaderResourceView? sceneBgSrv = null,
        float bgWidth = 0f,
        float bgHeight = 0f,
        float bgMaxMip = 0f,
        bool refractFullEnv = false)
    {
        if (!_initialized || _vs == null || _psOpaque == null) return;
        if (model.Noise3D.Enabled) return;

        IntPtr d3dPtr = d3d.NativePointer;
        if (!IsSameDevice(model.VertexBuffer, d3dPtr) || !IsSameDevice(model.IndexBuffer, d3dPtr))
            return;

        if (mode == ObjRenderMode.Silhouette)
        {
            RenderSilhouetteInternal(
                d3d, ctx, model, in worldMatrix, in viewProjMatrix,
                halfScreenWidth, halfScreenHeight, opacity, alphaThreshold, in matOverride);
            return;
        }

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, model.VertexBuffer, ObjVertexStride, 0);
        ctx.IASetIndexBuffer(model.IndexBuffer, Format.R32_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        ctx.VSSetShader(_vs);
        ctx.PSSetSampler(0, _sampler);
        ctx.PSSetSampler(4, _detailSampler);

        bool isTransPass = mode == ObjRenderMode.SemiTransDepthOnly || mode == ObjRenderMode.SemiTransColor || mode == ObjRenderMode.OIT;
        bool isTransmissivePass = mode == ObjRenderMode.Transmissive || mode == ObjRenderMode.TransmissiveDepthOnly;

        ID3D11PixelShader? basePs = mode switch
        {
            ObjRenderMode.OIT => _psOIT,
            ObjRenderMode.SemiTransColor => _psSemiTrans,
            ObjRenderMode.SemiTransDepthOnly => _psSemiDepthOnly,
            ObjRenderMode.Shadow => _psShadowAlpha,
            ObjRenderMode.TransmissiveDepthOnly => _psShadowAlpha,
            ObjRenderMode.Transmissive => _psTransmissive,
            _ => _psOpaque,
        };

        ID3D11PixelShader? lastPs = (ID3D11PixelShader?)null;
        bool psInitialized = false;

        bool flipWinding = worldMatrix.GetDeterminant() > 0f;
        ID3D11RasterizerState? cullBackRs = flipWinding ? (_rasterizerCullBackCw ?? _rasterizerCullBack) : _rasterizerCullBack;
        bool anyPartCullBack = model.CullBackface;
        if (!anyPartCullBack)
        {
            foreach (var p in model.Parts)
            {
                if (p.IndexCount > 0 && p.CullBackface) { anyPartCullBack = true; break; }
            }
        }
        ID3D11RasterizerState? prevRs = anyPartCullBack ? ctx.RSGetState() : null;
        bool currentCullBack = false;
        if (model.CullBackface && cullBackRs != null)
        {
            ctx.RSSetState(cullBackRs);
            currentCullBack = true;
        }

        Vector4 overrideTintSrgb = matOverride.Enabled
            ? new Vector4(
                LinearToSrgb(matOverride.TintColor.X),
                LinearToSrgb(matOverride.TintColor.Y),
                LinearToSrgb(matOverride.TintColor.Z),
                matOverride.TintColor.W)
            : Vector4.One;

        ID3D11ShaderResourceView? validBgSrv = (mode == ObjRenderMode.Transmissive && IsSameDevice(sceneBgSrv, d3dPtr)) ? sceneBgSrv : null;

        try
        {
            foreach (var part in model.Parts)
            {
                if (part.IndexCount <= 0) continue;
                if (mode == ObjRenderMode.Shadow && part.DisableShadow) continue;

                if (anyPartCullBack)
                {
                    bool wantCullBack = model.CullBackface || part.CullBackface;
                    if (wantCullBack != currentCullBack)
                    {
                        var nextRs = wantCullBack ? cullBackRs : (prevRs ?? _rasterizerCullNone);
                        if (nextRs != null)
                        {
                            ctx.RSSetState(nextRs);
                            currentCullBack = wantCullBack;
                        }
                    }
                }

                ID3D11ShaderResourceView? activeTransTex = (matOverride.Enabled && matOverride.Transmission > 0.001f)
                    ? null
                    : (IsSameDevice(part.TransmissionTexture, d3dPtr) ? part.TransmissionTexture : null);
                float effTrans = matOverride.Enabled
                    ? matOverride.Transmission
                    : (activeTransTex != null ? MathF.Max(part.Transmission, 1f) : part.Transmission);
                float effIor = matOverride.Enabled && matOverride.Ior > 1.0f
                    ? matOverride.Ior
                    : (part.Ior > 1.0f ? part.Ior : 1.5f);
                bool partIsTransmissive = effTrans > 0.001f || activeTransTex != null;

                if (isTransmissivePass)
                {
                    if (!partIsTransmissive) continue;
                }
                else
                {
                    if (partIsTransmissive)
                    {
                        if (mode == ObjRenderMode.Shadow && effTrans > 0.7f) continue;
                        if (mode != ObjRenderMode.Shadow) continue;
                    }
                }

                var baseCol = part.BaseColor * model.TintColor * overrideTintSrgb;

                if (isTransPass && opacity * model.TintColor.W * overrideTintSrgb.W >= 0.999f && part.IsOpaque)
                    continue;

                ID3D11ShaderResourceView? layerAlbedoSrv = part.LayerTextureAlbedoLayerPlusOne > 0
                    ? ShadingSrvIfValid(Viewport3D.Shading.LayerTextureRegistry.TryGetSrv(d3d, part.LayerTextureAlbedoLayerPlusOne - 1), d3dPtr)
                    : null;
                bool useLayerAlbedo = layerAlbedoSrv != null;
                ID3D11ShaderResourceView? activeTexture = useLayerAlbedo
                    ? layerAlbedoSrv
                    : (IsSameDevice(part.Texture, d3dPtr) ? part.Texture : null);

                ID3D11PixelShader? targetPs = ((mode == ObjRenderMode.Shadow || mode == ObjRenderMode.TransmissiveDepthOnly) &&
                    activeTexture == null && baseCol.W >= 0.999f && opacity >= 0.999f)
                    ? null
                    : basePs;

                if (!psInitialized || !ReferenceEquals(lastPs, targetPs))
                {
                    ctx.PSSetShader(targetPs);
                    lastPs = targetPs;
                    psInitialized = true;
                }

                bool useWireframeGs = part.WireframeMode > 0 && targetPs != null &&
                    mode != ObjRenderMode.Shadow && mode != ObjRenderMode.TransmissiveDepthOnly &&
                    _gsWireframe != null;
                ctx.GSSetShader(useWireframeGs ? _gsWireframe : null);

                float metallic = matOverride.Enabled
                    ? matOverride.Metallic
                    : (model.OverrideMaterial ? model.OverrideMetallic : part.Metallic);
                float roughness = matOverride.Enabled
                    ? matOverride.Roughness
                    : (model.OverrideMaterial ? model.OverrideRoughness : part.Roughness);
                float clearcoat = matOverride.Enabled ? matOverride.Clearcoat : part.CoatWeight;

                Vector3 partEmissiveLin = new Vector3(
                    SrgbToLinear(part.EmissiveColor.X),
                    SrgbToLinear(part.EmissiveColor.Y),
                    SrgbToLinear(part.EmissiveColor.Z)) * (part.EmissiveColor.W * model.EmissiveMultiplier);
                if (matOverride.Enabled && matOverride.EmissiveColor.W > 0f)
                {
                    partEmissiveLin += new Vector3(
                        matOverride.EmissiveColor.X,
                        matOverride.EmissiveColor.Y,
                        matOverride.EmissiveColor.Z) * matOverride.EmissiveColor.W;
                }
                Vector4 emissive = (partEmissiveLin.X > 0f || partEmissiveLin.Y > 0f || partEmissiveLin.Z > 0f)
                    ? new Vector4(partEmissiveLin, 1f)
                    : Vector4.Zero;

                ID3D11ShaderResourceView? activeNormal = (matOverride.Enabled && IsSameDevice(overrideNormalSrv, d3dPtr))
                    ? overrideNormalSrv
                    : (IsSameDevice(part.NormalTexture, d3dPtr) ? part.NormalTexture : null);
                float normScale = (matOverride.Enabled && IsSameDevice(overrideNormalSrv, d3dPtr))
                    ? matOverride.NormalStrength
                    : (part.NormalScale > 0f ? part.NormalScale : 1f);

                ID3D11ShaderResourceView? activeRoughMetal = (matOverride.Enabled && IsSameDevice(overrideRoughSrv, d3dPtr))
                    ? overrideRoughSrv
                    : (IsSameDevice(part.RoughnessMetallicTexture, d3dPtr) ? part.RoughnessMetallicTexture : null);
                int rmPacking = (matOverride.Enabled && IsSameDevice(overrideRoughSrv, d3dPtr))
                    ? 2
                    : (activeRoughMetal != null ? part.RoughMetalPacking : 0);

                float uvScale = matOverride.Enabled && matOverride.UvScale > 0f ? matOverride.UvScale : 1f;

                ID3D11ShaderResourceView? layerEmissiveSrv = part.LayerTextureEmissiveLayerPlusOne > 0
                    ? ShadingSrvIfValid(Viewport3D.Shading.LayerTextureRegistry.TryGetSrv(d3d, part.LayerTextureEmissiveLayerPlusOne - 1), d3dPtr)
                    : null;
                bool useLayerEmissive = layerEmissiveSrv != null && !(matOverride.Enabled && matOverride.EmissiveColor.W > 0f);
                ID3D11ShaderResourceView? activeEmissive = useLayerEmissive
                    ? layerEmissiveSrv
                    : (IsSameDevice(part.EmissiveTexture, d3dPtr) ? part.EmissiveTexture : null);
                bool partHasEmissiveFactor = part.EmissiveColor.W > 0f &&
                    (part.EmissiveColor.X > 0f || part.EmissiveColor.Y > 0f || part.EmissiveColor.Z > 0f);
                bool useEmissiveMap = activeEmissive != null && (partHasEmissiveFactor || useLayerEmissive) &&
                    !(matOverride.Enabled && matOverride.EmissiveColor.W > 0f);

                ID3D11ShaderResourceView? activeShaderLut = IsSameDevice(part.ShaderLutTexture, d3dPtr) ? part.ShaderLutTexture : null;
                int lutMode = activeShaderLut != null ? part.ShaderLutMode : 0;

                Vector4 layerUv = (part.LayerUvTransform.X == 0f && part.LayerUvTransform.Y == 0f)
                    ? new Vector4(1f, 1f, 0f, 0f)
                    : part.LayerUvTransform;

                var cb = new CbObjModel
                {
                    WorldMatrix = worldMatrix,
                    ViewProjMatrix = viewProjMatrix,
                    NormalMatrix = normalMatrix,
                    HalfWidth = halfScreenWidth,
                    HalfHeight = halfScreenHeight,
                    Opacity = opacity,
                    MinAlphaVal = alphaThreshold,
                    BaseColor = baseCol,
                    MaterialParams = new Vector4(metallic, roughness, clearcoat, useEmissiveMap ? 1f : 0f),
                    EmissiveColor = emissive,
                    DetailParams = new Vector4(
                        normScale,
                        activeNormal != null ? 1f : 0f,
                        rmPacking,
                        uvScale),
                    GlassParams = new Vector4(
                        effTrans,
                        effIor,
                        activeTransTex != null ? 1f : 0f,
                        validBgSrv != null ? 1f : 0f),
                    ScreenParams = new Vector4(
                        bgWidth > 0f ? 1f / bgWidth : 0f,
                        bgHeight > 0f ? 1f / bgHeight : 0f,
                        bgMaxMip,
                        refractFullEnv ? 1f : 0f),
                    ExtendedPbr0 = new Vector4(
                        part.CoatWeight,
                        part.CoatRoughness > 0f ? part.CoatRoughness : 0.08f,
                        part.CoatIor > 1f ? part.CoatIor : 1.5f,
                        part.DiffuseRoughness),
                    ExtendedPbr1 = new Vector4(
                        part.ThinFilmThickness,
                        part.ThinFilmIor > 1f ? part.ThinFilmIor : 1.33f,
                        part.Anisotropic,
                        part.AnisotropicRotation * (MathF.PI * 2f)),
                    ExtendedPbr2 = new Vector4(
                        part.SubsurfaceWeight,
                        part.SubsurfaceScale,
                        part.SheenWeight,
                        part.SheenRoughness > 0f ? part.SheenRoughness : 0.5f),
                    SubsurfaceColor = new Vector4(
                        SrgbToLinear(part.SubsurfaceColor.X),
                        SrgbToLinear(part.SubsurfaceColor.Y),
                        SrgbToLinear(part.SubsurfaceColor.Z),
                        part.IsDiffuseOnly ? 1f : 0f),
                    SheenTint = new Vector4(
                        SrgbToLinear(part.SheenTint.X),
                        SrgbToLinear(part.SheenTint.Y),
                        SrgbToLinear(part.SheenTint.Z),
                        0f),
                    ShaderLutParams = new Vector4(
                        lutMode,
                        part.ShaderLutParamA,
                        part.ShaderLutParamB,
                        part.ShaderLutEmissiveMax),
                    WireframeParams = new Vector4(
                        useWireframeGs ? 1f : 0f,
                        part.WireframeSize > 0f ? part.WireframeSize : 1.5f,
                        part.WireframePixelSize ? 1f : 0f,
                        0f),
                    WireBaseColor = new Vector4(
                        part.WireframeBaseColor.X,
                        part.WireframeBaseColor.Y,
                        part.WireframeBaseColor.Z,
                        part.WireframeEmissiveColor.W * model.EmissiveMultiplier),
                    WireEmissiveColor = new Vector4(
                        part.WireframeEmissiveColor.X,
                        part.WireframeEmissiveColor.Y,
                        part.WireframeEmissiveColor.Z,
                        0f),
                    LayerUvParams = layerUv,
                    LayerUvExtra = new Vector4(
                        part.LayerUvRotationRad,
                        part.LayerFlipV ? 1f : 0f,
                        useLayerAlbedo ? 1f : 0f,
                        useLayerEmissive ? 1f : 0f),
                };

                ctx.UpdateSubresource(in cb, _cbBuffer!);
                ctx.VSSetConstantBuffer(0, _cbBuffer);

                if (targetPs != null)
                {
                    ctx.PSSetConstantBuffer(0, _cbBuffer);
                    ctx.PSSetShaderResource(0, (activeTexture ?? _whiteSrv)!);
                    ctx.PSSetShaderResource(1, (activeNormal ?? _flatNormalSrv)!);
                    ctx.PSSetShaderResource(6, (activeRoughMetal ?? _whiteSrv)!);
                    ctx.PSSetShaderResource(7, (useEmissiveMap ? activeEmissive : _whiteSrv)!);
                    ctx.PSSetShaderResource(10, (activeShaderLut ?? _whiteSrv)!);
                    if (mode == ObjRenderMode.Transmissive)
                    {
                        ctx.PSSetShaderResource(8, (validBgSrv ?? _whiteSrv)!);
                        ctx.PSSetShaderResource(9, (activeTransTex ?? _whiteSrv)!);
                    }
                }

                ctx.DrawIndexed(part.IndexCount, part.IndexOffset, 0);
            }
        }
        finally
        {
            ctx.GSSetShader(null);
            ctx.PSSetShaderResource(1, null!);
            ctx.PSSetShaderResource(6, null!);
            ctx.PSSetShaderResource(7, null!);
            ctx.PSSetShaderResource(8, null!);
            ctx.PSSetShaderResource(9, null!);
            ctx.PSSetShaderResource(10, null!);
            if (prevRs != null)
            {
                ctx.RSSetState(prevRs);
                prevRs.Dispose();
            }
        }
    }

    private static ID3D11ShaderResourceView? ShadingSrvIfValid(ID3D11ShaderResourceView? srv, IntPtr devicePtr)
        => IsSameDevice(srv, devicePtr) ? srv : null;

    private static float SrgbToLinear(float x)
    {
        x = MathF.Max(0f, x);
        return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    private static float LinearToSrgb(float x)
    {
        x = MathF.Max(0f, x);
        return x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f;
    }

    private void ReleaseResources()
    {
        _vs?.Dispose(); _vs = null;
        _gsWireframe?.Dispose(); _gsWireframe = null;
        _psOpaque?.Dispose(); _psOpaque = null;
        _psSemiTrans?.Dispose(); _psSemiTrans = null;
        _psSemiDepthOnly?.Dispose(); _psSemiDepthOnly = null;
        _psOIT?.Dispose(); _psOIT = null;
        _psShadowAlpha?.Dispose(); _psShadowAlpha = null;
        _psTransmissive?.Dispose(); _psTransmissive = null;
        _inputLayout?.Dispose(); _inputLayout = null;
        _cbBuffer?.Dispose(); _cbBuffer = null;
        _sampler?.Dispose(); _sampler = null;
        _detailSampler?.Dispose(); _detailSampler = null;
        _rasterizerCullBack?.Dispose(); _rasterizerCullBack = null;
        _rasterizerCullBackCw?.Dispose(); _rasterizerCullBackCw = null;
        _rasterizerCullNone?.Dispose(); _rasterizerCullNone = null;
        _rasterizerCullFrontCcw?.Dispose(); _rasterizerCullFrontCcw = null;
        _rasterizerCullFrontCw?.Dispose(); _rasterizerCullFrontCw = null;
        _vsOutline?.Dispose(); _vsOutline = null;
        _psOutline?.Dispose(); _psOutline = null;
        _inputLayoutOutline?.Dispose(); _inputLayoutOutline = null;
        _cbOutline?.Dispose(); _cbOutline = null;
        _vsSil?.Dispose(); _vsSil = null;
        _psSil?.Dispose(); _psSil = null;
        _cbSil?.Dispose(); _cbSil = null;
        _whiteSrv?.Dispose(); _whiteSrv = null;
        _whiteTexture?.Dispose(); _whiteTexture = null;
        _flatNormalSrv?.Dispose(); _flatNormalSrv = null;
        _flatNormalTexture?.Dispose(); _flatNormalTexture = null;
        _devicePointer = IntPtr.Zero;
        _initialized = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
    }

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] ObjModelRenderer: {msg}");
}
