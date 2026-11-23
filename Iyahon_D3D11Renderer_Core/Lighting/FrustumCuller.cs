using System;
using System.Numerics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Lighting;

internal readonly struct BoundingSphere3D
{
    public readonly Vector3 Center;
    public readonly float Radius;

    public BoundingSphere3D(Vector3 center, float radius)
    {
        Center = center;
        Radius = radius;
    }
}

internal struct WorldBounds
{
    public Vector3 Min;
    public Vector3 Max;
    public BoundingSphere3D Sphere;
}

/// <summary>
/// Row-major D3D (z in [0, 1]) 視錐台6平面の抽出と球交差判定。
/// </summary>
internal readonly struct FrustumPlanes
{
    private readonly Vector4 _left;
    private readonly Vector4 _right;
    private readonly Vector4 _bottom;
    private readonly Vector4 _top;
    private readonly Vector4 _near;
    private readonly Vector4 _far;

    private FrustumPlanes(Vector4 left, Vector4 right, Vector4 bottom, Vector4 top, Vector4 near, Vector4 far)
    {
        _left = left;
        _right = right;
        _bottom = bottom;
        _top = top;
        _near = near;
        _far = far;
    }

    public static FrustumPlanes ExtractPlanes(in Matrix4x4 m)
    {
        return new FrustumPlanes(
            NormalizePlane(new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41)),
            NormalizePlane(new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41)),
            NormalizePlane(new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42)),
            NormalizePlane(new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42)),
            NormalizePlane(new Vector4(m.M13, m.M23, m.M33, m.M43)),
            NormalizePlane(new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43))
        );
    }

    private static Vector4 NormalizePlane(Vector4 p)
    {
        float lenSq = p.X * p.X + p.Y * p.Y + p.Z * p.Z;
        if (lenSq < 1e-12f) return Vector4.Zero;
        return p / MathF.Sqrt(lenSq);
    }

    public bool IntersectsSphere(in BoundingSphere3D sphere, bool skipNearPlane = false)
    {
        float r = -sphere.Radius;
        Vector3 c = sphere.Center;

        if (_left.X * c.X + _left.Y * c.Y + _left.Z * c.Z + _left.W < r) return false;
        if (_right.X * c.X + _right.Y * c.Y + _right.Z * c.Z + _right.W < r) return false;
        if (_bottom.X * c.X + _bottom.Y * c.Y + _bottom.Z * c.Z + _bottom.W < r) return false;
        if (_top.X * c.X + _top.Y * c.Y + _top.Z * c.Z + _top.W < r) return false;
        if (!skipNearPlane && (_near.X * c.X + _near.Y * c.Y + _near.Z * c.Z + _near.W < r)) return false;
        if (_far.X * c.X + _far.Y * c.Y + _far.Z * c.Z + _far.W < r) return false;

        return true;
    }

    public static bool IntersectsAabb(in Vector3 boxMin, in Vector3 boxMax, in BoundingSphere3D sphere)
    {
        Vector3 clamped = Vector3.Clamp(sphere.Center, boxMin, boxMax);
        return Vector3.DistanceSquared(clamped, sphere.Center) <= sphere.Radius * sphere.Radius;
    }
}

/// <summary>
/// 非一様スケール・反転対応の法線変換行列ユーティリティ。
/// </summary>
internal static class MatrixTransformHelper
{
    public static float ComputeDeterminant3x3(in Matrix4x4 m)
    {
        return m.M11 * (m.M22 * m.M33 - m.M23 * m.M32)
             - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31)
             + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);
    }

    /// <summary>
    /// 行ベクトル規約 v' = v * M において、法線行ベクトル n' = n * NormalMatrix を得るための逆転置行列を返す。
    /// </summary>
    public static Matrix4x4 ComputeNormalMatrix(in Matrix4x4 world)
    {
        var upper = new Matrix4x4(
            world.M11, world.M12, world.M13, 0f,
            world.M21, world.M22, world.M23, 0f,
            world.M31, world.M32, world.M33, 0f,
            0f, 0f, 0f, 1f);

        if (Matrix4x4.Invert(upper, out var inv))
        {
            return Matrix4x4.Transpose(inv);
        }

        // 縮退行列（スケール0等）の場合の安全なフォールバック
        return upper;
    }
}
