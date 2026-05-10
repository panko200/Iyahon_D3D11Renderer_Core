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
/// D3D立方体エフェクトの映像エフェクト。
/// YMM4のエフェクト一覧に「D3D立方体」として表示される。
/// </summary>
[VideoEffect("D3D立方体", new[] { "D3D" }, new[] { "3D", "D3D", "立方体", "Cube" })]
public class CubeD3DVideoEffect : VideoEffectBase, ID3DVideoEffect
{
    public override string Label => "D3D立方体";

    /// <summary>エフェクトID（レジストリで登録されたIDと一致）</summary>
    public string D3DEffectId => typeof(CubeD3DEffect).FullName ?? "";

    // ── パラメータ ──

    [Display(GroupName = "D3D立方体", Name = "奥行き", Description = "立方体の奥行きスケールです。")]
    [AnimationSlider("F2", "px", 0, 500)]
    public Animation DepthScale { get; } = new Animation(100.0, 0, 100000);

    [Display(GroupName = "D3D立方体", Name = "ライティング強度", Description = "簡易ライティングの強度です。0で無効。")]
    [AnimationSlider("F2", "", 0, 1)]
    public Animation LightIntensity { get; } = new Animation(0.5, 0, 1);

    /// <summary>
    /// エフェクト固有パラメータを ID3DEffect に設定する。
    /// </summary>
    public void ConfigureEffect(ID3DEffect effect, long frame, long length, int fps)
    {
        if (effect is CubeD3DEffect cube)
        {
            cube.DepthScale = (float)DepthScale.GetValue(frame, length, fps);
            cube.LightIntensity = (float)LightIntensity.GetValue(frame, length, fps);
        }
    }

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new D3DPassthroughProcessor();

    protected override IEnumerable<IAnimatable> GetAnimatables() =>
        new IAnimatable[] { DepthScale, LightIntensity };

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        => Array.Empty<string>();
}
