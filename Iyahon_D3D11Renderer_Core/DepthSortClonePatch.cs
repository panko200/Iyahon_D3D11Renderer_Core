using HarmonyLib;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// BaseItem.GetClone() を Postfix パッチして、
/// クローン先の VisualItem にも D3D11 デプスソートフラグをコピーする。
///
/// GetClone() は JSON シリアライズ経由でクローンを作るため、
/// DepthSortJsonPatch が適用済みであれば自動的にフラグが引き継がれる。
/// このパッチは JSON パッチが何らかの理由で効かない場合のフォールバック。
///
/// また BaseItem.Split() は内部で GetClone() を2回呼ぶため、
/// 分割時のフラグ引き継ぎもこれで自動的にカバーされる。
/// </summary>
[HarmonyPatch(typeof(BaseItem), nameof(BaseItem.GetClone))]
internal static class DepthSortClonePatch
{
    private static void Postfix(BaseItem __instance, ref IItem __result)
    {
        // VisualItem でなければスキップ
        if (__instance is not VisualItem src) return;
        if (__result is not VisualItem dst) return;

        // JSON パッチが正常に動いていれば既にコピー済みのはずだが、
        // 念のためここでも明示的にコピーする
        DepthSortRendererPatch.SetEnabledForItem(
            (IVideoItem)dst,
            DepthSortRendererPatch.IsEnabledForItem((IVideoItem)src));
    }
}
