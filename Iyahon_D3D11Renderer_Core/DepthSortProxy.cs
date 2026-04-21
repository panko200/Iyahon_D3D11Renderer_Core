using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// Per-Item D3D11 デプスソートのトグル用プロキシ。
/// PropertiesEditorPatch が選択中の IVideoItem を渡して生成する。
/// </summary>
internal sealed class DepthSortProxy : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly IVideoItem[] _items;

    public DepthSortProxy(IVideoItem[] items)
    {
        _items = items;
    }

    [Display(
        GroupName = "描画",
        Name = "D3D11描画",
        Description = "ONにすると D3D11 のデプスバッファを使って前後関係を正確に描画します。\nアイテムごとに個別に設定できます。",
        Order = 502)]
    [ToggleSlider]
    public bool UseD3D11DepthSort
    {
        get
        {
            // 全アイテムが有効なら true、それ以外は false
            foreach (var item in _items)
            {
                if (!DepthSortRendererPatch.IsEnabledForItem(item))
                    return false;
            }
            return _items.Length > 0;
        }
        set
        {
            bool changed = false;
            foreach (var item in _items)
            {
                if (DepthSortRendererPatch.IsEnabledForItem(item) != value)
                {
                    DepthSortRendererPatch.SetEnabledForItem(item, value);
                    changed = true;
                }
            }
            if (changed)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseD3D11DepthSort)));
                Application.Current.Dispatcher.BeginInvoke(
                    DepthSortVideoPlayerPatch.SetTimelineChanged);
            }
        }
    }
}
