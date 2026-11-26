using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Windows.Media;
using Iyahon_D3D11Renderer_Core.Lighting;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Settings;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.D3DEffect.Effects;

public enum D3DMaterialPreset
{
    [Display(Name = "カスタム（手動設定）")]
    Custom = 0,

    [Display(Name = "プラスチック（艶あり）")]
    Plastic = 1,

    [Display(Name = "金属（ブラシ仕上げ）")]
    Metal = 2,

    [Display(Name = "クローム（鏡面ミラー）")]
    Chrome = 3,

    [Display(Name = "ゴールド（純金）")]
    Gold = 4,

    [Display(Name = "マット（つや消し）")]
    Matte = 5,

    [Display(Name = "カーペイント（クリアコート層）")]
    CarPaint = 6,

    [Display(Name = "自己発光（ネオン/ライト）")]
    Emissive = 7,

    [Display(Name = "ガラス（屈折・フレネル）")]
    Glass = 8,
}

/// <summary>
/// アイテム個別の PBR マテリアル（金属度・粗さ・クリアコート・発光・ノーマル/ラフネスマップ）を設定する映像エフェクト。
/// 通常の2Dアイテム、3Dエフェクト（立方体・球・押し出し）、および3Dモデルすべてに適用可能。
/// </summary>
[VideoEffect("D3Dマテリアル", new[] { "D3D" }, new[] { "3D", "D3D", "マテリアル", "PBR", "金属", "光沢", "発光", "ノーマルマップ", "ラフネス", "クリアコート" })]
public class D3DMaterialVideoEffect : VideoEffectBase
{
    public override string Label => "D3Dマテリアル";

    [Display(GroupName = "質感プリセット", Name = "プリセット", Description = "代表的な質感プリセットを選択するか、「カスタム」で全パラメータを細かく調整します。")]
    [EnumComboBox]
    public D3DMaterialPreset Preset
    {
        get => preset;
        set => Set(ref preset, value);
    }
    private D3DMaterialPreset preset = D3DMaterialPreset.Custom;

    [Display(GroupName = "PBR質感", Name = "金属度 (Metallic)", Description = "0で非金属（プラスチック・紙・木材等）、1で純金属になります。")]
    [AnimationSlider("F2", "", 0, 1)]
    [ShowPropertyEditorWhen(nameof(Preset), D3DMaterialPreset.Custom)]
    public Animation Metallic { get; } = new Animation(0.0, 0.0, 1.0);

    [Display(GroupName = "PBR質感", Name = "粗さ (Roughness)", Description = "0.04で鏡面のようなツルツル感、1.0で完全に拡散するマットな質感になります。")]
    [AnimationSlider("F2", "", 0.04, 1)]
    [ShowPropertyEditorWhen(nameof(Preset), D3DMaterialPreset.Custom)]
    public Animation Roughness { get; } = new Animation(0.35, 0.04, 1.0);

    [Display(GroupName = "PBR質感", Name = "クリアコート", Description = "表面の上に透明な艶出し塗装層（車のボディ塗装やニス仕上げのような二重ハイライト）を追加します。")]
    [AnimationSlider("F2", "", 0, 1)]
    [ShowPropertyEditorWhen(nameof(Preset), D3DMaterialPreset.Custom)]
    public Animation Clearcoat { get; } = new Animation(0.0, 0.0, 1.0);

    [Display(GroupName = "PBR質感", Name = "透過屈折 (Transmission)", Description = "0で不透明、1でガラスや水のような透過・屈折表現になります（3Dモデル・ガラス向け）。")]
    [AnimationSlider("F2", "", 0, 1)]
    [ShowPropertyEditorWhen(nameof(Preset), D3DMaterialPreset.Custom)]
    public Animation Transmission { get; } = new Animation(0.0, 0.0, 1.0);

    [Display(GroupName = "PBR質感", Name = "屈折率 (IOR)", Description = "ガラスは約1.45〜1.52、水は1.33、ダイヤモンドは2.42です。")]
    [AnimationSlider("F2", "", 1.0, 2.5)]
    [ShowPropertyEditorWhen(nameof(Preset), D3DMaterialPreset.Custom)]
    public Animation Ior { get; } = new Animation(1.45, 1.0, 2.5);

    [Display(GroupName = "PBR質感", Name = "反射・乗算ティント色", Description = "ベースカラー（および金属時の鏡面反射色）に乗算される色味です。")]
    [ColorPicker]
    [ShowPropertyEditorWhen(nameof(Preset), D3DMaterialPreset.Custom)]
    public Color TintColor
    {
        get => tintColor;
        set => Set(ref tintColor, value);
    }
    private Color tintColor = Colors.White;

    [Display(GroupName = "自己発光 (Emissive)", Name = "発光色")]
    [ColorPicker]
    public Color EmissiveColor
    {
        get => emissiveColor;
        set => Set(ref emissiveColor, value);
    }
    private Color emissiveColor = Colors.White;

    [Display(GroupName = "自己発光 (Emissive)", Name = "発光強度", Description = "1.0以上でブルームやHDR効果により光り輝きます。")]
    [AnimationSlider("F2", "", 0, 10)]
    public Animation EmissiveIntensity { get; } = new Animation(0.0, 0.0, 20.0);

    [Display(GroupName = "表面ディテール", Name = "ノーマルマップ", Description = "微細な凹凸を表現する法線マップ画像を指定します。")]
    [FileSelector(FileGroupType.None, CustomFilterName = "画像ファイル", CustomFilterValue = "*.png;*.jpg;*.jpeg;*.bmp;*.dds;*.tga;*.tif;*.tiff")]
    public string NormalMapPath
    {
        get => normalMapPath;
        set => Set(ref normalMapPath, value ?? string.Empty);
    }
    private string normalMapPath = string.Empty;

    [Display(GroupName = "表面ディテール", Name = "凹凸の強さ", Description = "ノーマルマップの法線変化の強度倍率です。")]
    [AnimationSlider("F2", "", 0, 3)]
    public Animation NormalStrength { get; } = new Animation(1.0, 0.0, 3.0);

    [Display(GroupName = "表面ディテール", Name = "ラフネスマップ", Description = "場所ごとの粗さ（グレースケール）を画像で指定します。")]
    [FileSelector(FileGroupType.None, CustomFilterName = "画像ファイル", CustomFilterValue = "*.png;*.jpg;*.jpeg;*.bmp;*.dds;*.tga;*.tif;*.tiff")]
    public string RoughnessMapPath
    {
        get => roughnessMapPath;
        set => Set(ref roughnessMapPath, value ?? string.Empty);
    }
    private string roughnessMapPath = string.Empty;

    [Display(GroupName = "表面ディテール", Name = "UV繰り返し倍率", Description = "指定したノーマルマップ／ラフネスマップのタイリング倍率です。")]
    [AnimationSlider("F2", "x", 0.1, 20)]
    public Animation UvScale { get; } = new Animation(1.0, 0.1, 50.0);

    public override IVideoEffectProcessor CreateVideoEffect(IGraphicsDevicesAndContext devices)
        => new D3DPassthroughProcessor();

    public MaterialOverrideParameters Evaluate(long frame, long length, int fps)
    {
        float metallic = Math.Clamp((float)Metallic.GetValue(frame, length, fps), 0f, 1f);
        float roughness = Math.Clamp((float)Roughness.GetValue(frame, length, fps), 0.04f, 1f);
        float clearcoat = Math.Clamp((float)Clearcoat.GetValue(frame, length, fps), 0f, 1f);
        float transmission = Math.Clamp((float)Transmission.GetValue(frame, length, fps), 0f, 1f);
        float ior = Math.Clamp((float)Ior.GetValue(frame, length, fps), 1.0f, 2.5f);
        float emissiveInt = MathF.Max(0f, (float)EmissiveIntensity.GetValue(frame, length, fps));
        float normalStrength = Math.Clamp((float)NormalStrength.GetValue(frame, length, fps), 0f, 5f);
        float uvScale = Math.Clamp((float)UvScale.GetValue(frame, length, fps), 0.05f, 100f);

        Vector4 tintLinear = new Vector4(
            SrgbByteToLinear(TintColor.R),
            SrgbByteToLinear(TintColor.G),
            SrgbByteToLinear(TintColor.B),
            TintColor.A / 255f);

        switch (Preset)
        {
            case D3DMaterialPreset.Plastic:
                metallic = 0.0f;
                roughness = 0.28f;
                clearcoat = 0.15f;
                tintLinear = Vector4.One;
                break;
            case D3DMaterialPreset.Metal:
                metallic = 0.95f;
                roughness = 0.28f;
                clearcoat = 0.0f;
                tintLinear = new Vector4(0.92f, 0.94f, 0.96f, 1f);
                break;
            case D3DMaterialPreset.Chrome:
                metallic = 1.0f;
                roughness = 0.06f;
                clearcoat = 0.0f;
                tintLinear = Vector4.One;
                break;
            case D3DMaterialPreset.Gold:
                metallic = 1.0f;
                roughness = 0.18f;
                clearcoat = 0.0f;
                tintLinear = new Vector4(1.0f, 0.78f, 0.34f, 1f);
                break;
            case D3DMaterialPreset.Matte:
                metallic = 0.0f;
                roughness = 0.88f;
                clearcoat = 0.0f;
                tintLinear = Vector4.One;
                break;
            case D3DMaterialPreset.CarPaint:
                metallic = 0.45f;
                roughness = 0.32f;
                clearcoat = 1.0f;
                tintLinear = Vector4.One;
                break;
            case D3DMaterialPreset.Emissive:
                metallic = 0.0f;
                roughness = 0.50f;
                clearcoat = 0.0f;
                transmission = 0.0f;
                tintLinear = Vector4.One;
                emissiveInt = MathF.Max(emissiveInt, 2.5f);
                break;
            case D3DMaterialPreset.Glass:
                metallic = 0.0f;
                roughness = 0.05f;
                clearcoat = 0.0f;
                transmission = 1.0f;
                ior = 1.45f;
                tintLinear = Vector4.One;
                break;
        }

        Vector4 emissiveLinear = new Vector4(
            SrgbByteToLinear(EmissiveColor.R),
            SrgbByteToLinear(EmissiveColor.G),
            SrgbByteToLinear(EmissiveColor.B),
            emissiveInt);

        return new MaterialOverrideParameters
        {
            Enabled = true,
            Metallic = metallic,
            Roughness = roughness,
            Clearcoat = clearcoat,
            Transmission = transmission,
            Ior = ior,
            TintColor = tintLinear,
            EmissiveColor = emissiveLinear,
            NormalMapPath = string.IsNullOrWhiteSpace(NormalMapPath) ? null : NormalMapPath,
            NormalStrength = normalStrength,
            RoughnessMapPath = string.IsNullOrWhiteSpace(RoughnessMapPath) ? null : RoughnessMapPath,
            UvScale = uvScale,
        };
    }

    private static float SrgbByteToLinear(byte b)
    {
        float x = b / 255f;
        return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    protected override IEnumerable<IAnimatable> GetAnimatables() =>
        new IAnimatable[] { Metallic, Roughness, Clearcoat, Transmission, Ior, EmissiveIntensity, NormalStrength, UvScale };

    public override IEnumerable<string> CreateExoVideoFilters(int keyFrameIndex, ExoOutputDescription exoOutputDescription)
        => Array.Empty<string>();
}
