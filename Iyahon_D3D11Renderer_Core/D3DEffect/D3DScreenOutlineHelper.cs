using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Iyahon_D3D11Renderer_Core.D3DEffect.Effects;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect;

/// <summary>
/// Renders isolated silhouette masks into an offscreen target and composites dilated,
/// anti-aliased / blurred screen-space outlines onto the active HDR render target.
/// </summary>
internal sealed class D3DScreenOutlineHelper : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FsVertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbScreenOutline
    {
        public Vector4 OutlineColor;  // Linear RGB + Alpha
        public Vector4 TexelAndSize;  // x=1/W, y=1/H, z=ThicknessPx, w=BlurPx
        public Vector4 Params;        // x=Quality, y=OutlineOnly, z=Opacity, w=0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbSilQuad
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 ViewProjMatrix;
        public float HalfWidth;
        public float HalfHeight;
        public float Opacity;
        public float AlphaCutoff;
    }

    private const string OutlineShaderSource = @"
cbuffer CbScreenOutline : register(b0)
{
    float4 OutlineColor;
    float4 TexelAndSize;
    float4 Params;
};

Texture2D    gMaskTex       : register(t0);
SamplerState gPointSampler  : register(s0);
SamplerState gLinearSampler : register(s1);

struct VSIn  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSIn  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

PSIn VS_ScreenOutline(VSIn input)
{
    PSIn o;
    o.Pos = float4(input.Pos, 1.0);
    o.UV = input.UV;
    return o;
}

float4 PS_ScreenOutline(PSIn input) : SV_Target
{
    float centerA = saturate(gMaskTex.SampleLevel(gPointSampler, input.UV, 0).a);
    float radius = max(TexelAndSize.z, 0.0);
    float blur = max(TexelAndSize.w, 0.0);
    float maxReach = radius + blur;
    if (maxReach <= 0.01) discard;
    if (centerA >= 0.995) discard;

    float2 texel = TexelAndSize.xy;
    int samples = clamp((int)(Params.x + 0.5), 8, 64);
    int rings = clamp((int)ceil(maxReach / 2.5), 1, 10);
    float rStep = maxReach / (float)rings;
    float aaWidth = max(blur, 0.75);

    float bestCoverage = 0.0;
    const float TWO_PI = 6.2831853;
    [loop]
    for (int ring = 1; ring <= rings; ring++)
    {
        float dist = rStep * (float)ring;
        float distCov = 1.0 - smoothstep(radius, radius + aaWidth, dist);
        if (distCov <= 0.001) continue;

        float maxRingSample = 0.0;
        [loop]
        for (int s = 0; s < samples; s++)
        {
            float ang = ((float)s / (float)samples) * TWO_PI;
            float2 offset = float2(cos(ang), sin(ang)) * dist * texel;
            float aSample = saturate(gMaskTex.SampleLevel(gLinearSampler, saturate(input.UV + offset), 0).a);
            maxRingSample = max(maxRingSample, aSample);
        }
        bestCoverage = max(bestCoverage, maxRingSample * distCov);
    }

    float rimMask = bestCoverage * (1.0 - centerA);
    float outAlpha = saturate(rimMask * OutlineColor.a * Params.z);
    if (outAlpha <= 0.002) discard;
    return float4(OutlineColor.rgb * outAlpha, outAlpha);
}
";

    private const string SilQuadShaderSource = @"
cbuffer CbSilQuad : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float AlphaCutoff;
};

Texture2D    gSourceTex     : register(t0);
SamplerState gLinearSampler : register(s0);

struct VSIn  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSIn  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float Op : TEXCOORD1; float Cutoff : TEXCOORD2; };

PSIn VS_SilQuad(VSIn input)
{
    PSIn o;
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
    o.UV = input.UV;
    o.Op = Opacity;
    o.Cutoff = AlphaCutoff;
    return o;
}

float4 PS_SilQuad(PSIn input) : SV_Target
{
    float4 c = gSourceTex.Sample(gLinearSampler, input.UV);
    float a = saturate(c.a * input.Op);
    clip(a - input.Cutoff);
    return float4(a, a, a, a);
}
";

    private IntPtr _devicePtr;
    private int _maskWidth;
    private int _maskHeight;
    private bool _initialized;
    private bool _disposed;

    private ID3D11Texture2D? _maskTex;
    private ID3D11RenderTargetView? _maskRtv;
    private ID3D11ShaderResourceView? _maskSrv;

    private ID3D11VertexShader? _vsOutline;
    private ID3D11PixelShader? _psOutline;
    private ID3D11VertexShader? _vsSilQuad;
    private ID3D11PixelShader? _psSilQuad;
    private ID3D11InputLayout? _layout;
    private ID3D11Buffer? _fsVb;
    private ID3D11Buffer? _quadVb;
    private ID3D11Buffer? _cbOutline;
    private ID3D11Buffer? _cbSilQuad;

    private ID3D11SamplerState? _samplerPoint;
    private ID3D11SamplerState? _samplerLinear;
    private ID3D11BlendState? _blendPremul;
    private ID3D11DepthStencilState? _dsReadOnly;
    private ID3D11DepthStencilState? _dsDisabled;
    private ID3D11RasterizerState? _rsCullNone;

    public unsafe void Initialize(ID3D11Device device)
    {
        if (_disposed) return;
        if (_initialized && _devicePtr == device.NativePointer) return;
        ReleaseResources();

        try
        {
            var vsBytes = ShaderBytecodeCache.GetOrCompile("ScreenOutline_VS", OutlineShaderSource, "VS_ScreenOutline", "vs_5_0", "outline_screen");
            var psBytes = ShaderBytecodeCache.GetOrCompile("ScreenOutline_PS", OutlineShaderSource, "PS_ScreenOutline", "ps_5_0", "outline_screen");
            var vsQuadBytes = ShaderBytecodeCache.GetOrCompile("SilQuad_VS", SilQuadShaderSource, "VS_SilQuad", "vs_5_0", "outline_silquad");
            var psQuadBytes = ShaderBytecodeCache.GetOrCompile("SilQuad_PS", SilQuadShaderSource, "PS_SilQuad", "ps_5_0", "outline_silquad");

            if (vsBytes == null || psBytes == null || vsQuadBytes == null || psQuadBytes == null)
                return;

            _vsOutline = device.CreateVertexShader(vsBytes);
            _psOutline = device.CreatePixelShader(psBytes);
            _vsSilQuad = device.CreateVertexShader(vsQuadBytes);
            _psSilQuad = device.CreatePixelShader(psQuadBytes);

            _layout = device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
            }, vsBytes);

            int stride = Marshal.SizeOf<FsVertex>();
            var fsVerts = new FsVertex[]
            {
                new() { Position = new Vector3(-1f, -1f, 0f), TexCoord = new Vector2(0f, 1f) },
                new() { Position = new Vector3( 1f, -1f, 0f), TexCoord = new Vector2(1f, 1f) },
                new() { Position = new Vector3(-1f,  1f, 0f), TexCoord = new Vector2(0f, 0f) },
                new() { Position = new Vector3( 1f,  1f, 0f), TexCoord = new Vector2(1f, 0f) },
            };
            fixed (FsVertex* p = fsVerts)
                _fsVb = device.CreateBuffer(new BufferDescription(stride * 4, BindFlags.VertexBuffer), new SubresourceData((IntPtr)p));

            var qVerts = new FsVertex[]
            {
                new() { Position = new Vector3(-0.5f, -0.5f, 0f), TexCoord = new Vector2(0f, 0f) },
                new() { Position = new Vector3( 0.5f, -0.5f, 0f), TexCoord = new Vector2(1f, 0f) },
                new() { Position = new Vector3(-0.5f,  0.5f, 0f), TexCoord = new Vector2(0f, 1f) },
                new() { Position = new Vector3( 0.5f,  0.5f, 0f), TexCoord = new Vector2(1f, 1f) },
            };
            fixed (FsVertex* p = qVerts)
                _quadVb = device.CreateBuffer(new BufferDescription(stride * 4, BindFlags.VertexBuffer), new SubresourceData((IntPtr)p));

            _cbOutline = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbScreenOutline>(), BindFlags.ConstantBuffer));
            _cbSilQuad = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbSilQuad>(), BindFlags.ConstantBuffer));

            _samplerPoint = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });
            _samplerLinear = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            var premulDesc = new BlendDescription { IndependentBlendEnable = false };
            premulDesc.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = Blend.One,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            _blendPremul = device.CreateBlendState(premulDesc);

            _dsReadOnly = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.LessEqual,
            });
            _dsDisabled = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = false,
                DepthWriteMask = DepthWriteMask.Zero,
            });
            _rsCullNone = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                DepthClipEnable = false,
            });

            _devicePtr = device.NativePointer;
            _initialized = true;
        }
        catch
        {
            ReleaseResources();
        }
    }

    public bool BeginMaskPass(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11DepthStencilView? sceneDsv,
        int width,
        int height)
    {
        if (!_initialized || _devicePtr != device.NativePointer || width <= 0 || height <= 0)
            return false;

        EnsureMaskTarget(device, width, height);
        if (_maskRtv == null || _maskSrv == null) return false;

        ctx.PSSetShaderResource(0, null!);
        ctx.ClearRenderTargetView(_maskRtv, new Color4(0f, 0f, 0f, 0f));
        ctx.OMSetRenderTargets(_maskRtv, sceneDsv);
        ctx.OMSetBlendState(_blendPremul, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(sceneDsv != null ? _dsReadOnly : _dsDisabled, 0);
        ctx.RSSetState(_rsCullNone);
        return true;
    }

    public void DrawQuadSilhouette(
        ID3D11DeviceContext ctx,
        ID3D11ShaderResourceView sourceSrv,
        in Matrix4x4 worldMatrix,
        in Matrix4x4 viewProjMatrix,
        float halfProjWidth,
        float halfProjHeight,
        float opacity,
        float alphaCutoff)
    {
        if (!_initialized || _vsSilQuad == null || _psSilQuad == null || _quadVb == null || _cbSilQuad == null)
            return;

        ctx.IASetInputLayout(_layout);
        ctx.IASetVertexBuffer(0, _quadVb, Marshal.SizeOf<FsVertex>(), 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(_vsSilQuad);
        ctx.PSSetShader(_psSilQuad);
        ctx.PSSetSampler(0, _samplerLinear);
        ctx.PSSetShaderResource(0, sourceSrv);

        var cb = new CbSilQuad
        {
            WorldMatrix = worldMatrix,
            ViewProjMatrix = viewProjMatrix,
            HalfWidth = halfProjWidth,
            HalfHeight = halfProjHeight,
            Opacity = opacity,
            AlphaCutoff = alphaCutoff,
        };
        ctx.UpdateSubresource(in cb, _cbSilQuad);
        ctx.VSSetConstantBuffer(0, _cbSilQuad);
        ctx.PSSetConstantBuffer(0, _cbSilQuad);
        ctx.Draw(4, 0);
        ctx.PSSetShaderResource(0, null!);
    }

    public void CompositeOutline(
        ID3D11DeviceContext ctx,
        ID3D11RenderTargetView targetRtv,
        ID3D11DepthStencilView? restoreDsv,
        int width,
        int height,
        in OutlineParameters outline)
    {
        if (!_initialized || _maskSrv == null || _vsOutline == null || _psOutline == null ||
            _fsVb == null || _cbOutline == null || width <= 0 || height <= 0)
            return;

        ctx.OMSetRenderTargets(targetRtv, (ID3D11DepthStencilView?)null);
        ctx.OMSetBlendState(_blendPremul, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_dsDisabled, 0);
        ctx.RSSetState(_rsCullNone);

        ctx.IASetInputLayout(_layout);
        ctx.IASetVertexBuffer(0, _fsVb, Marshal.SizeOf<FsVertex>(), 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(_vsOutline);
        ctx.PSSetShader(_psOutline);
        ctx.PSSetSampler(0, _samplerPoint);
        ctx.PSSetSampler(1, _samplerLinear);
        ctx.PSSetShaderResource(0, _maskSrv);

        var cb = new CbScreenOutline
        {
            OutlineColor = new Vector4(
                SrgbToLinear(outline.Color.X),
                SrgbToLinear(outline.Color.Y),
                SrgbToLinear(outline.Color.Z),
                outline.Color.W),
            TexelAndSize = new Vector4(1f / width, 1f / height, outline.Thickness, outline.Blur),
            Params = new Vector4(outline.Quality, outline.OutlineOnly ? 1f : 0f, outline.Opacity, 0f),
        };
        ctx.UpdateSubresource(in cb, _cbOutline);
        ctx.PSSetConstantBuffer(0, _cbOutline);
        ctx.Draw(4, 0);

        ctx.PSSetShaderResource(0, null!);
        ctx.OMSetRenderTargets(targetRtv, restoreDsv);
    }

    public ID3D11RenderTargetView? MaskRtv => _maskRtv;

    private void EnsureMaskTarget(ID3D11Device device, int width, int height)
    {
        if (_maskTex != null && _maskWidth == width && _maskHeight == height)
            return;

        DisposeMaskTarget();
        _maskTex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _maskRtv = device.CreateRenderTargetView(_maskTex);
        _maskSrv = device.CreateShaderResourceView(_maskTex);
        _maskWidth = width;
        _maskHeight = height;
    }

    private void DisposeMaskTarget()
    {
        _maskSrv?.Dispose(); _maskSrv = null;
        _maskRtv?.Dispose(); _maskRtv = null;
        _maskTex?.Dispose(); _maskTex = null;
        _maskWidth = 0;
        _maskHeight = 0;
    }

    private static float SrgbToLinear(float x)
    {
        x = MathF.Max(0f, x);
        return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    private void ReleaseResources()
    {
        DisposeMaskTarget();
        _rsCullNone?.Dispose(); _rsCullNone = null;
        _dsDisabled?.Dispose(); _dsDisabled = null;
        _dsReadOnly?.Dispose(); _dsReadOnly = null;
        _blendPremul?.Dispose(); _blendPremul = null;
        _samplerLinear?.Dispose(); _samplerLinear = null;
        _samplerPoint?.Dispose(); _samplerPoint = null;
        _cbSilQuad?.Dispose(); _cbSilQuad = null;
        _cbOutline?.Dispose(); _cbOutline = null;
        _quadVb?.Dispose(); _quadVb = null;
        _fsVb?.Dispose(); _fsVb = null;
        _layout?.Dispose(); _layout = null;
        _psSilQuad?.Dispose(); _psSilQuad = null;
        _vsSilQuad?.Dispose(); _vsSilQuad = null;
        _psOutline?.Dispose(); _psOutline = null;
        _vsOutline?.Dispose(); _vsOutline = null;
        _devicePtr = IntPtr.Zero;
        _initialized = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
    }
}
