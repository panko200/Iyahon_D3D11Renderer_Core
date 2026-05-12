using Iyahon_D3D11Renderer_Core;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

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
        public Matrix4x4 WorldMatrix;   // 64 bytes
        public float HalfWidth;         // 4
        public float HalfHeight;        // 4
        public float Opacity;           // 4
        public float AlphaThreshold;    // 4  → 合計 80 bytes (16 の倍数 OK)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbFxaa
    {
        public float RcpFrameX;  // 1.0 / width
        public float RcpFrameY;  // 1.0 / height
        public float _pad0;
        public float _pad1;
    }

    private readonly IGraphicsDevicesAndContext _devices;
    private readonly ID3D11Device _d3d;
    private readonly ID3D11DeviceContext _ctx;

    // ── 最終出力 ──
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _renderTarget;

    // ── 深度バッファ ──
    private ID3D11DepthStencilView? _dsv;
    private ID3D11Texture2D? _depthStencil;

    // ── OIT: Accumulation バッファ (R16G16B16A16_Float) ──
    private ID3D11Texture2D? _accumTexture;
    private ID3D11RenderTargetView? _accumRtv;
    private ID3D11ShaderResourceView? _accumSrv;

    // ── OIT: Revealage バッファ (R16_Float) ──
    private ID3D11Texture2D? _revealTexture;
    private ID3D11RenderTargetView? _revealRtv;
    private ID3D11ShaderResourceView? _revealSrv;

    // ── シェーダ/パイプライン ──
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psOpaque;           // パス1: 不透明
    private ID3D11PixelShader? _psOIT;              // パス2: OIT 蓄積
    private ID3D11PixelShader? _psSemiTrans;        // 標準モード: 半透明
    private ID3D11VertexShader? _resolveVs;         // パス3: 解決
    private ID3D11PixelShader? _resolvePs;          // パス3: 解決
    private ID3D11InputLayout? _inputLayout;
    private ID3D11InputLayout? _resolveInputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _resolveVertexBuffer;
    private ID3D11Buffer? _cbPerObject;
    private ID3D11SamplerState? _samplerPoint;
    private ID3D11SamplerState? _samplerLinear;

    // ── ブレンドステート ──
    private ID3D11BlendState? _blendStateOpaque;     // パス1: 通常プレマルα
    private ID3D11BlendState? _blendStateNoColor;    // 標準モード: 深度プリパス用（カラー書き込みなし）
    private ID3D11BlendState? _blendStateOIT;        // パス2: RT0加算, RT1乗算
    private ID3D11BlendState? _blendStateResolve;    // パス3: プレマルα合成
    private ID3D11BlendState? _blendStateFxaa;       // パス4: 上書き

    // ── FXAA 中間バッファ ──
    private ID3D11Texture2D? _fxaaTexture;
    private ID3D11RenderTargetView? _fxaaRtv;
    private ID3D11ShaderResourceView? _fxaaSrv;
    private ID3D11PixelShader? _psFxaa;
    private ID3D11VertexShader? _vsFxaa;
    private ID3D11Buffer? _cbFxaa;

    // ── デプスステンシルステート ──
    private ID3D11DepthStencilState? _depthStateOpaque;   // パス1: depth ON, write ON
    private ID3D11DepthStencilState? _depthStateOIT;      // パス2: depth ON, write OFF
    private ID3D11DepthStencilState? _depthStateSemiTrans; // 標準モード半透明: 前面深度プリパス
    private ID3D11DepthStencilState? _depthStateSemiBack;  // 標準モード半透明: 背面色
    private ID3D11DepthStencilState? _depthStateSemiFront; // 標準モード半透明: 前面色
    private ID3D11DepthStencilState? _depthStateDisabled; // パス3: depth OFF

    private ID3D11RasterizerState? _rasterizerState;

    private int _width;
    private int _height;
    private bool _initialized;
    private bool _disposed;

    // ─── D3Dエフェクトインスタンスキャッシュ（IVideoItem ごと） ───
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<YukkuriMovieMaker.Project.Items.IVideoItem, ID3DEffect> _d3dEffects = new();
    private readonly List<ID3DEffect> _allCreatedEffects = new();

    public ID3DEffect? GetOrCreateEffect(YukkuriMovieMaker.Project.Items.IVideoItem item, string effectId)
    {
        if (_d3dEffects.TryGetValue(item, out var cached))
        {
            var info = Iyahon_D3D11Renderer_Core.D3DEffect.D3DEffectRegistry.GetEffectInfo(effectId);
            if (info != null && cached.GetType() == info.EffectType)
            {
                return cached;
            }
            else
            {
                cached.Dispose();
                _d3dEffects.Remove(item);
                var newEffect = Iyahon_D3D11Renderer_Core.D3DEffect.D3DEffectRegistry.CreateEffect(effectId);
                if (newEffect != null)
                {
                    _d3dEffects.Add(item, newEffect);
                    _allCreatedEffects.Add(newEffect);
                }
                return newEffect;
            }
        }
        else
        {
            var newEffect = Iyahon_D3D11Renderer_Core.D3DEffect.D3DEffectRegistry.CreateEffect(effectId);
            if (newEffect != null)
            {
                _d3dEffects.Add(item, newEffect);
                _allCreatedEffects.Add(newEffect);
            }
            return newEffect;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // シェーダソース
    // ═══════════════════════════════════════════════════════════════

    // パス1/パス2 共通の頂点シェーダ + パス1用ピクセルシェーダ + パス2用OITピクセルシェーダ
    private const string MainShaderSource = @"
cbuffer CbPerObject : register(b0)
{
    row_major float4x4 WorldMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float AlphaThreshold;
};

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float Op : TEXCOORD1; };

Texture2D    gTex    : register(t0);
SamplerState gSampler: register(s0);

PSInput VS(VSInput input)
{
    PSInput o;
    float4 pos = mul(float4(input.Pos, 1.0), WorldMatrix);

    o.Pos.x =  pos.x / HalfWidth;
    o.Pos.y = -pos.y / HalfHeight;
    o.Pos.z = -pos.z / 200000.0 + 0.5 * pos.w;
    o.Pos.w =  pos.w;

    o.UV = input.UV;
    o.Op = Opacity;
    return o;
}

float4 PS_Opaque(PSInput input) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;
    clip(c.a - AlphaThreshold);
    return c;
}

struct OITOutput
{
    float4 Accum  : SV_Target0;
    float  Reveal : SV_Target1;
};

OITOutput PS_OIT(PSInput input)
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;

    clip(c.a - 0.004);
    clip(0.999 - c.a);

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

float4 PS_SemiTrans(PSInput input) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op;
    clip(c.a - 0.004);
    clip(0.999 - c.a);
    return c;
}
";

    // パス3: 解決シェーダ (フルスクリーンクワッド)
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

    public DepthSortRenderer(IGraphicsDevicesAndContext devices)
    {
        _devices = devices;
        _d3d = devices.D3D.Device;
        _ctx = devices.D3D.DeviceContext;
    }

    public bool Initialize(int width, int height)
    {
        if (_initialized && _width == width && _height == height) return true;

        DisposeTargets();
        _width = width;
        _height = height;

        try
        {
            // ── 最終レンダーターゲット (B8G8R8A8_UNorm) ──
            _renderTarget = _d3d.CreateTexture2D(new Texture2DDescription
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
            _rtv = _d3d.CreateRenderTargetView(_renderTarget);

            // ── 深度バッファ ──
            _depthStencil = _d3d.CreateTexture2D(new Texture2DDescription
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
            _dsv = _d3d.CreateDepthStencilView(_depthStencil);

            // ── OIT Accumulation バッファ (R16G16B16A16_Float) ──
            _accumTexture = _d3d.CreateTexture2D(new Texture2DDescription
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
            _accumRtv = _d3d.CreateRenderTargetView(_accumTexture);
            _accumSrv = _d3d.CreateShaderResourceView(_accumTexture);

            // ── OIT Revealage バッファ (R16_Float) ──
            _revealTexture = _d3d.CreateTexture2D(new Texture2DDescription
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
            _revealRtv = _d3d.CreateRenderTargetView(_revealTexture);
            _revealSrv = _d3d.CreateShaderResourceView(_revealTexture);

            // ── FXAA 中間バッファ (B8G8R8A8_UNorm) ──
            _fxaaTexture = _d3d.CreateTexture2D(new Texture2DDescription
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
            _fxaaRtv = _d3d.CreateRenderTargetView(_fxaaTexture);
            _fxaaSrv = _d3d.CreateShaderResourceView(_fxaaTexture);

            if (!_initialized)
            {
                if (!InitializeShaders()) return false;
                if (!InitializeStates()) return false;
                InitializeGeometry();
            }

            _initialized = true;
            Iyahon_D3D11Renderer_CorePlugin.Log($"DepthSortRenderer 初期化完了: {width}x{height} (OIT + FXAA)");
            return true;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"DepthSortRenderer Initialize エラー: {ex.Message}");
            return false;
        }
    }

    private bool InitializeShaders()
    {
        try
        {
            // ── メインシェーダ (パス1/パス2) ──
            var vsBlob = Vortice.D3DCompiler.Compiler.Compile(
                MainShaderSource, "VS", "inline_shader",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (vsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("VS コンパイルエラー"); return false; }

            _vs = _d3d.CreateVertexShader(vsBlob);
            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    12, 0),
            };
            _inputLayout = _d3d.CreateInputLayout(inputElements, vsBlob);
            vsBlob.Dispose();

            // PS_Opaque (パス1)
            var psOpaqueBlob = Vortice.D3DCompiler.Compiler.Compile(
                MainShaderSource, "PS_Opaque", "inline_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psOpaqueBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_Opaque コンパイルエラー"); return false; }
            _psOpaque = _d3d.CreatePixelShader(psOpaqueBlob);
            psOpaqueBlob.Dispose();

            // PS_OIT (パス2)
            var psOITBlob = Vortice.D3DCompiler.Compiler.Compile(
                MainShaderSource, "PS_OIT", "inline_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psOITBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_OIT コンパイルエラー"); return false; }
            _psOIT = _d3d.CreatePixelShader(psOITBlob);
            psOITBlob.Dispose();

            // PS_SemiTrans (標準モード半透明)
            var psSemiBlob = Vortice.D3DCompiler.Compiler.Compile(
                MainShaderSource, "PS_SemiTrans", "inline_shader",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (psSemiBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_SemiTrans コンパイルエラー"); return false; }
            _psSemiTrans = _d3d.CreatePixelShader(psSemiBlob);
            psSemiBlob.Dispose();

            // ── 解決シェーダ (パス3) ──
            var resolveVsBlob = Vortice.D3DCompiler.Compiler.Compile(
                ResolveShaderSource, "VS_Resolve", "inline_resolve",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (resolveVsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("VS_Resolve コンパイルエラー"); return false; }
            _resolveVs = _d3d.CreateVertexShader(resolveVsBlob);
            _resolveInputLayout = _d3d.CreateInputLayout(inputElements, resolveVsBlob);
            resolveVsBlob.Dispose();

            var resolvePsBlob = Vortice.D3DCompiler.Compiler.Compile(
                ResolveShaderSource, "PS_Resolve", "inline_resolve",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (resolvePsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_Resolve コンパイルエラー"); return false; }
            _resolvePs = _d3d.CreatePixelShader(resolvePsBlob);
            resolvePsBlob.Dispose();

            // ── 定数バッファ ──
            _cbPerObject = _d3d.CreateBuffer(
                new BufferDescription(
                    Marshal.SizeOf<CbPerObject>(),
                    BindFlags.ConstantBuffer));

            // ── FXAA シェーダ (パス4) ──
            var fxaaVsBlob = Vortice.D3DCompiler.Compiler.Compile(
                FxaaShaderSource.Source, "VS_Fxaa", "inline_fxaa",
                Array.Empty<ShaderMacro>(), null,
                "vs_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (fxaaVsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("VS_Fxaa コンパイルエラー"); return false; }
            _vsFxaa = _d3d.CreateVertexShader(fxaaVsBlob);
            fxaaVsBlob.Dispose();

            var fxaaPsBlob = Vortice.D3DCompiler.Compiler.Compile(
                FxaaShaderSource.Source, "PS_Fxaa", "inline_fxaa",
                Array.Empty<ShaderMacro>(), null,
                "ps_5_0", Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None);
            if (fxaaPsBlob == null) { Iyahon_D3D11Renderer_CorePlugin.Log("PS_Fxaa コンパイルエラー"); return false; }
            _psFxaa = _d3d.CreatePixelShader(fxaaPsBlob);
            fxaaPsBlob.Dispose();

            _cbFxaa = _d3d.CreateBuffer(
                new BufferDescription(
                    Marshal.SizeOf<CbFxaa>(),
                    BindFlags.ConstantBuffer));

            return true;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"InitializeShaders エラー: {ex.Message}");
            return false;
        }
    }

    private bool InitializeStates()
    {
        try
        {
            _samplerPoint = _d3d.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            _samplerLinear = _d3d.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            // ── パス1: 通常プレマルチプライドアルファ合成 ──
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
                _blendStateOpaque = _d3d.CreateBlendState(desc);
            }

            // ── 標準モード半透明の前面深度プリパス: カラー書き込みなし ──
            {
                var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
                desc.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = false,
                    RenderTargetWriteMask = 0,
                };
                _blendStateNoColor = _d3d.CreateBlendState(desc);
            }

            // ── パス2: OIT 蓄積ブレンド ──
            // RT0 (accumulation): 加算 (One + One)
            // RT1 (revealage):    乗算 (Zero + InvSrcAlpha) → result = dest * (1 - srcAlpha)
            {
                var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = true };
                // RT0: Accumulation — 加算合成
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
                // RT1: Revealage — 乗算合成
                desc.RenderTarget[1] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = true,
                    SourceBlend = Blend.Zero,
                    DestinationBlend = Blend.InverseSourceColor,   // R チャンネルに (1 - c.a) を掛ける
                    BlendOperation = BlendOperation.Add,
                    SourceBlendAlpha = Blend.Zero,
                    DestinationBlendAlpha = Blend.One,             // アルファチャンネルはそのまま維持（実質無意味）
                    BlendOperationAlpha = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteEnable.Red,  // R チャンネルのみ書き込む
                };
                _blendStateOIT = _d3d.CreateBlendState(desc);
            }

            // ── パス3: 解決パスのブレンド (プレマルチアルファで合成) ──
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
                _blendStateResolve = _d3d.CreateBlendState(desc);
            }

            // ── パス4: FXAA (上書き) ──
            {
                var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
                desc.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = false,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                };
                _blendStateFxaa = _d3d.CreateBlendState(desc);
            }

            // ── デプスステンシルステート ──
            _depthStateOpaque = _d3d.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.Less,
                StencilEnable = false,
            });

            _depthStateOIT = _d3d.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.Zero,   // 書き込みOFF
                DepthFunc = ComparisonFunction.LessEqual,
                StencilEnable = false,
            });

            _depthStateSemiTrans = _d3d.CreateDepthStencilState(new DepthStencilDescription
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

            _depthStateSemiBack = _d3d.CreateDepthStencilState(new DepthStencilDescription
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

            _depthStateSemiFront = _d3d.CreateDepthStencilState(new DepthStencilDescription
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

            _depthStateDisabled = _d3d.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = false,
                DepthWriteMask = DepthWriteMask.Zero,
                StencilEnable = false,
            });

            _rasterizerState = _d3d.CreateRasterizerState(new RasterizerDescription
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

    private unsafe void InitializeGeometry()
    {
        // アイテム描画用クワッド
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
            _vertexBuffer = _d3d.CreateBuffer(
                new BufferDescription(totalBytes, BindFlags.VertexBuffer),
                new SubresourceData((IntPtr)pVerts, totalBytes));
        }

        // 解決パス用フルスクリーンクワッド (NDC: -1..1)
        var resolveVerts = new Vertex[]
        {
            new() { Position = new Vector3(-1f, -1f, 0f), TexCoord = new Vector2(0f, 1f) },
            new() { Position = new Vector3( 1f, -1f, 0f), TexCoord = new Vector2(1f, 1f) },
            new() { Position = new Vector3(-1f,  1f, 0f), TexCoord = new Vector2(0f, 0f) },
            new() { Position = new Vector3( 1f,  1f, 0f), TexCoord = new Vector2(1f, 0f) },
        };
        fixed (Vertex* pVerts = resolveVerts)
        {
            _resolveVertexBuffer = _d3d.CreateBuffer(
                new BufferDescription(totalBytes, BindFlags.VertexBuffer),
                new SubresourceData((IntPtr)pVerts, totalBytes));
        }
    }

    /// <summary>
    /// 設定に応じて OIT または標準モード (Painter's Algorithm) でレンダリング。
    /// </summary>
    public ID3D11Texture2D? Render(List<RenderItem> items, int screenWidth, int screenHeight)
    {
        if (!_initialized || _rtv == null || _dsv == null) return null;
        if (items.Count == 0) return null;

        var mode = D3D11RendererSettings.Default.TransparencyMode;
        return mode == TransparencyMode.Standard
            ? RenderStandard(items, screenWidth, screenHeight)
            : RenderOIT(items, screenWidth, screenHeight);
    }

    /// <summary>
    /// 標準モード。<br/>
    /// 不透明を通常描画後、半透明を前面深度プリパス+前後分離合成で描画する。<br/>
    /// 設定の層数 (2/4/8) で半透明の背面寄与量を切り替える。
    /// </summary>
    private ID3D11Texture2D? RenderStandard(List<RenderItem> items, int screenWidth, int screenHeight)
    {
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
            // ── バッファクリア (最終 RT + depth のみ) ──
            _ctx.ClearRenderTargetView(_rtv!, new Color4(0f, 0f, 0f, 0f));
            _ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1.0f, 0);

            _ctx.RSSetViewport(new Viewport(0, 0, _width, _height, 0f, 1f));
            _ctx.RSSetState(_rasterizerState);
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            _ctx.VSSetConstantBuffer(0, _cbPerObject);
            _ctx.PSSetConstantBuffer(0, _cbPerObject);
            _ctx.PSSetSampler(0, _samplerLinear);

            // ワールド行列を事前計算 + インデックス保持
            var itemsWithWorld = items
                .Where(i => i.Srv != null)
                .Select((item, index) => (item, world: BuildWorldMatrix(item), index))
                .ToList();

            // ═══════════════════════════════════════════
            // パス1: 不透明パス → _rtv 直接描画
            // ═══════════════════════════════════════════
            _ctx.OMSetRenderTargets(_rtv!, _dsv);
            _ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            _ctx.OMSetDepthStencilState(_depthStateOpaque, 0);

            _ctx.IASetInputLayout(_inputLayout);
            _ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            _ctx.VSSetShader(_vs);
            _ctx.PSSetShader(_psOpaque);

            foreach (var (item, world, _) in itemsWithWorld)
            {
                DrawItem(item, world, halfW, halfH, 0.999f, _psOpaque!, _samplerLinear!);
            }

            // ═══════════════════════════════════════════
            // 標準モード半透明の描画順
            //   Zソートは使わず、YMM4 レイヤー順のみを使用
            //   Layer 小 = 奥 (先に描画), Layer 大 = 手前 (後に描画)
            // ═══════════════════════════════════════════
            var sorted = itemsWithWorld
                .OrderBy(x => x.item.Layer)
                .ThenBy(x => x.index)
                .ToList();

            // ═══════════════════════════════════════════
            // パス2: 半透明前面 深度プリパス
            //   depth write ON, stencil=1
            // ═══════════════════════════════════════════
            _ctx.OMSetRenderTargets(_rtv!, _dsv);
            _ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));
            _ctx.OMSetDepthStencilState(_depthStateSemiTrans, 1);
            _ctx.PSSetShader(_psSemiTrans);

            foreach (var (item, world, _) in sorted)
            {
                DrawItem(item, world, halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!);
            }

            // ═══════════════════════════════════════════
            // パス3: 半透明背面 色描画 (層数設定で可変)
            // ═══════════════════════════════════════════
            if (backPassCount > 0)
            {
                _ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
                _ctx.OMSetDepthStencilState(_depthStateSemiBack, 1);

                for (int pass = 0; pass < backPassCount; pass++)
                {
                    float opacityMul = pass == 0 ? 1f : 0.5f;
                    foreach (var (item, world, _) in sorted)
                    {
                        DrawItem(item, world, halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!, opacityMul);
                    }
                }
            }

            // ═══════════════════════════════════════════
            // パス4: 不透明再描画（半透明背面が不透明の後ろから漏れるのを抑制）
            //   不透明同士の前後を維持するため depth を再構築して描画
            // ═══════════════════════════════════════════
            _ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1.0f, 0);
            _ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            _ctx.OMSetDepthStencilState(_depthStateOpaque, 0);
            _ctx.PSSetShader(_psOpaque);

            foreach (var (item, world, _) in itemsWithWorld)
            {
                DrawItem(item, world, halfW, halfH, 0.999f, _psOpaque!, _samplerLinear!, 1f);
            }

            // ═══════════════════════════════════════════
            // パス5: 半透明前面 深度プリパス（パス4で再構築した depth 基準に合わせ直す）
            // ═══════════════════════════════════════════
            _ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));
            _ctx.OMSetDepthStencilState(_depthStateSemiTrans, 1);
            _ctx.PSSetShader(_psSemiTrans);

            foreach (var (item, world, _) in sorted)
            {
                DrawItem(item, world, halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!, 1f);
            }

            // ═══════════════════════════════════════════
            // パス6: 半透明前面 色描画
            //   depth == frontDepth かつ stencil==1
            // ═══════════════════════════════════════════
            _ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            _ctx.PSSetShader(_psSemiTrans);
            _ctx.OMSetDepthStencilState(_depthStateSemiFront, 1);

            foreach (var (item, world, _) in sorted)
            {
                DrawItem(item, world, halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!, 1f);
            }

            _ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
            return _renderTarget;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"RenderStandard エラー: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// OIT モード (Weighted Blended OIT)。<br/>
    /// パス1: 不透明 → FXAA中間バッファ + depth確定<br/>
    /// パス2: 半透明 → OIT蓄積バッファ (ソート不要)<br/>
    /// パス3: OIT解決 → FXAA中間バッファに合成<br/>
    /// パス4: FXAA → 最終RT
    /// </summary>
    private ID3D11Texture2D? RenderOIT(List<RenderItem> items, int screenWidth, int screenHeight)
    {
        if (_accumRtv == null || _revealRtv == null) return null;

        float halfW = screenWidth / 2f;
        float halfH = screenHeight / 2f;

        try
        {
            // ── 全バッファクリア ──
            _ctx.ClearRenderTargetView(_fxaaRtv!, new Color4(0f, 0f, 0f, 0f));
            _ctx.ClearRenderTargetView(_rtv!, new Color4(0f, 0f, 0f, 0f));
            _ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1.0f, 0);
            _ctx.ClearRenderTargetView(_accumRtv, new Color4(0f, 0f, 0f, 0f));
            _ctx.ClearRenderTargetView(_revealRtv, new Color4(1f, 1f, 1f, 1f));

            _ctx.RSSetViewport(new Viewport(0, 0, _width, _height, 0f, 1f));
            _ctx.RSSetState(_rasterizerState);
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            _ctx.VSSetConstantBuffer(0, _cbPerObject);
            _ctx.PSSetConstantBuffer(0, _cbPerObject);
            _ctx.PSSetSampler(0, _samplerPoint);

            var itemsWithWorld = items
                .Where(i => i.Srv != null)
                .Select(item => (item, world: BuildWorldMatrix(item)))
                .ToList();

            // パス1: 不透明パス → FXAA中間バッファ
            _ctx.OMSetRenderTargets(_fxaaRtv!, _dsv);
            _ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            _ctx.OMSetDepthStencilState(_depthStateOpaque, 0);

            _ctx.IASetInputLayout(_inputLayout);
            _ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            _ctx.VSSetShader(_vs);
            _ctx.PSSetShader(_psOpaque);

            foreach (var (item, world) in itemsWithWorld)
            {
                DrawItem(item, world, halfW, halfH, 0.999f, _psOpaque!, _samplerPoint!);
            }

            // パス2: OIT 蓄積パス
            var oitRtvs = new[] { _accumRtv, _revealRtv };
            _ctx.OMSetRenderTargets(oitRtvs, _dsv);
            _ctx.OMSetBlendState(_blendStateOIT, null, unchecked((int)0xFFFFFFFF));
            _ctx.OMSetDepthStencilState(_depthStateOIT, 0);
            _ctx.PSSetShader(_psOIT);

            foreach (var (item, world) in itemsWithWorld)
            {
                DrawItem(item, world, halfW, halfH, 0.004f, _psOIT!, _samplerPoint!);
            }

            // パス3: 解決パス
            _ctx.OMSetRenderTargets(_fxaaRtv!, (ID3D11DepthStencilView?)null);
            _ctx.OMSetBlendState(_blendStateResolve, null, unchecked((int)0xFFFFFFFF));
            _ctx.OMSetDepthStencilState(_depthStateDisabled, 0);

            _ctx.IASetInputLayout(_resolveInputLayout);
            _ctx.IASetVertexBuffer(0, _resolveVertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            _ctx.VSSetShader(_resolveVs);
            _ctx.PSSetShader(_resolvePs);
            _ctx.PSSetSampler(0, _samplerLinear);
            _ctx.PSSetShaderResource(0, _accumSrv);
            _ctx.PSSetShaderResource(1, _revealSrv);

            _ctx.Draw(4, 0);

            _ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);
            _ctx.PSSetShaderResource(1, (ID3D11ShaderResourceView?)null);

            // パス4: FXAA
            _ctx.OMSetRenderTargets(_rtv!, (ID3D11DepthStencilView?)null);
            _ctx.OMSetBlendState(_blendStateFxaa, null, unchecked((int)0xFFFFFFFF));
            _ctx.OMSetDepthStencilState(_depthStateDisabled, 0);

            _ctx.IASetInputLayout(_resolveInputLayout);
            _ctx.IASetVertexBuffer(0, _resolveVertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            _ctx.VSSetShader(_vsFxaa);
            _ctx.PSSetShader(_psFxaa);
            _ctx.PSSetSampler(0, _samplerLinear);
            _ctx.PSSetShaderResource(0, _fxaaSrv);

            var cbFxaa = new CbFxaa
            {
                RcpFrameX = 1.0f / _width,
                RcpFrameY = 1.0f / _height,
            };
            _ctx.UpdateSubresource(ref cbFxaa, _cbFxaa!);
            _ctx.PSSetConstantBuffer(0, _cbFxaa);

            _ctx.Draw(4, 0);

            _ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null);

            _ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
            return _renderTarget;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"RenderOIT エラー: {ex.Message}");
            return null;
        }
    }

    private void DrawItem(RenderItem item, Matrix4x4 world, float halfW, float halfH, float alphaThreshold, ID3D11PixelShader activePs, ID3D11SamplerState activeSampler, float opacityMultiplier = 1f)
    {
        // D3Dエフェクトが設定されている場合、エフェクトのRenderに委譲
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
                    effect.Initialize(_d3d, _ctx);

                    // エフェクト固有パラメータを設定
                    d3dVideoEffect.ConfigureEffect(effect, item.ItemFrame, item.ItemLength, item.Fps);

                    // 共通コンテキストを構築
                    var renderContext = new D3DRenderContext
                    {
                        WorldMatrix = world,
                        TextureWidth = (int)item.PixelWidth,
                        TextureHeight = (int)item.PixelHeight,
                        HalfScreenWidth = halfW,
                        HalfScreenHeight = halfH,
                        Opacity = item.Opacity * opacityMultiplier,
                        AlphaThreshold = alphaThreshold,
                        CameraMatrix = item.DrawDescription.Camera,
                    };

                    effect.Render(_ctx, _d3d, item.Srv, renderContext);

                    // エフェクト描画後、元のパイプライン状態を復元
                    _ctx.IASetInputLayout(_inputLayout);
                    _ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
                    _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                    _ctx.VSSetShader(_vs);
                    _ctx.VSSetConstantBuffer(0, _cbPerObject);
                    _ctx.PSSetConstantBuffer(0, _cbPerObject);
                    _ctx.RSSetState(_rasterizerState);
                    _ctx.PSSetShader(activePs);
                    _ctx.PSSetSampler(0, activeSampler);
                }
                catch (Exception ex)
                {
                    Iyahon_D3D11Renderer_CorePlugin.Log($"D3Dエフェクト描画エラー: {ex.Message}");
                }
                return;
            }
        }

        // 通常の板ポリ描画
        var cb = new CbPerObject
        {
            WorldMatrix = world,
            HalfWidth = halfW,
            HalfHeight = halfH,
            Opacity = item.Opacity * opacityMultiplier,
            AlphaThreshold = alphaThreshold,
        };
        _ctx.UpdateSubresource(ref cb, _cbPerObject!);
        _ctx.PSSetShaderResource(0, item.Srv);
        _ctx.Draw(4, 0);
    }

    /// <summary>
    /// YMM4 の DrawingEffect.cs 118行目と同等のワールド行列を構築する。
    /// </summary>
    private Matrix4x4 BuildWorldMatrix(RenderItem item)
    {
        var desc = item.DrawDescription;
        float d2r = MathF.PI / 180f;

        var S = Matrix4x4.CreateScale(item.PixelWidth, item.PixelHeight, 1f);
        var Toffset = Matrix4x4.CreateTranslation(item.BoundsCenterX, item.BoundsCenterY, 0f);

        float zx = (float)desc.Zoom.X;
        float zy = (float)desc.Zoom.Y;
        if (desc.Invert) zx = -zx;
        var Zoom = Matrix4x4.CreateScale(zx, zy, 1f);

        var Rz = Matrix4x4.CreateRotationZ(d2r * (float)desc.Rotation.Z);
        var Ry = Matrix4x4.CreateRotationY(d2r * -(float)desc.Rotation.Y);
        var Rx = Matrix4x4.CreateRotationX(d2r * -(float)desc.Rotation.X);
        var Tdraw = Matrix4x4.CreateTranslation(desc.Draw);
        var cam = desc.Camera;

        var d2dProj = new Matrix4x4(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, -1f / 1000f,
            0f, 0f, 0f, 1f
        );

        return S * Toffset * Zoom * Rz * Ry * Rx * Tdraw * cam * d2dProj;
    }

    private void DisposeTargets()
    {
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var effect in _allCreatedEffects)
        {
            effect.Dispose();
        }
        _allCreatedEffects.Clear();
        // Clear ConditionalWeakTable entries? They will be cleared by GC.

        DisposeTargets();
        _renderTarget?.Dispose(); _renderTarget = null;
        _vs?.Dispose();
        _psOpaque?.Dispose();
        _psOIT?.Dispose();
        _psSemiTrans?.Dispose();
        _resolveVs?.Dispose();
        _resolvePs?.Dispose();
        _vsFxaa?.Dispose();
        _psFxaa?.Dispose();
        _inputLayout?.Dispose();
        _resolveInputLayout?.Dispose();
        _vertexBuffer?.Dispose();
        _resolveVertexBuffer?.Dispose();
        _cbPerObject?.Dispose();
        _cbFxaa?.Dispose();
        _samplerPoint?.Dispose();
        _samplerLinear?.Dispose();
        _blendStateOpaque?.Dispose();
        _blendStateNoColor?.Dispose();
        _blendStateOIT?.Dispose();
        _blendStateResolve?.Dispose();
        _blendStateFxaa?.Dispose();
        _depthStateOpaque?.Dispose();
        _depthStateOIT?.Dispose();
        _depthStateSemiTrans?.Dispose();
        _depthStateSemiBack?.Dispose();
        _depthStateSemiFront?.Dispose();
        _depthStateDisabled?.Dispose();
        _rasterizerState?.Dispose();
    }
}

internal sealed class RenderItem
{
    public required DrawDescription DrawDescription { get; init; }
    public required ID3D11ShaderResourceView? Srv { get; init; }
    public float PixelWidth { get; init; }
    public float PixelHeight { get; init; }

    /// <summary>バウンディングボックスの中心X (画像空間座標)</summary>
    public float BoundsCenterX { get; init; }
    /// <summary>バウンディングボックスの中心Y (画像空間座標)</summary>
    public float BoundsCenterY { get; init; }

    public float Opacity { get; init; } = 1f;

    /// <summary>YMM4 のレイヤー番号 (Layer小=奥, Layer大=手前)</summary>
    public int Layer { get; init; }

    // ── D3Dエフェクト関連 ──

    /// <summary>D3Dエフェクトの VideoEffect (ID3DVideoEffect 実装)</summary>
    public ID3DVideoEffect? D3DVideoEffect { get; init; }
    public YukkuriMovieMaker.Project.Items.IVideoItem? OriginalItem { get; init; }

    /// <summary>アイテム内の現在フレーム位置</summary>
    public long ItemFrame { get; init; }
    /// <summary>アイテムの長さ（フレーム数）</summary>
    public long ItemLength { get; init; }
    /// <summary>FPS</summary>
    public int Fps { get; init; }
}