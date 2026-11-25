using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

public enum BezierCurveInterpolation
{
    [Display(Name = "三次ベジェ曲線 (Cubic)")]
    CubicBezier = 0,

    [Display(Name = "二次ベジェ曲線 (Quadratic)")]
    QuadraticBezier = 1,

    [Display(Name = "滑らか自動補間 (Catmull-Rom)")]
    CatmullRom = 2,

    [Display(Name = "折れ線 (Linear)")]
    Linear = 3,
}

public enum BezierCurveLineStyle
{
    [Display(Name = "立体チューブ (3D)")]
    Tube3D = 0,

    [Display(Name = "平らな帯 (Flat Ribbon)")]
    FlatRibbon = 1,
}

public enum BezierCurveCapStyle
{
    [Display(Name = "四角・平ら (Flat)")]
    Flat = 0,

    [Display(Name = "円・丸み (Round)")]
    Round = 1,

    [Display(Name = "円錐・先端 (Cone)")]
    Cone = 2,

    [Display(Name = "なし (開放端)")]
    None = 3,
}

/// <summary>
/// 3Dベジェ曲線の制御点（アンカー＋前後ハンドル＋太さ・捻り）。
/// 座標は YMM4 ワールドピクセル相当のローカル座標。
/// </summary>
public sealed class BezierCurvePoint3D
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Size { get; set; } = 18f;

    public float HandleInX { get; set; }
    public float HandleInY { get; set; }
    public float HandleInZ { get; set; }

    public float HandleOutX { get; set; }
    public float HandleOutY { get; set; }
    public float HandleOutZ { get; set; }

    public float TiltDeg { get; set; }
    public bool AutoSmooth { get; set; } = true;

    [JsonIgnore]
    public Vector3 Position
    {
        get => new(X, Y, Z);
        set { X = value.X; Y = value.Y; Z = value.Z; }
    }

    [JsonIgnore]
    public Vector3 HandleIn
    {
        get => new(HandleInX, HandleInY, HandleInZ);
        set { HandleInX = value.X; HandleInY = value.Y; HandleInZ = value.Z; }
    }

    [JsonIgnore]
    public Vector3 HandleOut
    {
        get => new(HandleOutX, HandleOutY, HandleOutZ);
        set { HandleOutX = value.X; HandleOutY = value.Y; HandleOutZ = value.Z; }
    }

    public BezierCurvePoint3D Clone() => new()
    {
        X = X,
        Y = Y,
        Z = Z,
        Size = Size,
        HandleInX = HandleInX,
        HandleInY = HandleInY,
        HandleInZ = HandleInZ,
        HandleOutX = HandleOutX,
        HandleOutY = HandleOutY,
        HandleOutZ = HandleOutZ,
        TiltDeg = TiltDeg,
        AutoSmooth = AutoSmooth,
    };
}

/// <summary>
/// 3Dベジェ曲線の制御点リストと JSON シリアライズ。
/// </summary>
public sealed class BezierCurve3DData
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public List<BezierCurvePoint3D> Points { get; set; } = new();

    public static BezierCurve3DData CreateDefault()
    {
        var data = new BezierCurve3DData();
        data.Points.Add(new BezierCurvePoint3D { X = -160f, Y = 35f, Z = 0f, Size = 18f, AutoSmooth = true });
        data.Points.Add(new BezierCurvePoint3D { X = 0f, Y = -65f, Z = 55f, Size = 24f, AutoSmooth = true });
        data.Points.Add(new BezierCurvePoint3D { X = 160f, Y = 35f, Z = 0f, Size = 18f, AutoSmooth = true });
        data.AutoComputeHandles(onlyZero: false, closed: false);
        return data;
    }

    public BezierCurve3DData Clone()
    {
        var copy = new BezierCurve3DData();
        for (int i = 0; i < Points.Count; i++)
            copy.Points.Add(Points[i].Clone());
        return copy;
    }

    public void AutoComputeHandles(bool onlyZero, bool closed = false)
    {
        int n = Points.Count;
        if (n < 2) return;

        for (int i = 0; i < n; i++)
        {
            var pt = Points[i];
            if (onlyZero && pt.HandleIn.LengthSquared() > 1e-4f && pt.HandleOut.LengthSquared() > 1e-4f)
                continue;

            int iPrev = i - 1;
            int iNext = i + 1;
            if (closed)
            {
                iPrev = (iPrev + n) % n;
                iNext %= n;
            }

            Vector3 prev = iPrev >= 0 ? Points[iPrev].Position : pt.Position;
            Vector3 next = iNext < n ? Points[iNext].Position : pt.Position;

            Vector3 dir;
            if (closed || (i > 0 && i < n - 1))
                dir = next - prev;
            else if (i == 0)
                dir = next - pt.Position;
            else
                dir = pt.Position - prev;

            if (dir.LengthSquared() < 1e-6f)
                dir = Vector3.UnitX * 50f;
            else
                dir = Vector3.Normalize(dir);

            float lenIn = (closed || i > 0) ? MathF.Max(15f, Vector3.Distance(pt.Position, prev) * 0.30f) : 40f;
            float lenOut = (closed || i < n - 1) ? MathF.Max(15f, Vector3.Distance(next, pt.Position) * 0.30f) : 40f;

            pt.HandleIn = -dir * lenIn;
            pt.HandleOut = dir * lenOut;
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOpts);

    public static bool TryDeserialize(string? json, out BezierCurve3DData data)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<BezierCurve3DData>(json, JsonOpts);
                if (parsed != null && parsed.Points.Count > 0)
                {
                    data = parsed;
                    return true;
                }
            }
            catch { }
        }
        data = CreateDefault();
        return false;
    }
}
