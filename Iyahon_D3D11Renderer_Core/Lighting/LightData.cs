using System;
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
    public Vector3 AreaRight { get; set; } = Vector3.UnitX;
    public bool HasExplicitAreaRight { get; set; } = false;
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
    public float ShadowNormalBias { get; set; } = 1.5f;
    public float ShadowSoftness { get; set; } = 1.0f;
    public bool ShowGizmo { get; set; } = true;
    public int Layer { get; set; } = 0;
}

/// <summary>
/// ライティング定数バッファヘッダ (b1)。176 bytes (16Bアライン済)。
/// 実データの配列は StructuredBuffer (t3: Lights, t4: Shadows) に格納し、8個制限を完全撤廃する。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CbLightingHeader
{
    public int LightCount;
    public int ShadowEntryCount;
    public float UseSimpleLight;
    public float EnableShadow;

    public float AmbientIntensity;
    public float EnableSoftShadow;
    public int ShadowQuality;
    public float ShadowSoftness;

    public Vector4 AmbientColor;
    public Vector4 CameraWorldPosAndPageSize;   // xyz = camWorldPos, w = pageSize
    public Vector4 CameraForwardAndRcpPageSize; // xyz = camForward,  w = 1f / pageSize

    public float EnablePbr;
    public float IblIntensity;
    public float DefaultRoughness;
    public float ShadowNoiseScale;

    public Vector4 SkyColorAndGroundMix;        // rgb = linear sky tint, w = ground mix

    public Vector4 CamBasisX;                   // xyz = camera-normal transform basis col0, w = HasEnvMap (0 or 1)
    public Vector4 CamBasisY;                   // xyz = camera-normal transform basis col1, w = EnvRotationRad
    public Vector4 CamBasisZ;                   // xyz = camera-normal transform basis col2, w = EnvMaxMip
    public Vector4 EnvParams;                   // x = EnvExposure, y = EnvReflectionIntensity, z = HasExplicitReflectionScale (1 if set), w = reserved
}

/// <summary>
/// 大気・フォグ・ゴッドレイ用シーンパラメータ。
/// </summary>
public struct AtmosphereParameters
{
    public bool Enabled;
    public bool EnableFog;
    public Vector3 FogColor;
    public float FogDensity;
    public float FogStart;
    public float FogEnd;
    public float HeightFogStrength;
    public float HeightFogBaseY;
    public bool EnableGodRays;
    public float GodRayIntensity;
    public float GodRayDecay;
    public float GodRayDensity;
    public Vector3 GodRayColor;
}

/// <summary>
/// HDRI 環境マップ・スカイボックス・環境光・IBL用シーンパラメータ。
/// </summary>
public struct EnvironmentParameters
{
    public bool Enabled;
    public bool OverrideEnvironmentMap;
    public string? EnvironmentMapPath;
    public float RotationDeg;
    public float Exposure;
    public float? ReflectionIntensity;
    public bool ShowSkybox;
    public float SkyboxBlur;
    public bool OverrideIblAndAmbient;
    public float IblIntensity;
    public float AmbientIntensity;
    public Vector3 SkyColorLinear;
    public Matrix4x4 FallbackCameraMatrix;
}

/// <summary>
/// StructuredBuffer&lt;GpuLightData&gt; (t3) の1要素。96 bytes。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GpuLightData
{
    public Vector4 PositionAndType;       // xyz = pos, w = (float)Type
    public Vector4 DirectionAndIntensity; // xyz = normDir, w = Intensity
    public Vector4 ColorAndRange;         // rgb = linear color, w = Range
    public Vector4 SpotAndAreaParams;     // x = cosInner, y = cosOuter, z = AreaWidth, w = AreaHeight
    public int ShadowStartIndex;          // -1 if no shadow
    public int ShadowCount;               // 1 (Spot/Area), 1..4 (Dir CSM), 6 (Point)
    public int ShadowMode;                // 0=Spot, 1=Point, 2=AreaPCSS, 3=DirCSM
    public int _pad0;
    public Vector4 AreaRightAndSoftness;  // xyz = areaRight, w = perLightShadowSoftness

    private static float SrgbToLinearScalar(float x)
    {
        x = MathF.Max(0f, x);
        return x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    }

    public static GpuLightData FromLightData(
        LightData light,
        Vector3 areaRight,
        int shadowStartIndex,
        int shadowCount,
        int shadowMode)
    {
        float d2r = MathF.PI / 180f;
        Vector3 dir = light.Direction.LengthSquared() > 1e-6f
            ? Vector3.Normalize(light.Direction)
            : new Vector3(0f, -1f, 0f);

        float outerDeg = Math.Clamp(light.SpotOuterAngle, 0.1f, 89.5f);
        float innerDeg = Math.Clamp(light.SpotInnerAngle, 0f, outerDeg);

        var linearColor = new Vector3(
            SrgbToLinearScalar(light.Color.X),
            SrgbToLinearScalar(light.Color.Y),
            SrgbToLinearScalar(light.Color.Z));

        return new GpuLightData
        {
            PositionAndType = new Vector4(light.Position, (float)light.Type),
            DirectionAndIntensity = new Vector4(dir, light.Intensity),
            ColorAndRange = new Vector4(linearColor, light.Range),
            SpotAndAreaParams = new Vector4(
                MathF.Cos(innerDeg * d2r),
                MathF.Cos(outerDeg * d2r),
                light.AreaWidth,
                light.AreaHeight),
            ShadowStartIndex = shadowStartIndex,
            ShadowCount = shadowCount,
            ShadowMode = shadowMode,
            _pad0 = 0,
            AreaRightAndSoftness = new Vector4(areaRight, light.ShadowSoftness),
        };
    }
}

/// <summary>
/// StructuredBuffer&lt;GpuShadowEntry&gt; (t4) の1要素。112 bytes。
/// row_major 4x4行列の FXC パッキング差異を根絶するため行ベクトル4本で明示格納する。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GpuShadowEntry
{
    public Vector4 LvpRow0;
    public Vector4 LvpRow1;
    public Vector4 LvpRow2;
    public Vector4 LvpRow3;
    public Vector4 AtlasUvRect;   // xy = offset [0..1], zw = scale (0..1]
    public Vector4 BiasAndParams; // x = depthBias, y = normalBiasScale, z = shadowIntensity, w = sliceIndex
    public Vector4 ExtraParams;   // DirCSM: (splitFar, blendWidth, worldUnitsPerTexel, tileRes)
                                  // Spot/Point: (nearPlane, farPlane, tanHalfFov, tileRes)
                                  // Area: (nearPlane, farPlane, tanHalfFov, lightRadiusWorld)

    public void SetLightViewProj(in Matrix4x4 m)
    {
        LvpRow0 = new Vector4(m.M11, m.M12, m.M13, m.M14);
        LvpRow1 = new Vector4(m.M21, m.M22, m.M23, m.M24);
        LvpRow2 = new Vector4(m.M31, m.M32, m.M33, m.M34);
        LvpRow3 = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }
}
