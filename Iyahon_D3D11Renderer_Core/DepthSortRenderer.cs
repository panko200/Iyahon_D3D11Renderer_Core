using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using Iyahon_D3D11Renderer_Core.Lighting;
using BlendOperation = Vortice.Direct3D11.BlendOperation;
using Blend = Vortice.Direct3D11.Blend;
using BlendDescription = Vortice.Direct3D11.BlendDescription;
using Filter = Vortice.Direct3D11.Filter;
using FillMode = Vortice.Direct3D11.FillMode;
using InputElementDescription = Vortice.Direct3D11.InputElementDescription;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

internal static class DepthSortRendererRegistry
{
    private static readonly List<WeakReference<DepthSortRenderer>> _activeRenderers = new();
    private static readonly object _lock = new();

    static DepthSortRendererRegistry()
    {
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public static void Register(DepthSortRenderer renderer)
    {
        lock (_lock)
        {
            _activeRenderers.RemoveAll(wr => !wr.TryGetTarget(out _));
            _activeRenderers.Add(new WeakReference<DepthSortRenderer>(renderer));
        }
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            foreach (var wr in _activeRenderers)
            {
                if (wr.TryGetTarget(out var renderer))
                {
                    try
                    {
                        renderer.Dispose();
                    }
                    catch { }
                }
            }
            _activeRenderers.Clear();
        }
    }
}

internal sealed class CachedEffect
{
    public ID3DEffect Effect { get; }
    public DateTime LastSeen { get; set; }

    public CachedEffect(ID3DEffect effect)
    {
        Effect = effect;
        LastSeen = DateTime.UtcNow;
    }
}

internal sealed class DepthSortRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbPerObject
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 ViewProjMatrix;
        public float HalfWidth;
        public float HalfHeight;
        public float Opacity;
        public float AlphaThreshold;
        public Vector3 ShadowLightPos;
        public float ShadowLightRange;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbFxaa
    {
        public float RcpFrameX;
        public float RcpFrameY;
        public float _pad0;
        public float _pad1;
    }

    private const int MAX_SHADOWS = 8;

    private IntPtr _d3dDevicePointer = IntPtr.Zero;
    private IntPtr _d3dContextPointer = IntPtr.Zero;

    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _renderTarget;
    private ID2D1Bitmap1? _renderTargetBitmap;

    private ID3D11DepthStencilView? _dsv;
    private ID3D11Texture2D? _depthStencil;

    private ID3D11Texture2D? _accumTexture;
    private ID3D11RenderTargetView? _accumRtv;
    private ID3D11ShaderResourceView? _accumSrv;

    private ID3D11Texture2D? _revealTexture;
    private ID3D11RenderTargetView? _revealRtv;
    private ID3D11ShaderResourceView? _revealSrv;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psOpaque;
    private ID3D11PixelShader? _psOIT;
    private ID3D11PixelShader? _psSemiTrans;
    private ID3D11PixelShader? _psShadowCube;
    private ID3D11VertexShader? _resolveVs;
    private ID3D11PixelShader? _resolvePs;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11InputLayout? _resolveInputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _resolveVertexBuffer;
    private ID3D11Buffer? _cbPerObject;
    private ID3D11Buffer? _cbLighting;
    private ID3D11SamplerState? _samplerPoint;
    private ID3D11SamplerState? _samplerLinear;

    // 影シャドウアトラス（単一巨大バッファ）
    private ID3D11Texture2D? _shadowAtlasTex;
    private ID3D11RenderTargetView? _shadowAtlasRtv;
    private ID3D11ShaderResourceView? _shadowAtlasSrv;
    private ID3D11Texture2D? _shadowAtlasDepthTex;
    private ID3D11DepthStencilView? _shadowAtlasDsv;

    private ID3D11BlendState? _blendStateOpaque;
    private ID3D11BlendState? _blendStateNoColor;
    private ID3D11BlendState? _blendStateOIT;
    private ID3D11BlendState? _blendStateResolve;
    private ID3D11BlendState? _blendStateFxaa;

    private ID3D11Texture2D? _fxaaTexture;
    private ID3D11RenderTargetView? _fxaaRtv;
    private ID3D11ShaderResourceView? _fxaaSrv;
    private ID3D11PixelShader? _psFxaa;
    private ID3D11VertexShader? _vsFxaa;
    private ID3D11Buffer? _cbFxaa;

    private ID3D11DepthStencilState? _depthStateOpaque;
    private ID3D11DepthStencilState? _depthStateOIT;
    private ID3D11DepthStencilState? _depthStateSemiTrans;
    private ID3D11DepthStencilState? _depthStateSemiBack;
    private ID3D11DepthStencilState? _depthStateSemiFront;
    private ID3D11DepthStencilState? _depthStateDisabled;

    private ID3D11RasterizerState? _rasterizerState;
    private int _width;
    private int _height;
    private bool _initialized;
    private bool _disposed;
    private int _currentShadowResolution = 0;

    private System.Runtime.CompilerServices.ConditionalWeakTable<YukkuriMovieMaker.Project.Items.IVideoItem, CachedEffect> _d3dEffects = new();

    private ObjModelRenderer? _objModelRenderer;

    public int Width => _width;
    public int Height => _height;
    public IntPtr DevicePointer => _d3dDevicePointer;
    public ID2D1Bitmap1? RenderTargetBitmap => _renderTargetBitmap;

    public DepthSortRenderer()
    {
        DepthSortRendererRegistry.Register(this);
    }

    public ID3DEffect? GetOrCreateEffect(YukkuriMovieMaker.Project.Items.IVideoItem item, string effectId)
    {
        if (_d3dEffects.TryGetValue(item, out var cached))
        {
            cached.LastSeen = DateTime.UtcNow;
            var info = Iyahon_D3D11Renderer_Core.D3DEffect.D3DEffectRegistry.GetEffectInfo(effectId);
            if (info != null && cached.Effect.GetType() == info.EffectType)
            {
                return cached.Effect;
            }
            else
            {
                cached.Effect.Dispose();
                _d3dEffects.Remove(item);
                var newEffect = Iyahon_D3D11Renderer_Core.D3DEffect.D3DEffectRegistry.CreateEffect(effectId);
                if (newEffect != null)
                {
                    _d3dEffects.Add(item, new CachedEffect(newEffect));
                }
                return newEffect;
            }
        }
        else
        {
            var newEffect = Iyahon_D3D11Renderer_Core.D3DEffect.D3DEffectRegistry.CreateEffect(effectId);
            if (newEffect != null)
            {
                _d3dEffects.Add(item, new CachedEffect(newEffect));
            }
            return newEffect;
        }
    }

    private void PruneEffects()
    {
        var now = DateTime.UtcNow;
        var toRemove = new List<YukkuriMovieMaker.Project.Items.IVideoItem>();

        foreach (var pair in _d3dEffects)
        {
            if ((now - pair.Value.LastSeen).TotalSeconds > 5.0)
            {
                toRemove.Add(pair.Key);
            }
        }

        foreach (var key in toRemove)
        {
            if (_d3dEffects.TryGetValue(key, out var cached))
            {
                cached.Effect.Dispose();
                _d3dEffects.Remove(key);
            }
        }
    }

    private const string MainShaderSource = @"
cbuffer CbPerObject : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float AlphaThreshold;
    float3 ShadowLightPos;
    float ShadowLightRange;
};
" + LightingShaderCode.HlslCode + @"

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float3 Norm : TEXCOORD1; float Op : TEXCOORD2; float3 WorldPos : TEXCOORD3; };

Texture2D    gTex    : register(t0);
SamplerState gSampler: register(s0);

PSInput VS(VSInput input)
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

    o.UV = input.UV;
    
    float3 localNorm = float3(0.0, 0.0, -1.0);
    o.Norm = normalize(mul(float4(localNorm, 0.0), WorldMatrix).xyz);
    o.Op = Opacity;
    o.WorldPos = worldPos.xyz;
    return o;
}

float4 PS_Opaque(PSInput input, bool isFront : SV_IsFrontFace) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;
    clip(c.a - AlphaThreshold);

    float3 n = normalize(input.Norm);
    if (isFront)
    {
        n = -n;
    }

    if (UseSimpleLight > 0.5)
    {
        float lgtVal = CalcSimpleLgtEff(n, 0.5);
        c.rgb *= lgtVal;
    }
    else
    {
        float3 lgtVal = CalcDynamicLgtEff(n, input.WorldPos);
        c.rgb *= lgtVal;
    }

    return c;
}

float4 PS_ShadowCube(PSInput input) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;
    clip(c.a - AlphaThreshold);

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

struct OITOutput
{
    float4 Accum  : SV_Target0;
    float  Reveal : SV_Target1;
};

OITOutput PS_OIT(PSInput input, bool isFront : SV_IsFrontFace)
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;

    clip(c.a - 0.004);
    clip(0.999 - c.a);

    float3 n = normalize(input.Norm);
    if (isFront)
    {
        n = -n;
    }

    if (UseSimpleLight > 0.5)
    {
        float lgtVal = CalcSimpleLgtEff(n, 0.5);
        c.rgb *= lgtVal;
    }
    else
    {
        float3 lgtVal = CalcDynamicLgtEff(n, input.WorldPos);
        c.rgb *= lgtVal;
    }

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

float4 PS_SemiTrans(PSInput input, bool isFront : SV_IsFrontFace) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;
    clip(c.a - 0.004);
    clip(0.999 - c.a);

    float3 n = normalize(input.Norm);
    if (isFront)
    {
        n = -n;
    }

    if (UseSimpleLight > 0.5)
    {
        float lgtVal = CalcSimpleLgtEff(n, 0.5);
        c.rgb *= lgtVal;
    }
    else
    {
        float3 lgtVal = CalcDynamicLgtEff(n, input.WorldPos);
        c.rgb *= lgtVal;
    }

    return c;
}
";

    private const string ResolveShaderSource = @"
struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

Texture2D    accumTex  : register(t0);
Texture2D    revealTex : register(t1);
SamplerState gSampler  : register(s0);

PSInput VS_Resolve(VSInput input)
{
    PSInput o;
    o.Pos = float4(input.Pos.xy, 0.5, 1.0);
    o.UV  = input.UV;
    return o;
}

float4 PS_Resolve(PSInput input) : SV_Target
{
    float4 accum  = accumTex.Sample(gSampler,  input.UV);
    float  reveal = revealTex.Sample(gSampler, input.UV).r;

    if (accum.a < 1e-5)
        return float4(0, 0, 0, 0);

    float3 color = accum.rgb / max(accum.a, 1e-5);
    float alpha = 1.0 - reveal;

    return float4(color * alpha, alpha);
}
";

    public bool Initialize(IGraphicsDevicesAndContext devices, int width, int height)
    {
        if (devices == null || devices.D3D == null || devices.D3D.Device == null) return false;

        var currentDevice = devices.D3D.Device;
        if (currentDevice.NativePointer == IntPtr.Zero) return false;

        if (devices.D3D.DeviceContext == null) return false;

        var currentContext = devices.D3D.DeviceContext;
        if (currentContext.NativePointer == IntPtr.Zero) return false;

        var settings = D3D11RendererSettings.Default;
        int targetRes = (int)settings.ShadowResolution;

        if (_d3dDevicePointer == IntPtr.Zero || _d3dDevicePointer != currentDevice.NativePointer || _d3dContextPointer != currentContext.NativePointer)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log("D3D11デバイス変更を検出。リソースを再生成します。");
            DisposeTargets();
            DisposeResources();

            _d3dDevicePointer = currentDevice.NativePointer;
            _d3dContextPointer = currentContext.NativePointer;
            _initialized = false;
        }

        if (_initialized && _width == width && _height == height && _currentShadowResolution == targetRes) return true;

        DisposeTargets();
        _width = width;
        _height = height;

        try
        {
            _renderTarget = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.Shared,
            });
            _rtv = currentDevice.CreateRenderTargetView(_renderTarget);

            _depthStencil = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.D24_UNorm_S8_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
            });
            _dsv = currentDevice.CreateDepthStencilView(_depthStencil);

            _accumTexture = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _accumRtv = currentDevice.CreateRenderTargetView(_accumTexture);
            _accumSrv = currentDevice.CreateShaderResourceView(_accumTexture);

            _revealTexture = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _revealRtv = currentDevice.CreateRenderTargetView(_revealTexture);
            _revealSrv = currentDevice.CreateShaderResourceView(_revealTexture);

            _fxaaTexture = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _fxaaRtv = currentDevice.CreateRenderTargetView(_fxaaTexture);
            _fxaaSrv = currentDevice.CreateShaderResourceView(_fxaaTexture);

            // ─── 影アトラス巨大テクスチャ (8x6 ＝計48タイル) ───
            int maxTileSize = 16384 / 8; // 横8分割なので1タイルあたり最大2048pxに制限
            int actualTileSize = Math.Min(targetRes, maxTileSize);

            int atlasWidth = actualTileSize * 8;
            int atlasHeight = actualTileSize * 6;

            _shadowAtlasTex = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = atlasWidth,
                Height = atlasHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _shadowAtlasRtv = currentDevice.CreateRenderTargetView(_shadowAtlasTex);
            _shadowAtlasSrv = currentDevice.CreateShaderResourceView(_shadowAtlasTex);

            _shadowAtlasDepthTex = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = atlasWidth,
                Height = atlasHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.D24_UNorm_S8_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
            });
            _shadowAtlasDsv = currentDevice.CreateDepthStencilView(_shadowAtlasDepthTex);

            _renderTargetBitmap?.Dispose();
            _renderTargetBitmap = D2DD3DBridge.GetD2DBitmapFromD3DTexture(_renderTarget, devices);

            if (!_initialized)
            {
                if (!InitializeShaders(currentDevice)) return false;
                if (!InitializeStates(currentDevice)) return false;
                InitializeGeometry(currentDevice);
            }

            _initialized = true;
            _currentShadowResolution = targetRes;
            Iyahon_D3D11Renderer_CorePlugin.Log($"DepthSortRenderer 初期化完了: {width}x{height} (影アトラス統合8x6版)");
            return true;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"DepthSortRenderer Initialize エラー: {ex.Message}");
            return false;
        }
    }

    private bool InitializeShaders(ID3D11Device device)
    {
        try
        {
            var vsBlob = Vortice.D3DCompiler.Compiler.Compile(
                MainShaderSource, "VS", "inline_shader",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (vsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("VS コンパイルエラー"); return false; }

            _vs = device.CreateVertexShader(vsBlob);
            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    12, 0),
            };
            _inputLayout = device.CreateInputLayout(inputElements, vsBlob);
            vsBlob.Dispose();

            var psOpaqueBlob = Vortice.D3DCompiler.Compiler.Compile(
                MainShaderSource, "PS_Opaque", "inline_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psOpaqueBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_Opaque コンパイルエラー"); return false; }
            _psOpaque = device.CreatePixelShader(psOpaqueBlob);
            psOpaqueBlob.Dispose();

            var psShadowBlob = Vortice.D3DCompiler.Compiler.Compile(
                MainShaderSource, "PS_ShadowCube", "inline_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psShadowBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_ShadowCube コンパイルエラー"); return false; }
            _psShadowCube = device.CreatePixelShader(psShadowBlob);
            psShadowBlob.Dispose();

            var psOITBlob = Vortice.D3DCompiler.Compiler.Compile(
                MainShaderSource, "PS_OIT", "inline_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psOITBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_OIT コンパイルエラー"); return false; }
            _psOIT = device.CreatePixelShader(psOITBlob);
            psOITBlob.Dispose();

            var psSemiBlob = Vortice.D3DCompiler.Compiler.Compile(
                MainShaderSource, "PS_SemiTrans", "inline_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psSemiBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_SemiTrans コンパイルエラー"); return false; }
            _psSemiTrans = device.CreatePixelShader(psSemiBlob);
            psSemiBlob.Dispose();

            var resolveVsBlob = Vortice.D3DCompiler.Compiler.Compile(
                ResolveShaderSource, "VS_Resolve", "inline_resolve",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (resolveVsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("VS_Resolve コンパイルエラー"); return false; }
            _resolveVs = device.CreateVertexShader(resolveVsBlob);
            _resolveInputLayout = device.CreateInputLayout(inputElements, resolveVsBlob);
            resolveVsBlob.Dispose();

            var resolvePsBlob = Vortice.D3DCompiler.Compiler.Compile(
                ResolveShaderSource, "PS_Resolve", "inline_resolve",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (resolvePsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_Resolve コンパイルエラー"); return false; }
            _resolvePs = device.CreatePixelShader(resolvePsBlob);
            resolvePsBlob.Dispose();

            _cbPerObject = device.CreateBuffer(
                new BufferDescription(
                    Marshal.SizeOf<CbPerObject>(),
                    BindFlags.ConstantBuffer));

            var fxaaVsBlob = Vortice.D3DCompiler.Compiler.Compile(
                FxaaShaderSource.Source, "VS_Fxaa", "inline_fxaa",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (fxaaVsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("VS_Fxaa コンパイルエラー"); return false; }
            _vsFxaa = device.CreateVertexShader(fxaaVsBlob);
            fxaaVsBlob.Dispose();

            var fxaaPsBlob = Vortice.D3DCompiler.Compiler.Compile(
                FxaaShaderSource.Source, "PS_Fxaa", "inline_fxaa",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (fxaaPsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_Fxaa コンパイルエラー"); return false; }
            _psFxaa = device.CreatePixelShader(fxaaPsBlob);
            fxaaPsBlob.Dispose();

            _cbFxaa = device.CreateBuffer(
                new BufferDescription(
                    Marshal.SizeOf<CbFxaa>(),
                    BindFlags.ConstantBuffer));

            _cbLighting = device.CreateBuffer(
                new BufferDescription(
                    Marshal.SizeOf<CbLighting>(),
                    BindFlags.ConstantBuffer));

            return true;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"InitializeShaders エラー: {ex.Message}");
            return false;
        }
    }

    private bool InitializeStates(ID3D11Device device)
    {
        try
        {
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

            {
                var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
                desc.RenderTarget[0] = new RenderTargetBlendDescription
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
                _blendStateOpaque = device.CreateBlendState(desc);
            }

            {
                var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
                desc.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = false,
                    RenderTargetWriteMask = 0,
                };
                _blendStateNoColor = device.CreateBlendState(desc);
            }

            {
                var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = true };
                desc.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = true,
                    SourceBlend = Blend.One,
                    DestinationBlend = Blend.One,
                    BlendOperation = BlendOperation.Add,
                    SourceBlendAlpha = Blend.One,
                    DestinationBlendAlpha = Blend.One,
                    BlendOperationAlpha = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                };
                desc.RenderTarget[1] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = true,
                    SourceBlend = Blend.Zero,
                    DestinationBlend = Blend.InverseSourceColor,
                    BlendOperation = BlendOperation.Add,
                    SourceBlendAlpha = Blend.Zero,
                    DestinationBlendAlpha = Blend.One,
                    BlendOperationAlpha = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteEnable.Red,
                };
                _blendStateOIT = device.CreateBlendState(desc);
            }

            {
                var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
                desc.RenderTarget[0] = new RenderTargetBlendDescription
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
                _blendStateResolve = device.CreateBlendState(desc);
            }

            {
                var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
                desc.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = false,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                };
                _blendStateFxaa = device.CreateBlendState(desc);
            }

            _depthStateOpaque = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.Less,
                StencilEnable = false,
            });

            _depthStateOIT = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.LessEqual,
                StencilEnable = false,
            });

            _depthStateSemiTrans = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.LessEqual,
                StencilEnable = true,
                StencilReadMask = byte.MaxValue,
                StencilWriteMask = byte.MaxValue,
                FrontFace = new DepthStencilOperationDescription
                {
                    StencilFunc = ComparisonFunction.Always,
                    StencilPassOp = StencilOperation.Replace,
                    StencilDepthFailOp = StencilOperation.Keep,
                    StencilFailOp = StencilOperation.Keep,
                },
                BackFace = new DepthStencilOperationDescription
                {
                    StencilFunc = ComparisonFunction.Always,
                    StencilPassOp = StencilOperation.Replace,
                    StencilDepthFailOp = StencilOperation.Keep,
                    StencilFailOp = StencilOperation.Keep,
                },
            });

            _depthStateSemiBack = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.Greater,
                StencilEnable = true,
                StencilReadMask = byte.MaxValue,
                StencilWriteMask = 0,
                FrontFace = new DepthStencilOperationDescription
                {
                    StencilFunc = ComparisonFunction.Equal,
                    StencilPassOp = StencilOperation.Keep,
                    StencilDepthFailOp = StencilOperation.Keep,
                    StencilFailOp = StencilOperation.Keep,
                },
                BackFace = new DepthStencilOperationDescription
                {
                    StencilFunc = ComparisonFunction.Equal,
                    StencilPassOp = StencilOperation.Keep,
                    StencilDepthFailOp = StencilOperation.Keep,
                    StencilFailOp = StencilOperation.Keep,
                },
            });

            _depthStateSemiFront = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.Equal,
                StencilEnable = true,
                StencilReadMask = byte.MaxValue,
                StencilWriteMask = 0,
                FrontFace = new DepthStencilOperationDescription
                {
                    StencilFunc = ComparisonFunction.Equal,
                    StencilPassOp = StencilOperation.Keep,
                    StencilDepthFailOp = StencilOperation.Keep,
                    StencilFailOp = StencilOperation.Keep,
                },
                BackFace = new DepthStencilOperationDescription
                {
                    StencilFunc = ComparisonFunction.Equal,
                    StencilPassOp = StencilOperation.Keep,
                    StencilDepthFailOp = StencilOperation.Keep,
                    StencilFailOp = StencilOperation.Keep,
                },
            });

            _depthStateDisabled = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = false,
                DepthWriteMask = DepthWriteMask.Zero,
                StencilEnable = false,
            });

            _rasterizerState = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                DepthClipEnable = false,
            });

            return true;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"InitializeStates エラー: {ex.Message}");
            return false;
        }
    }

    private unsafe void InitializeGeometry(ID3D11Device device)
    {
        var vertices = new Vertex[]
        {
            new() { Position = new Vector3(-0.5f, -0.5f, 0f), TexCoord = new Vector2(0f, 0f) },
            new() { Position = new Vector3( 0.5f, -0.5f, 0f), TexCoord = new Vector2(1f, 0f) },
            new() { Position = new Vector3(-0.5f,  0.5f, 0f), TexCoord = new Vector2(0f, 1f) },
            new() { Position = new Vector3( 0.5f,  0.5f, 0f), TexCoord = new Vector2(1f, 1f) },
        };

        int stride = Marshal.SizeOf<Vertex>();
        int totalBytes = stride * vertices.Length;
        fixed (Vertex* pVerts = vertices)
        {
            _vertexBuffer = device.CreateBuffer(
                new BufferDescription(totalBytes, BindFlags.VertexBuffer),
                new SubresourceData((IntPtr)pVerts, totalBytes));
        }

        var resolveVerts = new Vertex[]
        {
            new() { Position = new Vector3(-1f, -1f, 0f), TexCoord = new Vector2(0f, 1f) },
            new() { Position = new Vector3( 1f, -1f, 0f), TexCoord = new Vector2(1f, 1f) },
            new() { Position = new Vector3(-1f,  1f, 0f), TexCoord = new Vector2(0f, 0f) },
            new() { Position = new Vector3( 1f,  1f, 0f), TexCoord = new Vector2(1f, 0f) },
        };
        fixed (Vertex* pVerts = resolveVerts)
        {
            _resolveVertexBuffer = device.CreateBuffer(
                new BufferDescription(totalBytes, BindFlags.VertexBuffer),
                new SubresourceData((IntPtr)pVerts, totalBytes));
        }
    }

    public ID3D11Texture2D? Render(IGraphicsDevicesAndContext devices, List<RenderItem> items, int screenWidth, int screenHeight)
    {
        if (!_initialized || _rtv == null || _dsv == null) return null;
        if (items.Count == 0) return null;

        PruneEffects();

        // ─── 影描画の前に、前フレームのバインドを確実に解除して衝突を防ぐ ───
        devices.D3D.DeviceContext.PSSetShaderResource(2, null!);

        var settings = D3D11RendererSettings.Default;
        int targetRes = (int)settings.ShadowResolution;

        int maxTileSize = 16384 / 8;
        int actualTileSize = Math.Min(targetRes, maxTileSize);

        // カメラの中心点（合焦面）を計算
        Vector3 cameraTarget = Vector3.Zero;
        if (items != null && items.Count > 0)
        {
            var firstItem = items.FirstOrDefault();
            if (firstItem != null)
            {
                if (Matrix4x4.Invert(firstItem.DrawDescription.Camera, out var invCam))
                {
                    cameraTarget = Vector3.Transform(Vector3.Zero, invCam);
                }
            }
        }

        // ─── 影描画：アトラスセクションを一気にクリア＆描画 ───
        if (settings.EnableShadow && _shadowAtlasRtv != null && _shadowAtlasDsv != null)
        {
            var activeLights = LightManager.GetActiveLights();
            var shadowLights = activeLights.Where(l => l.CastShadow).Take(MAX_SHADOWS).ToList();

            // アトラス全体を1回だけクリア（クリアは1.0f）
            devices.D3D.DeviceContext.OMSetRenderTargets(_shadowAtlasRtv, _shadowAtlasDsv);
            devices.D3D.DeviceContext.ClearRenderTargetView(_shadowAtlasRtv, new Color4(1.0f, 1.0f, 1.0f, 1.0f));
            devices.D3D.DeviceContext.ClearDepthStencilView(_shadowAtlasDsv, DepthStencilClearFlags.Depth, 1.0f, 0);

            // ★修正：影描画開始時に、D3D11の必要なステートをすべて明示的にセットします。
            // D2Dベイクや前フレームのFXAA等で破壊されたステートをここで完全にリセットし、シャドウアトラスへのデプス書き込みを保証します。
            devices.D3D.DeviceContext.OMSetDepthStencilState(_depthStateOpaque, 0); // 深度有効
            devices.D3D.DeviceContext.OMSetBlendState(_blendStateFxaa, null, unchecked((int)0xFFFFFFFF)); // ブレンドを完全無効化（デプスのブレンド異常を防止）
            devices.D3D.DeviceContext.RSSetState(_rasterizerState); // ラスタライザー（カリング・クリッピング保護）


            int currentTile = 0;
            for (int i = 0; i < shadowLights.Count; i++)
            {
                var shadowLight = shadowLights[i];
                bool isCube = shadowLight.Type == LightType.Point;
                int tilesNeeded = isCube ? 6 : 1;

                // 最大48タイルの範囲内で安全に割り当て
                if (currentTile + tilesNeeded <= 48)
                {
                    // ライトタイプごとに必要なビュー射影だけを計算する(無駄な計算を避ける)。
                    // Point: キューブ各面ごとにRenderShadowMap内で個別計算するためここでは不要。
                    // Area: 光源中心から単一視点(PCSS用シャドウマップ1枚分)。
                    // Directional/Spot: 通常の単一視点。
                    Matrix4x4 lightViewProj = Matrix4x4.Identity;
                    if (shadowLight.Type == LightType.Area)
                    {
                        lightViewProj = LightManager.BuildAreaSingleViewProj(shadowLight, items);
                    }
                    else if (!isCube)
                    {
                        lightViewProj = LightManager.BuildLightViewProj(shadowLight, screenWidth, screenHeight, items);
                    }

                    // ★ 影の動的LOD（距離減衰スケール）の計算
                    float scaleFactor = 1.0f;
                    if (shadowLight.Type != LightType.Directional)
                    {
                        float dist = Vector3.Distance(cameraTarget, shadowLight.Position);
                        if (dist > 8000f) scaleFactor = 0.25f; // 8000px超で1/4解像度に動的ダウン
                        else if (dist > 4000f) scaleFactor = 0.5f;  // 4000px超で1/2解像度に動的ダウン
                    }

                    int renderingRes = (int)(actualTileSize * scaleFactor);

                    RenderShadowMap(devices.D3D.Device, devices.D3D.DeviceContext, items, shadowLight, lightViewProj, currentTile, actualTileSize, renderingRes);
                    currentTile += tilesNeeded;
                }
            }

            // レンダーターゲットの解除
            devices.D3D.DeviceContext.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
        }

        var mode = settings.TransparencyMode;
        return mode == TransparencyMode.Standard
            ? RenderStandard(devices, items, screenWidth, screenHeight)
            : RenderOIT(devices, items, screenWidth, screenHeight);
    }

    private void RenderShadowMap(ID3D11Device d3d, ID3D11DeviceContext ctx, List<RenderItem> items, LightData shadowLight, Matrix4x4 lightViewProj, int startTile, int actualTileSize, int renderingRes)
    {
        bool isCube = shadowLight.Type == LightType.Point;

        if (isCube)
        {
            for (int face = 0; face < 6; face++)
            {
                int tileIdx = startTile + face;
                SetAtlasViewport(ctx, tileIdx, actualTileSize, renderingRes);

                var cubeViewProj = LightManager.BuildCubeLightViewProj(shadowLight.Position, face, shadowLight.Range);
                DrawShadowObjects(d3d, ctx, items, shadowLight, cubeViewProj, true);
            }
        }
        else
        {
            // Area光源も含め、単一視点のシャドウマップ1枚のみ描画する(1照明=1パス)。
            // Area光源のソフトシャドウ表現はシェーダー側のPCSS(ブロッカーサーチ+可変PCFカーネル)
            // に任せ、ここでは追加のドローコールを発生させない。
            SetAtlasViewport(ctx, startTile, actualTileSize, renderingRes);
            DrawShadowObjects(d3d, ctx, items, shadowLight, lightViewProj, false);
        }
    }

    private void SetAtlasViewport(ID3D11DeviceContext ctx, int tileIdx, int actualTileSize, int renderingRes)
    {
        // 8x6 座標系ビューポート設定
        int x = tileIdx % 8;
        int y = tileIdx / 8;
        // 指定されたタイルセルの内部で、必要十分な解像度(LOD)だけを切り取って描画
        ctx.RSSetViewport(new Viewport(x * actualTileSize, y * actualTileSize, renderingRes, renderingRes, 0f, 1f));
    }

    private void DrawShadowObjects(ID3D11Device d3d, ID3D11DeviceContext ctx, List<RenderItem> items, LightData shadowLight, Matrix4x4 lightViewProj, bool isCube)
    {
        foreach (var item in items)
        {
            if (item.ObjModels == null && item.Srv == null) continue;
            // ★修正：押し出しエフェクトも3Dのレイマーチング影に対応させるため、スキップ処理を削除します。

            if (item.ObjModels != null)
            {
                // OBJモデルの影描画処理（既存のまま）
                try
                {
                    if (_objModelRenderer == null)
                    {
                        _objModelRenderer = new ObjModelRenderer();
                    }
                    _objModelRenderer.Initialize(d3d, ctx);

                    foreach (var model in item.ObjModels)
                    {
                        Matrix4x4 modelMatrix = BuildObjModelMatrix(item);
                        _objModelRenderer.RenderShadow(
                            d3d, ctx, model, modelMatrix, lightViewProj,
                            shadowLight.Position, isCube ? shadowLight.Range : 0f, isCube);
                    }
                }
                catch (Exception ex)
                {
                    Iyahon_D3D11Renderer_CorePlugin.Log($"OBJモデル影描画エラー: {ex.Message}");
                }
            }
            else if (item.D3DVideoEffect != null && item.OriginalItem != null && item.Srv != null)
            {
                var effectId = item.D3DVideoEffect.D3DEffectId;
                var effect = GetOrCreateEffect(item.OriginalItem, effectId);
                if (effect != null)
                {
                    try
                    {
                        effect.Initialize(d3d, ctx);
                        item.D3DVideoEffect.ConfigureEffect(effect, item.ItemFrame, item.ItemLength, item.Fps);
                        Matrix4x4 modelMatrix = BuildModelMatrix(item);

                        // ★修正：2D板ポリゴンで平らな影を描くのをやめ、
                        // シャドウパス用のコンテキストを準備してエフェクト自身の Render() で描画させます。
                        var renderContext = new D3DRenderContext
                        {
                            WorldMatrix = modelMatrix,
                            ViewProjectionMatrix = lightViewProj,
                            CameraWorldPosition = shadowLight.Position, // 影レイマーチングの視点をシャドウ光源位置に設定
                            TextureWidth = (int)item.PixelWidth,
                            TextureHeight = (int)item.PixelHeight,
                            HalfScreenWidth = 0f, // 3D射影変換を行うため 0
                            HalfScreenHeight = 0f,
                            Opacity = item.Opacity,
                            AlphaThreshold = 0.5f,
                            CameraMatrix = Matrix4x4.Identity,
                            IsShadowPass = true,
                            ShadowLightPos = shadowLight.Position,
                            ShadowLightRange = isCube ? shadowLight.Range : 0f
                        };

                        effect.Render(ctx, d3d, item.Srv, renderContext);
                    }
                    catch (Exception ex)
                    {
                        Iyahon_D3D11Renderer_CorePlugin.Log($"D3Dエフェクト影描画エラー: {ex.Message}");
                    }
                }
            }
            else if (item.Srv != null)
            {
                // エフェクト未適用アイテムは従来通り安定した平面影を描画（既存のまま）
                try
                {
                    Matrix4x4 modelMatrix = BuildModelMatrix(item);
                    DrawStandardShadowCube(ctx, item, modelMatrix, lightViewProj, shadowLight.Position, isCube ? shadowLight.Range : 0f);
                }
                catch (Exception ex)
                {
                    Iyahon_D3D11Renderer_CorePlugin.Log($"標準アイテム影描画エラー: {ex.Message}");
                }
            }
        }
    }

    private void DrawStandardShadowCube(ID3D11DeviceContext ctx, RenderItem item, Matrix4x4 modelMatrix, Matrix4x4 lightViewProj, Vector3 lightPos, float lightRange)
    {
        var cb = new CbPerObject
        {
            WorldMatrix = modelMatrix,
            ViewProjMatrix = lightViewProj,
            HalfWidth = 0f,
            HalfHeight = 0f,
            Opacity = item.Opacity,
            AlphaThreshold = 0.5f,
            ShadowLightPos = lightPos,
            ShadowLightRange = lightRange,
        };
        ctx.UpdateSubresource(ref cb, _cbPerObject!);

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

        ctx.VSSetShader(_vs);
        ctx.VSSetConstantBuffer(0, _cbPerObject);
        ctx.PSSetConstantBuffer(0, _cbPerObject);
        ctx.PSSetShaderResource(0, item.Srv);
        ctx.PSSetSampler(0, _samplerLinear);
        ctx.PSSetShader(_psShadowCube);

        ctx.Draw(4, 0);
    }

    private ID3D11Texture2D? RenderStandard(IGraphicsDevicesAndContext devices, List<RenderItem> items, int screenWidth, int screenHeight)
    {
        var d3d = devices.D3D.Device;
        var ctx = devices.D3D.DeviceContext;

        float halfW = screenWidth / 2f;
        float halfH = screenHeight / 2f;
        int layerCount = (int)D3D11RendererSettings.Default.StandardDepthLayerCount;
        int backPassCount = layerCount switch
        {
            <= 2 => 0,
            <= 4 => 1,
            _ => 2,
        };

        try
        {
            ctx.ClearRenderTargetView(_rtv!, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1.0f, 0);

            ctx.RSSetViewport(new Viewport(0, 0, _width, _height, 0f, 1f));
            ctx.RSSetState(_rasterizerState);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            ctx.VSSetConstantBuffer(0, _cbPerObject);
            ctx.PSSetConstantBuffer(0, _cbPerObject);

            var cbLight = LightManager.BuildConstantBuffer(screenWidth, screenHeight, items);
            ctx.UpdateSubresource(ref cbLight, _cbLighting!);
            ctx.VSSetConstantBuffer(1, _cbLighting);
            ctx.PSSetConstantBuffer(1, _cbLighting);

            BindShadowResources(ctx);

            ctx.PSSetSampler(0, _samplerLinear);

            var itemsWithWorld = items
                .Where(i => i.Srv != null || i.ObjModels != null)
                .Select((item, index) => (item, world: item.ObjModels != null ? BuildObjModelMatrix(item) : BuildModelMatrix(item), viewProj: BuildViewProjMatrix(item), index))
                .ToList();

            ctx.OMSetRenderTargets(_rtv!, _dsv);
            ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateOpaque, 0);

            ctx.IASetInputLayout(_inputLayout);
            ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_psOpaque);

            foreach (var (item, world, viewProj, _) in itemsWithWorld)
            {
                DrawItem(d3d, ctx, item, world, viewProj, halfW, halfH, 0.999f, _psOpaque!, _samplerLinear!);
            }

            var sorted = itemsWithWorld
                .OrderBy(x => x.item.Layer)
                .ThenBy(x => x.index)
                .ToList();

            ctx.OMSetRenderTargets(_rtv!, _dsv);
            ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateSemiTrans, 1);
            ctx.PSSetShader(_psSemiTrans);

            foreach (var (item, world, viewProj, _) in sorted)
            {
                DrawItem(d3d, ctx, item, world, viewProj, halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!);
            }

            if (backPassCount > 0)
            {
                ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
                ctx.OMSetDepthStencilState(_depthStateSemiBack, 1);

                for (int pass = 0; pass < backPassCount; pass++)
                {
                    float opacityMul = pass == 0 ? 1f : 0.5f;
                    foreach (var (item, world, viewProj, _) in sorted)
                    {
                        DrawItem(d3d, ctx, item, world, viewProj, halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!, opacityMul);
                    }
                }
            }

            ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1.0f, 0);
            ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateOpaque, 0);
            ctx.PSSetShader(_psOpaque);

            foreach (var (item, world, viewProj, _) in itemsWithWorld)
            {
                DrawItem(d3d, ctx, item, world, viewProj, halfW, halfH, 0.999f, _psOpaque!, _samplerLinear!, 1f);
            }

            ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateSemiTrans, 1);
            ctx.PSSetShader(_psSemiTrans);

            foreach (var (item, world, viewProj, _) in sorted)
            {
                DrawItem(d3d, ctx, item, world, viewProj, halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!, 1f);
            }

            ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.PSSetShader(_psSemiTrans);
            ctx.OMSetDepthStencilState(_depthStateSemiFront, 1);

            foreach (var (item, world, viewProj, _) in sorted)
            {
                DrawItem(d3d, ctx, item, world, viewProj, halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!, 1f);
            }

            UnbindShadowResources(ctx);

            ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
            return _renderTarget;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"RenderStandard エラー: {ex.Message}");
            return null;
        }
    }

    private ID3D11Texture2D? RenderOIT(IGraphicsDevicesAndContext devices, List<RenderItem> items, int screenWidth, int screenHeight)
    {
        if (_accumRtv == null || _revealRtv == null) return null;

        var d3d = devices.D3D.Device;
        var ctx = devices.D3D.DeviceContext;

        float halfW = screenWidth / 2f;
        float halfH = screenHeight / 2f;

        try
        {
            ctx.ClearRenderTargetView(_fxaaRtv!, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearRenderTargetView(_rtv!, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1.0f, 0);
            ctx.ClearRenderTargetView(_accumRtv, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearRenderTargetView(_revealRtv, new Color4(1f, 1f, 1f, 1f));

            ctx.RSSetViewport(new Viewport(0, 0, _width, _height, 0f, 1f));
            ctx.RSSetState(_rasterizerState);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            ctx.VSSetConstantBuffer(0, _cbPerObject);
            ctx.PSSetConstantBuffer(0, _cbPerObject);

            var cbLight = LightManager.BuildConstantBuffer(screenWidth, screenHeight, items);
            ctx.UpdateSubresource(ref cbLight, _cbLighting!);
            ctx.VSSetConstantBuffer(1, _cbLighting);
            ctx.PSSetConstantBuffer(1, _cbLighting);

            BindShadowResources(ctx);

            ctx.PSSetSampler(0, _samplerPoint);

            var itemsWithWorld = items
                .Where(i => i.Srv != null || i.ObjModels != null)
                .Select(item => (item, world: item.ObjModels != null ? BuildObjModelMatrix(item) : BuildModelMatrix(item), viewProj: BuildViewProjMatrix(item)))
                .ToList();

            ctx.OMSetRenderTargets(_fxaaRtv!, _dsv);
            ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateOpaque, 0);

            ctx.IASetInputLayout(_inputLayout);
            ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_psOpaque);

            foreach (var (item, world, viewProj) in itemsWithWorld)
            {
                DrawItem(d3d, ctx, item, world, viewProj, halfW, halfH, 0.999f, _psOpaque!, _samplerPoint!);
            }

            var oitRtvs = new[] { _accumRtv, _revealRtv };
            ctx.OMSetRenderTargets(oitRtvs, _dsv);
            ctx.OMSetBlendState(_blendStateOIT, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateOIT, 0);
            ctx.PSSetShader(_psOIT);

            foreach (var (item, world, viewProj) in itemsWithWorld)
            {
                DrawItem(d3d, ctx, item, world, viewProj, halfW, halfH, 0.004f, _psOIT!, _samplerPoint!);
            }

            ctx.OMSetRenderTargets(_fxaaRtv!, (ID3D11DepthStencilView?)null);
            ctx.OMSetBlendState(_blendStateResolve, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateDisabled, 0);

            ctx.IASetInputLayout(_resolveInputLayout);
            ctx.IASetVertexBuffer(0, _resolveVertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            ctx.VSSetShader(_resolveVs);
            ctx.PSSetShader(_resolvePs);
            ctx.PSSetSampler(0, _samplerLinear);
            ctx.PSSetShaderResource(0, _accumSrv);
            ctx.PSSetShaderResource(1, _revealSrv);

            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
            ctx.PSSetShaderResource(1, (ID3D11ShaderResourceView?)null);

            ctx.OMSetRenderTargets(_rtv!, (ID3D11DepthStencilView?)null);
            ctx.OMSetBlendState(_blendStateFxaa, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateDisabled, 0);

            ctx.IASetInputLayout(_resolveInputLayout);
            ctx.IASetVertexBuffer(0, _resolveVertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            ctx.VSSetShader(_vsFxaa);
            ctx.PSSetShader(_psFxaa);
            ctx.PSSetSampler(0, _samplerLinear);
            ctx.PSSetShaderResource(0, _fxaaSrv);

            var cbFxaaLocal = new CbFxaa
            {
                RcpFrameX = 1.0f / _width,
                RcpFrameY = 1.0f / _height,
            };
            ctx.UpdateSubresource(ref cbFxaaLocal, _cbFxaa!);
            ctx.PSSetConstantBuffer(0, _cbFxaa);

            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
            UnbindShadowResources(ctx);

            ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
            return _renderTarget;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"RenderOIT エラー: {ex.Message}");
            return null;
        }
    }

    private void BindShadowResources(ID3D11DeviceContext ctx)
    {
        if (!D3D11RendererSettings.Default.EnableShadow || _shadowAtlasSrv == null) return;

        ctx.PSSetShaderResource(2, _shadowAtlasSrv);
        // PCF(Percentage-Closer Filtering)は「各サンプル点で深度比較してから結果を平均する」
        // のが正しい設計。Linearフィルタリングだと深度値そのものが先に補間されてしまい、
        // 深度が急激に変化する境界(物体のシルエットエッジなど)で、手前と奥の深度が
        // 混ざった実在しない中間値が生成され、誤った遮蔽判定の原因になっていた。
        // PCFループ側で複数サンプルを平均しているため、ここはPointサンプリングが正しい。
        ctx.PSSetSampler(1, _samplerPoint);
    }

    private void UnbindShadowResources(ID3D11DeviceContext ctx)
    {
        ctx.PSSetShaderResource(2, null!);
        ctx.PSSetSampler(1, null!);
    }

    private void DrawItem(ID3D11Device d3d, ID3D11DeviceContext ctx, RenderItem item, Matrix4x4 world, Matrix4x4 viewProj, float halfW, float halfH, float alphaThreshold, ID3D11PixelShader activePs, ID3D11SamplerState activeSampler, float opacityMultiplier = 1f)
    {
        if (item.ObjModels != null)
        {
            try
            {
                if (_objModelRenderer == null)
                {
                    _objModelRenderer = new ObjModelRenderer();
                }
                _objModelRenderer.Initialize(d3d, ctx);

                Matrix4x4 modelMatrix = BuildObjModelMatrix(item);
                Matrix4x4 viewProjMatrix = BuildViewProjMatrix(item);

                foreach (var model in item.ObjModels)
                {
                    _objModelRenderer.RenderModel(
                        d3d, ctx, model, modelMatrix, viewProjMatrix, halfW, halfH,
                        item.Opacity * opacityMultiplier, alphaThreshold,
                        activePs == _psOIT);
                }

                ctx.IASetInputLayout(_inputLayout);
                ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
                ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                ctx.VSSetShader(_vs);
                ctx.VSSetConstantBuffer(0, _cbPerObject);
                ctx.PSSetConstantBuffer(0, _cbPerObject);
                ctx.RSSetState(_rasterizerState);
                ctx.PSSetShader(activePs);
                ctx.PSSetSampler(0, activeSampler);
            }
            catch (Exception ex)
            {
                Iyahon_D3D11Renderer_CorePlugin.Log($"OBJモデル描画エラー: {ex.Message}");
            }
            return;
        }

        var d3dVideoEffect = item.D3DVideoEffect;
        var originalItem = item.OriginalItem;

        if (d3dVideoEffect != null && originalItem != null && item.Srv != null)
        {
            var effectId = d3dVideoEffect.D3DEffectId;
            var effect = GetOrCreateEffect(originalItem, effectId);
            if (effect != null)
            {
                try
                {
                    effect.Initialize(d3d, ctx);
                    d3dVideoEffect.ConfigureEffect(effect, item.ItemFrame, item.ItemLength, item.Fps);

                    Matrix4x4 modelMatrix = BuildModelMatrix(item);
                    Matrix4x4 viewProjMatrix = BuildViewProjMatrix(item);

                    var renderContext = new D3DRenderContext
                    {
                        WorldMatrix = modelMatrix,
                        ViewProjectionMatrix = viewProjMatrix,
                        TextureWidth = (int)item.PixelWidth,
                        TextureHeight = (int)item.PixelHeight,
                        HalfScreenWidth = halfW,
                        HalfScreenHeight = halfH,
                        Opacity = item.Opacity * opacityMultiplier,
                        AlphaThreshold = alphaThreshold,
                        CameraMatrix = item.DrawDescription.Camera,
                    };

                    effect.Render(ctx, d3d, item.Srv, renderContext);

                    ctx.IASetInputLayout(_inputLayout);
                    ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
                    ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                    ctx.VSSetShader(_vs);
                    ctx.VSSetConstantBuffer(0, _cbPerObject);
                    ctx.PSSetConstantBuffer(0, _cbPerObject);
                    ctx.RSSetState(_rasterizerState);
                    ctx.PSSetShader(activePs);
                    ctx.PSSetSampler(0, activeSampler);
                }
                catch (Exception ex)
                {
                    Iyahon_D3D11Renderer_CorePlugin.Log($"D3Dエフェクト描画エラー: {ex.Message}");
                }
                return;
            }
        }

        // ─── ★修正：エフェクトなし通常アイテム（床など）の描画 ───
        var cb = new CbPerObject
        {
            WorldMatrix = world,
            ViewProjMatrix = viewProj,
            HalfWidth = halfW,
            HalfHeight = halfH,
            Opacity = item.Opacity * opacityMultiplier,
            AlphaThreshold = alphaThreshold,
            ShadowLightPos = Vector3.Zero,
            ShadowLightRange = 0f,
        };
        ctx.UpdateSubresource(ref cb, _cbPerObject!);

        // ★追加：直前に描画されたエフェクトによってスロット0の定数バッファが汚染されている可能性があるため、
        // 通常アイテムを描画する直前に、床用の定数バッファリソース（_cbPerObject）を確実に再バインドします。
        ctx.VSSetConstantBuffer(0, _cbPerObject);
        ctx.PSSetConstantBuffer(0, _cbPerObject);

        ctx.PSSetShaderResource(0, item.Srv);
        ctx.Draw(4, 0);
    }

    private Matrix4x4 BuildModelMatrix(RenderItem item)
    {
        var desc = item.DrawDescription;
        float d2r = MathF.PI / 180f;

        var S = Matrix4x4.CreateScale(item.PixelWidth, item.PixelHeight, 1f);
        var Toffset = Matrix4x4.CreateTranslation(item.BoundsCenterX, item.BoundsCenterY, 0f);

        float zx = (float)desc.Zoom.X;
        float zy = (float)desc.Zoom.Y;
        if (desc.Invert) zx = -zx;
        var Zoom = Matrix4x4.CreateScale(zx, zy, zScale: 1f);

        var Rz = Matrix4x4.CreateRotationZ(d2r * (float)desc.Rotation.Z);
        var Ry = Matrix4x4.CreateRotationY(d2r * -(float)desc.Rotation.Y);
        var Rx = Matrix4x4.CreateRotationX(d2r * -(float)desc.Rotation.X);
        var Tdraw = Matrix4x4.CreateTranslation(desc.Draw);

        return S * Toffset * Zoom * Rz * Ry * Rx * Tdraw;
    }

    private Matrix4x4 BuildObjModelMatrix(RenderItem item)
    {
        var desc = item.DrawDescription;
        float d2r = MathF.PI / 180f;

        var modelData = item.ObjModels?[0];
        var normalize = Matrix4x4.CreateTranslation(-(modelData?.ModelCenter ?? Vector3.Zero))
                      * Matrix4x4.CreateScale(modelData?.ModelScale ?? 1f);

        var yFlip = Matrix4x4.CreateScale(1f, -1f, 1f);

        const float BaseScale = 200f;
        var baseScaleM = Matrix4x4.CreateScale(BaseScale);

        float zx = (float)desc.Zoom.X;
        float zy = (float)desc.Zoom.Y;
        if (desc.Invert) zx = -zx;
        float zScale = (MathF.Abs(zx) + MathF.Abs(zy)) / 2f;
        var Zoom = Matrix4x4.CreateScale(zx, zy, zScale);

        var Rz = Matrix4x4.CreateRotationZ(d2r * (float)desc.Rotation.Z);
        var Ry = Matrix4x4.CreateRotationY(d2r * -(float)desc.Rotation.Y);
        var Rx = Matrix4x4.CreateRotationX(d2r * -(float)desc.Rotation.X);
        var Tdraw = Matrix4x4.CreateTranslation(desc.Draw);

        return normalize * yFlip * baseScaleM * Zoom * Rz * Ry * Rx * Tdraw;
    }

    private Matrix4x4 BuildViewProjMatrix(RenderItem item)
    {
        var desc = item.DrawDescription;
        var cam = desc.Camera;
        var d2dProj = new Matrix4x4(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, -1f / 1000f,
            0f, 0f, 0f, 1f
        );
        return cam * d2dProj;
    }

    private void DisposeTargets()
    {
        _renderTargetBitmap?.Dispose(); _renderTargetBitmap = null;
        _rtv?.Dispose(); _rtv = null;
        _dsv?.Dispose(); _dsv = null;
        _depthStencil?.Dispose(); _depthStencil = null;
        _accumRtv?.Dispose(); _accumRtv = null;
        _accumSrv?.Dispose(); _accumSrv = null;
        _accumTexture?.Dispose(); _accumTexture = null;
        _revealRtv?.Dispose(); _revealRtv = null;
        _revealSrv?.Dispose(); _revealSrv = null;
        _revealTexture?.Dispose(); _revealTexture = null;
        _fxaaRtv?.Dispose(); _fxaaRtv = null;
        _fxaaSrv?.Dispose(); _fxaaSrv = null;
        _fxaaTexture?.Dispose(); _fxaaTexture = null;

        // 巨大影アトラスアセット破棄
        _shadowAtlasTex?.Dispose(); _shadowAtlasTex = null;
        _shadowAtlasRtv?.Dispose(); _shadowAtlasRtv = null;
        _shadowAtlasSrv?.Dispose(); _shadowAtlasSrv = null;
        _shadowAtlasDepthTex?.Dispose(); _shadowAtlasDepthTex = null;
        _shadowAtlasDsv?.Dispose(); _shadowAtlasDsv = null;
    }

    private void DisposeResources()
    {
        foreach (var pair in _d3dEffects)
        {
            try { pair.Value.Effect.Dispose(); } catch { }
        }
        _d3dEffects = new();

        _objModelRenderer?.Dispose();
        _objModelRenderer = null;

        _renderTarget?.Dispose(); _renderTarget = null;
        _vs?.Dispose(); _vs = null;
        _psOpaque?.Dispose(); _psOpaque = null;
        _psOIT?.Dispose(); _psOIT = null;
        _psSemiTrans?.Dispose(); _psSemiTrans = null;
        _psShadowCube?.Dispose(); _psShadowCube = null;
        _resolveVs?.Dispose(); _resolveVs = null;
        _resolvePs?.Dispose(); _resolvePs = null;
        _vsFxaa?.Dispose(); _vsFxaa = null;
        _psFxaa?.Dispose(); _psFxaa = null;
        _inputLayout?.Dispose(); _inputLayout = null;
        _resolveInputLayout?.Dispose(); _resolveInputLayout = null;
        _vertexBuffer?.Dispose(); _vertexBuffer = null;
        _resolveVertexBuffer?.Dispose(); _resolveVertexBuffer = null;
        _cbPerObject?.Dispose(); _cbPerObject = null;
        _cbLighting?.Dispose(); _cbLighting = null;
        _cbFxaa?.Dispose(); _cbFxaa = null;
        _samplerPoint?.Dispose(); _samplerPoint = null;
        _samplerLinear?.Dispose(); _samplerLinear = null;
        _blendStateOpaque?.Dispose(); _blendStateOpaque = null;
        _blendStateNoColor?.Dispose(); _blendStateNoColor = null;
        _blendStateOIT?.Dispose(); _blendStateOIT = null;
        _blendStateResolve?.Dispose(); _blendStateResolve = null;
        _blendStateFxaa?.Dispose(); _blendStateFxaa = null;
        _depthStateOpaque?.Dispose(); _depthStateOpaque = null;
        _depthStateOIT?.Dispose(); _depthStateOIT = null;
        _depthStateSemiTrans?.Dispose(); _depthStateSemiTrans = null;
        _depthStateSemiBack?.Dispose(); _depthStateSemiBack = null;
        _depthStateSemiFront?.Dispose(); _depthStateSemiFront = null;
        _depthStateDisabled?.Dispose(); _depthStateDisabled = null;
        _rasterizerState?.Dispose(); _rasterizerState = null;

        _d3dDevicePointer = IntPtr.Zero;
        _d3dContextPointer = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeTargets();
        DisposeResources();
    }
}

internal sealed class RenderItem
{
    public required DrawDescription DrawDescription { get; init; }
    public required ID3D11ShaderResourceView? Srv { get; init; }
    public float PixelWidth { get; init; }
    public float PixelHeight { get; init; }

    public float BoundsCenterX { get; init; }
    public float BoundsCenterY { get; init; }

    public float Opacity { get; init; } = 1f;

    public int Layer { get; init; }

    public ID3DVideoEffect? D3DVideoEffect { get; init; }
    public YukkuriMovieMaker.Project.Items.IVideoItem? OriginalItem { get; init; }

    public long ItemFrame { get; init; }
    public long ItemLength { get; init; }
    public int Fps { get; init; }

    public List<ObjModelData>? ObjModels { get; init; }
}