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
using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Settings;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// D3D11環境のパラメータ。
/// HDRI環境マップ／スカイボックス／IBL・環境光／フォグ／ゴッドレイをタイムライン上でアニメーション制御する。
/// </summary>
internal class D3DEnvironmentShapeParameter : ShapeParameterBase
{
    private const string EnvFileFilter = "*.hdr;*.pic;*.png;*.jpg;*.jpeg;*.dds;*.bmp;*.tif;*.tiff;*.tga";

    // ── 座標系 ──

    [Display(GroupName = "座標系", Name = "Blender軸 (Z-Up)", Description = "ONにすると、この環境アイテムがある区間ではD3D11空間の座標軸・回転・3DビューポートをBlender準拠（+X=右, +Y=奥, +Z=上）に切り替えます。")]
    [ToggleSlider]
    public bool UseBlenderAxis
    {
        get => useBlenderAxis;
        set
        {
            if (Set(ref useBlenderAxis, value))
            {
                DepthSortVideoPlayerPatch.SetTimelineChanged();
                CameraPath.D3DCameraPathViewportBridge.NotifyPropertyEdited();
            }
        }
    }
    private bool useBlenderAxis = false;

    // ── HDRI環境マップ (IBL) ──

    [Display(GroupName = "HDRI環境マップ (IBL)", Name = "環境マップを上書き", Description = "ONにするとグローバル設定の代わりにこのアイテムのHDRI環境マップ・スカイボックス設定を使用します。")]
    [ToggleSlider]
    public bool OverrideEnvironmentMap
    {
        get => overrideEnvironmentMap;
        set => Set(ref overrideEnvironmentMap, value);
    }
    private bool overrideEnvironmentMap = true;

    [Display(GroupName = "HDRI環境マップ (IBL)", Name = "HDRI / パノラマ画像", Description = "360° Equirectangular 形式の .hdr / .dds / .png / .jpg 等を指定します（未指定でスタジオIBL）。")]
    [FileSelector(FileGroupType.None, CustomFilterName = "HDRI / 画像ファイル", CustomFilterValue = EnvFileFilter)]
    [ShowPropertyEditorWhen(nameof(OverrideEnvironmentMap), true)]
    public string EnvironmentMapPath
    {
        get => environmentMapPath;
        set => Set(ref environmentMapPath, value ?? string.Empty);
    }
    private string environmentMapPath = string.Empty;

    [Display(GroupName = "HDRI環境マップ (IBL)", Name = "水平回転")]
    [AnimationSlider("F1", "°", 0, 360)]
    [ShowPropertyEditorWhen(nameof(OverrideEnvironmentMap), true)]
    public Animation EnvironmentRotation { get; } = new Animation(0.0, -36000.0, 36000.0);

    [Display(GroupName = "HDRI環境マップ (IBL)", Name = "明るさ倍率")]
    [AnimationSlider("F2", "", 0.1, 5.0)]
    [ShowPropertyEditorWhen(nameof(OverrideEnvironmentMap), true)]
    public Animation EnvironmentExposure { get; } = new Animation(1.0, 0.0, 10.0);

    [Display(GroupName = "HDRI環境マップ (IBL)", Name = "環境反射強度", Description = "HDRIの鏡面映り込み（反射）の強さ倍率です。背景のスカイボックスや環境光を変えずに、モデルへの映り込みだけを抑えたい場合は下げてください（0で反射オフ）。")]
    [AnimationSlider("F2", "", 0.0, 2.0)]
    [ShowPropertyEditorWhen(nameof(OverrideEnvironmentMap), true)]
    public Animation EnvironmentReflectionIntensity { get; } = new Animation(1.0, 0.0, 5.0);

    [Display(GroupName = "HDRI環境マップ (IBL)", Name = "スカイボックスを表示", Description = "ONにすると背景に360°環境マップ（未指定時は手続き型空背景）を不透明描画します。")]
    [ToggleSlider]
    [ShowPropertyEditorWhen(nameof(OverrideEnvironmentMap), true)]
    public bool ShowSkybox
    {
        get => showSkybox;
        set => Set(ref showSkybox, value);
    }
    private bool showSkybox = false;

    [Display(GroupName = "HDRI環境マップ (IBL)", Name = "スカイボックス ぼかし")]
    [AnimationSlider("F0", "%", 0, 100)]
    [ShowPropertyEditorWhen(nameof(OverrideEnvironmentMap), true)]
    public Animation SkyboxBlur { get; } = new Animation(0.0, 0.0, 100.0);

    // ── 環境光・IBL・空色 ──

    [Display(GroupName = "環境光・IBL・空色", Name = "環境光・IBLを上書き", Description = "ONにするとIBL強度・アンビエント強度・手続き型空色をこのアイテムで制御します。")]
    [ToggleSlider]
    public bool OverrideIblAndAmbient
    {
        get => overrideIblAndAmbient;
        set => Set(ref overrideIblAndAmbient, value);
    }
    private bool overrideIblAndAmbient = false;

    [Display(GroupName = "環境光・IBL・空色", Name = "IBL 強度")]
    [AnimationSlider("F2", "", 0, 2)]
    [ShowPropertyEditorWhen(nameof(OverrideIblAndAmbient), true)]
    public Animation IblIntensity { get; } = new Animation(0.65, 0.0, 5.0);

    [Display(GroupName = "環境光・IBL・空色", Name = "環境光 (Ambient) 強度")]
    [AnimationSlider("F2", "", 0, 2)]
    [ShowPropertyEditorWhen(nameof(OverrideIblAndAmbient), true)]
    public Animation AmbientIntensity { get; } = new Animation(0.30, 0.0, 5.0);

    [Display(GroupName = "環境光・IBL・空色", Name = "空色 (手続き型環境)")]
    [ColorPicker]
    [ShowPropertyEditorWhen(nameof(OverrideIblAndAmbient), true)]
    public Color SkyColor
    {
        get => skyColor;
        set => Set(ref skyColor, value);
    }
    private Color skyColor = Color.FromRgb(214, 226, 243);

    // ── 距離・高さフォグ ──

    [Display(GroupName = "距離・高さフォグ", Name = "フォグを有効にする")]
    [ToggleSlider]
    public bool EnableFog
    {
        get => enableFog;
        set => Set(ref enableFog, value);
    }
    private bool enableFog = false;

    [Display(GroupName = "距離・高さフォグ", Name = "フォグの色")]
    [ColorPicker]
    [ShowPropertyEditorWhen(nameof(EnableFog), true)]
    public Color FogColor
    {
        get => fogColor;
        set => Set(ref fogColor, value);
    }
    private Color fogColor = Color.FromRgb(184, 204, 224);

    [Display(GroupName = "距離・高さフォグ", Name = "最大濃度")]
    [AnimationSlider("F0", "%", 0, 100)]
    [ShowPropertyEditorWhen(nameof(EnableFog), true)]
    public Animation FogDensity { get; } = new Animation(25.0, 0.0, 100.0);

    [Display(GroupName = "距離・高さフォグ", Name = "開始距離")]
    [AnimationSlider("F0", "px", 0, 10000)]
    [ShowPropertyEditorWhen(nameof(EnableFog), true)]
    public Animation FogStart { get; } = new Animation(800.0, 0.0, 20000.0);

    [Display(GroupName = "距離・高さフォグ", Name = "最大距離")]
    [AnimationSlider("F0", "px", 100, 20000)]
    [ShowPropertyEditorWhen(nameof(EnableFog), true)]
    public Animation FogEnd { get; } = new Animation(4000.0, 100.0, 50000.0);

    [Display(GroupName = "距離・高さフォグ", Name = "高さフォグ倍率", Description = "Y座標が低い位置（床面付近）ほど霧を濃くする割合です。")]
    [AnimationSlider("F0", "%", 0, 100)]
    [ShowPropertyEditorWhen(nameof(EnableFog), true)]
    public Animation HeightFogStrength { get; } = new Animation(0.0, 0.0, 100.0);

    [Display(GroupName = "距離・高さフォグ", Name = "基準高さ Y")]
    [AnimationSlider("F0", "px", -2000, 2000)]
    [ShowPropertyEditorWhen(nameof(EnableFog), true)]
    public Animation HeightFogBaseY { get; } = new Animation(300.0, -5000.0, 5000.0);

    // ── ゴッドレイ（光筋） ──

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
    [ShowPropertyEditorWhen(nameof(EnableGodRays), true)]
    public Animation GodRayIntensity { get; } = new Animation(60.0, 0.0, 300.0);

    [Display(GroupName = "ゴッドレイ（光筋）", Name = "減衰率 (Decay)")]
    [AnimationSlider("F2", "", 0.80, 0.99)]
    [ShowPropertyEditorWhen(nameof(EnableGodRays), true)]
    public Animation GodRayDecay { get; } = new Animation(0.95, 0.80, 0.99);

    [Display(GroupName = "ゴッドレイ（光筋）", Name = "散乱長さ (Density)")]
    [AnimationSlider("F2", "", 0.1, 2.0)]
    [ShowPropertyEditorWhen(nameof(EnableGodRays), true)]
    public Animation GodRayDensity { get; } = new Animation(0.90, 0.1, 2.0);

    [Display(GroupName = "ゴッドレイ（光筋）", Name = "光筋の色")]
    [ColorPicker]
    [ShowPropertyEditorWhen(nameof(EnableGodRays), true)]
    public Color GodRayColor
    {
        get => godRayColor;
        set => Set(ref godRayColor, value);
    }
    private Color godRayColor = Color.FromRgb(255, 242, 214);

    // ── コンストラクタ ──

    public D3DEnvironmentShapeParameter(SharedDataStore? sharedData) : base(sharedData) { }
    public D3DEnvironmentShapeParameter() : this(null) { }

    // ── 評価メソッド ──

    public EnvironmentParameters EvaluateEnvironment(long frame, long length, int fps)
    {
        return new EnvironmentParameters
        {
            Enabled = OverrideEnvironmentMap || OverrideIblAndAmbient || UseBlenderAxis,
            OverrideEnvironmentMap = OverrideEnvironmentMap,
            EnvironmentMapPath = string.IsNullOrWhiteSpace(EnvironmentMapPath) ? null : EnvironmentMapPath,
            RotationDeg = (float)EnvironmentRotation.GetValue(frame, length, fps),
            Exposure = Math.Clamp((float)EnvironmentExposure.GetValue(frame, length, fps), 0f, 10f),
            ReflectionIntensity = Math.Clamp((float)EnvironmentReflectionIntensity.GetValue(frame, length, fps), 0f, 5f),
            ShowSkybox = ShowSkybox,
            SkyboxBlur = Math.Clamp((float)SkyboxBlur.GetValue(frame, length, fps) / 100f, 0f, 1f),
            OverrideIblAndAmbient = OverrideIblAndAmbient,
            IblIntensity = Math.Clamp((float)IblIntensity.GetValue(frame, length, fps), 0f, 5f),
            AmbientIntensity = Math.Clamp((float)AmbientIntensity.GetValue(frame, length, fps), 0f, 5f),
            SkyColorLinear = new Vector3(
                SrgbByteToLinear(SkyColor.R),
                SrgbByteToLinear(SkyColor.G),
                SrgbByteToLinear(SkyColor.B)),
            FallbackCameraMatrix = default,
            UseBlenderAxis = UseBlenderAxis,
        };
    }

    public AtmosphereParameters EvaluateAtmosphere(long frame, long length, int fps)
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

    // ── IShapeParameter 実装 ──

    public override IEnumerable<string> CreateMaskExoFilter(
        int keyFrameIndex, ExoOutputDescription desc, ShapeMaskExoOutputDescription shapeMaskDesc)
        => Array.Empty<string>();

    public override IEnumerable<string> CreateShapeItemExoFilter(
        int keyFrameIndex, ExoOutputDescription desc)
        => Array.Empty<string>();

    public override IShapeSource CreateShapeSource(IGraphicsDevicesAndContext devices)
        => new D3DEnvironmentShapeSource(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return EnvironmentRotation;
        yield return EnvironmentExposure;
        yield return EnvironmentReflectionIntensity;
        yield return SkyboxBlur;
        yield return IblIntensity;
        yield return AmbientIntensity;
        yield return FogDensity;
        yield return FogStart;
        yield return FogEnd;
        yield return HeightFogStrength;
        yield return HeightFogBaseY;
        yield return GodRayIntensity;
        yield return GodRayDecay;
        yield return GodRayDensity;
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
        public bool UseBlenderAxis { get; set; } = false;

        public bool OverrideEnvironmentMap { get; set; } = true;
        public string EnvironmentMapPath { get; set; } = string.Empty;
        public Animation EnvironmentRotation { get; } = new Animation(0.0, -36000.0, 36000.0);
        public Animation EnvironmentExposure { get; } = new Animation(1.0, 0.0, 10.0);
        public Animation EnvironmentReflectionIntensity { get; } = new Animation(1.0, 0.0, 5.0);
        public bool ShowSkybox { get; set; } = false;
        public Animation SkyboxBlur { get; } = new Animation(0.0, 0.0, 100.0);

        public bool OverrideIblAndAmbient { get; set; } = false;
        public Animation IblIntensity { get; } = new Animation(0.65, 0.0, 5.0);
        public Animation AmbientIntensity { get; } = new Animation(0.30, 0.0, 5.0);
        public Color SkyColor { get; set; } = Color.FromRgb(214, 226, 243);

        public bool EnableFog { get; set; } = false;
        public Color FogColor { get; set; } = Color.FromRgb(184, 204, 224);
        public Animation FogDensity { get; } = new Animation(25.0, 0.0, 100.0);
        public Animation FogStart { get; } = new Animation(800.0, 0.0, 20000.0);
        public Animation FogEnd { get; } = new Animation(4000.0, 100.0, 50000.0);
        public Animation HeightFogStrength { get; } = new Animation(0.0, 0.0, 100.0);
        public Animation HeightFogBaseY { get; } = new Animation(300.0, -5000.0, 5000.0);

        public bool EnableGodRays { get; set; } = false;
        public Animation GodRayIntensity { get; } = new Animation(60.0, 0.0, 300.0);
        public Animation GodRayDecay { get; } = new Animation(0.95, 0.80, 0.99);
        public Animation GodRayDensity { get; } = new Animation(0.90, 0.1, 2.0);
        public Color GodRayColor { get; set; } = Color.FromRgb(255, 242, 214);

        public SharedData() { }

        public SharedData(D3DEnvironmentShapeParameter p)
        {
            UseBlenderAxis = p.UseBlenderAxis;
            OverrideEnvironmentMap = p.OverrideEnvironmentMap;
            EnvironmentMapPath = p.EnvironmentMapPath;
            EnvironmentRotation.CopyFrom(p.EnvironmentRotation);
            EnvironmentExposure.CopyFrom(p.EnvironmentExposure);
            EnvironmentReflectionIntensity.CopyFrom(p.EnvironmentReflectionIntensity);
            ShowSkybox = p.ShowSkybox;
            SkyboxBlur.CopyFrom(p.SkyboxBlur);
            OverrideIblAndAmbient = p.OverrideIblAndAmbient;
            IblIntensity.CopyFrom(p.IblIntensity);
            AmbientIntensity.CopyFrom(p.AmbientIntensity);
            SkyColor = p.SkyColor;
            EnableFog = p.EnableFog;
            FogColor = p.FogColor;
            FogDensity.CopyFrom(p.FogDensity);
            FogStart.CopyFrom(p.FogStart);
            FogEnd.CopyFrom(p.FogEnd);
            HeightFogStrength.CopyFrom(p.HeightFogStrength);
            HeightFogBaseY.CopyFrom(p.HeightFogBaseY);
            EnableGodRays = p.EnableGodRays;
            GodRayIntensity.CopyFrom(p.GodRayIntensity);
            GodRayDecay.CopyFrom(p.GodRayDecay);
            GodRayDensity.CopyFrom(p.GodRayDensity);
            GodRayColor = p.GodRayColor;
        }

        public void CopyTo(D3DEnvironmentShapeParameter p)
        {
            p.UseBlenderAxis = UseBlenderAxis;
            p.OverrideEnvironmentMap = OverrideEnvironmentMap;
            p.EnvironmentMapPath = EnvironmentMapPath ?? string.Empty;
            p.EnvironmentRotation.CopyFrom(EnvironmentRotation);
            p.EnvironmentExposure.CopyFrom(EnvironmentExposure);
            p.EnvironmentReflectionIntensity.CopyFrom(EnvironmentReflectionIntensity);
            p.ShowSkybox = ShowSkybox;
            p.SkyboxBlur.CopyFrom(SkyboxBlur);
            p.OverrideIblAndAmbient = OverrideIblAndAmbient;
            p.IblIntensity.CopyFrom(IblIntensity);
            p.AmbientIntensity.CopyFrom(AmbientIntensity);
            p.SkyColor = SkyColor;
            p.EnableFog = EnableFog;
            p.FogColor = FogColor;
            p.FogDensity.CopyFrom(FogDensity);
            p.FogStart.CopyFrom(FogStart);
            p.FogEnd.CopyFrom(FogEnd);
            p.HeightFogStrength.CopyFrom(HeightFogStrength);
            p.HeightFogBaseY.CopyFrom(HeightFogBaseY);
            p.EnableGodRays = EnableGodRays;
            p.GodRayIntensity.CopyFrom(GodRayIntensity);
            p.GodRayDecay.CopyFrom(GodRayDecay);
            p.GodRayDensity.CopyFrom(GodRayDensity);
            p.GodRayColor = GodRayColor;
        }
    }
}
