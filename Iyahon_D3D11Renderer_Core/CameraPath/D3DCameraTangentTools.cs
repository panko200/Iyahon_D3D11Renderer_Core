using System;
using System.Collections.Immutable;
using System.Numerics;
using YukkuriMovieMaker.Commons;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.CameraPath;

/// <summary>
/// 3Dビューポートから接線（制御点）を編集するための補助ツール。
/// </summary>
internal static class D3DCameraTangentTools
{
    private const long EditFrame = 0L;
    private const long EditLength = 1L;
    private const int EditFps = 30;

    public static ImmutableList<D3DCameraKeyframe> WithAutoTangents(ImmutableList<D3DCameraKeyframe> keyframes)
    {
        if (keyframes.Count == 0)
            return keyframes;

        for (var i = 0; i < keyframes.Count; i++)
        {
            var keyframe = keyframes[i];
            var tangent = D3DCameraPathSolver.EvaluateTangent(keyframe, EditFrame, EditLength, EditFps);

            if (!IsUntouched(tangent))
                continue;

            SetAutoTangent(
                keyframe,
                i > 0 ? Position(keyframes[i - 1]) : null,
                i < keyframes.Count - 1 ? Position(keyframes[i + 1]) : null);
        }

        return keyframes;
    }

    public static void SetAutoTangent(D3DCameraKeyframe keyframe, Vector3? previous, Vector3? next)
    {
        var current = Position(keyframe);

        var direction = previous.HasValue && next.HasValue
            ? next.Value - previous.Value
            : next.HasValue
                ? next.Value - current
                : previous.HasValue
                    ? current - previous.Value
                    : Vector3.Zero;

        if (direction.LengthSquared() < 1e-6f)
            return;

        var tangent = D3DCameraPathSolver.EvaluateTangent(keyframe, EditFrame, EditLength, EditFps);
        D3DCameraPathSolver.GetTangentAngles(direction, tangent.Azimuth, out var azimuth, out var elevation);

        var lengthIn = previous.HasValue ? Math.Max(1.0, (current - previous.Value).Length() / 3.0) : 0.0;
        var lengthOut = next.HasValue ? Math.Max(1.0, (next.Value - current).Length() / 3.0) : 0.0;

        if (previous is null)
            lengthIn = lengthOut;
        if (next is null)
            lengthOut = lengthIn;

        SetValue(keyframe.TangentAngleH, EditFrame, azimuth);
        SetValue(keyframe.TangentAngleV, EditFrame, elevation);
        SetValue(keyframe.TangentLengthIn, EditFrame, lengthIn);
        SetValue(keyframe.TangentLengthOut, EditFrame, lengthOut);
    }

    private static bool IsUntouched(in D3DCameraTangent tangent)
    {
        if (tangent.LengthIn == 0.0 && tangent.LengthOut == 0.0)
            return true;

        const double tolerance = 1e-6;

        return Math.Abs(tangent.LengthIn - D3DCameraPathSolver.DefaultTangentLength) < tolerance
            && Math.Abs(tangent.LengthOut - D3DCameraPathSolver.DefaultTangentLength) < tolerance
            && Math.Abs(tangent.Azimuth) < tolerance
            && Math.Abs(tangent.Elevation) < tolerance;
    }

    public static Vector3 Position(D3DCameraKeyframe keyframe)
        => D3DCameraPathSolver.EvaluateKeyframe(keyframe, EditFrame, EditLength, EditFps).Position;

    public static void SetValue(Animation animation, long frame, double value)
    {
        var keyFrames = animation.KeyFrames;

        if (keyFrames is null || keyFrames.Frames.Count == 0)
        {
            if (animation.Values.Count > 0)
                animation.Values[0].Value = value;
            return;
        }

        var index = 1;
        var nearest = double.MaxValue;

        for (var i = 0; i < keyFrames.Frames.Count; i++)
        {
            var distance = Math.Abs(frame - keyFrames.Frames[i]);
            if (distance >= nearest)
                continue;

            nearest = distance;
            index = i + 1;
        }

        if (index < animation.Values.Count)
            animation.Values[index].Value = value;
    }
}
