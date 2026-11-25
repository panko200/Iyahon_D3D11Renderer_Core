using System.ComponentModel.DataAnnotations;
using System.Numerics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

/// <summary>
/// YMM4 公式の NoiseType と同一値・全8種類に対応した 3Dノイズ種別。
/// </summary>
public enum D3DNoise3DType
{
    [Display(Name = "フラクタル (Fractal)")]
    Fractal = 0,

    [Display(Name = "ランダム (Random)")]
    Random = 3,

    [Display(Name = "ブロック (Block)")]
    Block = 100,

    [Display(Name = "パーリン (Perlin)")]
    Perlin = 101,

    [Display(Name = "セルラー (Cellular)")]
    Cellular = 102,

    [Display(Name = "ボロノイ (Voronoi)")]
    Voronoi = 103,

    [Display(Name = "シンプレックス (Simplex)")]
    Simplex = 104,

    [Display(Name = "カール (Curl)")]
    Curl = 105,
}

public enum D3DNoise3DRenderMode
{
    [Display(Name = "3Dボリューム (雲・霧)")]
    Volume3D = 0,

    [Display(Name = "3D立体形状 (等値面・陰影)")]
    IsoSurface3D = 1,

    [Display(Name = "サーフェス (表面模様・バンプ)")]
    Surface3D = 2,
}

internal struct Noise3DParameters
{
    public bool Enabled;
    public D3DNoise3DType NoiseType;
    public D3DNoise3DRenderMode RenderMode;
    public Vector3 Scale;
    public float Complexity;
    public float Threshold;
    public float Softness;
    public float Density;
    public float BumpStrength;
    public int StepCount;
    public Vector3 Offset;
    public float Evolution;
    public float Seed;
    public bool Invert;
    public Vector4 ColorA;
    public Vector4 ColorB;
    public float EmissionStrength;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;

    public readonly bool IsSemiTransparent =>
        RenderMode == D3DNoise3DRenderMode.Volume3D || ColorA.W < 0.999f || ColorB.W < 0.999f;
}
