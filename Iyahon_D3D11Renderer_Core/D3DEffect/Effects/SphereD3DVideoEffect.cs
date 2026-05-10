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
/// D3D球エフェクトの映像エフェクト。
/// YMM4のエフェクト一覧に「D3D球」として表示される。
/// </summary>
[VideoEffect("D3D球", new[] { "D3D" }, new[] { "3D", "D3D", "球", "Sphere" })]
public class SphereD3DVideoEffect : VideoEffectBase, ID3DVideoEffect
{
    public override string Label => "D3D球";

    public string D3DEffectId => typeof(SphereD3DEffect).FullName ?? "";

    // ── パラメータ ──

    [Display(GroupName = "D3D球", Name = "球の深さ", Description = "球の奥行きスケールです。1.0で正球。")]
    [AnimationSlider("F2", "", 0, 5)]
    public Animation DepthScale { get; } = new Animation(1.0, 0, 10);

    [Display(GroupName = "D3D球", Name = "ライティング強度", Description = "簡易ライティングの強度です。0で無効。")]
    [AnimationSlider("F2", "", 0, 1)]
    public Animation LightIntensity { get; } = new Animation(0.5, 0, 1);

    public void ConfigureEffect(ID3DEffect effect, long frame, long length, int fps)
    {
        if (effect is SphereD3DEffect sphere)
        {
            sphere.DepthScale = (float)DepthScale.GetValue(frame, length, fps);
            sphere.LightIntensity = (float)LightIntensity.GetValue(frame, length, fps);
        }
    }

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new D3DPassthroughProcessor();

    protected override IEnumerable<IAnimatable> GetAnimatables() =>
        new IAnimatable[] { DepthScale, LightIntensity };

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        => Array.Empty<string>();
}
