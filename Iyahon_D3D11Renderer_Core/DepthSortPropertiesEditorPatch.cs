using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// PropertiesEditor.RefreshControls() の Postfix で
/// D3D11 デプスソートトグルを注入する。
/// 
/// RefreshControls は Groups を毎回再構築するため、
/// Postfix で毎回注入する（「既存チェック」はしない）。
/// </summary>
internal static class DepthSortPropertiesEditorPatch
{
    private static readonly Type _editorType = typeof(PropertiesEditor);

    private static readonly FieldInfo? _currentTargetsField =
        _editorType.GetField("currentTargets",
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? _getEditablePropertiesMethod =
        _editorType.GetMethod("GetEditableProperties",
            BindingFlags.Static | BindingFlags.NonPublic);

    private static readonly MethodInfo? _attachMethod =
        typeof(PropertiesEditor.EditorCache).GetMethod("Attach",
            BindingFlags.Instance | BindingFlags.Public);

    private const string InjectedTag = "Iyahon_D3D11Renderer_Core_DepthSort_Injected";

    /// <summary>描画グループ名（ローカライズ対応）</summary>
    private static string _drawGroupName = "描画";

    internal static void Apply(Harmony harmony)
    {
        var target = AccessTools.Method(_editorType, "RefreshControls");
        if (target == null) { Log("RefreshControls が見つかりません。"); return; }

        if (_currentTargetsField == null || _getEditablePropertiesMethod == null || _attachMethod == null)
        {
            Log($"必要なメンバが見つかりません。 currentTargets={_currentTargetsField != null} GetEditableProperties={_getEditablePropertiesMethod != null} Attach={_attachMethod != null}");
            return;
        }

        // Texts.DrawGroupName を動的に解決
        try
        {
            var textsType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "YukkuriMovieMaker.Resources.Localization.Texts");
            if (textsType != null)
            {
                var prop = textsType.GetProperty("DrawGroupName",
                    BindingFlags.Static | BindingFlags.Public);
                var val = prop?.GetValue(null) as string;
                if (!string.IsNullOrEmpty(val))
                {
                    _drawGroupName = val!;
                    Log($"DrawGroupName = \"{_drawGroupName}\"");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"DrawGroupName 解決失敗: {ex.Message}");
        }

        harmony.Patch(target, postfix: new HarmonyMethod(
            typeof(DepthSortPropertiesEditorPatch), nameof(Postfix)));

        Log("パッチ適用完了。");
    }

    /// <summary>
    /// RefreshControls の Postfix。
    /// 毎回呼ばれ、毎回トグルを注入する（RefreshControls が毎回 Groups を再構築するため）。
    /// </summary>
    private static void Postfix(PropertiesEditor __instance)
    {
        try
        {
            var targets = _currentTargetsField!.GetValue(__instance) as object[];
            if (targets == null || targets.Length == 0) return;

            var videoItems = targets.OfType<VisualItem>().Cast<IVideoItem>().ToArray();
            if (videoItems.Length == 0) return;

            // ── ステール Tag を消去 ──
            // RefreshControls が以前の注入 EditorCache を別プロパティに再利用すると、
            // Control.Tag に InjectedTag が残る。これを消去しないと
            // 「既に注入済み」と誤判定される。
            foreach (var g in __instance.Groups)
            {
                foreach (var pair in g.Items)
                {
                    if (pair.EditorCache.Control.Tag as string == InjectedTag)
                        pair.EditorCache.Control.Tag = null;
                }
            }

            // Per-Item プロキシ
            var proxy = new DepthSortProxy(videoItems);

            var editableProperties = (IEnumerable<PropertiesEditor.EditableProperty>)
                _getEditablePropertiesMethod!.Invoke(null,
                    new object?[] { proxy, proxy, null, 0, null, null })!;

            var propList = editableProperties.ToList();
            if (propList.Count == 0) return;

            // 描画グループを探す
            var targetGroup = __instance.Groups.FirstOrDefault(g => g.Name == _drawGroupName)
                ?? __instance.Groups.FirstOrDefault(g =>
                    g.Name != null && g.Name.Contains("描画"));

            if (targetGroup == null)
            {
                targetGroup = new PropertiesEditor.EditorGroup { Name = _drawGroupName };
                __instance.Groups.Add(targetGroup);
            }

            foreach (var editableProp in propList)
            {
                var attr = editableProp.PropertyEditorAttribute;
                if (attr == null) continue;

                var cache = new PropertiesEditor.EditorCache(attr);
                cache.Control.Tag = InjectedTag;

                _attachMethod!.Invoke(cache,
                    new object[] { (IEnumerable<PropertiesEditor.EditableProperty>)
                        new[] { editableProp } });

                targetGroup.Items.Add(
                    new PropertiesEditor.EditablePropertyAndEditorCachePair(editableProp, cache));
            }
        }
        catch (Exception ex)
        {
            Log($"Postfix エラー: {ex}");
        }
    }

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[Iyahon_D3D11Renderer_Core] DepthSortPropertiesEditorPatch: {msg}");
}
