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
/// 立方体エフェクト。
/// 入力テクスチャを立方体の各面にマッピングして描画する。
/// </summary>
public sealed class CubeD3DEffect : IOitCapableD3DEffect
{
    public string Name => "立方体";
    public string Category => "3Dオブジェクト";

    // ── エフェクト固有パラメータ（VideoEffect から設定される） ──
    public float DepthScale { get; set; } = 100f;
    public float LightIntensity { get; set; } = 0.5f;

    [StructLayout(LayoutKind.Sequential)]
    private struct CubeVertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
        public Vector3 Normal;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CubeCb
    {
        public Matrix4x4 WorldMatrix;    // 64
        public Matrix4x4 ViewProjMatrix; // 64 -> 128
        public Matrix4x4 NormalMatrix;   // 64 -> 192
        public float HalfWidth;          // 4
        public float HalfHeight;         // 4
        public float Opacity;            // 4
        public float AlphaThreshold;     // 4  -> 208
        public float DepthScale;         // 4
        public float LightIntensity;     // 4
        public float AlphaMax;           // 4
        public float _pad0;              // 4  -> 224
        public Vector4 MaterialParams;   // 16 -> 240 (Metallic, Roughness, Clearcoat, HasOverride)
        public Vector4 TintColor;        // 16 -> 256
        public Vector4 EmissiveColor;    // 16 -> 272
        public Vector4 DetailParams;     // 16 -> 288 bytes (16x)
    }

    private bool _initialized;
    private IntPtr _devicePointer = IntPtr.Zero;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psOpaque;
    private ID3D11PixelShader? _psSemiTrans;
    private ID3D11PixelShader? _psOit;
    private ID3D11PixelShader? _psDepthOnly;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private ID3D11Buffer? _cbBuffer;
    private ID3D11SamplerState? _sampler;
    private ID3D11SamplerState? _detailSampler;
    private int _indexCount;

    private const string ShaderSource = @"
cbuffer CubeCb : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    row_major float4x4 NormalMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float AlphaThreshold;
    float DepthScale;
    float LightIntensity;
    float AlphaMax;
    float _pad0;
    float4 MaterialParams;
    float4 TintColor;
    float4 EmissiveColor;
    float4 DetailParams;
};
" + LightingShaderCode.HlslCode + @"

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; float3 Norm : NORMAL; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float3 Norm : TEXCOORD1; float Op : TEXCOORD2; float3 WorldPos : TEXCOORD3; };

struct OITOutput
{
    float4 Accum  : SV_Target0;
    float  Reveal : SV_Target1;
};

Texture2D    gTex          : register(t0);
Texture2D    gNormalMap    : register(t1);
Texture2D    gRoughMetal   : register(t6);
SamplerState gSampler      : register(s0);
SamplerState gDetailSampler: register(s4);

PSInput VS_Cube(VSInput input)
{
    PSInput o;
    float3 scaledPos = input.Pos * float3(1.0, 1.0, DepthScale);
    float4 worldPos = mul(float4(scaledPos, 1.0), WorldMatrix);

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
    o.Norm = normalize(mul(float4(input.Norm, 0.0), NormalMatrix).xyz);
    o.Op = Opacity;
    o.WorldPos = worldPos.xyz;
    return o;
}

float4 ApplyCubeLighting(
    float4 c,
    PSInput input,
    out float3 outAlbedoLin,
    out float3 outNormal,
    out float outRough,
    out float outMetal,
    out float outClearcoat)
{
    float a = c.a;
    float3 straightSrgb = (a > 1e-5) ? (c.rgb / a) : float3(0.0, 0.0, 0.0);
    float3 albedoLinear = SrgbToLinearUnclamped(straightSrgb) * TintColor.rgb;
    float roughness = (MaterialParams.w > 0.5) ? MaterialParams.y : clamp(DefaultRoughness * 0.85, 0.15, 0.90);
    float metallic = (MaterialParams.w > 0.5) ? MaterialParams.x : 0.0;
    float clearcoat = (MaterialParams.w > 0.5) ? MaterialParams.z : 0.0;

    float2 dUv = input.UV * max(DetailParams.w, 0.001);
    float3 n = normalize(input.Norm);
    if (DetailParams.y > 0.5)
    {
        n = PerturbNormalCotangent(n, input.WorldPos, dUv, gNormalMap.SampleLevel(gDetailSampler, dUv, 0), DetailParams.x);
    }
    if (DetailParams.z > 0.5)
    {
        ApplyRoughMetalSample(gRoughMetal.SampleLevel(gDetailSampler, dUv, 0), DetailParams.z, roughness, metallic);
    }

    float3 litLinear;
    if (UseSimpleLight > 0.5)
        litLinear = CalcSimpleLgtPbrEx(albedoLinear, n, input.WorldPos, metallic, roughness, clearcoat, LightIntensity);
    else
        litLinear = CalcDynamicLgtPbrEx(albedoLinear, n, input.WorldPos, input.Pos.xy, metallic, roughness, clearcoat);

    if (EmissiveColor.w > 0.0)
        litLinear += EmissiveColor.rgb * EmissiveColor.w;

    outAlbedoLin = albedoLinear;
    outNormal = n;
    outRough = roughness;
    outMetal = metallic;
    outClearcoat = clearcoat;
    return float4(litLinear * a, a);
}

OpaqueMrtOutput PS_Cube(PSInput input)
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op * TintColor.a;
    clip(c.a - AlphaThreshold);
    float3 albedoLin, n;
    float r, m, cc;
    float4 col = ApplyCubeLighting(c, input, albedoLin, n, r, m, cc);
    return PackOpaqueMrt(col, n, albedoLin, r, m, cc);
}

float4 PS_Cube_SemiTrans(PSInput input) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op * TintColor.a;
    clip(c.a - 0.004);
    clip(0.999 - c.a);
    float3 albedoLin, n;
    float r, m, cc;
    return ApplyCubeLighting(c, input, albedoLin, n, r, m, cc);
}

OITOutput PS_Cube_OIT(PSInput input)
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op * TintColor.a;
    clip(c.a - 0.004);
    clip(0.999 - c.a);

    float3 albedoLin, n;
    float r, m, cc;
    c = ApplyCubeLighting(c, input, albedoLin, n, r, m, cc);

    float z = input.Pos.z;
    float weight = clamp(
        pow(min(1.0, c.a * 10.0) + 0.01, 3.0) *
        1e8 * pow(1.0 - z * 0.9, 3.0),
        1e-2, 3e3
    );

    OITOutput o;
    o.Accum = float4(c.rgb, c.a) * weight;
    o.Reveal = c.a;
    return o;
}

void PS_Cube_DepthOnly(PSInput input)
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op * TintColor.a;
    clip(c.a - AlphaThreshold);
    clip(AlphaMax - c.a);
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
            var vsBytes = ShaderBytecodeCache.GetOrCompile("Cube_VS", ShaderSource, "VS_Cube", "vs_5_0", "cube_shader");
            var psOpaqueBytes = ShaderBytecodeCache.GetOrCompile("Cube_PS_Opaque", ShaderSource, "PS_Cube", "ps_5_0", "cube_shader");
            var psSemiBytes = ShaderBytecodeCache.GetOrCompile("Cube_PS_Semi", ShaderSource, "PS_Cube_SemiTrans", "ps_5_0", "cube_shader");
            var psOitBytes = ShaderBytecodeCache.GetOrCompile("Cube_PS_OIT", ShaderSource, "PS_Cube_OIT", "ps_5_0", "cube_shader");
            var psDepthBytes = ShaderBytecodeCache.GetOrCompile("Cube_PS_Depth", ShaderSource, "PS_Cube_DepthOnly", "ps_5_0", "cube_shader");

            if (vsBytes == null || psOpaqueBytes == null || psSemiBytes == null || psOitBytes == null || psDepthBytes == null)
                return;

            _vs = device.CreateVertexShader(vsBytes);
            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,   12, 0),
                new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float, 20, 0),
            };
            _inputLayout = device.CreateInputLayout(inputElements, vsBytes);

            _psOpaque = device.CreatePixelShader(psOpaqueBytes);
            _psSemiTrans = device.CreatePixelShader(psSemiBytes);
            _psOit = device.CreatePixelShader(psOitBytes);
            _psDepthOnly = device.CreatePixelShader(psDepthBytes);

            _cbBuffer = device.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<CubeCb>(), BindFlags.ConstantBuffer));

            _sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
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

            CreateCubeGeometry(device);

            _devicePointer = device.NativePointer;
            _initialized = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] CubeD3DEffect Initialize エラー: {ex.Message}");
            Dispose();
        }
    }

    private unsafe void CreateCubeGeometry(ID3D11Device device)
    {
        var vertices = new List<CubeVertex>();
        var indices = new List<ushort>();

        var faces = new[]
        {
            // 前面 (Z-)
            (new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
             new Vector3(0, 0, -1)),
            // 背面 (Z+)
            (new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f,  0.5f),
             new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f),
             new Vector3(0, 0, 1)),
            // 右面 (X+)
            (new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
             new Vector3( 0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f,  0.5f),
             new Vector3(1, 0, 0)),
            // 左面 (X-)
            (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f, -0.5f),
             new Vector3(-1, 0, 0)),
            // 上面 (Y-)
            (new Vector3(-0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
             new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
             new Vector3(0, -1, 0)),
            // 下面 (Y+)
            (new Vector3(-0.5f,  0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
             new Vector3(-0.5f,  0.5f,  0.5f), new Vector3( 0.5f,  0.5f,  0.5f),
             new Vector3(0, 1, 0)),
        };

        foreach (var (v0, v1, v2, v3, normal) in faces)
        {
            ushort baseIdx = (ushort)vertices.Count;
            vertices.Add(new CubeVertex { Position = v0, TexCoord = new Vector2(0, 0), Normal = normal });
            vertices.Add(new CubeVertex { Position = v1, TexCoord = new Vector2(1, 0), Normal = normal });
            vertices.Add(new CubeVertex { Position = v2, TexCoord = new Vector2(0, 1), Normal = normal });
            vertices.Add(new CubeVertex { Position = v3, TexCoord = new Vector2(1, 1), Normal = normal });

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

        int vertStride = Marshal.SizeOf<CubeVertex>();
        fixed (CubeVertex* pv = vertArray)
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
        if (!_initialized || _vs == null || _psOpaque == null) return;

        float safeDepth = MathF.Abs(DepthScale) < 1e-4f ? 1e-4f : DepthScale;
        var effectiveWorld = Matrix4x4.CreateScale(1f, 1f, safeDepth) * renderContext.WorldMatrix;
        var normalMatrix = MatrixTransformHelper.ComputeNormalMatrix(effectiveWorld);

        float alphaMin = renderContext.AlphaThreshold;
        float alphaMax = 2.0f;

        ID3D11PixelShader activePs;
        if (renderContext.IsShadowPass)
        {
            activePs = _psDepthOnly!;
            alphaMin = renderContext.AlphaThreshold;
            alphaMax = 2.0f;
        }
        else if (renderContext.IsOitPass)
        {
            activePs = _psOit!;
        }
        else if (renderContext.IsDepthOnlyPass && renderContext.IsSemiTransPass)
        {
            activePs = _psDepthOnly!;
            alphaMin = 0.004f;
            alphaMax = 0.999f;
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
        var cb = new CubeCb
        {
            WorldMatrix = renderContext.WorldMatrix,
            ViewProjMatrix = renderContext.ViewProjectionMatrix,
            NormalMatrix = normalMatrix,
            HalfWidth = renderContext.HalfScreenWidth,
            HalfHeight = renderContext.HalfScreenHeight,
            Opacity = renderContext.Opacity,
            AlphaThreshold = alphaMin,
            DepthScale = DepthScale,
            LightIntensity = LightIntensity,
            AlphaMax = alphaMax,
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

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<CubeVertex>(), 0);
        ctx.IASetIndexBuffer(_indexBuffer!, Format.R16_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        ctx.VSSetShader(_vs);
        ctx.PSSetShader(activePs);
        ctx.VSSetConstantBuffer(0, _cbBuffer);
        ctx.PSSetConstantBuffer(0, _cbBuffer);
        ctx.PSSetSampler(0, _sampler);
        ctx.PSSetSampler(4, _detailSampler);
        ctx.PSSetShaderResource(0, inputSrv);
        ctx.PSSetShaderResource(1, renderContext.NormalMapSrv!);
        ctx.PSSetShaderResource(6, renderContext.RoughnessMapSrv!);

        ctx.DrawIndexed(_indexCount, 0, 0);

        ctx.PSSetShaderResource(1, null!);
        ctx.PSSetShaderResource(6, null!);
    }

    public void Dispose()
    {
        _vs?.Dispose(); _vs = null;
        _psOpaque?.Dispose(); _psOpaque = null;
        _psSemiTrans?.Dispose(); _psSemiTrans = null;
        _psOit?.Dispose(); _psOit = null;
        _psDepthOnly?.Dispose(); _psDepthOnly = null;
        _inputLayout?.Dispose(); _inputLayout = null;
        _vertexBuffer?.Dispose(); _vertexBuffer = null;
        _indexBuffer?.Dispose(); _indexBuffer = null;
        _cbBuffer?.Dispose(); _cbBuffer = null;
        _sampler?.Dispose(); _sampler = null;
        _detailSampler?.Dispose(); _detailSampler = null;
        _devicePointer = IntPtr.Zero;
        _initialized = false;
    }
}
