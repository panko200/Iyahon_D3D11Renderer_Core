using System.Numerics;
using System.Runtime.InteropServices;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

public enum LightType
{
    Directional = 0,
    Point = 1,
    Spot = 2,
    Area = 3,
}

public sealed class LightData
{
    public LightType Type { get; set; } = LightType.Directional;
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Vector3 Direction { get; set; } = Vector3.Normalize(new Vector3(0.3f, -0.7f, -1.0f));
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 1.0f;
    public float Range { get; set; } = 5000f;
    public float SpotInnerAngle { get; set; } = 15f;
    public float SpotOuterAngle { get; set; } = 30f;
    public float AreaWidth { get; set; } = 200f;
    public float AreaHeight { get; set; } = 200f;
    public bool CastShadow { get; set; } = false;
    public float ShadowIntensity { get; set; } = 0.5f;
    public float ShadowBias { get; set; } = 0.0005f;
    public bool ShowGizmo { get; set; } = true; // ★追加：ガイドライン等の視覚化フラグ
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuLightData
{
    public Vector4 PositionAndType;
    public Vector4 DirectionAndIntensity;
    public Vector4 ColorAndRange;
    public Vector4 SpotParams;

    public static GpuLightData FromLightData(LightData light)
    {
        float d2r = MathF.PI / 180f;
        return new GpuLightData
        {
            PositionAndType = new Vector4(light.Position, (float)light.Type),
            DirectionAndIntensity = new Vector4(
                Vector3.Normalize(light.Direction), light.Intensity),
            ColorAndRange = new Vector4(light.Color, light.Range),
            SpotParams = new Vector4(
                MathF.Cos(light.SpotInnerAngle * d2r),
                MathF.Cos(light.SpotOuterAngle * d2r),
                light.AreaWidth,
                light.AreaHeight),
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuShadowData
{
    // Area光源も含め、全タイプ単一視点のシャドウマップ1枚のみを使う(PCSS方式)。
    // Area光源のソフトシャドウは、複数視点の物理シミュレーションではなく、
    // シェーダー側のブロッカーサーチ+可変PCFカーネル(PCSS)で近似する。
    // これにより毎フレームのシーン再描画パス数を1照明=1回に抑える(旧:9回)。
    public Matrix4x4 LightViewProj0;
    public Vector4 ShadowParams; // x=bias, y=intensity, z=lightIndex, w=shadowMode(0=単一2D,1=Cube,2=Area/PCSS)
    public Vector4 AtlasParams;  // x=StartTile, y=TargetRes, z=ScaleFactor, w=lightSize(ワールド単位の半径相当。PCSSのライトサイズに使用)
    public Vector4 DepthParams;  // x=NearPlane, y=FarPlane (Area用。NDC深度→線形深度変換に使用), z/w=未使用
}

[StructLayout(LayoutKind.Sequential)]
public struct CbLighting
{
    public int LightCount;
    public float UseSimpleLight;
    public float EnableShadow;
    public float AmbientIntensity;

    public Vector4 AmbientColor;

    public GpuShadowData Shadow0;
    public GpuShadowData Shadow1;
    public GpuShadowData Shadow2;
    public GpuShadowData Shadow3;
    public GpuShadowData Shadow4;
    public GpuShadowData Shadow5;
    public GpuShadowData Shadow6;
    public GpuShadowData Shadow7;
    public int ShadowCount;
    public float EnableSoftShadow;
    public Vector2 _padShadow;

    public GpuLightData Light0;
    public GpuLightData Light1;
    public GpuLightData Light2;
    public GpuLightData Light3;
    public GpuLightData Light4;
    public GpuLightData Light5;
    public GpuLightData Light6;
    public GpuLightData Light7;

    public const int MAX_LIGHTS = 8;

    public void SetLight(int index, GpuLightData data)
    {
        switch (index)
        {
            case 0: Light0 = data; break;
            case 1: Light1 = data; break;
            case 2: Light2 = data; break;
            case 3: Light3 = data; break;
            case 4: Light4 = data; break;
            case 5: Light5 = data; break;
            case 6: Light6 = data; break;
            case 7: Light7 = data; break;
        }
    }
}