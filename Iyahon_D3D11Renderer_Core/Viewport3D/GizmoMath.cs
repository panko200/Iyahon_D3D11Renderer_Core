using System;
using System.Numerics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal enum GizmoMode
{
    Translate,
    Rotate,
    Scale,
}

internal enum GizmoSpace
{
    World,
    Local,
}

internal enum GizmoHandle
{
    None,
    MoveX,
    MoveY,
    MoveZ,
    PlaneXY,
    PlaneYZ,
    PlaneZX,
    MoveFree,
    RotateX,
    RotateY,
    RotateZ,
    ScaleX,
    ScaleY,
    ScaleZ,
    ScaleUniform,
}

internal readonly record struct PickRay(Vector3 Origin, Vector3 Direction)
{
    public Vector3? IntersectPlane(Vector3 planePoint, Vector3 planeNormal)
    {
        float denom = Vector3.Dot(Direction, planeNormal);
        if (MathF.Abs(denom) < 1e-6f) return null;
        float t = Vector3.Dot(planePoint - Origin, planeNormal) / denom;
        if (!float.IsFinite(t) || t < 0f) return null;
        return Origin + Direction * t;
    }

    public Vector3? IntersectPlaneUnbounded(Vector3 planePoint, Vector3 planeNormal)
    {
        float denom = Vector3.Dot(Direction, planeNormal);
        if (MathF.Abs(denom) < 1e-6f) return null;
        float t = Vector3.Dot(planePoint - Origin, planeNormal) / denom;
        if (!float.IsFinite(t)) return null;
        return Origin + Direction * t;
    }

    public float? ClosestParameterOnAxis(Vector3 axisOrigin, Vector3 axisDir)
    {
        Vector3 w = axisOrigin - Origin;
        float b = Vector3.Dot(axisDir, Direction);
        float denom = 1f - b * b;
        if (MathF.Abs(denom) < 1e-5f) return null;

        float d = Vector3.Dot(Direction, w);
        float c = Vector3.Dot(axisDir, w);
        float tAxis = (b * d - c) / denom;
        return float.IsFinite(tAxis) ? tAxis : null;
    }

    public bool IntersectAabb(Vector3 min, Vector3 max, out float tHit)
    {
        tHit = 0f;
        float tMin = 0f;
        float tMax = float.MaxValue;

        if (!Slab(Origin.X, Direction.X, min.X, max.X, ref tMin, ref tMax)) return false;
        if (!Slab(Origin.Y, Direction.Y, min.Y, max.Y, ref tMin, ref tMax)) return false;
        if (!Slab(Origin.Z, Direction.Z, min.Z, max.Z, ref tMin, ref tMax)) return false;

        tHit = tMin;
        return true;
    }

    public bool IntersectObb(in Matrix4x4 worldMatrix, Vector3 localMin, Vector3 localMax, out float worldDist)
    {
        worldDist = 0f;
        if (!Matrix4x4.Invert(worldMatrix, out var invWorld))
            return false;

        Vector3 localOrigin = Vector3.Transform(Origin, invWorld);
        Vector3 localDir = Vector3.TransformNormal(Direction, invWorld);
        float len = localDir.Length();
        if (len < 1e-7f) return false;

        var localRay = new PickRay(localOrigin, localDir / len);
        if (!localRay.IntersectAabb(localMin, localMax, out float localDist))
            return false;

        Vector3 worldHit = Vector3.Transform(localRay.Origin + localRay.Direction * localDist, worldMatrix);
        worldDist = Vector3.Distance(Origin, worldHit);
        return true;
    }

    private static bool Slab(float o, float d, float min, float max, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(d) < 1e-8f)
            return o >= min && o <= max;

        float inv = 1f / d;
        float t1 = (min - o) * inv;
        float t2 = (max - o) * inv;
        if (t1 > t2) (t1, t2) = (t2, t1);

        tMin = MathF.Max(tMin, t1);
        tMax = MathF.Min(tMax, t2);
        return tMin <= tMax;
    }
}

internal readonly struct TransformGizmoState
{
    public const float AxisRatio = 1.0f;
    public const float PlaneNear = 0.24f;
    public const float PlaneFar = 0.46f;
    public const float RingRadius = 0.88f;
    public const int RingSegments = 48;

    public Vector3 Origin { get; init; }
    public float Scale { get; init; }
    public Matrix4x4 Orientation { get; init; }
    public Vector3 FlipSigns { get; init; }

    public static TransformGizmoState Create(
        Vector3 origin,
        in Matrix4x4 orientation,
        ViewportCamera camera)
    {
        float dist = camera.IsOrthographic
            ? camera.Distance * camera.OrthoScale
            : Vector3.Distance(origin, camera.CameraPosition);

        float scale = MathF.Max(12f, dist * 0.16f);

        Vector3 toCam = camera.IsOrthographic
            ? -GetForward(camera)
            : (camera.CameraPosition - origin);

        Vector3 axX = Vector3.TransformNormal(Vector3.UnitX, orientation);
        Vector3 axY = Vector3.TransformNormal(Vector3.UnitY, orientation);
        Vector3 axZ = Vector3.TransformNormal(Vector3.UnitZ, orientation);

        Vector3 flips = new(
            Vector3.Dot(toCam, axX) >= 0f ? 1f : -1f,
            Vector3.Dot(toCam, axY) >= 0f ? 1f : -1f,
            Vector3.Dot(toCam, axZ) >= 0f ? 1f : -1f);

        return new TransformGizmoState
        {
            Origin = origin,
            Scale = scale,
            Orientation = orientation,
            FlipSigns = flips,
        };
    }

    private static Vector3 GetForward(ViewportCamera camera)
    {
        camera.GetCameraAxes(out _, out _, out var fwd);
        return fwd;
    }

    public Vector3 GetAxisVector(GizmoHandle handle) => handle switch
    {
        GizmoHandle.MoveX or GizmoHandle.RotateX or GizmoHandle.ScaleX => NormAxis(Vector3.UnitX),
        GizmoHandle.MoveY or GizmoHandle.RotateY or GizmoHandle.ScaleY => NormAxis(Vector3.UnitY),
        GizmoHandle.MoveZ or GizmoHandle.RotateZ or GizmoHandle.ScaleZ => NormAxis(Vector3.UnitZ),
        _ => Vector3.UnitZ,
    };

    public Vector3 GetPlaneNormal(GizmoHandle handle) => handle switch
    {
        GizmoHandle.PlaneYZ => NormAxis(Vector3.UnitX),
        GizmoHandle.PlaneZX => NormAxis(Vector3.UnitY),
        GizmoHandle.PlaneXY => NormAxis(Vector3.UnitZ),
        _ => Vector3.UnitZ,
    };

    public void GetPlaneCornersWorld(GizmoHandle plane, Span<Vector3> dst)
    {
        float sx = FlipSigns.X;
        float sy = FlipSigns.Y;
        float sz = FlipSigns.Z;

        switch (plane)
        {
            case GizmoHandle.PlaneXY:
                dst[0] = ToWorld(new Vector3(sx * PlaneNear, sy * PlaneNear, 0f));
                dst[1] = ToWorld(new Vector3(sx * PlaneFar, sy * PlaneNear, 0f));
                dst[2] = ToWorld(new Vector3(sx * PlaneFar, sy * PlaneFar, 0f));
                dst[3] = ToWorld(new Vector3(sx * PlaneNear, sy * PlaneFar, 0f));
                break;
            case GizmoHandle.PlaneYZ:
                dst[0] = ToWorld(new Vector3(0f, sy * PlaneNear, sz * PlaneNear));
                dst[1] = ToWorld(new Vector3(0f, sy * PlaneFar, sz * PlaneNear));
                dst[2] = ToWorld(new Vector3(0f, sy * PlaneFar, sz * PlaneFar));
                dst[3] = ToWorld(new Vector3(0f, sy * PlaneNear, sz * PlaneFar));
                break;
            case GizmoHandle.PlaneZX:
                dst[0] = ToWorld(new Vector3(sx * PlaneNear, 0f, sz * PlaneNear));
                dst[1] = ToWorld(new Vector3(sx * PlaneFar, 0f, sz * PlaneNear));
                dst[2] = ToWorld(new Vector3(sx * PlaneFar, 0f, sz * PlaneFar));
                dst[3] = ToWorld(new Vector3(sx * PlaneNear, 0f, sz * PlaneFar));
                break;
        }
    }

    public Vector3 ToWorld(Vector3 localNormalized)
        => Origin + Vector3.TransformNormal(localNormalized * Scale, Orientation);

    private Vector3 NormAxis(Vector3 axis)
    {
        Vector3 v = Vector3.TransformNormal(axis, Orientation);
        return v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : axis;
    }

    public GizmoHandle HitTest(Vector2 mousePx, GizmoMode mode, ViewportCamera camera, float vw, float vh)
    {
        var centerScreen = camera.WorldToScreen(Origin, vw, vh);
        if (centerScreen.HasValue && Vector2.Distance(mousePx, centerScreen.Value) <= 13f)
        {
            return mode == GizmoMode.Scale ? GizmoHandle.ScaleUniform :
                   mode == GizmoMode.Translate ? GizmoHandle.MoveFree :
                   GizmoHandle.None;
        }

        if (mode == GizmoMode.Rotate)
        {
            float bestDist = 11f;
            GizmoHandle best = GizmoHandle.None;

            CheckRing(GizmoHandle.RotateX, Vector3.UnitY, Vector3.UnitZ, mousePx, camera, vw, vh, ref bestDist, ref best);
            CheckRing(GizmoHandle.RotateY, Vector3.UnitZ, Vector3.UnitX, mousePx, camera, vw, vh, ref bestDist, ref best);
            CheckRing(GizmoHandle.RotateZ, Vector3.UnitX, Vector3.UnitY, mousePx, camera, vw, vh, ref bestDist, ref best);
            return best;
        }

        if (mode == GizmoMode.Translate)
        {
            if (HitPlane(GizmoHandle.PlaneXY, mousePx, camera, vw, vh)) return GizmoHandle.PlaneXY;
            if (HitPlane(GizmoHandle.PlaneYZ, mousePx, camera, vw, vh)) return GizmoHandle.PlaneYZ;
            if (HitPlane(GizmoHandle.PlaneZX, mousePx, camera, vw, vh)) return GizmoHandle.PlaneZX;

            float bestDist = 10f;
            GizmoHandle best = GizmoHandle.None;
            CheckAxis(GizmoHandle.MoveX, mousePx, camera, vw, vh, ref bestDist, ref best);
            CheckAxis(GizmoHandle.MoveY, mousePx, camera, vw, vh, ref bestDist, ref best);
            CheckAxis(GizmoHandle.MoveZ, mousePx, camera, vw, vh, ref bestDist, ref best);
            return best;
        }

        // Scale
        {
            float bestDist = 11f;
            GizmoHandle best = GizmoHandle.None;
            CheckAxis(GizmoHandle.ScaleX, mousePx, camera, vw, vh, ref bestDist, ref best);
            CheckAxis(GizmoHandle.ScaleY, mousePx, camera, vw, vh, ref bestDist, ref best);
            CheckAxis(GizmoHandle.ScaleZ, mousePx, camera, vw, vh, ref bestDist, ref best);
            return best;
        }
    }

    private void CheckAxis(
        GizmoHandle handle, Vector2 mousePx, ViewportCamera cam, float vw, float vh,
        ref float bestDist, ref GizmoHandle best)
    {
        var p0 = cam.WorldToScreen(Origin, vw, vh);
        var p1 = cam.WorldToScreen(Origin + GetAxisVector(handle) * (Scale * AxisRatio), vw, vh);
        if (!p0.HasValue || !p1.HasValue) return;

        float d = DistancePointToSegment(mousePx, p0.Value, p1.Value);
        if (d < bestDist)
        {
            bestDist = d;
            best = handle;
        }
    }

    private bool HitPlane(GizmoHandle plane, Vector2 mousePx, ViewportCamera cam, float vw, float vh)
    {
        Span<Vector3> worldCorners = stackalloc Vector3[4];
        GetPlaneCornersWorld(plane, worldCorners);

        Span<Vector2> screen = stackalloc Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            var sp = cam.WorldToScreen(worldCorners[i], vw, vh);
            if (!sp.HasValue) return false;
            screen[i] = sp.Value;
        }

        // Two triangles (0,1,2) and (0,2,3)
        return PointInTriangle(mousePx, screen[0], screen[1], screen[2])
            || PointInTriangle(mousePx, screen[0], screen[2], screen[3]);
    }

    private void CheckRing(
        GizmoHandle handle, Vector3 basisA, Vector3 basisB, Vector2 mousePx,
        ViewportCamera cam, float vw, float vh, ref float bestDist, ref GizmoHandle best)
    {
        Vector3 prevWorld = ToWorld(basisA * RingRadius);
        Vector2? prevScreen = cam.WorldToScreen(prevWorld, vw, vh);

        for (int i = 1; i <= RingSegments; i++)
        {
            float ang = MathF.Tau * i / RingSegments;
            Vector3 curWorld = ToWorld((basisA * MathF.Cos(ang) + basisB * MathF.Sin(ang)) * RingRadius);
            Vector2? curScreen = cam.WorldToScreen(curWorld, vw, vh);

            if (prevScreen.HasValue && curScreen.HasValue)
            {
                float d = DistancePointToSegment(mousePx, prevScreen.Value, curScreen.Value);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = handle;
                }
            }
            prevScreen = curScreen;
        }
    }

    private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-4f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = CrossSign(p, a, b);
        float d2 = CrossSign(p, b, c);
        float d3 = CrossSign(p, c, a);
        bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(hasNeg && hasPos);
    }

    private static float CrossSign(Vector2 p1, Vector2 p2, Vector2 p3)
        => (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
}

internal static class RotationEulerMath
{
    private const float Deg2Rad = MathF.PI / 180f;
    private const float Rad2Deg = 180f / MathF.PI;
    private static readonly Matrix4x4 YFlip = Matrix4x4.CreateScale(1f, -1f, 1f);

    /// <summary>
    /// DepthSortRenderer.BuildObjModelMatrix と一致するローカル回転行列:
    /// Rx(rx) * Ry(ry) * Rz(rz) (行ベクトル規約 v' = v * M)。
    /// </summary>
    public static Matrix4x4 ComposeModelLocalRotation(Vector3 deg)
    {
        return Matrix4x4.CreateRotationX(deg.X * Deg2Rad)
             * Matrix4x4.CreateRotationY(deg.Y * Deg2Rad)
             * Matrix4x4.CreateRotationZ(deg.Z * Deg2Rad);
    }

    /// <summary>
    /// Rx(rx) * Ry(ry) * Rz(rz) 行列からオイラー角 (deg) を分解する。
    /// </summary>
    public static Vector3 DecomposeModelLocalRotation(in Matrix4x4 m)
    {
        // m.M13 = -sin(ry), m.M23 = sin(rx)*cos(ry), m.M33 = cos(rx)*cos(ry)
        // m.M12 = cos(ry)*sin(rz), m.M11 = cos(ry)*cos(rz)
        float sinY = Math.Clamp(-m.M13, -1f, 1f);
        float ry = MathF.Asin(sinY);
        float cosY = MathF.Cos(ry);

        float rx, rz;
        if (MathF.Abs(cosY) > 1e-4f)
        {
            rx = MathF.Atan2(m.M23, m.M33);
            rz = MathF.Atan2(m.M12, m.M11);
        }
        else
        {
            // ジンバルロック時フォールバック: rx = 0 として rz を求める
            rx = 0f;
            rz = MathF.Atan2(-m.M21, m.M22);
        }

        return new Vector3(rx * Rad2Deg, ry * Rad2Deg, rz * Rad2Deg);
    }

    /// <summary>
    /// YMM4のアイテム回転行列 (行ベクトル規約): Rz(rz) * Ry(-ry) * Rx(-rx)。
    /// </summary>
    public static Matrix4x4 ComposeYmmRotation(Vector3 deg)
    {
        return Matrix4x4.CreateRotationZ(deg.Z * Deg2Rad)
             * Matrix4x4.CreateRotationY(-deg.Y * Deg2Rad)
             * Matrix4x4.CreateRotationX(-deg.X * Deg2Rad);
    }

    /// <summary>
    /// Rz(rz) * Ry(-ry) * Rx(-rx) から YMM4 Rotation (X, Y, Z deg) を分解する。
    /// </summary>
    public static Vector3 DecomposeYmmRotation(in Matrix4x4 m)
    {
        // R = Rz(c) * Ry(b) * Rx(a) where a = -rx, b = -ry, c = rz:
        // m.M31 = sin(b), m.M32 = -cos(b)*sin(a), m.M33 = cos(b)*cos(a)
        // m.M21 = -sin(c)*cos(b), m.M11 = cos(c)*cos(b)
        float sinB = Math.Clamp(m.M31, -1f, 1f);
        float b = MathF.Asin(sinB);
        float cosB = MathF.Cos(b);

        float a, c;
        if (MathF.Abs(cosB) > 1e-4f)
        {
            a = MathF.Atan2(-m.M32, m.M33);
            c = MathF.Atan2(-m.M21, m.M11);
        }
        else
        {
            a = 0f;
            c = MathF.Atan2(m.M12, m.M22);
        }

        return new Vector3(-a * Rad2Deg, -b * Rad2Deg, c * Rad2Deg);
    }

    public static Matrix4x4 WorldDeltaToModelLocalDelta(in Matrix4x4 worldRotDelta, in Matrix4x4 itemRot)
    {
        Matrix4x4 basis = YFlip * itemRot;
        Matrix4x4 invBasis = Matrix4x4.Transpose(itemRot) * YFlip;
        return basis * worldRotDelta * invBasis;
    }

    public static Matrix4x4 Orthonormalize(in Matrix4x4 m)
    {
        Vector3 x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        Vector3 z = Vector3.Normalize(Vector3.Cross(x, new Vector3(m.M21, m.M22, m.M23)));
        Vector3 y = Vector3.Cross(z, x);
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);
    }
}
