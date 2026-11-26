using System.Numerics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

/// <summary>
/// アイテム個別の PBR マテリアル上書きパラメータ（「D3Dマテリアル」エフェクトから供給）。
/// </summary>
public struct MaterialOverrideParameters
{
    public bool Enabled;
    public float Metallic;
    public float Roughness;
    public float Clearcoat;
    public Vector4 TintColor;     // linear RGBA
    public Vector4 EmissiveColor; // linear RGB, w = intensity
    public string? NormalMapPath;
    public float NormalStrength;
    public string? RoughnessMapPath;
    public float UvScale;
    public float Transmission;
    public float Ior;
}
