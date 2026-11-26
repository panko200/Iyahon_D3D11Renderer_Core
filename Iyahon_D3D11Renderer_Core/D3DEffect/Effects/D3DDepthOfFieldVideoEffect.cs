using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

/// <summary>
/// D3D被写界深度の映像エフェクト。
/// YMM4のエフェクト一覧に「D3D被写界深度」として表示される。
/// </summary>
[VideoEffect("D3D被写界深度", new[] { "D3D" }, new[] { "3D", "D3D", "被写界深度", "DoF", "ボケ", "ぼけ" })]
public class D3DDepthOfFieldVideoEffect : VideoEffectBase
{
    public override string Label => "D3D被写界深度";

    // ── パラメータ ──

    [Display(GroupName = "被写界深度", Name = "焦点距離", Description = "ピントが合うカメラからの距離です（ピクセル）。標準的な距離は1000px付近です。")]
    [AnimationSlider("F0", "px", 0, 5000)]
    public Animation FocusDistance { get; } = new Animation(1000, 0, 100000);

    [Display(GroupName = "被写界深度", Name = "絞り", Description = "ボケの大きさを調整します（ピクセル）。数値が大きいほどボケが大きくなります。")]
    [AnimationSlider("F0", "px", 0, 100)]
    public Animation Aperture { get; } = new Animation(30, 0, 10000);

    [Display(GroupName = "被写界深度", Name = "焦点領域範囲", Description = "ピントが合ってボケない領域の幅です（ピクセル）。")]
    [AnimationSlider("F0", "px", 0, 500)]
    public Animation FocusRange { get; } = new Animation(100, 0, 100000);

    [Display(GroupName = "ボケ倍率", Name = "ボケ倍率手前", Description = "焦点より手前（カメラに近い側）のボケやすさです。")]
    [AnimationSlider("F0", "%", 0, 200)]
    public Animation NearBlurScale { get; } = new Animation(100, 0, 1000);

    [Display(GroupName = "ボケ倍率", Name = "ボケ倍率奥", Description = "焦点より奥（カメラから遠い側）のボケやすさです。")]
    [AnimationSlider("F0", "%", 0, 200)]
    public Animation FarBlurScale { get; } = new Animation(100, 0, 1000);

    [Display(GroupName = "ボケ倍率", Name = "ぼかし最大値", Description = "最大のボケ半径（ピクセル単位）です。重い処理を避けるため、上限を制限します。")]
    [AnimationSlider("F0", "px", 0, 30)]
    public Animation MaxBlur { get; } = new Animation(15, 0, 100);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new D3DPassthroughProcessor();

    protected override IEnumerable<IAnimatable> GetAnimatables() =>
        new IAnimatable[] { FocusDistance, Aperture, FocusRange, NearBlurScale, FarBlurScale, MaxBlur };

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        => Array.Empty<string>();
}
