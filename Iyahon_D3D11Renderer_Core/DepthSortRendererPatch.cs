using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectWrite; // ★追加：光源ラベル描画用
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using Iyahon_D3D11Renderer_Core.D3DEffect;
using Iyahon_D3D11Renderer_Core.Lighting;
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

internal sealed class CachedRenderer
{
    public DepthSortRenderer Renderer { get; }
    public DateTime LastSeen { get; set; }

    public CachedRenderer(DepthSortRenderer renderer)
    {
        Renderer = renderer;
        LastSeen = DateTime.UtcNow;
    }
}

internal static class DepthSortRendererPatch
{
    private static readonly object _globalRenderLock = new();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IVideoItem, StrongBox<bool>>
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

    // ── 光源検出用フィールド ──
    private static Type? _shapeItemType;
    private static PropertyInfo? _shapeType2Prop;
    private static PropertyInfo? _eisSourceProp;
    private static Type? _shapeSourceType;
    private static FieldInfo? _shapeSourceInternalSourceField;
    private static readonly Type _lightShapePluginType = typeof(D3DLightShapePlugin);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, CachedRenderer>
        _renderers = new();

    private static TContext CreateIsolatedContext<TContext>(ID2D1Device d2dDevice, TContext templateDc) where TContext : ID2D1DeviceContext
    {
        return d2dDevice.CreateDeviceContext(DeviceContextOptions.None).QueryInterface<TContext>();
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

        _effectedSourceOutputsField = effectedItemSourceType.GetField("effectedSourceOutputs",
            BindingFlags.Instance | BindingFlags.NonPublic);

        _eisOutputProp = effectedItemSourceType.GetProperty("Output",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        _eisDevicesField = effectedItemSourceType.GetField("devices",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _eisItemField = effectedItemSourceType.GetField("item",
            BindingFlags.Instance | BindingFlags.NonPublic);

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

    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
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
                try { cached.Renderer.Dispose(); } catch { }
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
                    Log("注入した commandList を即座に破棄しました。");
                }
            }

            if (_devicesField == null) return;
            var devices = _devicesField.GetValue(__instance);
            if (devices == null) return;

            lock (_globalRenderLock)
            {
                if (_renderers.TryGetValue(devices, out var cached))
                {
                    Log("TimelineSource.Dispose() を検知。紐づく DepthSortRenderer を即座に破棄します。");
                    cached.Renderer.Dispose();
                    _renderers.Remove(devices);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"DisposePrefix 例外: {ex.Message}");
        }
    }

    [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
    [System.Security.SecurityCritical]
    private static void UpdatePostfix(object __instance)
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
                int width = GetSceneWidth(scene) ?? 1920;
                int height = GetSceneHeight(scene) ?? 1080;

                var d3dItems = new List<(IVideoItem item, object eisValue)>();
                var d2dItems = new List<(IVideoItem item, object eisValue)>();

                // ── 光源収集 ──
                LightManager.BeginFrame();
                var lightItemKeys = new HashSet<IVideoItem>();

                foreach (object pair in (IEnumerable)resources)
                {
                    var key = _kvpKeyProp!.GetValue(pair) as IVideoItem;
                    var value = _kvpValueProp!.GetValue(pair);
                    if (key == null || value == null) continue;

                    if (TryCollectLight(key, value))
                    {
                        lightItemKeys.Add(key);
                        continue; // 光源は通常の描画対象から除外（オーバーレイ視覚化は別途行う）
                    }

                    if (IsEnabledForItem(key))
                        d3dItems.Add((key, value));
                    else
                        d2dItems.Add((key, value));
                }

                LightManager.EndFrame();

                if (d3dItems.Count == 0) return;

                var dc = devices.DeviceContext;

                var renderItems = new List<RenderItem>();
                var srvList = new List<ID3D11ShaderResourceView>();

                foreach (var (item, eisValue) in d3dItems)
                {
                    var esoList = _effectedSourceOutputsField!.GetValue(eisValue) as IList;
                    if (esoList == null || esoList.Count == 0) continue;

                    if (ObjLoaderBridge.IsAvailable)
                    {
                        bool isObjItem = ObjLoaderBridge.IsObjLoaderItem(item);
                        if (isObjItem)
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
                                    });
                                    continue;
                                }
                            }
                        }
                    }

                    ID3DVideoEffect? d3dVideoEffect = null;
                    long itemFrame = 0;
                    long itemLength = 1;
                    int fps = 60;

                    try
                    {
                        var videoEffects = item.VideoEffects;
                        if (videoEffects != null)
                        {
                            foreach (var ve in videoEffects)
                            {
                                if (ve is ID3DVideoEffect candidate && ve.IsEnabled)
                                {
                                    d3dVideoEffect = candidate;
                                    try
                                    {
                                        fps = (int)(GetFps(scene) ?? 60.0);
                                        itemFrame = GetItemFrame(item, scene) ?? 0L;
                                        itemLength = GetItemLength(item) ?? 1L;
                                    }
                                    catch { }
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"D3Dエフェクト検出エラー: {ex.Message}");
                    }

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

                        var d3dTex = D2DD3DBridge.BakeToD3DTexture(preRenderOutput, devices, texW, texH, -left, -top);
                        if (d3dTex == null) continue;

                        var srv = D2DD3DBridge.CreateSrv(d3dTex, devices.D3D.Device);
                        d3dTex.Dispose();
                        if (srv == null) continue;

                        srvList.Add(srv);

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
                        });
                    }
                }

                if (renderItems.Count == 0)
                {
                    foreach (var s in srvList) s.Dispose();
                    return;
                }

                if (!_renderers.TryGetValue(devices, out var cached))
                {
                    var renderer = new DepthSortRenderer();
                    cached = new CachedRenderer(renderer);
                    _renderers.Add(devices, cached);
                }
                cached.LastSeen = DateTime.UtcNow;

                PruneRenderers();

                bool needsReset = false;
                if (cached.Renderer.DevicePointer != IntPtr.Zero && cached.Renderer.DevicePointer != devices.D3D.Device.NativePointer)
                {
                    needsReset = true;
                }
                if (cached.Renderer.Width > 0 && (cached.Renderer.Width != width || cached.Renderer.Height != height))
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

                if (!cached.Renderer.Initialize(devices, width, height))
                {
                    foreach (var s in srvList) s.Dispose();
                    return;
                }

                var resultTexture = cached.Renderer.Render(devices, renderItems, width, height);
                foreach (var s in srvList) s.Dispose();

                if (resultTexture == null) return;

                var resultBitmap = cached.Renderer.RenderTargetBitmap;
                if (resultBitmap == null || resultBitmap.NativePointer == IntPtr.Zero) return;

                var oldCommandList2 = _commandListField!.GetValue(__instance) as ID2D1CommandList;

                using var d2dDevice = dc.Device;
                using var isolatedDc = CreateIsolatedContext(d2dDevice, dc);
                isolatedDc.Dpi = dc.Dpi;
                isolatedDc.AntialiasMode = dc.AntialiasMode;
                isolatedDc.TextAntialiasMode = dc.TextAntialiasMode;

                var newCommandList = isolatedDc.CreateCommandList();
                isolatedDc.Target = newCommandList;
                isolatedDc.BeginDraw();
                isolatedDc.Clear(new Color4(0f, 0f, 0f, 0f));

                foreach (var (item, eisValue) in d2dItems)
                {
                    var output = _eisOutputProp!.GetValue(eisValue) as ID2D1Image;
                    if (output == null) continue;

                    var blend = item.Blend;
                    if (blend.IsCompositionEffect())
                        isolatedDc.DrawImage(output, Vortice.Direct2D1.InterpolationMode.MultiSampleLinear, blend.ToD2DCompositionMode());
                    else
                        isolatedDc.BlendImage(output, blend.ToD2DBlendMode(), null, null, Vortice.Direct2D1.InterpolationMode.MultiSampleLinear);
                }

                isolatedDc.DrawImage(resultBitmap, new Vector2(-width / 2f, -height / 2f));

                // ── 光源の視覚化ガイドライン（見やすく描画） ──
                var activeLights = LightManager.GetActiveLights();
                if (activeLights.Count > 0 && renderItems.Count > 0)
                {
                    var sceneCameraMatrix = renderItems[0].DrawDescription.Camera;
                    DrawLightGizmos(isolatedDc, activeLights, sceneCameraMatrix);
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

    /// <summary>
    /// 各光源のパラメータ・状態をもとにプレビュー上に3D対応の視覚的なガイドライン・情報をオーバーレイ描画します。
    /// </summary>
    private static void DrawLightGizmos(ID2D1DeviceContext dc, List<LightData> lights, Matrix4x4 cameraMatrix)
    {
        // 3Dからプレビュー（D2D）座標系への変換用プロジェクション
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
            // カメラの背後にある点は投影計算をスキップして反転バグを回避
            if (clip.W <= 0.0001f) return null;
            return new Vector2(clip.X / clip.W, clip.Y / clip.W);
        }

        using var whiteBrush = dc.CreateSolidColorBrush(new Color4(1f, 1.0f, 1.0f, 1.0f));
        using var blackBrush = dc.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0.8f));

        // DirectWriteの初期化（光源のラベル表示用）
        IDWriteFactory? dwriteFactory = null;
        IDWriteTextFormat? textFormat = null;
        try
        {
            dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
            textFormat = dwriteFactory.CreateTextFormat(
                "Yu Gothic UI", null,
                FontWeight.Bold,
                Vortice.DirectWrite.FontStyle.Normal,
                FontStretch.Normal,
                11f, "ja-jp"
            );
        }
        catch
        {
            // DirectWrite関連が失敗した場合はフォールバック
        }

        for (int i = 0; i < lights.Count; i++)
        {
            var light = lights[i];
            if (!light.ShowGizmo) continue;

            Vector3 pos = light.Position;
            var screenPosOpt = Project(pos);
            if (screenPosOpt == null) continue;
            Vector2 screenPos = screenPosOpt.Value;

            // 光源に設定されている色でガイドラインを描画
            var col = new Color4(light.Color.X, light.Color.Y, light.Color.Z, 1f);
            using var lightBrush = dc.CreateSolidColorBrush(col);

            // ── 光源タイプごとの3D投影幾何学ガイドの描画 ──
            if (light.Type == LightType.Directional)
            {
                // ディレクショナルライト：光の方向ベクトルラインの描画
                Vector3 endPosWorld = pos + light.Direction * 120f;
                var endScreenOpt = Project(endPosWorld);
                if (endScreenOpt != null)
                {
                    Vector2 endScreen = endScreenOpt.Value;
                    dc.DrawLine(screenPos, endScreen, lightBrush, 2f);

                    // 先端を小さな円でマーク
                    dc.FillEllipse(new Ellipse(endScreen, 4f, 4f), lightBrush);
                }
            }
            else if (light.Type == LightType.Point)
            {
                // ポイントライト：X/Y/Z各軸方向への影響範囲(Range)を示す星状のガイド線
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
                // スポットライト：主軸の方向線と外角(SpotOuterAngle)に基づいた円錐の骨組み線
                Vector3 forward = light.Direction;
                Vector3 endPosWorld = pos + forward * light.Range;
                var endScreenOpt = Project(endPosWorld);
                if (endScreenOpt != null)
                {
                    dc.DrawLine(screenPos, endScreenOpt.Value, lightBrush, 2f);
                }

                // 照射方向から垂直な基底ベクトルを計算
                Vector3 up = Vector3.UnitY;
                if (MathF.Abs(Vector3.Dot(forward, up)) > 0.9f) up = Vector3.UnitZ;
                Vector3 right = Vector3.Normalize(Vector3.Cross(forward, up));
                up = Vector3.Normalize(Vector3.Cross(right, forward));

                float radOuter = light.SpotOuterAngle * (MathF.PI / 180f);
                float cosO = MathF.Cos(radOuter);
                float sinO = MathF.Sin(radOuter);

                Vector3 coneDir1 = forward * cosO + right * sinO;
                Vector3 coneDir2 = forward * cosO - right * sinO;
                Vector3 coneDir3 = forward * cosO + up * sinO;
                Vector3 coneDir4 = forward * cosO - up * sinO;

                Vector3[] coneEnds = {
                    pos + coneDir1 * light.Range,
                    pos + coneDir2 * light.Range,
                    pos + coneDir3 * light.Range,
                    pos + coneDir4 * light.Range
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
                // エリアライト：発光面を構築する矩形および照射方向線
                Vector3 forward = light.Direction;
                Vector3 up = Vector3.UnitY;
                if (MathF.Abs(Vector3.Dot(forward, up)) > 0.9f) up = Vector3.UnitZ;
                Vector3 right = Vector3.Normalize(Vector3.Cross(forward, up));
                up = Vector3.Normalize(Vector3.Cross(right, forward));

                float halfW = light.AreaWidth * 0.5f;
                float halfH = light.AreaHeight * 0.5f;

                Vector3 c1 = pos + right * halfW + up * halfH;
                Vector3 c2 = pos - right * halfW + up * halfH;
                Vector3 c3 = pos - right * halfW - up * halfH;
                Vector3 c4 = pos + right * halfW - up * halfH;

                var p1 = Project(c1);
                var p2 = Project(c2);
                var p3 = Project(c3);
                var p4 = Project(c4);

                if (p1 != null && p2 != null && p3 != null && p4 != null)
                {
                    dc.DrawLine(p1.Value, p2.Value, lightBrush, 1.5f);
                    dc.DrawLine(p2.Value, p3.Value, lightBrush, 1.5f);
                    dc.DrawLine(p3.Value, p4.Value, lightBrush, 1.5f);
                    dc.DrawLine(p4.Value, p1.Value, lightBrush, 1.5f);
                }

                var endPosWorld = pos + forward * light.Range;
                var endScreenOpt = Project(endPosWorld);
                if (endScreenOpt != null)
                {
                    dc.DrawLine(screenPos, endScreenOpt.Value, lightBrush, 1f);
                }
            }

            // ── 中心電球ノードと光ハローの描画 ──
            dc.FillEllipse(new Ellipse(screenPos, 6f, 6f), lightBrush);
            dc.DrawEllipse(new Ellipse(screenPos, 6f, 6f), whiteBrush, 1.5f);
            dc.DrawEllipse(new Ellipse(screenPos, 12f, 12f), lightBrush, 0.5f);

            // ── 光源の名前・インデックスラベル描画 ──
            if (textFormat != null)
            {
                string label = $"Light {i}: {light.Type}";
                var textRectShadow = new RawRectF(screenPos.X + 11f, screenPos.Y - 14f, screenPos.X + 250f, screenPos.Y + 20f);
                var textRect = new RawRectF(screenPos.X + 10f, screenPos.Y - 15f, screenPos.X + 250f, screenPos.Y + 20f);

                dc.DrawText(label, textFormat, textRectShadow, blackBrush);
                dc.DrawText(label, textFormat, textRect, whiteBrush);
            }
        }

        textFormat?.Dispose();
        dwriteFactory?.Dispose();
    }

    private static int? GetSceneWidth(object? scene)
    {
        if (scene == null) return null;
        try
        {
            var tl = scene.GetType().GetProperty("Timeline", BindingFlags.Instance | BindingFlags.Public)?.GetValue(scene);
            var vi = tl?.GetType().GetProperty("VideoInfo", BindingFlags.Instance | BindingFlags.Public)?.GetValue(tl);
            return (int?)vi?.GetType().GetProperty("Width", BindingFlags.Instance | BindingFlags.Public)?.GetValue(vi);
        }
        catch { return null; }
    }

    private static int? GetSceneHeight(object? scene)
    {
        if (scene == null) return null;
        try
        {
            var tl = scene.GetType().GetProperty("Timeline", BindingFlags.Instance | BindingFlags.Public)?.GetValue(scene);
            var vi = tl?.GetType().GetProperty("VideoInfo", BindingFlags.Instance | BindingFlags.Public)?.GetValue(tl);
            return (int?)vi?.GetType().GetProperty("Height", BindingFlags.Instance | BindingFlags.Public)?.GetValue(vi);
        }
        catch { return null; }
    }

    private static double? GetFps(object? scene)
    {
        if (scene == null) return null;
        try
        {
            var tl = scene.GetType().GetProperty("Timeline", BindingFlags.Instance | BindingFlags.Public)?.GetValue(scene);
            var vi = tl?.GetType().GetProperty("VideoInfo", BindingFlags.Instance | BindingFlags.Public)?.GetValue(tl);
            return (double?)vi?.GetType().GetProperty("FPS", BindingFlags.Instance | BindingFlags.Public)?.GetValue(vi);
        }
        catch { return null; }
    }

    private static long? GetItemFrame(IVideoItem item, object? scene)
    {
        try
        {
            var currentFrameProp = scene?.GetType().GetProperty("CurrentFrame",
                BindingFlags.Instance | BindingFlags.Public);
            var currentFrame = currentFrameProp?.GetValue(scene);
            if (currentFrame == null) return 0;

            long sceneFrame = Convert.ToInt64(currentFrame);
            long itemStart = item.Frame;
            return Math.Max(0, sceneFrame - itemStart);
        }
        catch { return null; }
    }

    private static long? GetItemLength(IVideoItem item)
    {
        try
        {
            return item.Length;
        }
        catch { return null; }
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

            var sourceProp = __instance.GetType().GetProperty("Source",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var source = sourceProp?.GetValue(__instance);

            if (source == null) return;

            var srcDevicesField = source.GetType().GetField("devices",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (srcDevicesField == null)
            {
                srcDevicesField = source.GetType().BaseType?.GetField("devices",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (srcDevicesField == null) return;
            }

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

    private static bool TryCollectLight(IVideoItem item, object eisValue)
    {
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

            var lightData = lightSource.LastRegisteredLight;
            if (lightData == null) return false;

            var esoList = _effectedSourceOutputsField?.GetValue(eisValue) as System.Collections.IList;
            if (esoList != null && esoList.Count > 0)
            {
                var firstEso = esoList[0];
                if (firstEso != null)
                {
                    var drawDesc = _esoDrawDescProp?.GetValue(firstEso) as DrawDescription;
                    if (drawDesc != null)
                    {
                        lightData.Position = drawDesc.Draw;
                    }
                }
            }

            LightManager.AddLight(lightData);
            return true;
        }
        catch (Exception ex)
        {
            Log($"TryCollectLight 例外: {ex.Message}");
            return false;
        }
    }

    private static Type? FindType(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] DepthSortRendererPatch: {msg}");
}