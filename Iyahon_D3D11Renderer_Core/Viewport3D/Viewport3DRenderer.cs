using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using Iyahon_D3D11Renderer_Core.Lighting;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal sealed class ViewportRenderOptions
{
    public bool ShowGrid { get; set; } = true;
    public bool ShowCameraFrustum { get; set; } = true;
    public bool ShowLights { get; set; } = true;
    public bool ShowAxisCompass { get; set; } = true;
    public bool ShowEditOverlays { get; set; } = true;
    public bool ShowObjectGizmoAndBoundsWhenEditActive { get; set; }
    public AxisCompassTarget HoveredCompassTarget { get; set; } = AxisCompassTarget.None;
    public float DpiScale { get; set; } = 1f;
}

internal sealed class Viewport3DRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct QuadVertex
    {
        public Vector3 Position;
        public Vector2 TexCoord;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OverlayVertex
    {
        public Vector3 Position;
        public Vector4 Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbPerObject
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 ViewProjMatrix;
        public Matrix4x4 NormalMatrix;
        public float HalfWidth;
        public float HalfHeight;
        public float Opacity;
        public float AlphaThreshold;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbToneMap
    {
        public float ToneMode;
        public float ExposureMult;
        public float _pad0;
        public float _pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CbOverlay
    {
        public Matrix4x4 ViewProj;
    }

    internal static readonly string QuadShaderSource = @"
cbuffer CbPerObject : register(b0)
{
    row_major float4x4 WorldMatrix;
    row_major float4x4 ViewProjMatrix;
    row_major float4x4 NormalMatrix;
    float HalfWidth;
    float HalfHeight;
    float Opacity;
    float AlphaThreshold;
};
" + LightingShaderCode.HlslCode + @"
struct VSInput { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; float3 Norm : TEXCOORD1; float Op : TEXCOORD2; float3 WorldPos : TEXCOORD3; };

Texture2D    gTex     : register(t0);
SamplerState gSampler : register(s0);

PSInput VS_VpQuad(VSInput input)
{
    PSInput o;
    float4 worldPos = mul(float4(input.Pos, 1.0), WorldMatrix);
    o.Pos = mul(worldPos, ViewProjMatrix);
    o.UV = input.UV;
    o.Norm = normalize(mul(float4(0.0, 0.0, 1.0, 0.0), NormalMatrix).xyz);
    o.Op = Opacity;
    o.WorldPos = worldPos.xyz;
    return o;
}

float4 ApplyQuadLit(float4 c, PSInput input)
{
    if (c.a <= 0.0001) return float4(0.0, 0.0, 0.0, 0.0);
    float a = c.a;
    float3 albedoLinear = SrgbToLinearUnclamped(c.rgb / a);
    float3 n = normalize(input.Norm);
    float3 toEye = CameraWorldPosAndPageSize.xyz - input.WorldPos;
    if (dot(n, toEye) < 0.0) n = -n;
    float roughness = max(DefaultRoughness, 0.72);
    float3 litLinear = (UseSimpleLight > 0.5)
        ? CalcSimpleLgtPbr(albedoLinear, n, input.WorldPos, 0.0, roughness, 0.5)
        : CalcDynamicLgtPbr(albedoLinear, n, input.WorldPos, input.Pos.xy, 0.0, roughness);
    return float4(litLinear * a, a);
}

float4 PS_VpQuadOpaque(PSInput input) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV) * input.Op;
    clip(c.a - 0.98);
    return ApplyQuadLit(c, input);
}

float4 PS_VpQuadSemi(PSInput input) : SV_Target
{
    float4 c = gTex.Sample(gSampler, input.UV) * input.Op;
    clip(c.a - 0.004);
    clip(0.98 - c.a);
    return ApplyQuadLit(c, input);
}
";

    internal const string ToneMapShaderSource = @"
cbuffer CbToneMap : register(b0)
{
    float ToneMode;
    float ExposureMult;
    float _pad0;
    float _pad1;
};

Texture2D    gTex     : register(t0);
SamplerState gSampler : register(s0);

struct VSInput { float3 Pos : POSITION; float2 UV : TEXCOORD0; };
struct PSInput { float4 Pos : SV_POSITION; float2 UV : TEXCOORD0; };

PSInput VS_VpToneMap(VSInput input)
{
    PSInput o;
    o.Pos = float4(input.Pos.xy, 0.5, 1.0);
    o.UV = input.UV;
    return o;
}

float3 ToneMapGT(float3 x)
{
    const float P = 1.0;
    const float a = 1.0;
    const float m = 0.22;
    const float l = 0.40;
    const float c = 1.33;
    float l0 = ((P - m) * l) / a;
    float S0 = m + l0;
    float S1 = m + a * l0;
    float C2 = (a * P) / max(P - S1, 1e-4);
    float CP = -C2 / max(P, 1e-4);

    float3 w0 = 1.0 - smoothstep(0.0, m, x);
    float3 w2 = step(S0, x);
    float3 w1 = 1.0 - w0 - w2;

    float3 T = m * pow(max(x / m, 1e-6), c);
    float3 L = m + a * (x - m);
    float3 S = P - (P - S1) * exp(CP * (x - S0));
    return max(T * w0 + L * w1 + S * w2, 0.0);
}

float3 ToneMapAces(float3 x)
{
    return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
}

float3 AgxContrast(float3 x)
{
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    return +15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

float3 ToneMapAgX(float3 val)
{
    const float3x3 m = float3x3(
        0.84247906, 0.04232824, 0.04237565,
        0.07843360, 0.87846864, 0.07843360,
        0.07922375, 0.07916613, 0.87914297);
    const float3x3 invM = float3x3(
         1.1968790, -0.05289685, -0.05297163,
        -0.09802088, 1.1519031,  -0.09804345,
        -0.09902974,-0.09896118,  1.1510737);
    val = mul(max(val, 1e-6), m);
    val = (clamp(log2(max(val, 1e-6)), -12.47393, 4.026069) + 12.47393) / (4.026069 + 12.47393);
    val = clamp(mul(AgxContrast(val), invM), 0.0, 1.0);
    return pow(max(val, 0.0), 2.2);
}

float LinearToSrgbCh(float x)
{
    x = saturate(x);
    return (x <= 0.0031308) ? (12.92 * x) : (1.055 * pow(x, 1.0 / 2.4) - 0.055);
}

float3 LinearToSrgb(float3 c)
{
    return float3(LinearToSrgbCh(c.r), LinearToSrgbCh(c.g), LinearToSrgbCh(c.b));
}

float4 PS_VpToneMap(PSInput input) : SV_Target
{
    float3 bgTop = float3(0.145, 0.155, 0.180);
    float3 bgBot = float3(0.095, 0.100, 0.115);
    float3 bg = lerp(bgTop, bgBot, input.UV.y);

    float4 src = gTex.SampleLevel(gSampler, input.UV, 0);
    float a = saturate(src.a);
    if (a <= 1e-4)
        return float4(bg, 1.0);

    float3 straightHdr = (max(src.rgb, 0.0) / a) * ExposureMult;
    float3 mappedLinear = straightHdr;
    if (ToneMode < 0.5) mappedLinear = ToneMapGT(straightHdr);
    else if (ToneMode < 1.5) mappedLinear = ToneMapAces(straightHdr);
    else if (ToneMode < 2.5) mappedLinear = ToneMapAgX(straightHdr);
    else mappedLinear = saturate(straightHdr);

    float3 fgSrgb = LinearToSrgb(mappedLinear);
    return float4(lerp(bg, fgSrgb, a), 1.0);
}
";

    internal const string OverlayShaderSource = @"
cbuffer CbOverlay : register(b0)
{
    row_major float4x4 ViewProj;
};

struct VSInput { float3 Pos : POSITION; float4 Color : COLOR; };
struct PSInput { float4 Pos : SV_POSITION; float4 Color : COLOR; };

PSInput VS_VpOverlay(VSInput input)
{
    PSInput o;
    o.Pos = mul(float4(input.Pos, 1.0), ViewProj);
    o.Color = input.Color;
    return o;
}

float4 PS_VpOverlay(PSInput input) : SV_Target
{
    return input.Color;
}
";

    private IntPtr _devicePtr;
    private int _width;
    private int _height;
    private bool _disposed;

    private ID3D11Texture2D? _hdrTex;
    private ID3D11RenderTargetView? _hdrRtv;
    private ID3D11ShaderResourceView? _hdrSrv;
    private ID3D11Texture2D? _refractBgTex;
    private ID3D11ShaderResourceView? _refractBgSrv;
    private float _refractBgMaxMip;

    private ID3D11VertexShader? _quadVs;
    private ID3D11PixelShader? _quadPsOpaque;
    private ID3D11PixelShader? _quadPsSemi;
    private ID3D11InputLayout? _quadLayout;
    private ID3D11Buffer? _quadVb;
    private ID3D11Buffer? _cbPerObject;
    private ID3D11SamplerState? _samplerLinear;

    private ID3D11VertexShader? _toneVs;
    private ID3D11PixelShader? _tonePs;
    private ID3D11Buffer? _fullscreenVb;
    private ID3D11Buffer? _cbToneMap;

    private ID3D11VertexShader? _ovVs;
    private ID3D11PixelShader? _ovPs;
    private ID3D11InputLayout? _ovLayout;
    private ID3D11Buffer? _cbOverlay;
    private ID3D11Buffer? _ovLineVb;
    private int _ovLineCap;
    private ID3D11Buffer? _ovTriVb;
    private int _ovTriCap;

    private ID3D11Buffer? _cbLighting;
    private readonly DynamicStructuredBuffer<GpuLightData> _lightBuffer = new();
    private readonly DynamicStructuredBuffer<GpuShadowEntry> _shadowBuffer = new();

    private ID3D11BlendState? _blendOpaque;
    private ID3D11BlendState? _blendPremul;
    private ID3D11BlendState? _blendAlpha;
    private ID3D11BlendState? _blendNoColor;
    private ID3D11DepthStencilState? _dsOpaque;
    private ID3D11DepthStencilState? _dsDepthEqual;
    private ID3D11DepthStencilState? _dsDepthReadOnly;
    private ID3D11DepthStencilState? _dsDisabled;
    private ID3D11RasterizerState? _rsSolid;

    private ObjModelRenderer? _objRenderer;
    private Model3D.Noise3DVolumeRenderer? _noise3DRenderer;
    private D3DEffect.D3DScreenOutlineHelper? _screenOutlineHelper;
    private readonly Dictionary<IVideoItem, (string Id, ID3DEffect Effect)> _d3dEffects = new();
    private long _cachedSnapshotSeq = -1;
    private Dictionary<ViewportItemEntry, (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv)> _sharedTextures = new();

    private readonly List<OverlayVertex> _gridLines = new(256);
    private readonly List<OverlayVertex> _depthLines = new(512);
    private readonly List<OverlayVertex> _depthTris = new(512);
    private readonly List<OverlayVertex> _topLines = new(512);
    private readonly List<OverlayVertex> _topTris = new(256);
    private readonly List<OverlayVertex> _ndcTris = new(1024);
    private readonly List<OverlayVertex> _ndcLines = new(32);

    public void Render(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11RenderTargetView backBufferRtv,
        ID3D11DepthStencilView dsv,
        int width,
        int height,
        ViewportCamera camera,
        ViewportSceneSnapshot? snapshot,
        ViewportManipulator manipulator,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportRenderOptions options)
        => RenderWithEditSession(device, ctx, backBufferRtv, dsv, width, height, camera, snapshot, manipulator, selectedItems, options, null);

    public void RenderWithEditSession(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11RenderTargetView backBufferRtv,
        ID3D11DepthStencilView dsv,
        int width,
        int height,
        ViewportCamera camera,
        ViewportSceneSnapshot? snapshot,
        ViewportManipulator manipulator,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportRenderOptions options,
        Modeling.MeshEditSession? editSession)
        => RenderWithSceneTools(device, ctx, backBufferRtv, dsv, width, height, camera, snapshot, manipulator, selectedItems, options, editSession);

    public void RenderWithSceneTools(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11RenderTargetView backBufferRtv,
        ID3D11DepthStencilView dsv,
        int width,
        int height,
        ViewportCamera camera,
        ViewportSceneSnapshot? snapshot,
        ViewportManipulator manipulator,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportRenderOptions options,
        Modeling.MeshEditSession? editSession,
        ViewportCameraPathController? cameraPathCtrl = null,
        CameraPath.D3DCameraPathVideoEffect? activeCameraPath = null,
        ViewportBezierCurveController? bezierCurveCtrl = null,
        Model3D.D3DPrimitiveShapeParameter? activeBezierParam = null)
    {
        if (_disposed || width <= 0 || height <= 0) return;

        EnsureResources(device, width, height);
        if (_hdrRtv == null || _hdrSrv == null) return;

        SyncSnapshotSharedTextures(device, ctx, snapshot);

        float aspect = (float)width / height;
        Matrix4x4 viewMat = camera.GetViewMatrix();
        Matrix4x4 viewProj = camera.GetViewProjectionMatrix(aspect);
        Vector3 camPos = camera.CameraPosition;
        camera.GetCameraAxes(out _, out _, out var camFwd);

        if (editSession is { IsActive: true })
        {
            editSession.UpdateWorldTransform(snapshot, manipulator);
        }

        ctx.ClearRenderTargetView(_hdrRtv, new Color4(0f, 0f, 0f, 0f));
        ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1.0f, 0);
        ctx.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        ctx.RSSetState(_rsSolid);

        if (snapshot != null)
        {
            BindLighting(device, ctx, snapshot, manipulator, camPos, camFwd, in viewMat);
            RenderSceneItems(device, ctx, dsv, snapshot, manipulator, viewProj, camPos, camFwd, editSession);
        }

        UnbindCommonSrvs(ctx);

        RenderToneMapPass(ctx, backBufferRtv);

        // Depth-tested 3D overlays
        ctx.OMSetRenderTargets(backBufferRtv, dsv);
        ctx.OMSetBlendState(_blendAlpha, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_dsDepthReadOnly, 0);
        ctx.RSSetState(_rsSolid);
        DrawDepthTestedOverlays(
            device, ctx, width, height, viewProj, camera, snapshot, manipulator, selectedItems, options, editSession,
            cameraPathCtrl, activeCameraPath, bezierCurveCtrl, activeBezierParam);

        // Top-most 3D overlays (Transform Gizmo + Top-right Axis Compass + Box Marquee)
        ctx.OMSetRenderTargets(backBufferRtv, (ID3D11DepthStencilView?)null);
        ctx.OMSetDepthStencilState(_dsDisabled, 0);
        DrawTopMostOverlays(device, ctx, width, height, viewProj, camera, snapshot, manipulator, selectedItems, options, editSession);

        ctx.PSSetShaderResource(0, null!);
        ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
    }

    private void RenderSceneItems(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11DepthStencilView dsv,
        ViewportSceneSnapshot snapshot,
        ViewportManipulator manipulator,
        in Matrix4x4 viewProj,
        Vector3 camPos,
        Vector3 camFwd,
        Modeling.MeshEditSession? editSession)
    {
        int maxTexSize = (int)D3D11RendererSettings.Default.ModelMaxTextureSize;
        int count = snapshot.Items.Count;

        Shading.LayerTextureRegistry.BeginFrame(device);
        for (int i = 0; i < count; i++)
        {
            var entry = snapshot.Items[i];
            if (_sharedTextures.TryGetValue(entry, out var texPair) && texPair.Srv != null)
            {
                Shading.LayerTextureRegistry.Register(device, entry.Layer, texPair.Srv);
            }
        }

        var resolvedModels = new ObjModelData?[count];
        var effDescs = new DrawDescription[count];
        var quadZOffsets = new float[count];
        var drawOrder = new int[count];
        int quadOrdinal = 0;

        var liveEmbedded = new string[count];
        var liveModData = new string[count];
        var modelWorlds = new Matrix4x4[count];
        bool anySceneDependentMod = editSession is { IsActive: true } &&
                                    editSession.Modifiers.HasSceneDependentEnabledModifiers;

        for (int i = 0; i < count; i++)
        {
            var entry = snapshot.Items[i];
            effDescs[i] = manipulator.GetEffectiveDrawDescription(entry);
            if (entry.Kind == ViewportItemKind.BuiltInModel && entry.ModelSpec != null)
            {
                ResolveLiveModelStrings(entry, out liveEmbedded[i], out liveModData[i]);
                modelWorlds[i] = ComputeEntryModelWorldMatrix(entry, effDescs[i], manipulator);
                if (!anySceneDependentMod && Modifiers.MeshModifierStack.HasSceneDependentModifiers(liveModData[i]))
                    anySceneDependentMod = true;
            }
        }

        Modifiers.SceneModifierContext? sceneCtx = null;
        if (anySceneDependentMod)
        {
            sceneCtx = new Modifiers.SceneModifierContext();
            for (int i = 0; i < count; i++)
            {
                var entry = snapshot.Items[i];
                if (entry.Kind != ViewportItemKind.BuiltInModel || entry.ModelSpec == null) continue;

                bool isEdit = editSession is { IsActive: true } && ReferenceEquals(editSession.TargetItem, entry.Item);
                if (isEdit)
                {
                    var capturedSession = editSession!;
                    sceneCtx.RegisterTarget(new Modifiers.SceneModifierTarget
                    {
                        Layer = entry.Layer,
                        DisplayName = entry.DisplayName,
                        BaseKey = $"edit:{entry.Item.GetHashCode()}:{capturedSession.Mesh.Vertices.Count}:{capturedSession.Mesh.Faces.Count}",
                        ModelToWorld = capturedSession.ModelToWorld,
                        ResolveBaseMesh = () => capturedSession.Mesh.Clone(),
                        ModifierStackData = capturedSession.Modifiers.Serialize(),
                    });
                }
                else
                {
                    var sp = entry.ModelSpec;
                    string emb = liveEmbedded[i];
                    string baseKey = !string.IsNullOrWhiteSpace(emb)
                        ? $"emb:{Model3D.ModelGpuCache.ComputeStringHash64(emb)}:{emb.Length}:{sp.FilePath}"
                        : (!string.IsNullOrWhiteSpace(sp.ProceduralCacheKey)
                            ? $"proc:{sp.ProceduralCacheKey}"
                            : $"file:{sp.FilePath}");

                    sceneCtx.RegisterTarget(new Modifiers.SceneModifierTarget
                    {
                        Layer = entry.Layer,
                        DisplayName = entry.DisplayName,
                        BaseKey = baseKey,
                        ModelToWorld = modelWorlds[i],
                        ResolveBaseMesh = () => Model3D.ModelGpuCache.GetOrLoadBaseEditableMesh(
                            emb, sp.FilePath, sp.ProceduralMeshFactory, out _),
                        ModifierStackData = liveModData[i],
                    });
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            drawOrder[i] = i;
            var entry = snapshot.Items[i];

            if (entry.Kind == ViewportItemKind.BuiltInModel && entry.ModelSpec != null)
            {
                var evalTime = new Shading.ShaderEvalTime(entry.ItemFrame, entry.ItemLength, entry.Fps);
                Model3D.CachedGpuModel? cached;
                if (editSession is { IsActive: true } && ReferenceEquals(editSession.TargetItem, entry.Item))
                {
                    cached = editSession.GetOrBuildGpuModelWithSceneAtTime(device, ctx, evalTime, sceneCtx, entry.Layer);
                }
                else if (Modifiers.MeshModifierStack.HasAnyEnabledModifiers(liveModData[i]))
                {
                    cached = Model3D.ModelGpuCache.GetOrBuildWithModifiersAtTime(
                        device, ctx,
                        liveEmbedded[i],
                        entry.ModelSpec.FilePath,
                        entry.ModelSpec.ProceduralCacheKey,
                        entry.ModelSpec.ProceduralMeshFactory,
                        liveModData[i],
                        sceneCtx,
                        entry.Layer,
                        modelWorlds[i],
                        maxTexSize,
                        evalTime);
                }
                else if (!string.IsNullOrWhiteSpace(liveEmbedded[i]))
                {
                    cached = Model3D.ModelGpuCache.GetOrBuildFromEditableMeshAtTime(
                        device, ctx, liveEmbedded[i], entry.ModelSpec.FilePath, maxTexSize, evalTime);
                }
                else if (!string.IsNullOrWhiteSpace(entry.ModelSpec.ProceduralCacheKey) &&
                         entry.ModelSpec.ProceduralMeshFactory != null)
                {
                    cached = Model3D.ModelGpuCache.GetOrBuildProceduralMesh(
                        device, entry.ModelSpec.ProceduralCacheKey, entry.ModelSpec.ProceduralMeshFactory);
                }
                else
                {
                    cached = Model3D.ModelGpuCache.GetOrLoad(device, ctx, entry.ModelSpec.FilePath, maxTexSize);
                }
                if (cached != null)
                    resolvedModels[i] = manipulator.CreateEffectiveModelData(entry, cached);
            }
            else if (entry.Kind == ViewportItemKind.Quad2D)
            {
                // Keep coplanar YMM4 layers separated slightly along +Z to prevent depth-buffer Z-fighting.
                quadZOffsets[i] = quadOrdinal++ * 0.05f;
            }
        }

        for (int pass = 0; pass < 2; pass++)
        {
            bool semiPass = pass == 1;
            if (semiPass && count > 1)
            {
                Array.Sort(drawOrder, (a, b) =>
                {
                    float da = Vector3.Dot(effDescs[a].Draw - camPos, camFwd);
                    float db = Vector3.Dot(effDescs[b].Draw - camPos, camFwd);
                    int cmp = db.CompareTo(da);
                    if (cmp != 0) return cmp;
                    int layerCmp = snapshot.Items[a].Layer.CompareTo(snapshot.Items[b].Layer);
                    return layerCmp != 0 ? layerCmp : a.CompareTo(b);
                });
            }

            ctx.OMSetRenderTargets(_hdrRtv!, dsv);
            ctx.OMSetBlendState(semiPass ? _blendPremul : _blendOpaque, null, unchecked((int)0xFFFFFFFF));
            ctx.OMSetDepthStencilState(semiPass ? _dsDepthReadOnly : _dsOpaque, 0);
            ctx.RSSetState(_rsSolid);

            for (int ord = 0; ord < count; ord++)
            {
                int i = drawOrder[ord];
                var entry = snapshot.Items[i];
                if (entry.Kind == ViewportItemKind.Light) continue;
                if (entry.Outline.Enabled && entry.Outline.OutlineOnly) continue;

                var effDesc = effDescs[i];
                if (entry.Opacity <= 0.001f) continue;

                int instCount = entry.RandomInstanceDrawDescs != null ? entry.RandomInstanceDrawDescs.Count : 1;
                if (instCount <= 0) continue;

                Vector3 dragDeltaDraw = effDesc.Draw - entry.DrawDescription.Draw;
                Vector3 dragDeltaRot = effDesc.Rotation - entry.DrawDescription.Rotation;
                Vector2 dragScaleRatio = new(
                    entry.DrawDescription.Zoom.X != 0 ? (float)(effDesc.Zoom.X / entry.DrawDescription.Zoom.X) : 1f,
                    entry.DrawDescription.Zoom.Y != 0 ? (float)(effDesc.Zoom.Y / entry.DrawDescription.Zoom.Y) : 1f);

                for (int inst = 0; inst < instCount; inst++)
                {
                    var instDesc = entry.RandomInstanceDrawDescs == null
                        ? effDesc
                        : entry.RandomInstanceDrawDescs[inst] with
                        {
                            Draw = entry.RandomInstanceDrawDescs[inst].Draw + dragDeltaDraw,
                            Rotation = entry.RandomInstanceDrawDescs[inst].Rotation + dragDeltaRot,
                            Zoom = new Vector2(
                                entry.RandomInstanceDrawDescs[inst].Zoom.X * dragScaleRatio.X,
                                entry.RandomInstanceDrawDescs[inst].Zoom.Y * dragScaleRatio.Y),
                            Camera = effDesc.Camera,
                        };

                    if (entry.Kind == ViewportItemKind.BuiltInModel && resolvedModels[i] is { } modelData)
                    {
                        var ri = new RenderItem { DrawDescription = instDesc, Srv = null };
                        var world = DepthSortRenderer.BuildObjModelMatrix(ri, modelData);
                        var normal = MatrixTransformHelper.ComputeNormalMatrix(world);

                        if (modelData.Noise3D.Enabled)
                        {
                            bool isSemiNoise = modelData.Noise3D.IsSemiTransparent;
                            if (isSemiNoise == semiPass)
                            {
                                _noise3DRenderer ??= new Model3D.Noise3DVolumeRenderer();
                                _noise3DRenderer.Initialize(device, ctx);
                                _noise3DRenderer.Render(
                                    device, ctx, _hdrRtv!, dsv, _width, _height,
                                    modelData, world, normal, viewProj, camPos, 0f, 0f, entry.Opacity);
                                ctx.OMSetRenderTargets(_hdrRtv!, dsv);
                                ctx.OMSetBlendState(semiPass ? _blendPremul : _blendOpaque, null, unchecked((int)0xFFFFFFFF));
                                ctx.OMSetDepthStencilState(semiPass ? _dsDepthReadOnly : _dsOpaque, 0);
                                ctx.RSSetState(_rsSolid);
                            }
                            continue;
                        }

                        _objRenderer ??= new ObjModelRenderer();
                        _objRenderer.Initialize(device, ctx);
                        var mat = entry.Material;
                        ResolveMaterialSrvs(device, ctx, in mat, out var matNormSrv, out var matRoughSrv);

                        _objRenderer.RenderModel(
                            device, ctx, modelData, world, normal, viewProj,
                            0f, 0f, entry.Opacity,
                            semiPass ? 0.004f : 0.999f,
                            semiPass ? ObjRenderMode.SemiTransColor : ObjRenderMode.Opaque,
                            in mat, matNormSrv, matRoughSrv);

                        ctx.RSSetState(_rsSolid);
                        continue;
                    }

                    if (!_sharedTextures.TryGetValue(entry, out var texPair)) continue;

                    var biasedDesc = quadZOffsets[i] > 0f
                        ? instDesc with { Draw = new Vector3(instDesc.Draw.X, instDesc.Draw.Y, instDesc.Draw.Z + quadZOffsets[i]) }
                        : instDesc;

                    var itemRi = new RenderItem
                    {
                        DrawDescription = biasedDesc,
                        Srv = texPair.Srv,
                        PixelWidth = entry.PixelWidth,
                        PixelHeight = entry.PixelHeight,
                        BoundsCenterX = entry.BoundsCenterX,
                        BoundsCenterY = entry.BoundsCenterY,
                        Opacity = entry.Opacity,
                    };
                    var itemWorld = DepthSortRenderer.BuildModelMatrix(itemRi);
                    var itemNormal = MatrixTransformHelper.ComputeNormalMatrix(itemWorld);

                    if (entry.Kind == ViewportItemKind.D3DEffect && entry.D3DVideoEffect != null)
                    {
                        var eff = GetOrCreateEffect(entry.Item, entry.D3DEffectId ?? string.Empty);
                        if (eff != null)
                        {
                            if (semiPass && eff is not IOitCapableD3DEffect) continue;
                            eff.Initialize(device, ctx);
                            entry.D3DVideoEffect.ConfigureEffect(eff, entry.ItemFrame, entry.ItemLength, entry.Fps);

                            var rCtx = new D3DRenderContext
                            {
                                WorldMatrix = itemWorld,
                                NormalMatrix = itemNormal,
                                ViewProjectionMatrix = viewProj,
                                CameraWorldPosition = camPos,
                                TextureWidth = (int)entry.PixelWidth,
                                TextureHeight = (int)entry.PixelHeight,
                                HalfScreenWidth = 0f,
                                HalfScreenHeight = 0f,
                                Opacity = entry.Opacity,
                                AlphaThreshold = semiPass ? 0.004f : 0.98f,
                                CameraMatrix = instDesc.Camera,
                                IsSemiTransPass = semiPass,
                            };
                            eff.Render(ctx, device, texPair.Srv, rCtx);
                            ctx.RSSetState(_rsSolid);
                            ctx.OMSetBlendState(semiPass ? _blendPremul : _blendOpaque, null, unchecked((int)0xFFFFFFFF));
                            ctx.OMSetDepthStencilState(semiPass ? _dsDepthReadOnly : _dsOpaque, 0);
                            continue;
                        }
                    }

                    DrawQuad2D(ctx, texPair.Srv, itemWorld, itemNormal, viewProj, entry.Opacity, semiPass);
                }
            }

            if (pass == 0)
            {
                RenderViewportOutlines(device, ctx, dsv, resolvedModels, effDescs, snapshot, viewProj, camPos);
        RenderTransmissiveViewportPass(device, ctx, dsv, resolvedModels, effDescs, snapshot, viewProj);
            }
        }
    }

    private static void ResolveMaterialSrvs(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        in MaterialOverrideParameters mat,
        out ID3D11ShaderResourceView? normSrv,
        out ID3D11ShaderResourceView? roughSrv)
    {
        normSrv = (mat.Enabled && !string.IsNullOrEmpty(mat.NormalMapPath))
            ? Model3D.MaterialTextureCache.GetOrLoadSrv(device, ctx, mat.NormalMapPath)
            : null;
        roughSrv = (mat.Enabled && !string.IsNullOrEmpty(mat.RoughnessMapPath))
            ? Model3D.MaterialTextureCache.GetOrLoadSrv(device, ctx, mat.RoughnessMapPath)
            : null;
    }

    private void RenderTransmissiveViewportPass(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11DepthStencilView dsv,
        ObjModelData?[] resolvedModels,
        DrawDescription[] effDescs,
        ViewportSceneSnapshot snapshot,
        in Matrix4x4 viewProj)
    {
        bool anyTrans = false;
        for (int i = 0; i < resolvedModels.Length; i++)
        {
            var m = resolvedModels[i];
            var mat = snapshot.Items[i].Material;
            if (m != null && ObjModelRenderer.HasTransmissiveParts(m, in mat))
            {
                anyTrans = true;
                break;
            }
        }
        if (!anyTrans || _hdrTex == null || _hdrRtv == null) return;

        EnsureRefractBgTarget(device, _width, _height);
        if (_refractBgTex == null || _refractBgSrv == null) return;

        ctx.CopySubresourceRegion(_refractBgTex, 0, 0, 0, 0, _hdrTex, 0, null);
        ctx.GenerateMips(_refractBgSrv);

        if (_objRenderer == null) _objRenderer = new ObjModelRenderer();
        _objRenderer.Initialize(device, ctx);

        ctx.OMSetRenderTargets(_hdrRtv, dsv);
        ctx.RSSetState(_rsSolid);

        ctx.OMSetBlendState(_blendNoColor, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_dsOpaque, 0);
        for (int i = 0; i < resolvedModels.Length; i++)
        {
            var modelData = resolvedModels[i];
            if (modelData == null) continue;
            var entry = snapshot.Items[i];
            var mat = entry.Material;
            if (entry.Opacity <= 0.001f || !ObjModelRenderer.HasTransmissiveParts(modelData, in mat)) continue;
            var ri = new RenderItem { DrawDescription = effDescs[i], Srv = null };
            var world = DepthSortRenderer.BuildObjModelMatrix(ri, modelData);
            var normal = MatrixTransformHelper.ComputeNormalMatrix(world);
            _objRenderer.RenderModel(device, ctx, modelData, world, normal, viewProj, 0f, 0f, entry.Opacity, 0.004f, ObjRenderMode.TransmissiveDepthOnly, in mat, null, null);
        }

        ctx.OMSetBlendState(_blendPremul, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_dsDepthEqual, 0);
        for (int i = 0; i < resolvedModels.Length; i++)
        {
            var modelData = resolvedModels[i];
            if (modelData == null) continue;
            var entry = snapshot.Items[i];
            var mat = entry.Material;
            if (entry.Opacity <= 0.001f || !ObjModelRenderer.HasTransmissiveParts(modelData, in mat)) continue;
            var ri = new RenderItem { DrawDescription = effDescs[i], Srv = null };
            var world = DepthSortRenderer.BuildObjModelMatrix(ri, modelData);
            var normal = MatrixTransformHelper.ComputeNormalMatrix(world);
            ResolveMaterialSrvs(device, ctx, in mat, out var matNormSrv, out var matRoughSrv);
            _objRenderer.RenderModel(
                device, ctx, modelData, world, normal, viewProj, 0f, 0f, entry.Opacity, 0.004f,
                ObjRenderMode.Transmissive, in mat, matNormSrv, matRoughSrv,
                _refractBgSrv, _width, _height, _refractBgMaxMip);
        }

        ctx.PSSetShaderResource(8, null!);
        ctx.PSSetShaderResource(9, null!);
        ctx.RSSetState(_rsSolid);
    }

    private void RenderViewportOutlines(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11DepthStencilView dsv,
        ObjModelData?[] resolvedModels,
        DrawDescription[] effDescs,
        ViewportSceneSnapshot snapshot,
        in Matrix4x4 viewProj,
        Vector3 camPos)
    {
        for (int i = 0; i < snapshot.Items.Count; i++)
        {
            var entry = snapshot.Items[i];
            if (entry.Kind == ViewportItemKind.Light || entry.Opacity <= 0.001f) continue;
            var ol = entry.Outline;
            if (!ol.Enabled || ol.Thickness <= 0.001f || ol.Opacity <= 0.001f) continue;

            int instCount = entry.RandomInstanceDrawDescs != null ? entry.RandomInstanceDrawDescs.Count : 1;
            if (instCount <= 0) continue;

            var effDesc = effDescs[i];
            Vector3 dragDeltaDraw = effDesc.Draw - entry.DrawDescription.Draw;
            Vector3 dragDeltaRot = effDesc.Rotation - entry.DrawDescription.Rotation;
            Vector2 dragScaleRatio = new(
                entry.DrawDescription.Zoom.X != 0 ? (float)(effDesc.Zoom.X / entry.DrawDescription.Zoom.X) : 1f,
                entry.DrawDescription.Zoom.Y != 0 ? (float)(effDesc.Zoom.Y / entry.DrawDescription.Zoom.Y) : 1f);

            var modelData = entry.Kind == ViewportItemKind.BuiltInModel ? resolvedModels[i] : null;
            bool isModel = modelData != null;
            bool isNoise3D = modelData?.Noise3D.Enabled == true;
            bool useHull = isModel && !isNoise3D && ol.Mode != D3DEffect.Effects.D3DOutlineMode.ScreenSpace;

            for (int inst = 0; inst < instCount; inst++)
            {
                var instDesc = entry.RandomInstanceDrawDescs == null
                    ? effDesc
                    : entry.RandomInstanceDrawDescs[inst] with
                    {
                        Draw = entry.RandomInstanceDrawDescs[inst].Draw + dragDeltaDraw,
                        Rotation = entry.RandomInstanceDrawDescs[inst].Rotation + dragDeltaRot,
                        Zoom = new Vector2(
                            entry.RandomInstanceDrawDescs[inst].Zoom.X * dragScaleRatio.X,
                            entry.RandomInstanceDrawDescs[inst].Zoom.Y * dragScaleRatio.Y),
                        Camera = effDesc.Camera,
                    };

                if (useHull && modelData != null)
                {
                    _objRenderer ??= new ObjModelRenderer();
                    _objRenderer.Initialize(device, ctx);
                    var world = DepthSortRenderer.BuildObjModelMatrix(new RenderItem { DrawDescription = instDesc, Srv = null }, modelData);
                    var normal = MatrixTransformHelper.ComputeNormalMatrix(world);

                    if (ol.OutlineOnly)
                    {
                        ctx.OMSetRenderTargets(_hdrRtv!, dsv);
                        ctx.OMSetBlendState(_blendNoColor, null, unchecked((int)0xFFFFFFFF));
                        ctx.OMSetDepthStencilState(_dsOpaque, 0);
                        var mat = entry.Material;
                        _objRenderer.RenderModel(
                            device, ctx, modelData, world, normal, viewProj,
                            0f, 0f, entry.Opacity, 0.999f, ObjRenderMode.Opaque, in mat, null, null);
                    }

                    ctx.OMSetRenderTargets(_hdrRtv!, dsv);
                    ctx.OMSetBlendState(_blendPremul, null, unchecked((int)0xFFFFFFFF));
                    ctx.OMSetDepthStencilState(_dsDepthReadOnly, 0);
                    _objRenderer.RenderOutline(
                        device, ctx, modelData, world, normal, viewProj,
                        0f, 0f, _width * 0.5f, _height * 0.5f, entry.Opacity, in ol);
                    continue;
                }

                // Screen-space outline
                _screenOutlineHelper ??= new D3DEffect.D3DScreenOutlineHelper();
                _screenOutlineHelper.Initialize(device);
                if (!_screenOutlineHelper.BeginMaskPass(device, ctx, dsv, _width, _height))
                    continue;

                if (isModel && modelData != null)
                {
                    var world = DepthSortRenderer.BuildObjModelMatrix(new RenderItem { DrawDescription = instDesc, Srv = null }, modelData);
                    var normal = MatrixTransformHelper.ComputeNormalMatrix(world);
                    if (isNoise3D)
                    {
                        _noise3DRenderer ??= new Model3D.Noise3DVolumeRenderer();
                        _noise3DRenderer.Initialize(device, ctx);
                        _noise3DRenderer.Render(
                            device, ctx, _screenOutlineHelper.MaskRtv!, dsv, _width, _height,
                            modelData, world, normal, viewProj, camPos, 0f, 0f, entry.Opacity);
                    }
                    else
                    {
                        _objRenderer ??= new ObjModelRenderer();
                        _objRenderer.Initialize(device, ctx);
                        var mat = entry.Material;
                        _objRenderer.RenderModel(
                            device, ctx, modelData, world, Matrix4x4.Identity, viewProj,
                            0f, 0f, entry.Opacity, 0.004f, ObjRenderMode.Silhouette, in mat, null, null);
                    }
                }
                else if (_sharedTextures.TryGetValue(entry, out var texPair))
                {
                    var itemRi = new RenderItem
                    {
                        DrawDescription = instDesc,
                        Srv = texPair.Srv,
                        PixelWidth = entry.PixelWidth,
                        PixelHeight = entry.PixelHeight,
                        BoundsCenterX = entry.BoundsCenterX,
                        BoundsCenterY = entry.BoundsCenterY,
                        Opacity = entry.Opacity,
                    };
                    var itemWorld = DepthSortRenderer.BuildModelMatrix(itemRi);
                    var itemNormal = MatrixTransformHelper.ComputeNormalMatrix(itemWorld);

                    if (entry.Kind == ViewportItemKind.D3DEffect && entry.D3DVideoEffect != null)
                    {
                        var eff = GetOrCreateEffect(entry.Item, entry.D3DEffectId ?? string.Empty);
                        if (eff != null)
                        {
                            eff.Initialize(device, ctx);
                            entry.D3DVideoEffect.ConfigureEffect(eff, entry.ItemFrame, entry.ItemLength, entry.Fps);
                            var rCtx = new D3DRenderContext
                            {
                                WorldMatrix = itemWorld,
                                NormalMatrix = itemNormal,
                                ViewProjectionMatrix = viewProj,
                                CameraWorldPosition = camPos,
                                TextureWidth = (int)entry.PixelWidth,
                                TextureHeight = (int)entry.PixelHeight,
                                HalfScreenWidth = 0f,
                                HalfScreenHeight = 0f,
                                Opacity = entry.Opacity,
                                AlphaThreshold = 0.004f,
                                CameraMatrix = instDesc.Camera,
                                IsSemiTransPass = false,
                                Material = entry.Material,
                            };
                            eff.Render(ctx, device, texPair.Srv, rCtx);
                        }
                    }
                    else
                    {
                        _screenOutlineHelper.DrawQuadSilhouette(
                            ctx, texPair.Srv, itemWorld, viewProj, 0f, 0f, entry.Opacity, 0.004f);
                    }
                }

                _screenOutlineHelper.CompositeOutline(ctx, _hdrRtv!, dsv, _width, _height, in ol);
            }
        }

        ctx.OMSetRenderTargets(_hdrRtv!, dsv);
        ctx.OMSetBlendState(_blendOpaque, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_dsOpaque, 0);
        ctx.RSSetState(_rsSolid);
    }

    private static void ResolveLiveModelStrings(ViewportItemEntry entry, out string embedded, out string modData)
    {
        if (entry.Item is ShapeItem si && si.ShapeParameter is Model3D.ID3DModelTransformParameter lp)
        {
            embedded = lp.EmbeddedMeshData ?? string.Empty;
            modData = lp.ModifierStackData ?? string.Empty;
            return;
        }
        embedded = entry.ModelSpec?.EmbeddedMeshData ?? string.Empty;
        modData = entry.ModelSpec?.ModifierStackData ?? string.Empty;
    }

    private static Matrix4x4 ComputeEntryModelWorldMatrix(
        ViewportItemEntry entry, in DrawDescription effDesc, ViewportManipulator manipulator)
    {
        var sp = entry.ModelSpec!;
        Vector3 localScale = sp.LocalScale;
        Vector3 localRotDeg = sp.LocalRotationDeg;
        if (manipulator.GetLiveOverride(entry.Item) is { } ov)
        {
            localScale = ViewportManipulator.ComputeEffectiveLocalScale(sp, ov);
            localRotDeg = ov.ModelLocalRotDeg ?? sp.LocalRotationDeg;
        }

        var temp = new ObjModelData
        {
            VertexBuffer = null!,
            IndexBuffer = null!,
            Parts = Array.Empty<ObjPartRenderInfo>(),
            BoundsMin = sp.BoundsMin,
            BoundsMax = sp.BoundsMax,
            ModelCenter = sp.ModelCenter,
            ModelScale = sp.ModelScale,
            NormalizeSize = sp.NormalizeSize,
            GroundToOrigin = sp.GroundToOrigin,
            UnitScale = sp.UnitScale,
            LocalScale = localScale,
            LocalRotationDeg = localRotDeg,
        };
        return DepthSortRenderer.BuildObjModelMatrix(new RenderItem { DrawDescription = effDesc, Srv = null }, temp);
    }

    private void DrawQuad2D(
        ID3D11DeviceContext ctx,
        ID3D11ShaderResourceView srv,
        in Matrix4x4 world,
        in Matrix4x4 normal,
        in Matrix4x4 viewProj,
        float opacity,
        bool semiPass)
    {
        ctx.IASetInputLayout(_quadLayout);
        ctx.IASetVertexBuffer(0, _quadVb!, Marshal.SizeOf<QuadVertex>(), 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(_quadVs);
        ctx.PSSetShader(semiPass ? _quadPsSemi : _quadPsOpaque);
        ctx.PSSetSampler(0, _samplerLinear);

        var cb = new CbPerObject
        {
            WorldMatrix = world,
            ViewProjMatrix = viewProj,
            NormalMatrix = normal,
            HalfWidth = 0f,
            HalfHeight = 0f,
            Opacity = opacity,
            AlphaThreshold = semiPass ? 0.004f : 0.98f,
        };
        ctx.UpdateSubresource(in cb, _cbPerObject!);
        ctx.VSSetConstantBuffer(0, _cbPerObject);
        ctx.PSSetConstantBuffer(0, _cbPerObject);
        ctx.PSSetShaderResource(0, srv);
        ctx.Draw(4, 0);
    }

    private void BindLighting(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ViewportSceneSnapshot snapshot,
        ViewportManipulator manipulator,
        Vector3 camPos,
        Vector3 camFwd,
        in Matrix4x4 camViewMat)
    {
        var activeLights = new List<GpuLightData>();
        for (int i = 0; i < snapshot.Items.Count; i++)
        {
            var e = snapshot.Items[i];
            if (e.Kind != ViewportItemKind.Light) continue;
            var effDesc = manipulator.GetEffectiveDrawDescription(e);
            var ld = manipulator.GetEffectiveLight(e, effDesc);
            var (_, areaRight, _) = LightManager.ComputeAreaBasis(ld);
            activeLights.Add(GpuLightData.FromLightData(ld, areaRight, -1, 0, 0));
        }

        for (int i = 0; i < snapshot.Lights.Count; i++)
        {
            var ld = snapshot.Lights[i];
            if (snapshot.Items.Any(x => x.Kind == ViewportItemKind.Light && x.Light == ld))
                continue;
            var (_, areaRight, _) = LightManager.ComputeAreaBasis(ld);
            activeLights.Add(GpuLightData.FromLightData(ld, areaRight, -1, 0, 0));
        }

        var settings = D3D11RendererSettings.Default;
        var env = snapshot.Environment;
        string? envPath = env.OverrideEnvironmentMap ? env.EnvironmentMapPath : settings.EnvironmentMapPath;
        float envRotDeg = env.OverrideEnvironmentMap ? env.RotationDeg : (float)settings.EnvironmentRotation;
        float envExposure = env.OverrideEnvironmentMap ? env.Exposure : (float)settings.EnvironmentExposure;
        float envRefl = env.OverrideEnvironmentMap
            ? (env.ReflectionIntensity ?? 1.0f)
            : (float)settings.EnvironmentReflectionIntensity;
        float iblInt = env.OverrideIblAndAmbient ? env.IblIntensity : (float)settings.IblIntensity;
        float ambInt = env.OverrideIblAndAmbient ? env.AmbientIntensity : (float)settings.AmbientIntensity;
        Vector3 skyColorLin = env.OverrideIblAndAmbient ? env.SkyColorLinear : new Vector3(0.68f, 0.76f, 0.90f);

        bool hasEnvMap = EnvironmentMapManager.TryGetEnvMap(
            device, ctx, envPath, out var envSrv, out float envMaxMip);
        var envSampler = EnvironmentMapManager.GetOrCreateEnvSampler(device);

        var header = new CbLightingHeader
        {
            LightCount = activeLights.Count,
            ShadowEntryCount = 0,
            UseSimpleLight = activeLights.Count > 0 ? 0f : 1f,
            EnableShadow = 0f,
            AmbientIntensity = ambInt,
            EnableSoftShadow = 0f,
            ShadowQuality = 0,
            ShadowSoftness = 1f,
            AmbientColor = new Vector4(0.28f, 0.27f, 0.25f, 1.0f),
            CameraWorldPosAndPageSize = new Vector4(camPos, 1024f),
            CameraForwardAndRcpPageSize = new Vector4(camFwd, 1f / 1024f),
            EnablePbr = settings.EnablePbr ? 1f : 0f,
            IblIntensity = iblInt,
            DefaultRoughness = (float)settings.DefaultRoughness,
            ShadowNoiseScale = 1f,
            SkyColorAndGroundMix = new Vector4(skyColorLin, 1.0f),
            CamBasisX = new Vector4(camViewMat.M11, camViewMat.M12, camViewMat.M13, hasEnvMap ? 1f : 0f),
            CamBasisY = new Vector4(camViewMat.M21, camViewMat.M22, camViewMat.M23, envRotDeg * (MathF.PI / 180f)),
            CamBasisZ = new Vector4(camViewMat.M31, camViewMat.M32, camViewMat.M33, hasEnvMap ? envMaxMip : 0f),
            EnvParams = new Vector4(envExposure, MathF.Max(envRefl, 0f), 1f, 0f),
        };

        ctx.UpdateSubresource(in header, _cbLighting!);
        ctx.VSSetConstantBuffer(1, _cbLighting);
        ctx.PSSetConstantBuffer(1, _cbLighting);

        _lightBuffer.Update(device, ctx, CollectionsMarshal.AsSpan(activeLights));
        _shadowBuffer.Update(device, ctx, ReadOnlySpan<GpuShadowEntry>.Empty);

        ctx.PSSetShaderResource(2, null!);
        ctx.PSSetShaderResource(3, _lightBuffer.ShaderResourceView!);
        ctx.PSSetShaderResource(4, _shadowBuffer.ShaderResourceView!);
        ctx.PSSetShaderResource(5, hasEnvMap ? envSrv! : null!);
        if (envSampler != null)
            ctx.PSSetSampler(3, envSampler);
    }

    private void RenderToneMapPass(ID3D11DeviceContext ctx, ID3D11RenderTargetView backBufferRtv)
    {
        var settings = D3D11RendererSettings.Default;
        var cb = new CbToneMap
        {
            ToneMode = (float)(int)settings.ToneMapping,
            ExposureMult = MathF.Pow(2f, (float)settings.Exposure),
        };
        ctx.UpdateSubresource(in cb, _cbToneMap!);

        ctx.OMSetRenderTargets(backBufferRtv, (ID3D11DepthStencilView?)null);
        ctx.OMSetBlendState(_blendOpaque, null, unchecked((int)0xFFFFFFFF));
        ctx.OMSetDepthStencilState(_dsDisabled, 0);
        ctx.IASetInputLayout(_quadLayout);
        ctx.IASetVertexBuffer(0, _fullscreenVb!, Marshal.SizeOf<QuadVertex>(), 0);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(_toneVs);
        ctx.PSSetShader(_tonePs);
        ctx.PSSetConstantBuffer(0, _cbToneMap);
        ctx.PSSetSampler(0, _samplerLinear);
        ctx.PSSetShaderResource(0, _hdrSrv!);
        ctx.Draw(4, 0);
        ctx.PSSetShaderResource(0, null!);
    }

    private void DrawDepthTestedOverlays(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        int width,
        int height,
        in Matrix4x4 viewProj,
        ViewportCamera camera,
        ViewportSceneSnapshot? snapshot,
        ViewportManipulator manipulator,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportRenderOptions options,
        Modeling.MeshEditSession? editSession,
        ViewportCameraPathController? cameraPathCtrl,
        CameraPath.D3DCameraPathVideoEffect? activeCameraPath,
        ViewportBezierCurveController? bezierCurveCtrl,
        Model3D.D3DPrimitiveShapeParameter? activeBezierParam)
    {
        _depthLines.Clear();
        _depthTris.Clear();
        _topLines.Clear();
        _topTris.Clear();
        _ndcTris.Clear();
        _ndcLines.Clear();

        if (options.ShowGrid)
        {
            if (_gridLines.Count == 0) ViewportOverlayBuilder.BuildFloorGrid(_gridLines);
            _depthLines.AddRange(_gridLines);
        }

        bool suppressObjectOverlays = editSession is { IsActive: true } && !options.ShowObjectGizmoAndBoundsWhenEditActive;
        if (snapshot != null)
        {
            if (options.ShowCameraFrustum)
            {
                ViewportOverlayBuilder.BuildCameraFrustum(
                    _depthLines, snapshot.SceneCameraMatrix, snapshot.VideoWidth, snapshot.VideoHeight);
            }

            IVideoItem? skipObbItem = suppressObjectOverlays ? editSession!.TargetItem : null;
            ViewportOverlayBuilder.BuildSelectionAndLightOverlays(
                _depthLines, snapshot, manipulator, selectedItems, options.ShowLights, skipObbItem);
        }

        if (editSession is { IsActive: true } && options.ShowEditOverlays)
        {
            editSession.BuildOverlays(
                _depthLines, _depthTris, _topLines, _topTris, _ndcLines,
                camera, width, height, manipulator.Space);
        }
        else if (!suppressObjectOverlays)
        {
            if (cameraPathCtrl != null && activeCameraPath != null)
            {
                cameraPathCtrl.BuildOverlays(
                    activeCameraPath, _depthLines, _topLines, _topTris, camera,
                    snapshot?.VideoWidth ?? 1920, snapshot?.VideoHeight ?? 1080);
            }
            if (bezierCurveCtrl != null && activeBezierParam != null)
            {
                bezierCurveCtrl.BuildOverlays(
                    activeBezierParam, snapshot, selectedItems, manipulator,
                    _depthLines, _topTris, camera);
            }
        }

        DrawOverlayGeometry(device, ctx, viewProj, _depthLines, _depthTris);
    }

    private void DrawTopMostOverlays(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        int width,
        int height,
        in Matrix4x4 viewProj,
        ViewportCamera camera,
        ViewportSceneSnapshot? snapshot,
        ViewportManipulator manipulator,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportRenderOptions options,
        Modeling.MeshEditSession? editSession)
    {
        bool suppressObjectOverlays = editSession is { IsActive: true } && !options.ShowObjectGizmoAndBoundsWhenEditActive;
        if (!suppressObjectOverlays &&
            manipulator.TryComputeGizmo(selectedItems, snapshot, camera, out var gizmo))
        {
            var highlight = manipulator.IsDragging ? manipulator.ActiveHandle : manipulator.HoveredHandle;
            ViewportOverlayBuilder.BuildTransformGizmo(
                _topLines, _topTris, gizmo, manipulator.Mode, highlight, manipulator.IsDragging);
        }

        DrawOverlayGeometry(device, ctx, viewProj, _topLines, _topTris);

        if (options.ShowAxisCompass)
        {
            AxisCompassGizmo.BuildNdcGeometry(
                _ndcTris, camera.GetViewMatrix(), width, height, options.DpiScale, options.HoveredCompassTarget);
        }
        if (_ndcLines.Count > 0 || _ndcTris.Count > 0)
        {
            DrawOverlayGeometry(device, ctx, Matrix4x4.Identity, _ndcLines, _ndcTris);
        }
    }

    private unsafe void DrawOverlayGeometry(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        in Matrix4x4 viewProj,
        List<OverlayVertex>? lines,
        List<OverlayVertex>? tris)
    {
        bool hasTris = tris != null && tris.Count >= 3;
        bool hasLines = lines != null && lines.Count >= 2;
        if (!hasTris && !hasLines) return;

        var cb = new CbOverlay { ViewProj = viewProj };
        ctx.UpdateSubresource(in cb, _cbOverlay!);
        ctx.IASetInputLayout(_ovLayout);
        ctx.VSSetShader(_ovVs);
        ctx.VSSetConstantBuffer(0, _cbOverlay);
        ctx.PSSetShader(_ovPs);

        int stride = Marshal.SizeOf<OverlayVertex>();

        if (hasTris)
        {
            var span = CollectionsMarshal.AsSpan(tris!);
            EnsureDynamicVb(device, ref _ovTriVb, ref _ovTriCap, span.Length, stride);
            UploadDynamicVb(ctx, _ovTriVb!, span);
            ctx.IASetVertexBuffer(0, _ovTriVb!, stride, 0);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.Draw(span.Length, 0);
        }

        if (hasLines)
        {
            var span = CollectionsMarshal.AsSpan(lines!);
            EnsureDynamicVb(device, ref _ovLineVb, ref _ovLineCap, span.Length, stride);
            UploadDynamicVb(ctx, _ovLineVb!, span);
            ctx.IASetVertexBuffer(0, _ovLineVb!, stride, 0);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            ctx.Draw(span.Length, 0);
        }
    }

    private static void EnsureDynamicVb(
        ID3D11Device device,
        ref ID3D11Buffer? vb,
        ref int cap,
        int neededVerts,
        int stride)
    {
        if (vb != null && cap >= neededVerts) return;
        vb?.Dispose();
        int newCap = Math.Max(256, Math.Max(cap * 2, neededVerts));
        vb = device.CreateBuffer(new BufferDescription(
            newCap * stride, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        cap = newCap;
    }

    private static unsafe void UploadDynamicVb(ID3D11DeviceContext ctx, ID3D11Buffer vb, ReadOnlySpan<OverlayVertex> verts)
    {
        ctx.Map(vb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out var mapped);
        try
        {
            var src = MemoryMarshal.AsBytes(verts);
            var dst = new Span<byte>(mapped.DataPointer.ToPointer(), src.Length);
            src.CopyTo(dst);
        }
        finally
        {
            ctx.Unmap(vb, 0);
        }
    }

    private void SyncSnapshotSharedTextures(ID3D11Device device, ID3D11DeviceContext ctx, ViewportSceneSnapshot? snapshot)
    {
        long newSeq = snapshot?.SequenceId ?? -1;
        if (newSeq == _cachedSnapshotSeq) return;

        var oldTextures = _sharedTextures;
        var prevByKey = new Dictionary<(IVideoItem Item, int PartIndex), (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv)>();
        foreach (var kv in oldTextures)
        {
            prevByKey.TryAdd((kv.Key.Item, kv.Key.PartIndex), kv.Value);
        }

        var nextTextures = new Dictionary<ViewportItemEntry, (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv)>();
        var retainedTexPtrs = new HashSet<IntPtr>();

        if (snapshot != null)
        {
            for (int i = 0; i < snapshot.Items.Count; i++)
            {
                var entry = snapshot.Items[i];
                var st = entry.SharedTexture;
                if (st == null || st.SharedHandle == IntPtr.Zero) continue;

                var copied = TryCopySharedTexture(device, ctx, st);
                if (copied.HasValue)
                {
                    nextTextures[entry] = copied.Value;
                }
                else if (prevByKey.TryGetValue((entry.Item, entry.PartIndex), out var fallback))
                {
                    nextTextures[entry] = fallback;
                    retainedTexPtrs.Add(fallback.Tex.NativePointer);
                }
            }
        }

        foreach (var pair in oldTextures.Values)
        {
            if (retainedTexPtrs.Contains(pair.Tex.NativePointer)) continue;
            try { pair.Srv.Dispose(); } catch { }
            try { pair.Tex.Dispose(); } catch { }
        }

        _sharedTextures = nextTextures;
        _cachedSnapshotSeq = newSeq;
    }

    private static (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv)? TryCopySharedTexture(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ViewportSharedTexture st)
    {
        ID3D11Texture2D? copyTex = null;
        ID3D11ShaderResourceView? srv = null;
        try
        {
            copyTex = device.CreateTexture2D(new Texture2DDescription
            {
                Width = st.Width,
                Height = st.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });

            ID3D11Texture2D? opened = st.SourceDevicePtr == device.NativePointer
                ? null
                : device.OpenSharedResource<ID3D11Texture2D>(st.SharedHandle);
            try
            {
                ID3D11Texture2D? srcTex = opened ?? (st.SourceDevicePtr == device.NativePointer ? st.Texture : null);
                if (srcTex == null)
                {
                    copyTex.Dispose();
                    return null;
                }

                if (st.HasKeyedMutex)
                {
                    using var readMutex = srcTex.QueryInterface<IDXGIKeyedMutex>();
                    try
                    {
                        readMutex.AcquireSync(1, 2000);
                    }
                    catch (Exception ex)
                    {
                        Iyahon_D3D11Renderer_CorePlugin.Log($"Viewport3DRenderer ReadMutex タイムアウト: {ex.Message}");
                        copyTex.Dispose();
                        return null;
                    }
                    try
                    {
                        ctx.CopyResource(copyTex, srcTex);
                        ctx.Flush();
                    }
                    finally
                    {
                        try { readMutex.ReleaseSync(1); } catch { }
                    }
                }
                else
                {
                    ctx.CopyResource(copyTex, srcTex);
                    ctx.Flush();
                }
            }
            finally
            {
                opened?.Dispose();
            }

            srv = device.CreateShaderResourceView(copyTex);
            return (copyTex, srv);
        }
        catch (Exception ex)
        {
            Iyahon_D3D11Renderer_CorePlugin.Log($"Viewport3DRenderer 共有テクスチャ同期エラー: {ex.Message}");
            srv?.Dispose();
            copyTex?.Dispose();
            return null;
        }
    }

    private ID3DEffect? GetOrCreateEffect(IVideoItem item, string effectId)
    {
        if (string.IsNullOrEmpty(effectId)) return null;
        if (_d3dEffects.TryGetValue(item, out var cached))
        {
            if (cached.Id == effectId) return cached.Effect;
            try { cached.Effect.Dispose(); } catch { }
            _d3dEffects.Remove(item);
        }
        var created = D3DEffectRegistry.CreateEffect(effectId);
        if (created != null)
            _d3dEffects[item] = (effectId, created);
        return created;
    }

    private unsafe void EnsureResources(ID3D11Device device, int width, int height)
    {
        if (_devicePtr != device.NativePointer)
        {
            ReleaseDeviceResources();
            CreateDeviceResources(device);
            _devicePtr = device.NativePointer;
        }

        if (_hdrTex == null || _width != width || _height != height)
        {
            _hdrSrv?.Dispose(); _hdrSrv = null;
            _hdrRtv?.Dispose(); _hdrRtv = null;
            _hdrTex?.Dispose(); _hdrTex = null;

            _width = width;
            _height = height;

            _hdrTex = device.CreateTexture2D(new Texture2DDescription
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
            _hdrRtv = device.CreateRenderTargetView(_hdrTex);
            _hdrSrv = device.CreateShaderResourceView(_hdrTex);

            _refractBgSrv?.Dispose(); _refractBgSrv = null;
            _refractBgTex?.Dispose(); _refractBgTex = null;
            _refractBgMaxMip = 0f;
        }
    }

    private void EnsureRefractBgTarget(ID3D11Device device, int width, int height)
    {
        if (_refractBgTex != null)
        {
            var d = _refractBgTex.Description;
            if (d.Width == width && d.Height == height) return;
            _refractBgSrv?.Dispose(); _refractBgSrv = null;
            _refractBgTex?.Dispose(); _refractBgTex = null;
        }

        _refractBgTex = device.CreateTexture2D(new Texture2DDescription
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
        _refractBgSrv = device.CreateShaderResourceView(_refractBgTex);
        _refractBgMaxMip = MathF.Max(0f, _refractBgTex.Description.MipLevels - 1);
    }

    private unsafe void CreateDeviceResources(ID3D11Device device)
    {
        var qVs = ShaderBytecodeCache.GetOrCompile("VpQuad_VS", QuadShaderSource, "VS_VpQuad", "vs_5_0", "vp_quad")!;
        var qPsO = ShaderBytecodeCache.GetOrCompile("VpQuad_PSO", QuadShaderSource, "PS_VpQuadOpaque", "ps_5_0", "vp_quad")!;
        var qPsS = ShaderBytecodeCache.GetOrCompile("VpQuad_PSS", QuadShaderSource, "PS_VpQuadSemi", "ps_5_0", "vp_quad")!;
        _quadVs = device.CreateVertexShader(qVs);
        _quadPsOpaque = device.CreatePixelShader(qPsO);
        _quadPsSemi = device.CreatePixelShader(qPsS);
        _quadLayout = device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        }, qVs);

        var tVs = ShaderBytecodeCache.GetOrCompile("VpTone_VS", ToneMapShaderSource, "VS_VpToneMap", "vs_5_0", "vp_tone")!;
        var tPs = ShaderBytecodeCache.GetOrCompile("VpTone_PS", ToneMapShaderSource, "PS_VpToneMap", "ps_5_0", "vp_tone")!;
        _toneVs = device.CreateVertexShader(tVs);
        _tonePs = device.CreatePixelShader(tPs);

        var oVs = ShaderBytecodeCache.GetOrCompile("VpOv_VS", OverlayShaderSource, "VS_VpOverlay", "vs_5_0", "vp_ov")!;
        var oPs = ShaderBytecodeCache.GetOrCompile("VpOv_PS", OverlayShaderSource, "PS_VpOverlay", "ps_5_0", "vp_ov")!;
        _ovVs = device.CreateVertexShader(oVs);
        _ovPs = device.CreatePixelShader(oPs);
        _ovLayout = device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
        }, oVs);

        var qVerts = new QuadVertex[]
        {
            new() { Position = new Vector3(-0.5f, -0.5f, 0f), TexCoord = new Vector2(0f, 0f) },
            new() { Position = new Vector3( 0.5f, -0.5f, 0f), TexCoord = new Vector2(1f, 0f) },
            new() { Position = new Vector3(-0.5f,  0.5f, 0f), TexCoord = new Vector2(0f, 1f) },
            new() { Position = new Vector3( 0.5f,  0.5f, 0f), TexCoord = new Vector2(1f, 1f) },
        };
        fixed (QuadVertex* p = qVerts)
            _quadVb = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<QuadVertex>() * 4, BindFlags.VertexBuffer), new SubresourceData((IntPtr)p));

        var fsVerts = new QuadVertex[]
        {
            new() { Position = new Vector3(-1f, -1f, 0f), TexCoord = new Vector2(0f, 1f) },
            new() { Position = new Vector3( 1f, -1f, 0f), TexCoord = new Vector2(1f, 1f) },
            new() { Position = new Vector3(-1f,  1f, 0f), TexCoord = new Vector2(0f, 0f) },
            new() { Position = new Vector3( 1f,  1f, 0f), TexCoord = new Vector2(1f, 0f) },
        };
        fixed (QuadVertex* p = fsVerts)
            _fullscreenVb = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<QuadVertex>() * 4, BindFlags.VertexBuffer), new SubresourceData((IntPtr)p));

        _cbPerObject = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbPerObject>(), BindFlags.ConstantBuffer));
        _cbToneMap = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbToneMap>(), BindFlags.ConstantBuffer));
        _cbOverlay = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbOverlay>(), BindFlags.ConstantBuffer));
        _cbLighting = device.CreateBuffer(new BufferDescription(Marshal.SizeOf<CbLightingHeader>(), BindFlags.ConstantBuffer));

        _samplerLinear = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
        });

        var opaqueDesc = new BlendDescription { IndependentBlendEnable = false };
        opaqueDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = false,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendOpaque = device.CreateBlendState(opaqueDesc);

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

        var alphaDesc = new BlendDescription { IndependentBlendEnable = false };
        alphaDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendAlpha = device.CreateBlendState(alphaDesc);

        var noColorDesc = new BlendDescription { IndependentBlendEnable = false };
        noColorDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            IsBlendEnabled = false,
            RenderTargetWriteMask = ColorWriteEnable.None,
        };
        _blendNoColor = device.CreateBlendState(noColorDesc);

        _dsOpaque = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.LessEqual,
        });
        _dsDepthEqual = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Equal,
        });
        _dsDepthReadOnly = device.CreateDepthStencilState(new DepthStencilDescription
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

        _rsSolid = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
        });
    }

    private static void UnbindCommonSrvs(ID3D11DeviceContext ctx)
    {
        ctx.PSSetShaderResource(0, null!);
        ctx.PSSetShaderResource(2, null!);
        ctx.PSSetShaderResource(3, null!);
        ctx.PSSetShaderResource(4, null!);
        ctx.PSSetShaderResource(5, null!);
        ctx.PSSetShaderResource(6, null!);
        ctx.PSSetShaderResource(7, null!);
        ctx.PSSetShaderResource(8, null!);
        ctx.PSSetShaderResource(9, null!);
        ctx.PSSetSampler(3, null!);
    }

    private void ClearSharedTextures()
    {
        foreach (var pair in _sharedTextures.Values)
        {
            try { pair.Srv.Dispose(); } catch { }
            try { pair.Tex.Dispose(); } catch { }
        }
        _sharedTextures.Clear();
    }

    private void ReleaseDeviceResources()
    {
        if (_devicePtr != IntPtr.Zero)
        {
            Model3D.ModelGpuCache.ClearForDevice(_devicePtr);
            Model3D.MaterialTextureCache.ClearForDevice(_devicePtr);
            EnvironmentMapManager.ClearForDevice(_devicePtr);
        }
        ClearSharedTextures();
        _cachedSnapshotSeq = -1;

        foreach (var pair in _d3dEffects.Values)
        {
            try { pair.Effect.Dispose(); } catch { }
        }
        _d3dEffects.Clear();

        _objRenderer?.Dispose(); _objRenderer = null;
        _noise3DRenderer?.Dispose(); _noise3DRenderer = null;
        _screenOutlineHelper?.Dispose(); _screenOutlineHelper = null;
        _lightBuffer.Release();
        _shadowBuffer.Release();

        _hdrSrv?.Dispose(); _hdrSrv = null;
        _hdrRtv?.Dispose(); _hdrRtv = null;
        _hdrTex?.Dispose(); _hdrTex = null;
        _refractBgSrv?.Dispose(); _refractBgSrv = null;
        _refractBgTex?.Dispose(); _refractBgTex = null;
        _refractBgMaxMip = 0f;

        _quadVs?.Dispose(); _quadVs = null;
        _quadPsOpaque?.Dispose(); _quadPsOpaque = null;
        _quadPsSemi?.Dispose(); _quadPsSemi = null;
        _quadLayout?.Dispose(); _quadLayout = null;
        _quadVb?.Dispose(); _quadVb = null;
        _cbPerObject?.Dispose(); _cbPerObject = null;
        _samplerLinear?.Dispose(); _samplerLinear = null;

        _toneVs?.Dispose(); _toneVs = null;
        _tonePs?.Dispose(); _tonePs = null;
        _fullscreenVb?.Dispose(); _fullscreenVb = null;
        _cbToneMap?.Dispose(); _cbToneMap = null;

        _ovVs?.Dispose(); _ovVs = null;
        _ovPs?.Dispose(); _ovPs = null;
        _ovLayout?.Dispose(); _ovLayout = null;
        _cbOverlay?.Dispose(); _cbOverlay = null;
        _ovLineVb?.Dispose(); _ovLineVb = null; _ovLineCap = 0;
        _ovTriVb?.Dispose(); _ovTriVb = null; _ovTriCap = 0;

        _cbLighting?.Dispose(); _cbLighting = null;
        _blendOpaque?.Dispose(); _blendOpaque = null;
        _blendPremul?.Dispose(); _blendPremul = null;
        _blendAlpha?.Dispose(); _blendAlpha = null;
        _blendNoColor?.Dispose(); _blendNoColor = null;
        _dsOpaque?.Dispose(); _dsOpaque = null;
        _dsDepthEqual?.Dispose(); _dsDepthEqual = null;
        _dsDepthReadOnly?.Dispose(); _dsDepthReadOnly = null;
        _dsDisabled?.Dispose(); _dsDisabled = null;
        _rsSolid?.Dispose(); _rsSolid = null;

        _devicePtr = IntPtr.Zero;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDeviceResources();
        _lightBuffer.Dispose();
        _shadowBuffer.Dispose();
    }
}
