using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Windows.Media;
using Iyahon_D3D11Renderer_Core.Lighting;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

/// <summary>
/// 距離フォグ／高さフォグおよびスクリーン空間ゴッドレイ（光筋）をアニメーション制御するエフェクト。
/// グループ制御アイテムやカメラ近傍のアイテムに適用してシーン全体の大気を演出できます。
/// </summary>
[VideoEffect("D3D大気・フォグ", new[] { "D3D" }, new[] { "3D", "D3D", "フォグ", "霧", "大気", "ゴッドレイ", "光筋", "ボリューメトリック" })]
public class D3DAtmosphereVideoEffect : VideoEffectBase
{
    public override string Label => "D3D大気・フォグ";

    [Display(GroupName = "距離・高さフォグ", Name = "フォグを有効にする")]
    [ToggleSlider]
    public bool EnableFog
    {
        get => enableFog;
        set => Set(ref enableFog, value);
    }
    private bool enableFog = true;

    [Display(GroupName = "距離・高さフォグ", Name = "フォグの色")]
    [ColorPicker]
    public Color FogColor
    {
        get => fogColor;
        set => Set(ref fogColor, value);
    }
    private Color fogColor = Color.FromRgb(184, 204, 224);

    [Display(GroupName = "距離・高さフォグ", Name = "最大濃度")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation FogDensity { get; } = new Animation(25.0, 0.0, 100.0);

    [Display(GroupName = "距離・高さフォグ", Name = "開始距離")]
    [AnimationSlider("F0", "px", 0, 10000)]
    public Animation FogStart { get; } = new Animation(800.0, 0.0, 20000.0);

    [Display(GroupName = "距離・高さフォグ", Name = "最大距離")]
    [AnimationSlider("F0", "px", 100, 20000)]
    public Animation FogEnd { get; } = new Animation(4000.0, 100.0, 50000.0);

    [Display(GroupName = "距離・高さフォグ", Name = "高さフォグ倍率", Description = "Y座標が低い位置（床面付近）ほど霧を濃くする割合です。")]
    [AnimationSlider("F0", "%", 0, 100)]
    public Animation HeightFogStrength { get; } = new Animation(0.0, 0.0, 100.0);

    [Display(GroupName = "距離・高さフォグ", Name = "基準高さ Y")]
    [AnimationSlider("F0", "px", -2000, 2000)]
    public Animation HeightFogBaseY { get; } = new Animation(300.0, -5000.0, 5000.0);

    [Display(GroupName = "ゴッドレイ（光筋）", Name = "ゴッドレイを有効にする", Description = "画面内の光源や明るい空から光の筋を放射状に伸ばします。")]
    [ToggleSlider]
    public bool EnableGodRays
    {
        get => enableGodRays;
        set => Set(ref enableGodRays, value);
    }
    private bool enableGodRays = false;

    [Display(GroupName = "ゴッドレイ（光筋）", Name = "光筋の強さ")]
    [AnimationSlider("F0", "%", 0, 300)]
    public Animation GodRayIntensity { get; } = new Animation(60.0, 0.0, 300.0);

    [Display(GroupName = "ゴッドレイ（光筋）", Name = "減衰率 (Decay)")]
    [AnimationSlider("F2", "", 0.80, 0.99)]
    public Animation GodRayDecay { get; } = new Animation(0.95, 0.80, 0.99);

    [Display(GroupName = "ゴッドレイ（光筋）", Name = "散乱長さ (Density)")]
    [AnimationSlider("F2", "", 0.1, 2.0)]
    public Animation GodRayDensity { get; } = new Animation(0.90, 0.1, 2.0);

    [Display(GroupName = "ゴッドレイ（光筋）", Name = "光筋の色")]
    [ColorPicker]
    public Color GodRayColor
    {
        get => godRayColor;
        set => Set(ref godRayColor, value);
    }
    private Color godRayColor = Color.FromRgb(255, 242, 214);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new D3DPassthroughProcessor();

    public AtmosphereParameters Evaluate(long frame, long length, int fps)
    {
        float fogStart = MathF.Max(0f, (float)FogStart.GetValue(frame, length, fps));
        float fogEnd = MathF.Max(fogStart + 50f, (float)FogEnd.GetValue(frame, length, fps));

        return new AtmosphereParameters
        {
            Enabled = EnableFog || EnableGodRays,
            EnableFog = EnableFog,
            FogColor = new Vector3(
                SrgbByteToLinear(FogColor.R),
                SrgbByteToLinear(FogColor.G),
                SrgbByteToLinear(FogColor.B)),
            FogDensity = Math.Clamp((float)FogDensity.GetValue(frame, length, fps) / 100f, 0f, 1f),
            FogStart = fogStart,
            FogEnd = fogEnd,
            HeightFogStrength = Math.Clamp((float)HeightFogStrength.GetValue(frame, length, fps) / 100f, 0f, 1f),
            HeightFogBaseY = (float)HeightFogBaseY.GetValue(frame, length, fps),
            EnableGodRays = EnableGodRays,
            GodRayIntensity = Math.Clamp((float)GodRayIntensity.GetValue(frame, length, fps) / 100f, 0f, 3f),
            GodRayDecay = Math.Clamp((float)GodRayDecay.GetValue(frame, length, fps), 0.80f, 0.99f),
            GodRayDensity = Math.Clamp((float)GodRayDensity.GetValue(frame, length, fps), 0.1f, 2.0f),
            GodRayColor = new Vector3(
                SrgbByteToLinear(GodRayColor.R),
                SrgbByteToLinear(GodRayColor.G),
                SrgbByteToLinear(GodRayColor.B)),
        };
    }

    private static float SrgbByteToLinear(byte b)
    {
        float x = b / 255f;
        return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    protected override IEnumerable<IAnimatable> GetAnimatables() =>
        new IAnimatable[]
        {
            FogDensity, FogStart, FogEnd, HeightFogStrength, HeightFogBaseY,
            GodRayIntensity, GodRayDecay, GodRayDensity
        };

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        => Array.Empty<string>();
}
