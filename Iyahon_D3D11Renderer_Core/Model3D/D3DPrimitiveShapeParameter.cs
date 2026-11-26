using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Windows.Media;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Exo;
using YukkuriMovieMaker.ItemEditor.CustomVisibilityAttributes;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Shape;
using YukkuriMovieMaker.Project;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

public enum D3DPrimitiveShapeKind
{
    [Display(Name = "立方体 (Cube)")]
    Cube = 10,

    [Display(Name = "UV球 (UV Sphere)")]
    UvSphere = 11,

    [Display(Name = "ICO球 (Ico Sphere)")]
    IcoSphere = 12,

    [Display(Name = "円 (Circle)")]
    Circle = 13,

    [Display(Name = "円柱 (Cylinder)")]
    Cylinder = 14,

    [Display(Name = "円錐 (Cone)")]
    Cone = 15,

    [Display(Name = "トーラス (Torus)")]
    Torus = 16,

    [Display(Name = "3Dベジェ曲線 (Bezier Curve)")]
    BezierCurve = 17,

    [Display(Name = "3Dノイズ (3D Noise)")]
    Noise3D = 18,
}

internal class D3DPrimitiveShapeParameter : ShapeParameterBase, ID3DModelTransformParameter
{
    [Display(GroupName = "図形設定", Name = "形状", Description = "生成する3D図形の種類を選択します。")]
    [EnumComboBox]
    public D3DPrimitiveShapeKind ShapeKind
    {
        get => shapeKind;
        set
        {
            if (shapeKind != value)
            {
                Set(ref shapeKind, value);
                // 形状を変更した場合はプロシージャル再生成へ切り替える
                if (!string.IsNullOrEmpty(embeddedMeshData))
                {
                    EmbeddedMeshData = string.Empty;
                }
            }
        }
    }
    private D3DPrimitiveShapeKind shapeKind = D3DPrimitiveShapeKind.Cube;

    [Display(GroupName = "図形設定", Name = "分割数 (横/円周)", Description = "球・円・円柱・円錐・トーラスの水平セグメント数です。")]
    [AnimationSlider("F0", "", 3, 64)]
    public Animation Segments { get; } = new Animation(24, 3, 64);

    [Display(GroupName = "図形設定", Name = "分割数 (縦/チューブ)", Description = "UV球のリング数・トーラスの断面セグメント数です。")]
    [AnimationSlider("F0", "", 3, 48)]
    public Animation Rings { get; } = new Animation(16, 3, 48);

    [Display(GroupName = "図形設定", Name = "細分化レベル", Description = "ICO球の細分化回数 (0〜4) です。")]
    [AnimationSlider("F0", "", 0, 4)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.IcoSphere)]
    public Animation Subdivisions { get; } = new Animation(2, 0, 4);

    [Display(GroupName = "図形設定", Name = "チューブ太さ比", Description = "トーラスのチューブ断面半径比率です。")]
    [AnimationSlider("F2", "", 0.05, 0.90)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Torus)]
    public Animation TubeRadius { get; } = new Animation(0.35, 0.05, 0.90);

    [Display(GroupName = "図形設定", Name = "スムーズシェード", Description = "ONにすると曲面を滑らかにシェーディングします。")]
    [ToggleSlider]
    public bool SmoothShading
    {
        get => smoothShading;
        set => Set(ref smoothShading, value);
    }
    private bool smoothShading = true;

    [Display(GroupName = "図形設定", Name = "底面基準配置", Description = "ONにすると図形の底面をY=0に揃えます。")]
    [ToggleSlider]
    public bool GroundToOrigin
    {
        get => groundToOrigin;
        set => Set(ref groundToOrigin, value);
    }
    private bool groundToOrigin = false;

    // ── 3Dベジェ曲線設定 ──

    [Display(GroupName = "3Dベジェ曲線", Name = "補間方式")]
    [EnumComboBox]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.BezierCurve)]
    public BezierCurveInterpolation BezierInterpolation
    {
        get => bezierInterpolation;
        set => Set(ref bezierInterpolation, value);
    }
    private BezierCurveInterpolation bezierInterpolation = BezierCurveInterpolation.CubicBezier;

    [Display(GroupName = "3Dベジェ曲線", Name = "線の種類")]
    [EnumComboBox]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.BezierCurve)]
    public BezierCurveLineStyle BezierLineStyle
    {
        get => bezierLineStyle;
        set => Set(ref bezierLineStyle, value);
    }
    private BezierCurveLineStyle bezierLineStyle = BezierCurveLineStyle.Tube3D;

    [Display(GroupName = "3Dベジェ曲線", Name = "断面角数 (ポリゴン)")]
    [AnimationSlider("F0", "角", 3, 64)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.BezierCurve)]
    public Animation BezierSides { get; } = new Animation(12, 3, 64);

    [Display(GroupName = "3Dベジェ曲線", Name = "曲線分割数 (解像度)")]
    [AnimationSlider("F0", "", 2, 64)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.BezierCurve)]
    public Animation BezierResolution { get; } = new Animation(16, 2, 64);

    [Display(GroupName = "3Dベジェ曲線", Name = "線を閉じる (ループ)")]
    [ToggleSlider]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.BezierCurve)]
    public bool BezierClosed
    {
        get => bezierClosed;
        set => Set(ref bezierClosed, value);
    }
    private bool bezierClosed;

    [Display(GroupName = "3Dベジェ曲線", Name = "始点の断面形状")]
    [EnumComboBox]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.BezierCurve)]
    public BezierCurveCapStyle BezierStartCap
    {
        get => bezierStartCap;
        set => Set(ref bezierStartCap, value);
    }
    private BezierCurveCapStyle bezierStartCap = BezierCurveCapStyle.Flat;

    [Display(GroupName = "3Dベジェ曲線", Name = "終点の断面形状")]
    [EnumComboBox]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.BezierCurve)]
    public BezierCurveCapStyle BezierEndCap
    {
        get => bezierEndCap;
        set => Set(ref bezierEndCap, value);
    }
    private BezierCurveCapStyle bezierEndCap = BezierCurveCapStyle.Flat;

    [Display(GroupName = "3Dベジェ曲線", Name = "制御点")]
    [BezierCurvePointEditor]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.BezierCurve)]
    public string BezierCurveData
    {
        get => bezierCurveData;
        set => Set(ref bezierCurveData, value ?? string.Empty);
    }
    private string bezierCurveData = BezierCurve3DData.CreateDefault().Serialize();

    // ── 3Dノイズ設定 ──

    [Display(GroupName = "3Dノイズ", Name = "ノイズの種類")]
    [EnumComboBox]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public D3DNoise3DType NoiseType
    {
        get => noiseType;
        set => Set(ref noiseType, value);
    }
    private D3DNoise3DType noiseType = D3DNoise3DType.Fractal;

    [Display(GroupName = "3Dノイズ", Name = "描画方式")]
    [EnumComboBox]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public D3DNoise3DRenderMode NoiseRenderMode
    {
        get => noiseRenderMode;
        set => Set(ref noiseRenderMode, value);
    }
    private D3DNoise3DRenderMode noiseRenderMode = D3DNoise3DRenderMode.Volume3D;

    [Display(GroupName = "3Dノイズ", Name = "スケール X")]
    [AnimationSlider("F2", "", 0.1, 20)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseScaleX { get; } = new Animation(3.0, 0.05, 100);

    [Display(GroupName = "3Dノイズ", Name = "スケール Y")]
    [AnimationSlider("F2", "", 0.1, 20)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseScaleY { get; } = new Animation(3.0, 0.05, 100);

    [Display(GroupName = "3Dノイズ", Name = "スケール Z")]
    [AnimationSlider("F2", "", 0.1, 20)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseScaleZ { get; } = new Animation(3.0, 0.05, 100);

    [Display(GroupName = "3Dノイズ", Name = "複雑度 (オクターブ)")]
    [AnimationSlider("F1", "", 1, 8)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseComplexity { get; } = new Animation(4.0, 1, 8);

    [Display(GroupName = "3Dノイズ", Name = "しきい値 (Threshold)")]
    [AnimationSlider("F2", "", 0, 1)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseThreshold { get; } = new Animation(0.42, 0, 1);

    [Display(GroupName = "3Dノイズ", Name = "滑らかさ (Softness)")]
    [AnimationSlider("F2", "", 0.01, 1)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseSoftness { get; } = new Animation(0.25, 0.005, 1);

    [Display(GroupName = "3Dノイズ", Name = "密度 (Density)")]
    [AnimationSlider("F2", "", 0.05, 8)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseDensity { get; } = new Animation(2.0, 0.05, 20);

    [Display(GroupName = "3Dノイズ", Name = "凹凸強さ (Surface3D)")]
    [AnimationSlider("F2", "", -2, 2)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseBumpStrength { get; } = new Animation(0.45, -5, 5);

    [Display(GroupName = "3Dノイズ", Name = "品質 (レイステップ)")]
    [AnimationSlider("F0", "", 16, 128)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseStepCount { get; } = new Animation(48, 16, 128);

    [Display(GroupName = "3Dノイズ", Name = "オフセット X")]
    [AnimationSlider("F2", "", -10, 10)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseOffsetX { get; } = new Animation(0.0, -1000, 1000);

    [Display(GroupName = "3Dノイズ", Name = "オフセット Y")]
    [AnimationSlider("F2", "", -10, 10)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseOffsetY { get; } = new Animation(0.0, -1000, 1000);

    [Display(GroupName = "3Dノイズ", Name = "オフセット Z")]
    [AnimationSlider("F2", "", -10, 10)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseOffsetZ { get; } = new Animation(0.0, -1000, 1000);

    [Display(GroupName = "3Dノイズ", Name = "展開 / 時間 (Evolution)")]
    [AnimationSlider("F2", "", -20, 20)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseEvolution { get; } = new Animation(0.0, -1000, 1000);

    [Display(GroupName = "3Dノイズ", Name = "シード値")]
    [AnimationSlider("F0", "", 0, 1000)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseSeed { get; } = new Animation(1.0, 0, 100000);

    [Display(GroupName = "3Dノイズ", Name = "反転")]
    [ToggleSlider]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public bool NoiseInvert
    {
        get => noiseInvert;
        set => Set(ref noiseInvert, value);
    }
    private bool noiseInvert;

    [Display(GroupName = "3Dノイズ", Name = "カラー 1 (低密度)")]
    [ColorPicker]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Color NoiseColorA
    {
        get => noiseColorA;
        set => Set(ref noiseColorA, value);
    }
    private Color noiseColorA = Color.FromRgb(200, 200, 200);

    [Display(GroupName = "3Dノイズ", Name = "カラー 1 不透明度 (Alpha)")]
    [AnimationSlider("F1", "%", 0, 100)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseColorAAlpha { get; } = new Animation(100.0, 0, 100);

    [Display(GroupName = "3Dノイズ", Name = "カラー 2 (高密度)")]
    [ColorPicker]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Color NoiseColorB
    {
        get => noiseColorB;
        set => Set(ref noiseColorB, value);
    }
    private Color noiseColorB = Color.FromRgb(255, 255, 255);

    [Display(GroupName = "3Dノイズ", Name = "カラー 2 不透明度 (Alpha)")]
    [AnimationSlider("F1", "%", 0, 100)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseColorBAlpha { get; } = new Animation(100.0, 0, 100);

    [Display(GroupName = "3Dノイズ", Name = "発光強さ")]
    [AnimationSlider("F2", "", 0, 5)]
    [ShowPropertyEditorWhen(nameof(ShapeKind), D3DPrimitiveShapeKind.Noise3D)]
    public Animation NoiseEmissionStrength { get; } = new Animation(0.0, 0, 20);

    [Display(GroupName = "ローカル調整", Name = "倍率")]
    [AnimationSlider("F1", "%", 0, 400)]
    public Animation ModelScale { get; } = new Animation(100.0, 0, 100000);

    [Display(GroupName = "ローカル調整", Name = "拡大率 X")]
    [AnimationSlider("F1", "%", 0, 400)]
    public Animation ScaleX { get; } = new Animation(100.0, 0, 100000);

    [Display(GroupName = "ローカル調整", Name = "拡大率 Y")]
    [AnimationSlider("F1", "%", 0, 400)]
    public Animation ScaleY { get; } = new Animation(100.0, 0, 100000);

    [Display(GroupName = "ローカル調整", Name = "拡大率 Z")]
    [AnimationSlider("F1", "%", 0, 400)]
    public Animation ScaleZ { get; } = new Animation(100.0, 0, 100000);

    [Display(GroupName = "ローカル調整", Name = "回転 X")]
    [AnimationSlider("F1", "°", -360, 360)]
    public Animation LocalRotX { get; } = new Animation(0.0, -100000, 100000);

    [Display(GroupName = "ローカル調整", Name = "回転 Y")]
    [AnimationSlider("F1", "°", -360, 360)]
    public Animation LocalRotY { get; } = new Animation(0.0, -100000, 100000);

    [Display(GroupName = "ローカル調整", Name = "回転 Z")]
    [AnimationSlider("F1", "°", -360, 360)]
    public Animation LocalRotZ { get; } = new Animation(0.0, -100000, 100000);

    [Display(GroupName = "マテリアル", Name = "ベースカラー")]
    [ColorPicker]
    public Color BaseColor
    {
        get => baseColor;
        set => Set(ref baseColor, value);
    }
    private Color baseColor = Colors.White;

    [Display(GroupName = "マテリアル", Name = "金属度 (Metallic)")]
    [AnimationSlider("F2", "", 0, 1)]
    public Animation Metallic { get; } = new Animation(0.0, 0, 1);

    [Display(GroupName = "マテリアル", Name = "粗さ (Roughness)")]
    [AnimationSlider("F2", "", 0.04, 1)]
    public Animation Roughness { get; } = new Animation(0.5, 0.04, 1);

    [Display(GroupName = "マテリアル", Name = "透過 (Transmission)")]
    [AnimationSlider("F2", "", 0, 1)]
    public Animation Transmission { get; } = new Animation(0.0, 0, 1);

    [Display(GroupName = "マテリアル", Name = "屈折率 (IOR)")]
    [AnimationSlider("F2", "", 1.0, 2.5)]
    public Animation Ior { get; } = new Animation(1.45, 1.0, 2.5);

    [Display(GroupName = "マテリアル", Name = "発光色")]
    [ColorPicker]
    public Color EmissiveColor
    {
        get => emissiveColor;
        set => Set(ref emissiveColor, value);
    }
    private Color emissiveColor = Colors.White;

    [Display(GroupName = "マテリアル", Name = "発光強度")]
    [AnimationSlider("F2", "", 0, 10)]
    public Animation EmissiveIntensity { get; } = new Animation(0.0, 0, 20);

    [Display(GroupName = "マテリアル", Name = "背面カリング", Description = "ONにすると裏向きのポリゴンを描画せず高速化します。")]
    [ToggleSlider]
    public bool CullBackface
    {
        get => cullBackface;
        set => Set(ref cullBackface, value);
    }
    private bool cullBackface = false;

    [Browsable(false)]
    public string EmbeddedMeshData
    {
        get => embeddedMeshData;
        set => Set(ref embeddedMeshData, value ?? string.Empty);
    }
    private string embeddedMeshData = string.Empty;

    [Browsable(false)]
    public string ModifierStackData
    {
        get => modifierStackData;
        set => Set(ref modifierStackData, value ?? string.Empty);
    }
    private string modifierStackData = string.Empty;

    [Browsable(false)]
    public bool NormalizeSize => ShapeKind != D3DPrimitiveShapeKind.BezierCurve;

    [Browsable(false)]
    public Animation UnitScale { get; } = new Animation(100.0, 0.1, 10000);

    internal float EffectiveUnitScale => ShapeKind == D3DPrimitiveShapeKind.BezierCurve ? 1f : 100f;

    [Browsable(false)]
    public string FilePath => string.Empty;

    public D3DPrimitiveShapeParameter(SharedDataStore? sharedData) : base(sharedData) { }
    public D3DPrimitiveShapeParameter() : this(null) { }

    internal static EditablePrimitiveType ToEditablePrimitive(D3DPrimitiveShapeKind kind) => kind switch
    {
        D3DPrimitiveShapeKind.UvSphere => EditablePrimitiveType.UvSphere,
        D3DPrimitiveShapeKind.IcoSphere => EditablePrimitiveType.IcoSphere,
        D3DPrimitiveShapeKind.Circle => EditablePrimitiveType.Circle,
        D3DPrimitiveShapeKind.Cylinder => EditablePrimitiveType.Cylinder,
        D3DPrimitiveShapeKind.Cone => EditablePrimitiveType.Cone,
        D3DPrimitiveShapeKind.Torus => EditablePrimitiveType.Torus,
        _ => EditablePrimitiveType.Cube,
    };

    internal EditableMaterial BuildMaterialAt(long frame, long length, int fps)
    {
        float emInt = MathF.Max(0f, (float)EmissiveIntensity.GetValue(frame, length, fps));
        return new EditableMaterial
        {
            Name = "Material",
            BaseColor = new Vector4(BaseColor.R / 255f, BaseColor.G / 255f, BaseColor.B / 255f, BaseColor.A / 255f),
            EmissiveColor = new Vector4(EmissiveColor.R / 255f, EmissiveColor.G / 255f, EmissiveColor.B / 255f, emInt),
            Metallic = Math.Clamp((float)Metallic.GetValue(frame, length, fps), 0f, 1f),
            Roughness = Math.Clamp((float)Roughness.GetValue(frame, length, fps), 0.04f, 1f),
            Transmission = Math.Clamp((float)Transmission.GetValue(frame, length, fps), 0f, 1f),
            Ior = Math.Clamp((float)Ior.GetValue(frame, length, fps), 1.0f, 2.5f),
            ForceOpaque = false,
            SourcePartIndex = -1,
        };
    }

    internal Noise3DParameters BuildNoise3DAt(long frame, long length, int fps, Vector3 boundsMin, Vector3 boundsMax) => new()
    {
        Enabled = ShapeKind == D3DPrimitiveShapeKind.Noise3D,
        NoiseType = NoiseType,
        RenderMode = NoiseRenderMode,
        Scale = new Vector3(
            MathF.Max(0.01f, (float)NoiseScaleX.GetValue(frame, length, fps)),
            MathF.Max(0.01f, (float)NoiseScaleY.GetValue(frame, length, fps)),
            MathF.Max(0.01f, (float)NoiseScaleZ.GetValue(frame, length, fps))),
        Complexity = Math.Clamp((float)NoiseComplexity.GetValue(frame, length, fps), 1f, 8f),
        Threshold = Math.Clamp((float)NoiseThreshold.GetValue(frame, length, fps), 0f, 1f),
        Softness = Math.Clamp((float)NoiseSoftness.GetValue(frame, length, fps), 0.002f, 1f),
        Density = Math.Clamp((float)NoiseDensity.GetValue(frame, length, fps), 0.01f, 30f),
        BumpStrength = (float)NoiseBumpStrength.GetValue(frame, length, fps),
        StepCount = Math.Clamp((int)Math.Round(NoiseStepCount.GetValue(frame, length, fps)), 16, 128),
        Offset = new Vector3(
            (float)NoiseOffsetX.GetValue(frame, length, fps),
            (float)NoiseOffsetY.GetValue(frame, length, fps),
            (float)NoiseOffsetZ.GetValue(frame, length, fps)),
        Evolution = (float)NoiseEvolution.GetValue(frame, length, fps),
        Seed = (float)NoiseSeed.GetValue(frame, length, fps),
        Invert = NoiseInvert,
        ColorA = new Vector4(
            NoiseColorA.R / 255f,
            NoiseColorA.G / 255f,
            NoiseColorA.B / 255f,
            (NoiseColorA.A / 255f) * Math.Clamp((float)NoiseColorAAlpha.GetValue(frame, length, fps) / 100f, 0f, 1f)),
        ColorB = new Vector4(
            NoiseColorB.R / 255f,
            NoiseColorB.G / 255f,
            NoiseColorB.B / 255f,
            (NoiseColorB.A / 255f) * Math.Clamp((float)NoiseColorBAlpha.GetValue(frame, length, fps) / 100f, 0f, 1f)),
        EmissionStrength = MathF.Max(0f, (float)NoiseEmissionStrength.GetValue(frame, length, fps)),
        BoundsMin = boundsMin,
        BoundsMax = boundsMax,
    };

    public EditableMesh CreateDefaultEditableMesh()
    {
        var mat = BuildMaterialAt(0, 1, 60);
        if (ShapeKind == D3DPrimitiveShapeKind.BezierCurve)
        {
            BezierCurve3DData.TryDeserialize(BezierCurveData, out var curveData);
            int sides = Math.Clamp((int)Math.Round(BezierSides.GetValue(0, 1, 60)), 3, 64);
            int res = Math.Clamp((int)Math.Round(BezierResolution.GetValue(0, 1, 60)), 2, 64);
            return BezierCurve3DMeshBuilder.BuildMesh(
                curveData,
                BezierInterpolation,
                BezierLineStyle,
                sides,
                res,
                BezierClosed,
                BezierStartCap,
                BezierEndCap,
                SmoothShading,
                mat);
        }

        int segs = Math.Clamp((int)Math.Round(Segments.GetValue(0, 1, 60)), 3, 64);
        int rings = Math.Clamp((int)Math.Round(Rings.GetValue(0, 1, 60)), 3, 48);
        int sub = Math.Clamp((int)Math.Round(Subdivisions.GetValue(0, 1, 60)), 0, 4);
        float tube = Math.Clamp((float)TubeRadius.GetValue(0, 1, 60), 0.05f, 0.90f);

        return EditableMesh.CreatePrimitive(
            ToEditablePrimitive(ShapeKind),
            size: 1f,
            segments: segs,
            rings: rings,
            icoSubdivisions: sub,
            tubeRadiusRatio: tube,
            smoothShading: SmoothShading,
            material: mat);
    }

    public override IEnumerable<string> CreateMaskExoFilter(
        int keyFrameIndex, ExoOutputDescription desc, ShapeMaskExoOutputDescription shapeMaskDesc)
        => Array.Empty<string>();

    public override IEnumerable<string> CreateShapeItemExoFilter(
        int keyFrameIndex, ExoOutputDescription desc)
        => Array.Empty<string>();

    public override IShapeSource CreateShapeSource(IGraphicsDevicesAndContext devices)
        => new D3DPrimitiveShapeSource(devices, this);

    protected override IEnumerable<IAnimatable> GetAnimatables()
    {
        yield return Segments;
        yield return Rings;
        yield return Subdivisions;
        yield return TubeRadius;
        yield return ModelScale;
        yield return ScaleX;
        yield return ScaleY;
        yield return ScaleZ;
        yield return LocalRotX;
        yield return LocalRotY;
        yield return LocalRotZ;
        yield return Metallic;
        yield return Roughness;
        yield return Transmission;
        yield return Ior;
        yield return EmissiveIntensity;
        yield return BezierSides;
        yield return BezierResolution;
        yield return NoiseScaleX;
        yield return NoiseScaleY;
        yield return NoiseScaleZ;
        yield return NoiseComplexity;
        yield return NoiseThreshold;
        yield return NoiseSoftness;
        yield return NoiseDensity;
        yield return NoiseBumpStrength;
        yield return NoiseStepCount;
        yield return NoiseOffsetX;
        yield return NoiseOffsetY;
        yield return NoiseOffsetZ;
        yield return NoiseEvolution;
        yield return NoiseSeed;
        yield return NoiseColorAAlpha;
        yield return NoiseColorBAlpha;
        yield return NoiseEmissionStrength;
    }

    protected override void LoadSharedData(SharedDataStore store)
    {
        var data = store.Load<SharedData>();
        if (data is null) return;
        data.CopyTo(this);
    }

    protected override void SaveSharedData(SharedDataStore store)
        => store.Save(new SharedData(this));

    private class SharedData
    {
        public D3DPrimitiveShapeKind ShapeKind { get; set; } = D3DPrimitiveShapeKind.Cube;
        public Animation Segments { get; } = new Animation(24, 3, 64);
        public Animation Rings { get; } = new Animation(16, 3, 48);
        public Animation Subdivisions { get; } = new Animation(2, 0, 4);
        public Animation TubeRadius { get; } = new Animation(0.35, 0.05, 0.90);
        public bool SmoothShading { get; set; } = true;
        public bool GroundToOrigin { get; set; } = false;
        public Animation ModelScale { get; } = new Animation(100.0, 0, 100000);
        public Animation ScaleX { get; } = new Animation(100.0, 0, 100000);
        public Animation ScaleY { get; } = new Animation(100.0, 0, 100000);
        public Animation ScaleZ { get; } = new Animation(100.0, 0, 100000);
        public Animation LocalRotX { get; } = new Animation(0.0, -100000, 100000);
        public Animation LocalRotY { get; } = new Animation(0.0, -100000, 100000);
        public Animation LocalRotZ { get; } = new Animation(0.0, -100000, 100000);
        public Color BaseColor { get; set; } = Colors.White;
        public Animation Metallic { get; } = new Animation(0.0, 0, 1);
        public Animation Roughness { get; } = new Animation(0.5, 0.04, 1);
        public Animation Transmission { get; } = new Animation(0.0, 0, 1);
        public Animation Ior { get; } = new Animation(1.45, 1.0, 2.5);
        public Color EmissiveColor { get; set; } = Colors.White;
        public Animation EmissiveIntensity { get; } = new Animation(0.0, 0, 20);
        public bool CullBackface { get; set; } = false;
        public string EmbeddedMeshData { get; set; } = string.Empty;
        public string ModifierStackData { get; set; } = string.Empty;

        public BezierCurveInterpolation BezierInterpolation { get; set; } = BezierCurveInterpolation.CubicBezier;
        public BezierCurveLineStyle BezierLineStyle { get; set; } = BezierCurveLineStyle.Tube3D;
        public Animation BezierSides { get; } = new Animation(12, 3, 64);
        public Animation BezierResolution { get; } = new Animation(16, 2, 64);
        public bool BezierClosed { get; set; }
        public BezierCurveCapStyle BezierStartCap { get; set; } = BezierCurveCapStyle.Flat;
        public BezierCurveCapStyle BezierEndCap { get; set; } = BezierCurveCapStyle.Flat;
        public string BezierCurveData { get; set; } = string.Empty;

        public D3DNoise3DType NoiseType { get; set; } = D3DNoise3DType.Fractal;
        public D3DNoise3DRenderMode NoiseRenderMode { get; set; } = D3DNoise3DRenderMode.Volume3D;
        public Animation NoiseScaleX { get; } = new Animation(3.0, 0.05, 100);
        public Animation NoiseScaleY { get; } = new Animation(3.0, 0.05, 100);
        public Animation NoiseScaleZ { get; } = new Animation(3.0, 0.05, 100);
        public Animation NoiseComplexity { get; } = new Animation(4.0, 1, 8);
        public Animation NoiseThreshold { get; } = new Animation(0.42, 0, 1);
        public Animation NoiseSoftness { get; } = new Animation(0.25, 0.005, 1);
        public Animation NoiseDensity { get; } = new Animation(2.0, 0.05, 20);
        public Animation NoiseBumpStrength { get; } = new Animation(0.45, -5, 5);
        public Animation NoiseStepCount { get; } = new Animation(48, 16, 128);
        public Animation NoiseOffsetX { get; } = new Animation(0.0, -1000, 1000);
        public Animation NoiseOffsetY { get; } = new Animation(0.0, -1000, 1000);
        public Animation NoiseOffsetZ { get; } = new Animation(0.0, -1000, 1000);
        public Animation NoiseEvolution { get; } = new Animation(0.0, -1000, 1000);
        public Animation NoiseSeed { get; } = new Animation(1.0, 0, 100000);
        public bool NoiseInvert { get; set; }
        public Color NoiseColorA { get; set; } = Color.FromRgb(200, 200, 200);
        public Animation NoiseColorAAlpha { get; } = new Animation(100.0, 0, 100);
        public Color NoiseColorB { get; set; } = Color.FromRgb(255, 255, 255);
        public Animation NoiseColorBAlpha { get; } = new Animation(100.0, 0, 100);
        public Animation NoiseEmissionStrength { get; } = new Animation(0.0, 0, 20);

        public SharedData() { }

        public SharedData(D3DPrimitiveShapeParameter p)
        {
            ShapeKind = p.ShapeKind;
            Segments.CopyFrom(p.Segments);
            Rings.CopyFrom(p.Rings);
            Subdivisions.CopyFrom(p.Subdivisions);
            TubeRadius.CopyFrom(p.TubeRadius);
            SmoothShading = p.SmoothShading;
            GroundToOrigin = p.GroundToOrigin;
            ModelScale.CopyFrom(p.ModelScale);
            ScaleX.CopyFrom(p.ScaleX);
            ScaleY.CopyFrom(p.ScaleY);
            ScaleZ.CopyFrom(p.ScaleZ);
            LocalRotX.CopyFrom(p.LocalRotX);
            LocalRotY.CopyFrom(p.LocalRotY);
            LocalRotZ.CopyFrom(p.LocalRotZ);
            BaseColor = p.BaseColor;
            Metallic.CopyFrom(p.Metallic);
            Roughness.CopyFrom(p.Roughness);
            Transmission.CopyFrom(p.Transmission);
            Ior.CopyFrom(p.Ior);
            EmissiveColor = p.EmissiveColor;
            EmissiveIntensity.CopyFrom(p.EmissiveIntensity);
            CullBackface = p.CullBackface;
            EmbeddedMeshData = p.EmbeddedMeshData;
            ModifierStackData = p.ModifierStackData;

            BezierInterpolation = p.BezierInterpolation;
            BezierLineStyle = p.BezierLineStyle;
            BezierSides.CopyFrom(p.BezierSides);
            BezierResolution.CopyFrom(p.BezierResolution);
            BezierClosed = p.BezierClosed;
            BezierStartCap = p.BezierStartCap;
            BezierEndCap = p.BezierEndCap;
            BezierCurveData = p.BezierCurveData;

            NoiseType = p.NoiseType;
            NoiseRenderMode = p.NoiseRenderMode;
            NoiseScaleX.CopyFrom(p.NoiseScaleX);
            NoiseScaleY.CopyFrom(p.NoiseScaleY);
            NoiseScaleZ.CopyFrom(p.NoiseScaleZ);
            NoiseComplexity.CopyFrom(p.NoiseComplexity);
            NoiseThreshold.CopyFrom(p.NoiseThreshold);
            NoiseSoftness.CopyFrom(p.NoiseSoftness);
            NoiseDensity.CopyFrom(p.NoiseDensity);
            NoiseBumpStrength.CopyFrom(p.NoiseBumpStrength);
            NoiseStepCount.CopyFrom(p.NoiseStepCount);
            NoiseOffsetX.CopyFrom(p.NoiseOffsetX);
            NoiseOffsetY.CopyFrom(p.NoiseOffsetY);
            NoiseOffsetZ.CopyFrom(p.NoiseOffsetZ);
            NoiseEvolution.CopyFrom(p.NoiseEvolution);
            NoiseSeed.CopyFrom(p.NoiseSeed);
            NoiseInvert = p.NoiseInvert;
            NoiseColorA = p.NoiseColorA;
            NoiseColorAAlpha.CopyFrom(p.NoiseColorAAlpha);
            NoiseColorB = p.NoiseColorB;
            NoiseColorBAlpha.CopyFrom(p.NoiseColorBAlpha);
            NoiseEmissionStrength.CopyFrom(p.NoiseEmissionStrength);
        }

        public void CopyTo(D3DPrimitiveShapeParameter p)
        {
            // ShapeKind セッターが EmbeddedMeshData をクリアするため先に ShapeKind を設定する
            p.ShapeKind = ShapeKind;
            p.Segments.CopyFrom(Segments);
            p.Rings.CopyFrom(Rings);
            p.Subdivisions.CopyFrom(Subdivisions);
            p.TubeRadius.CopyFrom(TubeRadius);
            p.SmoothShading = SmoothShading;
            p.GroundToOrigin = GroundToOrigin;
            p.ModelScale.CopyFrom(ModelScale);
            p.ScaleX.CopyFrom(ScaleX);
            p.ScaleY.CopyFrom(ScaleY);
            p.ScaleZ.CopyFrom(ScaleZ);
            p.LocalRotX.CopyFrom(LocalRotX);
            p.LocalRotY.CopyFrom(LocalRotY);
            p.LocalRotZ.CopyFrom(LocalRotZ);
            p.BaseColor = BaseColor;
            p.Metallic.CopyFrom(Metallic);
            p.Roughness.CopyFrom(Roughness);
            p.Transmission.CopyFrom(Transmission);
            p.Ior.CopyFrom(Ior);
            p.EmissiveColor = EmissiveColor;
            p.EmissiveIntensity.CopyFrom(EmissiveIntensity);
            p.CullBackface = CullBackface;
            p.EmbeddedMeshData = EmbeddedMeshData;
            p.ModifierStackData = ModifierStackData;

            p.BezierInterpolation = BezierInterpolation;
            p.BezierLineStyle = BezierLineStyle;
            p.BezierSides.CopyFrom(BezierSides);
            p.BezierResolution.CopyFrom(BezierResolution);
            p.BezierClosed = BezierClosed;
            p.BezierStartCap = BezierStartCap;
            p.BezierEndCap = BezierEndCap;
            if (!string.IsNullOrWhiteSpace(BezierCurveData))
                p.BezierCurveData = BezierCurveData;

            p.NoiseType = NoiseType;
            p.NoiseRenderMode = NoiseRenderMode;
            p.NoiseScaleX.CopyFrom(NoiseScaleX);
            p.NoiseScaleY.CopyFrom(NoiseScaleY);
            p.NoiseScaleZ.CopyFrom(NoiseScaleZ);
            p.NoiseComplexity.CopyFrom(NoiseComplexity);
            p.NoiseThreshold.CopyFrom(NoiseThreshold);
            p.NoiseSoftness.CopyFrom(NoiseSoftness);
            p.NoiseDensity.CopyFrom(NoiseDensity);
            p.NoiseBumpStrength.CopyFrom(NoiseBumpStrength);
            p.NoiseStepCount.CopyFrom(NoiseStepCount);
            p.NoiseOffsetX.CopyFrom(NoiseOffsetX);
            p.NoiseOffsetY.CopyFrom(NoiseOffsetY);
            p.NoiseOffsetZ.CopyFrom(NoiseOffsetZ);
            p.NoiseEvolution.CopyFrom(NoiseEvolution);
            p.NoiseSeed.CopyFrom(NoiseSeed);
            p.NoiseInvert = NoiseInvert;
            p.NoiseColorA = NoiseColorA;
            p.NoiseColorAAlpha.CopyFrom(NoiseColorAAlpha);
            p.NoiseColorB = NoiseColorB;
            p.NoiseColorBAlpha.CopyFrom(NoiseColorBAlpha);
            p.NoiseEmissionStrength.CopyFrom(NoiseEmissionStrength);
        }
    }
}
