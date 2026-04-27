using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Plugin;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// D3D11 半透明描画モード
/// </summary>
public enum TransparencyMode
{
    [Display(Name = "OIT（順序非依存・FXAA付き）")]
    OIT,

    [Display(Name = "標準（depth判定・AA有効）")]
    Standard,
}

/// <summary>
/// 標準モードの深度レイヤー数。
/// </summary>
public enum StandardDepthLayerCount
{
    [Display(Name = "2層（軽量）")]
    Two = 2,

    [Display(Name = "4層（推奨）")]
    Four = 4,

    [Display(Name = "8層（高品質）")]
    Eight = 8,
}

/// <summary>
/// YMM4 設定画面「その他」カテゴリに表示される D3D11 描画設定。
/// </summary>
internal class D3D11RendererSettings : SettingsBase<D3D11RendererSettings>
{
    public override SettingsCategory Category => SettingsCategory.Other;
    public override string Name => "D3D11描画設定";
    public override bool HasSettingView => true;
    public override object? SettingView => new D3D11SettingsView();

    private TransparencyMode transparencyMode = TransparencyMode.OIT;
    private StandardDepthLayerCount standardDepthLayerCount = StandardDepthLayerCount.Four;

    /// <summary>半透明描画モード</summary>
    public TransparencyMode TransparencyMode
    {
        get => transparencyMode;
        set => Set(ref transparencyMode, value);
    }

    /// <summary>標準モードの深度レイヤー数</summary>
    public StandardDepthLayerCount StandardDepthLayerCount
    {
        get => standardDepthLayerCount;
        set => Set(ref standardDepthLayerCount, value);
    }

    public override void Initialize() { }
}
