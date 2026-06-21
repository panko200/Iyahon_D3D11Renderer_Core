using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Plugin;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

public enum TransparencyMode
{
    [Display(Name = "OIT（順序非依存・FXAA付き）")]
    OIT,

    [Display(Name = "標準（depth判定・AA有効）")]
    Standard,
}

public enum StandardDepthLayerCount
{
    [Display(Name = "2層（軽量）")]
    Two = 2,

    [Display(Name = "4層（推奨）")]
    Four = 4,

    [Display(Name = "8層（高品質）")]
    Eight = 8,
}

public enum ShadowResolution
{
    [Display(Name = "低（512px：超軽量）")]
    Low = 512,

    [Display(Name = "中（1024px：標準）")]
    Medium = 1024,

    [Display(Name = "高（2048px：綺麗・推奨）")]
    High = 2048,

    [Display(Name = "超高（4096px：高精細・重い）")]
    Ultra = 4096,
}

internal class D3D11RendererSettings : SettingsBase<D3D11RendererSettings>
{
    public override SettingsCategory Category => SettingsCategory.Other;
    public override string Name => "D3D11描画設定";
    public override bool HasSettingView => true;
    public override object? SettingView => new D3D11SettingsView();

    private TransparencyMode transparencyMode = TransparencyMode.OIT;
    private StandardDepthLayerCount standardDepthLayerCount = StandardDepthLayerCount.Four;
    private ShadowResolution shadowResolution = ShadowResolution.Medium;
    private bool enableShadow = false;
    private bool enableSoftShadow = false; // ★追加：影のぼかし（トグル）
    private double ambientIntensity = 0.3;

    public TransparencyMode TransparencyMode
    {
        get => transparencyMode;
        set => Set(ref transparencyMode, value);
    }

    public StandardDepthLayerCount StandardDepthLayerCount
    {
        get => standardDepthLayerCount;
        set => Set(ref standardDepthLayerCount, value);
    }

    public ShadowResolution ShadowResolution
    {
        get => shadowResolution;
        set => Set(ref shadowResolution, value);
    }

    public bool EnableShadow
    {
        get => enableShadow;
        set => Set(ref enableShadow, value);
    }

    /// <summary>影のぼかし（Soft Shadow）を有効にするか</summary>
    public bool EnableSoftShadow
    {
        get => enableSoftShadow;
        set => Set(ref enableSoftShadow, value);
    }

    public double AmbientIntensity
    {
        get => ambientIntensity;
        set => Set(ref ambientIntensity, value);
    }

    public override void Initialize() { }
}