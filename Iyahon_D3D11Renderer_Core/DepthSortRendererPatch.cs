using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Effects;
using YukkuriMovieMaker.Project.Items;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using Iyahon_D3D11Renderer_Core.D3DEffect.Effects;
using Iyahon_D3D11Renderer_Core.Lighting;
using Iyahon_D3D11Renderer_Core.Model3D;
using Iyahon_D3D11Renderer_Core.Viewport3D;
using TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

internal struct AaState
{
    public bool ShouldRestore;
    public AntialiasMode OriginalAA;
    public TextAntialiasMode OriginalTextAA;
    public bool ShouldRestoreSource;
    public AntialiasMode OrigSourceAA;
    public TextAntialiasMode OrigSourceTextAA;
    public object? SourceDevices;
}

internal sealed class CachedRenderer : IDisposable
{
    public DepthSortRenderer Renderer { get; }
    public ID2D1DeviceContext6? IsolatedDc { get; set; }
    public IntPtr D2DDevicePtr { get; set; }
    public DateTime LastSeen { get; set; }

    public CachedRenderer(DepthSortRenderer renderer)
    {
        Renderer = renderer;
        LastSeen = DateTime.UtcNow;
    }

    public void Dispose()
    {
        try { IsolatedDc?.Dispose(); } catch { }
        IsolatedDc = null;
        D2DDevicePtr = IntPtr.Zero;
        Renderer.Dispose();
    }
}

internal static class DepthSortRendererPatch
{
    private static readonly object _globalRenderLock = new();
    private static long _frameSequence;

    private static readonly ConditionalWeakTable<IVideoItem, StrongBox<bool>>
        _perItemEnabled = new();

    public static bool IsEnabledForItem(IVideoItem item)
    {
        if (_perItemEnabled.TryGetValue(item, out var box))
            return box.Value;
        return false;
    }

    public static void SetEnabledForItem(IVideoItem item, bool enabled)
    {
        if (_perItemEnabled.TryGetValue(item, out var box))
            box.Value = enabled;
        else
            _perItemEnabled.AddOrUpdate(item, new StrongBox<bool>(enabled));
    }

    private static Type? _timelineSourceType;
    private static FieldInfo? _timelineResourcesField;
    private static FieldInfo? _sceneField;
    private static FieldInfo? _devicesField;
    private static FieldInfo? _commandListField;

    private static FieldInfo? _effectedSourceOutputsField;
    private static PropertyInfo? _kvpKeyProp;
    private static PropertyInfo? _kvpValueProp;

    private static Type? _effectedSourceOutputType;
    private static PropertyInfo? _esoPreRenderOutputProp;
    private static PropertyInfo? _esoDrawDescProp;

    private static PropertyInfo? _eisOutputProp;
    private static FieldInfo? _eisDevicesField;
    private static FieldInfo? _eisItemField;
    private static PropertyInfo? _eisParentEffectsProp;
    private static MethodInfo? _eisGetZIndexMethod;
    private static PropertyInfo? _tsepEffectItemProp;
    private static PropertyInfo? _tsepTimeSourceProp;
    private static Type? _groupItemType;

    // ── シーン・タイムラインメタ情報キャッシュ ──
    private static PropertyInfo? _sceneTimelineProp;
    private static PropertyInfo? _sceneCurrentFrameProp;
    private static PropertyInfo? _timelineVideoInfoProp;
    private static PropertyInfo? _videoInfoWidthProp;
    private static PropertyInfo? _videoInfoHeightProp;
    private static PropertyInfo? _videoInfoFpsProp;
    private static PropertyInfo? _timelineItemRectsProp;
    private static PropertyInfo? _needTimelineItemRectsProp;

    // ── Source別 devices フィールドキャッシュ ──
    private static readonly ConcurrentDictionary<Type, FieldInfo?> _sourceDevicesFieldCache = new();

    // ── 光源検出用フィールド ──
    private static Type? _shapeItemType;
    private static PropertyInfo? _shapeType2Prop;
    private static PropertyInfo? _eisSourceProp;
    private static Type? _shapeSourceType;
    private static FieldInfo? _shapeSourceInternalSourceField;
    private static readonly Type _lightShapePluginType = typeof(D3DLightShapePlugin);
    private static readonly Type _modelShapePluginType = typeof(D3DModelShapePlugin);
    private static readonly Type _primitiveShapePluginType = typeof(D3DPrimitiveShapePlugin);
    private static readonly Type _envShapePluginType = typeof(D3DEnvironmentShapePlugin);

    // ── DirectWrite キャッシュ (ギズモラベル描画用) ──
    private static IDWriteFactory? _dwriteFactory;
    private static IDWriteTextFormat? _gizmoTextFormat;
    private static bool _dwriteInitAttempted;

    private static readonly ConditionalWeakTable<object, CachedRenderer>
        _renderers = new();

    static DepthSortRendererPatch()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { _gizmoTextFormat?.Dispose(); } catch { }
            _gizmoTextFormat = null;
            try { _dwriteFactory?.Dispose(); } catch { }
            _dwriteFactory = null;
        };
    }

    private static ID2D1DeviceContext6 EnsureIsolatedDc(CachedRenderer cached, ID2D1DeviceContext sourceDc)
    {
        using var d2dDevice = sourceDc.Device;
        if (cached.IsolatedDc == null || cached.D2DDevicePtr != d2dDevice.NativePointer)
        {
            try { cached.IsolatedDc?.Dispose(); } catch { }
            using var baseDc = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            cached.IsolatedDc = baseDc.QueryInterface<ID2D1DeviceContext6>();
            cached.D2DDevicePtr = d2dDevice.NativePointer;
        }

        var iso = cached.IsolatedDc;
        iso.Dpi = sourceDc.Dpi;
        iso.AntialiasMode = sourceDc.AntialiasMode;
        iso.TextAntialiasMode = sourceDc.TextAntialiasMode;
        return iso;
    }

    internal static void Apply(Harmony harmony)
    {
        _timelineSourceType = FindType("YukkuriMovieMaker.Player.Video.TimelineSource");
        if (_timelineSourceType == null) { Log("TimelineSource が見つかりません。"); return; }

        var effectedItemSourceType = FindType("YukkuriMovieMaker.Player.Video.EffectedItemSource");
        if (effectedItemSourceType == null) { Log("EffectedItemSource が見つかりません。"); return; }

        _effectedSourceOutputType = FindType("YukkuriMovieMaker.Player.Video.EffectedSourceOutput");
        if (_effectedSourceOutputType == null) { Log("EffectedSourceOutput が見つかりません。"); return; }

        _timelineResourcesField = _timelineSourceType.GetField("timelineResources",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _sceneField = _timelineSourceType.GetField("scene",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _devicesField = _timelineSourceType.GetField("devices",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _commandListField = _timelineSourceType.GetField("commandList",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _timelineItemRectsProp = _timelineSourceType.GetProperty("TimelineItemRects",
            BindingFlags.Instance | BindingFlags.Public);
        _needTimelineItemRectsProp = _timelineSourceType.GetProperty("NeedTimelineItemRects",
            BindingFlags.Instance | BindingFlags.Public);

        _effectedSourceOutputsField = effectedItemSourceType.GetField("effectedSourceOutputs",
            BindingFlags.Instance | BindingFlags.NonPublic);

        _eisOutputProp = effectedItemSourceType.GetProperty("Output",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _eisDevicesField = effectedItemSourceType.GetField("devices",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _eisItemField = effectedItemSourceType.GetField("item",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _eisParentEffectsProp = effectedItemSourceType.GetProperty("ParentEffects",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _eisGetZIndexMethod = effectedItemSourceType.GetMethod("GetZIndex",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        var tsepType = FindType("YukkuriMovieMaker.Player.Video.TimeSourceAndEffectPair");
        _tsepEffectItemProp = tsepType?.GetProperty("EffectItem",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _tsepTimeSourceProp = tsepType?.GetProperty("TimeSource",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _groupItemType = FindType("YukkuriMovieMaker.Project.Items.GroupItem");

        _esoPreRenderOutputProp = _effectedSourceOutputType.GetProperty("PreRenderOutput",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _esoDrawDescProp = _effectedSourceOutputType.GetProperty("DrawDescription",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        var kvpType = typeof(KeyValuePair<,>).MakeGenericType(typeof(IVideoItem), effectedItemSourceType);
        _kvpKeyProp = kvpType.GetProperty("Key");
        _kvpValueProp = kvpType.GetProperty("Value");

        if (_devicesField == null || _timelineResourcesField == null ||
            _commandListField == null || _effectedSourceOutputsField == null ||
            _esoPreRenderOutputProp == null || _esoDrawDescProp == null ||
            _eisOutputProp == null)
        {
            Log("必要なメンバのキャッシュ失敗。");
            return;
        }

        var sceneType = FindType("YukkuriMovieMaker.Project.Scene");
        if (sceneType != null)
        {
            _sceneTimelineProp = sceneType.GetProperty("Timeline", BindingFlags.Instance | BindingFlags.Public);
            _sceneCurrentFrameProp = sceneType.GetProperty("CurrentFrame", BindingFlags.Instance | BindingFlags.Public);
        }

        var timelineType = FindType("YukkuriMovieMaker.Project.Timeline");
        _timelineVideoInfoProp = timelineType?.GetProperty("VideoInfo", BindingFlags.Instance | BindingFlags.Public);

        var videoInfoType = FindType("YukkuriMovieMaker.Project.VideoInfo");
        if (videoInfoType != null)
        {
            _videoInfoWidthProp = videoInfoType.GetProperty("Width", BindingFlags.Instance | BindingFlags.Public);
            _videoInfoHeightProp = videoInfoType.GetProperty("Height", BindingFlags.Instance | BindingFlags.Public);
            _videoInfoFpsProp = videoInfoType.GetProperty("FPS", BindingFlags.Instance | BindingFlags.Public);
        }

        var updateMethod = _timelineSourceType.GetMethod("Update",
            BindingFlags.Instance | BindingFlags.Public);
        if (updateMethod == null) { Log("Update() が見つかりません。"); return; }

        harmony.Patch(updateMethod, postfix: new HarmonyMethod(
            typeof(DepthSortRendererPatch), nameof(UpdatePostfix)));

        Log("TimelineSource.Update() Postfix パッチ適用完了。");

        var disposeMethod = _timelineSourceType.GetMethod("Dispose",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        if (disposeMethod == null)
        {
            var baseType = _timelineSourceType.BaseType;
            while (baseType != null && disposeMethod == null)
            {
                disposeMethod = baseType.GetMethod("Dispose",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                baseType = baseType.BaseType;
            }
        }

        if (disposeMethod != null)
        {
            harmony.Patch(disposeMethod, prefix: new HarmonyMethod(
                typeof(DepthSortRendererPatch), nameof(DisposePrefix)));
            Log("TimelineSource.Dispose() Prefix パッチ適用完了。");
        }
        else
        {
            Log("Dispose() メソッドが見つかりません。");
        }

        var eisUpdateMethod = effectedItemSourceType.GetMethod("Update",
            BindingFlags.Instance | BindingFlags.Public);
        if (eisUpdateMethod != null && _eisDevicesField != null && _eisItemField != null)
        {
            harmony.Patch(eisUpdateMethod,
                prefix: new HarmonyMethod(typeof(DepthSortRendererPatch), nameof(EisUpdatePrefix)),
                postfix: new HarmonyMethod(typeof(DepthSortRendererPatch), nameof(EisUpdatePostfix)));
            Log("EffectedItemSource.Update() AA パッチ適用完了。");
        }

        _shapeItemType = FindType("YukkuriMovieMaker.Project.Items.ShapeItem");
        if (_shapeItemType != null)
        {
            _shapeType2Prop = _shapeItemType.GetProperty("ShapeType2",
                BindingFlags.Instance | BindingFlags.Public);
        }

        _eisSourceProp = effectedItemSourceType.GetProperty("Source",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        _shapeSourceType = FindType("YukkuriMovieMaker.Player.Video.Items.ShapeSource");
        if (_shapeSourceType != null)
        {
            _shapeSourceInternalSourceField = _shapeSourceType.GetField("source",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }

        var lightDetected = _shapeItemType != null && _shapeType2Prop != null
            && _eisSourceProp != null && _shapeSourceType != null
            && _shapeSourceInternalSourceField != null;
        Log($"光源検出: {(lightDetected ? "有効" : "無効")} (ShapeItem={_shapeItemType != null}, ShapeType2={_shapeType2Prop != null}, EIS.Source={_eisSourceProp != null}, ShapeSource={_shapeSourceType != null})");
    }

    private static bool IsValidDevice(IGraphicsDevicesAndContext? devices)
    {
        if (devices == null) return false;
        try
        {
            var d3d = devices.D3D;
            if (d3d == null) return false;

            var device = d3d.Device;
            if (device == null || device.NativePointer == IntPtr.Zero) return false;
            if (device.DeviceRemovedReason.Failure) return false;

            var d3dCtx = d3d.DeviceContext;
            if (d3dCtx == null || d3dCtx.NativePointer == IntPtr.Zero) return false;

            var dc = devices.DeviceContext;
            if (dc == null || dc.NativePointer == IntPtr.Zero) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PruneRenderers()
    {
        var now = DateTime.UtcNow;
        var toRemove = new List<object>();

        foreach (var pair in _renderers)
        {
            if ((now - pair.Value.LastSeen).TotalSeconds > 10.0)
            {
                toRemove.Add(pair.Key);
            }
        }

        foreach (var key in toRemove)
        {
            if (_renderers.TryGetValue(key, out var cached))
            {
                try { cached.Dispose(); } catch { }
                _renderers.Remove(key);
            }
        }
    }

    private static void DisposePrefix(object __instance)
    {
        try
        {
            if (_commandListField != null)
            {
                var cl = _commandListField.GetValue(__instance) as ID2D1CommandList;
                if (cl != null && cl.NativePointer != IntPtr.Zero)
                {
                    cl.Dispose();
                    _commandListField.SetValue(__instance, null);
                }
            }

            if (_devicesField == null) return;
            var devices = _devicesField.GetValue(__instance);
            if (devices == null) return;

            lock (_globalRenderLock)
            {
                if (_renderers.TryGetValue(devices, out var cached))
                {
                    cached.Dispose();
                    _renderers.Remove(devices);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"DisposePrefix 例外: {ex.Message}");
        }
    }

    private static void UpdatePostfix(object __instance, TimeSpan time, TimelineSourceUsage usage)
    {
        lock (_globalRenderLock)
        {
            try
            {
                var devices = _devicesField!.GetValue(__instance) as IGraphicsDevicesAndContext;
                if (!IsValidDevice(devices)) return;

                var resources = _timelineResourcesField!.GetValue(__instance);
                if (resources == null) return;

                var scene = _sceneField?.GetValue(__instance);
                GetSceneVideoInfo(scene, out int width, out int height, out double fpsDouble);
                int fps = Math.Max(1, (int)Math.Round(fpsDouble));
                long currentSceneFrame = ComputeSceneFrame(time, fps, scene);

                var d3dItems = new List<(IVideoItem item, object eisValue)>();
                var d2dItems = new List<(IVideoItem item, object eisValue)>();
                var frameLights = new List<LightData>();
                var frameLightEntries = new List<(IVideoItem item, LightData light, DrawDescription? drawDesc)>();

                DofParameters dofParams = default;
                dofParams.Enabled = false;
                AtmosphereParameters atmosphereParams = default;
                EnvironmentParameters envParams = default;
                var frameEnvironments = new List<(int Layer, EnvironmentParameters Env, AtmosphereParameters Atmo, Matrix4x4 Camera)>();

                foreach (object pair in (IEnumerable)resources)
                {
                    var key = _kvpKeyProp!.GetValue(pair) as IVideoItem;
                    var value = _kvpValueProp!.GetValue(pair);
                    if (key == null || value == null) continue;

                    if (!dofParams.Enabled)
                    {
                        TryCollectDof(key, value, currentSceneFrame, fps, ref dofParams);
                    }
                    if (!atmosphereParams.Enabled)
                    {
                        TryCollectAtmosphere(key, value, currentSceneFrame, fps, ref atmosphereParams);
                    }

                    if (TryCollectLight(key, value, out var lightData, out var lightDrawDesc))
                    {
                        frameLights.Add(lightData!);
                        frameLightEntries.Add((key, lightData!, lightDrawDesc));
                        continue;
                    }

                    if (TryCollectEnvironment(key, value, out var envData, out var atmoData, out var envCamera))
                    {
                        frameEnvironments.Add((key.Layer, envData, atmoData, envCamera));
                        continue;
                    }

                    if (IsEnabledForItem(key) || IsBuiltInModelItem(key) || HasD3DPostOrLayoutVideoEffect(key, value))
                        d3dItems.Add((key, value));
                    else
                        d2dItems.Add((key, value));
                }

                if (frameEnvironments.Count > 0)
                {
                    if (frameEnvironments.Count > 1)
                        frameEnvironments.Sort(static (a, b) => a.Layer.CompareTo(b.Layer));

                    Matrix4x4 firstValidEnvCam = default;
                    for (int i = 0; i < frameEnvironments.Count; i++)
                    {
                        var (_, e, a, cam) = frameEnvironments[i];
                        if (cam.M44 != 0f)
                        {
                            if (firstValidEnvCam.M44 == 0f || cam != Matrix4x4.Identity)
                                firstValidEnvCam = cam;
                        }

                        if (e.OverrideEnvironmentMap)
                        {
                            envParams.Enabled = true;
                            envParams.OverrideEnvironmentMap = true;
                            envParams.EnvironmentMapPath = e.EnvironmentMapPath;
                            envParams.RotationDeg = e.RotationDeg;
                            envParams.Exposure = e.Exposure;
                            envParams.ReflectionIntensity = e.ReflectionIntensity;
                            envParams.ShowSkybox = e.ShowSkybox;
                            envParams.SkyboxBlur = e.SkyboxBlur;
                        }

                        if (e.OverrideIblAndAmbient)
                        {
                            envParams.Enabled = true;
                            envParams.OverrideIblAndAmbient = true;
                            envParams.IblIntensity = e.IblIntensity;
                            envParams.AmbientIntensity = e.AmbientIntensity;
                            envParams.SkyColorLinear = e.SkyColorLinear;
                        }

                        if (a.EnableFog)
                        {
                            atmosphereParams.Enabled = true;
                            atmosphereParams.EnableFog = true;
                            atmosphereParams.FogColor = a.FogColor;
                            atmosphereParams.FogDensity = a.FogDensity;
                            atmosphereParams.FogStart = a.FogStart;
                            atmosphereParams.FogEnd = a.FogEnd;
                            atmosphereParams.HeightFogStrength = a.HeightFogStrength;
                            atmosphereParams.HeightFogBaseY = a.HeightFogBaseY;
                        }

                        if (a.EnableGodRays)
                        {
                            atmosphereParams.Enabled = true;
                            atmosphereParams.EnableGodRays = true;
                            atmosphereParams.GodRayIntensity = a.GodRayIntensity;
                            atmosphereParams.GodRayDecay = a.GodRayDecay;
                            atmosphereParams.GodRayDensity = a.GodRayDensity;
                            atmosphereParams.GodRayColor = a.GodRayColor;
                        }
                    }

                    if (firstValidEnvCam.M44 != 0f)
                        envParams.FallbackCameraMatrix = firstValidEnvCam;
                }

                bool wantSkyboxOnly = envParams.OverrideEnvironmentMap && envParams.ShowSkybox;

                bool captureViewport = usage != TimelineSourceUsage.Exporting
                    && Viewport3DBridge.HasActiveViewports
                    && DepthSortVideoPlayerPatch.IsMainTimelineSource(__instance);
                if (d3dItems.Count == 0 && !wantSkyboxOnly && !captureViewport) return;

                // レイヤー順の決定論的ソートで光源インデックスを固定化
                if (frameLights.Count > 1)
                {
                    frameLights = frameLights.OrderBy(l => l.Layer).ToList();
                    frameLightEntries = frameLightEntries.OrderBy(e => e.light.Layer).ToList();
                }

                if (d3dItems.Count == 0 && !wantSkyboxOnly)
                {
                    var vpCtx = devices!.D3D.DeviceContext;
                    using var vpSaver = vpCtx != null ? new D3D11StateSaver(vpCtx) : null;
                    PublishViewportSnapshot(
                        devices,
                        d3dItems, d2dItems, frameLights, frameLightEntries,
                        width, height, fps, currentSceneFrame, in envParams);
                    return;
                }

                if (!_renderers.TryGetValue(devices!, out var cached))
                {
                    cached = new CachedRenderer(new DepthSortRenderer());
                    _renderers.Add(devices!, cached);
                }
                cached.LastSeen = DateTime.UtcNow;
                PruneRenderers();

                bool needsReset = false;
                if (cached.Renderer.DevicePointer != IntPtr.Zero &&
                    cached.Renderer.DevicePointer != devices!.D3D.Device.NativePointer)
                {
                    needsReset = true;
                }
                if (cached.Renderer.Width > 0 && (cached.Renderer.Width != width || cached.Renderer.Height != height || cached.Renderer.IsHdrMismatch || cached.Renderer.IsAaMismatch))
                {
                    needsReset = true;
                }

                if (needsReset)
                {
                    var oldCommandList = _commandListField!.GetValue(__instance) as ID2D1CommandList;
                    if (oldCommandList != null)
                    {
                        oldCommandList.Dispose();
                        _commandListField.SetValue(__instance, null);
                    }
                }

                if (!cached.Renderer.Initialize(devices!, width, height))
                    return;

                var ymmCtx = devices!.D3D.DeviceContext;
                var dc = devices.DeviceContext;
                var renderItems = new List<RenderItem>();
                var builtInEntries = new List<(IVideoItem Item, DrawDescription DrawDesc, ObjModelData ModelData, string FilePath, string EmbeddedData, string ModData, long Frame, long Length, int? RenderItemIdx)>();
                bool anySceneDependentMod = false;
                ID3D11Texture2D? resultTexture;

                using (var renderSaver = ymmCtx != null ? new D3D11StateSaver(ymmCtx) : null)
                {
                    long frameSeq = Interlocked.Increment(ref _frameSequence);
                    cached.Renderer.BakedTexturePool.BeginFrame(frameSeq);
                    Viewport3D.Shading.LayerTextureRegistry.BeginFrame(devices.D3D.Device);

                    foreach (var (item, eisValue) in d3dItems)
                    {
                        var esoList = _effectedSourceOutputsField!.GetValue(eisValue) as IList;
                        if (esoList == null || esoList.Count == 0) continue;

                        long itemFrame = Math.Max(0, currentSceneFrame - item.Frame);
                        long itemLength = Math.Max(1, item.Length);
                        MaterialOverrideParameters itemMaterial = TryCollectMaterial(item, eisValue, itemFrame, itemLength, fps);

                        if (TryGetBuiltInModelCapture(item, eisValue, out var modelPath, out var embData, out var modData, out var builtInModel) && builtInModel != null)
                        {
                            if (!Viewport3D.Modifiers.MeshModifierStack.HasSceneDependentModifiers(modData) &&
                                (builtInModel.HasDisposedResources() ||
                                 !string.Equals(builtInModel.EmbeddedMeshData ?? string.Empty, embData, StringComparison.Ordinal) ||
                                 !string.Equals(builtInModel.ModifierStackData ?? string.Empty, modData, StringComparison.Ordinal)))
                            {
                                int maxTexSize = (int)D3D11RendererSettings.Default.ModelMaxTextureSize;
                                var evalTime = new Viewport3D.Shading.ShaderEvalTime(itemFrame, itemLength, fps);
                                CachedGpuModel? rebuilt = null;
                                if (Viewport3D.Modifiers.MeshModifierStack.HasAnyEnabledModifiers(modData))
                                {
                                    rebuilt = ModelGpuCache.GetOrBuildWithModifiersAtTime(
                                        devices.D3D.Device, devices.D3D.DeviceContext,
                                        embData, modelPath,
                                        builtInModel.ProceduralCacheKey, builtInModel.ProceduralMeshFactory,
                                        modData, null, -1, Matrix4x4.Identity, maxTexSize, evalTime);
                                }
                                else if (!string.IsNullOrWhiteSpace(embData))
                                {
                                    rebuilt = ModelGpuCache.GetOrBuildFromEditableMeshAtTime(
                                        devices.D3D.Device, devices.D3D.DeviceContext,
                                        embData, modelPath, maxTexSize, evalTime);
                                }
                                else if (!string.IsNullOrWhiteSpace(builtInModel.ProceduralCacheKey) &&
                                         builtInModel.ProceduralMeshFactory != null)
                                {
                                    rebuilt = ModelGpuCache.GetOrBuildProceduralMesh(
                                        devices.D3D.Device, builtInModel.ProceduralCacheKey, builtInModel.ProceduralMeshFactory);
                                }
                                else if (!string.IsNullOrWhiteSpace(modelPath))
                                {
                                    rebuilt = ModelGpuCache.GetOrLoad(
                                        devices.D3D.Device, devices.D3D.DeviceContext, modelPath, maxTexSize);
                                }

                                if (rebuilt != null)
                                {
                                    builtInModel = builtInModel.WithCachedModel(rebuilt);
                                    builtInModel.EmbeddedMeshData = embData;
                                    builtInModel.ModifierStackData = modData;
                                }
                            }

                            var firstEso = esoList[0];
                            var drawDesc = firstEso != null
                                ? _esoDrawDescProp!.GetValue(firstEso) as DrawDescription
                                : null;

                            if (drawDesc != null)
                            {
                                int? riIdx = null;
                                if (drawDesc.Opacity > 0.0)
                                {
                                    riIdx = renderItems.Count;
                                    renderItems.Add(new RenderItem
                                    {
                                        DrawDescription = drawDesc,
                                        Srv = null,
                                        PixelWidth = 0,
                                        PixelHeight = 0,
                                        BoundsCenterX = 0,
                                        BoundsCenterY = 0,
                                        Opacity = (float)drawDesc.Opacity,
                                        Layer = item.Layer,
                                        ObjModels = new List<ObjModelData> { builtInModel },
                                        OriginalItem = item,
                                        ItemFrame = itemFrame,
                                        ItemLength = itemLength,
                                        Fps = fps,
                                        Material = itemMaterial,
                                    });
                                }

                                if (Viewport3D.Modifiers.MeshModifierStack.HasSceneDependentModifiers(modData))
                                    anySceneDependentMod = true;

                                builtInEntries.Add((item, drawDesc, builtInModel, modelPath, embData, modData, itemFrame, itemLength, riIdx));
                                continue;
                            }
                        }

                        if (ObjLoaderBridge.IsAvailable && ObjLoaderBridge.IsObjLoaderItem(item))
                        {
                            var objModels = ObjLoaderBridge.TryGetModelData(eisValue);
                            if (objModels != null && objModels.Count > 0)
                            {
                                var firstEso = esoList[0];
                                var drawDesc = firstEso != null
                                    ? _esoDrawDescProp!.GetValue(firstEso) as DrawDescription
                                    : null;

                                if (drawDesc != null && drawDesc.Opacity > 0.0)
                                {
                                    renderItems.Add(new RenderItem
                                    {
                                        DrawDescription = drawDesc,
                                        Srv = null,
                                        PixelWidth = 0,
                                        PixelHeight = 0,
                                        BoundsCenterX = 0,
                                        BoundsCenterY = 0,
                                        Opacity = (float)drawDesc.Opacity,
                                        Layer = item.Layer,
                                        ObjModels = objModels,
                                        OriginalItem = item,
                                        ItemFrame = itemFrame,
                                        ItemLength = itemLength,
                                        Fps = fps,
                                        Material = itemMaterial,
                                    });
                                    continue;
                                }
                            }
                        }

                        ID3DVideoEffect? d3dVideoEffect = FindEnabledD3DVideoEffect(item);

                        foreach (object? eso in esoList)
                        {
                            if (eso == null) continue;

                            var preRenderOutput = _esoPreRenderOutputProp!.GetValue(eso) as ID2D1Image;
                            var drawDesc = _esoDrawDescProp!.GetValue(eso) as DrawDescription;
                            if (preRenderOutput == null || drawDesc == null) continue;

                            if ((double)drawDesc.Zoom.X == 0.0 || (double)drawDesc.Zoom.Y == 0.0 ||
                                drawDesc.Opacity == 0.0) continue;

                            RawRectF bounds;
                            try { bounds = dc.GetImageLocalBounds(preRenderOutput); }
                            catch { continue; }
                            if (float.IsInfinity(bounds.Left) || float.IsInfinity(bounds.Top) ||
                                float.IsInfinity(bounds.Right) || float.IsInfinity(bounds.Bottom)) continue;

                            int left = (int)MathF.Floor(bounds.Left);
                            int top = (int)MathF.Floor(bounds.Top);
                            int right = (int)MathF.Ceiling(bounds.Right);
                            int bottom = (int)MathF.Ceiling(bounds.Bottom);

                            float pw = right - left;
                            float ph = bottom - top;
                            if (pw <= 0 || ph <= 0) continue;

                            const int MaxTexSize = 8192;
                            int texW = Math.Min((int)pw, MaxTexSize);
                            int texH = Math.Min((int)ph, MaxTexSize);
                            if (texW <= 0 || texH <= 0) continue;

                            var srv = cached.Renderer.BakedTexturePool.Bake(preRenderOutput, devices, texW, texH, -left, -top, cached.Renderer.IsHdrOutput);
                            if (srv == null) continue;
                            Viewport3D.Shading.LayerTextureRegistry.Register(devices.D3D.Device, item.Layer, srv);

                            float cx = left + texW / 2f;
                            float cy = top + texH / 2f;

                            renderItems.Add(new RenderItem
                            {
                                DrawDescription = drawDesc,
                                Srv = srv,
                                PixelWidth = texW,
                                PixelHeight = texH,
                                BoundsCenterX = cx,
                                BoundsCenterY = cy,
                                Opacity = (float)drawDesc.Opacity,
                                Layer = item.Layer,
                                D3DVideoEffect = d3dVideoEffect,
                                OriginalItem = item,
                                ItemFrame = itemFrame,
                                ItemLength = itemLength,
                                Fps = fps,
                                Material = itemMaterial,
                            });
                        }
                    }

                    if (anySceneDependentMod && builtInEntries.Count > 0)
                    {
                        int maxTexSize = (int)D3D11RendererSettings.Default.ModelMaxTextureSize;
                        var sceneCtx = new Viewport3D.Modifiers.SceneModifierContext();
                        var entryWorlds = new Matrix4x4[builtInEntries.Count];

                        for (int bi = 0; bi < builtInEntries.Count; bi++)
                        {
                            var be = builtInEntries[bi];
                            var dummyRi = new RenderItem { DrawDescription = be.DrawDesc, Srv = null };
                            var worldMat = DepthSortRenderer.BuildObjModelMatrix(dummyRi, be.ModelData);
                            entryWorlds[bi] = worldMat;

                            string baseKey = !string.IsNullOrWhiteSpace(be.EmbeddedData)
                                ? $"emb:{ModelGpuCache.ComputeStringHash64(be.EmbeddedData)}:{be.EmbeddedData.Length}:{be.FilePath}"
                                : (!string.IsNullOrWhiteSpace(be.ModelData.ProceduralCacheKey)
                                    ? $"proc:{be.ModelData.ProceduralCacheKey}"
                                    : $"file:{be.FilePath}");

                            var captured = be;
                            sceneCtx.RegisterTarget(new Viewport3D.Modifiers.SceneModifierTarget
                            {
                                Layer = captured.Item.Layer,
                                DisplayName = GetItemDisplayName(captured.Item, $"レイヤー {captured.Item.Layer}"),
                                BaseKey = baseKey,
                                ModelToWorld = worldMat,
                                ResolveBaseMesh = () => ModelGpuCache.GetOrLoadBaseEditableMesh(
                                    captured.EmbeddedData,
                                    captured.FilePath,
                                    captured.ModelData.ProceduralMeshFactory,
                                    out _),
                                ModifierStackData = captured.ModData,
                            });
                        }

                        for (int bi = 0; bi < builtInEntries.Count; bi++)
                        {
                            var be = builtInEntries[bi];
                            if (be.RenderItemIdx is not { } rIdx) continue;
                            if (!Viewport3D.Modifiers.MeshModifierStack.HasSceneDependentModifiers(be.ModData)) continue;

                            var evalTime = new Viewport3D.Shading.ShaderEvalTime(be.Frame, be.Length, fps);
                            var resolved = ModelGpuCache.GetOrBuildWithModifiersAtTime(
                                devices.D3D.Device,
                                devices.D3D.DeviceContext,
                                be.EmbeddedData,
                                be.FilePath,
                                be.ModelData.ProceduralCacheKey,
                                be.ModelData.ProceduralMeshFactory,
                                be.ModData,
                                sceneCtx,
                                be.Item.Layer,
                                entryWorlds[bi],
                                maxTexSize,
                                evalTime);

                            if (resolved != null && renderItems[rIdx].ObjModels is { Count: > 0 } omList)
                            {
                                omList[0] = be.ModelData.WithCachedModel(resolved);
                            }
                        }
                    }

                    ApplyPostEffectsToRenderItems(renderItems, d3dItems);

                    if (renderItems.Count == 0 && !wantSkyboxOnly)
                    {
                        cached.Renderer.BakedTexturePool.EndFrame(frameSeq);
                        resultTexture = null;
                    }
                    else
                    {
                        PrepareRequestedLayerTextures(devices, dc, d2dItems, renderItems, cached.Renderer);
                        resultTexture = cached.Renderer.Render(devices, renderItems, frameLights, width, height, dofParams, atmosphereParams, envParams);
                        cached.Renderer.BakedTexturePool.EndFrame(frameSeq);
                    }
                }

                if (captureViewport)
                {
                    using var vpSaver = ymmCtx != null ? new D3D11StateSaver(ymmCtx) : null;
                    PublishViewportSnapshot(
                        devices,
                        d3dItems, d2dItems, frameLights, frameLightEntries,
                        width, height, fps, currentSceneFrame, in envParams);
                }

                if (resultTexture == null) return;

                using var composeSaver = ymmCtx != null ? new D3D11StateSaver(ymmCtx) : null;

                if (usage != TimelineSourceUsage.Exporting)
                {
                    UpdateMainPreviewItemRects(__instance, renderItems, frameLightEntries, width, height, d3dItems);
                }

                var resultBitmap = cached.Renderer.RenderTargetBitmap;
                if (resultBitmap == null || resultBitmap.NativePointer == IntPtr.Zero) return;

                var oldCommandList2 = _commandListField!.GetValue(__instance) as ID2D1CommandList;

                var isolatedDc = EnsureIsolatedDc(cached, dc);
                var newCommandList = isolatedDc.CreateCommandList();
                isolatedDc.Target = newCommandList;
                isolatedDc.BeginDraw();
                isolatedDc.Clear(new Color4(0f, 0f, 0f, 0f));

                // YMM4 の GetOrderedTimelineResources 準拠で D2D アイテムを描画順ソート
                var orderedD2d = d2dItems
                    .OrderBy(x => x.item.IsAlwaysOnTop ? 1 : 0)
                    .ThenBy(x => !x.item.IsZOrderEnabled ? 0f : GetEisZIndex(x.eisValue))
                    .ThenBy(x => x.item.Layer)
                    .ToList();

                int d2dIdx = 0;
                for (; d2dIdx < orderedD2d.Count && !orderedD2d[d2dIdx].item.IsAlwaysOnTop; d2dIdx++)
                {
                    DrawD2DItem(isolatedDc, orderedD2d[d2dIdx].item, orderedD2d[d2dIdx].eisValue);
                }

                isolatedDc.DrawImage(resultBitmap, new Vector2(-width / 2f, -height / 2f));

                for (; d2dIdx < orderedD2d.Count; d2dIdx++)
                {
                    DrawD2DItem(isolatedDc, orderedD2d[d2dIdx].item, orderedD2d[d2dIdx].eisValue);
                }

                // 書き出し時 (Exporting) は光源ギズモを描画しない
                if (usage != TimelineSourceUsage.Exporting && frameLights.Count > 0)
                {
                    bool hasVisibleGizmo = false;
                    for (int i = 0; i < frameLights.Count; i++)
                    {
                        if (frameLights[i].ShowGizmo) { hasVisibleGizmo = true; break; }
                    }
                    if (hasVisibleGizmo)
                    {
                        var sceneCameraMatrix = renderItems.Count > 0
                            ? renderItems[0].DrawDescription.Camera
                            : (envParams.FallbackCameraMatrix.M44 != 0f ? envParams.FallbackCameraMatrix : Matrix4x4.Identity);
                        DrawLightGizmos(isolatedDc, frameLights, sceneCameraMatrix);
                    }
                }

                isolatedDc.EndDraw();
                isolatedDc.Target = null;
                newCommandList.Close();

                _commandListField.SetValue(__instance, newCommandList);
                oldCommandList2?.Dispose();
            }
            catch (Exception ex)
            {
                Log($"UpdatePostfix 例外: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    private static void DrawD2DItem(ID2D1DeviceContext6 isolatedDc, IVideoItem item, object eisValue)
    {
        var output = _eisOutputProp!.GetValue(eisValue) as ID2D1Image;
        if (output == null) return;

        var blend = item.Blend;
        if (blend.IsCompositionEffect())
            isolatedDc.DrawImage(output, Vortice.Direct2D1.InterpolationMode.MultiSampleLinear, blend.ToD2DCompositionMode());
        else
            isolatedDc.BlendImage(output, blend.ToD2DBlendMode(), null, null, Vortice.Direct2D1.InterpolationMode.MultiSampleLinear);
    }

    private static void PrepareRequestedLayerTextures(
        IGraphicsDevicesAndContext devices,
        ID2D1DeviceContext dc,
        List<(IVideoItem item, object eisValue)> d2dItems,
        List<RenderItem> renderItems,
        DepthSortRenderer renderer)
    {
        HashSet<int>? neededLayers = null;
        for (int i = 0; i < renderItems.Count; i++)
        {
            var models = renderItems[i].ObjModels;
            if (models == null) continue;
            for (int m = 0; m < models.Count; m++)
            {
                var parts = models[m].Parts;
                for (int p = 0; p < parts.Length; p++)
                {
                    int al = parts[p].LayerTextureAlbedoLayerPlusOne;
                    if (al > 0) (neededLayers ??= new()).Add(al - 1);
                    int em = parts[p].LayerTextureEmissiveLayerPlusOne;
                    if (em > 0) (neededLayers ??= new()).Add(em - 1);
                }
            }
        }
        if (neededLayers == null || neededLayers.Count == 0) return;

        var d3dDev = devices.D3D.Device;
        foreach (int reqLayer in neededLayers)
        {
            if (Viewport3D.Shading.LayerTextureRegistry.TryGetSrv(d3dDev, reqLayer) != null)
                continue;

            for (int i = 0; i < d2dItems.Count; i++)
            {
                var (item, eisValue) = d2dItems[i];
                if (item.Layer != reqLayer) continue;

                ID2D1Image? sourceImg = null;
                var esoList = _effectedSourceOutputsField?.GetValue(eisValue) as IList;
                if (esoList != null && esoList.Count > 0 && esoList[0] != null)
                    sourceImg = _esoPreRenderOutputProp?.GetValue(esoList[0]!) as ID2D1Image;
                sourceImg ??= _eisOutputProp?.GetValue(eisValue) as ID2D1Image;
                if (sourceImg == null) continue;

                RawRectF bounds;
                try { bounds = dc.GetImageLocalBounds(sourceImg); }
                catch { continue; }
                if (float.IsInfinity(bounds.Left) || float.IsInfinity(bounds.Top) ||
                    float.IsInfinity(bounds.Right) || float.IsInfinity(bounds.Bottom)) continue;

                int left = (int)MathF.Floor(bounds.Left);
                int top = (int)MathF.Floor(bounds.Top);
                int right = (int)MathF.Ceiling(bounds.Right);
                int bottom = (int)MathF.Ceiling(bounds.Bottom);
                int texW = Math.Clamp(right - left, 1, 4096);
                int texH = Math.Clamp(bottom - top, 1, 4096);

                var srv = renderer.BakedTexturePool.Bake(sourceImg, devices, texW, texH, -left, -top, renderer.IsHdrOutput);
                if (srv != null)
                {
                    Viewport3D.Shading.LayerTextureRegistry.Register(d3dDev, reqLayer, srv);
                    break;
                }
            }
        }
    }

    private static void TryCollectDof(IVideoItem key, object eisValue, long currentSceneFrame, int fps, ref DofParameters dofParams)
    {
        try
        {
            D3DDepthOfFieldVideoEffect? dofEffect = null;
            if (key.GetType().Name != "GroupItem" && key.VideoEffects != null)
            {
                for (int i = 0; i < key.VideoEffects.Count; i++)
                {
                    if (key.VideoEffects[i] is D3DDepthOfFieldVideoEffect candidate && candidate.IsEnabled)
                    {
                        dofEffect = candidate;
                        break;
                    }
                }
            }

            if (dofEffect == null && _eisParentEffectsProp != null && _tsepEffectItemProp != null)
            {
                if (_eisParentEffectsProp.GetValue(eisValue) is IEnumerable parentEffects)
                {
                    foreach (object pePair in parentEffects)
                    {
                        if (_tsepEffectItemProp.GetValue(pePair) is D3DDepthOfFieldVideoEffect parentDof && parentDof.IsEnabled)
                        {
                            dofEffect = parentDof;
                            break;
                        }
                    }
                }
            }

            if (dofEffect != null)
            {
                long itemFrame = Math.Max(0, currentSceneFrame - key.Frame);
                long itemLength = Math.Max(1, key.Length);

                dofParams.Enabled = true;
                dofParams.FocusDistance = (float)dofEffect.FocusDistance.GetValue(itemFrame, itemLength, fps);
                dofParams.Aperture = (float)dofEffect.Aperture.GetValue(itemFrame, itemLength, fps);
                dofParams.FocusRange = (float)dofEffect.FocusRange.GetValue(itemFrame, itemLength, fps);
                dofParams.NearBlurScale = (float)dofEffect.NearBlurScale.GetValue(itemFrame, itemLength, fps) / 100f;
                dofParams.FarBlurScale = (float)dofEffect.FarBlurScale.GetValue(itemFrame, itemLength, fps) / 100f;
                dofParams.MaxBlur = (float)dofEffect.MaxBlur.GetValue(itemFrame, itemLength, fps);
            }
        }
        catch (Exception ex)
        {
            Log($"D3D被写界深度パラメータ取得エラー: {ex.Message}");
        }
    }

    private static void TryCollectAtmosphere(IVideoItem key, object eisValue, long currentSceneFrame, int fps, ref AtmosphereParameters atmosphere)
    {
        try
        {
            D3DAtmosphereVideoEffect? effect = null;
            if (key.GetType().Name != "GroupItem" && key.VideoEffects != null)
            {
                for (int i = 0; i < key.VideoEffects.Count; i++)
                {
                    if (key.VideoEffects[i] is D3DAtmosphereVideoEffect candidate && candidate.IsEnabled)
                    {
                        effect = candidate;
                        break;
                    }
                }
            }

            if (effect == null && _eisParentEffectsProp != null && _tsepEffectItemProp != null)
            {
                if (_eisParentEffectsProp.GetValue(eisValue) is IEnumerable parentEffects)
                {
                    foreach (object pePair in parentEffects)
                    {
                        if (_tsepEffectItemProp.GetValue(pePair) is D3DAtmosphereVideoEffect parentEff && parentEff.IsEnabled)
                        {
                            effect = parentEff;
                            break;
                        }
                    }
                }
            }

            if (effect != null)
            {
                long itemFrame = Math.Max(0, currentSceneFrame - key.Frame);
                long itemLength = Math.Max(1, key.Length);
                atmosphere = effect.Evaluate(itemFrame, itemLength, fps);
            }
        }
        catch (Exception ex)
        {
            Log($"D3D大気・フォグパラメータ取得エラー: {ex.Message}");
        }
    }

    private static MaterialOverrideParameters TryCollectMaterial(IVideoItem item, object eisValue, long itemFrame, long itemLength, int fps)
    {
        try
        {
            D3DMaterialVideoEffect? matEffect = null;
            var videoEffects = item.VideoEffects;
            if (videoEffects != null)
            {
                for (int i = 0; i < videoEffects.Count; i++)
                {
                    if (videoEffects[i] is D3DMaterialVideoEffect candidate && candidate.IsEnabled)
                    {
                        matEffect = candidate;
                        break;
                    }
                }
            }

            if (matEffect == null && _eisParentEffectsProp != null && _tsepEffectItemProp != null)
            {
                if (_eisParentEffectsProp.GetValue(eisValue) is IEnumerable parentEffects)
                {
                    foreach (object pePair in parentEffects)
                    {
                        if (_tsepEffectItemProp.GetValue(pePair) is D3DMaterialVideoEffect parentMat && parentMat.IsEnabled)
                        {
                            matEffect = parentMat;
                            break;
                        }
                    }
                }
            }

            if (matEffect != null)
                return matEffect.Evaluate(itemFrame, itemLength, fps);
        }
        catch (Exception ex)
        {
            Log($"D3Dマテリアル取得エラー: {ex.Message}");
        }
        return default;
    }

    private static bool HasD3DPostOrLayoutVideoEffect(IVideoItem item, object eisValue)
    {
        try
        {
            if (item.GetType().Name != "GroupItem" && item.VideoEffects != null)
            {
                for (int i = 0; i < item.VideoEffects.Count; i++)
                {
                    if ((item.VideoEffects[i] is D3DOutlineVideoEffect or D3DRandomPlacementVideoEffect) &&
                        item.VideoEffects[i].IsEnabled)
                        return true;
                }
            }
            if (_eisParentEffectsProp != null && _tsepEffectItemProp != null &&
                _eisParentEffectsProp.GetValue(eisValue) is IEnumerable parentEffects)
            {
                foreach (object pePair in parentEffects)
                {
                    var effObj = _tsepEffectItemProp.GetValue(pePair);
                    if ((effObj is D3DOutlineVideoEffect oe && oe.IsEnabled) ||
                        (effObj is D3DRandomPlacementVideoEffect re && re.IsEnabled))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static OutlineParameters TryCollectOutline(IVideoItem item, object eisValue, long itemFrame, long itemLength, int fps)
    {
        try
        {
            D3DOutlineVideoEffect? effect = null;
            if (item.GetType().Name != "GroupItem" && item.VideoEffects != null)
            {
                for (int i = 0; i < item.VideoEffects.Count; i++)
                {
                    if (item.VideoEffects[i] is D3DOutlineVideoEffect candidate && candidate.IsEnabled)
                    {
                        effect = candidate;
                        break;
                    }
                }
            }
            if (effect == null && _eisParentEffectsProp != null && _tsepEffectItemProp != null &&
                _eisParentEffectsProp.GetValue(eisValue) is IEnumerable parentEffects)
            {
                foreach (object pePair in parentEffects)
                {
                    if (_tsepEffectItemProp.GetValue(pePair) is D3DOutlineVideoEffect parentEff && parentEff.IsEnabled)
                    {
                        effect = parentEff;
                        break;
                    }
                }
            }
            if (effect != null)
                return effect.Evaluate(itemFrame, itemLength, fps);
        }
        catch (Exception ex)
        {
            Log($"D3D縁取り取得エラー: {ex.Message}");
        }
        return default;
    }

    private static D3DRandomPlacementVideoEffect? TryCollectRandomPlacement(IVideoItem item, object eisValue)
    {
        try
        {
            if (item.GetType().Name != "GroupItem" && item.VideoEffects != null)
            {
                for (int i = 0; i < item.VideoEffects.Count; i++)
                {
                    if (item.VideoEffects[i] is D3DRandomPlacementVideoEffect candidate && candidate.IsEnabled)
                        return candidate;
                }
            }
            if (_eisParentEffectsProp != null && _tsepEffectItemProp != null &&
                _eisParentEffectsProp.GetValue(eisValue) is IEnumerable parentEffects)
            {
                foreach (object pePair in parentEffects)
                {
                    if (_tsepEffectItemProp.GetValue(pePair) is D3DRandomPlacementVideoEffect parentEff && parentEff.IsEnabled)
                        return parentEff;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"D3Dランダム配置取得エラー: {ex.Message}");
        }
        return null;
    }

    private static void ApplyPostEffectsToRenderItems(
        List<RenderItem> renderItems,
        List<(IVideoItem item, object eisValue)> d3dItems)
    {
        if (renderItems.Count == 0 || d3dItems.Count == 0) return;

        var eisMap = new Dictionary<IVideoItem, object>(d3dItems.Count);
        for (int i = 0; i < d3dItems.Count; i++)
            eisMap.TryAdd(d3dItems[i].item, d3dItems[i].eisValue);

        var expanded = new List<RenderItem>(renderItems.Count);
        for (int i = 0; i < renderItems.Count; i++)
        {
            var ri = renderItems[i];
            if (ri.OriginalItem != null && eisMap.TryGetValue(ri.OriginalItem, out var eisValue))
            {
                var outline = TryCollectOutline(ri.OriginalItem, eisValue, ri.ItemFrame, ri.ItemLength, ri.Fps);
                var randEff = TryCollectRandomPlacement(ri.OriginalItem, eisValue);
                if (randEff != null)
                {
                    var instances = randEff.EvaluateInstances(ri.DrawDescription, ri.ItemFrame, ri.ItemLength, ri.Fps);
                    for (int k = 0; k < instances.Count; k++)
                        expanded.Add(ri.CloneWith(instances[k], outline));
                    continue;
                }
                ri.Outline = outline;
            }
            expanded.Add(ri);
        }

        if (expanded.Count != renderItems.Count)
        {
            renderItems.Clear();
            renderItems.AddRange(expanded);
        }
    }

    private static void ApplyPostEffectsToViewportSnapshot(
        ViewportSceneSnapshot snapshot,
        List<(IVideoItem item, object eisValue)> combinedVisualItems,
        int fps,
        long currentSceneFrame)
    {
        if (snapshot.Items.Count == 0 || combinedVisualItems.Count == 0) return;

        var eisMap = new Dictionary<IVideoItem, object>(combinedVisualItems.Count);
        for (int i = 0; i < combinedVisualItems.Count; i++)
            eisMap.TryAdd(combinedVisualItems[i].item, combinedVisualItems[i].eisValue);

        for (int i = 0; i < snapshot.Items.Count; i++)
        {
            var entry = snapshot.Items[i];
            if (entry.Kind == ViewportItemKind.Light) continue;
            if (!eisMap.TryGetValue(entry.Item, out var eisValue)) continue;

            long itemFrame = Math.Max(0, currentSceneFrame - entry.Item.Frame);
            long itemLength = Math.Max(1, entry.Item.Length);
            entry.Outline = TryCollectOutline(entry.Item, eisValue, itemFrame, itemLength, fps);

            var randEff = TryCollectRandomPlacement(entry.Item, eisValue);
            if (randEff != null)
                entry.RandomInstanceDrawDescs = randEff.EvaluateInstances(entry.DrawDescription, itemFrame, itemLength, fps);
        }
    }

    private static IDWriteTextFormat? GetGizmoTextFormat()
    {
        if (!_dwriteInitAttempted)
        {
            _dwriteInitAttempted = true;
            try
            {
                _dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
                _gizmoTextFormat = _dwriteFactory.CreateTextFormat(
                    "Yu Gothic UI", null,
                    FontWeight.Bold,
                    Vortice.DirectWrite.FontStyle.Normal,
                    FontStretch.Normal,
                    11f, "ja-jp");
            }
            catch
            {
                _gizmoTextFormat = null;
            }
        }
        return _gizmoTextFormat;
    }

    private static void DrawLightGizmos(ID2D1DeviceContext6 dc, List<LightData> lights, Matrix4x4 cameraMatrix)
    {
        var d2dProj = new Matrix4x4(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, -1f / 1000f,
            0f, 0f, 0f, 1f
        );
        Matrix4x4 vp = cameraMatrix * d2dProj;

        Vector2? Project(Vector3 worldPos)
        {
            Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1f), vp);
            if (clip.W <= 0.0001f) return null;
            return new Vector2(clip.X / clip.W, clip.Y / clip.W);
        }

        using var whiteBrush = dc.CreateSolidColorBrush(new Color4(1f, 1.0f, 1.0f, 1.0f));
        using var blackBrush = dc.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0.8f));
        var textFormat = GetGizmoTextFormat();

        for (int i = 0; i < lights.Count; i++)
        {
            var light = lights[i];
            if (!light.ShowGizmo) continue;

            Vector3 pos = light.Position;
            var screenPosOpt = Project(pos);
            if (screenPosOpt == null) continue;
            Vector2 screenPos = screenPosOpt.Value;

            var col = new Color4(light.Color.X, light.Color.Y, light.Color.Z, 1f);
            using var lightBrush = dc.CreateSolidColorBrush(col);

            if (light.Type == LightType.Directional)
            {
                Vector3 endPosWorld = pos + light.Direction * 120f;
                var endScreenOpt = Project(endPosWorld);
                if (endScreenOpt != null)
                {
                    dc.DrawLine(screenPos, endScreenOpt.Value, lightBrush, 2f);
                    dc.FillEllipse(new Ellipse(endScreenOpt.Value, 4f, 4f), lightBrush);
                }
            }
            else if (light.Type == LightType.Point)
            {
                Vector3[] directions = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
                foreach (var dir in directions)
                {
                    var rangePosOpt = Project(pos + dir * light.Range);
                    if (rangePosOpt != null)
                    {
                        dc.DrawLine(screenPos, rangePosOpt.Value, lightBrush, 1f);
                    }
                }
            }
            else if (light.Type == LightType.Spot)
            {
                Vector3 forward = light.Direction;
                var endScreenOpt = Project(pos + forward * light.Range);
                if (endScreenOpt != null)
                {
                    dc.DrawLine(screenPos, endScreenOpt.Value, lightBrush, 2f);
                }

                Vector3 up = MathF.Abs(forward.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
                Vector3 right = Vector3.Normalize(Vector3.Cross(forward, up));
                up = Vector3.Normalize(Vector3.Cross(right, forward));

                float radOuter = light.SpotOuterAngle * (MathF.PI / 180f);
                float cosO = MathF.Cos(radOuter);
                float sinO = MathF.Sin(radOuter);

                Vector3[] coneEnds =
                {
                    pos + (forward * cosO + right * sinO) * light.Range,
                    pos + (forward * cosO - right * sinO) * light.Range,
                    pos + (forward * cosO + up * sinO) * light.Range,
                    pos + (forward * cosO - up * sinO) * light.Range
                };

                foreach (var end in coneEnds)
                {
                    var coneEndOpt = Project(end);
                    if (coneEndOpt != null)
                    {
                        dc.DrawLine(screenPos, coneEndOpt.Value, lightBrush, 1f);
                    }
                }
            }
            else if (light.Type == LightType.Area)
            {
                var (forward, right, up) = LightManager.ComputeAreaBasis(light);

                float halfW = light.AreaWidth * 0.5f;
                float halfH = light.AreaHeight * 0.5f;

                var p1 = Project(pos + right * halfW + up * halfH);
                var p2 = Project(pos - right * halfW + up * halfH);
                var p3 = Project(pos - right * halfW - up * halfH);
                var p4 = Project(pos + right * halfW - up * halfH);

                if (p1 != null && p2 != null && p3 != null && p4 != null)
                {
                    dc.DrawLine(p1.Value, p2.Value, lightBrush, 1.5f);
                    dc.DrawLine(p2.Value, p3.Value, lightBrush, 1.5f);
                    dc.DrawLine(p3.Value, p4.Value, lightBrush, 1.5f);
                    dc.DrawLine(p4.Value, p1.Value, lightBrush, 1.5f);
                }

                var endScreenOpt = Project(pos + forward * light.Range);
                if (endScreenOpt != null)
                {
                    dc.DrawLine(screenPos, endScreenOpt.Value, lightBrush, 1f);
                }
            }

            dc.FillEllipse(new Ellipse(screenPos, 6f, 6f), lightBrush);
            dc.DrawEllipse(new Ellipse(screenPos, 6f, 6f), whiteBrush, 1.5f);
            dc.DrawEllipse(new Ellipse(screenPos, 12f, 12f), lightBrush, 0.5f);

            if (textFormat != null)
            {
                string label = $"Light {i}: {light.Type}";
                var textRectShadow = new RawRectF(screenPos.X + 11f, screenPos.Y - 14f, screenPos.X + 250f, screenPos.Y + 20f);
                var textRect = new RawRectF(screenPos.X + 10f, screenPos.Y - 15f, screenPos.X + 250f, screenPos.Y + 20f);

                dc.DrawText(label, textFormat, textRectShadow, blackBrush);
                dc.DrawText(label, textFormat, textRect, whiteBrush);
            }
        }
    }

    internal static void GetSceneVideoInfo(object? scene, out int width, out int height, out double fps)
    {
        width = 1920;
        height = 1080;
        fps = 60.0;
        if (scene == null) return;
        try
        {
            var tl = _sceneTimelineProp?.GetValue(scene);
            var vi = _timelineVideoInfoProp?.GetValue(tl);
            if (vi == null) return;

            if (_videoInfoWidthProp?.GetValue(vi) is int w && w > 0) width = w;
            if (_videoInfoHeightProp?.GetValue(vi) is int h && h > 0) height = h;
            var fpsVal = _videoInfoFpsProp?.GetValue(vi);
            if (fpsVal != null)
            {
                double f = Convert.ToDouble(fpsVal);
                if (f > 0) fps = f;
            }
        }
        catch { }
    }

    internal static long ComputeSceneFrame(TimeSpan time, int fps, object? scene = null)
    {
        int safeFps = Math.Max(1, fps);
        long fromTime = YukkuriMovieMaker.Commons.FrameTime.TimeToFrame(time, safeFps);
        if (fromTime != 0 || time != TimeSpan.Zero)
            return fromTime;
        return GetSceneCurrentFrame(scene);
    }

    private static long GetSceneCurrentFrame(object? scene)
    {
        if (scene == null || _sceneCurrentFrameProp == null) return 0;
        try
        {
            var cf = _sceneCurrentFrameProp.GetValue(scene);
            return cf != null ? Convert.ToInt64(cf) : 0;
        }
        catch { return 0; }
    }

    private static float GetEisZIndex(object eisValue)
    {
        if (_eisGetZIndexMethod == null) return 0f;
        try
        {
            return _eisGetZIndexMethod.Invoke(eisValue, null) is float z ? z : 0f;
        }
        catch { return 0f; }
    }

    private static void EisUpdatePrefix(object __instance, ref AaState __state)
    {
        __state = default;
        try
        {
            var item = _eisItemField!.GetValue(__instance) as IVideoItem;
            if (item == null || !IsEnabledForItem(item)) return;

            if (D3D11RendererSettings.Default.TransparencyMode == TransparencyMode.Standard)
                return;

            var devices = _eisDevicesField!.GetValue(__instance) as IGraphicsDevicesAndContext;
            if (devices != null)
            {
                var dc = devices.DeviceContext;
                __state.OriginalAA = dc.AntialiasMode;
                __state.OriginalTextAA = dc.TextAntialiasMode;
                __state.ShouldRestore = true;
                dc.AntialiasMode = AntialiasMode.Aliased;
                dc.TextAntialiasMode = TextAntialiasMode.Aliased;
            }

            var source = _eisSourceProp?.GetValue(__instance);
            if (source == null) return;

            var srcType = source.GetType();
            var srcDevicesField = _sourceDevicesFieldCache.GetOrAdd(srcType, static t =>
                t.GetField("devices", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? t.BaseType?.GetField("devices", BindingFlags.Instance | BindingFlags.NonPublic));
            if (srcDevicesField == null) return;

            var srcDevices = srcDevicesField.GetValue(source) as IGraphicsDevicesAndContext;
            if (srcDevices == null) return;

            var srcDc = srcDevices.DeviceContext;
            __state.OrigSourceAA = srcDc.AntialiasMode;
            __state.OrigSourceTextAA = srcDc.TextAntialiasMode;
            __state.ShouldRestoreSource = true;
            __state.SourceDevices = srcDevices;
            srcDc.AntialiasMode = AntialiasMode.Aliased;
            srcDc.TextAntialiasMode = TextAntialiasMode.Aliased;
        }
        catch (Exception ex)
        {
            Log($"EisUpdatePrefix 例外: {ex.Message}");
        }
    }

    private static void EisUpdatePostfix(object __instance, ref AaState __state)
    {
        try
        {
            if (__state.ShouldRestore)
            {
                var devices = _eisDevicesField!.GetValue(__instance) as IGraphicsDevicesAndContext;
                if (devices != null)
                {
                    var dc = devices.DeviceContext;
                    dc.AntialiasMode = __state.OriginalAA;
                    dc.TextAntialiasMode = __state.OriginalTextAA;
                }
            }

            if (__state.ShouldRestoreSource && __state.SourceDevices is IGraphicsDevicesAndContext srcDevices)
            {
                var srcDc = srcDevices.DeviceContext;
                srcDc.AntialiasMode = __state.OrigSourceAA;
                srcDc.TextAntialiasMode = __state.OrigSourceTextAA;
            }
        }
        catch (Exception ex)
        {
            Log($"EisUpdatePostfix 例外: {ex.Message}");
        }
    }

    private static bool IsBuiltInModelItem(IVideoItem item)
    {
        if (_shapeItemType == null || _shapeType2Prop == null)
            return false;
        if (!_shapeItemType.IsInstanceOfType(item))
            return false;
        var shapeType = _shapeType2Prop.GetValue(item) as Type;
        return shapeType == _modelShapePluginType || shapeType == _primitiveShapePluginType;
    }

    private static ID3DVideoEffect? FindEnabledD3DVideoEffect(IVideoItem item)
    {
        var videoEffects = item.VideoEffects;
        if (videoEffects == null) return null;
        for (int vi = 0; vi < videoEffects.Count; vi++)
        {
            var ve = videoEffects[vi];
            if (ve is ID3DVideoEffect candidate && ve.IsEnabled)
                return candidate;
        }
        return null;
    }

    private static bool TryGetBuiltInModelData(IVideoItem item, object eisValue, out ObjModelData? modelData)
        => TryGetBuiltInModelCapture(item, eisValue, out _, out _, out _, out modelData);

    private static bool TryGetBuiltInModelCapture(
        IVideoItem item,
        object eisValue,
        out string filePath,
        out ObjModelData? modelData)
        => TryGetBuiltInModelCapture(item, eisValue, out filePath, out _, out _, out modelData);

    private static bool TryGetBuiltInModelCapture(
        IVideoItem item,
        object eisValue,
        out string filePath,
        out string embeddedMeshData,
        out string modifierStackData,
        out ObjModelData? modelData)
    {
        filePath = string.Empty;
        embeddedMeshData = string.Empty;
        modifierStackData = string.Empty;
        modelData = null;
        if (_eisSourceProp == null || !IsBuiltInModelItem(item))
            return false;

        try
        {
            var source = _eisSourceProp.GetValue(eisValue);
            if (source == null) return false;

            if (_shapeSourceType != null && _shapeSourceType.IsInstanceOfType(source)
                && _shapeSourceInternalSourceField != null)
            {
                var internalSource = _shapeSourceInternalSourceField.GetValue(source);
                if (internalSource != null)
                    source = internalSource;
            }

            if (source is not ID3DBuiltInModelSource modelSource)
                return false;

            filePath = modelSource.FilePath ?? string.Empty;
            embeddedMeshData = modelSource.EmbeddedMeshData ?? string.Empty;
            modifierStackData = modelSource.ModifierStackData ?? string.Empty;
            modelData = modelSource.LastModelData;
            return modelData != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCollectLight(
        IVideoItem item,
        object eisValue,
        out LightData? lightData,
        out DrawDescription? lightDrawDesc)
    {
        lightData = null;
        lightDrawDesc = null;
        if (_shapeItemType == null || _shapeType2Prop == null || _eisSourceProp == null)
            return false;

        try
        {
            if (!_shapeItemType.IsInstanceOfType(item))
                return false;

            var shapeType = _shapeType2Prop.GetValue(item) as Type;
            if (shapeType != _lightShapePluginType)
                return false;

            var source = _eisSourceProp.GetValue(eisValue);
            if (source == null) return false;

            if (_shapeSourceType != null && _shapeSourceType.IsInstanceOfType(source)
                && _shapeSourceInternalSourceField != null)
            {
                var internalSource = _shapeSourceInternalSourceField.GetValue(source);
                if (internalSource != null)
                    source = internalSource;
            }

            if (source is not D3DLightShapeSource lightSource)
                return false;

            var srcLight = lightSource.LastRegisteredLight;
            if (srcLight == null) return false;

            // 毎フレーム複製し、DrawDescription の Position / Rotation を安全に反映する
            var collected = new LightData
            {
                Type = srcLight.Type,
                Position = srcLight.Position,
                Direction = srcLight.Direction,
                AreaRight = srcLight.AreaRight,
                HasExplicitAreaRight = srcLight.HasExplicitAreaRight,
                Color = srcLight.Color,
                Intensity = srcLight.Intensity,
                Range = srcLight.Range,
                SpotInnerAngle = srcLight.SpotInnerAngle,
                SpotOuterAngle = srcLight.SpotOuterAngle,
                AreaWidth = srcLight.AreaWidth,
                AreaHeight = srcLight.AreaHeight,
                CastShadow = srcLight.CastShadow,
                ShadowIntensity = srcLight.ShadowIntensity,
                ShadowBias = srcLight.ShadowBias,
                ShadowNormalBias = srcLight.ShadowNormalBias,
                ShadowSoftness = srcLight.ShadowSoftness,
                ShowGizmo = srcLight.ShowGizmo,
                Layer = item.Layer,
            };

            var esoList = _effectedSourceOutputsField?.GetValue(eisValue) as IList;
            if (esoList != null && esoList.Count > 0)
            {
                var firstEso = esoList[0];
                if (firstEso != null && _esoDrawDescProp?.GetValue(firstEso) is DrawDescription drawDesc)
                {
                    lightDrawDesc = drawDesc;
                    collected.Position = drawDesc.Draw;

                    if (drawDesc.Rotation != Vector3.Zero)
                    {
                        float d2r = MathF.PI / 180f;
                        var rot = Matrix4x4.CreateRotationZ(d2r * drawDesc.Rotation.Z)
                                * Matrix4x4.CreateRotationY(d2r * -drawDesc.Rotation.Y)
                                * Matrix4x4.CreateRotationX(d2r * -drawDesc.Rotation.X);

                        var (baseFwd, baseRight, _) = LightManager.ComputeAreaBasis(collected);
                        collected.Direction = Vector3.Normalize(Vector3.TransformNormal(baseFwd, rot));
                        collected.AreaRight = Vector3.Normalize(Vector3.TransformNormal(baseRight, rot));
                        collected.HasExplicitAreaRight = true;
                    }
                }
            }

            lightData = collected;
            return true;
        }
        catch (Exception ex)
        {
            Log($"TryCollectLight 例外: {ex.Message}");
            return false;
        }
    }

    private static bool TryCollectEnvironment(
        IVideoItem item,
        object eisValue,
        out EnvironmentParameters envData,
        out AtmosphereParameters atmoData,
        out Matrix4x4 cameraMatrix)
    {
        envData = default;
        atmoData = default;
        cameraMatrix = default;
        if (_shapeItemType == null || _shapeType2Prop == null || _eisSourceProp == null)
            return false;

        try
        {
            if (!_shapeItemType.IsInstanceOfType(item))
                return false;

            var shapeType = _shapeType2Prop.GetValue(item) as Type;
            if (shapeType != _envShapePluginType)
                return false;

            var source = _eisSourceProp.GetValue(eisValue);
            if (source == null) return false;

            if (_shapeSourceType != null && _shapeSourceType.IsInstanceOfType(source)
                && _shapeSourceInternalSourceField != null)
            {
                var internalSource = _shapeSourceInternalSourceField.GetValue(source);
                if (internalSource != null)
                    source = internalSource;
            }

            if (source is not D3DEnvironmentShapeSource envSource)
                return false;

            envData = envSource.LastEnvironment;
            atmoData = envSource.LastAtmosphere;

            var esoList = _effectedSourceOutputsField?.GetValue(eisValue) as IList;
            if (esoList != null && esoList.Count > 0 && esoList[0] != null
                && _esoDrawDescProp?.GetValue(esoList[0]!) is DrawDescription drawDesc)
            {
                cameraMatrix = drawDesc.Camera;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"TryCollectEnvironment 例外: {ex.Message}");
            return false;
        }
    }

    internal static void UpdateMainPreviewItemRects(
        object timelineSourceInstance,
        List<RenderItem> renderItems,
        List<(IVideoItem item, LightData light, DrawDescription? drawDesc)> frameLightEntries,
        int width,
        int height)
        => UpdateMainPreviewItemRects(timelineSourceInstance, renderItems, frameLightEntries, width, height, null);

    internal static void UpdateMainPreviewItemRects(
        object timelineSourceInstance,
        List<RenderItem> renderItems,
        List<(IVideoItem item, LightData light, DrawDescription? drawDesc)> frameLightEntries,
        int width,
        int height,
        List<(IVideoItem item, object eisValue)>? d3dItems)
    {
        if (_needTimelineItemRectsProp?.GetValue(timelineSourceInstance) is not true)
            return;

        var rectsObj = _timelineItemRectsProp?.GetValue(timelineSourceInstance);
        if (rectsObj == null)
            return;

        Dictionary<IVideoItem, (IVideoItem? GroupItem, DrawDescription? OrigDesc)>? groupLookup = null;
        if (d3dItems != null && d3dItems.Count > 0)
        {
            groupLookup = new Dictionary<IVideoItem, (IVideoItem? GroupItem, DrawDescription? OrigDesc)>(
                d3dItems.Count, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < d3dItems.Count; i++)
            {
                var (item, eisValue) = d3dItems[i];
                TryResolveGroupAndDrawDesc(eisValue, out var grp, out var origDesc);
                if (grp != null)
                    groupLookup[item] = (grp, origDesc);
            }
        }

        // アイテム単位で RenderItem (D3Dランダム配置の複数インスタンス含む) をグループ化
        var groupedByItem = new Dictionary<IVideoItem, List<RenderItem>>(ReferenceEqualityComparer.Instance);
        for (int r = 0; r < renderItems.Count; r++)
        {
            var ri = renderItems[r];
            if (ri.OriginalItem == null || ri.ObjModels == null || ri.ObjModels.Count == 0)
                continue;
            if (!groupedByItem.TryGetValue(ri.OriginalItem, out var list))
            {
                list = new List<RenderItem>(1);
                groupedByItem[ri.OriginalItem] = list;
            }
            list.Add(ri);
        }

        static bool IsItemRectMatch(
            IVideoItem rectItem,
            DrawDescription rectDesc,
            IVideoItem origItem,
            Dictionary<IVideoItem, (IVideoItem? GroupItem, DrawDescription? OrigDesc)>? lookup)
        {
            if (ReferenceEquals(rectItem, origItem))
                return true;
            if (lookup != null && lookup.TryGetValue(origItem, out var info) && info.GroupItem != null)
            {
                if (ReferenceEquals(rectItem, info.GroupItem) &&
                    (info.OrigDesc == null || ReferenceEquals(rectDesc, info.OrigDesc)))
                {
                    return true;
                }
            }
            return false;
        }

        if (rectsObj is List<(IVideoItem item, RawRectF rect, Vector2[] quad, DrawDescription desc, IEnumerable<VideoController> itemControllers)> rects5)
        {
            lock (rects5)
            {
                foreach (var (origItem, group) in groupedByItem)
                {
                    if (!DepthSortRenderer.TryComputeProjectedScreenBounds(group, width, height, out var projRect, out var projQuad))
                        continue;

                    for (int i = 0; i < rects5.Count; i++)
                    {
                        var cur = rects5[i];
                        if (IsItemRectMatch(cur.item, cur.desc, origItem, groupLookup))
                        {
                            rects5[i] = (cur.item, projRect, projQuad, cur.desc, cur.itemControllers);
                        }
                    }
                }

                if (frameLightEntries.Count > 0)
                {
                    var camMatrix = renderItems.Count > 0
                        ? renderItems[0].DrawDescription.Camera
                        : (frameLightEntries[0].drawDesc?.Camera ?? Matrix4x4.Identity);
                    for (int l = 0; l < frameLightEntries.Count; l++)
                    {
                        var (lightItem, light, _) = frameLightEntries[l];
                        if (!light.ShowGizmo) continue;
                        if (!DepthSortRenderer.TryComputeProjectedLightRect(light.Position, camMatrix, out var lightRect))
                            continue;
                        var lightQuad = DepthSortRenderer.RectToQuad(lightRect);

                        for (int i = 0; i < rects5.Count; i++)
                        {
                            if (ReferenceEquals(rects5[i].item, lightItem))
                            {
                                var cur = rects5[i];
                                rects5[i] = (cur.item, lightRect, lightQuad, cur.desc, cur.itemControllers);
                            }
                        }
                    }
                }
            }
        }
        else if (rectsObj is List<(IVideoItem item, RawRectF rect, DrawDescription desc, IEnumerable<VideoController> itemControllers)> rects4)
        {
            lock (rects4)
            {
                foreach (var (origItem, group) in groupedByItem)
                {
                    if (!DepthSortRenderer.TryComputeProjectedScreenBounds(group, width, height, out var projRect, out _))
                        continue;

                    for (int i = 0; i < rects4.Count; i++)
                    {
                        var cur = rects4[i];
                        if (IsItemRectMatch(cur.item, cur.desc, origItem, groupLookup))
                        {
                            rects4[i] = (cur.item, projRect, cur.desc, cur.itemControllers);
                        }
                    }
                }

                if (frameLightEntries.Count > 0)
                {
                    var camMatrix = renderItems.Count > 0
                        ? renderItems[0].DrawDescription.Camera
                        : (frameLightEntries[0].drawDesc?.Camera ?? Matrix4x4.Identity);
                    for (int l = 0; l < frameLightEntries.Count; l++)
                    {
                        var (lightItem, light, _) = frameLightEntries[l];
                        if (!light.ShowGizmo) continue;
                        if (!DepthSortRenderer.TryComputeProjectedLightRect(light.Position, camMatrix, out var lightRect))
                            continue;

                        for (int i = 0; i < rects4.Count; i++)
                        {
                            if (ReferenceEquals(rects4[i].item, lightItem))
                            {
                                var cur = rects4[i];
                                rects4[i] = (cur.item, lightRect, cur.desc, cur.itemControllers);
                            }
                        }
                    }
                }
            }
        }
    }

    private static void TryResolveGroupAndDrawDesc(
        object eisValue,
        out IVideoItem? groupItem,
        out DrawDescription? eisDrawDesc)
    {
        groupItem = null;
        eisDrawDesc = null;
        try
        {
            if (_eisParentEffectsProp != null && _tsepTimeSourceProp != null &&
                _eisParentEffectsProp.GetValue(eisValue) is IEnumerable parentEffects)
            {
                foreach (object pePair in parentEffects)
                {
                    var ts = _tsepTimeSourceProp.GetValue(pePair);
                    if (ts is IVideoItem tsVideo &&
                        (_groupItemType != null ? _groupItemType.IsInstanceOfType(tsVideo) : tsVideo.GetType().Name == "GroupItem"))
                    {
                        groupItem = tsVideo;
                    }
                }
            }

            if (groupItem != null && _effectedSourceOutputsField?.GetValue(eisValue) is IList esoList && esoList.Count > 0)
            {
                var firstEso = esoList[0];
                if (firstEso != null)
                    eisDrawDesc = _esoDrawDescProp?.GetValue(firstEso) as DrawDescription;
            }
        }
        catch { }
    }

    private static void PublishViewportSnapshot(
        IGraphicsDevicesAndContext devices,
        List<(IVideoItem item, object eisValue)> d3dItems,
        List<(IVideoItem item, object eisValue)> d2dItems,
        List<LightData> frameLights,
        List<(IVideoItem item, LightData light, DrawDescription? drawDesc)> frameLightEntries,
        int width,
        int height,
        int fps,
        long currentSceneFrame,
        in EnvironmentParameters envParams)
    {
        Matrix4x4 sceneCameraMatrix = ResolveSceneCameraMatrix(d3dItems, d2dItems, frameLightEntries, in envParams);

        var snapshot = new ViewportSceneSnapshot
        {
            SequenceId = Viewport3DBridge.NextSequenceId(),
            VideoWidth = width,
            VideoHeight = height,
            Fps = fps,
            CurrentFrame = currentSceneFrame,
            SceneCameraMatrix = sceneCameraMatrix,
            Environment = envParams,
        };
        snapshot.Lights.AddRange(frameLights);

        try
        {
            for (int i = 0; i < frameLightEntries.Count; i++)
            {
                var (lightItem, light, lightDrawDesc) = frameLightEntries[i];
                long itemFrame = Math.Max(0, currentSceneFrame - lightItem.Frame);
                long itemLength = Math.Max(1, lightItem.Length);
                var baseline = CaptureAnimBaseline(lightItem, itemFrame, itemLength, fps);

                var desc = lightDrawDesc ?? new DrawDescription(
                    light.Position,
                    Vector2.Zero,
                    Vector2.One,
                    Vector3.Zero,
                    sceneCameraMatrix,
                    Vortice.Direct2D1.InterpolationMode.Linear,
                    1.0,
                    false,
                    ImmutableList<VideoEffectController>.Empty);

                snapshot.Items.Add(new ViewportItemEntry
                {
                    Item = lightItem,
                    Kind = ViewportItemKind.Light,
                    DisplayName = GetItemDisplayName(lightItem, $"光源 ({light.Type})"),
                    DrawDescription = desc,
                    Layer = lightItem.Layer,
                    ItemFrame = itemFrame,
                    ItemLength = itemLength,
                    Fps = fps,
                    Opacity = 1f,
                    Light = light,
                    CapturedAnimPos = baseline.Pos,
                    CapturedAnimZoom = baseline.Zoom,
                    CapturedAnimRotZ = baseline.RotZ,
                    CapturedRotateEffectDeg = baseline.EffRot,
                    HasCapturedRotateEffect = baseline.HasEff,
                    HasCapturedAnimBaseline = true,
                });
            }

            var combinedVisualItems = new List<(IVideoItem item, object eisValue)>(d3dItems.Count + d2dItems.Count);
            combinedVisualItems.AddRange(d3dItems);
            combinedVisualItems.AddRange(d2dItems);
            combinedVisualItems.Sort(static (a, b) =>
            {
                int topCmp = (a.item.IsAlwaysOnTop ? 1 : 0).CompareTo(b.item.IsAlwaysOnTop ? 1 : 0);
                return topCmp != 0 ? topCmp : a.item.Layer.CompareTo(b.item.Layer);
            });
            AddViewportItems(snapshot, devices, combinedVisualItems, fps, currentSceneFrame);
            ApplyPostEffectsToViewportSnapshot(snapshot, combinedVisualItems, fps, currentSceneFrame);

            devices.D3D.DeviceContext?.Flush();
            Viewport3DBridge.PublishFrame(snapshot);
        }
        catch (Exception ex)
        {
            snapshot.Release();
            Log($"PublishViewportSnapshot 例外: {ex.Message}");
        }
    }

    private static void AddViewportItems(
        ViewportSceneSnapshot snapshot,
        IGraphicsDevicesAndContext devices,
        List<(IVideoItem item, object eisValue)> items,
        int fps,
        long currentSceneFrame)
    {
        for (int idx = 0; idx < items.Count; idx++)
        {
            var (item, eisValue) = items[idx];
            var esoList = _effectedSourceOutputsField?.GetValue(eisValue) as IList;
            if (esoList == null || esoList.Count == 0) continue;

            long itemFrame = Math.Max(0, currentSceneFrame - item.Frame);
            long itemLength = Math.Max(1, item.Length);
            var baseline = CaptureAnimBaseline(item, itemFrame, itemLength, fps);

            if (TryGetBuiltInModelCapture(item, eisValue, out var modelPath, out var embeddedMeshData, out _, out var builtInModel)
                && builtInModel != null)
            {
                var firstEso = esoList[0];
                var drawDesc = firstEso != null ? _esoDrawDescProp!.GetValue(firstEso) as DrawDescription : null;
                if (drawDesc == null) continue;

                MaterialOverrideParameters itemMaterial = TryCollectMaterial(item, eisValue, itemFrame, itemLength, fps);
                string fallbackName = string.IsNullOrWhiteSpace(modelPath)
                    ? "3Dモデル"
                    : Path.GetFileName(modelPath);

                snapshot.Items.Add(new ViewportItemEntry
                {
                    Item = item,
                    Kind = ViewportItemKind.BuiltInModel,
                    DisplayName = GetItemDisplayName(item, fallbackName),
                    DrawDescription = drawDesc,
                    Layer = item.Layer,
                    ItemFrame = itemFrame,
                    ItemLength = itemLength,
                    Fps = fps,
                    Opacity = (float)drawDesc.Opacity,
                    ModelSpec = ViewportModelSpec.FromObjModelDataWithMesh(modelPath, builtInModel, embeddedMeshData),
                    Material = itemMaterial,
                    CapturedAnimPos = baseline.Pos,
                    CapturedAnimZoom = baseline.Zoom,
                    CapturedAnimRotZ = baseline.RotZ,
                    CapturedRotateEffectDeg = baseline.EffRot,
                    HasCapturedRotateEffect = baseline.HasEff,
                    HasCapturedAnimBaseline = true,
                });
                continue;
            }

            ID3DVideoEffect? d3dVideoEffect = FindEnabledD3DVideoEffect(item);

            for (int eIdx = 0; eIdx < esoList.Count; eIdx++)
            {
                object? eso = esoList[eIdx];
                if (eso == null) continue;

                var preRenderOutput = _esoPreRenderOutputProp!.GetValue(eso) as ID2D1Image;
                var drawDesc = _esoDrawDescProp!.GetValue(eso) as DrawDescription;
                if (preRenderOutput == null || drawDesc == null) continue;
                if ((double)drawDesc.Zoom.X == 0.0 || (double)drawDesc.Zoom.Y == 0.0 || drawDesc.Opacity == 0.0)
                    continue;

                if (!Viewport3DBridge.TryBakeImage(
                        preRenderOutput, devices,
                        out var sharedTex, out int texW, out int texH, out float cx, out float cy)
                    || sharedTex == null)
                    continue;

                snapshot.Items.Add(new ViewportItemEntry
                {
                    Item = item,
                    Kind = d3dVideoEffect != null ? ViewportItemKind.D3DEffect : ViewportItemKind.Quad2D,
                    DisplayName = GetItemDisplayName(item, item.GetType().Name),
                    DrawDescription = drawDesc,
                    Layer = item.Layer,
                    ItemFrame = itemFrame,
                    ItemLength = itemLength,
                    Fps = fps,
                    Opacity = (float)drawDesc.Opacity,
                    PixelWidth = texW,
                    PixelHeight = texH,
                    BoundsCenterX = cx,
                    BoundsCenterY = cy,
                    D3DEffectId = d3dVideoEffect?.D3DEffectId,
                    D3DVideoEffect = d3dVideoEffect,
                    SharedTexture = sharedTex,
                    PartIndex = eIdx,
                    CapturedAnimPos = baseline.Pos,
                    CapturedAnimZoom = baseline.Zoom,
                    CapturedAnimRotZ = baseline.RotZ,
                    CapturedRotateEffectDeg = baseline.EffRot,
                    HasCapturedRotateEffect = baseline.HasEff,
                    HasCapturedAnimBaseline = true,
                });
            }
        }
    }

    private static Matrix4x4 ResolveSceneCameraMatrix(
        List<(IVideoItem item, object eisValue)> d3dItems,
        List<(IVideoItem item, object eisValue)> d2dItems,
        List<(IVideoItem item, LightData light, DrawDescription? drawDesc)> frameLightEntries,
        in EnvironmentParameters envParams)
    {
        Matrix4x4? firstFound = null;

        if (TryFindCameraMatrix(d3dItems, ref firstFound, out var nonIdentity))
            return nonIdentity;
        if (TryFindCameraMatrix(d2dItems, ref firstFound, out nonIdentity))
            return nonIdentity;

        for (int i = 0; i < frameLightEntries.Count; i++)
        {
            var cam = frameLightEntries[i].drawDesc?.Camera;
            if (cam.HasValue)
            {
                if (cam.Value != Matrix4x4.Identity) return cam.Value;
                firstFound ??= cam.Value;
            }
        }

        if (envParams.FallbackCameraMatrix.M44 != 0f)
        {
            if (envParams.FallbackCameraMatrix != Matrix4x4.Identity) return envParams.FallbackCameraMatrix;
            firstFound ??= envParams.FallbackCameraMatrix;
        }

        return firstFound ?? Matrix4x4.Identity;
    }

    private static bool TryFindCameraMatrix(
        List<(IVideoItem item, object eisValue)> items,
        ref Matrix4x4? firstFound,
        out Matrix4x4 nonIdentity)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var esoList = _effectedSourceOutputsField?.GetValue(items[i].eisValue) as IList;
            if (esoList == null || esoList.Count == 0 || esoList[0] == null) continue;
            if (_esoDrawDescProp?.GetValue(esoList[0]!) is DrawDescription dd)
            {
                if (dd.Camera != Matrix4x4.Identity)
                {
                    nonIdentity = dd.Camera;
                    return true;
                }
                firstFound ??= dd.Camera;
            }
        }
        nonIdentity = Matrix4x4.Identity;
        return false;
    }

    private static (Vector3 Pos, float Zoom, float RotZ, Vector3 EffRot, bool HasEff) CaptureAnimBaseline(
        IVideoItem item,
        long itemFrame,
        long itemLength,
        int fps)
    {
        var pos = new Vector3(
            (float)item.X.GetValue(itemFrame, itemLength, fps),
            (float)item.Y.GetValue(itemFrame, itemLength, fps),
            (float)item.Z.GetValue(itemFrame, itemLength, fps));
        float zoom = (float)item.Zoom.GetValue(itemFrame, itemLength, fps);
        float rotZ = (float)item.Rotation.GetValue(itemFrame, itemLength, fps);

        Vector3 effRot = Vector3.Zero;
        bool hasEff = false;
        if (item.VideoEffects != null)
        {
            for (int i = item.VideoEffects.Count - 1; i >= 0; i--)
            {
                if (item.VideoEffects[i] is RotateEffect re && re.IsEnabled)
                {
                    effRot = new Vector3(
                        (float)re.X.GetValue(itemFrame, itemLength, fps),
                        (float)re.Y.GetValue(itemFrame, itemLength, fps),
                        (float)re.Z.GetValue(itemFrame, itemLength, fps));
                    hasEff = true;
                    break;
                }
            }
        }
        return (pos, zoom, rotZ, effRot, hasEff);
    }

    private static string GetItemDisplayName(IVideoItem item, string fallback)
        => !string.IsNullOrWhiteSpace(item.Label) ? item.Label : fallback;

    private static Type? FindType(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] DepthSortRendererPatch: {msg}");
}
