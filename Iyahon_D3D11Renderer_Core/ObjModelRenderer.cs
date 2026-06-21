using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Iyahon_D3D11Renderer_Core.Lighting;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

internal sealed class ObjModelRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CbObjModel
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 ViewProjMatrix;
        public float HalfWidth;
        public float HalfHeight;
        public float Opacity;
        public float MinAlphaVal;
        public Vector4 BaseColor;
        public Vector3 ShadowLightPos;
        public float ShadowLightRange;
    }

    private const int ObjVertexStride = 48;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11PixelShader? _psOIT;
    private ID3D11PixelShader? _psShadowCube;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _cbBuffer;
    private ID3D11SamplerState? _sampler;
    private ID3D11Texture2D? _whiteTexture;
    private ID3D11ShaderResourceView? _whiteSrv;
    private bool _initialized;
    private bool _disposed;

    private ID3D11RenderTargetView? _rtv;

    private const string ShaderSourcePrefix = @"
cbuffer CbObjModel : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float MinAlphaVal;
    float4 BaseColor;
    float3 ShadowLightPos;
    float ShadowLightRange;
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
};

struct OITOutput
{
    float4 Accum  : SV_Target0;
    float  Reveal : SV_Target1;
};

Texture2D    gTex    : register(t0);
SamplerState gSampler: register(s0);

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

    o.Normal = normalize(mul(input.Normal, (float3x3)WorldMatrix));
    o.UV = input.UV;
    o.Color = input.Color;
    o.Op = Opacity;
    o.WorldPos = worldPos.xyz;
    return o;
}

float3 CalcSimpleLgt(float3 normal)
{
    float3 lightDir = normalize(float3(0.3, 0.7, -1.0));
    float ndl = saturate(dot(normal, -lightDir));
    float3 diffuse = float3(1.0, 0.95, 0.9) * ndl;
    float3 ambient = float3(0.3, 0.3, 0.35);
    return ambient + diffuse;
}

float4 PS_Obj(PSInput input) : SV_Target
{
    float4 texColor = gTex.Sample(gSampler, input.UV);
    float4 vertexColor = input.Color;
    if (vertexColor.r == 0.0 && vertexColor.g == 0.0 && vertexColor.b == 0.0 && vertexColor.a == 0.0)
    {
        vertexColor = float4(1.0, 1.0, 1.0, 1.0);
    }
    float4 c = texColor * vertexColor * BaseColor;

    float3 n = normalize(input.Normal);
    float3 lgtVal;

    if (UseSimpleLight > 0.5)
        lgtVal = CalcSimpleLgt(n);
    else
        lgtVal = CalcDynamicLgtEff(n, input.WorldPos);

    c.rgb *= lgtVal;
    c *= input.Op;
    clip(c.a - MinAlphaVal);
    return c;
}

float4 PS_Obj_ShadowCube(PSInput input) : SV_Target
{
    float4 texColor = gTex.Sample(gSampler, input.UV);
    float4 vertexColor = input.Color;
    if (vertexColor.r == 0.0 && vertexColor.g == 0.0 && vertexColor.b == 0.0 && vertexColor.a == 0.0)
    {
        vertexColor = float4(1.0, 1.0, 1.0, 1.0);
    }
    float4 c = texColor * vertexColor * BaseColor;
    c *= input.Op;
    clip(c.a - MinAlphaVal);

    if (ShadowLightRange > 0.1)
    {
        float dist = length(input.WorldPos - ShadowLightPos);
        float normDist = dist / max(ShadowLightRange, 1.0);
        return float4(normDist, 0.0, 0.0, 1.0);
    }
    else
    {
        return float4(input.Pos.z, 0.0, 0.0, 1.0);
    }
}

OITOutput PS_Obj_OIT(PSInput input)
{
    float4 texColor = gTex.Sample(gSampler, input.UV);
    float4 vertexColor = input.Color;
    if (vertexColor.r == 0.0 && vertexColor.g == 0.0 && vertexColor.b == 0.0 && vertexColor.a == 0.0)
    {
        vertexColor = float4(1.0, 1.0, 1.0, 1.0);
    }
    float4 c = texColor * vertexColor * BaseColor;

    float3 n = normalize(input.Normal);
    float3 lgtVal;

    if (UseSimpleLight > 0.5)
        lgtVal = CalcSimpleLgt(n);
    else
        lgtVal = CalcDynamicLgtEff(n, input.WorldPos);

    c.rgb *= lgtVal;

    c *= input.Op;
    clip(c.a - 0.004);
    clip(0.999 - c.a);

    float z = input.Pos.z;
    float weight = clamp(
        pow(min(1.0, c.a * 10.0) + 0.01, 3.0) *
        100000000.0 * pow(1.0 - z * 0.9, 3.0),
        0.01, 3000.0
    );

    OITOutput o;
    o.Accum = float4(c.rgb, c.a) * weight;
    o.Reveal = c.a;
    return o;
}
";

    public void Initialize(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        if (_initialized) return;

        try
        {
            var vsBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "VS_Obj", "obj_shader",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (vsBlob == null) { Log("VS_Obj コンパイルエラー"); return; }

            _vs = device.CreateVertexShader(vsBlob);

            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,     0, 0),
                new InputElementDescription("NORMAL",   0, Format.R32G32B32_Float,    12, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,       24, 0),
                new InputElementDescription("COLOR",    0, Format.R32G32B32A32_Float, 32, 0),
            };
            _inputLayout = device.CreateInputLayout(inputElements, vsBlob);
            vsBlob.Dispose();

            var psBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "PS_Obj", "obj_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psBlob == null) { Log("PS_Obj コンパイルエラー"); return; }
            _ps = device.CreatePixelShader(psBlob);
            psBlob.Dispose();

            var psShadowBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "PS_Obj_ShadowCube", "obj_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psShadowBlob == null) { Log("PS_Obj_ShadowCube コンパイルエラー"); return; }
            _psShadowCube = device.CreatePixelShader(psShadowBlob);
            psShadowBlob.Dispose();

            var psOITBlob = Vortice.D3DCompiler.Compiler.Compile(
                ShaderSource, "PS_Obj_OIT", "obj_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psOITBlob == null) { Log("PS_Obj_OIT コンパイルエラー"); return; }
            _psOIT = device.CreatePixelShader(psOITBlob);
            psOITBlob.Dispose();

            _cbBuffer = device.CreateBuffer(new BufferDescription(
                Marshal.SizeOf<CbObjModel>(), BindFlags.ConstantBuffer));

            _sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            CreateWhiteTexture(device);

            _initialized = true;
        }
        catch (Exception ex)
        {
            Log($"Initialize エラー: {ex.Message}");
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
    }

    public void RenderModel(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        ObjModelData model,
        Matrix4x4 worldMatrix,
        Matrix4x4 viewProjMatrix,
        float halfScreenWidth,
        float halfScreenHeight,
        float opacity,
        float alphaThreshold,
        bool isOit)
    {
        if (!_initialized || _vs == null || _ps == null || _psOIT == null) return;

        try
        {
            if (model.VertexBuffer == null || model.VertexBuffer.NativePointer == IntPtr.Zero ||
                model.IndexBuffer == null || model.IndexBuffer.NativePointer == IntPtr.Zero) return;

            if (model.VertexBuffer.Device == null || model.VertexBuffer.Device.NativePointer != d3d.NativePointer ||
                model.IndexBuffer.Device == null || model.IndexBuffer.Device.NativePointer != d3d.NativePointer)
            {
                return;
            }
        }
        catch { return; }

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, model.VertexBuffer, ObjVertexStride, 0);
        ctx.IASetIndexBuffer(model.IndexBuffer, Format.R32_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        ctx.VSSetShader(_vs);
        ctx.PSSetShader(isOit ? _psOIT : _ps);
        ctx.PSSetSampler(0, _sampler);

        foreach (var part in model.Parts)
        {
            if (part.IndexCount <= 0) continue;

            var cb = new CbObjModel
            {
                WorldMatrix = worldMatrix,
                ViewProjMatrix = viewProjMatrix,
                HalfWidth = halfScreenWidth,
                HalfHeight = halfScreenHeight,
                Opacity = opacity,
                MinAlphaVal = alphaThreshold,
                BaseColor = part.BaseColor,
                ShadowLightPos = Vector3.Zero,
                ShadowLightRange = 0f,
            };

            ctx.UpdateSubresource(ref cb, _cbBuffer!);
            ctx.VSSetConstantBuffer(0, _cbBuffer);
            ctx.PSSetConstantBuffer(0, _cbBuffer);

            ID3D11ShaderResourceView? activeTexture = null;
            if (part.Texture != null && part.Texture.NativePointer != IntPtr.Zero)
            {
                try
                {
                    if (part.Texture.Device != null && part.Texture.Device.NativePointer == d3d.NativePointer)
                    {
                        activeTexture = part.Texture;
                    }
                }
                catch { }
            }

            ctx.PSSetShaderResource(0, activeTexture ?? _whiteSrv);
            ctx.DrawIndexed(part.IndexCount, part.IndexOffset, 0);
        }
    }

    public void RenderShadow(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        ObjModelData model,
        Matrix4x4 worldMatrix,
        Matrix4x4 viewProjMatrix,
        Vector3 lightPos,
        float lightRange,
        bool isCube)
    {
        if (!_initialized || _vs == null || _ps == null || _psShadowCube == null) return;

        try
        {
            if (model.VertexBuffer == null || model.VertexBuffer.NativePointer == IntPtr.Zero ||
                model.IndexBuffer == null || model.IndexBuffer.NativePointer == IntPtr.Zero) return;

            if (model.VertexBuffer.Device == null || model.VertexBuffer.Device.NativePointer != d3d.NativePointer ||
                model.IndexBuffer.Device == null || model.IndexBuffer.Device.NativePointer != d3d.NativePointer)
            {
                return;
            }
        }
        catch { return; }

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, model.VertexBuffer, ObjVertexStride, 0);
        ctx.IASetIndexBuffer(model.IndexBuffer, Format.R32_UInt, 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        ctx.VSSetShader(_vs);

        // ★ 補正：2D/立体を問わず、影描画パスの時は「常に」影書き込み用ピクセルシェーダ _psShadowCube を適用します。
        // これにより、2D影時にOBJモデルがカラーテクスチャを誤書き込みして影を破壊するバグを完全に排除します。
        ctx.PSSetShader(_psShadowCube);

        ctx.PSSetSampler(0, _sampler);

        foreach (var part in model.Parts)
        {
            if (part.IndexCount <= 0) continue;

            var cb = new CbObjModel
            {
                WorldMatrix = worldMatrix,
                ViewProjMatrix = viewProjMatrix,
                HalfWidth = 0f,
                HalfHeight = 0f,
                Opacity = 1.0f,
                MinAlphaVal = 0.5f,
                BaseColor = part.BaseColor,
                ShadowLightPos = lightPos,
                ShadowLightRange = lightRange,
            };

            ctx.UpdateSubresource(ref cb, _cbBuffer!);
            ctx.VSSetConstantBuffer(0, _cbBuffer);
            ctx.PSSetConstantBuffer(0, _cbBuffer);

            ID3D11ShaderResourceView? activeTexture = null;
            if (part.Texture != null && part.Texture.NativePointer != IntPtr.Zero)
            {
                try
                {
                    if (part.Texture.Device != null && part.Texture.Device.NativePointer == d3d.NativePointer)
                    {
                        activeTexture = part.Texture;
                    }
                }
                catch { }
            }

            ctx.PSSetShaderResource(0, activeTexture ?? _whiteSrv);
            ctx.DrawIndexed(part.IndexCount, part.IndexOffset, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _vs?.Dispose();
        _ps?.Dispose();
        _psOIT?.Dispose();
        _psShadowCube?.Dispose();
        _inputLayout?.Dispose();
        _cbBuffer?.Dispose();
        _sampler?.Dispose();
        _whiteSrv?.Dispose();
        _whiteTexture?.Dispose();
        _initialized = false;
    }

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] ObjModelRenderer: {msg}");
}