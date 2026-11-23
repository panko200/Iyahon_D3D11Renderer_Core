using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

public enum D3DOutlineMode
{
    [Display(Name = "自動 (3D反転ハル / 2Dシルエット)")]
    Auto = 0,

    [Display(Name = "3D 反転ハル法 (Inverted Hull)")]
    InvertedHull3D = 1,

    [Display(Name = "スクリーンスペース (輪郭合成)")]
    ScreenSpace = 2,
}

internal struct OutlineParameters
{
    public bool Enabled;
    public D3DOutlineMode Mode;
    public float Thickness;
    public bool WorldSpaceThickness;
    public Vector4 Color; // RGBA sRGB (0..1)
    public float Opacity;
    public float Blur;
    public int Quality;
    public bool OutlineOnly;
    public float DepthBias;
    public bool SmoothNormals;
}

/// <summary>
/// 3DモデルやD3Dアイテムに高品質な縁取り（アウトライン）を追加する映像エフェクト。
/// 3Dモデルはスムーズ法線による反転ハル法、2D/エフェクト系や指定時はスクリーンスペース輪郭を描画します。
/// </summary>
[VideoEffect(
    "D3D 縁取り",
    ["D3D"],
    ["縁取り", "アウトライン", "outline", "輪郭", "D3D"],
    IsAviUtlSupported = false)]
internal sealed class D3DOutlineVideoEffect : VideoEffectBase
{
    public override string Label => "D3D 縁取り";

    [Display(GroupName = "縁取り", Name = "方式", Description = "輪郭の描画方式です。「自動」では3Dモデルに反転ハル法、2D/エフェクトにスクリーンスペース方式を使用します。")]
    [EnumComboBox]
    public D3DOutlineMode Mode
    {
        get => mode;
        set => Set(ref mode, value);
    }
    private D3DOutlineMode mode = D3DOutlineMode.Auto;

    [Display(GroupName = "縁取り", Name = "太さ", Description = "縁取りの幅です（通常は画面ピクセル、ワールド空間太さON時はワールド単位）。")]
    [AnimationSlider("F1", "px", 0, 100)]
    public Animation Thickness { get; } = new Animation(4.0, 0, 500);

    [Display(GroupName = "縁取り", Name = "ワールド空間太さ", Description = "ONにするとカメラ距離に応じて太さが遠近変化します（反転ハル法時）。")]
    [ShowPropertyEditorWhen(nameof(Mode), D3DOutlineMode.Auto | D3DOutlineMode.InvertedHull3D)]
    [ToggleSlider]
    public bool WorldSpaceThickness
    {
        get => worldSpaceThickness;
        set => Set(ref worldSpaceThickness, value);
    }
    private bool worldSpaceThickness;

    [Display(GroupName = "縁取り", Name = "色")]
    [ColorPicker]
    public Color Color
    {
        get => color;
        set => Set(ref color, value);
    }
    private Color color = Colors.Black;

    [Display(GroupName = "縁取り", Name = "不透明度")]
    [AnimationSlider("F1", "%", 0, 100)]
    public Animation Opacity { get; } = new Animation(100.0, 0, 100);

    [Display(GroupName = "縁取り", Name = "ぼかし", Description = "スクリーンスペース方式時の輪郭エッジの柔らかさです。")]
    [ShowPropertyEditorWhen(nameof(Mode), D3DOutlineMode.Auto | D3DOutlineMode.ScreenSpace)]
    [AnimationSlider("F1", "", 0, 20)]
    public Animation Blur { get; } = new Animation(0.0, 0, 100);

    [Display(GroupName = "縁取り", Name = "精度 (サンプル数)", Description = "スクリーンスペース方式の方向サンプル数です。")]
    [ShowPropertyEditorWhen(nameof(Mode), D3DOutlineMode.Auto | D3DOutlineMode.ScreenSpace)]
    [AnimationSlider("F0", "", 8, 64)]
    public Animation Quality { get; } = new Animation(16.0, 8, 64);

    [Display(GroupName = "縁取り", Name = "縁取りのみ表示", Description = "ONにすると本体を描画せず輪郭線のみを残します。")]
    [ToggleSlider]
    public bool OutlineOnly
    {
        get => outlineOnly;
        set => Set(ref outlineOnly, value);
    }
    private bool outlineOnly;

    [Display(GroupName = "3D詳細", Name = "深度バイアス", Description = "反転ハル法で本体に輪郭線が食い込むのを防ぐ深度オフセットです。")]
    [ShowPropertyEditorWhen(nameof(Mode), D3DOutlineMode.Auto | D3DOutlineMode.InvertedHull3D)]
    [AnimationSlider("F4", "", -0.01, 0.05)]
    public Animation DepthBias { get; } = new Animation(0.0005, -0.05, 0.05);

    [Display(GroupName = "3D詳細", Name = "スムーズ法線を使用", Description = "同位置の頂点法線を平均化して角割れのない滑らかな3D輪郭を作ります。")]
    [ShowPropertyEditorWhen(nameof(Mode), D3DOutlineMode.Auto | D3DOutlineMode.InvertedHull3D)]
    [ToggleSlider]
    public bool SmoothNormals
    {
        get => smoothNormals;
        set => Set(ref smoothNormals, value);
    }
    private bool smoothNormals = true;

    internal OutlineParameters Evaluate(long frame, long length, int fps) => new()
    {
        Enabled = IsEnabled,
        Mode = Mode,
        Thickness = MathF.Max(0f, (float)Thickness.GetValue(frame, length, fps)),
        WorldSpaceThickness = WorldSpaceThickness,
        Color = new Vector4(Color.R / 255f, Color.G / 255f, Color.B / 255f, Color.A / 255f),
        Opacity = Math.Clamp((float)Opacity.GetValue(frame, length, fps) / 100f, 0f, 1f),
        Blur = MathF.Max(0f, (float)Blur.GetValue(frame, length, fps)),
        Quality = Math.Clamp((int)Math.Round(Quality.GetValue(frame, length, fps)), 8, 64),
        OutlineOnly = OutlineOnly,
        DepthBias = (float)DepthBias.GetValue(frame, length, fps),
        SmoothNormals = SmoothNormals,
    };

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription) => [];

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new D3DPassthroughProcessor();

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return Thickness;
        yield return Opacity;
        yield return Blur;
        yield return Quality;
        yield return DepthBias;
    }
}
