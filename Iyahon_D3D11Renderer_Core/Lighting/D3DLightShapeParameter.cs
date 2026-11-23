using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// D3D11光源のパラメータ。
/// YMM4のプロパティエディタにアニメーション可能な光源設定が表示される。
/// </summary>
internal class D3DLightShapeParameter : ShapeParameterBase
{
    // ── 光源タイプ ──

    [Display(GroupName = "光源設定", Name = "光源タイプ")]
    [EnumComboBox]
    public LightType LightType
    {
        get => lightType;
        set => Set(ref lightType, value);
    }
    private LightType lightType = LightType.Directional;

    // ── 色 ──

    [Display(GroupName = "光源設定", Name = "光の色")]
    [ColorPicker]
    public Color LightColor
    {
        get => lightColor;
        set => Set(ref lightColor, value);
    }
    private Color lightColor = Colors.White;

    // ── 強度 ──

    [Display(GroupName = "光源設定", Name = "強度")]
    [AnimationSlider("F2", "", 0, 5)]
    public Animation Intensity { get; } = new Animation(1.0, 0, 100);

    // ── ガイド表示（見やすく描画）トグル ──

    [Display(GroupName = "光源設定", Name = "位置を視覚化", Description = "ONにするとプレビュー画面上に光源の位置や方向を示すガイドライン・名前を表示します。")]
    [ToggleSlider]
    public bool ShowGizmo
    {
        get => showGizmo;
        set => Set(ref showGizmo, value);
    }
    private bool showGizmo = false;

    // ── 方向（ディレクショナル / スポット / エリア共用） ──

    [Display(GroupName = "方向（ディレクショナル/スポット/エリア）", Name = "方向X")]
    [AnimationSlider("F2", "", -1, 1)]
    public Animation DirectionX { get; } = new Animation(0.3, -1, 1);

    [Display(GroupName = "方向（ディレクショナル/スポット/エリア）", Name = "方向Y")]
    [AnimationSlider("F2", "", -1, 1)]
    public Animation DirectionY { get; } = new Animation(-0.7, -1, 1);

    [Display(GroupName = "方向（ディレクショナル/スポット/エリア）", Name = "方向Z")]
    [AnimationSlider("F2", "", -1, 1)]
    public Animation DirectionZ { get; } = new Animation(-1.0, -1, 1);

    // ── 範囲（ポイント/スポット/エリア） ──

    [Display(GroupName = "ポイント/スポット設定", Name = "影響範囲")]
    [AnimationSlider("F0", "px", 0, 10000)]
    public Animation Range { get; } = new Animation(5000, 0, 100000);

    // ── スポットライト角度 ──

    [Display(GroupName = "スポットライト設定", Name = "内側角度")]
    [AnimationSlider("F1", "°", 0, 90)]
    public Animation SpotInnerAngle { get; } = new Animation(15, 0, 90);

    [Display(GroupName = "スポットライト設定", Name = "外側角度")]
    [AnimationSlider("F1", "°", 0, 90)]
    public Animation SpotOuterAngle { get; } = new Animation(30, 0, 90);

    // ── エリアライト ──

    [Display(GroupName = "エリアライト設定", Name = "幅")]
    [AnimationSlider("F0", "px", 0, 2000)]
    public Animation AreaWidth { get; } = new Animation(200, 0, 10000);

    [Display(GroupName = "エリアライト設定", Name = "高さ")]
    [AnimationSlider("F0", "px", 0, 2000)]
    public Animation AreaHeight { get; } = new Animation(200, 0, 10000);

    // ── 影設定 ──

    [Display(GroupName = "影設定", Name = "影を落とす")]
    [ToggleSlider]
    public bool CastShadow
    {
        get => castShadow;
        set => Set(ref castShadow, value);
    }
    private bool castShadow = false;

    [Display(GroupName = "影設定", Name = "影の濃さ")]
    [AnimationSlider("F2", "", 0, 1)]
    [ShowPropertyEditorWhen(nameof(CastShadow), true)]
    public Animation ShadowIntensity { get; } = new Animation(0.5, 0, 1);

    [Display(GroupName = "影設定", Name = "シャドウバイアス")]
    [AnimationSlider("F5", "", 0.00001, 0.01)]
    [ShowPropertyEditorWhen(nameof(CastShadow), true)]
    public Animation ShadowBias { get; } = new Animation(0.0005, 0.00001, 0.1);

    [Display(GroupName = "影設定", Name = "法線バイアス", Description = "物体の法線方向にサンプリング位置をずらしてシャドウアクネ（縞模様ノイズ）を防ぎます。")]
    [AnimationSlider("F2", "px", 0, 5)]
    [ShowPropertyEditorWhen(nameof(CastShadow), true)]
    public Animation ShadowNormalBias { get; } = new Animation(1.5, 0, 10);

    [Display(GroupName = "影設定", Name = "影の柔らかさ", Description = "ソフトシャドウ有効時のぼかし半径倍率です。")]
    [AnimationSlider("F2", "", 0, 3)]
    [ShowPropertyEditorWhen(nameof(CastShadow), true)]
    public Animation ShadowSoftness { get; } = new Animation(1.0, 0, 5);

    // ── コンストラクタ ──

    public D3DLightShapeParameter(SharedDataStore? sharedData) : base(sharedData) { }
    public D3DLightShapeParameter() : this(null) { }

    // ── IShapeParameter 実装 ──

    public override IEnumerable<string> CreateMaskExoFilter(
        int keyFrameIndex, ExoOutputDescription desc, ShapeMaskExoOutputDescription shapeMaskDesc)
        => Array.Empty<string>();

    public override IEnumerable<string> CreateShapeItemExoFilter(
        int keyFrameIndex, ExoOutputDescription desc)
        => Array.Empty<string>();

    public override IShapeSource CreateShapeSource(IGraphicsDevicesAndContext devices)
        => new D3DLightShapeSource(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return Intensity;
        yield return DirectionX;
        yield return DirectionY;
        yield return DirectionZ;
        yield return Range;
        yield return SpotInnerAngle;
        yield return SpotOuterAngle;
        yield return AreaWidth;
        yield return AreaHeight;
        yield return ShadowIntensity;
        yield return ShadowBias;
        yield return ShadowNormalBias;
        yield return ShadowSoftness;
    }

    protected override void LoadSharedData(SharedDataStore store)
    {
        var data = store.Load<SharedData>();
        if (data is null) return;
        data.CopyTo(this);
    }

    protected override void SaveSharedData(SharedDataStore store)
        => store.Save(new SharedData(this));

    // ── SharedData ──

    private class SharedData
    {
        public LightType LightType { get; set; }
        public Color LightColor { get; set; }
        public Animation Intensity { get; } = new Animation(1.0, 0, 100);
        public bool ShowGizmo { get; set; }
        public Animation DirectionX { get; } = new Animation(0.3, -1, 1);
        public Animation DirectionY { get; } = new Animation(-0.7, -1, 1);
        public Animation DirectionZ { get; } = new Animation(-1.0, -1, 1);
        public Animation Range { get; } = new Animation(5000, 0, 100000);
        public Animation SpotInnerAngle { get; } = new Animation(15, 0, 90);
        public Animation SpotOuterAngle { get; } = new Animation(30, 0, 90);
        public Animation AreaWidth { get; } = new Animation(200, 0, 10000);
        public Animation AreaHeight { get; } = new Animation(200, 0, 10000);
        public bool CastShadow { get; set; }
        public Animation ShadowIntensity { get; } = new Animation(0.5, 0, 1);
        public Animation ShadowBias { get; } = new Animation(0.0005, 0.00001, 0.1);
        public Animation ShadowNormalBias { get; } = new Animation(1.5, 0, 10);
        public Animation ShadowSoftness { get; } = new Animation(1.0, 0, 5);

        public SharedData(D3DLightShapeParameter p)
        {
            LightType = p.LightType;
            LightColor = p.LightColor;
            Intensity.CopyFrom(p.Intensity);
            ShowGizmo = p.ShowGizmo;
            DirectionX.CopyFrom(p.DirectionX);
            DirectionY.CopyFrom(p.DirectionY);
            DirectionZ.CopyFrom(p.DirectionZ);
            Range.CopyFrom(p.Range);
            SpotInnerAngle.CopyFrom(p.SpotInnerAngle);
            SpotOuterAngle.CopyFrom(p.SpotOuterAngle);
            AreaWidth.CopyFrom(p.AreaWidth);
            AreaHeight.CopyFrom(p.AreaHeight);
            CastShadow = p.CastShadow;
            ShadowIntensity.CopyFrom(p.ShadowIntensity);
            ShadowBias.CopyFrom(p.ShadowBias);
            ShadowNormalBias.CopyFrom(p.ShadowNormalBias);
            ShadowSoftness.CopyFrom(p.ShadowSoftness);
        }

        public void CopyTo(D3DLightShapeParameter p)
        {
            p.LightType = LightType;
            p.LightColor = LightColor;
            p.Intensity.CopyFrom(Intensity);
            p.ShowGizmo = ShowGizmo;
            p.DirectionX.CopyFrom(DirectionX);
            p.DirectionY.CopyFrom(DirectionY);
            p.DirectionZ.CopyFrom(DirectionZ);
            p.Range.CopyFrom(Range);
            p.SpotInnerAngle.CopyFrom(SpotInnerAngle);
            p.SpotOuterAngle.CopyFrom(SpotOuterAngle);
            p.AreaWidth.CopyFrom(AreaWidth);
            p.AreaHeight.CopyFrom(AreaHeight);
            p.CastShadow = CastShadow;
            p.ShadowIntensity.CopyFrom(ShadowIntensity);
            p.ShadowBias.CopyFrom(ShadowBias);
            p.ShadowNormalBias.CopyFrom(ShadowNormalBias);
            p.ShadowSoftness.CopyFrom(ShadowSoftness);
        }
    }
}
