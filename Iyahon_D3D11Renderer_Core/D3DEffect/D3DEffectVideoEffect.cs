using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Newtonsoft.Json;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect;

/// <summary>
/// D3Dエフェクトの映像エフェクト。
/// YMM4のエフェクト一覧に「D3Dエフェクト」として表示される。
/// D3D描画が有効なアイテムでのみ機能する。
/// </summary>
[VideoEffect("D3Dエフェクト", new[] { "D3D" }, new[] { "3D", "D3D", "エフェクト", "立方体", "球" })]
public class D3DEffectVideoEffect : VideoEffectBase
{
    [JsonIgnore]
    public override string Label
    {
        get
        {
            var effectName = "未選択";
            if (!string.IsNullOrEmpty(SelectedEffectId))
            {
                var info = D3DEffectRegistry.GetEffectInfo(SelectedEffectId);
                if (info != null) effectName = info.Name;
            }
            return $"D3Dエフェクト ({effectName})";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // エフェクト選択
    // ═══════════════════════════════════════════════════════════════

    [Display(GroupName = "D3Dエフェクト", Name = "エフェクト", Description = "使用するD3Dエフェクトを選択します。")]
    [EnumComboBox]
    public D3DEffectSelection EffectSelection
    {
        get => _effectSelection;
        set
        {
            Set(ref _effectSelection, value);
            // EnumからIDに変換
            SelectedEffectId = value switch
            {
                D3DEffectSelection.Cube => typeof(Effects.CubeD3DEffect).FullName ?? "",
                D3DEffectSelection.Sphere => typeof(Effects.SphereD3DEffect).FullName ?? "",
                _ => "",
            };
            OnPropertyChanged(nameof(Label));
        }
    }
    private D3DEffectSelection _effectSelection = D3DEffectSelection.Cube;

    /// <summary>内部用: 選択されたエフェクトのID</summary>
    public string SelectedEffectId
    {
        get => _selectedEffectId;
        set => Set(ref _selectedEffectId, value);
    }
    private string _selectedEffectId = typeof(Effects.CubeD3DEffect).FullName ?? "";

    // ═══════════════════════════════════════════════════════════════
    // 共通パラメータ
    // ═══════════════════════════════════════════════════════════════

    [Display(GroupName = "D3Dエフェクト", Name = "ライティング強度", Description = "簡易ライティングの強度です。0で無効。")]
    [AnimationSlider("F2", "", 0, 1)]
    public Animation LightIntensity { get; } = new Animation(0.5, 0, 1);

    // ═══════════════════════════════════════════════════════════════
    // 立方体専用パラメータ
    // ═══════════════════════════════════════════════════════════════

    [Display(GroupName = "立方体", Name = "奥行き", Description = "立方体の奥行きスケールです。")]
    [AnimationSlider("F2", "px", 0, 500)]
    [ShowPropertyEditorWhen(nameof(EffectSelection), D3DEffectSelection.Cube)]
    public Animation CubeDepthScale { get; } = new Animation(100.0, 0, 100000);

    // ═══════════════════════════════════════════════════════════════
    // 球専用パラメータ
    // ═══════════════════════════════════════════════════════════════

    [Display(GroupName = "球", Name = "球の深さ", Description = "球の奥行きスケールです。1.0で正球。")]
    [AnimationSlider("F2", "", 0, 5)]
    [ShowPropertyEditorWhen(nameof(EffectSelection), D3DEffectSelection.Sphere)]
    public Animation SphereDepthScale { get; } = new Animation(1.0, 0, 10);

    // ═══════════════════════════════════════════════════════════════
    // IVideoEffect
    // ═══════════════════════════════════════════════════════════════

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
    {
        return new D3DEffectVideoEffectProcessor(devices, this);
    }

    protected override IEnumerable<IAnimatable> GetAnimatables() =>
        new IAnimatable[] { LightIntensity, CubeDepthScale, SphereDepthScale };

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        => Array.Empty<string>();
}

/// <summary>
/// D3Dエフェクトの選択肢（UIドロップダウン用）。
/// </summary>
public enum D3DEffectSelection
{
    [Display(Name = "立方体")] Cube = 1,
    [Display(Name = "球")] Sphere = 2,
}
