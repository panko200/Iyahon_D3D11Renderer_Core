using Newtonsoft.Json.Serialization;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// Newtonsoft.Json が VisualItem を JSON に書き出す・読み込む際に使う
/// カスタム IValueProvider。
///
/// VisualItem の実際のフィールドではなく、DepthSortRendererPatch の
/// ConditionalWeakTable に対して読み書きする。
/// </summary>
internal sealed class DepthSortValueProvider : IValueProvider
{
    /// <summary>
    /// JSON 読み込み時: パースされた bool 値を ConditionalWeakTable に書き込む
    /// </summary>
    public void SetValue(object target, object? value)
    {
        if (target is IVideoItem item && value is bool b)
            DepthSortRendererPatch.SetEnabledForItem(item, b);
    }

    /// <summary>
    /// JSON 書き出し時: ConditionalWeakTable から bool 値を読み取る
    /// </summary>
    public object? GetValue(object target)
        => target is IVideoItem item ? DepthSortRendererPatch.IsEnabledForItem(item) : (object?)false;
}
