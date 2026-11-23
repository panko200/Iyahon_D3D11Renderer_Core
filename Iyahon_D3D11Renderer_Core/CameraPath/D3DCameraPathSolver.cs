using System;
using System.Collections.Generic;
using System.Numerics;
using YukkuriMovieMaker.Commons;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.CameraPath;

/// <summary>
/// ある1フレーム時点でのカメラ状態。
/// Position / Rotation は YMM4 標準カメラと同じ意味を持つ (Position.Z+: 奥)。
/// </summary>
internal readonly struct D3DCameraState
{
    public readonly Vector3 Position;
    public readonly Vector3 Rotation;
    public readonly float Fov;

    public D3DCameraState(Vector3 position, Vector3 rotation, float fov)
    {
        Position = position;
        Rotation = rotation;
        Fov = fov;
    }

    public static D3DCameraState Default => new(Vector3.Zero, Vector3.Zero, (float)D3DCameraPathSolver.DefaultFovDegrees);
}

internal readonly record struct D3DCameraTangent(
    double Azimuth, double Elevation, double LengthIn, double LengthOut);

internal readonly record struct D3DCameraPathSettings(
    D3DCameraCurveType CurveType,
    float CurveRoundness,
    float AngleOvershoot,
    D3DCameraTimeControl TimeControl,
    float TimeSmoothness,
    EasingType EasingType,
    EasingMode EasingMode,
    BezierAnimation? TimeBezier,
    bool Reverse,
    bool LookAt);

/// <summary>
/// カメラパスの補間計算・行列生成・LookAt逆算・サンプリングを行うソルバー。
/// </summary>
internal static class D3DCameraPathSolver
{
    public const double DefaultFovDegrees = 56.7380926;
    public const double MinFovDegrees = 0.1;
    public const double MaxFovDegrees = 179.9;
    public const double DefaultTangentLength = 100.0;
    public const double DefaultLookAtDepth = 1000.0;
    public const float BasePerspectiveDistance = 1000f;

    public static Matrix4x4 BuildCameraMatrix(in D3DCameraState state)
    {
        const float degToRad = MathF.PI / 180f;

        // YMM4 / D3D11 内部では Z+ がカメラ手前のため保存座標の Z を反転する
        var position = new Vector3(state.Position.X, state.Position.Y, -state.Position.Z);
        var pivot = new Vector3(0f, 0f, BasePerspectiveDistance);

        return Matrix4x4.CreateTranslation(-position)
             * Matrix4x4.CreateRotationY(state.Rotation.Y * degToRad, pivot)
             * Matrix4x4.CreateRotationX(state.Rotation.X * degToRad, pivot)
             * Matrix4x4.CreateRotationZ(state.Rotation.Z * degToRad, pivot);
    }

    public static void GetView(in D3DCameraState state, out Vector3 eye, out Vector3 right, out Vector3 up, out Vector3 forward)
    {
        var camera = BuildCameraMatrix(state);
        if (!Matrix4x4.Invert(camera, out var inverse))
            inverse = Matrix4x4.Identity;

        eye = Vector3.Transform(new Vector3(0f, 0f, BasePerspectiveDistance), inverse);
        right = Vector3.TransformNormal(Vector3.UnitX, inverse);
        up = Vector3.TransformNormal(new Vector3(0f, -1f, 0f), inverse);
        forward = Vector3.TransformNormal(new Vector3(0f, 0f, -1f), inverse);

        if (right.LengthSquared() > 1e-12f) right = Vector3.Normalize(right);
        if (up.LengthSquared() > 1e-12f) up = Vector3.Normalize(up);
        if (forward.LengthSquared() > 1e-12f) forward = Vector3.Normalize(forward);
    }

    public static float CalculatePerspectiveDistance(double fovDegrees, int screenHeight)
    {
        if (!double.IsFinite(fovDegrees))
            fovDegrees = DefaultFovDegrees;

        fovDegrees = Math.Clamp(fovDegrees, 0.0, MaxFovDegrees);

        if (fovDegrees < MinFovDegrees)
            return float.PositiveInfinity;

        if (screenHeight <= 0)
            screenHeight = 1080;

        var distance = screenHeight / 2.0 / Math.Tan(fovDegrees * Math.PI / 360.0);
        return !double.IsFinite(distance) || distance <= 0.0 || distance > float.MaxValue
            ? BasePerspectiveDistance
            : (float)distance;
    }

    private struct RawState
    {
        public double Frame;
        public double X, Y, Z;
        public double RotationX, RotationY, RotationZ;
        public double Fov;
        public double TangentAngleH, TangentAngleV, TangentLengthIn, TangentLengthOut;
        public double TargetX, TargetY, TargetZ;
        public BezierAnimation? SegmentBezier;

        public static RawState From(D3DCameraKeyframe keyframe, long frame, long length, int fps) => new()
        {
            Frame = keyframe.Frame.GetValue(frame, length, fps),
            X = keyframe.X.GetValue(frame, length, fps),
            Y = keyframe.Y.GetValue(frame, length, fps),
            Z = keyframe.Z.GetValue(frame, length, fps),
            RotationX = keyframe.RotationX.GetValue(frame, length, fps),
            RotationY = keyframe.RotationY.GetValue(frame, length, fps),
            RotationZ = keyframe.RotationZ.GetValue(frame, length, fps),
            Fov = keyframe.Fov.GetValue(frame, length, fps),
            TangentAngleH = keyframe.TangentAngleH.GetValue(frame, length, fps),
            TangentAngleV = keyframe.TangentAngleV.GetValue(frame, length, fps),
            TangentLengthIn = keyframe.TangentLengthIn.GetValue(frame, length, fps),
            TangentLengthOut = keyframe.TangentLengthOut.GetValue(frame, length, fps),
            TargetX = keyframe.TargetX.GetValue(frame, length, fps),
            TargetY = keyframe.TargetY.GetValue(frame, length, fps),
            TargetZ = keyframe.TargetZ.GetValue(frame, length, fps),
            SegmentBezier = keyframe.SegmentTimeBezier,
        };

        public readonly Vector3 Position => new((float)X, (float)Y, (float)Z);
        public readonly Vector3 Target => new((float)TargetX, (float)TargetY, (float)TargetZ);
        public readonly D3DCameraTangent Tangent => new(
            TangentAngleH, TangentAngleV, TangentLengthIn, TangentLengthOut);

        public readonly D3DCameraState ToState() => new(
            new Vector3((float)X, (float)Y, (float)Z),
            new Vector3((float)RotationX, (float)RotationY, (float)RotationZ),
            (float)Fov);
    }

    public static D3DCameraState Evaluate(
        IReadOnlyList<D3DCameraKeyframe> keyframes,
        in D3DCameraPathSettings settings,
        long frame,
        long length,
        int fps)
    {
        if (keyframes.Count == 0)
            return D3DCameraState.Default;

        var raw = new RawState[keyframes.Count];
        for (var i = 0; i < raw.Length; i++)
            raw[i] = RawState.From(keyframes[i], frame, length, fps);

        if (raw.Length == 1)
        {
            var single = raw[0].ToState();
            if (settings.LookAt)
                single = ApplyLookAt(single, raw[0].Target);

            return Sanitize(single);
        }

        var order = BuildOrder(raw, settings.TimeControl);
        GetSegment(raw, order, settings, frame, length, out var segment, out var t);

        var roundness = settings.CurveType == D3DCameraCurveType.Bezier
            ? Math.Clamp(settings.CurveRoundness, 0f, 10f) / 5f
            : 0f;

        var isBezierPath = settings.CurveType.UsesTangent();

        var position = isBezierPath
            ? BezierPosition(raw, order, segment, t, settings.CurveType)
            : new Vector3(
                (float)Channel(BuildTrack(raw, order, roundness, double.PositiveInfinity, static r => r.X), segment, t),
                (float)Channel(BuildTrack(raw, order, roundness, double.PositiveInfinity, static r => r.Y), segment, t),
                (float)Channel(BuildTrack(raw, order, roundness, double.PositiveInfinity, static r => r.Z), segment, t));

        var scalarRoundness = isBezierPath ? 1f : roundness;
        var overshoot = Math.Max(0.0, settings.AngleOvershoot);

        var state = new D3DCameraState(
            position,
            new Vector3(
                (float)Channel(BuildTrack(raw, order, scalarRoundness, overshoot, static r => r.RotationX), segment, t),
                (float)Channel(BuildTrack(raw, order, scalarRoundness, overshoot, static r => r.RotationY), segment, t),
                (float)Channel(BuildTrack(raw, order, scalarRoundness, overshoot, static r => r.RotationZ), segment, t)),
            (float)Channel(BuildTrack(raw, order, scalarRoundness, overshoot, static r => r.Fov), segment, t));

        if (settings.LookAt)
        {
            var target = new Vector3(
                (float)Channel(BuildTrack(raw, order, scalarRoundness, double.PositiveInfinity, static r => r.TargetX), segment, t),
                (float)Channel(BuildTrack(raw, order, scalarRoundness, double.PositiveInfinity, static r => r.TargetY), segment, t),
                (float)Channel(BuildTrack(raw, order, scalarRoundness, double.PositiveInfinity, static r => r.TargetZ), segment, t));

            state = ApplyLookAt(state, target);
        }

        return Sanitize(state);
    }

    private static D3DCameraState ApplyLookAt(in D3DCameraState state, Vector3 target)
    {
        if (!TryGetLookAngles(state.Position, target, out var pitch, out var yaw))
            return state;

        return new D3DCameraState(
            state.Position,
            new Vector3((float)pitch, (float)yaw, state.Rotation.Z),
            state.Fov);
    }

    public static bool TryGetLookAngles(Vector3 position, Vector3 target, out double pitch, out double yaw)
    {
        const double radToDeg = 180.0 / Math.PI;
        pitch = 0.0;
        yaw = 0.0;

        var dx = (double)target.X - position.X;
        var dy = (double)target.Y - position.Y;
        var dz = (double)position.Z - target.Z - BasePerspectiveDistance;

        var squared = dx * dx + dy * dy + dz * dz;
        if (!(squared > 1e-9) || !double.IsFinite(squared))
            return false;

        var length = Math.Sqrt(squared);
        var ndx = dx / length;
        var ndy = Math.Clamp(dy / length, -1.0, 1.0);
        var ndz = dz / length;

        pitch = -Math.Asin(ndy) * radToDeg;
        yaw = Math.Atan2(ndx, -ndz) * radToDeg;

        return double.IsFinite(pitch) && double.IsFinite(yaw);
    }

    public static D3DCameraState EvaluateKeyframe(D3DCameraKeyframe keyframe, long frame, long length, int fps)
        => Sanitize(RawState.From(keyframe, frame, length, fps).ToState());

    public static Vector3 GetTangentDirection(double azimuthDegrees, double elevationDegrees)
    {
        const double degToRad = Math.PI / 180.0;

        var azimuth = azimuthDegrees * degToRad;
        var elevation = elevationDegrees * degToRad;
        var cosElevation = Math.Cos(elevation);

        return new Vector3(
            (float)(cosElevation * Math.Cos(azimuth)),
            (float)(-Math.Sin(elevation)),
            (float)(cosElevation * Math.Sin(azimuth)));
    }

    public static void GetTangentAngles(Vector3 direction, double fallbackAzimuth, out double azimuth, out double elevation)
    {
        const double radToDeg = 180.0 / Math.PI;

        if (direction.LengthSquared() < 1e-9f)
        {
            azimuth = fallbackAzimuth;
            elevation = 0.0;
            return;
        }

        var directionUnit = Vector3.Normalize(direction);
        var sinElevation = (double)Math.Clamp(-directionUnit.Y, -1f, 1f);
        elevation = Math.Asin(sinElevation) * radToDeg;

        var cosElevation = Math.Sqrt(Math.Max(0.0, 1.0 - sinElevation * sinElevation));
        azimuth = cosElevation < 1e-4
            ? fallbackAzimuth
            : Math.Atan2(directionUnit.Z, directionUnit.X) * radToDeg;
    }

    public static D3DCameraTangent EvaluateTangent(D3DCameraKeyframe keyframe, long frame, long length, int fps)
        => RawState.From(keyframe, frame, length, fps).Tangent;

    public static Vector3 EvaluateTarget(D3DCameraKeyframe keyframe, long frame, long length, int fps)
        => RawState.From(keyframe, frame, length, fps).Target;

    /// <summary>
    /// 保存座標（X=右, Y=下, Z=奥）の座標・向きを D3D11 ワールド座標（Z=手前）へ変換する。
    /// </summary>
    public static Vector3 ToPreviewSpace(Vector3 savedPosOrDir)
        => new(savedPosOrDir.X, savedPosOrDir.Y, -savedPosOrDir.Z);

    private static Vector3 BezierPosition(
        RawState[] raw,
        int[] order,
        int segment,
        float t,
        D3DCameraCurveType curveType)
    {
        var count = order.Length;
        var first = raw[order[segment]];
        var second = raw[order[Math.Min(segment + 1, count - 1)]];

        var start = first.Position;
        var end = second.Position;
        var outControl = OutControlPoint(first);
        var inControl = InControlPoint(second);
        var u = 1f - t;

        if (curveType == D3DCameraCurveType.QuadraticBezier)
        {
            var control = (outControl + inControl) * 0.5f;
            return start * (u * u) + control * (2f * u * t) + end * (t * t);
        }

        return start * (u * u * u)
             + outControl * (3f * u * u * t)
             + inControl * (3f * u * t * t)
             + end * (t * t * t);
    }

    private static Vector3 OutControlPoint(in RawState raw)
        => raw.Position + GetTangentDirection(raw.TangentAngleH, raw.TangentAngleV) * (float)raw.TangentLengthOut;

    private static Vector3 InControlPoint(in RawState raw)
        => raw.Position - GetTangentDirection(raw.TangentAngleH, raw.TangentAngleV) * (float)raw.TangentLengthIn;

    private static D3DCameraState Sanitize(in D3DCameraState state)
    {
        static float Fix(float value) => float.IsFinite(value) ? value : 0f;

        var fov = Fix(state.Fov);
        if (fov < MinFovDegrees || fov > MaxFovDegrees)
            fov = (float)DefaultFovDegrees;

        return new D3DCameraState(
            new Vector3(Fix(state.Position.X), Fix(state.Position.Y), Fix(state.Position.Z)),
            new Vector3(Fix(state.Rotation.X), Fix(state.Rotation.Y), Fix(state.Rotation.Z)),
            fov);
    }

    private static int[] BuildOrder(RawState[] raw, D3DCameraTimeControl timeControl)
    {
        var order = new int[raw.Length];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;

        if (timeControl is D3DCameraTimeControl.KeyframeTime or D3DCameraTimeControl.SegmentBezier)
            Array.Sort(order, (a, b) => raw[a].Frame.CompareTo(raw[b].Frame));

        return order;
    }

    private static void GetSegment(
        RawState[] raw,
        int[] order,
        in D3DCameraPathSettings settings,
        long frame,
        long length,
        out int segment,
        out float t)
    {
        var count = order.Length;

        if (settings.TimeControl is D3DCameraTimeControl.KeyframeTime or D3DCameraTimeControl.SegmentBezier)
        {
            var first = raw[order[0]].Frame;
            var last = raw[order[count - 1]].Frame;

            if (last - first <= 0.0)
            {
                segment = 0;
                t = frame >= first ? 1f : 0f;
                return;
            }

            if (frame <= first) { segment = 0; t = 0f; return; }
            if (frame >= last) { segment = count - 2; t = 1f; return; }

            for (var i = 0; i < count - 1; i++)
            {
                var from = raw[order[i]].Frame;
                var to = raw[order[i + 1]].Frame;
                if (frame < from || frame > to)
                    continue;

                segment = i;
                var local = to - from <= 0.0 ? 1.0 : (frame - from) / (to - from);
                t = settings.TimeControl == D3DCameraTimeControl.SegmentBezier
                    ? SegmentProgress(raw[order[i]].SegmentBezier, (float)local)
                    : SmoothInterpolate((float)local, settings.TimeSmoothness);
                return;
            }

            segment = count - 2;
            t = 1f;
            return;
        }

        var total = Math.Max(1f, length - 1);
        var rate = Math.Clamp((float)frame / total, 0f, 1f);
        if (settings.Reverse)
            rate = 1f - rate;

        var progress = settings.TimeControl == D3DCameraTimeControl.Easing
            ? Easing.GetValue(settings.EasingType, settings.EasingMode, rate)
            : settings.TimeBezier?.GetAnimation(rate) ?? rate;

        progress = Math.Clamp(progress, 0.0, 1.0);

        var position = progress * (count - 1);
        var index = Math.Clamp((int)Math.Floor(position), 0, count - 2);
        segment = index;
        t = (float)Math.Clamp(position - index, 0.0, 1.0);
    }

    private static float SegmentProgress(BezierAnimation? animation, float local)
    {
        var rate = Math.Clamp(local, 0f, 1f);
        if (animation is null)
            return rate;

        var progress = animation.GetAnimation(rate);
        return double.IsFinite(progress) ? (float)Math.Clamp(progress, 0.0, 1.0) : rate;
    }

    private readonly struct ChannelTrack
    {
        public readonly double[] Values;
        public readonly double[] Tangents;
        public readonly bool Linear;

        public ChannelTrack(double[] values, double[] tangents, bool linear)
        {
            Values = values;
            Tangents = tangents;
            Linear = linear;
        }
    }

    private static ChannelTrack BuildTrack(
        RawState[] raw,
        int[] order,
        float roundness,
        double overshoot,
        Func<RawState, double> select)
    {
        var count = order.Length;
        var values = new double[count];
        for (var i = 0; i < count; i++)
            values[i] = select(raw[order[i]]);

        if (roundness <= 0f || count < 2)
            return new ChannelTrack(values, [], true);

        var natural = new double[count];
        var limited = new double[count];

        for (var i = 0; i < count; i++)
        {
            var prev = i - 1 >= 0 ? values[i - 1] : 2.0 * values[i] - values[i + 1];
            var next = i + 1 < count ? values[i + 1] : 2.0 * values[i] - values[i - 1];

            natural[i] = roundness * (next - prev) * 0.5;
            limited[i] = LimitTangent(prev, values[i], next, natural[i]);
        }

        if (!double.IsFinite(overshoot))
            return new ChannelTrack(values, natural, false);

        var weight = 0.0;
        if (WithinOvershoot(values, natural, limited, 1.0, overshoot))
        {
            weight = 1.0;
        }
        else if (overshoot > 0.0)
        {
            var low = 0.0;
            var high = 1.0;
            for (var i = 0; i < 20; i++)
            {
                var middle = (low + high) * 0.5;
                if (WithinOvershoot(values, natural, limited, middle, overshoot))
                    low = middle;
                else
                    high = middle;
            }
            weight = low;
        }

        if (weight >= 1.0)
            return new ChannelTrack(values, natural, false);

        var tangents = new double[count];
        for (var i = 0; i < count; i++)
            tangents[i] = limited[i] + (natural[i] - limited[i]) * weight;

        return new ChannelTrack(values, tangents, false);
    }

    private static double LimitTangent(double prev, double current, double next, double tangent)
    {
        var before = current - prev;
        var after = next - current;

        if (before * after <= 0.0)
            return 0.0;

        var limit = 3.0 * Math.Min(Math.Abs(before), Math.Abs(after));

        return after > 0.0
            ? Math.Clamp(tangent, 0.0, limit)
            : Math.Clamp(tangent, -limit, 0.0);
    }

    private static bool WithinOvershoot(
        double[] values,
        double[] natural,
        double[] limited,
        double weight,
        double overshoot)
    {
        for (var i = 0; i < values.Length - 1; i++)
        {
            var m0 = limited[i] + (natural[i] - limited[i]) * weight;
            var m1 = limited[i + 1] + (natural[i + 1] - limited[i + 1]) * weight;

            GetCurveRange(values[i], values[i + 1], m0, m1, out var low, out var high);

            if (high > Math.Max(values[i], values[i + 1]) + overshoot + 1e-9)
                return false;

            if (low < Math.Min(values[i], values[i + 1]) - overshoot - 1e-9)
                return false;
        }

        return true;
    }

    private static void GetCurveRange(
        double from, double to, double m0, double m1, out double low, out double high)
    {
        low = Math.Min(from, to);
        high = Math.Max(from, to);

        var delta = to - from;
        var c2 = 3.0 * delta - 2.0 * m0 - m1;
        var c3 = -2.0 * delta + m0 + m1;

        foreach (var root in QuadraticRoots(3.0 * c3, 2.0 * c2, m0))
        {
            if (!(root > 0.0 && root < 1.0))
                continue;

            var value = Hermite(from, to, m0, m1, root);
            if (!double.IsFinite(value))
                continue;

            low = Math.Min(low, value);
            high = Math.Max(high, value);
        }
    }

    private static IEnumerable<double> QuadraticRoots(double a, double b, double c)
    {
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) > 1e-12)
                yield return -c / b;

            yield break;
        }

        var discriminant = b * b - 4.0 * a * c;
        if (discriminant < 0.0)
            yield break;

        var sqrt = Math.Sqrt(discriminant);
        yield return (-b + sqrt) / (2.0 * a);
        yield return (-b - sqrt) / (2.0 * a);
    }

    private static double Channel(ChannelTrack track, int segment, float t)
    {
        var count = track.Values.Length;
        var index2 = Math.Min(segment + 1, count - 1);

        var from = track.Values[segment];
        var to = track.Values[index2];

        if (track.Linear || track.Tangents.Length != count)
            return from + (to - from) * t;

        return Hermite(from, to, track.Tangents[segment], track.Tangents[index2], t);
    }

    private static double Hermite(double from, double to, double m0, double m1, double t)
    {
        double t2 = t * t;
        double t3 = t2 * t;

        return (2 * t3 - 3 * t2 + 1) * from
             + (t3 - 2 * t2 + t) * m0
             + (-2 * t3 + 3 * t2) * to
             + (t3 - t2) * m1;
    }

    private static float SmoothInterpolate(float t, float smoothness)
    {
        if (smoothness <= 0f)
            return t;

        var factor = Math.Clamp(smoothness, 0f, 10f) / 10f;
        var smooth = t * t * (3f - 2f * t);
        return t + (smooth - t) * factor;
    }

    public static void GetFrameRange(
        IReadOnlyList<D3DCameraKeyframe> keyframes,
        D3DCameraTimeControl timeControl,
        long length,
        out long start,
        out long end)
    {
        if ((timeControl is D3DCameraTimeControl.KeyframeTime or D3DCameraTimeControl.SegmentBezier)
            && keyframes.Count > 0)
        {
            double min = double.MaxValue;
            double max = double.MinValue;
            foreach (var keyframe in keyframes)
            {
                var value = keyframe.Frame.GetValue(0L, 1L, 30);
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }

            start = (long)Math.Floor(min);
            end = (long)Math.Ceiling(max);
            if (end <= start)
                end = start + 1;
            return;
        }

        start = 0;
        end = Math.Max(1, length - 1);
    }

    public static void SamplePath(
        IReadOnlyList<D3DCameraKeyframe> keyframes,
        in D3DCameraPathSettings settings,
        long length,
        int fps,
        int sampleCount,
        List<D3DCameraState> destination)
    {
        destination.Clear();

        if (keyframes.Count == 0)
        {
            destination.Add(D3DCameraState.Default);
            return;
        }

        GetFrameRange(keyframes, settings.TimeControl, length, out var start, out var end);
        var count = Math.Max(2, sampleCount);

        for (var i = 0; i < count; i++)
        {
            var frame = start + (long)Math.Round((end - start) * (i / (double)(count - 1)));
            destination.Add(Evaluate(keyframes, settings, frame, length, fps));
        }
    }
}
