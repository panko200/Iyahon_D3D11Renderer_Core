using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// EffectedItemSource.Update() の AA モード保存用構造体。
/// Prefix で元の値を保存し、Postfix で復元する。
/// </summary>
internal struct AaState
{
    public bool ShouldRestore;
    public AntialiasMode OriginalAA;
    public TextAntialiasMode OriginalTextAA;
    // Source (TextSource/ShapeSource等) の DeviceContext の元の値
    public bool ShouldRestoreSource;
    public AntialiasMode OrigSourceAA;
    public TextAntialiasMode OrigSourceTextAA;
    public object? SourceDevices;  // IGraphicsDevicesAndContext
}


/// <summary>
/// TimelineSource.Update() を Postfix パッチして、
/// D3D11 有効アイテムをデプスバッファ付きで再レンダリングし、
/// commandList を差し替える。
/// プレビュー・エンコード・スクリーンショットすべてに効く。
/// </summary>
internal static class DepthSortRendererPatch
{
    // ─── Per-Item 有効フラグ ───
    // ConditionalWeakTable: IVideoItem がGCされたら自動で消える
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IVideoItem, StrongBox<bool>>
        _perItemEnabled = new();

    /// <summary>指定アイテムの D3D11 デプスソート有効/無効を取得</summary>
    public static bool IsEnabledForItem(IVideoItem item)
    {
        if (_perItemEnabled.TryGetValue(item, out var box))
            return box.Value;
        return false;
    }

    /// <summary>指定アイテムの D3D11 デプスソート有効/無効を設定</summary>
    public static void SetEnabledForItem(IVideoItem item, bool enabled)
    {
        if (_perItemEnabled.TryGetValue(item, out var box))
            box.Value = enabled;
        else
            _perItemEnabled.AddOrUpdate(item, new StrongBox<bool>(enabled));
    }

    // ─── TimelineSource のフィールド ───
    private static Type? _timelineSourceType;
    private static FieldInfo? _timelineResourcesField;
    private static FieldInfo? _sceneField;
    private static FieldInfo? _devicesField;          // IGraphicsDevicesAndContext
    private static FieldInfo? _commandListField;      // ID2D1CommandList
    private static FieldInfo? _disposerField;         // DisposeCollector

    // ─── EffectedItemSource のフィールド ───
    private static FieldInfo? _effectedSourceOutputsField;
    private static PropertyInfo? _kvpKeyProp;
    private static PropertyInfo? _kvpValueProp;

    // ─── EffectedSourceOutput のプロパティ ───
    private static Type? _effectedSourceOutputType;
    private static PropertyInfo? _esoPreRenderOutputProp;
    private static PropertyInfo? _esoDrawDescProp;

    // ─── EffectedItemSource のプロパティ/フィールド ───
    private static PropertyInfo? _eisOutputProp;
    private static FieldInfo? _eisDevicesField;   // IGraphicsDevicesAndContext
    private static FieldInfo? _eisItemField;      // IVideoItem

    // ─── レンダラーキャッシュ（TimelineSource インスタンスごと） ───
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, DepthSortRenderer>
        _renderers = new();

    internal static void Apply(Harmony harmony)
    {
        _timelineSourceType = FindType("YukkuriMovieMaker.Player.Video.TimelineSource");
        if (_timelineSourceType == null) { Log("TimelineSource が見つかりません。"); return; }

        var effectedItemSourceType = FindType("YukkuriMovieMaker.Player.Video.EffectedItemSource");
        if (effectedItemSourceType == null) { Log("EffectedItemSource が見つかりません。"); return; }

        _effectedSourceOutputType = FindType("YukkuriMovieMaker.Player.Video.EffectedSourceOutput");
        if (_effectedSourceOutputType == null) { Log("EffectedSourceOutput が見つかりません。"); return; }

        // ─── フィールド/プロパティのキャッシュ ───

        _timelineResourcesField = _timelineSourceType.GetField("timelineResources",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _sceneField = _timelineSourceType.GetField("scene",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _devicesField = _timelineSourceType.GetField("devices",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _commandListField = _timelineSourceType.GetField("commandList",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _disposerField = _timelineSourceType.GetField("disposer",
            BindingFlags.Instance | BindingFlags.NonPublic);

        // EffectedItemSource → effectedSourceOutputs (List<EffectedSourceOutput>)
        _effectedSourceOutputsField = effectedItemSourceType.GetField("effectedSourceOutputs",
            BindingFlags.Instance | BindingFlags.NonPublic);

        // EffectedItemSource.Output
        _eisOutputProp = effectedItemSourceType.GetProperty("Output",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // EffectedItemSource.devices / item (AA パッチ用)
        _eisDevicesField = effectedItemSourceType.GetField("devices",
            BindingFlags.Instance | BindingFlags.NonPublic);
        _eisItemField = effectedItemSourceType.GetField("item",
            BindingFlags.Instance | BindingFlags.NonPublic);

        // EffectedSourceOutput のプロパティ
        _esoPreRenderOutputProp = _effectedSourceOutputType.GetProperty("PreRenderOutput",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _esoDrawDescProp = _effectedSourceOutputType.GetProperty("DrawDescription",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // KeyValuePair<IVideoItem, EffectedItemSource>
        var kvpType = typeof(KeyValuePair<,>).MakeGenericType(typeof(IVideoItem), effectedItemSourceType);
        _kvpKeyProp = kvpType.GetProperty("Key");
        _kvpValueProp = kvpType.GetProperty("Value");

        Log($"フィールドキャッシュ:");
        Log($"  devices={_devicesField != null} timelineResources={_timelineResourcesField != null}");
        Log($"  commandList={_commandListField != null} disposer={_disposerField != null}");
        Log($"  effectedSourceOutputs={_effectedSourceOutputsField != null}");
        Log($"  EIS.Output={_eisOutputProp != null}");
        Log($"  PreRenderOutput={_esoPreRenderOutputProp != null} DrawDescription={_esoDrawDescProp != null}");

        if (_devicesField == null || _timelineResourcesField == null ||
            _commandListField == null || _effectedSourceOutputsField == null ||
            _esoPreRenderOutputProp == null || _esoDrawDescProp == null ||
            _eisOutputProp == null)
        {
            Log("必要なメンバのキャッシュ失敗。");
            return;
        }

        // TimelineSource.Update() をパッチ
        var updateMethod = _timelineSourceType.GetMethod("Update",
            BindingFlags.Instance | BindingFlags.Public);
        if (updateMethod == null) { Log("Update() が見つかりません。"); return; }

        harmony.Patch(updateMethod, postfix: new HarmonyMethod(
            typeof(DepthSortRendererPatch), nameof(UpdatePostfix)));

        Log("TimelineSource.Update() Postfix パッチ適用完了。");

        // EffectedItemSource.Update() をパッチ（AA 無効化用）
        var eisUpdateMethod = effectedItemSourceType.GetMethod("Update",
            BindingFlags.Instance | BindingFlags.Public);
        if (eisUpdateMethod != null && _eisDevicesField != null && _eisItemField != null)
        {
            harmony.Patch(eisUpdateMethod,
                prefix: new HarmonyMethod(typeof(DepthSortRendererPatch), nameof(EisUpdatePrefix)),
                postfix: new HarmonyMethod(typeof(DepthSortRendererPatch), nameof(EisUpdatePostfix)));
            Log("EffectedItemSource.Update() AA パッチ適用完了。");
        }
        else
        {
            Log("EffectedItemSource.Update() AA パッチ: 必要なメンバが見つかりません。スキップ。");
        }
    }

    /// <summary>
    /// TimelineSource.Update() の Postfix。
    /// D3D11 有効アイテムがあれば commandList を差し替える。
    /// </summary>
    private static void UpdatePostfix(object __instance)
    {
        try
        {
            var devices = _devicesField!.GetValue(__instance) as IGraphicsDevicesAndContext;
            if (devices == null) return;

            var resources = _timelineResourcesField!.GetValue(__instance);
            if (resources == null) return;

            var scene = _sceneField?.GetValue(__instance);
            int width = GetSceneWidth(scene) ?? 1920;
            int height = GetSceneHeight(scene) ?? 1080;

            // ─── D3D11有効アイテムを収集 ───
            var d3dItems = new List<(IVideoItem item, object eisValue)>();
            var d2dItems = new List<(IVideoItem item, object eisValue)>();

            foreach (object pair in (IEnumerable)resources)
            {
                var key = _kvpKeyProp!.GetValue(pair) as IVideoItem;
                var value = _kvpValueProp!.GetValue(pair);
                if (key == null || value == null) continue;

                if (IsEnabledForItem(key))
                    d3dItems.Add((key, value));
                else
                    d2dItems.Add((key, value));
            }

            // D3D11 対象がなければ何もしない
            if (d3dItems.Count == 0) return;

            var dc = devices.DeviceContext;

            // ─── D3D11 アイテムの PreRenderOutput + DrawDescription を収集 ───
            var renderItems = new List<RenderItem>();
            var srvList = new List<ID3D11ShaderResourceView>();

            foreach (var (item, eisValue) in d3dItems)
            {
                var esoList = _effectedSourceOutputsField!.GetValue(eisValue) as IList;
                if (esoList == null || esoList.Count == 0) continue;

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
                    });
                }
            }

            if (renderItems.Count == 0)
            {
                foreach (var s in srvList) s.Dispose();
                return;
            }

            // ─── D3D11 レンダリング ───
            if (!_renderers.TryGetValue(__instance, out var renderer))
            {
                renderer = new DepthSortRenderer(devices);
                _renderers.Add(__instance, renderer);

                // __instance (TimelineSource) の disposer に追加して一緒に破棄させる
                var rendererDisposer = _disposerField?.GetValue(__instance);
                if (rendererDisposer != null)
                {
                    try
                    {
                        var collectMethod = rendererDisposer.GetType().GetMethod("Collect", new[] { typeof(IDisposable) });
                        if (collectMethod == null)
                        {
                            // ジェネリックメソッドの場合のフォールバック
                            var methods = rendererDisposer.GetType().GetMethods();
                            foreach (var m in methods)
                            {
                                if (m.Name == "Collect" && m.GetParameters().Length == 1)
                                {
                                    collectMethod = m;
                                    break;
                                }
                            }
                            if (collectMethod != null && collectMethod.IsGenericMethodDefinition)
                            {
                                collectMethod = collectMethod.MakeGenericMethod(typeof(IDisposable));
                            }
                        }
                        
                        collectMethod?.Invoke(rendererDisposer, new object[] { renderer });
                    }
                    catch (Exception ex)
                    {
                        Log($"disposer.Collect() エラー (Renderer): {ex.Message}");
                    }
                }
            }

            if (!renderer.Initialize(width, height))
            {
                foreach (var s in srvList) s.Dispose();
                return;
            }

            var resultTexture = renderer.Render(renderItems, width, height);
            foreach (var s in srvList) s.Dispose();

            if (resultTexture == null) return;

            var resultBitmap = D2DD3DBridge.GetD2DBitmapFromD3DTexture(resultTexture, devices);
            if (resultBitmap == null) return;

            // ─── commandList を差し替え ───
            // 元の commandList は Update() 内で Close 済み。
            // 新しい commandList を作成し、
            // (1) D2Dアイテム（D3D11無効）をそのまま描画
            // (2) D3D11結果を上に合成
            // して差し替える。

            var oldCommandList = _commandListField!.GetValue(__instance) as ID2D1CommandList;

            var newCommandList = dc.CreateCommandList();
            dc.Target = newCommandList;
            dc.BeginDraw();
            dc.Clear(new Color4(0f, 0f, 0f, 0f));

            // (1) D2Dアイテム: 元のパイプラインの EffectedItemSource.Output を再描画
            // ただし、元の commandList は全アイテムの合成結果なので、
            // D3D11アイテムだけ除いて再描画するには各アイテムの Output を使う必要がある。
            foreach (var (item, eisValue) in d2dItems)
            {
                var output = _eisOutputProp!.GetValue(eisValue) as ID2D1Image;
                if (output == null) continue;

                var blend = item.Blend;
                if (blend.IsCompositionEffect())
                    dc.DrawImage(output, Vortice.Direct2D1.InterpolationMode.MultiSampleLinear, blend.ToD2DCompositionMode());
                else
                    dc.BlendImage(output, blend.ToD2DBlendMode(), null, null, Vortice.Direct2D1.InterpolationMode.MultiSampleLinear);
            }

            // (2) D3D11 結果を合成
            dc.DrawImage(resultBitmap, new Vector2(-width / 2f, -height / 2f));

            dc.EndDraw();
            dc.Target = null;
            newCommandList.Close();

            resultBitmap.Dispose();

            // commandList フィールドを差し替え
            _commandListField.SetValue(__instance, newCommandList);

            // disposer から古い commandList を外して dispose、新しいのを登録
            var disposer = _disposerField?.GetValue(__instance);
            if (disposer != null && oldCommandList != null)
            {
                try
                {
                    // DisposeCollector.Remove(IDisposable) → disposer から外す
                    var removeMethod = disposer.GetType().GetMethod("Remove",
                        new[] { typeof(IDisposable) });
                    removeMethod?.Invoke(disposer, new object[] { oldCommandList });
                    oldCommandList.Dispose();
                }
                catch { }

                try
                {
                    // DisposeCollector.Collect(IDisposable) → disposer に追加
                    var collectMethod = disposer.GetType().GetMethod("Collect",
                        new[] { typeof(IDisposable) });
                    collectMethod?.Invoke(disposer, new object[] { newCommandList });
                }
                catch { }
            }

            Log($"UpdatePostfix: D3D11={renderItems.Count}件, D2D={d2dItems.Count}件 → commandList 差し替え完了");
        }
        catch (Exception ex)
        {
            Log($"UpdatePostfix 例外: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
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

    // ═══════════════════════════════════════════════════════════════
    // EffectedItemSource.Update() AA 無効化パッチ
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// EffectedItemSource.Update() の Prefix。
    /// D3D11 有効アイテムなら EffectedItemSource の DC と
    /// Source (TextSource/ShapeSource等) の DC 両方の AA を Aliased に変更。
    /// </summary>
    private static void EisUpdatePrefix(object __instance, ref AaState __state)
    {
        __state = default;
        try
        {
            var item = _eisItemField!.GetValue(__instance) as IVideoItem;
            if (item == null || !IsEnabledForItem(item)) return;

            // ── EffectedItemSource 自身の DC ──
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

            // ── Source (TextSource/ShapeSource等) の DC ──
            var sourceProp = __instance.GetType().GetProperty("Source",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var source = sourceProp?.GetValue(__instance);

            if (source == null)
            {
                Log($"AA Prefix: Source が null (item={item.GetType().Name})");
                return;
            }

            var srcDevicesField = source.GetType().GetField("devices",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (srcDevicesField == null)
            {
                Log($"AA Prefix: Source.devices フィールドが見つかりません (Source={source.GetType().Name})");
                // NonPublic だけでなく、継承元も検索
                srcDevicesField = source.GetType().BaseType?.GetField("devices",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (srcDevicesField == null)
                {
                    Log($"AA Prefix: BaseType にも devices なし (BaseType={source.GetType().BaseType?.Name})");
                    return;
                }
                Log($"AA Prefix: BaseType で devices 発見 (BaseType={source.GetType().BaseType?.Name})");
            }

            var srcDevices = srcDevicesField.GetValue(source) as IGraphicsDevicesAndContext;
            if (srcDevices == null)
            {
                Log($"AA Prefix: Source.devices 値が null または非 IGraphicsDevicesAndContext (Source={source.GetType().Name}, FieldType={srcDevicesField.FieldType.Name})");
                return;
            }

            var srcDc = srcDevices.DeviceContext;
            __state.OrigSourceAA = srcDc.AntialiasMode;
            __state.OrigSourceTextAA = srcDc.TextAntialiasMode;
            __state.ShouldRestoreSource = true;
            __state.SourceDevices = srcDevices;
            srcDc.AntialiasMode = AntialiasMode.Aliased;
            srcDc.TextAntialiasMode = TextAntialiasMode.Aliased;

            Log($"AA Prefix OK: Source={source.GetType().Name} AA={__state.OrigSourceAA}→Aliased TextAA={__state.OrigSourceTextAA}→Aliased");
        }
        catch (Exception ex)
        {
            Log($"EisUpdatePrefix 例外: {ex.Message}");
        }
    }

    /// <summary>
    /// EffectedItemSource.Update() の Postfix。
    /// AA 設定を元に戻す。
    /// </summary>
    private static void EisUpdatePostfix(object __instance, ref AaState __state)
    {
        try
        {
            // EffectedItemSource の DC を復元
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

            // Source の DC を復元
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

    private static Type? FindType(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] DepthSortRendererPatch: {msg}");
}