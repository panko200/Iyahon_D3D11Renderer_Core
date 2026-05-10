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

/// <summary>
/// D3D押し出しエフェクトの映像エフェクト。
/// YMM4のエフェクト一覧に「D3D押し出し」として表示される。
/// テクスチャのα形状を立体化するレイマーチングエフェクト。
/// </summary>
[VideoEffect("D3D押し出し", new[] { "D3D" }, new[] { "3D", "D3D", "押し出し", "Extrusion", "立体化" })]
public class ExtrusionD3DVideoEffect : VideoEffectBase, ID3DVideoEffect
{
    public override string Label => "D3D押し出し";

    public string D3DEffectId => typeof(ExtrusionD3DEffect).FullName ?? "";

    // ── パラメータ ──

    [Display(GroupName = "D3D押し出し", Name = "厚み", Description = "押し出しの厚み（ピクセル）です。")]
    [AnimationSlider("F0", "px", 0, 500)]
    public Animation Thickness { get; } = new Animation(100.0, 0, 10000);

    [Display(GroupName = "D3D押し出し", Name = "ライティング強度", Description = "簡易ライティングの強度です。0で無効。")]
    [AnimationSlider("F2", "", 0, 1)]
    public Animation LightIntensity { get; } = new Animation(0.5, 0, 1);

    [Display(GroupName = "D3D押し出し", Name = "α閾値", Description = "立体化する際のα判定閾値です。小さいほど薄い部分も立体化されます。")]
    [AnimationSlider("F2", "", 0, 1)]
    public Animation AlphaThreshold { get; } = new Animation(0.99, 0.001, 1);

    [Display(GroupName = "D3D押し出し", Name = "側面", Description = "側面の種類です。")]
    [EnumComboBox]
    public ExtrusionSideType SideType
    {
        get => _sideType;
        set => Set(ref _sideType, value);
    }
    private ExtrusionSideType _sideType = ExtrusionSideType.Image;

    [Display(GroupName = "D3D押し出し", Name = "減衰", Description = "側面の減衰の強さです。")]
    [AnimationSlider("F0", "%", 0, 100)]
    [ShowPropertyEditorWhen(nameof(SideType), ExtrusionSideType.Image)]
    public Animation Attenuation { get; } = new Animation(0, 0, 100);

    [Display(GroupName = "D3D押し出し", Name = "側面色", Description = "側面の塗りつぶし色です。")]
    [ColorPicker]
    [ShowPropertyEditorWhen(nameof(SideType), ExtrusionSideType.Solid)]
    public Color SideColor
    {
        get => _sideColor;
        set => Set(ref _sideColor, value);
    }
    private Color _sideColor = Colors.White;

    public void ConfigureEffect(ID3DEffect effect, long frame, long length, int fps)
    {
        if (effect is ExtrusionD3DEffect extrusion)
        {
            extrusion.Thickness = (float)Thickness.GetValue(frame, length, fps);
            extrusion.LightIntensity = (float)LightIntensity.GetValue(frame, length, fps);
            extrusion.AlphaThreshold = (float)AlphaThreshold.GetValue(frame, length, fps);
            extrusion.ExtrusionType = (int)SideType;
            extrusion.SideColor = new Vector4(
                SideColor.R / 255f, SideColor.G / 255f,
                SideColor.B / 255f, SideColor.A / 255f);
            extrusion.Attenuation = (float)(Attenuation.GetValue(frame, length, fps) / 100.0);
        }
    }

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new D3DPassthroughProcessor();

    protected override IEnumerable<IAnimatable> GetAnimatables() =>
        new IAnimatable[] { Thickness, LightIntensity, AlphaThreshold, Attenuation };

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        => Array.Empty<string>();
}
