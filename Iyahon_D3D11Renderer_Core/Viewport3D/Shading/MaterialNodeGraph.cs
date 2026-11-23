using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Shading;

public enum ShaderSocketKind
{
    Float,
    Color,
    Vector,
    Shader
}

public enum ShaderNodeType
{
    MaterialOutput = 0,
    PrincipledBsdf = 1,
    Emission = 2,
    MixShader = 3,
    DiffuseBsdf = 4,
    ShaderToRgb = 5,

    TexCoord = 10,
    Value = 11,
    RgbColor = 12,
    TimeInfo = 13,
    LayerWeight = 14,
    Fresnel = 15,
    AmbientOcclusion = 16,
    Wireframe = 17,

    ImageTexture = 20,
    NoiseTexture = 21,
    VoronoiTexture = 22,
    CheckerTexture = 23,
    WaveTexture = 24,
    GradientTexture = 25,
    BrickTexture = 26,
    LayerTexture = 27,

    MixColor = 30,
    ColorRamp = 31,
    HueSaturation = 32,
    InvertColor = 33,
    BrightContrast = 34,

    Mapping = 40,
    Bump = 41,
    NormalMap = 42,

    Math = 50,
    MapRange = 51,
    SeparateColor = 52,
    CombineColor = 53,
    RgbToBw = 54,
    VectorMath = 55,
    SeparateXyz = 56,
    CombineXyz = 57,
    MixValue = 58
}

public sealed class ShaderSocketDef
{
    public string Name { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public ShaderSocketKind Kind { get; init; }
    public bool IsOutput { get; init; }
    public float DefaultFloat { get; init; }
    public float Min { get; init; }
    public float Max { get; init; } = 1f;
    public float[] DefaultColor { get; init; } = { 1f, 1f, 1f, 1f };
    public string? ParamKey { get; init; }
    public string? Group { get; init; }
}

public sealed class ShaderNodeSpec
{
    public ShaderNodeType Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string HeaderColorHex { get; init; } = "#3A3F4B";
    public float DefaultWidth { get; init; } = 190f;
    public IReadOnlyList<ShaderSocketDef> Inputs { get; init; } = Array.Empty<ShaderSocketDef>();
    public IReadOnlyList<ShaderSocketDef> Outputs { get; init; } = Array.Empty<ShaderSocketDef>();
}

public static class ShaderNodeRegistry
{
    private const string HdrOutput = "#742834";
    private const string HdrShader = "#2D6F47";
    private const string HdrTexture = "#9B5224";
    private const string HdrVector = "#4B4794";
    private const string HdrColor = "#857427";
    private const string HdrInput = "#7D3048";
    private const string HdrConverter = "#356478";

    private static ShaderSocketDef InF(string name, string label, float def, float min = 0f, float max = 1f, string? group = null)
        => new() { Name = name, Label = label, Kind = ShaderSocketKind.Float, IsOutput = false, DefaultFloat = def, Min = min, Max = max, ParamKey = name, Group = group };

    private static ShaderSocketDef InC(string name, string label, float[] def, string? group = null)
        => new() { Name = name, Label = label, Kind = ShaderSocketKind.Color, IsOutput = false, DefaultColor = def, ParamKey = name, Group = group };

    private static ShaderSocketDef InV(string name, string label, string? group = null)
        => new() { Name = name, Label = label, Kind = ShaderSocketKind.Vector, IsOutput = false, Group = group };

    private static ShaderSocketDef InS(string name, string label)
        => new() { Name = name, Label = label, Kind = ShaderSocketKind.Shader, IsOutput = false };

    private static ShaderSocketDef Out(string name, string label, ShaderSocketKind kind)
        => new() { Name = name, Label = label, Kind = kind, IsOutput = true };

    private static readonly float[] White = { 1f, 1f, 1f, 1f };
    private static readonly float[] Gray80 = { 0.8f, 0.8f, 0.8f, 1f };
    private static readonly float[] Gray50 = { 0.5f, 0.5f, 0.5f, 1f };
    private static readonly float[] Dark18 = { 0.18f, 0.18f, 0.18f, 1f };
    private static readonly float[] FlatNorm = { 0.5f, 0.5f, 1f, 1f };
    private static readonly float[] Brick1 = { 0.72f, 0.28f, 0.18f, 1f };
    private static readonly float[] Brick2 = { 0.52f, 0.18f, 0.12f, 1f };
    private static readonly float[] Mortar = { 0.14f, 0.14f, 0.14f, 1f };
    private static readonly float[] SssSkin = { 1f, 0.35f, 0.25f, 1f };

    private static readonly Dictionary<ShaderNodeType, ShaderNodeSpec> s_specs = new()
    {
        [ShaderNodeType.MaterialOutput] = new()
        {
            Type = ShaderNodeType.MaterialOutput, Title = "マテリアル出力", Category = "出力",
            HeaderColorHex = HdrOutput, DefaultWidth = 176f,
            Inputs = new[] { InS("Surface", "サーフェス") }
        },
        [ShaderNodeType.PrincipledBsdf] = new()
        {
            Type = ShaderNodeType.PrincipledBsdf, Title = "プリンシプルBSDF", Category = "シェーダー",
            HeaderColorHex = HdrShader, DefaultWidth = 236f,
            Outputs = new[] { Out("BSDF", "BSDF", ShaderSocketKind.Shader) },
            Inputs = new[]
            {
                InC("BaseColor", "ベースカラー", (float[])Gray80.Clone()),
                InF("Metallic", "メタリック", 0f, 0f, 1f),
                InF("Roughness", "粗さ", 0.5f, 0.04f, 1f),
                InF("Ior", "IOR", 1.5f, 1f, 2.5f),
                InF("Alpha", "アルファ", 1f, 0f, 1f),
                InV("Normal", "ノーマル"),
                InF("DiffuseRoughness", "ディフューズの粗さ", 0f, 0f, 1f),

                InF("SubsurfaceWeight", "ウェイト", 0f, 0f, 1f, "サブサーフェス"),
                InF("SubsurfaceScale", "スケール", 0.25f, 0f, 2f, "サブサーフェス"),
                InC("SubsurfaceColor", "サブサーフェスカラー", (float[])SssSkin.Clone(), "サブサーフェス"),

                InF("Anisotropic", "異方性", 0f, 0f, 1f, "スペキュラー / 異方性"),
                InF("AnisotropicRotation", "異方性の回転", 0f, 0f, 1f, "スペキュラー / 異方性"),

                InF("Transmission", "伝播 (ウェイト)", 0f, 0f, 1f, "伝播"),

                InF("CoatWeight", "コート ウェイト", 0f, 0f, 1f, "コート"),
                InF("CoatRoughness", "コート 粗さ", 0.05f, 0.03f, 1f, "コート"),
                InF("CoatIor", "コート IOR", 1.5f, 1f, 2.5f, "コート"),

                InF("SheenWeight", "シーン ウェイト", 0f, 0f, 1f, "シーン"),
                InF("SheenRoughness", "シーン 粗さ", 0.5f, 0.05f, 1f, "シーン"),
                InC("SheenTint", "シーン チント", (float[])White.Clone(), "シーン"),

                InF("ThinFilmThickness", "薄膜の厚さ (nm)", 0f, 0f, 1200f, "薄膜干渉"),
                InF("ThinFilmIor", "薄膜の IOR", 1.33f, 1f, 2.5f, "薄膜干渉"),

                InC("EmissionColor", "放射カラー", (float[])White.Clone(), "放射"),
                InF("EmissionStrength", "放射の強さ", 0f, 0f, 20f, "放射"),
            }
        },
        [ShaderNodeType.DiffuseBsdf] = new()
        {
            Type = ShaderNodeType.DiffuseBsdf, Title = "ディフューズ BSDF", Category = "シェーダー",
            HeaderColorHex = HdrShader, DefaultWidth = 192f,
            Outputs = new[] { Out("BSDF", "BSDF", ShaderSocketKind.Shader) },
            Inputs = new[]
            {
                InC("Color", "カラー", (float[])Gray80.Clone()),
                InF("Roughness", "粗さ", 0f, 0f, 1f),
                InV("Normal", "ノーマル"),
            }
        },
        [ShaderNodeType.Emission] = new()
        {
            Type = ShaderNodeType.Emission, Title = "放射 (Emission)", Category = "シェーダー",
            HeaderColorHex = HdrShader, DefaultWidth = 188f,
            Outputs = new[] { Out("Emission", "放射", ShaderSocketKind.Shader) },
            Inputs = new[]
            {
                InC("Color", "カラー", (float[])White.Clone()),
                InF("Strength", "強さ", 1f, 0f, 20f),
            }
        },
        [ShaderNodeType.MixShader] = new()
        {
            Type = ShaderNodeType.MixShader, Title = "シェーダーミックス", Category = "シェーダー",
            HeaderColorHex = HdrShader, DefaultWidth = 186f,
            Outputs = new[] { Out("Shader", "シェーダー", ShaderSocketKind.Shader) },
            Inputs = new[]
            {
                InF("Fac", "係数", 0.5f, 0f, 1f),
                InS("Shader1", "シェーダー1"),
                InS("Shader2", "シェーダー2"),
            }
        },
        [ShaderNodeType.ShaderToRgb] = new()
        {
            Type = ShaderNodeType.ShaderToRgb, Title = "シェーダーの RGB 化", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 188f,
            Outputs = new[]
            {
                Out("Color", "カラー", ShaderSocketKind.Color),
                Out("Alpha", "アルファ", ShaderSocketKind.Float),
            },
            Inputs = new[] { InS("Shader", "シェーダー") }
        },
        [ShaderNodeType.TexCoord] = new()
        {
            Type = ShaderNodeType.TexCoord, Title = "テクスチャ座標", Category = "入力",
            HeaderColorHex = HdrInput, DefaultWidth = 166f,
            Outputs = new[]
            {
                Out("UV", "UV", ShaderSocketKind.Vector),
                Out("Generated", "生成", ShaderSocketKind.Vector),
                Out("Object", "オブジェクト", ShaderSocketKind.Vector),
            }
        },
        [ShaderNodeType.TimeInfo] = new()
        {
            Type = ShaderNodeType.TimeInfo, Title = "時間 / フレーム (Time)", Category = "入力",
            HeaderColorHex = HdrInput, DefaultWidth = 196f,
            Outputs = new[]
            {
                Out("Time", "時間 (秒)", ShaderSocketKind.Float),
                Out("Frame", "フレーム", ShaderSocketKind.Float),
                Out("Progress", "進行度 (0..1)", ShaderSocketKind.Float),
            },
            Inputs = new[]
            {
                InF("Speed", "速度", 1f, -10f, 10f),
                InF("Offset", "オフセット", 0f, -1000f, 1000f),
            }
        },
        [ShaderNodeType.LayerWeight] = new()
        {
            Type = ShaderNodeType.LayerWeight, Title = "レイヤーウェイト", Category = "入力",
            HeaderColorHex = HdrInput, DefaultWidth = 186f,
            Outputs = new[]
            {
                Out("Fresnel", "フレネル", ShaderSocketKind.Float),
                Out("Facing", "フェイシング", ShaderSocketKind.Float),
            },
            Inputs = new[]
            {
                InF("Blend", "ブレンド", 0.5f, 0f, 1f),
                InV("Normal", "ノーマル"),
            }
        },
        [ShaderNodeType.Fresnel] = new()
        {
            Type = ShaderNodeType.Fresnel, Title = "フレネル (Fresnel)", Category = "入力",
            HeaderColorHex = HdrInput, DefaultWidth = 182f,
            Outputs = new[] { Out("Fac", "係数", ShaderSocketKind.Float) },
            Inputs = new[]
            {
                InF("Ior", "IOR", 1.45f, 1f, 3f),
                InV("Normal", "ノーマル"),
            }
        },
        [ShaderNodeType.AmbientOcclusion] = new()
        {
            Type = ShaderNodeType.AmbientOcclusion, Title = "アンビエントオクルージョン", Category = "入力",
            HeaderColorHex = HdrInput, DefaultWidth = 204f,
            Outputs = new[]
            {
                Out("Color", "カラー", ShaderSocketKind.Color),
                Out("AO", "AO", ShaderSocketKind.Float),
            },
            Inputs = new[]
            {
                InC("Color", "カラー", (float[])White.Clone()),
                InF("Distance", "距離", 1f, 0.01f, 10f),
                InV("Normal", "ノーマル"),
            }
        },
        [ShaderNodeType.Wireframe] = new()
        {
            Type = ShaderNodeType.Wireframe, Title = "ワイヤーフレーム", Category = "入力",
            HeaderColorHex = HdrInput, DefaultWidth = 186f,
            Outputs = new[] { Out("Fac", "係数", ShaderSocketKind.Float) },
            Inputs = new[] { InF("Size", "サイズ", 1.5f, 0.01f, 10f) }
        },
        [ShaderNodeType.LayerTexture] = new()
        {
            Type = ShaderNodeType.LayerTexture, Title = "YMM4 レイヤーテクスチャ", Category = "テクスチャ",
            HeaderColorHex = HdrTexture, DefaultWidth = 208f,
            Outputs = new[]
            {
                Out("Color", "カラー", ShaderSocketKind.Color),
                Out("Alpha", "アルファ", ShaderSocketKind.Float),
            },
            Inputs = new[] { InV("Vector", "ベクトル") }
        },
        [ShaderNodeType.Value] = new()
        {
            Type = ShaderNodeType.Value, Title = "値 (Value)", Category = "入力",
            HeaderColorHex = HdrInput, DefaultWidth = 160f,
            Outputs = new[] { Out("Value", "値", ShaderSocketKind.Float) }
        },
        [ShaderNodeType.RgbColor] = new()
        {
            Type = ShaderNodeType.RgbColor, Title = "RGBカラー", Category = "入力",
            HeaderColorHex = HdrInput, DefaultWidth = 166f,
            Outputs = new[] { Out("Color", "カラー", ShaderSocketKind.Color) }
        },
        [ShaderNodeType.ImageTexture] = new()
        {
            Type = ShaderNodeType.ImageTexture, Title = "画像テクスチャ", Category = "テクスチャ",
            HeaderColorHex = HdrTexture, DefaultWidth = 216f,
            Outputs = new[]
            {
                Out("Color", "カラー", ShaderSocketKind.Color),
                Out("Alpha", "アルファ", ShaderSocketKind.Float),
            },
            Inputs = new[] { InV("Vector", "ベクトル") }
        },
        [ShaderNodeType.NoiseTexture] = new()
        {
            Type = ShaderNodeType.NoiseTexture, Title = "ノイズテクスチャ", Category = "テクスチャ",
            HeaderColorHex = HdrTexture, DefaultWidth = 206f,
            Outputs = new[]
            {
                Out("Fac", "係数", ShaderSocketKind.Float),
                Out("Color", "カラー", ShaderSocketKind.Color),
            },
            Inputs = new[]
            {
                InV("Vector", "ベクトル"),
                InF("Scale", "スケール", 5f, 0.05f, 100f),
                InF("Detail", "細かさ", 4f, 0f, 12f),
                InF("Roughness", "粗さ", 0.55f, 0f, 1f),
                InF("Lacunarity", "空隙性", 2f, 1f, 6f),
                InF("Distortion", "歪み", 0f, 0f, 10f),
            }
        },
        [ShaderNodeType.VoronoiTexture] = new()
        {
            Type = ShaderNodeType.VoronoiTexture, Title = "ボロノイテクスチャ", Category = "テクスチャ",
            HeaderColorHex = HdrTexture, DefaultWidth = 200f,
            Outputs = new[]
            {
                Out("Distance", "距離", ShaderSocketKind.Float),
                Out("Color", "カラー", ShaderSocketKind.Color),
            },
            Inputs = new[]
            {
                InV("Vector", "ベクトル"),
                InF("Scale", "スケール", 8f, 0.1f, 100f),
                InF("Smoothness", "滑らかさ", 0.5f, 0f, 1f),
                InF("Randomness", "ランダム", 1f, 0f, 1f),
            }
        },
        [ShaderNodeType.CheckerTexture] = new()
        {
            Type = ShaderNodeType.CheckerTexture, Title = "チェッカーテクスチャ", Category = "テクスチャ",
            HeaderColorHex = HdrTexture, DefaultWidth = 196f,
            Outputs = new[]
            {
                Out("Color", "カラー", ShaderSocketKind.Color),
                Out("Fac", "係数", ShaderSocketKind.Float),
            },
            Inputs = new[]
            {
                InV("Vector", "ベクトル"),
                InC("Color1", "カラー1", (float[])Gray80.Clone()),
                InC("Color2", "カラー2", (float[])Dark18.Clone()),
                InF("Scale", "スケール", 8f, 0.1f, 64f),
            }
        },
        [ShaderNodeType.WaveTexture] = new()
        {
            Type = ShaderNodeType.WaveTexture, Title = "波テクスチャ", Category = "テクスチャ",
            HeaderColorHex = HdrTexture, DefaultWidth = 196f,
            Outputs = new[]
            {
                Out("Color", "カラー", ShaderSocketKind.Color),
                Out("Fac", "係数", ShaderSocketKind.Float),
            },
            Inputs = new[]
            {
                InV("Vector", "ベクトル"),
                InF("Scale", "スケール", 6f, 0.1f, 64f),
                InF("Distortion", "歪み", 2f, 0f, 20f),
                InF("Detail", "細かさ", 2f, 0f, 8f),
            }
        },
        [ShaderNodeType.GradientTexture] = new()
        {
            Type = ShaderNodeType.GradientTexture, Title = "グラデーションテクスチャ", Category = "テクスチャ",
            HeaderColorHex = HdrTexture, DefaultWidth = 194f,
            Outputs = new[]
            {
                Out("Color", "カラー", ShaderSocketKind.Color),
                Out("Fac", "係数", ShaderSocketKind.Float),
            },
            Inputs = new[] { InV("Vector", "ベクトル") }
        },
        [ShaderNodeType.BrickTexture] = new()
        {
            Type = ShaderNodeType.BrickTexture, Title = "レンガテクスチャ", Category = "テクスチャ",
            HeaderColorHex = HdrTexture, DefaultWidth = 206f,
            Outputs = new[]
            {
                Out("Color", "カラー", ShaderSocketKind.Color),
                Out("Fac", "係数", ShaderSocketKind.Float),
            },
            Inputs = new[]
            {
                InV("Vector", "ベクトル"),
                InC("Color1", "カラー1", (float[])Brick1.Clone()),
                InC("Color2", "カラー2", (float[])Brick2.Clone()),
                InC("Mortar", "目地カラー", (float[])Mortar.Clone()),
                InF("Scale", "スケール", 6f, 0.1f, 50f),
                InF("MortarSize", "目地サイズ", 0.025f, 0f, 0.2f),
                InF("BrickWidth", "幅", 0.5f, 0.1f, 2f),
                InF("RowHeight", "高さ", 0.25f, 0.1f, 2f),
            }
        },
        [ShaderNodeType.MixColor] = new()
        {
            Type = ShaderNodeType.MixColor, Title = "カラーミックス", Category = "カラー",
            HeaderColorHex = HdrColor, DefaultWidth = 192f,
            Outputs = new[] { Out("Result", "結果", ShaderSocketKind.Color) },
            Inputs = new[]
            {
                InF("Fac", "係数", 0.5f, 0f, 1f),
                InC("A", "A", (float[])Gray50.Clone()),
                InC("B", "B", (float[])Gray80.Clone()),
            }
        },
        [ShaderNodeType.ColorRamp] = new()
        {
            Type = ShaderNodeType.ColorRamp, Title = "カラーランプ", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 230f,
            Outputs = new[]
            {
                Out("Color", "カラー", ShaderSocketKind.Color),
                Out("Alpha", "アルファ", ShaderSocketKind.Float),
            },
            Inputs = new[] { InF("Fac", "係数", 0.5f, 0f, 1f) }
        },
        [ShaderNodeType.HueSaturation] = new()
        {
            Type = ShaderNodeType.HueSaturation, Title = "色相・彩度・明度", Category = "カラー",
            HeaderColorHex = HdrColor, DefaultWidth = 196f,
            Outputs = new[] { Out("Color", "カラー", ShaderSocketKind.Color) },
            Inputs = new[]
            {
                InF("Hue", "色相", 0.5f, 0f, 1f),
                InF("Saturation", "彩度", 1f, 0f, 2f),
                InF("Value", "明度", 1f, 0f, 2f),
                InF("Fac", "係数", 1f, 0f, 1f),
                InC("Color", "カラー", (float[])White.Clone()),
            }
        },
        [ShaderNodeType.InvertColor] = new()
        {
            Type = ShaderNodeType.InvertColor, Title = "カラー反転", Category = "カラー",
            HeaderColorHex = HdrColor, DefaultWidth = 180f,
            Outputs = new[] { Out("Color", "カラー", ShaderSocketKind.Color) },
            Inputs = new[]
            {
                InF("Fac", "係数", 1f, 0f, 1f),
                InC("Color", "カラー", (float[])White.Clone()),
            }
        },
        [ShaderNodeType.BrightContrast] = new()
        {
            Type = ShaderNodeType.BrightContrast, Title = "明るさ・コントラスト", Category = "カラー",
            HeaderColorHex = HdrColor, DefaultWidth = 194f,
            Outputs = new[] { Out("Color", "カラー", ShaderSocketKind.Color) },
            Inputs = new[]
            {
                InC("Color", "カラー", (float[])White.Clone()),
                InF("Bright", "明るさ", 0f, -1f, 1f),
                InF("Contrast", "コントラスト", 0f, -1f, 1f),
            }
        },
        [ShaderNodeType.Mapping] = new()
        {
            Type = ShaderNodeType.Mapping, Title = "マッピング", Category = "ベクトル",
            HeaderColorHex = HdrVector, DefaultWidth = 196f,
            Outputs = new[] { Out("Vector", "ベクトル", ShaderSocketKind.Vector) },
            Inputs = new[]
            {
                InV("Vector", "ベクトル"),
                InF("LocX", "位置 X", 0f, -10f, 10f),
                InF("LocY", "位置 Y", 0f, -10f, 10f),
                InF("RotZ", "回転 (°)", 0f, -360f, 360f),
                InF("ScaleX", "スケール X", 1f, -20f, 20f),
                InF("ScaleY", "スケール Y", 1f, -20f, 20f),
            }
        },
        [ShaderNodeType.Bump] = new()
        {
            Type = ShaderNodeType.Bump, Title = "バンプ", Category = "ベクトル",
            HeaderColorHex = HdrVector, DefaultWidth = 190f,
            Outputs = new[] { Out("Normal", "ノーマル", ShaderSocketKind.Vector) },
            Inputs = new[]
            {
                InF("Strength", "強さ", 1f, 0f, 1f),
                InF("Distance", "距離", 0.25f, 0f, 2f),
                InF("Height", "高さ", 0f, 0f, 1f),
                InV("Normal", "ノーマル"),
            }
        },
        [ShaderNodeType.NormalMap] = new()
        {
            Type = ShaderNodeType.NormalMap, Title = "ノーマルマップ", Category = "ベクトル",
            HeaderColorHex = HdrVector, DefaultWidth = 192f,
            Outputs = new[] { Out("Normal", "ノーマル", ShaderSocketKind.Vector) },
            Inputs = new[]
            {
                InF("Strength", "強さ", 1f, 0f, 5f),
                InC("Color", "カラー", (float[])FlatNorm.Clone()),
            }
        },
        [ShaderNodeType.Math] = new()
        {
            Type = ShaderNodeType.Math, Title = "数式 (Math)", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 186f,
            Outputs = new[] { Out("Value", "値", ShaderSocketKind.Float) },
            Inputs = new[]
            {
                InF("Value1", "値 1", 0.5f, -100f, 100f),
                InF("Value2", "値 2", 0.5f, -100f, 100f),
            }
        },
        [ShaderNodeType.MapRange] = new()
        {
            Type = ShaderNodeType.MapRange, Title = "範囲マッピング", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 190f,
            Outputs = new[] { Out("Result", "結果", ShaderSocketKind.Float) },
            Inputs = new[]
            {
                InF("Value", "値", 0.5f, -10f, 10f),
                InF("FromMin", "入力最小", 0f, -10f, 10f),
                InF("FromMax", "入力最大", 1f, -10f, 10f),
                InF("ToMin", "出力最小", 0f, -10f, 10f),
                InF("ToMax", "出力最大", 1f, -10f, 10f),
            }
        },
        [ShaderNodeType.SeparateColor] = new()
        {
            Type = ShaderNodeType.SeparateColor, Title = "カラー分離 (RGB)", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 176f,
            Outputs = new[]
            {
                Out("Red", "R", ShaderSocketKind.Float),
                Out("Green", "G", ShaderSocketKind.Float),
                Out("Blue", "B", ShaderSocketKind.Float),
            },
            Inputs = new[] { InC("Color", "カラー", (float[])Gray50.Clone()) }
        },
        [ShaderNodeType.CombineColor] = new()
        {
            Type = ShaderNodeType.CombineColor, Title = "カラー合成 (RGB)", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 176f,
            Outputs = new[] { Out("Color", "カラー", ShaderSocketKind.Color) },
            Inputs = new[]
            {
                InF("Red", "R", 0f, 0f, 1f),
                InF("Green", "G", 0f, 0f, 1f),
                InF("Blue", "B", 0f, 0f, 1f),
            }
        },
        [ShaderNodeType.RgbToBw] = new()
        {
            Type = ShaderNodeType.RgbToBw, Title = "RGB → BW (明度)", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 176f,
            Outputs = new[] { Out("Val", "値", ShaderSocketKind.Float) },
            Inputs = new[] { InC("Color", "カラー", (float[])Gray50.Clone()) }
        },
        [ShaderNodeType.VectorMath] = new()
        {
            Type = ShaderNodeType.VectorMath, Title = "ベクトル数式 (Vector Math)", Category = "ベクトル",
            HeaderColorHex = HdrVector, DefaultWidth = 200f,
            Outputs = new[]
            {
                Out("Vector", "ベクトル", ShaderSocketKind.Vector),
                Out("Value", "値", ShaderSocketKind.Float),
            },
            Inputs = new[]
            {
                InV("Vector1", "ベクトル 1"),
                InV("Vector2", "ベクトル 2"),
                InF("Scale", "スケール", 1f, -100f, 100f),
            }
        },
        [ShaderNodeType.SeparateXyz] = new()
        {
            Type = ShaderNodeType.SeparateXyz, Title = "XYZ 分離", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 176f,
            Outputs = new[]
            {
                Out("X", "X", ShaderSocketKind.Float),
                Out("Y", "Y", ShaderSocketKind.Float),
                Out("Z", "Z", ShaderSocketKind.Float),
            },
            Inputs = new[] { InV("Vector", "ベクトル") }
        },
        [ShaderNodeType.CombineXyz] = new()
        {
            Type = ShaderNodeType.CombineXyz, Title = "XYZ 合成", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 176f,
            Outputs = new[] { Out("Vector", "ベクトル", ShaderSocketKind.Vector) },
            Inputs = new[]
            {
                InF("X", "X", 0f, -100f, 100f),
                InF("Y", "Y", 0f, -100f, 100f),
                InF("Z", "Z", 0f, -100f, 100f),
            }
        },
        [ShaderNodeType.MixValue] = new()
        {
            Type = ShaderNodeType.MixValue, Title = "ミックス (数値 / ベクトル)", Category = "コンバーター",
            HeaderColorHex = HdrConverter, DefaultWidth = 196f,
            Outputs = new[]
            {
                Out("Result", "結果 (値)", ShaderSocketKind.Float),
                Out("Vector", "結果 (ベクトル)", ShaderSocketKind.Vector),
            },
            Inputs = new[]
            {
                InF("Fac", "係数", 0.5f, 0f, 1f),
                InF("A", "A (値)", 0f, -100f, 100f),
                InF("B", "B (値)", 1f, -100f, 100f),
                InV("VecA", "A (ベクトル)"),
                InV("VecB", "B (ベクトル)"),
            }
        },
    };

    public static ShaderNodeSpec GetSpec(ShaderNodeType type)
        => s_specs.TryGetValue(type, out var spec) ? spec : s_specs[ShaderNodeType.PrincipledBsdf];

    public static IEnumerable<ShaderNodeSpec> GetAddableSpecs()
        => s_specs.Values.Where(s => s.Type != ShaderNodeType.MaterialOutput);

    public static string GetSocketColorHex(ShaderSocketKind kind) => kind switch
    {
        ShaderSocketKind.Float => "#A1A1A1",
        ShaderSocketKind.Color => "#C7C729",
        ShaderSocketKind.Vector => "#6E6CD6",
        ShaderSocketKind.Shader => "#63C763",
        _ => "#A1A1A1"
    };
}

public sealed class ColorRampStop
{
    public float Position { get; set; }
    public float[] Color { get; set; } = { 1f, 1f, 1f, 1f };

    public ColorRampStop Clone() => new()
    {
        Position = Position,
        Color = Color is { Length: 4 } ? (float[])Color.Clone() : new[] { 1f, 1f, 1f, 1f }
    };
}

public sealed class ShaderNode
{
    public int Id { get; set; }
    public ShaderNodeType Type { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public Dictionary<string, float> FloatParams { get; set; } = new();
    public Dictionary<string, float[]> ColorParams { get; set; } = new();
    public Dictionary<string, string> StringParams { get; set; } = new();
    public Dictionary<string, bool> BoolParams { get; set; } = new();
    public List<ColorRampStop> RampStops { get; set; } = new();

    public float GetFloat(string key, float fallback = 0f)
        => FloatParams.TryGetValue(key, out float v) ? v : fallback;

    public Vector4 GetColor(string key, Vector4 fallback)
    {
        if (ColorParams.TryGetValue(key, out var c) && c is { Length: >= 3 })
            return new Vector4(c[0], c[1], c[2], c.Length >= 4 ? c[3] : 1f);
        return fallback;
    }

    public void SetColor(string key, Vector4 col)
        => ColorParams[key] = new[] { col.X, col.Y, col.Z, col.W };

    public string GetString(string key, string fallback = "")
        => StringParams.TryGetValue(key, out var s) ? s : fallback;

    public bool GetBool(string key, bool fallback = false)
        => BoolParams.TryGetValue(key, out bool b) ? b : fallback;

    public ShaderNode Clone()
    {
        var copy = new ShaderNode
        {
            Id = Id,
            Type = Type,
            X = X,
            Y = Y,
            FloatParams = new Dictionary<string, float>(FloatParams),
            StringParams = new Dictionary<string, string>(StringParams),
            BoolParams = new Dictionary<string, bool>(BoolParams),
            ColorParams = new Dictionary<string, float[]>(ColorParams.Count),
            RampStops = new List<ColorRampStop>(RampStops.Count)
        };
        foreach (var kv in ColorParams)
            copy.ColorParams[kv.Key] = (float[])kv.Value.Clone();
        foreach (var stop in RampStops)
            copy.RampStops.Add(stop.Clone());
        return copy;
    }

    public static ShaderNode CreateWithDefaults(int id, ShaderNodeType type, float x, float y)
    {
        var n = new ShaderNode { Id = id, Type = type, X = x, Y = y };
        var spec = ShaderNodeRegistry.GetSpec(type);
        foreach (var inp in spec.Inputs)
        {
            if (inp.ParamKey == null) continue;
            if (inp.Kind == ShaderSocketKind.Float)
                n.FloatParams[inp.ParamKey] = inp.DefaultFloat;
            else if (inp.Kind == ShaderSocketKind.Color)
                n.ColorParams[inp.ParamKey] = (float[])inp.DefaultColor.Clone();
        }

        switch (type)
        {
            case ShaderNodeType.Value:
                n.FloatParams["Value"] = 0.5f;
                break;
            case ShaderNodeType.RgbColor:
                n.ColorParams["Color"] = new[] { 1f, 1f, 1f, 1f };
                break;
            case ShaderNodeType.ImageTexture:
                n.StringParams["FilePath"] = string.Empty;
                n.StringParams["ColorSpace"] = "sRGB";
                n.BoolParams["FlipV"] = false;
                break;
            case ShaderNodeType.NoiseTexture:
                n.StringParams["Dimension"] = "3D";
                n.StringParams["NoiseType"] = "fBM";
                n.BoolParams["Normalize"] = true;
                break;
            case ShaderNodeType.VoronoiTexture:
                n.StringParams["Feature"] = "F1";
                break;
            case ShaderNodeType.WaveTexture:
                n.StringParams["WaveType"] = "Bands";
                n.StringParams["Profile"] = "Sine";
                break;
            case ShaderNodeType.GradientTexture:
                n.StringParams["GradientType"] = "Linear";
                break;
            case ShaderNodeType.MixColor:
                n.StringParams["BlendType"] = "Mix";
                n.BoolParams["Clamp"] = true;
                break;
            case ShaderNodeType.ColorRamp:
                n.StringParams["Interpolation"] = "Linear";
                n.RampStops.Add(new ColorRampStop { Position = 0f, Color = new[] { 0f, 0f, 0f, 1f } });
                n.RampStops.Add(new ColorRampStop { Position = 1f, Color = new[] { 1f, 1f, 1f, 1f } });
                break;
            case ShaderNodeType.Bump:
                n.BoolParams["Invert"] = false;
                break;
            case ShaderNodeType.NormalMap:
                n.BoolParams["FlipY"] = false;
                break;
            case ShaderNodeType.Math:
                n.StringParams["Operation"] = "Add";
                n.BoolParams["Clamp"] = false;
                break;
            case ShaderNodeType.MapRange:
                n.BoolParams["Clamp"] = true;
                break;
            case ShaderNodeType.TimeInfo:
                n.StringParams["UpdateFps"] = "15 fps";
                n.BoolParams["AnimDraftRes"] = true;
                break;
            case ShaderNodeType.LayerTexture:
                n.FloatParams["Layer"] = 0f;
                n.BoolParams["FlipV"] = false;
                break;
            case ShaderNodeType.AmbientOcclusion:
                n.BoolParams["Inside"] = false;
                n.BoolParams["OnlyLocal"] = false;
                break;
            case ShaderNodeType.Wireframe:
                n.BoolParams["PixelSize"] = true;
                break;
            case ShaderNodeType.VectorMath:
                n.StringParams["Operation"] = "Add";
                break;
            case ShaderNodeType.MixValue:
                n.StringParams["DataType"] = "Float";
                n.BoolParams["ClampFactor"] = true;
                break;
        }
        return n;
    }
}

public sealed class ShaderNodeLink
{
    public int FromNodeId { get; set; }
    public string FromSocket { get; set; } = string.Empty;
    public int ToNodeId { get; set; }
    public string ToSocket { get; set; } = string.Empty;

    public ShaderNodeLink Clone() => new()
    {
        FromNodeId = FromNodeId,
        FromSocket = FromSocket,
        ToNodeId = ToNodeId,
        ToSocket = ToSocket
    };
}

public sealed class MaterialNodeGraph
{
    public int NextNodeId { get; set; } = 1;
    public int BakeResolution { get; set; } = 512;
    public List<ShaderNode> Nodes { get; set; } = new();
    public List<ShaderNodeLink> Links { get; set; } = new();

    public ShaderNode? FindNode(int id) => Nodes.FirstOrDefault(n => n.Id == id);
    public ShaderNode? FindOutputNode() => Nodes.FirstOrDefault(n => n.Type == ShaderNodeType.MaterialOutput);

    public ShaderNodeLink? FindLinkTo(int toNodeId, string toSocket)
        => Links.FirstOrDefault(l => l.ToNodeId == toNodeId && string.Equals(l.ToSocket, toSocket, StringComparison.Ordinal));

    public ShaderNode AddNode(ShaderNodeType type, float x, float y)
    {
        int id = Math.Max(1, NextNodeId++);
        while (FindNode(id) != null) id = NextNodeId++;
        var node = ShaderNode.CreateWithDefaults(id, type, x, y);
        Nodes.Add(node);
        return node;
    }

    public bool RemoveNode(int nodeId)
    {
        var node = FindNode(nodeId);
        if (node == null || node.Type == ShaderNodeType.MaterialOutput)
            return false;
        Links.RemoveAll(l => l.FromNodeId == nodeId || l.ToNodeId == nodeId);
        return Nodes.Remove(node);
    }

    public void RemoveLinkTo(int toNodeId, string toSocket)
        => Links.RemoveAll(l => l.ToNodeId == toNodeId && string.Equals(l.ToSocket, toSocket, StringComparison.Ordinal));

    public bool TryAddLink(int fromNodeId, string fromSocket, int toNodeId, string toSocket)
    {
        if (fromNodeId == toNodeId) return false;
        var src = FindNode(fromNodeId);
        var dst = FindNode(toNodeId);
        if (src == null || dst == null) return false;

        var srcSpec = ShaderNodeRegistry.GetSpec(src.Type);
        var dstSpec = ShaderNodeRegistry.GetSpec(dst.Type);
        var outSock = srcSpec.Outputs.FirstOrDefault(s => s.Name == fromSocket);
        var inSock = dstSpec.Inputs.FirstOrDefault(s => s.Name == toSocket);
        if (outSock == null || inSock == null) return false;

        // Shader ソケット同士、または MaterialOutput.Surface への Color/Float 直接出力（Blender互換暗黙Emission）のみ許可
        bool allowToSurface = dst.Type == ShaderNodeType.MaterialOutput
            && string.Equals(toSocket, "Surface", StringComparison.Ordinal)
            && (outSock.Kind == ShaderSocketKind.Color || outSock.Kind == ShaderSocketKind.Float);
        if (!allowToSurface && (outSock.Kind == ShaderSocketKind.Shader) != (inSock.Kind == ShaderSocketKind.Shader))
            return false;

        if (WouldCreateCycle(fromNodeId, toNodeId))
            return false;

        Links.RemoveAll(l => l.ToNodeId == toNodeId && string.Equals(l.ToSocket, toSocket, StringComparison.Ordinal));
        Links.Add(new ShaderNodeLink
        {
            FromNodeId = fromNodeId,
            FromSocket = fromSocket,
            ToNodeId = toNodeId,
            ToSocket = toSocket
        });
        return true;
    }

    private bool WouldCreateCycle(int fromNodeId, int toNodeId)
    {
        // 既存のリンク群に沿って fromNodeId から上流へ辿り、toNodeId に到達したらループ
        var visited = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(fromNodeId);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            if (cur == toNodeId) return true;
            if (!visited.Add(cur)) continue;
            foreach (var l in Links)
            {
                if (l.ToNodeId == cur)
                    stack.Push(l.FromNodeId);
            }
        }
        return false;
    }

    public MaterialNodeGraph Clone()
    {
        var g = new MaterialNodeGraph
        {
            NextNodeId = NextNodeId,
            BakeResolution = BakeResolution,
            Nodes = new List<ShaderNode>(Nodes.Count),
            Links = new List<ShaderNodeLink>(Links.Count)
        };
        foreach (var n in Nodes) g.Nodes.Add(n.Clone());
        foreach (var l in Links) g.Links.Add(l.Clone());
        return g;
    }

    internal static MaterialNodeGraph CreateDefaultFromMaterial(EditableMaterial mat)
    {
        var g = new MaterialNodeGraph { NextNodeId = 1, BakeResolution = 512 };
        var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 280f, 60f);
        var output = g.AddNode(ShaderNodeType.MaterialOutput, 580f, 100f);
        g.SyncPrincipledScalarsFromMaterial(mat);
        g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
        return g;
    }

    internal static MaterialNodeGraph CreatePreset(string presetKey, EditableMaterial? baseMat = null)
    {
        var g = new MaterialNodeGraph { NextNodeId = 1, BakeResolution = 512 };
        switch (presetKey)
        {
            case "NoiseBump":
            {
                var noise = g.AddNode(ShaderNodeType.NoiseTexture, 30f, 70f);
                noise.FloatParams["Scale"] = 18f;
                noise.FloatParams["Detail"] = 6f;
                noise.FloatParams["Roughness"] = 0.55f;
                var bump = g.AddNode(ShaderNodeType.Bump, 280f, 120f);
                bump.FloatParams["Strength"] = 1f;
                bump.FloatParams["Distance"] = 0.25f;
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 510f, 40f);
                if (baseMat != null) ApplyMaterialToBsdfNode(bsdf, baseMat);
                else bsdf.FloatParams["Roughness"] = 0.35f;
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 790f, 90f);
                g.TryAddLink(noise.Id, "Color", bump.Id, "Height");
                g.TryAddLink(bump.Id, "Normal", bsdf.Id, "Normal");
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "NoiseColorRamp":
            {
                var noise = g.AddNode(ShaderNodeType.NoiseTexture, 20f, 60f);
                noise.FloatParams["Scale"] = 6f;
                noise.FloatParams["Detail"] = 5f;
                noise.FloatParams["Distortion"] = 0.6f;
                var ramp = g.AddNode(ShaderNodeType.ColorRamp, 260f, 60f);
                ramp.RampStops.Clear();
                ramp.RampStops.Add(new ColorRampStop { Position = 0.20f, Color = new[] { 0.08f, 0.20f, 0.55f, 1f } });
                ramp.RampStops.Add(new ColorRampStop { Position = 0.55f, Color = new[] { 0.15f, 0.72f, 0.85f, 1f } });
                ramp.RampStops.Add(new ColorRampStop { Position = 0.82f, Color = new[] { 0.96f, 0.88f, 0.65f, 1f } });
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 530f, 40f);
                bsdf.FloatParams["Roughness"] = 0.28f;
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 810f, 90f);
                g.TryAddLink(noise.Id, "Fac", ramp.Id, "Fac");
                g.TryAddLink(ramp.Id, "Color", bsdf.Id, "BaseColor");
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "VoronoiBump":
            {
                var vor = g.AddNode(ShaderNodeType.VoronoiTexture, 30f, 70f);
                vor.FloatParams["Scale"] = 12f;
                var bump = g.AddNode(ShaderNodeType.Bump, 280f, 120f);
                bump.FloatParams["Strength"] = 0.9f;
                bump.FloatParams["Distance"] = 0.35f;
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 510f, 40f);
                bsdf.FloatParams["Roughness"] = 0.30f;
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 790f, 90f);
                g.TryAddLink(vor.Id, "Distance", bump.Id, "Height");
                g.TryAddLink(bump.Id, "Normal", bsdf.Id, "Normal");
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "Checker":
            {
                var chk = g.AddNode(ShaderNodeType.CheckerTexture, 60f, 60f);
                chk.FloatParams["Scale"] = 8f;
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 330f, 50f);
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 610f, 90f);
                g.TryAddLink(chk.Id, "Color", bsdf.Id, "BaseColor");
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "BrickBump":
            {
                var brick = g.AddNode(ShaderNodeType.BrickTexture, 20f, 60f);
                brick.FloatParams["Scale"] = 5f;
                var bump = g.AddNode(ShaderNodeType.Bump, 280f, 180f);
                bump.BoolParams["Invert"] = true;
                bump.FloatParams["Distance"] = 0.3f;
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 510f, 40f);
                bsdf.FloatParams["Roughness"] = 0.78f;
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 790f, 90f);
                g.TryAddLink(brick.Id, "Color", bsdf.Id, "BaseColor");
                g.TryAddLink(brick.Id, "Fac", bump.Id, "Height");
                g.TryAddLink(bump.Id, "Normal", bsdf.Id, "Normal");
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "ImageNormal":
            {
                var img = g.AddNode(ShaderNodeType.ImageTexture, 20f, 80f);
                img.StringParams["ColorSpace"] = "Non-Color";
                var nm = g.AddNode(ShaderNodeType.NormalMap, 280f, 110f);
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 510f, 40f);
                if (baseMat != null) ApplyMaterialToBsdfNode(bsdf, baseMat);
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 790f, 90f);
                g.TryAddLink(img.Id, "Color", nm.Id, "Color");
                g.TryAddLink(nm.Id, "Normal", bsdf.Id, "Normal");
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "ToonCel":
            {
                var diff = g.AddNode(ShaderNodeType.DiffuseBsdf, 20f, 80f);
                diff.SetColor("Color", new Vector4(0.85f, 0.90f, 1.0f, 1f));
                var s2r = g.AddNode(ShaderNodeType.ShaderToRgb, 240f, 95f);
                var ramp = g.AddNode(ShaderNodeType.ColorRamp, 450f, 60f);
                ramp.StringParams["Interpolation"] = "Constant";
                ramp.RampStops.Clear();
                ramp.RampStops.Add(new ColorRampStop { Position = 0.00f, Color = new[] { 0.16f, 0.19f, 0.34f, 1f } });
                ramp.RampStops.Add(new ColorRampStop { Position = 0.32f, Color = new[] { 0.42f, 0.62f, 0.90f, 1f } });
                ramp.RampStops.Add(new ColorRampStop { Position = 0.68f, Color = new[] { 0.88f, 0.95f, 1.00f, 1f } });
                var em = g.AddNode(ShaderNodeType.Emission, 705f, 85f);
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 920f, 95f);
                g.TryAddLink(diff.Id, "BSDF", s2r.Id, "Shader");
                g.TryAddLink(s2r.Id, "Color", ramp.Id, "Fac");
                g.TryAddLink(ramp.Id, "Color", em.Id, "Color");
                g.TryAddLink(em.Id, "Emission", output.Id, "Surface");
                break;
            }
            case "IridescentSoap":
            {
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 240f, 50f);
                bsdf.SetColor("BaseColor", new Vector4(0.18f, 0.22f, 0.28f, 1f));
                bsdf.FloatParams["Roughness"] = 0.08f;
                bsdf.FloatParams["Metallic"] = 0.15f;
                bsdf.FloatParams["CoatWeight"] = 0.85f;
                bsdf.FloatParams["CoatRoughness"] = 0.04f;
                bsdf.FloatParams["ThinFilmThickness"] = 460f;
                bsdf.FloatParams["ThinFilmIor"] = 1.38f;
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 530f, 90f);
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "FresnelRim":
            {
                var lw = g.AddNode(ShaderNodeType.LayerWeight, 30f, 90f);
                lw.FloatParams["Blend"] = 0.42f;
                var ramp = g.AddNode(ShaderNodeType.ColorRamp, 250f, 70f);
                ramp.RampStops.Clear();
                ramp.RampStops.Add(new ColorRampStop { Position = 0.25f, Color = new[] { 0.0f, 0.0f, 0.0f, 1f } });
                ramp.RampStops.Add(new ColorRampStop { Position = 0.85f, Color = new[] { 0.12f, 0.85f, 1.0f, 1f } });
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 510f, 40f);
                bsdf.SetColor("BaseColor", new Vector4(0.08f, 0.10f, 0.16f, 1f));
                bsdf.FloatParams["Roughness"] = 0.25f;
                bsdf.FloatParams["EmissionStrength"] = 3.2f;
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 790f, 90f);
                g.TryAddLink(lw.Id, "Facing", ramp.Id, "Fac");
                g.TryAddLink(ramp.Id, "Color", bsdf.Id, "EmissionColor");
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "WireframeGlow":
            {
                var wf = g.AddNode(ShaderNodeType.Wireframe, 30f, 90f);
                wf.FloatParams["Size"] = 1.5f;
                wf.BoolParams["PixelSize"] = true;
                var mix = g.AddNode(ShaderNodeType.MixColor, 250f, 75f);
                mix.SetColor("A", new Vector4(0f, 0f, 0f, 1f));
                mix.SetColor("B", new Vector4(0.10f, 0.95f, 0.65f, 1f));
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 480f, 40f);
                bsdf.SetColor("BaseColor", new Vector4(0.08f, 0.09f, 0.12f, 1f));
                bsdf.FloatParams["Roughness"] = 0.35f;
                bsdf.FloatParams["EmissionStrength"] = 2.5f;
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 760f, 90f);
                g.TryAddLink(wf.Id, "Fac", mix.Id, "Fac");
                g.TryAddLink(mix.Id, "Result", bsdf.Id, "EmissionColor");
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "VelvetSheen":
            {
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 240f, 50f);
                bsdf.SetColor("BaseColor", new Vector4(0.28f, 0.06f, 0.14f, 1f));
                bsdf.FloatParams["Roughness"] = 0.78f;
                bsdf.FloatParams["SheenWeight"] = 1f;
                bsdf.FloatParams["SheenRoughness"] = 0.35f;
                bsdf.SetColor("SheenTint", new Vector4(1.0f, 0.72f, 0.86f, 1f));
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 530f, 90f);
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "BrushedAniso":
            {
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 240f, 50f);
                bsdf.SetColor("BaseColor", new Vector4(0.84f, 0.86f, 0.90f, 1f));
                bsdf.FloatParams["Metallic"] = 1f;
                bsdf.FloatParams["Roughness"] = 0.28f;
                bsdf.FloatParams["Anisotropic"] = 0.88f;
                bsdf.FloatParams["AnisotropicRotation"] = 0f;
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 530f, 90f);
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "SubsurfaceWax":
            {
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 240f, 50f);
                bsdf.SetColor("BaseColor", new Vector4(0.95f, 0.86f, 0.78f, 1f));
                bsdf.FloatParams["Roughness"] = 0.32f;
                bsdf.FloatParams["SubsurfaceWeight"] = 0.85f;
                bsdf.FloatParams["SubsurfaceScale"] = 0.65f;
                bsdf.SetColor("SubsurfaceColor", new Vector4(1f, 0.36f, 0.22f, 1f));
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 530f, 90f);
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            case "LayerScreen":
            {
                var lt = g.AddNode(ShaderNodeType.LayerTexture, 30f, 75f);
                lt.FloatParams["Layer"] = 0f;
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 280f, 50f);
                bsdf.FloatParams["Roughness"] = 0.25f;
                bsdf.FloatParams["EmissionStrength"] = 0.75f;
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 560f, 90f);
                g.TryAddLink(lt.Id, "Color", bsdf.Id, "BaseColor");
                g.TryAddLink(lt.Id, "Color", bsdf.Id, "EmissionColor");
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
            default:
            {
                var bsdf = g.AddNode(ShaderNodeType.PrincipledBsdf, 280f, 60f);
                if (baseMat != null) ApplyMaterialToBsdfNode(bsdf, baseMat);
                var output = g.AddNode(ShaderNodeType.MaterialOutput, 580f, 100f);
                g.TryAddLink(bsdf.Id, "BSDF", output.Id, "Surface");
                break;
            }
        }
        return g;
    }

    internal ShaderNode? FindPrimaryPrincipledNode()
    {
        var outNode = FindOutputNode();
        if (outNode != null)
        {
            var surfLink = FindLinkTo(outNode.Id, "Surface");
            if (surfLink != null)
            {
                var direct = FindNode(surfLink.FromNodeId);
                if (direct?.Type == ShaderNodeType.PrincipledBsdf)
                    return direct;
            }
        }
        return Nodes.FirstOrDefault(n => n.Type == ShaderNodeType.PrincipledBsdf);
    }

    internal void SyncPrincipledScalarsFromMaterial(EditableMaterial mat)
    {
        var bsdf = FindPrimaryPrincipledNode();
        if (bsdf == null) return;
        ApplyMaterialToBsdfNode(bsdf, mat);
    }

    private static void ApplyMaterialToBsdfNode(ShaderNode bsdf, EditableMaterial mat)
    {
        bsdf.SetColor("BaseColor", mat.BaseColor);
        bsdf.FloatParams["Alpha"] = Math.Clamp(mat.BaseColor.W, 0f, 1f);
        bsdf.FloatParams["Metallic"] = Math.Clamp(mat.Metallic, 0f, 1f);
        bsdf.FloatParams["Roughness"] = mat.Roughness >= 0f ? Math.Clamp(mat.Roughness, 0.04f, 1f) : 0.5f;
        bsdf.FloatParams["Transmission"] = Math.Clamp(mat.Transmission, 0f, 1f);
        bsdf.FloatParams["Ior"] = Math.Clamp(mat.Ior > 0f ? mat.Ior : 1.5f, 1f, 2.5f);
        bsdf.SetColor("EmissionColor", new Vector4(mat.EmissiveColor.X, mat.EmissiveColor.Y, mat.EmissiveColor.Z, 1f));
        bsdf.FloatParams["EmissionStrength"] = Math.Clamp(mat.EmissiveColor.W, 0f, 20f);
    }

    internal void MirrorPrincipledScalarsToMaterial(EditableMaterial mat)
    {
        var bsdf = FindPrimaryPrincipledNode();
        if (bsdf == null) return;

        if (FindLinkTo(bsdf.Id, "BaseColor") == null)
        {
            var bc = bsdf.GetColor("BaseColor", mat.BaseColor);
            float alpha = bsdf.GetFloat("Alpha", bc.W);
            mat.BaseColor = new Vector4(bc.X, bc.Y, bc.Z, Math.Clamp(alpha, 0f, 1f));
        }
        if (FindLinkTo(bsdf.Id, "Metallic") == null)
            mat.Metallic = Math.Clamp(bsdf.GetFloat("Metallic", mat.Metallic), 0f, 1f);
        if (FindLinkTo(bsdf.Id, "Roughness") == null)
            mat.Roughness = Math.Clamp(bsdf.GetFloat("Roughness", mat.Roughness), 0.04f, 1f);
        if (FindLinkTo(bsdf.Id, "Transmission") == null)
            mat.Transmission = Math.Clamp(bsdf.GetFloat("Transmission", mat.Transmission), 0f, 1f);
        if (FindLinkTo(bsdf.Id, "Ior") == null)
            mat.Ior = Math.Clamp(bsdf.GetFloat("Ior", mat.Ior), 1f, 2.5f);
        if (FindLinkTo(bsdf.Id, "EmissionColor") == null && FindLinkTo(bsdf.Id, "EmissionStrength") == null)
        {
            var ec = bsdf.GetColor("EmissionColor", new Vector4(1f, 1f, 1f, 1f));
            float ew = Math.Clamp(bsdf.GetFloat("EmissionStrength", mat.EmissiveColor.W), 0f, 20f);
            mat.EmissiveColor = new Vector4(ec.X, ec.Y, ec.Z, ew);
        }
    }

    public string ComputeContentHash()
    {
        var hash = new HashCode();
        hash.Add(BakeResolution);
        hash.Add(Nodes.Count);
        foreach (var n in Nodes.OrderBy(x => x.Id))
        {
            hash.Add(n.Id);
            hash.Add((int)n.Type);
            foreach (var kv in n.FloatParams.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                hash.Add(kv.Key);
                hash.Add(kv.Value);
            }
            foreach (var kv in n.ColorParams.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                hash.Add(kv.Key);
                if (kv.Value != null)
                {
                    foreach (float f in kv.Value) hash.Add(f);
                }
            }
            foreach (var kv in n.StringParams.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                hash.Add(kv.Key);
                hash.Add(kv.Value);
                if (kv.Key == "FilePath" && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    try
                    {
                        var fi = new FileInfo(kv.Value);
                        if (fi.Exists)
                        {
                            hash.Add(fi.LastWriteTimeUtc.Ticks);
                            hash.Add(fi.Length);
                        }
                    }
                    catch { }
                }
            }
            foreach (var kv in n.BoolParams.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                hash.Add(kv.Key);
                hash.Add(kv.Value);
            }
            hash.Add(n.RampStops.Count);
            foreach (var st in n.RampStops)
            {
                hash.Add(st.Position);
                foreach (float f in st.Color) hash.Add(f);
            }
        }
        hash.Add(Links.Count);
        foreach (var l in Links.OrderBy(x => x.ToNodeId).ThenBy(x => x.ToSocket, StringComparer.Ordinal))
        {
            hash.Add(l.FromNodeId);
            hash.Add(l.FromSocket);
            hash.Add(l.ToNodeId);
            hash.Add(l.ToSocket);
        }
        return hash.ToHashCode().ToString("X8", CultureInfo.InvariantCulture);
    }
}

internal sealed class ShaderNodeClipboardPayload
{
    public int Version { get; set; } = 1;
    public float CenterX { get; set; }
    public float CenterY { get; set; }
    public List<ShaderNode> Nodes { get; set; } = new();
    public List<ShaderNodeLink> Links { get; set; } = new();

    public ShaderNodeClipboardPayload Clone()
    {
        var copy = new ShaderNodeClipboardPayload
        {
            Version = Version,
            CenterX = CenterX,
            CenterY = CenterY,
            Nodes = new List<ShaderNode>(Nodes.Count),
            Links = new List<ShaderNodeLink>(Links.Count)
        };
        foreach (var n in Nodes) copy.Nodes.Add(n.Clone());
        foreach (var l in Links) copy.Links.Add(l.Clone());
        return copy;
    }
}

internal static class ShaderNodeClipboard
{
    private const string ClipboardPrefix = "D3D11_SNODE1:";
    private static readonly object s_clipLock = new();
    private static ShaderNodeClipboardPayload? s_cachedPayload;

    public static bool HasData
    {
        get
        {
            lock (s_clipLock)
                return s_cachedPayload != null && s_cachedPayload.Nodes.Count > 0;
        }
    }

    public static string? CopySubgraph(MaterialNodeGraph graph, IReadOnlyCollection<int> selectedIds)
    {
        if (graph == null || selectedIds == null || selectedIds.Count == 0) return null;

        var idSet = new HashSet<int>();
        var nodesToCopy = new List<ShaderNode>();
        foreach (int id in selectedIds)
        {
            var n = graph.FindNode(id);
            if (n == null || n.Type == ShaderNodeType.MaterialOutput) continue;
            if (idSet.Add(id)) nodesToCopy.Add(n.Clone());
        }

        if (nodesToCopy.Count == 0) return null;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var n in nodesToCopy)
        {
            var spec = ShaderNodeRegistry.GetSpec(n.Type);
            minX = MathF.Min(minX, n.X);
            minY = MathF.Min(minY, n.Y);
            maxX = MathF.Max(maxX, n.X + spec.DefaultWidth);
            maxY = MathF.Max(maxY, n.Y + 120f);
        }

        var linksToCopy = new List<ShaderNodeLink>();
        foreach (var l in graph.Links)
        {
            if (idSet.Contains(l.FromNodeId) && idSet.Contains(l.ToNodeId))
                linksToCopy.Add(l.Clone());
        }

        var payload = new ShaderNodeClipboardPayload
        {
            Version = 1,
            CenterX = 0.5f * (minX + maxX),
            CenterY = 0.5f * (minY + maxY),
            Nodes = nodesToCopy,
            Links = linksToCopy
        };

        lock (s_clipLock)
        {
            s_cachedPayload = payload.Clone();
        }

        return ClipboardPrefix + JsonSerializer.Serialize(payload);
    }

    public static bool TryDeserializePayload(string? text, out ShaderNodeClipboardPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith(ClipboardPrefix, StringComparison.Ordinal))
            return false;

        try
        {
            string json = text.Substring(ClipboardPrefix.Length);
            var parsed = JsonSerializer.Deserialize<ShaderNodeClipboardPayload>(json);
            if (parsed == null || parsed.Nodes == null || parsed.Nodes.Count == 0)
                return false;
            payload = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool PasteSubgraph(
        MaterialNodeGraph targetGraph,
        float? targetCenterX,
        float? targetCenterY,
        out List<int> pastedNodeIds,
        string? externalClipboardText = null)
    {
        pastedNodeIds = new List<int>();
        if (targetGraph == null) return false;

        ShaderNodeClipboardPayload? payload = null;
        if (TryDeserializePayload(externalClipboardText, out var parsedExt) && parsedExt != null)
        {
            payload = parsedExt.Clone();
            lock (s_clipLock) s_cachedPayload = parsedExt.Clone();
        }
        else
        {
            lock (s_clipLock) payload = s_cachedPayload?.Clone();
        }

        if (payload == null || payload.Nodes.Count == 0) return false;

        float dx = targetCenterX.HasValue ? targetCenterX.Value - payload.CenterX : 36f;
        float dy = targetCenterY.HasValue ? targetCenterY.Value - payload.CenterY : 36f;

        var idMap = new Dictionary<int, int>(payload.Nodes.Count);
        foreach (var srcNode in payload.Nodes)
        {
            if (srcNode.Type == ShaderNodeType.MaterialOutput) continue;
            var cloned = srcNode.Clone();
            int newId = Math.Max(1, targetGraph.NextNodeId++);
            while (targetGraph.FindNode(newId) != null) newId = targetGraph.NextNodeId++;
            cloned.Id = newId;
            cloned.X = MathF.Round(cloned.X + dx);
            cloned.Y = MathF.Round(cloned.Y + dy);
            targetGraph.Nodes.Add(cloned);
            idMap[srcNode.Id] = newId;
            pastedNodeIds.Add(newId);
        }

        if (pastedNodeIds.Count == 0) return false;

        foreach (var srcLink in payload.Links)
        {
            if (idMap.TryGetValue(srcLink.FromNodeId, out int newFrom) &&
                idMap.TryGetValue(srcLink.ToNodeId, out int newTo))
            {
                targetGraph.TryAddLink(newFrom, srcLink.FromSocket, newTo, srcLink.ToSocket);
            }
        }

        return true;
    }
}
