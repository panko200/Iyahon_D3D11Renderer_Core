using System;
using System.Numerics;
using System.Threading;

#nullable enable
namespace Iyahon_D3D11Renderer_Core;

/// <summary>
/// Blender 準拠（右手系 Z-Up: +X=右, +Y=画面奥, +Z=上）と
/// YMM4/D3D11 内部ワールド座標（+X=右, +Y=下, +Z=手前）およびモデルローカル座標（+X=右, +Y=上, +Z=手前）
/// の相互変換・回転合成・直交化ヘルパー。
/// すべて行ベクトル規約 (v' = v * M) を用いる。
/// </summary>
internal static class BlenderAxisMath
{
    private const float Deg2Rad = MathF.PI / 180f;
    private const float Rad2Deg = 180f / MathF.PI;

    /// <summary>
    /// Blender 軸 → 内部ワールド座標の基底変換行列 (row0=+X, row1=+Y(奥=-Z_W), row2=+Z(上=-Y_W))。
    /// 対称行列かつ自己逆行列 (det = -1)。
    /// </summary>
    public static readonly Matrix4x4 BlenderWorldBasis = new(
        1f, 0f, 0f, 0f,
        0f, 0f, -1f, 0f,
        0f, -1f, 0f, 0f,
        0f, 0f, 0f, 1f);

    /// <summary>
    /// Blender ローカル軸 → モデルローカル軸 (+X=右, +Y=上, +Z=手前) への変換行列 (det = +1)。
    /// BlenderLocalToModelLocal * YFlip == BlenderWorldBasis を満たす。
    /// </summary>
    public static readonly Matrix4x4 BlenderLocalToModelLocal = new(
        1f, 0f, 0f, 0f,
        0f, 0f, -1f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 0f, 1f);

    /// <summary>
    /// モデルローカル軸 → Blender ローカル軸への逆変換行列 (BlenderLocalToModelLocal の転置)。
    /// </summary>
    public static readonly Matrix4x4 ModelLocalToBlenderLocal = new(
        1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, -1f, 0f, 0f,
        0f, 0f, 0f, 1f);

    public static Vector3 BlenderToWorld(Vector3 v) => new(v.X, -v.Z, -v.Y);

    public static Vector3 WorldToBlender(Vector3 v) => new(v.X, -v.Z, -v.Y);

    public static Vector3 BlenderToModelLocal(Vector3 v) => new(v.X, v.Z, -v.Y);

    public static Vector3 ModelLocalToBlender(Vector3 v) => new(v.X, -v.Z, v.Y);

    public static Vector3 SwapAbsYZ(Vector3 v) => new(v.X, v.Z, v.Y);

    /// <summary>
    /// Blender ローカル回転 (X, Y, Z deg) からモデルローカル回転行列 (Rx(+rx) * Rz(-ry) * Ry(+rz)) を合成する。
    /// </summary>
    public static Matrix4x4 ComposeModelLocalRotationBlender(Vector3 deg)
    {
        return Matrix4x4.CreateRotationX(deg.X * Deg2Rad)
             * Matrix4x4.CreateRotationZ(-deg.Y * Deg2Rad)
             * Matrix4x4.CreateRotationY(deg.Z * Deg2Rad);
    }

    /// <summary>
    /// モデルローカル回転行列 (Rx(a) * Rz(b) * Ry(c), a=rx, b=-ry, c=rz) から Blender ローカル回転角 (deg) を分解する。
    /// </summary>
    public static Vector3 DecomposeModelLocalRotationBlender(in Matrix4x4 m)
    {
        float sinB = Math.Clamp(m.M12, -1f, 1f);
        float b = MathF.Asin(sinB);
        float cosB = MathF.Cos(b);

        float a, c;
        if (MathF.Abs(cosB) > 1e-4f)
        {
            a = MathF.Atan2(-m.M32, m.M22);
            c = MathF.Atan2(-m.M13, m.M11);
        }
        else
        {
            a = 0f;
            c = MathF.Atan2(m.M31, m.M33);
        }

        return new Vector3(a * Rad2Deg, -b * Rad2Deg, c * Rad2Deg);
    }

    /// <summary>
    /// Blender アイテム回転 (X, Y, Z deg) から内部ワールド空間での回転行列
    /// YFlip * ComposeModelLocalRotationBlender(deg) * YFlip = Rx(-rx) * Rz(+ry) * Ry(+rz) を合成する。
    /// </summary>
    public static Matrix4x4 ComposeItemRotationBlender(Vector3 deg)
    {
        return Matrix4x4.CreateRotationX(-deg.X * Deg2Rad)
             * Matrix4x4.CreateRotationZ(deg.Y * Deg2Rad)
             * Matrix4x4.CreateRotationY(deg.Z * Deg2Rad);
    }

    /// <summary>
    /// 内部ワールド空間のアイテム回転行列 (Rx(a) * Rz(b) * Ry(c), a=-rx, b=ry, c=rz) から
    /// Blender 回転角 (deg) を分解する。
    /// </summary>
    public static Vector3 DecomposeItemRotationBlender(in Matrix4x4 m)
    {
        float sinB = Math.Clamp(m.M12, -1f, 1f);
        float b = MathF.Asin(sinB);
        float cosB = MathF.Cos(b);

        float a, c;
        if (MathF.Abs(cosB) > 1e-4f)
        {
            a = MathF.Atan2(-m.M32, m.M22);
            c = MathF.Atan2(-m.M13, m.M11);
        }
        else
        {
            a = 0f;
            c = MathF.Atan2(m.M31, m.M33);
        }

        return new Vector3(-a * Rad2Deg, b * Rad2Deg, c * Rad2Deg);
    }

    /// <summary>
    /// 鏡映 (det = -1) を含む基底行列のカイラリティ（掌性）を維持したまま Gram-Schmidt 直交化する。
    /// </summary>
    public static Matrix4x4 OrthonormalizePreserveHandedness(in Matrix4x4 m)
    {
        Vector3 r0 = new(m.M11, m.M12, m.M13);
        Vector3 r1 = new(m.M21, m.M22, m.M23);
        Vector3 r2 = new(m.M31, m.M32, m.M33);

        Vector3 x = r0.LengthSquared() > 1e-8f ? Vector3.Normalize(r0) : Vector3.UnitX;

        Vector3 yOrtho = r1 - x * Vector3.Dot(x, r1);
        Vector3 y;
        if (yOrtho.LengthSquared() > 1e-8f)
        {
            y = Vector3.Normalize(yOrtho);
        }
        else
        {
            Vector3 hint = MathF.Abs(x.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
            y = Vector3.Normalize(Vector3.Cross(hint, x));
        }

        Vector3 zOrtho = r2 - x * Vector3.Dot(x, r2) - y * Vector3.Dot(y, r2);
        Vector3 z = zOrtho.LengthSquared() > 1e-8f
            ? Vector3.Normalize(zOrtho)
            : Vector3.Cross(x, y);

        return new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    public static Vector3 CameraPosBlenderToYmmSaved(Vector3 blenderPos)
        => new(blenderPos.X, -blenderPos.Z, blenderPos.Y);

    public static Vector3 CameraPosYmmSavedToBlender(Vector3 ymmSavedPos)
        => new(ymmSavedPos.X, ymmSavedPos.Z, -ymmSavedPos.Y);

    public static Vector3 CameraRotBlenderToYmmSaved(Vector3 blenderRot)
        => new(blenderRot.X, -blenderRot.Z, -blenderRot.Y);

    public static Vector3 CameraRotYmmSavedToBlender(Vector3 ymmRot)
        => new(ymmRot.X, -ymmRot.Z, -ymmRot.Y);
}

/// <summary>
/// 現在のフレーム評価中に Blender 軸が有効かどうかをスレッド・AsyncLocal スコープで伝達するコンテキスト。
/// </summary>
internal static class BlenderAxisContext
{
    private static readonly AsyncLocal<bool?> _scopedUseBlenderAxis = new();
    private static bool _lastMainUseBlenderAxis;

    public static bool IsBlenderAxisActive
        => _scopedUseBlenderAxis.Value ?? Volatile.Read(ref _lastMainUseBlenderAxis);

    public static bool? PushScope(bool useBlender, bool isMainTimeline)
    {
        bool? prev = _scopedUseBlenderAxis.Value;
        _scopedUseBlenderAxis.Value = useBlender;
        if (isMainTimeline)
        {
            Volatile.Write(ref _lastMainUseBlenderAxis, useBlender);
        }
        return prev;
    }

    public static void PopScope(bool? prev)
    {
        _scopedUseBlenderAxis.Value = prev;
    }
}
