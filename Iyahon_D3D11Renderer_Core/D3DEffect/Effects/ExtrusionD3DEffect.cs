using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using Iyahon_D3D11Renderer_Core.Lighting;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

/// <summary>
/// Extrusion effect.
/// Raymarches through a box volume to create 3D extrusion from alpha shape.
/// </summary>
public sealed class ExtrusionD3DEffect : IOitCapableD3DEffect
{
    public string Name => "押し出し";
    public string Category => "3Dオブジェクト";

    // Effect-specific parameters
    public float Thickness { get; set; } = 100f;
    public float LightIntensity { get; set; } = 0.5f;
    public float AlphaThreshold { get; set; } = 0.5f;
    public int ExtrusionType { get; set; } = 1; // 1=Image, 2=Solid
    public Vector4 SideColor { get; set; } = new Vector4(1f, 1f, 1f, 1f);
    public float Attenuation { get; set; } = 0f;

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtrusionVertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtrusionCb
    {
        public Matrix4x4 WorldMatrix;      // 64
        public Matrix4x4 ScaleMatrix;      // 64 -> 128
        public Matrix4x4 ViewProjMatrix;   // 64 -> 192
        public Matrix4x4 NormalMatrix;     // 64 -> 256
        public float HalfWidth;            // 4
        public float HalfHeight;           // 4
        public float Opacity;              // 4
        public float ShapeAlphaThreshold;  // 4  -> 272
        public float Thickness;            // 4
        public float LightIntensity;       // 4
        public float Attenuation;          // 4
        public float IsOrthographic;       // 4  -> 288
        public int ExtrusionType;          // 4
        public float PassAlphaMin;         // 4
        public float PassAlphaMax;         // 4
        public float _pad0;                // 4  -> 304
        public Vector4 SideColor;          // 16 -> 320
        public Vector3 CameraLocalPos;     // 12
        public float _pad1;                // 4  -> 336
        public Vector4 MaterialParams;     // 16 -> 352
        public Vector4 TintColor;          // 16 -> 368
        public Vector4 EmissiveColor;      // 16 -> 384
        public Vector4 DetailParams;       // 16 -> 400 bytes (16x)
    }

    private bool _initialized;
    private IntPtr _devicePointer = IntPtr.Zero;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psOpaque;
    private ID3D11PixelShader? _psSemiTrans;
    private ID3D11PixelShader? _psOit;
    private ID3D11PixelShader? _psShadow;
    private ID3D11PixelShader? _psSemiDepthOnly;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private ID3D11Buffer? _cbBuffer;
    private ID3D11SamplerState? _sampler;
    private ID3D11SamplerState? _detailSampler;
    private ID3D11RasterizerState? _rasterCullFront;
    private ID3D11RasterizerState? _rasterCullBack;
    private int _indexCount;

    // 2byte chars cause shader load errors - keep comments in ASCII only
    private const string ShaderSource = @"
cbuffer ExtrusionCb : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ScaleMatrix;
    row_major float4x4 ViewProjMatrix;
    row_major float4x4 NormalMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float ShapeAlphaThreshold;
    float Thickness;
    float LightIntensity;
    float Attenuation;
    float IsOrthographic;
    int ExtrusionType;
    float PassAlphaMin;
    float PassAlphaMax;
    float _pad0;
    float4 SideColor;
    float3 CameraLocalPos;
    float _pad1;
    float4 MaterialParams;
    float4 TintColor;
    float4 EmissiveColor;
    float4 DetailParams;
};
" + LightingShaderCode.HlslCode + @"

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float3 LocalPos : TEXCOORD1; };

struct PSExtOpaqueOutput {
    float4 Color       : SV_Target0;
    float4 NormalRough : SV_Target1;
    float4 SpecMeta    : SV_Target2;
    float  Depth       : SV_Depth;
};

struct PSOutput {
    float4 Color : SV_Target;
    float  Depth : SV_Depth;
};

struct PSOitOutput {
    float4 Accum  : SV_Target0;
    float  Reveal : SV_Target1;
    float  Depth  : SV_Depth;
};

Texture2D    gTex          : register(t0);
Texture2D    gNormalMap    : register(t1);
Texture2D    gRoughMetal   : register(t6);
SamplerState gSampler      : register(s0);
SamplerState gDetailSampler: register(s4);

PSInput VS_Extrusion(VSInput input)
{
    PSInput o;
    float4 scaled = mul(float4(input.Pos, 1.0), ScaleMatrix);
    float4 worldPos = mul(scaled, WorldMatrix);

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

    o.UV = input.UV;
    o.LocalPos = input.Pos;
    return o;
}

bool RaymarchBox(PSInput input, int numSteps, int refineSteps, out float3 hitPos, out float2 hitUV)
{
    hitPos = float3(0, 0, 0);
    hitUV = float2(0, 0);

    float3 ro, rd;
    if (IsOrthographic > 0.5)
    {
        rd = normalize(CameraLocalPos);
        ro = input.LocalPos - rd * 2.0;
    }
    else
    {
        ro = CameraLocalPos;
        rd = normalize(input.LocalPos - CameraLocalPos);
    }

    float3 invDir = 1.0 / rd;
    float3 t0 = (-0.5 - ro) * invDir;
    float3 t1 = ( 0.5 - ro) * invDir;

    float3 tmin = min(t0, t1);
    float3 tmax = max(t0, t1);

    float tNear = max(max(tmin.x, tmin.y), tmin.z);
    float tFar  = min(min(tmax.x, tmax.y), tmax.z);

    if (tNear > tFar || tFar < 0.0) return false;

    float noise = frac(sin(dot(input.Pos.xy, float2(12.9898, 78.233))) * 43758.5453);
    float startT = max(tNear, 0.0);
    float stepSize = max((tFar - startT) / float(numSteps), 1e-5);
    float t = startT + stepSize * noise;

    float hitT = -1.0;
    [loop]
    for (int i = 0; i < numSteps; i++)
    {
        float3 pos = ro + rd * t;
        float2 uv = float2(pos.x + 0.5, pos.y + 0.5);

        float a = gTex.SampleLevel(gSampler, uv, 0).a;
        if (a > ShapeAlphaThreshold)
        {
            float tLow = max(startT, t - stepSize);
            float tHigh = t;
            for (int j = 0; j < refineSteps; j++)
            {
                float tMid = (tLow + tHigh) * 0.5;
                float3 mPos = ro + rd * tMid;
                float2 mUV = float2(mPos.x + 0.5, mPos.y + 0.5);
                float mA = gTex.SampleLevel(gSampler, mUV, 0).a;
                if (mA > ShapeAlphaThreshold) tHigh = tMid;
                else tLow = tMid;
            }
            hitT = tHigh;
            hitPos = ro + rd * hitT;
            hitUV = float2(hitPos.x + 0.5, hitPos.y + 0.5);
            return true;
        }
        t += stepSize;
    }

    return false;
}

float4 EvaluateExtrusionShading(
    float3 hitPos,
    float2 hitUV,
    float2 screenPos,
    out float4 worldHit,
    out float outDepth,
    out float3 outAlbedoLin,
    out float3 outWorldNormal,
    out float outRough,
    out float outMetal,
    out float outClearcoat)
{
    float4 scaledHit = mul(float4(hitPos, 1.0), ScaleMatrix);
    worldHit = mul(scaledHit, WorldMatrix);

    if (HalfWidth > 0.0)
    {
        float4 clipHit = mul(worldHit, ViewProjMatrix);
        float clipZ = -clipHit.z / 200000.0 + 0.5 * clipHit.w;
        outDepth = saturate(clipZ / clipHit.w);
    }
    else
    {
        float4 clipHit = mul(worldHit, ViewProjMatrix);
        outDepth = saturate(clipHit.z / clipHit.w);
    }

    bool isFace = (abs(hitPos.z) > 0.499);
    float3 localNormal;
    if (abs(hitPos.z + 0.5) < 0.005)
    {
        localNormal = float3(0, 0, -1);
    }
    else if (abs(hitPos.z - 0.5) < 0.005)
    {
        localNormal = float3(0, 0, 1);
    }
    else
    {
        float eps = 0.01;
        float aR = gTex.SampleLevel(gSampler, hitUV + float2(eps, 0), 0).a;
        float aL = gTex.SampleLevel(gSampler, hitUV - float2(eps, 0), 0).a;
        float aU = gTex.SampleLevel(gSampler, hitUV + float2(0, eps), 0).a;
        float aD = gTex.SampleLevel(gSampler, hitUV - float2(0, eps), 0).a;
        localNormal = normalize(float3(aL - aR, aD - aU, 0.005));
    }

    float3 worldNormal = normalize(mul(float4(localNormal, 0.0), NormalMatrix).xyz);
    float4 texSample = gTex.SampleLevel(gSampler, hitUV, 0);

    float3 straightRgb;
    float outAlpha;
    float sideFade = 1.0;

    if (isFace)
    {
        straightRgb = texSample.rgb / max(texSample.a, 1e-4);
        outAlpha = texSample.a * Opacity * TintColor.a;
    }
    else
    {
        if (ExtrusionType == 1)
        {
            straightRgb = texSample.rgb / max(texSample.a, 1e-4);
            outAlpha = Opacity * TintColor.a;
            float depthFade = lerp(1.0, saturate(1.0 - (hitPos.z + 0.5)), Attenuation);
            sideFade = depthFade;
        }
        else
        {
            straightRgb = SideColor.rgb;
            outAlpha = SideColor.a * Opacity * TintColor.a;
        }
    }

    float3 albedoLinear = SrgbToLinearUnclamped(straightRgb) * TintColor.rgb;
    float roughness = (MaterialParams.w > 0.5) ? MaterialParams.y : clamp(DefaultRoughness * 0.85, 0.15, 0.90);
    float metallic = (MaterialParams.w > 0.5) ? MaterialParams.x : 0.0;
    float clearcoat = (MaterialParams.w > 0.5) ? MaterialParams.z : 0.0;

    float2 dUv = hitUV * max(DetailParams.w, 0.001);
    if (DetailParams.y > 0.5)
    {
        worldNormal = PerturbNormalCotangent(worldNormal, worldHit.xyz, dUv, gNormalMap.SampleLevel(gDetailSampler, dUv, 0), DetailParams.x);
    }
    if (DetailParams.z > 0.5)
    {
        ApplyRoughMetalSample(gRoughMetal.SampleLevel(gDetailSampler, dUv, 0), DetailParams.z, roughness, metallic);
    }

    float3 litRgb;
    if (UseSimpleLight < 0.5)
    {
        float3 lgtVal = CalcDynamicLgtPbrEx(albedoLinear, worldNormal, worldHit.xyz, screenPos, metallic, roughness, clearcoat);
        litRgb = lgtVal * sideFade;
    }
    else
    {
        float3 lgtVal = CalcSimpleLgtPbrEx(albedoLinear, worldNormal, worldHit.xyz, metallic, roughness, clearcoat, LightIntensity);
        if (!isFace && ExtrusionType == 1)
        {
            lgtVal = lerp(albedoLinear, lgtVal, Attenuation);
        }
        litRgb = lgtVal;
    }

    if (EmissiveColor.w > 0.0)
    {
        litRgb += EmissiveColor.rgb * EmissiveColor.w;
    }

    outAlbedoLin = albedoLinear;
    outWorldNormal = worldNormal;
    outRough = roughness;
    outMetal = metallic;
    outClearcoat = clearcoat;
    return float4(litRgb * outAlpha, outAlpha);
}

PSExtOpaqueOutput PS_Extrusion(PSInput input)
{
    float3 hitPos;
    float2 hitUV;
    if (!RaymarchBox(input, 128, 10, hitPos, hitUV)) discard;

    float4 worldHit;
    float outDepth;
    float3 albedoLin, wn;
    float r, m, cc;
    float4 finalCol = EvaluateExtrusionShading(hitPos, hitUV, input.Pos.xy, worldHit, outDepth, albedoLin, wn, r, m, cc);
    clip(finalCol.a - PassAlphaMin);

    OpaqueMrtOutput mrt = PackOpaqueMrt(finalCol, wn, albedoLin, r, m, cc);
    PSExtOpaqueOutput output;
    output.Color = mrt.Color;
    output.NormalRough = mrt.NormalRough;
    output.SpecMeta = mrt.SpecMeta;
    output.Depth = outDepth;
    return output;
}

PSOutput PS_Extrusion_SemiTrans(PSInput input)
{
    float3 hitPos;
    float2 hitUV;
    if (!RaymarchBox(input, 128, 10, hitPos, hitUV)) discard;

    float4 worldHit;
    float outDepth;
    float3 albedoLin, wn;
    float r, m, cc;
    float4 finalCol = EvaluateExtrusionShading(hitPos, hitUV, input.Pos.xy, worldHit, outDepth, albedoLin, wn, r, m, cc);
    clip(finalCol.a - 0.004);
    clip(0.999 - finalCol.a);

    PSOutput output;
    output.Color = finalCol;
    output.Depth = outDepth;
    return output;
}

PSOitOutput PS_Extrusion_OIT(PSInput input)
{
    float3 hitPos;
    float2 hitUV;
    if (!RaymarchBox(input, 128, 10, hitPos, hitUV)) discard;

    float4 worldHit;
    float outDepth;
    float3 albedoLin, wn;
    float r, m, cc;
    float4 finalCol = EvaluateExtrusionShading(hitPos, hitUV, input.Pos.xy, worldHit, outDepth, albedoLin, wn, r, m, cc);
    clip(finalCol.a - 0.004);
    clip(0.999 - finalCol.a);

    float weight = clamp(
        pow(min(1.0, finalCol.a * 10.0) + 0.01, 3.0) *
        1e8 * pow(1.0 - outDepth * 0.9, 3.0),
        1e-2, 3e3
    );

    PSOitOutput output;
    output.Accum = float4(finalCol.rgb, finalCol.a) * weight;
    output.Reveal = finalCol.a;
    output.Depth = outDepth;
    return output;
}

float PS_Extrusion_Shadow(PSInput input) : SV_Depth
{
    float3 hitPos;
    float2 hitUV;
    if (!RaymarchBox(input, 64, 6, hitPos, hitUV)) discard;

    float4 scaledHit = mul(float4(hitPos, 1.0), ScaleMatrix);
    float4 worldHit = mul(scaledHit, WorldMatrix);
    float4 clipHit = mul(worldHit, ViewProjMatrix);

    if (IsOrthographic < 0.5 && clipHit.w <= 0.0001) discard;
    float d = clipHit.z / clipHit.w;
    if (IsOrthographic < 0.5 && (d < 0.0 || d > 1.0)) discard;
    if (IsOrthographic > 0.5 && d > 1.0) discard;

    return saturate(d);
}

float PS_Extrusion_SemiDepthOnly(PSInput input) : SV_Depth
{
    float3 hitPos;
    float2 hitUV;
    if (!RaymarchBox(input, 96, 8, hitPos, hitUV)) discard;

    bool isFace = (abs(hitPos.z) > 0.499);
    float outAlpha;
    if (isFace)
    {
        outAlpha = gTex.SampleLevel(gSampler, hitUV, 0).a * Opacity;
    }
    else
    {
        outAlpha = (ExtrusionType == 1 ? 1.0 : SideColor.a) * Opacity;
    }

    clip(outAlpha - PassAlphaMin);
    clip(PassAlphaMax - outAlpha);

    float4 scaledHit = mul(float4(hitPos, 1.0), ScaleMatrix);
    float4 worldHit = mul(scaledHit, WorldMatrix);
    float4 clipHit = mul(worldHit, ViewProjMatrix);

    if (HalfWidth > 0.0)
    {
        float clipZ = -clipHit.z / 200000.0 + 0.5 * clipHit.w;
        return saturate(clipZ / clipHit.w);
    }
    return saturate(clipHit.z / clipHit.w);
}
";

    public void Initialize(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        if (_initialized && _devicePointer == device.NativePointer) return;

        if (_initialized)
        {
            Dispose();
        }

        try
        {
            var vsBytes = ShaderBytecodeCache.GetOrCompile("Extrusion_VS", ShaderSource, "VS_Extrusion", "vs_5_0", "extrusion_shader");
            var psOpaqueBytes = ShaderBytecodeCache.GetOrCompile("Extrusion_PS_Opaque", ShaderSource, "PS_Extrusion", "ps_5_0", "extrusion_shader");
            var psSemiBytes = ShaderBytecodeCache.GetOrCompile("Extrusion_PS_Semi", ShaderSource, "PS_Extrusion_SemiTrans", "ps_5_0", "extrusion_shader");
            var psOitBytes = ShaderBytecodeCache.GetOrCompile("Extrusion_PS_OIT", ShaderSource, "PS_Extrusion_OIT", "ps_5_0", "extrusion_shader");
            var psShadowBytes = ShaderBytecodeCache.GetOrCompile("Extrusion_PS_Shadow", ShaderSource, "PS_Extrusion_Shadow", "ps_5_0", "extrusion_shader");
            var psSemiDepthBytes = ShaderBytecodeCache.GetOrCompile("Extrusion_PS_SemiDepth", ShaderSource, "PS_Extrusion_SemiDepthOnly", "ps_5_0", "extrusion_shader");

            if (vsBytes == null || psOpaqueBytes == null || psSemiBytes == null || psOitBytes == null || psShadowBytes == null || psSemiDepthBytes == null)
                return;

            _vs = device.CreateVertexShader(vsBytes);
            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,   12, 0),
            };
            _inputLayout = device.CreateInputLayout(inputElements, vsBytes);

            _psOpaque = device.CreatePixelShader(psOpaqueBytes);
            _psSemiTrans = device.CreatePixelShader(psSemiBytes);
            _psOit = device.CreatePixelShader(psOitBytes);
            _psShadow = device.CreatePixelShader(psShadowBytes);
            _psSemiDepthOnly = device.CreatePixelShader(psSemiDepthBytes);

            _cbBuffer = device.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<ExtrusionCb>(), BindFlags.ConstantBuffer));

            _sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
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

            _rasterCullFront = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Front,
                DepthClipEnable = false,
            });

            _rasterCullBack = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Back,
                DepthClipEnable = false,
            });

            CreateCubeGeometry(device);

            _devicePointer = device.NativePointer;
            _initialized = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] ExtrusionD3DEffect Initialize error: {ex.Message}");
            Dispose();
        }
    }

    private unsafe void CreateCubeGeometry(ID3D11Device device)
    {
        var vertices = new List<ExtrusionVertex>();
        var indices = new List<ushort>();

        var faces = new[]
        {
            (new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f)),
            (new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f,  0.5f),
             new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f)),
            (new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
             new Vector3( 0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f,  0.5f)),
            (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f, -0.5f)),
            (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
             new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f)),
            (new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f,  0.5f), new Vector3( 0.5f,  0.5f,  0.5f)),
        };

        foreach (var (v0, v1, v2, v3) in faces)
        {
            ushort baseIdx = (ushort)vertices.Count;
            vertices.Add(new ExtrusionVertex { Position = v0, TexCoord = new Vector2(0, 0) });
            vertices.Add(new ExtrusionVertex { Position = v1, TexCoord = new Vector2(1, 0) });
            vertices.Add(new ExtrusionVertex { Position = v2, TexCoord = new Vector2(0, 1) });
            vertices.Add(new ExtrusionVertex { Position = v3, TexCoord = new Vector2(1, 1) });

            indices.Add(baseIdx);
            indices.Add((ushort)(baseIdx + 1));
            indices.Add((ushort)(baseIdx + 2));
            indices.Add((ushort)(baseIdx + 1));
            indices.Add((ushort)(baseIdx + 3));
            indices.Add((ushort)(baseIdx + 2));
        }

        _indexCount = indices.Count;

        var vertArray = vertices.ToArray();
        var idxArray = indices.ToArray();

        int vertStride = Marshal.SizeOf<ExtrusionVertex>();
        fixed (ExtrusionVertex* pv = vertArray)
        {
            _vertexBuffer = device.CreateBuffer(
                new BufferDescription(vertStride * vertArray.Length, BindFlags.VertexBuffer),
                new SubresourceData((IntPtr)pv, vertStride * vertArray.Length));
        }

        fixed (ushort* pi = idxArray)
        {
            _indexBuffer = device.CreateBuffer(
                new BufferDescription(sizeof(ushort) * idxArray.Length, BindFlags.IndexBuffer),
                new SubresourceData((IntPtr)pi, sizeof(ushort) * idxArray.Length));
        }
    }

    public void Render(ID3D11DeviceContext ctx, ID3D11Device device,
                       ID3D11ShaderResourceView inputSrv,
                       D3DRenderContext renderContext)
    {
        if (!_initialized || _vs == null || _psOpaque == null || _psShadow == null) return;

        float safeThickness = MathF.Abs(Thickness) < 0.01f ? 0.01f : Thickness;
        var scaleMatrix = Matrix4x4.CreateScale(1f, 1f, safeThickness);
        var effectiveModel = scaleMatrix * renderContext.WorldMatrix;
        var normalMatrix = MatrixTransformHelper.ComputeNormalMatrix(effectiveModel);
        bool isMirrored = MatrixTransformHelper.ComputeDeterminant3x3(effectiveModel) < 0f;

        Vector3 cameraLocalPos;
        float isOrthographic = 0f;

        if (renderContext.HalfScreenWidth > 0)
        {
            var cam = renderContext.CameraMatrix;
            var d2dProj = new Matrix4x4(
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, -1f / 1000f,
                0f, 0f, 0f, 1f
            );

            var combined = cam * d2dProj;
            if (Math.Abs(combined.M14) < 0.00001f &&
                Math.Abs(combined.M24) < 0.00001f &&
                Math.Abs(combined.M34) < 0.00001f)
            {
                isOrthographic = 1f;
            }

            if (!Matrix4x4.Invert(cam, out var invCam)) invCam = Matrix4x4.Identity;
            if (!Matrix4x4.Invert(effectiveModel, out var invEffectiveModel)) invEffectiveModel = Matrix4x4.Identity;

            if (isOrthographic > 0.5f)
            {
                var cameraForwardWorld = Vector3.TransformNormal(new Vector3(0, 0, -1), invCam);
                cameraLocalPos = Vector3.Normalize(Vector3.TransformNormal(cameraForwardWorld, invEffectiveModel));
            }
            else
            {
                var cameraWorldPos = Vector3.Transform(new Vector3(0, 0, 1000), invCam);
                cameraLocalPos = Vector3.Transform(cameraWorldPos, invEffectiveModel);
            }
        }
        else
        {
            var viewProj = renderContext.ViewProjectionMatrix;
            if (Math.Abs(viewProj.M14) < 0.00001f &&
                Math.Abs(viewProj.M24) < 0.00001f &&
                Math.Abs(viewProj.M34) < 0.00001f)
            {
                isOrthographic = 1f;
            }

            if (!Matrix4x4.Invert(effectiveModel, out var invEffectiveModel)) invEffectiveModel = Matrix4x4.Identity;

            if (isOrthographic > 0.5f)
            {
                if (!Matrix4x4.Invert(viewProj, out var invViewProj)) invViewProj = Matrix4x4.Identity;
                var rayDirWorld = Vector3.Normalize(
                    Vector3.Transform(new Vector3(0, 0, 1), invViewProj) -
                    Vector3.Transform(new Vector3(0, 0, 0), invViewProj));
                cameraLocalPos = Vector3.Normalize(Vector3.TransformNormal(rayDirWorld, invEffectiveModel));
            }
            else
            {
                var cameraWorldPos = renderContext.CameraWorldPosition;
                cameraLocalPos = Vector3.Transform(cameraWorldPos, invEffectiveModel);
            }
        }

        float passAlphaMin = renderContext.AlphaThreshold;
        float passAlphaMax = 2.0f;

        ID3D11PixelShader activePs;
        if (renderContext.IsShadowPass)
        {
            activePs = _psShadow!;
        }
        else if (renderContext.IsOitPass)
        {
            activePs = _psOit!;
        }
        else if (renderContext.IsDepthOnlyPass && renderContext.IsSemiTransPass)
        {
            activePs = _psSemiDepthOnly!;
            passAlphaMin = 0.004f;
            passAlphaMax = 0.999f;
        }
        else if (renderContext.IsSemiTransPass)
        {
            activePs = _psSemiTrans!;
        }
        else
        {
            activePs = _psOpaque!;
        }

        var mat = renderContext.Material;
        bool hasMat = mat.Enabled;
        var cb = new ExtrusionCb
        {
            WorldMatrix = renderContext.WorldMatrix,
            ScaleMatrix = scaleMatrix,
            ViewProjMatrix = renderContext.ViewProjectionMatrix,
            NormalMatrix = normalMatrix,
            HalfWidth = renderContext.HalfScreenWidth,
            HalfHeight = renderContext.HalfScreenHeight,
            Opacity = renderContext.Opacity,
            ShapeAlphaThreshold = AlphaThreshold,
            Thickness = Thickness,
            LightIntensity = LightIntensity,
            Attenuation = Attenuation,
            IsOrthographic = isOrthographic,
            ExtrusionType = ExtrusionType,
            PassAlphaMin = passAlphaMin,
            PassAlphaMax = passAlphaMax,
            SideColor = SideColor,
            CameraLocalPos = cameraLocalPos,
            MaterialParams = hasMat
                ? new Vector4(mat.Metallic, mat.Roughness, mat.Clearcoat, 1f)
                : Vector4.Zero,
            TintColor = hasMat ? mat.TintColor : Vector4.One,
            EmissiveColor = hasMat ? mat.EmissiveColor : Vector4.Zero,
            DetailParams = hasMat
                ? new Vector4(
                    mat.NormalStrength,
                    renderContext.NormalMapSrv != null ? 1f : 0f,
                    renderContext.RoughnessMapSrv != null ? 2f : 0f,
                    mat.UvScale > 0f ? mat.UvScale : 1f)
                : new Vector4(1f, 0f, 0f, 1f),
        };
        ctx.UpdateSubresource(in cb, _cbBuffer!);

        ctx.RSSetState(isMirrored ? _rasterCullBack : _rasterCullFront);

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<ExtrusionVertex>(), 0);
        ctx.IASetIndexBuffer(_indexBuffer!, Format.R16_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        ctx.VSSetShader(_vs);
        ctx.PSSetShader(activePs);
        ctx.VSSetConstantBuffer(0, _cbBuffer);
        ctx.PSSetConstantBuffer(0, _cbBuffer);
        ctx.PSSetShaderResource(0, inputSrv);
        ctx.PSSetShaderResource(1, renderContext.NormalMapSrv!);
        ctx.PSSetShaderResource(6, renderContext.RoughnessMapSrv!);
        ctx.PSSetSampler(0, _sampler);
        ctx.PSSetSampler(4, _detailSampler);

        ctx.DrawIndexed(_indexCount, 0, 0);

        ctx.RSSetState(null!);
        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
        ctx.PSSetShaderResource(6, null!);
    }

    public void Dispose()
    {
        _vs?.Dispose(); _vs = null;
        _psOpaque?.Dispose(); _psOpaque = null;
        _psSemiTrans?.Dispose(); _psSemiTrans = null;
        _psOit?.Dispose(); _psOit = null;
        _psShadow?.Dispose(); _psShadow = null;
        _psSemiDepthOnly?.Dispose(); _psSemiDepthOnly = null;
        _inputLayout?.Dispose(); _inputLayout = null;
        _vertexBuffer?.Dispose(); _vertexBuffer = null;
        _indexBuffer?.Dispose(); _indexBuffer = null;
        _cbBuffer?.Dispose(); _cbBuffer = null;
        _sampler?.Dispose(); _sampler = null;
        _detailSampler?.Dispose(); _detailSampler = null;
        _rasterCullFront?.Dispose(); _rasterCullFront = null;
        _rasterCullBack?.Dispose(); _rasterCullBack = null;
        _devicePointer = IntPtr.Zero;
        _initialized = false;
    }
}
