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
using Iyahon_D3D11Renderer_Core.D3DEffect.Effects;
using Iyahon_D3D11Renderer_Core.Lighting;
using Iyahon_D3D11Renderer_Core.Model3D;
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
                    try { renderer.Dispose(); } catch { }
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

[StructLayout(LayoutKind.Sequential)]
public struct DofParameters
{
    public bool Enabled;
    public float FocusDistance;
    public float Aperture;
    public float FocusRange;
    public float NearBlurScale;
    public float FarBlurScale;
    public float MaxBlur;
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
        public Matrix4x4 WorldMatrix;    // 64
        public Matrix4x4 ViewProjMatrix; // 64 -> 128
        public Matrix4x4 NormalMatrix;   // 64 -> 192
        public float HalfWidth;          // 4
        public float HalfHeight;         // 4
        public float Opacity;            // 4
        public float AlphaThreshold;     // 4  -> 208
        public Vector4 MaterialParams;   // 16 -> 224 (Metallic, Roughness, Clearcoat, HasOverride)
        public Vector4 TintColor;        // 16 -> 240
        public Vector4 EmissiveColor;    // 16 -> 256
        public Vector4 DetailParams;     // 16 -> 272 bytes (16x)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbFxaa
    {
        public float RcpFrameX;
        public float RcpFrameY;
        public float _pad0;
        public float _pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbSkybox
    {
        public Matrix4x4 InvCameraMatrix; // 64
        public float HalfWidth;           // 4
        public float HalfHeight;          // 4
        public float SkyboxBlur;          // 4
        public float HasEnvMap;           // 4  -> 80
        public float EnvExposure;         // 4
        public float _pad0;               // 4
        public float _pad1;               // 4
        public float _pad2;               // 4  -> 96 bytes (16x)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbDof
    {
        public float FocusDistance;
        public float Aperture;
        public float FocusRange;
        public float NearBlurScale;
        public float FarBlurScale;
        public float MaxBlur;
        public float ScreenWidth;
        public float ScreenHeight;
    }

    private IntPtr _d3dDevicePointer = IntPtr.Zero;
    private IntPtr _d3dContextPointer = IntPtr.Zero;

    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _renderTarget;
    private ID2D1Bitmap1? _renderTargetBitmap;
    private ID3D11Texture2D? _hdrSceneTex;
    private ID3D11RenderTargetView? _hdrSceneRtv;
    private ID3D11ShaderResourceView? _hdrSceneSrv;
    private ID3D11Texture2D? _gbufNormalRoughTex;
    private ID3D11RenderTargetView? _gbufNormalRoughRtv;
    private ID3D11ShaderResourceView? _gbufNormalRoughSrv;
    private ID3D11Texture2D? _gbufSpecMetaTex;
    private ID3D11RenderTargetView? _gbufSpecMetaRtv;
    private ID3D11ShaderResourceView? _gbufSpecMetaSrv;
    private bool _isHdrOutput;
    private bool _fp16RenderTargetUnsupported;
    private readonly PostProcessPipeline _postProcess = new();

    private ID3D11DepthStencilView? _dsv;
    private ID3D11Texture2D? _depthStencil;
    private ID3D11ShaderResourceView? _depthSrv;

    private ID3D11Texture2D? _accumTexture;
    private ID3D11RenderTargetView? _accumRtv;
    private ID3D11ShaderResourceView? _accumSrv;

    private ID3D11Texture2D? _revealTexture;
    private ID3D11RenderTargetView? _revealRtv;
    private ID3D11ShaderResourceView? _revealSrv;

    private ID3D11Texture2D? _fxaaTexture;
    private ID3D11RenderTargetView? _fxaaRtv;
    private ID3D11ShaderResourceView? _fxaaSrv;

    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psOpaque;
    private ID3D11PixelShader? _psOIT;
    private ID3D11PixelShader? _psSemiTrans;
    private ID3D11PixelShader? _psSemiTransDepthOnly;
    private ID3D11PixelShader? _psShadowAlpha;
    private ID3D11VertexShader? _resolveVs;
    private ID3D11PixelShader? _resolvePs;
    private ID3D11VertexShader? _skyboxVs;
    private ID3D11PixelShader? _skyboxPs;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11InputLayout? _resolveInputLayout;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _resolveVertexBuffer;
    private ID3D11Buffer? _cbPerObject;
    private ID3D11Buffer? _cbLighting;
    private ID3D11Buffer? _cbSkybox;
    private ID3D11SamplerState? _samplerPoint;
    private ID3D11SamplerState? _samplerLinear;
    private ID3D11SamplerState? _samplerDetail;
    private ID3D11SamplerState? _samplerShadowCmp;
    private ID3D11Texture2D? _whiteTex;
    private ID3D11ShaderResourceView? _whiteSrv;
    private ID3D11Texture2D? _flatNormalTex;
    private ID3D11ShaderResourceView? _flatNormalSrv;

    // 動的拡張ライト/シャドウバッファ＆ページ式シャドウプール＆ベイクプール
    private readonly ShadowMapPool _shadowPool = new();
    private readonly DynamicStructuredBuffer<GpuLightData> _lightBuffer = new();
    private readonly DynamicStructuredBuffer<GpuShadowEntry> _shadowBuffer = new();
    private readonly BakedTexturePool _bakedTexturePool = new();

    private ID3D11BlendState? _blendStateOpaque;
    private ID3D11BlendState? _blendStateOpaqueMrt;
    private ID3D11BlendState? _blendStateNoColor;
    private ID3D11BlendState? _blendStateOIT;
    private ID3D11BlendState? _blendStateResolve;
    private ID3D11BlendState? _blendStateFxaa;

    private ID3D11PixelShader? _psFxaa;
    private ID3D11PixelShader? _psSsaaResolve;
    private ID3D11VertexShader? _vsFxaa;
    private ID3D11Buffer? _cbFxaa;

    private ID3D11VertexShader? _dofVs;
    private ID3D11PixelShader? _dofPs;
    private ID3D11Buffer? _cbDof;
    private ID3D11Texture2D? _dofTempTexture;
    private ID3D11RenderTargetView? _dofTempRtv;
    private ID3D11ShaderResourceView? _dofTempSrv;

    private ID3D11Texture2D? _refractBgTexture;
    private ID3D11ShaderResourceView? _refractBgSrv;
    private float _refractBgMaxMip;

    private ID3D11DepthStencilState? _depthStateOpaque;
    private ID3D11DepthStencilState? _depthStateOIT;
    private ID3D11DepthStencilState? _depthStateSemiTrans;
    private ID3D11DepthStencilState? _depthStateSemiBack;
    private ID3D11DepthStencilState? _depthStateSemiFront;
    private ID3D11DepthStencilState? _depthStateSkybox;
    private ID3D11DepthStencilState? _depthStateDisabled;

    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11RasterizerState? _rasterizerShadowOrtho;
    private ID3D11RasterizerState? _rasterizerShadowPersp;

    private int _width;
    private int _height;
    private int _renderWidth;
    private int _renderHeight;
    private AntiAliasingMode _aaMode = AntiAliasingMode.Fxaa;
    private bool _initialized;
    private bool _disposed;

    private System.Runtime.CompilerServices.ConditionalWeakTable<YukkuriMovieMaker.Project.Items.IVideoItem, CachedEffect> _d3dEffects = new();
    private ObjModelRenderer? _objModelRenderer;
    private Model3D.Noise3DVolumeRenderer? _noise3DRenderer;
    private D3DEffect.D3DScreenOutlineHelper? _screenOutlineHelper;
    private readonly HashSet<int> _clearedSlices = new();

    public int Width => _width;
    public int Height => _height;
    public int RenderWidth => _renderWidth;
    public int RenderHeight => _renderHeight;
    public bool IsHdrOutput => _isHdrOutput;
    public bool IsHdrMismatch => (HdrInjectorBridge.IsHdrActive && !_fp16RenderTargetUnsupported) != _isHdrOutput;
    public bool IsAaMismatch => _initialized && _aaMode != D3D11RendererSettings.Default.AntiAliasing;
    public IntPtr DevicePointer => _d3dDevicePointer;
    public ID2D1Bitmap1? RenderTargetBitmap => _renderTargetBitmap;
    public BakedTexturePool BakedTexturePool => _bakedTexturePool;

    public DepthSortRenderer()
    {
        DepthSortRendererRegistry.Register(this);
    }

    public ID3DEffect? GetOrCreateEffect(YukkuriMovieMaker.Project.Items.IVideoItem item, string effectId)
    {
        if (_d3dEffects.TryGetValue(item, out var cached))
        {
            cached.LastSeen = DateTime.UtcNow;
            var info = D3DEffectRegistry.GetEffectInfo(effectId);
            if (info != null && cached.Effect.GetType() == info.EffectType)
            {
                return cached.Effect;
            }

            cached.Effect.Dispose();
            _d3dEffects.Remove(item);
            var newEffect = D3DEffectRegistry.CreateEffect(effectId);
            if (newEffect != null)
            {
                _d3dEffects.Add(item, new CachedEffect(newEffect));
            }
            return newEffect;
        }
        else
        {
            var newEffect = D3DEffectRegistry.CreateEffect(effectId);
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
    row_major float4x4 NormalMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float AlphaThreshold;
    float4 MaterialParams;
    float4 TintColor;
    float4 EmissiveColor;
    float4 DetailParams;
};
" + LightingShaderCode.HlslCode + @"

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float3 Norm : TEXCOORD1; float Op : TEXCOORD2; float3 WorldPos : TEXCOORD3; };

Texture2D    gTex          : register(t0);
Texture2D    gNormalMap    : register(t1);
Texture2D    gRoughMetal   : register(t6);
SamplerState gSampler      : register(s0);
SamplerState gDetailSampler: register(s4);

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

    float3 localNorm = float3(0.0, 0.0, 1.0);
    o.Norm = normalize(mul(float4(localNorm, 0.0), NormalMatrix).xyz);
    o.Op = Opacity;
    o.WorldPos = worldPos.xyz;
    return o;
}

float3 ResolveDoubleSidedNormal(PSInput input)
{
    float3 n = normalize(input.Norm);
    float3 toEye = CameraWorldPosAndPageSize.xyz - input.WorldPos;
    if (dot(n, toEye) < 0.0)
    {
        n = -n;
    }
    return n;
}

float4 ApplyQuadLighting(
    float4 c,
    PSInput input,
    out float3 outAlbedoLin,
    out float3 outNormal,
    out float outRough,
    out float outMetal,
    out float outClearcoat)
{
    if (c.a <= 0.0001)
    {
        outAlbedoLin = float3(0.0, 0.0, 0.0);
        outNormal = float3(0.0, 0.0, 1.0);
        outRough = 0.72;
        outMetal = 0.0;
        outClearcoat = 0.0;
        return float4(0.0, 0.0, 0.0, 0.0);
    }
    float a = c.a;
    float3 straightSrgb = c.rgb / a;
    float3 albedoLinear = SrgbToLinearUnclamped(straightSrgb) * TintColor.rgb;
    float3 n = ResolveDoubleSidedNormal(input);
    float metallic = (MaterialParams.w > 0.5) ? MaterialParams.x : 0.0;
    float roughness = (MaterialParams.w > 0.5) ? MaterialParams.y : max(DefaultRoughness, 0.72);
    float clearcoat = (MaterialParams.w > 0.5) ? MaterialParams.z : 0.0;

    float2 dUv = input.UV * max(DetailParams.w, 0.001);
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
    {
        litLinear = CalcSimpleLgtPbrEx(albedoLinear, n, input.WorldPos, metallic, roughness, clearcoat, 0.5);
    }
    else
    {
        litLinear = CalcDynamicLgtPbrEx(albedoLinear, n, input.WorldPos, input.Pos.xy, metallic, roughness, clearcoat);
    }

    if (EmissiveColor.w > 0.0)
    {
        litLinear += EmissiveColor.rgb * EmissiveColor.w;
    }

    outAlbedoLin = albedoLinear;
    outNormal = n;
    outRough = roughness;
    outMetal = metallic;
    outClearcoat = clearcoat;
    return float4(litLinear * a, a);
}

OpaqueMrtOutput PS_Opaque(PSInput input)
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op * TintColor.a;
    clip(c.a - AlphaThreshold);
    float3 albedoLin, n;
    float r, m, cc;
    float4 col = ApplyQuadLighting(c, input, albedoLin, n, r, m, cc);
    return PackOpaqueMrt(col, n, albedoLin, r, m, cc);
}

void PS_ShadowAlpha(PSInput input)
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op * TintColor.a;
    clip(c.a - AlphaThreshold);
}

void PS_SemiTransDepthOnly(PSInput input)
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op * TintColor.a;
    clip(c.a - 0.004);
    clip(0.999 - c.a);
}

struct OITOutput
{
    float4 Accum  : SV_Target0;
    float  Reveal : SV_Target1;
};

OITOutput PS_OIT(PSInput input)
{
    float4 c = gTex.Sample(gSampler, input.UV);
    c *= input.Op * TintColor.a;

    clip(c.a - 0.004);
    clip(0.999 - c.a);

    float3 albedoLin, n;
    float r, m, cc;
    c = ApplyQuadLighting(c, input, albedoLin, n, r, m, cc);

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
    c *= input.Op * TintColor.a;
    clip(c.a - 0.004);
    clip(0.999 - c.a);
    float3 albedoLin, n;
    float r, m, cc;
    return ApplyQuadLighting(c, input, albedoLin, n, r, m, cc);
}
";

    private const string SkyboxShaderSource = @"
cbuffer CbSkybox : register(b0)
{
    row_major float4x4 InvCameraMatrix;
    float HalfWidth;
    float HalfHeight;
    float SkyboxBlur;
    float HasEnvMap;
    float EnvExposure;
    float _sp0;
    float _sp1;
    float _sp2;
};
" + LightingShaderCode.HlslCode + @"

struct VSInput { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

PSInput VS_Skybox(VSInput input)
{
    PSInput o;
    o.Pos = float4(input.Pos.xy, 0.99999, 1.0);
    o.UV = input.UV;
    return o;
}

float4 PS_Skybox(PSInput input) : SV_Target
{
    float3 rayCam = normalize(float3(
        (input.UV.x * 2.0 - 1.0) * HalfWidth,
        (input.UV.y * 2.0 - 1.0) * HalfHeight,
        -1000.0
    ));
    float3 rayWorld = normalize(mul(float4(rayCam, 0.0), InvCameraMatrix).xyz);

    float3 skyLin;
    if (HasEnvMap > 0.5 && CamBasisX.w > 0.5)
    {
        float2 uv = DirToEquirectUV(rayWorld, CamBasisY.w);
        float mip = clamp(SkyboxBlur * CamBasisZ.w, 0.0, CamBasisZ.w);
        skyLin = min(gEnvMap.SampleLevel(gEnvSampler, uv, mip).rgb * max(EnvExposure, 0.0), 256.0);
    }
    else
    {
        skyLin = EvaluateProceduralEnv(rayWorld, SkyboxBlur) * max(EnvExposure, 0.0);
    }
    return float4(skyLin, 1.0);
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

    private const string DofShaderSource = @"
cbuffer CbDof : register(b0)
{
    float FocusDistance;
    float Aperture;
    float FocusRange;
    float NearBlurScale;
    float FarBlurScale;
    float MaxBlur;
    float ScreenWidth;
    float ScreenHeight;
};

Texture2D    colorTex  : register(t0);
Texture2D    depthTex  : register(t1);
SamplerState gSampler  : register(s0);

struct VSInput  { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput  { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

PSInput VS_Dof(VSInput input)
{
    PSInput o;
    o.Pos = float4(input.Pos.xy, 0.5, 1.0);
    o.UV  = input.UV;
    return o;
}

float GetLinearDepth(float d)
{
    if (d >= 0.505) return 100000.0;
    float diff = 0.505 - d;
    if (diff < 1e-6) return 100000.0;
    return 5.0 / diff;
}

float GetBlurAmount(float depth)
{
    float nearBoundary = FocusDistance - FocusRange * 0.5;
    float farBoundary = FocusDistance + FocusRange * 0.5;
    float blur = 0.0;

    if (depth < nearBoundary)
    {
        blur = (nearBoundary - depth) * (Aperture * 0.001) * NearBlurScale;
    }
    else if (depth > farBoundary)
    {
        blur = (depth - farBoundary) * (Aperture * 0.001) * FarBlurScale;
    }

    return min(blur, MaxBlur);
}

float4 PS_Dof(PSInput input) : SV_Target
{
    const float2 poisson[16] = {
        float2(-0.94201624, -0.39906216),
        float2(0.94558609, -0.76890725),
        float2(-0.094184101, -0.9293887),
        float2(0.34495938, 0.2938776),
        float2(-0.91588581, 0.45771432),
        float2(-0.81544232, -0.87912464),
        float2(-0.38208752, 0.27476561),
        float2(0.97484398, 0.7564837),
        float2(0.44323325, -0.97511554),
        float2(0.53742981, -0.4737342),
        float2(-0.26496911, -0.41893023),
        float2(0.79197514, 0.19090188),
        float2(-0.2418884, 0.99706507),
        float2(-0.81272995, 0.612513),
        float2(0.71638662, 0.57612097),
        float2(0.29012752, 0.72926164)
    };

    float4 centerColor = colorTex.Sample(gSampler, input.UV);
    float centerDVal = depthTex.Sample(gSampler, input.UV).r;
    float centerDepth = GetLinearDepth(centerDVal);
    float centerBlur = GetBlurAmount(centerDepth);

    if (centerBlur < 0.1)
    {
        return centerColor;
    }

    float ign = frac(52.9829189 * frac(dot(input.Pos.xy, float2(0.06711056, 0.00583715))));
    float angle = ign * 6.2831853;
    float s, c;
    sincos(angle, s, c);

    float4 colorAcc = centerColor;
    float weightAcc = 1.0;

    float2 texelSize = float2(1.0 / ScreenWidth, 1.0 / ScreenHeight);

    [unroll]
    for (int i = 0; i < 16; i++)
    {
        float2 p = poisson[i];
        float2 rot = float2(p.x * c - p.y * s, p.x * s + p.y * c);
        float2 offset = rot * centerBlur * texelSize;
        float2 sampleUV = input.UV + offset;

        if (sampleUV.x < 0.0 || sampleUV.x > 1.0 || sampleUV.y < 0.0 || sampleUV.y > 1.0)
            continue;

        float4 sampleColor = colorTex.Sample(gSampler, sampleUV);
        float sampleDVal = depthTex.Sample(gSampler, sampleUV).r;
        float sampleDepth = GetLinearDepth(sampleDVal);
        float sampleBlur = GetBlurAmount(sampleDepth);

        float weight = (sampleDepth < centerDepth) ? saturate(sampleBlur) : saturate(centerBlur);
        if (sampleColor.a < 0.001)
            weight *= 0.25;

        colorAcc += sampleColor * weight;
        weightAcc += weight;
    }

    return colorAcc / max(weightAcc, 1e-5);
}
";

    private static void ComputeRenderDimensions(int width, int height, AntiAliasingMode aaMode, out int renderW, out int renderH)
    {
        float scale = aaMode switch
        {
            AntiAliasingMode.Ssaa15x => 1.5f,
            AntiAliasingMode.Ssaa2x => 2.0f,
            _ => 1.0f,
        };
        int maxIn = Math.Max(1, Math.Max(width, height));
        if (maxIn * scale > 4096f)
        {
            scale = MathF.Max(1.0f, 4096f / maxIn);
        }
        renderW = Math.Max(1, (int)MathF.Round(width * scale));
        renderH = Math.Max(1, (int)MathF.Round(height * scale));
    }

    public bool Initialize(IGraphicsDevicesAndContext devices, int width, int height)
    {
        if (devices?.D3D?.Device == null || devices.D3D.DeviceContext == null) return false;

        var currentDevice = devices.D3D.Device;
        var currentContext = devices.D3D.DeviceContext;
        if (currentDevice.NativePointer == IntPtr.Zero || currentContext.NativePointer == IntPtr.Zero) return false;

        if (_d3dDevicePointer == IntPtr.Zero ||
            _d3dDevicePointer != currentDevice.NativePointer ||
            _d3dContextPointer != currentContext.NativePointer)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log("D3D11デバイス変更を検出。リソースを再生成します。");
            DisposeTargets();
            DisposeResources();

            _d3dDevicePointer = currentDevice.NativePointer;
            _d3dContextPointer = currentContext.NativePointer;
            _initialized = false;
        }

        bool wantHdr = HdrInjectorBridge.IsHdrActive && !_fp16RenderTargetUnsupported;
        AntiAliasingMode wantAa = D3D11RendererSettings.Default.AntiAliasing;
        ComputeRenderDimensions(width, height, wantAa, out int renderW, out int renderH);

        if (_initialized && _width == width && _height == height &&
            _renderWidth == renderW && _renderHeight == renderH &&
            _isHdrOutput == wantHdr && _aaMode == wantAa)
            return true;

        DisposeTargets();
        _width = width;
        _height = height;
        _renderWidth = renderW;
        _renderHeight = renderH;
        _aaMode = wantAa;

        try
        {
            if (wantHdr)
            {
                try
                {
                    _renderTarget = currentDevice.CreateTexture2D(new Texture2DDescription
                    {
                        Width = width,
                        Height = height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.R16G16B16A16_Float,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                        MiscFlags = ResourceOptionFlags.Shared,
                    });
                    _rtv = currentDevice.CreateRenderTargetView(_renderTarget);
                    _isHdrOutput = true;
                }
                catch
                {
                    _fp16RenderTargetUnsupported = true;
                    _isHdrOutput = false;
                    wantHdr = false;
                    _rtv?.Dispose();
                    _rtv = null;
                    _renderTarget?.Dispose();
                    _renderTarget = null;
                }
            }

            if (_renderTarget == null)
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
                _isHdrOutput = false;
            }

            _depthStencil = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = renderW,
                Height = renderH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R24G8_Typeless,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
            });
            _dsv = currentDevice.CreateDepthStencilView(_depthStencil, new DepthStencilViewDescription
            {
                Format = Format.D24_UNorm_S8_UInt,
                ViewDimension = DepthStencilViewDimension.Texture2D,
            });
            _depthSrv = currentDevice.CreateShaderResourceView(_depthStencil, new ShaderResourceViewDescription
            {
                Format = Format.R24_UNorm_X8_Typeless,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
            });

            _renderTargetBitmap = D2DD3DBridge.GetD2DBitmapFromD3DTexture(_renderTarget, devices);
            if (_renderTargetBitmap == null && _isHdrOutput)
            {
                Iyahon_D3D11Renderer_CorePlugin.Log("FP16 Shared RenderTarget の D2D ビットマップ化に失敗したため B8G8R8A8_UNorm にフォールバックします。");
                _fp16RenderTargetUnsupported = true;
                _isHdrOutput = false;
                _rtv?.Dispose();
                _rtv = null;
                _renderTarget?.Dispose();
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
                _renderTargetBitmap = D2DD3DBridge.GetD2DBitmapFromD3DTexture(_renderTarget, devices);
            }

            _hdrSceneTex = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = renderW,
                Height = renderH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _hdrSceneRtv = currentDevice.CreateRenderTargetView(_hdrSceneTex);
            _hdrSceneSrv = currentDevice.CreateShaderResourceView(_hdrSceneTex);

            _gbufNormalRoughTex = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = renderW,
                Height = renderH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _gbufNormalRoughRtv = currentDevice.CreateRenderTargetView(_gbufNormalRoughTex);
            _gbufNormalRoughSrv = currentDevice.CreateShaderResourceView(_gbufNormalRoughTex);

            _gbufSpecMetaTex = currentDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = renderW,
                Height = renderH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            _gbufSpecMetaRtv = currentDevice.CreateRenderTargetView(_gbufSpecMetaTex);
            _gbufSpecMetaSrv = currentDevice.CreateShaderResourceView(_gbufSpecMetaTex);

            if (!_initialized)
            {
                if (!InitializeShaders(currentDevice)) return false;
                if (!InitializeStates(currentDevice)) return false;
                InitializeGeometry(currentDevice);
                _postProcess.Initialize(currentDevice);
            }

            _initialized = true;
            Iyahon_D3D11Renderer_CorePlugin.Log($"DepthSortRenderer 初期化完了: {width}x{height} (Render={renderW}x{renderH}, AA={wantAa})");
            return true;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"DepthSortRenderer Initialize エラー: {ex.Message}");
            return false;
        }
    }

    private void EnsureAaTarget(ID3D11Device device, int width, int height)
    {
        var desiredFxaaFormat = _isHdrOutput ? Format.R16G16B16A16_Float : Format.B8G8R8A8_UNorm;
        if (_fxaaTexture != null)
        {
            var desc = _fxaaTexture.Description;
            if (desc.Format == desiredFxaaFormat && desc.Width == width && desc.Height == height)
                return;
            DisposeAaTarget();
        }

        _fxaaTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desiredFxaaFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _fxaaRtv = device.CreateRenderTargetView(_fxaaTexture);
        _fxaaSrv = device.CreateShaderResourceView(_fxaaTexture);
    }

    private void DisposeAaTarget()
    {
        _fxaaRtv?.Dispose(); _fxaaRtv = null;
        _fxaaSrv?.Dispose(); _fxaaSrv = null;
        _fxaaTexture?.Dispose(); _fxaaTexture = null;
    }

    private void EnsureOitTargets(ID3D11Device device, int width, int height)
    {
        if (_accumTexture != null && _revealTexture != null)
        {
            var desc = _accumTexture.Description;
            if (desc.Width == width && desc.Height == height)
                return;
            DisposeOitTargets();
        }

        _accumTexture = device.CreateTexture2D(new Texture2DDescription
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
        _accumRtv = device.CreateRenderTargetView(_accumTexture);
        _accumSrv = device.CreateShaderResourceView(_accumTexture);

        _revealTexture = device.CreateTexture2D(new Texture2DDescription
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
        _revealRtv = device.CreateRenderTargetView(_revealTexture);
        _revealSrv = device.CreateShaderResourceView(_revealTexture);
    }

    private void DisposeOitTargets()
    {
        _accumRtv?.Dispose(); _accumRtv = null;
        _accumSrv?.Dispose(); _accumSrv = null;
        _accumTexture?.Dispose(); _accumTexture = null;
        _revealRtv?.Dispose(); _revealRtv = null;
        _revealSrv?.Dispose(); _revealSrv = null;
        _revealTexture?.Dispose(); _revealTexture = null;
    }

    private void EnsureDofTargets(ID3D11Device device, int width, int height)
    {
        if (_dofTempTexture != null)
        {
            var desc = _dofTempTexture.Description;
            if (desc.Width == width && desc.Height == height)
                return;
            DisposeDofTargets();
        }

        _dofTempTexture = device.CreateTexture2D(new Texture2DDescription
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
        _dofTempRtv = device.CreateRenderTargetView(_dofTempTexture);
        _dofTempSrv = device.CreateShaderResourceView(_dofTempTexture);
    }

    private void DisposeDofTargets()
    {
        _dofTempRtv?.Dispose(); _dofTempRtv = null;
        _dofTempSrv?.Dispose(); _dofTempSrv = null;
        _dofTempTexture?.Dispose(); _dofTempTexture = null;
    }

    private void EnsureRefractBgTarget(ID3D11Device device, int width, int height)
    {
        if (_refractBgTexture != null)
        {
            var desc = _refractBgTexture.Description;
            if (desc.Width == width && desc.Height == height)
                return;
            DisposeRefractBgTarget();
        }

        _refractBgTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 0,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _refractBgSrv = device.CreateShaderResourceView(_refractBgTexture);
        _refractBgMaxMip = MathF.Max(0f, _refractBgTexture.Description.MipLevels - 1);
    }

    private void DisposeRefractBgTarget()
    {
        _refractBgSrv?.Dispose(); _refractBgSrv = null;
        _refractBgTexture?.Dispose(); _refractBgTexture = null;
        _refractBgMaxMip = 0f;
    }

    private bool InitializeShaders(ID3D11Device device)
    {
        try
        {
            var vsBytes = ShaderBytecodeCache.GetOrCompile("Main_VS", MainShaderSource, "VS", "vs_5_0", "inline_shader");
            var psOpaqueBytes = ShaderBytecodeCache.GetOrCompile("Main_PS_Opaque", MainShaderSource, "PS_Opaque", "ps_5_0", "inline_shader");
            var psShadowAlphaBytes = ShaderBytecodeCache.GetOrCompile("Main_PS_ShadowAlpha", MainShaderSource, "PS_ShadowAlpha", "ps_5_0", "inline_shader");
            var psOitBytes = ShaderBytecodeCache.GetOrCompile("Main_PS_OIT", MainShaderSource, "PS_OIT", "ps_5_0", "inline_shader");
            var psSemiBytes = ShaderBytecodeCache.GetOrCompile("Main_PS_Semi", MainShaderSource, "PS_SemiTrans", "ps_5_0", "inline_shader");
            var psSemiDepthBytes = ShaderBytecodeCache.GetOrCompile("Main_PS_SemiDepth", MainShaderSource, "PS_SemiTransDepthOnly", "ps_5_0", "inline_shader");

            var resolveVsBytes = ShaderBytecodeCache.GetOrCompile("Resolve_VS", ResolveShaderSource, "VS_Resolve", "vs_5_0", "inline_resolve");
            var resolvePsBytes = ShaderBytecodeCache.GetOrCompile("Resolve_PS", ResolveShaderSource, "PS_Resolve", "ps_5_0", "inline_resolve");

            var skyVsBytes = ShaderBytecodeCache.GetOrCompile("Skybox_VS", SkyboxShaderSource, "VS_Skybox", "vs_5_0", "inline_skybox");
            var skyPsBytes = ShaderBytecodeCache.GetOrCompile("Skybox_PS", SkyboxShaderSource, "PS_Skybox", "ps_5_0", "inline_skybox");

            var fxaaVsBytes = ShaderBytecodeCache.GetOrCompile("Fxaa_VS", FxaaShaderSource.Source, "VS_Fxaa", "vs_5_0", "inline_fxaa");
            var fxaaPsBytes = ShaderBytecodeCache.GetOrCompile("Fxaa_PS", FxaaShaderSource.Source, "PS_Fxaa", "ps_5_0", "inline_fxaa");
            var ssaaPsBytes = ShaderBytecodeCache.GetOrCompile("Ssaa_PS", FxaaShaderSource.Source, "PS_SsaaResolve", "ps_5_0", "inline_fxaa");

            var dofVsBytes = ShaderBytecodeCache.GetOrCompile("Dof_VS", DofShaderSource, "VS_Dof", "vs_5_0", "inline_dof");
            var dofPsBytes = ShaderBytecodeCache.GetOrCompile("Dof_PS", DofShaderSource, "PS_Dof", "ps_5_0", "inline_dof");

            if (vsBytes == null || psOpaqueBytes == null || psShadowAlphaBytes == null ||
                psOitBytes == null || psSemiBytes == null || psSemiDepthBytes == null ||
                resolveVsBytes == null || resolvePsBytes == null ||
                skyVsBytes == null || skyPsBytes == null ||
                fxaaVsBytes == null || fxaaPsBytes == null || ssaaPsBytes == null ||
                dofVsBytes == null || dofPsBytes == null)
            {
                Iyahon_D3D11Renderer_CorePlugin.Log("シェーダーコンパイルエラー");
                return false;
            }

            _vs = device.CreateVertexShader(vsBytes);
            var inputElements = new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,    12, 0),
            };
            _inputLayout = device.CreateInputLayout(inputElements, vsBytes);

            _psOpaque = device.CreatePixelShader(psOpaqueBytes);
            _psShadowAlpha = device.CreatePixelShader(psShadowAlphaBytes);
            _psOIT = device.CreatePixelShader(psOitBytes);
            _psSemiTrans = device.CreatePixelShader(psSemiBytes);
            _psSemiTransDepthOnly = device.CreatePixelShader(psSemiDepthBytes);

            _resolveVs = device.CreateVertexShader(resolveVsBytes);
            _resolveInputLayout = device.CreateInputLayout(inputElements, resolveVsBytes);
            _resolvePs = device.CreatePixelShader(resolvePsBytes);

            _skyboxVs = device.CreateVertexShader(skyVsBytes);
            _skyboxPs = device.CreatePixelShader(skyPsBytes);

            _vsFxaa = device.CreateVertexShader(fxaaVsBytes);
            _psFxaa = device.CreatePixelShader(fxaaPsBytes);
            _psSsaaResolve = device.CreatePixelShader(ssaaPsBytes);

            _dofVs = device.CreateVertexShader(dofVsBytes);
            _dofPs = device.CreatePixelShader(dofPsBytes);

            _cbPerObject = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbPerObject>(), BindFlags.ConstantBuffer));
            _cbFxaa = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbFxaa>(), BindFlags.ConstantBuffer));
            _cbLighting = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbLightingHeader>(), BindFlags.ConstantBuffer));
            _cbSkybox = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbSkybox>(), BindFlags.ConstantBuffer));
            _cbDof = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbDof>(), BindFlags.ConstantBuffer));

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

            _samplerDetail = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                ComparisonFunction = ComparisonFunction.Always,
                MaxLOD = float.MaxValue,
            });

            _samplerShadowCmp = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.ComparisonMinMagLinearMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = ComparisonFunction.LessEqual,
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
                var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = true };
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
                desc.RenderTarget[1] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = false,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                };
                desc.RenderTarget[2] = new RenderTargetBlendDescription
                {
                    IsBlendEnabled = false,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                };
                _blendStateOpaqueMrt = device.CreateBlendState(desc);
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

            _depthStateSkybox = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.LessEqual,
                StencilEnable = false,
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

            _rasterizerShadowOrtho = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                DepthClipEnable = false,
                ScissorEnable = true,
                SlopeScaledDepthBias = 1.5f,
            });

            _rasterizerShadowPersp = device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                DepthClipEnable = true,
                ScissorEnable = true,
                SlopeScaledDepthBias = 1.5f,
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

        uint white = 0xFFFFFFFF;
        _whiteTex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        }, new SubresourceData[] { new SubresourceData((IntPtr)(&white), 4) });
        _whiteSrv = device.CreateShaderResourceView(_whiteTex);

        uint flatNormal = 0xFF8080FF;
        _flatNormalTex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        }, new SubresourceData[] { new SubresourceData((IntPtr)(&flatNormal), 4) });
        _flatNormalSrv = device.CreateShaderResourceView(_flatNormalTex);
    }

    public ID3D11Texture2D? Render(
        IGraphicsDevicesAndContext devices,
        List<RenderItem> items,
        IReadOnlyList<LightData> activeLights,
        int screenWidth,
        int screenHeight,
        DofParameters dofParams)
    {
        return Render(devices, items, activeLights, screenWidth, screenHeight, dofParams, default, default);
    }

    public ID3D11Texture2D? Render(
        IGraphicsDevicesAndContext devices,
        List<RenderItem> items,
        IReadOnlyList<LightData> activeLights,
        int screenWidth,
        int screenHeight,
        DofParameters dofParams,
        AtmosphereParameters atmosphere)
    {
        return Render(devices, items, activeLights, screenWidth, screenHeight, dofParams, atmosphere, default);
    }

    public ID3D11Texture2D? Render(
        IGraphicsDevicesAndContext devices,
        List<RenderItem> items,
        IReadOnlyList<LightData> activeLights,
        int screenWidth,
        int screenHeight,
        DofParameters dofParams,
        AtmosphereParameters atmosphere,
        EnvironmentParameters environment)
    {
        if (!_initialized || _rtv == null || _dsv == null) return null;
        bool allowEmptyRender = environment.OverrideEnvironmentMap && environment.ShowSkybox;
        if (items.Count == 0 && !allowEmptyRender) return null;

        PruneEffects();

        var d3d = devices.D3D.Device;
        var ctx = devices.D3D.DeviceContext;
        var settings = D3D11RendererSettings.Default;

        // 1. 全アイテムの行列・法線行列・ワールドAABB/球・エフェクトを1回だけ事前準備
        var preparedItems = PrepareRenderItems(d3d, ctx, items);
        if (preparedItems.Count == 0 && !allowEmptyRender) return null;

        List<PreparedRenderItem>? filteredItems = null;
        for (int i = 0; i < preparedItems.Count; i++)
        {
            var ol = preparedItems[i].Item.Outline;
            if (ol.Enabled && ol.OutlineOnly)
            {
                filteredItems = new List<PreparedRenderItem>(preparedItems.Count);
                for (int j = 0; j < preparedItems.Count; j++)
                {
                    var o = preparedItems[j].Item.Outline;
                    if (!(o.Enabled && o.OutlineOnly))
                        filteredItems.Add(preparedItems[j]);
                }
                break;
            }
        }
        var mainPassItems = filteredItems ?? preparedItems;

        Matrix4x4 cameraMatrix = preparedItems.Count > 0
            ? preparedItems[0].Item.DrawDescription.Camera
            : (environment.FallbackCameraMatrix.M44 != 0f ? environment.FallbackCameraMatrix : Matrix4x4.Identity);

        // 2. 光源＆シャドウのフレーム計画を1回だけ構築
        var plan = LightManager.BuildFramePlan(activeLights, mainPassItems, screenWidth, screenHeight, in environment, in cameraMatrix);

        // 3. 深度専用シャドウパス描画
        ctx.PSSetShaderResource(2, null!);
        if (settings.EnableShadow && plan.ShadowJobs.Count > 0)
        {
            RenderShadowPass(d3d, ctx, mainPassItems, plan);
        }
        else if (!settings.EnableShadow)
        {
            _shadowPool.ReleaseAll();
        }
        else
        {
            _shadowPool.OnFrameUnused();
        }

        // 4. ライティング定数バッファ & StructuredBuffer & 環境マップバインド
        bool hasEnvMap = UpdateAndBindLighting(d3d, ctx, plan, in environment);

        // 5. メイン描画 (Linear HDR シーン + G-Buffer ターゲットへ描画: Standard or OIT)
        if (_aaMode != AntiAliasingMode.Off)
        {
            EnsureAaTarget(d3d, _renderWidth, _renderHeight);
        }
        else
        {
            DisposeAaTarget();
        }

        bool isStandardMode = settings.TransparencyMode == TransparencyMode.Standard;
        ID3D11Texture2D? hdrResult;
        if (isStandardMode)
        {
            DisposeOitTargets();
            hdrResult = RenderStandard(d3d, ctx, mainPassItems, screenWidth, screenHeight, cameraMatrix, hasEnvMap, in environment);
        }
        else
        {
            EnsureOitTargets(d3d, _renderWidth, _renderHeight);
            hdrResult = RenderOIT(d3d, ctx, mainPassItems, screenWidth, screenHeight, cameraMatrix, hasEnvMap, in environment);
        }

        if (hdrResult != null && _hdrSceneRtv != null)
        {
            var camWorldPos = new Vector3(
                plan.Header.CameraWorldPosAndPageSize.X,
                plan.Header.CameraWorldPosAndPageSize.Y,
                plan.Header.CameraWorldPosAndPageSize.Z);
            RenderPostSceneEffectsAndOutlines(
                d3d, ctx, preparedItems, camWorldPos, screenWidth, screenHeight,
                alreadyRenderedOpaqueInStandard: isStandardMode);
        }

        UnbindLightingAndShadowResources(ctx);

        if (hdrResult == null || _hdrSceneRtv == null || _hdrSceneSrv == null)
        {
            return null;
        }

        int vertStride = Marshal.SizeOf<Vertex>();

        // 6. Half-Res GTAO / SSGI / SSR + Contact Shadows + Fog + GodRays
        var (activeHdrTex, activeHdrRtv, activeHdrSrv) = _postProcess.ApplyPostOpaque(
            d3d, ctx,
            hdrResult, _hdrSceneRtv, _hdrSceneSrv,
            _depthSrv!, _gbufNormalRoughSrv, _gbufSpecMetaSrv,
            _resolveInputLayout!, _resolveVertexBuffer!, vertStride,
            _samplerLinear!, _samplerPoint!,
            _blendStateFxaa!, _depthStateDisabled!, _rasterizerState!,
            _renderWidth, _renderHeight, screenWidth, screenHeight,
            in cameraMatrix, activeLights, in atmosphere);

        // 7. DoF ポストプロセス (Linear HDR 空間で実行)
        if (dofParams.Enabled && _dofVs != null && _dofPs != null)
        {
            EnsureDofTargets(d3d, _renderWidth, _renderHeight);
            if (_dofTempTexture != null)
            {
                ApplyDepthOfField(ctx, activeHdrTex, activeHdrRtv, screenWidth, screenHeight, dofParams);
            }
        }
        else
        {
            DisposeDofTargets();
        }

        // 8. 5-Mip HDR Bloom + Tone Mapping (+ HDRInjector 用 Unclamped sRGB 出力)
        bool useAaPass = (_aaMode != AntiAliasingMode.Off) && _fxaaRtv != null && _fxaaSrv != null;
        var postTargetRtv = useAaPass ? _fxaaRtv! : _rtv!;
        _postProcess.ApplyBloomAndToneMap(
            d3d, ctx,
            activeHdrSrv, postTargetRtv,
            _resolveInputLayout!, _resolveVertexBuffer!, vertStride,
            _samplerLinear!, _samplerPoint!,
            _blendStateFxaa!, _depthStateDisabled!, _rasterizerState!,
            _renderWidth, _renderHeight, _isHdrOutput);

        // 9. AA仕上げ / SSAAダウンサンプル解像度変換パス（Standard / OIT 共通）
        if (useAaPass)
        {
            RunAaPass(ctx);
        }

        return _renderTarget;
    }

    private List<PreparedRenderItem> PrepareRenderItems(ID3D11Device d3d, ID3D11DeviceContext ctx, List<RenderItem> items)
    {
        var prepared = new List<PreparedRenderItem>(items.Count);

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.ObjModels == null && item.Srv == null) continue;

            Matrix4x4 viewProj = BuildViewProjMatrix(item);

            if (item.ObjModels != null)
            {
                int modelCount = item.ObjModels.Count;
                var objWorlds = new Matrix4x4[modelCount];
                var objNormals = new Matrix4x4[modelCount];

                Vector3 unionMin = new Vector3(float.MaxValue);
                Vector3 unionMax = new Vector3(float.MinValue);

                var desc = item.DrawDescription;
                float zx = MathF.Abs((float)desc.Zoom.X);
                float zy = MathF.Abs((float)desc.Zoom.Y);
                float zScale = (zx + zy) * 0.5f;
                float maxScale = MathF.Max(MathF.Max(zx, zy), zScale);
                float objRadius = MathF.Max(200f, 380f * maxScale);

                for (int m = 0; m < modelCount; m++)
                {
                    var model = item.ObjModels[m];
                    var w = BuildObjModelMatrix(item, model);
                    objWorlds[m] = w;
                    objNormals[m] = MatrixTransformHelper.ComputeNormalMatrix(w);

                    if (Vector3.DistanceSquared(model.BoundsMax, model.BoundsMin) > 1e-6f)
                    {
                        var bMin = model.BoundsMin;
                        var bMax = model.BoundsMax;
                        for (int c = 0; c < 8; c++)
                        {
                            var corner = new Vector3(
                                (c & 1) == 0 ? bMin.X : bMax.X,
                                (c & 2) == 0 ? bMin.Y : bMax.Y,
                                (c & 4) == 0 ? bMin.Z : bMax.Z);
                            var wc = Vector3.Transform(corner, w);
                            unionMin = Vector3.Min(unionMin, wc);
                            unionMax = Vector3.Max(unionMax, wc);
                        }
                    }
                    else
                    {
                        Vector3 center = w.Translation;
                        Vector3 ext = new Vector3(objRadius);
                        unionMin = Vector3.Min(unionMin, center - ext);
                        unionMax = Vector3.Max(unionMax, center + ext);
                    }
                }

                var bounds = new WorldBounds
                {
                    Min = unionMin,
                    Max = unionMax,
                    Sphere = new BoundingSphere3D((unionMin + unionMax) * 0.5f, Vector3.Distance(unionMin, unionMax) * 0.5f),
                };

                prepared.Add(new PreparedRenderItem
                {
                    Item = item,
                    WorldMatrix = objWorlds.Length > 0 ? objWorlds[0] : Matrix4x4.Identity,
                    NormalMatrix = objNormals.Length > 0 ? objNormals[0] : Matrix4x4.Identity,
                    ViewProjMatrix = viewProj,
                    ObjWorldMatrices = objWorlds,
                    ObjNormalMatrices = objNormals,
                    Bounds = bounds,
                    OriginalIndex = i,
                    IsOitCapable = true,
                });
                continue;
            }

            Matrix4x4 world = BuildModelMatrix(item);
            Matrix4x4 normalMat = MatrixTransformHelper.ComputeNormalMatrix(world);

            ID3DEffect? configuredEffect = null;
            bool isOitCapable = true;

            if (item.D3DVideoEffect != null && item.OriginalItem != null)
            {
                var effect = GetOrCreateEffect(item.OriginalItem, item.D3DVideoEffect.D3DEffectId);
                if (effect != null)
                {
                    try
                    {
                        effect.Initialize(d3d, ctx);
                        item.D3DVideoEffect.ConfigureEffect(effect, item.ItemFrame, item.ItemLength, item.Fps);
                        configuredEffect = effect;
                        isOitCapable = effect is IOitCapableD3DEffect;
                    }
                    catch (Exception ex)
                    {
                        Iyahon_D3D11Renderer_CorePlugin.Log($"D3Dエフェクト準備エラー: {ex.Message}");
                    }
                }
            }

            WorldBounds itemBounds = ComputeEffectOrQuadBounds(item, world, configuredEffect);

            prepared.Add(new PreparedRenderItem
            {
                Item = item,
                WorldMatrix = world,
                NormalMatrix = normalMat,
                ViewProjMatrix = viewProj,
                ConfiguredEffect = configuredEffect,
                Bounds = itemBounds,
                OriginalIndex = i,
                IsOitCapable = isOitCapable,
            });
        }

        return prepared;
    }

    private static WorldBounds ComputeEffectOrQuadBounds(RenderItem item, in Matrix4x4 world, ID3DEffect? effect)
    {
        float halfZ = 1f;
        float halfXY = 0.5f;

        if (effect is CubeD3DEffect cube)
        {
            halfZ = MathF.Max(1f, MathF.Abs(cube.DepthScale) * 0.5f);
        }
        else if (effect is SphereD3DEffect sphere)
        {
            float r = MathF.Min(item.PixelWidth, item.PixelHeight);
            halfZ = MathF.Max(1f, r * MathF.Abs(sphere.DepthScale) * 0.5f);
        }
        else if (effect is ExtrusionD3DEffect extrusion)
        {
            halfZ = MathF.Max(1f, MathF.Abs(extrusion.Thickness) * 0.5f);
        }
        else if (effect != null)
        {
            halfXY = 1.0f;
            halfZ = MathF.Max(item.PixelWidth, item.PixelHeight);
        }

        Vector3 min = new Vector3(float.MaxValue);
        Vector3 max = new Vector3(float.MinValue);

        Span<Vector3> localCorners = stackalloc Vector3[8]
        {
            new(-halfXY, -halfXY, -halfZ), new( halfXY, -halfXY, -halfZ),
            new(-halfXY,  halfXY, -halfZ), new( halfXY,  halfXY, -halfZ),
            new(-halfXY, -halfXY,  halfZ), new( halfXY, -halfXY,  halfZ),
            new(-halfXY,  halfXY,  halfZ), new( halfXY,  halfXY,  halfZ),
        };

        for (int k = 0; k < 8; k++)
        {
            Vector3 wPos = Vector3.Transform(localCorners[k], world);
            min = Vector3.Min(min, wPos);
            max = Vector3.Max(max, wPos);
        }

        return new WorldBounds
        {
            Min = min,
            Max = max,
            Sphere = new BoundingSphere3D((min + max) * 0.5f, Vector3.Distance(min, max) * 0.5f),
        };
    }

    private void RenderShadowPass(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        List<PreparedRenderItem> items,
        LightingFramePlan plan)
    {
        _shadowPool.EnsureAllocated(d3d, plan.PageSize, plan.RequiredPages);
        var dsvs = _shadowPool.SliceDsvs;
        if (dsvs == null) return;

        _clearedSlices.Clear();
        for (int i = 0; i < plan.ShadowJobs.Count; i++)
        {
            int slice = plan.ShadowJobs[i].Tile.SliceIndex;
            if (slice >= 0 && slice < dsvs.Length && _clearedSlices.Add(slice))
            {
                ctx.ClearDepthStencilView(dsvs[slice], DepthStencilClearFlags.Depth, 1.0f, 0);
            }
        }

        ctx.OMSetDepthStencilState(_depthStateOpaque, 0);
        ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));

        int currentSlice = -1;
        bool? currentOrtho = null;

        for (int j = 0; j < plan.ShadowJobs.Count; j++)
        {
            var job = plan.ShadowJobs[j];
            int slice = job.Tile.SliceIndex;
            if (slice < 0 || slice >= dsvs.Length) continue;

            if (slice != currentSlice)
            {
                ctx.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), dsvs[slice]);
                currentSlice = slice;
            }

            var jobRs = job.IsOrthographic ? _rasterizerShadowOrtho : _rasterizerShadowPersp;
            if (currentOrtho != job.IsOrthographic)
            {
                ctx.RSSetState(jobRs);
                currentOrtho = job.IsOrthographic;
            }

            ctx.RSSetViewport(new Viewport(job.Tile.PixelX, job.Tile.PixelY, job.Tile.TileRes, job.Tile.TileRes, 0f, 1f));
            ctx.RSSetScissorRect(job.Tile.PixelX, job.Tile.PixelY, job.Tile.TileRes, job.Tile.TileRes);

            DrawShadowJobItems(d3d, ctx, items, job, jobRs!);
        }

        ctx.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), null);
    }

    private void DrawShadowJobItems(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        List<PreparedRenderItem> items,
        ShadowRenderJob job,
        ID3D11RasterizerState jobRs)
    {
        bool standardPipelineBound = false;

        for (int i = 0; i < items.Count; i++)
        {
            var prep = items[i];
            if (!job.Frustum.IntersectsSphere(prep.Bounds.Sphere, skipNearPlane: job.IsOrthographic))
                continue;

            var item = prep.Item;
            if (item.ObjModels != null)
            {
                try
                {
                    if (_objModelRenderer == null) _objModelRenderer = new ObjModelRenderer();
                    _objModelRenderer.Initialize(d3d, ctx);

                    var mat = item.Material;
                    for (int m = 0; m < item.ObjModels.Count; m++)
                    {
                        _objModelRenderer.RenderModel(
                            d3d, ctx, item.ObjModels[m],
                            prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m], job.LightViewProj,
                            0f, 0f, item.Opacity, 0.5f, ObjRenderMode.Shadow,
                            in mat, null, null);
                    }
                    standardPipelineBound = false;
                }
                catch (Exception ex)
                {
                    Iyahon_D3D11Renderer_CorePlugin.Log($"OBJモデル影描画エラー: {ex.Message}");
                }
            }
            else if (prep.ConfiguredEffect != null && item.Srv != null)
            {
                try
                {
                    var renderContext = new D3DRenderContext
                    {
                        WorldMatrix = prep.WorldMatrix,
                        NormalMatrix = prep.NormalMatrix,
                        ViewProjectionMatrix = job.LightViewProj,
                        CameraWorldPosition = job.ShadowCameraPos,
                        TextureWidth = (int)item.PixelWidth,
                        TextureHeight = (int)item.PixelHeight,
                        HalfScreenWidth = 0f,
                        HalfScreenHeight = 0f,
                        Opacity = item.Opacity,
                        AlphaThreshold = 0.5f,
                        CameraMatrix = Matrix4x4.Identity,
                        IsShadowPass = true,
                        IsDepthOnlyPass = true,
                        Material = item.Material,
                    };

                    prep.ConfiguredEffect.Render(ctx, d3d, item.Srv, renderContext);
                    ctx.RSSetState(jobRs);
                    ctx.RSSetScissorRect(job.Tile.PixelX, job.Tile.PixelY, job.Tile.TileRes, job.Tile.TileRes);
                    standardPipelineBound = false;
                }
                catch (Exception ex)
                {
                    Iyahon_D3D11Renderer_CorePlugin.Log($"D3Dエフェクト影描画エラー: {ex.Message}");
                }
            }
            else if (item.Srv != null)
            {
                if (!standardPipelineBound)
                {
                    ctx.IASetInputLayout(_inputLayout);
                    ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
                    ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                    ctx.VSSetShader(_vs);
                    ctx.VSSetConstantBuffer(0, _cbPerObject);
                    ctx.PSSetConstantBuffer(0, _cbPerObject);
                    ctx.PSSetSampler(0, _samplerLinear);
                    ctx.PSSetShader(_psShadowAlpha);
                    standardPipelineBound = true;
                }

                var cb = new CbPerObject
                {
                    WorldMatrix = prep.WorldMatrix,
                    ViewProjMatrix = job.LightViewProj,
                    NormalMatrix = prep.NormalMatrix,
                    HalfWidth = 0f,
                    HalfHeight = 0f,
                    Opacity = item.Opacity,
                    AlphaThreshold = 0.5f,
                    TintColor = item.Material.Enabled ? item.Material.TintColor : Vector4.One,
                };
                ctx.UpdateSubresource(in cb, _cbPerObject!);
                ctx.PSSetShaderResource(0, item.Srv!);
                ctx.Draw(4, 0);
            }
        }
    }

    private bool UpdateAndBindLighting(ID3D11Device d3d, ID3D11DeviceContext ctx, LightingFramePlan plan, in EnvironmentParameters environment)
    {
        var settings = D3D11RendererSettings.Default;
        var header = plan.Header;

        string? envPath = environment.OverrideEnvironmentMap ? environment.EnvironmentMapPath : settings.EnvironmentMapPath;
        float envRotDeg = environment.OverrideEnvironmentMap ? environment.RotationDeg : (float)settings.EnvironmentRotation;
        float envExposure = environment.OverrideEnvironmentMap ? environment.Exposure : (float)settings.EnvironmentExposure;
        float envRefl = environment.OverrideEnvironmentMap
            ? (environment.ReflectionIntensity ?? 1.0f)
            : (float)settings.EnvironmentReflectionIntensity;

        bool hasEnvMap = EnvironmentMapManager.TryGetEnvMap(
            d3d, ctx, envPath, out var envSrv, out float envMaxMip);
        var envSampler = EnvironmentMapManager.GetOrCreateEnvSampler(d3d);

        header.CamBasisX.W = hasEnvMap ? 1f : 0f;
        header.CamBasisY.W = envRotDeg * (MathF.PI / 180f);
        header.CamBasisZ.W = hasEnvMap ? envMaxMip : 0f;
        header.EnvParams = new Vector4(envExposure, MathF.Max(envRefl, 0f), 1f, 0f);

        ctx.UpdateSubresource(in header, _cbLighting!);
        ctx.VSSetConstantBuffer(1, _cbLighting);
        ctx.PSSetConstantBuffer(1, _cbLighting);

        _lightBuffer.Update(d3d, ctx, plan.Lights.AsSpan());
        _shadowBuffer.Update(d3d, ctx, plan.ShadowEntries.AsSpan());

        if (_lightBuffer.ShaderResourceView != null)
            ctx.PSSetShaderResource(3, _lightBuffer.ShaderResourceView);
        else
            ctx.PSSetShaderResource(3, null!);

        if (_shadowBuffer.ShaderResourceView != null)
            ctx.PSSetShaderResource(4, _shadowBuffer.ShaderResourceView);
        else
            ctx.PSSetShaderResource(4, null!);

        if (header.EnableShadow > 0.5f && _shadowPool.ArraySrv != null)
        {
            ctx.PSSetShaderResource(2, _shadowPool.ArraySrv);
            ctx.PSSetSampler(1, _samplerShadowCmp);
            ctx.PSSetSampler(2, _samplerPoint);
        }
        else
        {
            ctx.PSSetShaderResource(2, null!);
        }

        ctx.PSSetShaderResource(5, hasEnvMap ? envSrv! : null!);
        if (envSampler != null)
            ctx.PSSetSampler(3, envSampler);

        return hasEnvMap;
    }

    private void UnbindLightingAndShadowResources(ID3D11DeviceContext ctx)
    {
        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
        ctx.PSSetShaderResource(2, null!);
        ctx.PSSetShaderResource(3, null!);
        ctx.PSSetShaderResource(4, null!);
        ctx.PSSetShaderResource(5, null!);
        ctx.PSSetShaderResource(6, null!);
        ctx.PSSetShaderResource(7, null!);
        ctx.PSSetShaderResource(8, null!);
        ctx.PSSetShaderResource(9, null!);
        ctx.PSSetSampler(1, null!);
        ctx.PSSetSampler(2, null!);
        ctx.PSSetSampler(3, null!);
        ctx.PSSetSampler(4, null!);
    }

    private void RenderSkyboxPass(
        ID3D11DeviceContext ctx,
        in Matrix4x4 cameraMatrix,
        int screenWidth,
        int screenHeight,
        bool hasEnvMap,
        in EnvironmentParameters environment)
    {
        if (_skyboxVs == null || _skyboxPs == null || _cbSkybox == null) return;
        var settings = D3D11RendererSettings.Default;
        bool showSkybox = environment.OverrideEnvironmentMap ? environment.ShowSkybox : settings.ShowSkybox;
        if (!showSkybox) return;

        float skyboxBlur = environment.OverrideEnvironmentMap ? environment.SkyboxBlur : (float)settings.SkyboxBlur;
        float envExposure = environment.OverrideEnvironmentMap ? environment.Exposure : (float)settings.EnvironmentExposure;

        if (!Matrix4x4.Invert(cameraMatrix, out var invCam))
            invCam = Matrix4x4.Identity;

        var cb = new CbSkybox
        {
            InvCameraMatrix = invCam,
            HalfWidth = screenWidth * 0.5f,
            HalfHeight = screenHeight * 0.5f,
            SkyboxBlur = skyboxBlur,
            HasEnvMap = hasEnvMap ? 1f : 0f,
            EnvExposure = envExposure,
        };
        ctx.UpdateSubresource(in cb, _cbSkybox);

        ctx.OMSetBlendState(_blendStateFxaa, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_depthStateSkybox, 0);
        ctx.IASetInputLayout(_resolveInputLayout);
        ctx.IASetVertexBuffer(0, _resolveVertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(_skyboxVs);
        ctx.PSSetShader(_skyboxPs);
        ctx.PSSetConstantBuffer(0, _cbSkybox);
        ctx.Draw(4, 0);

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
        ctx.VSSetShader(_vs);
        ctx.PSSetConstantBuffer(0, _cbPerObject);
    }

    private ID3D11Texture2D? RenderStandard(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        List<PreparedRenderItem> items,
        int screenWidth,
        int screenHeight,
        in Matrix4x4 cameraMatrix,
        bool hasEnvMap,
        in EnvironmentParameters environment)
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
            ctx.ClearRenderTargetView(_hdrSceneRtv!, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearRenderTargetView(_gbufNormalRoughRtv!, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearRenderTargetView(_gbufSpecMetaRtv!, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1.0f, 0);

            ctx.RSSetViewport(new Viewport(0, 0, _renderWidth, _renderHeight, 0f, 1f));
            ctx.RSSetState(_rasterizerState);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            ctx.VSSetConstantBuffer(0, _cbPerObject);
            ctx.PSSetConstantBuffer(0, _cbPerObject);
            ctx.PSSetSampler(0, _samplerLinear);
            ctx.PSSetSampler(4, _samplerDetail);

            var sorted = items
                .OrderBy(x => x.Item.Layer)
                .ThenBy(x => x.OriginalIndex)
                .ToList();

            // 1. 不透明パス + G-Buffer MRT出力
            var mrtRtvs = new[] { _hdrSceneRtv!, _gbufNormalRoughRtv!, _gbufSpecMetaRtv! };
            ctx.OMSetRenderTargets(mrtRtvs, _dsv);
            ctx.OMSetBlendState(_blendStateOpaqueMrt, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateOpaque, 0);
            ctx.IASetInputLayout(_inputLayout);
            ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_psOpaque);

            for (int i = 0; i < items.Count; i++)
            {
                DrawItem(d3d, ctx, items[i], halfW, halfH, 0.999f, _psOpaque!, _samplerLinear!,
                    isOitPass: false, isSemiTransPass: false, isDepthOnlyPass: false);
            }

            ctx.OMSetRenderTargets(_hdrSceneRtv!, _dsv);
            RenderOpaqueNoise3DAndHulls(d3d, ctx, items, ComputeCameraWorldPos(in cameraMatrix), halfW, halfH, includeOpaqueNoise3D: true, includeNormalHulls: true, includeOutlineOnlyHulls: false);
            ctx.OMSetRenderTargets(_hdrSceneRtv!, _dsv);
            bool showSkybox = environment.OverrideEnvironmentMap
                ? environment.ShowSkybox
                : D3D11RendererSettings.Default.ShowSkybox;
            RenderTransmissiveModelsPass(d3d, ctx, items, halfW, halfH, showSkybox);
            RenderSkyboxPass(ctx, in cameraMatrix, screenWidth, screenHeight, hasEnvMap, in environment);

            if (backPassCount > 0)
            {
                ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));
                ctx.OMSetDepthStencilState(_depthStateSemiTrans, 1);
                ctx.PSSetShader(_psSemiTransDepthOnly);

                for (int i = 0; i < sorted.Count; i++)
                {
                    DrawItem(d3d, ctx, sorted[i], halfW, halfH, 0.004f, _psSemiTransDepthOnly!, _samplerLinear!,
                        isOitPass: false, isSemiTransPass: true, isDepthOnlyPass: true);
                }

                ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
                ctx.OMSetDepthStencilState(_depthStateSemiBack, 1);
                ctx.PSSetShader(_psSemiTrans);

                for (int pass = 0; pass < backPassCount; pass++)
                {
                    float opacityMul = pass == 0 ? 1f : 0.5f;
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        DrawItem(d3d, ctx, sorted[i], halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!,
                            isOitPass: false, isSemiTransPass: true, isDepthOnlyPass: false, opacityMultiplier: opacityMul);
                    }
                }

                ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1.0f, 0);
                ctx.OMSetRenderTargets(mrtRtvs, _dsv);
                ctx.OMSetBlendState(_blendStateOpaqueMrt, null, unchecked((int)0xFFFFFFFF));
                ctx.OMSetDepthStencilState(_depthStateOpaque, 0);
                ctx.PSSetShader(_psOpaque);

                for (int i = 0; i < items.Count; i++)
                {
                    DrawItem(d3d, ctx, items[i], halfW, halfH, 0.999f, _psOpaque!, _samplerLinear!,
                        isOitPass: false, isSemiTransPass: false, isDepthOnlyPass: false);
                }

                ctx.OMSetRenderTargets(_hdrSceneRtv!, _dsv);
                RenderOpaqueNoise3DAndHulls(d3d, ctx, items, ComputeCameraWorldPos(in cameraMatrix), halfW, halfH, includeOpaqueNoise3D: true, includeNormalHulls: true, includeOutlineOnlyHulls: false);
                ctx.OMSetRenderTargets(_hdrSceneRtv!, _dsv);
                RestoreTransmissiveDepthPass(d3d, ctx, items, halfW, halfH);
            }

            ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateSemiTrans, 1);
            ctx.PSSetShader(_psSemiTransDepthOnly);

            for (int i = 0; i < sorted.Count; i++)
            {
                DrawItem(d3d, ctx, sorted[i], halfW, halfH, 0.004f, _psSemiTransDepthOnly!, _samplerLinear!,
                    isOitPass: false, isSemiTransPass: true, isDepthOnlyPass: true);
            }

            ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.PSSetShader(_psSemiTrans);
            ctx.OMSetDepthStencilState(_depthStateSemiFront, 1);

            for (int i = 0; i < sorted.Count; i++)
            {
                DrawItem(d3d, ctx, sorted[i], halfW, halfH, 0.004f, _psSemiTrans!, _samplerLinear!,
                    isOitPass: false, isSemiTransPass: true, isDepthOnlyPass: false);
            }

            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
            return _hdrSceneTex;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"RenderStandard エラー: {ex.Message}");
            return null;
        }
    }

    private ID3D11Texture2D? RenderOIT(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        List<PreparedRenderItem> items,
        int screenWidth,
        int screenHeight,
        in Matrix4x4 cameraMatrix,
        bool hasEnvMap,
        in EnvironmentParameters environment)
    {
        if (_accumRtv == null || _revealRtv == null || _hdrSceneRtv == null) return null;

        float halfW = screenWidth / 2f;
        float halfH = screenHeight / 2f;

        try
        {
            ctx.ClearRenderTargetView(_hdrSceneRtv, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearRenderTargetView(_gbufNormalRoughRtv!, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearRenderTargetView(_gbufSpecMetaRtv!, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1.0f, 0);
            ctx.ClearRenderTargetView(_accumRtv, new Color4(0f, 0f, 0f, 0f));
            ctx.ClearRenderTargetView(_revealRtv, new Color4(1f, 1f, 1f, 1f));

            ctx.RSSetViewport(new Viewport(0, 0, _renderWidth, _renderHeight, 0f, 1f));
            ctx.RSSetState(_rasterizerState);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

            ctx.VSSetConstantBuffer(0, _cbPerObject);
            ctx.PSSetConstantBuffer(0, _cbPerObject);
            ctx.PSSetSampler(0, _samplerPoint);
            ctx.PSSetSampler(4, _samplerDetail);

            var mrtRtvs = new[] { _hdrSceneRtv, _gbufNormalRoughRtv!, _gbufSpecMetaRtv! };
            ctx.OMSetRenderTargets(mrtRtvs, _dsv);
            ctx.OMSetBlendState(_blendStateOpaqueMrt, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateOpaque, 0);

            ctx.IASetInputLayout(_inputLayout);
            ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_psOpaque);

            for (int i = 0; i < items.Count; i++)
            {
                DrawItem(d3d, ctx, items[i], halfW, halfH, 0.999f, _psOpaque!, _samplerPoint!,
                    isOitPass: false, isSemiTransPass: false, isDepthOnlyPass: false);
            }

            ctx.OMSetRenderTargets(_hdrSceneRtv, _dsv);
            bool showSkybox = environment.OverrideEnvironmentMap
                ? environment.ShowSkybox
                : D3D11RendererSettings.Default.ShowSkybox;
            RenderTransmissiveModelsPass(d3d, ctx, items, halfW, halfH, showSkybox);
            RenderSkyboxPass(ctx, in cameraMatrix, screenWidth, screenHeight, hasEnvMap, in environment);

            var oitRtvs = new[] { _accumRtv, _revealRtv };
            ctx.OMSetRenderTargets(oitRtvs, _dsv);
            ctx.OMSetBlendState(_blendStateOIT, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateOIT, 0);
            ctx.PSSetShader(_psOIT);

            for (int i = 0; i < items.Count; i++)
            {
                DrawItem(d3d, ctx, items[i], halfW, halfH, 0.004f, _psOIT!, _samplerPoint!,
                    isOitPass: true, isSemiTransPass: false, isDepthOnlyPass: false);
            }

            ctx.OMSetRenderTargets(_hdrSceneRtv, (ID3D11DepthStencilView?)null);
            ctx.OMSetBlendState(_blendStateResolve, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateDisabled, 0);

            ctx.IASetInputLayout(_resolveInputLayout);
            ctx.IASetVertexBuffer(0, _resolveVertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
            ctx.VSSetShader(_resolveVs);
            ctx.PSSetShader(_resolvePs);
            ctx.PSSetSampler(0, _samplerLinear);
            ctx.PSSetShaderResource(0, _accumSrv!);
            ctx.PSSetShaderResource(1, _revealSrv!);

            ctx.Draw(4, 0);

            ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
            ctx.PSSetShaderResource(1, (ID3D11ShaderResourceView)null!);
            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
            return _hdrSceneTex;
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"RenderOIT エラー: {ex.Message}");
            return null;
        }
    }

    private void RunAaPass(ID3D11DeviceContext ctx)
    {
        bool isSsaa = _aaMode == AntiAliasingMode.Ssaa15x || _aaMode == AntiAliasingMode.Ssaa2x;
        ID3D11PixelShader? activeAaPs = isSsaa ? _psSsaaResolve : _psFxaa;
        if (_rtv == null || _fxaaSrv == null || _vsFxaa == null || activeAaPs == null || _cbFxaa == null) return;

        ctx.RSSetViewport(new Viewport(0, 0, _width, _height, 0f, 1f));
        ctx.RSSetState(_rasterizerState);
        ctx.OMSetRenderTargets(_rtv, (ID3D11DepthStencilView?)null);
        ctx.OMSetBlendState(_blendStateFxaa, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_depthStateDisabled, 0);

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.IASetInputLayout(_resolveInputLayout);
        ctx.IASetVertexBuffer(0, _resolveVertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
        ctx.VSSetShader(_vsFxaa);
        ctx.PSSetShader(activeAaPs);
        ctx.PSSetSampler(0, _samplerLinear);
        ctx.PSSetShaderResource(0, _fxaaSrv);

        var cbFxaaLocal = new CbFxaa
        {
            RcpFrameX = 1.0f / Math.Max(1, _renderWidth),
            RcpFrameY = 1.0f / Math.Max(1, _renderHeight),
        };
        ctx.UpdateSubresource(in cbFxaaLocal, _cbFxaa);
        ctx.PSSetConstantBuffer(0, _cbFxaa);

        ctx.Draw(4, 0);

        ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
    }

    private void ApplyDepthOfField(
        ID3D11DeviceContext ctx,
        ID3D11Texture2D hdrTex,
        ID3D11RenderTargetView hdrRtv,
        int screenWidth,
        int screenHeight,
        DofParameters dofParams)
    {
        ctx.RSSetViewport(new Viewport(0, 0, _renderWidth, _renderHeight, 0f, 1f));
        ctx.RSSetState(_rasterizerState);
        ctx.CopyResource(_dofTempTexture!, hdrTex);
        ctx.OMSetRenderTargets(hdrRtv, null);

        var cbDofLocal = new CbDof
        {
            FocusDistance = dofParams.FocusDistance,
            Aperture = dofParams.Aperture,
            FocusRange = dofParams.FocusRange,
            NearBlurScale = dofParams.NearBlurScale,
            FarBlurScale = dofParams.FarBlurScale,
            MaxBlur = dofParams.MaxBlur,
            ScreenWidth = screenWidth,
            ScreenHeight = screenHeight,
        };
        ctx.UpdateSubresource(in cbDofLocal, _cbDof!);
        ctx.PSSetConstantBuffer(0, _cbDof);

        ctx.PSSetShaderResource(0, _dofTempSrv!);
        ctx.PSSetShaderResource(1, _depthSrv!);
        ctx.PSSetSampler(0, _samplerLinear);

        ctx.OMSetBlendState(_blendStateFxaa, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_depthStateDisabled, 0);
        ctx.IASetInputLayout(_resolveInputLayout);
        ctx.IASetVertexBuffer(0, _resolveVertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(_dofVs);
        ctx.PSSetShader(_dofPs);

        ctx.Draw(4, 0);

        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(1, null!);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
    }

    private void RenderTransmissiveModelsPass(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        List<PreparedRenderItem> items,
        float halfW,
        float halfH,
        bool showSkybox)
    {
        bool anyTransmissive = false;
        for (int i = 0; i < items.Count && !anyTransmissive; i++)
        {
            var objs = items[i].Item.ObjModels;
            if (objs == null) continue;
            var mat = items[i].Item.Material;
            for (int m = 0; m < objs.Count; m++)
            {
                if (ObjModelRenderer.HasTransmissiveParts(objs[m], in mat))
                {
                    anyTransmissive = true;
                    break;
                }
            }
        }
        if (!anyTransmissive || _hdrSceneTex == null || _hdrSceneRtv == null || _dsv == null)
            return;

        EnsureRefractBgTarget(d3d, _renderWidth, _renderHeight);
        if (_refractBgTexture == null || _refractBgSrv == null) return;

        ctx.CopySubresourceRegion(_refractBgTexture, 0, 0, 0, 0, _hdrSceneTex, 0, null);
        ctx.GenerateMips(_refractBgSrv);

        if (_objModelRenderer == null) _objModelRenderer = new ObjModelRenderer();
        _objModelRenderer.Initialize(d3d, ctx);

        ctx.OMSetRenderTargets(_hdrSceneRtv, _dsv);
        ctx.RSSetState(_rasterizerState);

        // Pass A: 前面の深度・ステンシルを1回だけ記録（表裏の二重描画を防止）
        ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_depthStateSemiTrans, 1);

        for (int i = 0; i < items.Count; i++)
        {
            var prep = items[i];
            var item = prep.Item;
            if (item.ObjModels == null || item.Opacity <= 0.001f) continue;

            var mat = item.Material;
            for (int m = 0; m < item.ObjModels.Count; m++)
            {
                if (!ObjModelRenderer.HasTransmissiveParts(item.ObjModels[m], in mat)) continue;
                _objModelRenderer.RenderModel(
                    d3d, ctx, item.ObjModels[m],
                    prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m], prep.ViewProjMatrix,
                    halfW, halfH, item.Opacity, 0.004f, ObjRenderMode.TransmissiveDepthOnly,
                    in mat, null, null);
            }
        }

        // Pass B: 屈折＋IORフレネル鏡面反射を前面にのみシェーディング
        ctx.OMSetBlendState(_blendStateResolve, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_depthStateSemiFront, 1);

        for (int i = 0; i < items.Count; i++)
        {
            var prep = items[i];
            var item = prep.Item;
            if (item.ObjModels == null || item.Opacity <= 0.001f) continue;

            var mat = item.Material;
            ID3D11ShaderResourceView? matNormalSrv = (mat.Enabled && !string.IsNullOrEmpty(mat.NormalMapPath))
                ? MaterialTextureCache.GetOrLoadSrv(d3d, ctx, mat.NormalMapPath)
                : null;
            ID3D11ShaderResourceView? matRoughSrv = (mat.Enabled && !string.IsNullOrEmpty(mat.RoughnessMapPath))
                ? MaterialTextureCache.GetOrLoadSrv(d3d, ctx, mat.RoughnessMapPath)
                : null;

            for (int m = 0; m < item.ObjModels.Count; m++)
            {
                if (!ObjModelRenderer.HasTransmissiveParts(item.ObjModels[m], in mat)) continue;
                _objModelRenderer.RenderModel(
                    d3d, ctx, item.ObjModels[m],
                    prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m], prep.ViewProjMatrix,
                    halfW, halfH, item.Opacity, 0.004f, ObjRenderMode.Transmissive,
                    in mat, matNormalSrv, matRoughSrv,
                    _refractBgSrv, _renderWidth, _renderHeight, _refractBgMaxMip,
                    refractFullEnv: showSkybox);
            }
        }

        ctx.PSSetShaderResource(8, null!);
        ctx.PSSetShaderResource(9, null!);
        RestoreStandardPipelineState(ctx, _psOpaque!, _samplerLinear!);
    }

    private void RestoreTransmissiveDepthPass(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        List<PreparedRenderItem> items,
        float halfW,
        float halfH)
    {
        if (_dsv == null || _hdrSceneRtv == null) return;
        bool anyTrans = false;
        for (int i = 0; i < items.Count && !anyTrans; i++)
        {
            var objs = items[i].Item.ObjModels;
            if (objs == null) continue;
            var mat = items[i].Item.Material;
            for (int m = 0; m < objs.Count; m++)
            {
                if (ObjModelRenderer.HasTransmissiveParts(objs[m], in mat))
                {
                    anyTrans = true;
                    break;
                }
            }
        }
        if (!anyTrans) return;

        if (_objModelRenderer == null) _objModelRenderer = new ObjModelRenderer();
        _objModelRenderer.Initialize(d3d, ctx);

        ctx.OMSetRenderTargets(_hdrSceneRtv, _dsv);
        ctx.RSSetState(_rasterizerState);
        ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_depthStateOpaque, 0);

        for (int i = 0; i < items.Count; i++)
        {
            var prep = items[i];
            var item = prep.Item;
            if (item.ObjModels == null || item.Opacity <= 0.001f) continue;

            var mat = item.Material;
            for (int m = 0; m < item.ObjModels.Count; m++)
            {
                if (!ObjModelRenderer.HasTransmissiveParts(item.ObjModels[m], in mat)) continue;
                _objModelRenderer.RenderModel(
                    d3d, ctx, item.ObjModels[m],
                    prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m], prep.ViewProjMatrix,
                    halfW, halfH, item.Opacity, 0.004f, ObjRenderMode.TransmissiveDepthOnly,
                    in mat, null, null);
            }
        }

        RestoreStandardPipelineState(ctx, _psOpaque!, _samplerLinear!);
    }

    private void DrawItem(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        PreparedRenderItem prep,
        float halfW,
        float halfH,
        float alphaThreshold,
        ID3D11PixelShader activePs,
        ID3D11SamplerState activeSampler,
        bool isOitPass,
        bool isSemiTransPass,
        bool isDepthOnlyPass,
        float opacityMultiplier = 1f)
    {
        var item = prep.Item;
        var mat = item.Material;
        ID3D11ShaderResourceView? matNormalSrv = (mat.Enabled && !string.IsNullOrEmpty(mat.NormalMapPath))
            ? MaterialTextureCache.GetOrLoadSrv(d3d, ctx, mat.NormalMapPath)
            : null;
        ID3D11ShaderResourceView? matRoughSrv = (mat.Enabled && !string.IsNullOrEmpty(mat.RoughnessMapPath))
            ? MaterialTextureCache.GetOrLoadSrv(d3d, ctx, mat.RoughnessMapPath)
            : null;

        if (item.ObjModels != null)
        {
            try
            {
                if (_objModelRenderer == null) _objModelRenderer = new ObjModelRenderer();
                _objModelRenderer.Initialize(d3d, ctx);

                ObjRenderMode objMode = isOitPass
                    ? ObjRenderMode.OIT
                    : (isSemiTransPass
                        ? (isDepthOnlyPass ? ObjRenderMode.SemiTransDepthOnly : ObjRenderMode.SemiTransColor)
                        : ObjRenderMode.Opaque);

                for (int m = 0; m < item.ObjModels.Count; m++)
                {
                    _objModelRenderer.RenderModel(
                        d3d, ctx, item.ObjModels[m],
                        prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m], prep.ViewProjMatrix,
                        halfW, halfH, item.Opacity * opacityMultiplier, alphaThreshold, objMode,
                        in mat, matNormalSrv, matRoughSrv);
                }

                RestoreStandardPipelineState(ctx, activePs, activeSampler);
            }
            catch (Exception ex)
            {
                Iyahon_D3D11Renderer_CorePlugin.Log($"OBJモデル描画エラー: {ex.Message}");
            }
            return;
        }

        if (prep.ConfiguredEffect != null && item.Srv != null)
        {
            // 非対応の外部ID3DEffectがOIT/半透明パスで二重描画や不正出力を起こすのを防止
            if (!prep.IsOitCapable && (isOitPass || isSemiTransPass))
                return;

            try
            {
                var renderContext = new D3DRenderContext
                {
                    WorldMatrix = prep.WorldMatrix,
                    NormalMatrix = prep.NormalMatrix,
                    ViewProjectionMatrix = prep.ViewProjMatrix,
                    TextureWidth = (int)item.PixelWidth,
                    TextureHeight = (int)item.PixelHeight,
                    HalfScreenWidth = halfW,
                    HalfScreenHeight = halfH,
                    Opacity = item.Opacity * opacityMultiplier,
                    AlphaThreshold = alphaThreshold,
                    CameraMatrix = item.DrawDescription.Camera,
                    IsOitPass = isOitPass,
                    IsSemiTransPass = isSemiTransPass,
                    IsDepthOnlyPass = isDepthOnlyPass,
                    Material = mat,
                    NormalMapSrv = matNormalSrv,
                    RoughnessMapSrv = matRoughSrv,
                };

                prep.ConfiguredEffect.Render(ctx, d3d, item.Srv, renderContext);
                RestoreStandardPipelineState(ctx, activePs, activeSampler);
            }
            catch (Exception ex)
            {
                Iyahon_D3D11Renderer_CorePlugin.Log($"D3Dエフェクト描画エラー: {ex.Message}");
            }
            return;
        }

        var cb = new CbPerObject
        {
            WorldMatrix = prep.WorldMatrix,
            ViewProjMatrix = prep.ViewProjMatrix,
            NormalMatrix = prep.NormalMatrix,
            HalfWidth = halfW,
            HalfHeight = halfH,
            Opacity = item.Opacity * opacityMultiplier,
            AlphaThreshold = alphaThreshold,
            MaterialParams = mat.Enabled
                ? new Vector4(mat.Metallic, mat.Roughness, mat.Clearcoat, 1f)
                : Vector4.Zero,
            TintColor = mat.Enabled ? mat.TintColor : Vector4.One,
            EmissiveColor = mat.Enabled ? mat.EmissiveColor : Vector4.Zero,
            DetailParams = mat.Enabled
                ? new Vector4(
                    mat.NormalStrength,
                    matNormalSrv != null ? 1f : 0f,
                    matRoughSrv != null ? 2f : 0f,
                    mat.UvScale > 0f ? mat.UvScale : 1f)
                : new Vector4(1f, 0f, 0f, 1f),
        };
        ctx.UpdateSubresource(in cb, _cbPerObject!);

        ctx.VSSetConstantBuffer(0, _cbPerObject);
        ctx.PSSetConstantBuffer(0, _cbPerObject);
        ctx.PSSetShaderResource(0, item.Srv!);
        ctx.PSSetShaderResource(1, matNormalSrv ?? _flatNormalSrv!);
        ctx.PSSetShaderResource(6, matRoughSrv ?? _whiteSrv!);
        ctx.Draw(4, 0);
    }

    private void RestoreStandardPipelineState(ID3D11DeviceContext ctx, ID3D11PixelShader activePs, ID3D11SamplerState activeSampler)
    {
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, Marshal.SizeOf<Vertex>(), 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(_vs);
        ctx.VSSetConstantBuffer(0, _cbPerObject);
        ctx.PSSetConstantBuffer(0, _cbPerObject);
        ctx.RSSetState(_rasterizerState);
        ctx.PSSetShader(activePs);
        ctx.PSSetSampler(0, activeSampler);
        ctx.PSSetSampler(4, _samplerDetail);
    }

    public static Matrix4x4 BuildModelMatrix(RenderItem item)
    {
        var desc = item.DrawDescription;
        float d2r = MathF.PI / 180f;

        var S = Matrix4x4.CreateScale(item.PixelWidth, item.PixelHeight, 1f);
        var Toffset = Matrix4x4.CreateTranslation(item.BoundsCenterX, item.BoundsCenterY, 0f);
        var invertScale = desc.Invert
            ? Matrix4x4.CreateScale(-1f, 1f, 1f, new Vector3(desc.CenterPoint.X, desc.CenterPoint.Y, 0f))
            : Matrix4x4.Identity;
        var Zoom = Matrix4x4.CreateScale((float)desc.Zoom.X, (float)desc.Zoom.Y, 1f);

        var Rz = Matrix4x4.CreateRotationZ(d2r * (float)desc.Rotation.Z);
        var Ry = Matrix4x4.CreateRotationY(d2r * -(float)desc.Rotation.Y);
        var Rx = Matrix4x4.CreateRotationX(d2r * -(float)desc.Rotation.X);
        var Tdraw = Matrix4x4.CreateTranslation(desc.Draw);

        return S * Toffset * invertScale * Zoom * Rz * Ry * Rx * Tdraw;
    }

    public static Matrix4x4 BuildObjLocalMatrix(ObjModelData modelData)
    {
        float d2r = MathF.PI / 180f;

        Vector3 pivot = modelData.GroundToOrigin
            ? new Vector3(modelData.ModelCenter.X, modelData.BoundsMin.Y, modelData.ModelCenter.Z)
            : (modelData.NormalizeSize ? modelData.ModelCenter : Vector3.Zero);

        Matrix4x4 normalize = modelData.NormalizeSize
            ? Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(modelData.ModelScale)
            : Matrix4x4.CreateTranslation(-pivot);

        float baseScale = modelData.NormalizeSize ? 200f : modelData.UnitScale;

        var localScaleM = Matrix4x4.CreateScale(modelData.LocalScale);
        var localRotM = Matrix4x4.CreateRotationX(d2r * modelData.LocalRotationDeg.X)
                      * Matrix4x4.CreateRotationY(d2r * modelData.LocalRotationDeg.Y)
                      * Matrix4x4.CreateRotationZ(d2r * modelData.LocalRotationDeg.Z);

        var yFlip = Matrix4x4.CreateScale(1f, -1f, 1f);
        var baseScaleM = Matrix4x4.CreateScale(baseScale);

        return normalize * localScaleM * localRotM * yFlip * baseScaleM;
    }

    public static Vortice.RawRectF ComputeLocalModelBoundsRect(ObjModelData? modelData)
    {
        if (modelData == null)
            return new Vortice.RawRectF(-100f, -100f, 100f, 100f);

        Matrix4x4 localMatrix = BuildObjLocalMatrix(modelData);

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        var supportPoints = modelData.BoundingSupportPoints;
        if (supportPoints != null && supportPoints.Length > 0)
        {
            for (int i = 0; i < supportPoints.Length; i++)
            {
                var localPos = Vector3.Transform(supportPoints[i], localMatrix);
                if (!float.IsFinite(localPos.X) || !float.IsFinite(localPos.Y))
                    continue;

                minX = MathF.Min(minX, localPos.X);
                minY = MathF.Min(minY, localPos.Y);
                maxX = MathF.Max(maxX, localPos.X);
                maxY = MathF.Max(maxY, localPos.Y);
            }
        }
        else
        {
            Vector3 bMin = modelData.BoundsMin;
            Vector3 bMax = modelData.BoundsMax;
            if (Vector3.DistanceSquared(bMax, bMin) < 1e-6f)
            {
                bMin = new Vector3(-0.5f, -0.5f, -0.5f);
                bMax = new Vector3(0.5f, 0.5f, 0.5f);
            }

            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? bMin.X : bMax.X,
                    (c & 2) == 0 ? bMin.Y : bMax.Y,
                    (c & 4) == 0 ? bMin.Z : bMax.Z);

                var localPos = Vector3.Transform(corner, localMatrix);
                if (!float.IsFinite(localPos.X) || !float.IsFinite(localPos.Y))
                    continue;

                minX = MathF.Min(minX, localPos.X);
                minY = MathF.Min(minY, localPos.Y);
                maxX = MathF.Max(maxX, localPos.X);
                maxY = MathF.Max(maxY, localPos.Y);
            }
        }

        if (minX > maxX || minY > maxY)
            return new Vortice.RawRectF(-100f, -100f, 100f, 100f);

        const float MaxLocalExtent = 8192f;
        minX = Math.Clamp(minX, -MaxLocalExtent, MaxLocalExtent);
        maxX = Math.Clamp(maxX, -MaxLocalExtent, MaxLocalExtent);
        minY = Math.Clamp(minY, -MaxLocalExtent, MaxLocalExtent);
        maxY = Math.Clamp(maxY, -MaxLocalExtent, MaxLocalExtent);

        const float MinBoxSize = 24f;
        if (maxX - minX < MinBoxSize)
        {
            float cx = (minX + maxX) * 0.5f;
            minX = cx - MinBoxSize * 0.5f;
            maxX = cx + MinBoxSize * 0.5f;
        }
        if (maxY - minY < MinBoxSize)
        {
            float cy = (minY + maxY) * 0.5f;
            minY = cy - MinBoxSize * 0.5f;
            maxY = cy + MinBoxSize * 0.5f;
        }

        return new Vortice.RawRectF(minX, minY, maxX, maxY);
    }

    public static Vector2[] RectToQuad(Vortice.RawRectF rect)
    {
        return new Vector2[4]
        {
            new Vector2(rect.Left, rect.Top),
            new Vector2(rect.Right, rect.Top),
            new Vector2(rect.Right, rect.Bottom),
            new Vector2(rect.Left, rect.Bottom),
        };
    }

    public static Matrix4x4 BuildObjModelMatrix(RenderItem item, ObjModelData modelData)
    {
        var desc = item.DrawDescription;
        float d2r = MathF.PI / 180f;

        Matrix4x4 localM = BuildObjLocalMatrix(modelData);

        float zx = (float)desc.Zoom.X;
        float zy = (float)desc.Zoom.Y;
        if (desc.Invert) zx = -zx;
        float zScale = (MathF.Abs(zx) + MathF.Abs(zy)) / 2f;
        var Zoom = Matrix4x4.CreateScale(zx, zy, zScale);

        var Rz = Matrix4x4.CreateRotationZ(d2r * (float)desc.Rotation.Z);
        var Ry = Matrix4x4.CreateRotationY(d2r * -(float)desc.Rotation.Y);
        var Rx = Matrix4x4.CreateRotationX(d2r * -(float)desc.Rotation.X);
        var Tdraw = Matrix4x4.CreateTranslation(desc.Draw);

        return localM * Zoom * Rz * Ry * Rx * Tdraw;
    }

    private static Matrix4x4 BuildViewProjMatrix(RenderItem item)
    {
        return BuildViewProjMatrix(item.DrawDescription.Camera);
    }

    public static Matrix4x4 BuildViewProjMatrix(in Matrix4x4 cameraMatrix)
    {
        var d2dProj = new Matrix4x4(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, -1f / 1000f,
            0f, 0f, 0f, 1f
        );
        return cameraMatrix * d2dProj;
    }

    public static bool TryComputeProjectedScreenRect(
        RenderItem item,
        int screenWidth,
        int screenHeight,
        out Vortice.RawRectF rect)
    {
        return TryComputeProjectedScreenBounds(new[] { item }, screenWidth, screenHeight, out rect, out _);
    }

    private static bool IsCameraAlignedWithScreen(in Matrix4x4 cam)
    {
        const float Eps = 1e-4f;
        return MathF.Abs(cam.M11 - 1f) < Eps && MathF.Abs(cam.M12) < Eps && MathF.Abs(cam.M13) < Eps
            && MathF.Abs(cam.M21) < Eps && MathF.Abs(cam.M22 - 1f) < Eps && MathF.Abs(cam.M23) < Eps
            && MathF.Abs(cam.M31) < Eps && MathF.Abs(cam.M32) < Eps && MathF.Abs(cam.M33 - 1f) < Eps;
    }

    public static bool TryComputeProjectedScreenBounds(
        IReadOnlyList<RenderItem> itemGroup,
        int screenWidth,
        int screenHeight,
        out Vortice.RawRectF rect,
        out Vector2[] quad)
    {
        rect = default;
        quad = Array.Empty<Vector2>();
        if (itemGroup == null || itemGroup.Count == 0)
            return false;

        var primaryDesc = itemGroup[0].DrawDescription;
        var primaryVp = BuildViewProjMatrix(primaryDesc.Camera);
        float limitW = MathF.Max(screenWidth, 1920f);
        float limitH = MathF.Max(screenHeight, 1080f);

        bool use2DZRoll = itemGroup.Count == 1
            && MathF.Abs(primaryDesc.Rotation.X) < 0.01f
            && MathF.Abs(primaryDesc.Rotation.Y) < 0.01f
            && MathF.Abs(primaryDesc.Rotation.Z) >= 0.01f
            && IsCameraAlignedWithScreen(primaryDesc.Camera);

        var pivotClip = Vector4.Transform(new Vector4(primaryDesc.Draw, 1f), primaryVp);
        bool hasValidPivot = use2DZRoll &&
                             pivotClip.W > 0.01f &&
                             float.IsFinite(pivotClip.X / pivotClip.W) &&
                             float.IsFinite(pivotClip.Y / pivotClip.W);
        Vector2 pivot2D = hasValidPivot
            ? new Vector2(pivotClip.X / pivotClip.W, pivotClip.Y / pivotClip.W)
            : Vector2.Zero;

        float rotZRad = (float)primaryDesc.Rotation.Z * (MathF.PI / 180f);
        Vector2 axisU = new Vector2(MathF.Cos(rotZRad), MathF.Sin(rotZRad));
        Vector2 axisV = new Vector2(-MathF.Sin(rotZRad), MathF.Cos(rotZRad));

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        float minU = float.MaxValue;
        float minV = float.MaxValue;
        float maxU = float.MinValue;
        float maxV = float.MinValue;
        int validCount = 0;

        void AccumulatePoint(in Vector3 localPt, in Matrix4x4 world, in Matrix4x4 vp)
        {
            var wPos = Vector3.Transform(localPt, world);
            var clip = Vector4.Transform(new Vector4(wPos, 1f), vp);
            if (clip.W <= 0.01f) return;

            float sx = clip.X / clip.W;
            float sy = clip.Y / clip.W;
            if (!float.IsFinite(sx) || !float.IsFinite(sy)) return;

            sx = Math.Clamp(sx, -limitW, limitW);
            sy = Math.Clamp(sy, -limitH, limitH);

            minX = MathF.Min(minX, sx);
            minY = MathF.Min(minY, sy);
            maxX = MathF.Max(maxX, sx);
            maxY = MathF.Max(maxY, sy);

            if (hasValidPivot)
            {
                Vector2 rel = new Vector2(sx, sy) - pivot2D;
                float u = Vector2.Dot(rel, axisU);
                float v = Vector2.Dot(rel, axisV);
                minU = MathF.Min(minU, u);
                maxU = MathF.Max(maxU, u);
                minV = MathF.Min(minV, v);
                maxV = MathF.Max(maxV, v);
            }

            validCount++;
        }

        for (int g = 0; g < itemGroup.Count; g++)
        {
            var item = itemGroup[g];
            if (item.ObjModels == null || item.ObjModels.Count == 0)
                continue;

            var itemVp = BuildViewProjMatrix(item.DrawDescription.Camera);
            for (int m = 0; m < item.ObjModels.Count; m++)
            {
                var model = item.ObjModels[m];
                var world = BuildObjModelMatrix(item, model);

                var supportPoints = model.BoundingSupportPoints;
                if (supportPoints != null && supportPoints.Length > 0)
                {
                    for (int p = 0; p < supportPoints.Length; p++)
                        AccumulatePoint(in supportPoints[p], in world, in itemVp);
                }
                else
                {
                    Vector3 bMin = model.BoundsMin;
                    Vector3 bMax = model.BoundsMax;
                    if (Vector3.DistanceSquared(bMax, bMin) < 1e-6f)
                    {
                        bMin = new Vector3(-0.5f, -0.5f, -0.5f);
                        bMax = new Vector3(0.5f, 0.5f, 0.5f);
                    }

                    for (int c = 0; c < 8; c++)
                    {
                        var corner = new Vector3(
                            (c & 1) == 0 ? bMin.X : bMax.X,
                            (c & 2) == 0 ? bMin.Y : bMax.Y,
                            (c & 4) == 0 ? bMin.Z : bMax.Z);
                        AccumulatePoint(in corner, in world, in itemVp);
                    }
                }
            }
        }

        if (validCount == 0)
            return false;

        const float MinBoxSize = 24f;
        if (maxX - minX < MinBoxSize)
        {
            float cx = (minX + maxX) * 0.5f;
            minX = cx - MinBoxSize * 0.5f;
            maxX = cx + MinBoxSize * 0.5f;
        }
        if (maxY - minY < MinBoxSize)
        {
            float cy = (minY + maxY) * 0.5f;
            minY = cy - MinBoxSize * 0.5f;
            maxY = cy + MinBoxSize * 0.5f;
        }

        rect = new Vortice.RawRectF(minX, minY, maxX, maxY);

        if (hasValidPivot && minU <= maxU && minV <= maxV)
        {
            if (maxU - minU < MinBoxSize)
            {
                float cu = (minU + maxU) * 0.5f;
                minU = cu - MinBoxSize * 0.5f;
                maxU = cu + MinBoxSize * 0.5f;
            }
            if (maxV - minV < MinBoxSize)
            {
                float cv = (minV + maxV) * 0.5f;
                minV = cv - MinBoxSize * 0.5f;
                maxV = cv + MinBoxSize * 0.5f;
            }

            quad = new Vector2[4]
            {
                pivot2D + axisU * minU + axisV * minV,
                pivot2D + axisU * maxU + axisV * minV,
                pivot2D + axisU * maxU + axisV * maxV,
                pivot2D + axisU * minU + axisV * maxV,
            };
        }
        else
        {
            quad = RectToQuad(rect);
        }

        return true;
    }

    public static bool TryComputeProjectedLightRect(
        in Vector3 worldPos,
        in Matrix4x4 cameraMatrix,
        out Vortice.RawRectF rect)
    {
        rect = default;
        var vp = BuildViewProjMatrix(cameraMatrix);
        var clip = Vector4.Transform(new Vector4(worldPos, 1f), vp);
        if (clip.W <= 0.01f)
            return false;

        float sx = clip.X / clip.W;
        float sy = clip.Y / clip.W;
        const float HalfSize = 18f;
        rect = new Vortice.RawRectF(sx - HalfSize, sy - HalfSize, sx + HalfSize, sy + HalfSize);
        return true;
    }

    private static Vector3 ComputeCameraWorldPos(in Matrix4x4 cameraMatrix)
        => Matrix4x4.Invert(cameraMatrix, out var invCam)
            ? Vector3.Transform(new Vector3(0f, 0f, 1000f), invCam)
            : new Vector3(0f, 0f, 1000f);

    private static bool ShouldUseInvertedHull(PreparedRenderItem prep)
    {
        var ol = prep.Item.Outline;
        if (!ol.Enabled || ol.Thickness <= 0.001f || ol.Opacity <= 0.001f || prep.Item.Opacity <= 0.001f)
            return false;
        if (ol.Mode == D3DEffect.Effects.D3DOutlineMode.ScreenSpace)
            return false;
        if (prep.Item.ObjModels == null)
            return false;
        for (int m = 0; m < prep.Item.ObjModels.Count; m++)
        {
            if (!prep.Item.ObjModels[m].Noise3D.Enabled)
                return true;
        }
        return false;
    }

    private static bool ShouldUseScreenOutline(PreparedRenderItem prep)
    {
        var ol = prep.Item.Outline;
        if (!ol.Enabled || ol.Thickness <= 0.001f || ol.Opacity <= 0.001f || prep.Item.Opacity <= 0.001f)
            return false;
        if (ol.Mode == D3DEffect.Effects.D3DOutlineMode.ScreenSpace)
            return true;
        if (ol.Mode == D3DEffect.Effects.D3DOutlineMode.Auto)
        {
            if (prep.Item.ObjModels == null)
                return true;
            for (int m = 0; m < prep.Item.ObjModels.Count; m++)
            {
                if (!prep.Item.ObjModels[m].Noise3D.Enabled)
                    return false;
            }
            return true;
        }
        return false;
    }

    private void RenderOpaqueNoise3DAndHulls(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        List<PreparedRenderItem> items,
        Vector3 camWorldPos,
        float halfW,
        float halfH,
        bool includeOpaqueNoise3D,
        bool includeNormalHulls,
        bool includeOutlineOnlyHulls)
    {
        if (_hdrSceneRtv == null || _dsv == null) return;

        if (includeOpaqueNoise3D)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var prep = items[i];
                if (prep.Item.ObjModels == null || prep.Item.Opacity <= 0.001f) continue;
                if (prep.Item.Outline.Enabled && prep.Item.Outline.OutlineOnly) continue;

                for (int m = 0; m < prep.Item.ObjModels.Count; m++)
                {
                    var model = prep.Item.ObjModels[m];
                    if (!model.Noise3D.Enabled || model.Noise3D.IsSemiTransparent)
                        continue;
                    _noise3DRenderer ??= new Model3D.Noise3DVolumeRenderer();
                    _noise3DRenderer.Initialize(d3d, ctx);
                    _noise3DRenderer.Render(
                        d3d, ctx, _hdrSceneRtv, _dsv, _renderWidth, _renderHeight,
                        model, prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m], prep.ViewProjMatrix,
                        camWorldPos, halfW, halfH, prep.Item.Opacity);
                }
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            var prep = items[i];
            if (!ShouldUseInvertedHull(prep)) continue;
            bool isOnly = prep.Item.Outline.OutlineOnly;
            if (isOnly ? !includeOutlineOnlyHulls : !includeNormalHulls) continue;

            _objModelRenderer ??= new ObjModelRenderer();
            _objModelRenderer.Initialize(d3d, ctx);

            if (isOnly)
            {
                ctx.OMSetRenderTargets(_hdrSceneRtv, _dsv);
                ctx.OMSetBlendState(_blendStateNoColor, null, unchecked((int)0xFFFFFFFF));
                ctx.OMSetDepthStencilState(_depthStateOpaque, 0);
                var mat = prep.Item.Material;
                for (int m = 0; m < prep.Item.ObjModels!.Count; m++)
                {
                    var model = prep.Item.ObjModels[m];
                    if (model.Noise3D.Enabled) continue;
                    _objModelRenderer.RenderModel(
                        d3d, ctx, model, prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m],
                        prep.ViewProjMatrix, halfW, halfH, prep.Item.Opacity, 0.999f,
                        ObjRenderMode.Opaque, in mat, null, null);
                }
            }

            ctx.OMSetRenderTargets(_hdrSceneRtv, _dsv);
            ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(_depthStateOIT, 0);
            for (int m = 0; m < prep.Item.ObjModels!.Count; m++)
            {
                var model = prep.Item.ObjModels[m];
                if (model.Noise3D.Enabled) continue;
                _objModelRenderer.RenderOutline(
                    d3d, ctx, model,
                    prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m], prep.ViewProjMatrix,
                    halfW, halfH, halfW, halfH,
                    prep.Item.Opacity, prep.Item.Outline);
            }
        }

        ctx.OMSetRenderTargets(_hdrSceneRtv, _dsv);
        ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_depthStateOpaque, 0);
        RestoreStandardPipelineState(ctx, _psOpaque!, _samplerLinear!);
    }

    private void RenderPostSceneEffectsAndOutlines(
        ID3D11Device d3d,
        ID3D11DeviceContext ctx,
        List<PreparedRenderItem> allItems,
        Vector3 camWorldPos,
        int screenWidth,
        int screenHeight,
        bool alreadyRenderedOpaqueInStandard)
    {
        if (_hdrSceneRtv == null || _dsv == null) return;
        float halfW = screenWidth * 0.5f;
        float halfH = screenHeight * 0.5f;
        ctx.RSSetViewport(new Viewport(0, 0, _renderWidth, _renderHeight, 0f, 1f));

        if (alreadyRenderedOpaqueInStandard)
        {
            RenderOpaqueNoise3DAndHulls(d3d, ctx, allItems, camWorldPos, halfW, halfH,
                includeOpaqueNoise3D: false, includeNormalHulls: false, includeOutlineOnlyHulls: true);
        }
        else
        {
            RenderOpaqueNoise3DAndHulls(d3d, ctx, allItems, camWorldPos, halfW, halfH,
                includeOpaqueNoise3D: true, includeNormalHulls: true, includeOutlineOnlyHulls: true);
        }

        // Volume3D Noise Pass
        for (int i = 0; i < allItems.Count; i++)
        {
            var prep = allItems[i];
            if (prep.Item.ObjModels == null || prep.Item.Opacity <= 0.001f) continue;
            if (prep.Item.Outline.Enabled && prep.Item.Outline.OutlineOnly) continue;

            for (int m = 0; m < prep.Item.ObjModels.Count; m++)
            {
                var model = prep.Item.ObjModels[m];
                if (!model.Noise3D.Enabled || !model.Noise3D.IsSemiTransparent)
                    continue;
                _noise3DRenderer ??= new Model3D.Noise3DVolumeRenderer();
                _noise3DRenderer.Initialize(d3d, ctx);
                _noise3DRenderer.Render(
                    d3d, ctx, _hdrSceneRtv, _dsv, _renderWidth, _renderHeight,
                    model, prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m], prep.ViewProjMatrix,
                    camWorldPos, halfW, halfH, prep.Item.Opacity);
            }
        }

        // Screen-space Outlines
        float aaScale = screenWidth > 0 ? (_renderWidth / (float)screenWidth) : 1f;
        for (int i = 0; i < allItems.Count; i++)
        {
            var prep = allItems[i];
            if (!ShouldUseScreenOutline(prep)) continue;

            _screenOutlineHelper ??= new D3DEffect.D3DScreenOutlineHelper();
            _screenOutlineHelper.Initialize(d3d);
            if (!_screenOutlineHelper.BeginMaskPass(d3d, ctx, _dsv, _renderWidth, _renderHeight))
                continue;

            var item = prep.Item;
            if (item.ObjModels != null)
            {
                for (int m = 0; m < item.ObjModels.Count; m++)
                {
                    var model = item.ObjModels[m];
                    if (model.Noise3D.Enabled)
                    {
                        _noise3DRenderer ??= new Model3D.Noise3DVolumeRenderer();
                        _noise3DRenderer.Initialize(d3d, ctx);
                        _noise3DRenderer.Render(
                            d3d, ctx, _screenOutlineHelper.MaskRtv!, _dsv, _renderWidth, _renderHeight,
                            model, prep.ObjWorldMatrices![m], prep.ObjNormalMatrices![m], prep.ViewProjMatrix,
                            camWorldPos, halfW, halfH, item.Opacity);
                    }
                    else
                    {
                        _objModelRenderer ??= new ObjModelRenderer();
                        _objModelRenderer.Initialize(d3d, ctx);
                        var mat = item.Material;
                        _objModelRenderer.RenderModel(
                            d3d, ctx, model, prep.ObjWorldMatrices![m], Matrix4x4.Identity,
                            prep.ViewProjMatrix, halfW, halfH, item.Opacity, 0.004f,
                            ObjRenderMode.Silhouette, in mat, null, null);
                    }
                }
            }
            else if (prep.ConfiguredEffect != null && item.Srv != null)
            {
                var rCtx = new D3DEffect.D3DRenderContext
                {
                    WorldMatrix = prep.WorldMatrix,
                    NormalMatrix = prep.NormalMatrix,
                    ViewProjectionMatrix = prep.ViewProjMatrix,
                    CameraWorldPosition = camWorldPos,
                    TextureWidth = (int)item.PixelWidth,
                    TextureHeight = (int)item.PixelHeight,
                    HalfScreenWidth = halfW,
                    HalfScreenHeight = halfH,
                    Opacity = item.Opacity,
                    AlphaThreshold = 0.004f,
                    CameraMatrix = item.DrawDescription.Camera,
                    IsSemiTransPass = false,
                    Material = item.Material,
                };
                prep.ConfiguredEffect.Render(ctx, d3d, item.Srv, rCtx);
            }
            else if (item.Srv != null)
            {
                _screenOutlineHelper.DrawQuadSilhouette(
                    ctx, item.Srv, prep.WorldMatrix, prep.ViewProjMatrix,
                    halfW, halfH, item.Opacity, 0.004f);
            }

            var scaledOutline = item.Outline;
            scaledOutline.Thickness *= aaScale;
            scaledOutline.Blur *= aaScale;
            _screenOutlineHelper.CompositeOutline(ctx, _hdrSceneRtv, _dsv, _renderWidth, _renderHeight, in scaledOutline);
        }

        ctx.OMSetRenderTargets(_hdrSceneRtv, _dsv);
        ctx.OMSetBlendState(_blendStateOpaque, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_depthStateOpaque, 0);
        RestoreStandardPipelineState(ctx, _psOpaque!, _samplerLinear!);
    }

    private void DisposeTargets()
    {
        _renderTargetBitmap?.Dispose(); _renderTargetBitmap = null;
        _rtv?.Dispose(); _rtv = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _hdrSceneRtv?.Dispose(); _hdrSceneRtv = null;
        _hdrSceneSrv?.Dispose(); _hdrSceneSrv = null;
        _hdrSceneTex?.Dispose(); _hdrSceneTex = null;
        _gbufNormalRoughRtv?.Dispose(); _gbufNormalRoughRtv = null;
        _gbufNormalRoughSrv?.Dispose(); _gbufNormalRoughSrv = null;
        _gbufNormalRoughTex?.Dispose(); _gbufNormalRoughTex = null;
        _gbufSpecMetaRtv?.Dispose(); _gbufSpecMetaRtv = null;
        _gbufSpecMetaSrv?.Dispose(); _gbufSpecMetaSrv = null;
        _gbufSpecMetaTex?.Dispose(); _gbufSpecMetaTex = null;
        _isHdrOutput = false;
        _dsv?.Dispose(); _dsv = null;
        _depthSrv?.Dispose(); _depthSrv = null;
        _depthStencil?.Dispose(); _depthStencil = null;

        DisposeOitTargets();
        DisposeAaTarget();
        DisposeDofTargets();
        DisposeRefractBgTarget();
        _postProcess.DisposeTargets();
        _shadowPool.ReleaseAll();
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
        _noise3DRenderer?.Dispose();
        _noise3DRenderer = null;
        _screenOutlineHelper?.Dispose();
        _screenOutlineHelper = null;

        ModelGpuCache.ClearForDevice(_d3dDevicePointer);
        MaterialTextureCache.ClearForDevice(_d3dDevicePointer);
        EnvironmentMapManager.ClearForDevice(_d3dDevicePointer);
        _postProcess.Release();
        _fp16RenderTargetUnsupported = false;
        _isHdrOutput = false;
        _shadowPool.ReleaseAll();
        _lightBuffer.Release();
        _shadowBuffer.Release();
        _bakedTexturePool.ClearAll();

        _vs?.Dispose(); _vs = null;
        _psOpaque?.Dispose(); _psOpaque = null;
        _psOIT?.Dispose(); _psOIT = null;
        _psSemiTrans?.Dispose(); _psSemiTrans = null;
        _psSemiTransDepthOnly?.Dispose(); _psSemiTransDepthOnly = null;
        _psShadowAlpha?.Dispose(); _psShadowAlpha = null;
        _resolveVs?.Dispose(); _resolveVs = null;
        _resolvePs?.Dispose(); _resolvePs = null;
        _skyboxVs?.Dispose(); _skyboxVs = null;
        _skyboxPs?.Dispose(); _skyboxPs = null;
        _vsFxaa?.Dispose(); _vsFxaa = null;
        _psFxaa?.Dispose(); _psFxaa = null;
        _psSsaaResolve?.Dispose(); _psSsaaResolve = null;
        _inputLayout?.Dispose(); _inputLayout = null;
        _resolveInputLayout?.Dispose(); _resolveInputLayout = null;
        _vertexBuffer?.Dispose(); _vertexBuffer = null;
        _resolveVertexBuffer?.Dispose(); _resolveVertexBuffer = null;
        _cbPerObject?.Dispose(); _cbPerObject = null;
        _cbLighting?.Dispose(); _cbLighting = null;
        _cbSkybox?.Dispose(); _cbSkybox = null;
        _cbFxaa?.Dispose(); _cbFxaa = null;
        _dofVs?.Dispose(); _dofVs = null;
        _dofPs?.Dispose(); _dofPs = null;
        _cbDof?.Dispose(); _cbDof = null;
        _samplerPoint?.Dispose(); _samplerPoint = null;
        _samplerLinear?.Dispose(); _samplerLinear = null;
        _samplerDetail?.Dispose(); _samplerDetail = null;
        _samplerShadowCmp?.Dispose(); _samplerShadowCmp = null;
        _whiteSrv?.Dispose(); _whiteSrv = null;
        _whiteTex?.Dispose(); _whiteTex = null;
        _flatNormalSrv?.Dispose(); _flatNormalSrv = null;
        _flatNormalTex?.Dispose(); _flatNormalTex = null;
        _blendStateOpaque?.Dispose(); _blendStateOpaque = null;
        _blendStateOpaqueMrt?.Dispose(); _blendStateOpaqueMrt = null;
        _blendStateNoColor?.Dispose(); _blendStateNoColor = null;
        _blendStateOIT?.Dispose(); _blendStateOIT = null;
        _blendStateResolve?.Dispose(); _blendStateResolve = null;
        _blendStateFxaa?.Dispose(); _blendStateFxaa = null;
        _depthStateOpaque?.Dispose(); _depthStateOpaque = null;
        _depthStateOIT?.Dispose(); _depthStateOIT = null;
        _depthStateSemiTrans?.Dispose(); _depthStateSemiTrans = null;
        _depthStateSemiBack?.Dispose(); _depthStateSemiBack = null;
        _depthStateSemiFront?.Dispose(); _depthStateSemiFront = null;
        _depthStateSkybox?.Dispose(); _depthStateSkybox = null;
        _depthStateDisabled?.Dispose(); _depthStateDisabled = null;
        _rasterizerState?.Dispose(); _rasterizerState = null;
        _rasterizerShadowOrtho?.Dispose(); _rasterizerShadowOrtho = null;
        _rasterizerShadowPersp?.Dispose(); _rasterizerShadowPersp = null;

        _d3dDevicePointer = IntPtr.Zero;
        _d3dContextPointer = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeTargets();
        DisposeResources();

        _shadowPool.Dispose();
        _lightBuffer.Dispose();
        _shadowBuffer.Dispose();
        _bakedTexturePool.Dispose();
        _postProcess.Dispose();
    }
}

internal sealed class RenderItem
{
    public required DrawDescription DrawDescription { get; set; }
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
    public MaterialOverrideParameters Material { get; init; }
    public D3DEffect.Effects.OutlineParameters Outline { get; set; }

    public RenderItem CloneWith(DrawDescription desc, D3DEffect.Effects.OutlineParameters outline)
    {
        var copy = (RenderItem)MemberwiseClone();
        copy.DrawDescription = desc;
        copy.Outline = outline;
        return copy;
    }
}

internal sealed class PreparedRenderItem
{
    public required RenderItem Item { get; init; }
    public Matrix4x4 WorldMatrix { get; init; }
    public Matrix4x4 NormalMatrix { get; init; }
    public Matrix4x4 ViewProjMatrix { get; init; }
    public Matrix4x4[]? ObjWorldMatrices { get; init; }
    public Matrix4x4[]? ObjNormalMatrices { get; init; }
    public ID3DEffect? ConfiguredEffect { get; init; }
    public WorldBounds Bounds { get; init; }
    public int OriginalIndex { get; init; }
    public bool IsOitCapable { get; init; }
}
