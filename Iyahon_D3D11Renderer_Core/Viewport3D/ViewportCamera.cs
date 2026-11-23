using System;
using System.Numerics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal enum ViewportCameraPreset
{
    Perspective,
    Front,
    Back,
    Right,
    Left,
    Top,
    Bottom,
}

/// <summary>
/// 3Dビューポート用フリーカメラ。
/// YMM4座標系（X+ = 右, Y+ = 下, Z+ = カメラ手前）に完全一致。
/// デフォルト状態 (Target=(0,0,0), Distance=1000, Yaw=0, Pitch=0) は
/// YMM4の標準カメラ (0, 0, +1000) と同一投影になる。
/// </summary>
internal sealed class ViewportCamera
{
    private const float RotateSensitivity = 0.0055f;
    private const float MinDistance = 20f;
    private const float MaxDistance = 200000f;

    public Vector3 Target { get; set; } = Vector3.Zero;
    public float Distance { get; set; } = 1000f;
    public float Yaw { get; set; } = 0f;
    public float Pitch { get; set; } = 0f;
    public float Roll { get; set; } = 0f;

    public bool IsOrthographic { get; set; } = false;
    public float OrthoScale { get; set; } = 1f;
    public float FovDegrees { get; private set; } = ComputeYmmVerticalFovDegrees(1080);

    public Vector3 CameraPosition => Target + GetCameraOffset();

    public static float ComputeYmmVerticalFovDegrees(int videoHeight)
    {
        float h = Math.Max(64, videoHeight) * 0.5f;
        return 2f * MathF.Atan(h / 1000f) * (180f / MathF.PI);
    }

    public void SetVideoHeight(int videoHeight)
    {
        FovDegrees = ComputeYmmVerticalFovDegrees(videoHeight);
    }

    public void Reset(int videoHeight = 1080)
    {
        Target = Vector3.Zero;
        Distance = 1000f;
        Yaw = 0f;
        Pitch = 0f;
        Roll = 0f;
        OrthoScale = 1f;
        IsOrthographic = false;
        SetVideoHeight(videoHeight);
    }

    public void Orbit(float deltaX, float deltaY)
    {
        Yaw -= deltaX * RotateSensitivity;
        Pitch -= deltaY * RotateSensitivity;
        float limit = MathF.PI * 0.5f - 0.01f;
        Pitch = Math.Clamp(Pitch, -limit, limit);
    }

    public void AddRoll(float delta)
    {
        Roll -= delta * RotateSensitivity;
    }

    public void Pan(float deltaX, float deltaY, float viewportW, float viewportH)
    {
        GetCameraAxes(out var right, out var up, out _);
        float vh = MathF.Max(1f, viewportH);

        float worldHeightAtTarget = IsOrthographic
            ? MathF.Max(10f, Distance * OrthoScale)
            : 2f * Distance * MathF.Tan((FovDegrees * MathF.PI / 180f) * 0.5f);

        float unitsPerPx = worldHeightAtTarget / vh;
        Target -= right * (deltaX * unitsPerPx);
        Target += up * (deltaY * unitsPerPx);
    }

    public void Zoom(float wheelDelta)
    {
        float factor = MathF.Pow(0.999f, wheelDelta);
        Distance = Math.Clamp(Distance * factor, MinDistance, MaxDistance);
        OrthoScale = Math.Clamp(OrthoScale * factor, 0.02f, 50f);
    }

    public void FocusOn(Vector3 center, float radius)
    {
        Target = center;
        float r = MathF.Max(40f, radius);
        float halfFov = (FovDegrees * MathF.PI / 180f) * 0.5f;
        float dist = (r * 1.4f) / MathF.Max(0.1f, MathF.Tan(halfFov));
        Distance = Math.Clamp(dist, 80f, MaxDistance * 0.5f);
        OrthoScale = Math.Clamp((r * 2.4f) / Distance, 0.05f, 20f);
    }

    public void SetPreset(ViewportCameraPreset preset)
    {
        Roll = 0f;
        switch (preset)
        {
            case ViewportCameraPreset.Perspective:
                Yaw = 0.48f;
                Pitch = -0.32f;
                IsOrthographic = false;
                break;
            case ViewportCameraPreset.Front:
                Yaw = 0f;
                Pitch = 0f;
                IsOrthographic = true;
                break;
            case ViewportCameraPreset.Back:
                Yaw = MathF.PI;
                Pitch = 0f;
                IsOrthographic = true;
                break;
            case ViewportCameraPreset.Right:
                Yaw = MathF.PI * 0.5f;
                Pitch = 0f;
                IsOrthographic = true;
                break;
            case ViewportCameraPreset.Left:
                Yaw = -MathF.PI * 0.5f;
                Pitch = 0f;
                IsOrthographic = true;
                break;
            case ViewportCameraPreset.Top:
                Yaw = 0f;
                Pitch = -(MathF.PI * 0.5f - 0.01f);
                IsOrthographic = true;
                break;
            case ViewportCameraPreset.Bottom:
                Yaw = 0f;
                Pitch = MathF.PI * 0.5f - 0.01f;
                IsOrthographic = true;
                break;
        }
    }

    /// <summary>
    /// YMM4の Camera 行列（カメラ空間原点がZ=+1000、視線は-Z、上は-Y）を
    /// ViewportCamera の Target/Distance/Yaw/Pitch/Roll にそのまま同期する。
    /// </summary>
    public void MatchYmmCamera(in Matrix4x4 ymmCameraMatrix, int videoHeight)
    {
        SetVideoHeight(videoHeight);
        IsOrthographic = false;
        OrthoScale = 1f;

        if (!Matrix4x4.Invert(ymmCameraMatrix, out var invCam))
        {
            Reset(videoHeight);
            return;
        }

        Vector3 eyeWorld = Vector3.Transform(new Vector3(0f, 0f, 1000f), invCam);
        Vector3 targetWorld = Vector3.Transform(Vector3.Zero, invCam);
        Vector3 upWorld = Vector3.TransformNormal(new Vector3(0f, -1f, 0f), invCam);
        upWorld = upWorld.LengthSquared() > 1e-8f ? Vector3.Normalize(upWorld) : new Vector3(0f, -1f, 0f);

        Vector3 offset = eyeWorld - targetWorld;
        float dist = offset.Length();
        if (dist < 1e-3f)
        {
            Reset(videoHeight);
            return;
        }

        Target = targetWorld;
        Distance = Math.Clamp(dist, MinDistance, MaxDistance);

        Vector3 dir = offset / dist;
        float limit = MathF.PI * 0.5f - 0.01f;
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(dir.Y, -1f, 1f)), -limit, limit);
        Yaw = MathF.Atan2(dir.X, dir.Z);
        Roll = 0f;

        GetCameraAxes(out var baseRight, out var baseUp, out _);
        float cosR = Vector3.Dot(upWorld, baseUp);
        float sinR = -Vector3.Dot(upWorld, baseRight);
        Roll = MathF.Atan2(sinR, cosR);
    }

    public Vector3 GetCameraOffset()
    {
        float cosP = MathF.Cos(Pitch);
        return new Vector3(
            Distance * MathF.Sin(Yaw) * cosP,
            Distance * MathF.Sin(Pitch),
            Distance * MathF.Cos(Yaw) * cosP);
    }

    public void GetCameraAxes(out Vector3 right, out Vector3 up, out Vector3 forward)
    {
        Vector3 eye = CameraPosition;
        Vector3 dir = Target - eye;
        forward = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : new Vector3(0f, 0f, -1f);

        Vector3 worldUp = new(0f, -1f, 0f);
        Vector3 crossRight = Vector3.Cross(worldUp, forward);
        if (crossRight.LengthSquared() < 1e-8f)
            crossRight = Vector3.Cross(new Vector3(0f, 0f, -1f), forward);

        right = Vector3.Normalize(crossRight);
        up = Vector3.Normalize(Vector3.Cross(forward, right));

        if (MathF.Abs(Roll) > 1e-6f)
        {
            var rollMat = Matrix4x4.CreateFromAxisAngle(forward, Roll);
            right = Vector3.Normalize(Vector3.TransformNormal(right, rollMat));
            up = Vector3.Normalize(Vector3.TransformNormal(up, rollMat));
        }
    }

    public Matrix4x4 GetViewMatrix()
    {
        Vector3 eye = CameraPosition;
        GetCameraAxes(out var right, out var up, out var forward);

        return new Matrix4x4(
            right.X, up.X, forward.X, 0f,
            right.Y, up.Y, forward.Y, 0f,
            right.Z, up.Z, forward.Z, 0f,
            -Vector3.Dot(right, eye), -Vector3.Dot(up, eye), -Vector3.Dot(forward, eye), 1f);
    }

    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        float aspect = MathF.Max(0.05f, aspectRatio);
        float nearZ = Math.Clamp(Distance * 0.005f, 2f, 200f);
        float farZ = MathF.Max(100000f, Distance * 50f);

        if (IsOrthographic)
        {
            float height = MathF.Max(10f, Distance * OrthoScale);
            float width = height * aspect;
            float range = 1f / (farZ - nearZ);
            return new Matrix4x4(
                2f / width, 0f, 0f, 0f,
                0f, 2f / height, 0f, 0f,
                0f, 0f, range, 0f,
                0f, 0f, -nearZ * range, 1f);
        }
        else
        {
            float fovRad = FovDegrees * MathF.PI / 180f;
            float h = 1f / MathF.Tan(fovRad * 0.5f);
            float w = h / aspect;
            float range = farZ / (farZ - nearZ);
            return new Matrix4x4(
                w, 0f, 0f, 0f,
                0f, h, 0f, 0f,
                0f, 0f, range, 1f,
                0f, 0f, -range * nearZ, 0f);
        }
    }

    public Matrix4x4 GetViewProjectionMatrix(float aspectRatio)
        => GetViewMatrix() * GetProjectionMatrix(aspectRatio);

    public PickRay ScreenToRay(float screenX, float screenY, float viewportW, float viewportH)
    {
        float vw = MathF.Max(1f, viewportW);
        float vh = MathF.Max(1f, viewportH);
        float ndcX = (screenX / vw) * 2f - 1f;
        float ndcY = 1f - (screenY / vh) * 2f;

        GetCameraAxes(out var right, out var up, out var forward);
        float aspect = vw / vh;

        if (IsOrthographic)
        {
            float height = MathF.Max(10f, Distance * OrthoScale);
            float width = height * aspect;
            Vector3 origin = CameraPosition
                + right * (ndcX * width * 0.5f)
                + up * (ndcY * height * 0.5f);
            return new PickRay(origin, forward);
        }
        else
        {
            float tanHalfFovY = MathF.Tan((FovDegrees * MathF.PI / 180f) * 0.5f);
            float tanHalfFovX = tanHalfFovY * aspect;
            Vector3 dir = Vector3.Normalize(
                forward
                + right * (ndcX * tanHalfFovX)
                + up * (ndcY * tanHalfFovY));
            return new PickRay(CameraPosition, dir);
        }
    }

    public Vector2? WorldToScreen(Vector3 worldPos, float viewportW, float viewportH)
    {
        float vw = MathF.Max(1f, viewportW);
        float vh = MathF.Max(1f, viewportH);
        var vp = GetViewProjectionMatrix(vw / vh);
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1f), vp);
        if (clip.W <= 1e-4f) return null;

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        return new Vector2(
            (ndcX * 0.5f + 0.5f) * vw,
            (0.5f - ndcY * 0.5f) * vh);
    }
}
