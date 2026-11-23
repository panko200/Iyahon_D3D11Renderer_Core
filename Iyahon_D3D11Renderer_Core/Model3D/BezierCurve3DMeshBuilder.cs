using System;
using System.Collections.Generic;
using System.Numerics;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

/// <summary>
/// 3Dベジェ曲線データからスイープ済み EditableMesh（チューブ / 平帯 ＋ 始終端キャップ）を生成するビルダー。
/// </summary>
internal static class BezierCurve3DMeshBuilder
{
    private readonly struct CurveSample
    {
        public Vector3 Position { get; init; }
        public Vector3 Tangent { get; init; }
        public float Size { get; init; }
        public float TiltRad { get; init; }
        public float Distance { get; init; }
    }

    public static EditableMesh BuildMesh(
        BezierCurve3DData data,
        BezierCurveInterpolation interpolation,
        BezierCurveLineStyle lineStyle,
        int sides,
        int segmentsPerSpan,
        bool closed,
        BezierCurveCapStyle startCap,
        BezierCurveCapStyle endCap,
        bool smoothShading,
        EditableMaterial material)
    {
        var mesh = new EditableMesh();
        mesh.Materials.Add(material?.Clone() ?? new EditableMaterial { Name = "Material" });

        if (data.Points.Count < 2)
        {
            mesh.RebuildEdges(false);
            mesh.RecalculateNormals();
            return mesh;
        }

        int ringSides = Math.Clamp(sides, 3, 64);
        int stepsPerSpan = Math.Clamp(segmentsPerSpan, 2, 64);

        var samples = SampleCurve(data.Points, interpolation, stepsPerSpan, closed);
        if (samples.Count < 2)
        {
            mesh.RebuildEdges(false);
            mesh.RecalculateNormals();
            return mesh;
        }

        ComputeParallelTransportFrames(samples, closed, out var normals, out var binormals);

        if (lineStyle == BezierCurveLineStyle.FlatRibbon)
        {
            BuildRibbonMesh(mesh, samples, normals, binormals, closed, startCap, endCap, smoothShading);
        }
        else
        {
            BuildTubeMesh(mesh, samples, normals, binormals, ringSides, closed, startCap, endCap, smoothShading);
        }

        mesh.RebuildEdges(false);
        mesh.RecalculateNormals();
        return mesh;
    }

    private static List<CurveSample> SampleCurve(
        IReadOnlyList<BezierCurvePoint3D> pts,
        BezierCurveInterpolation interp,
        int stepsPerSpan,
        bool closed)
    {
        int n = pts.Count;
        int spanCount = closed ? n : n - 1;
        var list = new List<CurveSample>(spanCount * stepsPerSpan + 1);
        float accumDist = 0f;
        Vector3 prevPos = pts[0].Position;

        for (int s = 0; s < spanCount; s++)
        {
            int i1 = s;
            int i2 = (s + 1) % n;
            int i0 = closed ? (s - 1 + n) % n : Math.Max(0, s - 1);
            int i3 = closed ? (s + 2) % n : Math.Min(n - 1, s + 2);

            var p1 = pts[i1];
            var p2 = pts[i2];
            Vector3 pos0 = i0 == i1 ? (p1.Position - (p2.Position - p1.Position)) : pts[i0].Position;
            Vector3 pos3 = i3 == i2 ? (p2.Position + (p2.Position - p1.Position)) : pts[i3].Position;

            int endStep = (!closed && s == spanCount - 1) ? stepsPerSpan : stepsPerSpan - 1;
            for (int k = 0; k <= endStep; k++)
            {
                float t = k / (float)stepsPerSpan;
                EvaluateSpan(
                    p1, p2, pos0, pos3, interp, t,
                    out var pos, out var tan, out float size, out float tiltDeg);

                if (list.Count > 0)
                    accumDist += Vector3.Distance(prevPos, pos);
                prevPos = pos;

                if (tan.LengthSquared() < 1e-8f)
                {
                    Vector3 fallback = p2.Position - p1.Position;
                    tan = fallback.LengthSquared() > 1e-8f ? Vector3.Normalize(fallback) : Vector3.UnitX;
                }
                else
                {
                    tan = Vector3.Normalize(tan);
                }

                list.Add(new CurveSample
                {
                    Position = pos,
                    Tangent = tan,
                    Size = MathF.Max(0.25f, size),
                    TiltRad = tiltDeg * (MathF.PI / 180f),
                    Distance = accumDist,
                });
            }
        }

        return list;
    }

    private static void EvaluateSpan(
        BezierCurvePoint3D a,
        BezierCurvePoint3D b,
        Vector3 posPrev,
        Vector3 posNext,
        BezierCurveInterpolation interp,
        float t,
        out Vector3 pos,
        out Vector3 tangent,
        out float size,
        out float tiltDeg)
    {
        Vector3 p0 = a.Position;
        Vector3 p3 = b.Position;
        float u = 1f - t;

        switch (interp)
        {
            case BezierCurveInterpolation.Linear:
                pos = Vector3.Lerp(p0, p3, t);
                tangent = p3 - p0;
                break;

            case BezierCurveInterpolation.QuadraticBezier:
            {
                Vector3 c = ((p0 + a.HandleOut) + (p3 + b.HandleIn)) * 0.5f;
                pos = p0 * (u * u) + c * (2f * u * t) + p3 * (t * t);
                tangent = (c - p0) * (2f * u) + (p3 - c) * (2f * t);
                break;
            }

            case BezierCurveInterpolation.CatmullRom:
            {
                float t2 = t * t;
                float t3 = t2 * t;
                pos = 0.5f * ((2f * p0)
                    + (-posPrev + p3) * t
                    + (2f * posPrev - 5f * p0 + 4f * p3 - posNext) * t2
                    + (-posPrev + 3f * p0 - 3f * p3 + posNext) * t3);
                tangent = 0.5f * ((-posPrev + p3)
                    + 2f * (2f * posPrev - 5f * p0 + 4f * p3 - posNext) * t
                    + 3f * (-posPrev + 3f * p0 - 3f * p3 + posNext) * t2);
                break;
            }

            default: // CubicBezier
            {
                Vector3 c1 = p0 + a.HandleOut;
                Vector3 c2 = p3 + b.HandleIn;
                pos = p0 * (u * u * u)
                    + c1 * (3f * u * u * t)
                    + c2 * (3f * u * t * t)
                    + p3 * (t * t * t);
                tangent = (c1 - p0) * (3f * u * u)
                    + (c2 - c1) * (6f * u * t)
                    + (p3 - c2) * (3f * t * t);
                break;
            }
        }

        // Smoothstep-like interpolation feels natural for tube radius transitions
        float sSmooth = t * t * (3f - 2f * t);
        size = a.Size + (b.Size - a.Size) * sSmooth;
        tiltDeg = a.TiltDeg + (b.TiltDeg - a.TiltDeg) * t;
    }

    private static void ComputeParallelTransportFrames(
        List<CurveSample> samples,
        bool closed,
        out Vector3[] normals,
        out Vector3[] binormals)
    {
        int count = samples.Count;
        normals = new Vector3[count];
        binormals = new Vector3[count];

        Vector3 t0 = samples[0].Tangent;
        Vector3 upRef = MathF.Abs(t0.Y) < 0.92f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 b0 = Vector3.Cross(t0, upRef);
        if (b0.LengthSquared() < 1e-8f) b0 = Vector3.Cross(t0, Vector3.UnitZ);
        b0 = Vector3.Normalize(b0);
        Vector3 n0 = Vector3.Normalize(Vector3.Cross(b0, t0));

        normals[0] = n0;
        binormals[0] = b0;

        for (int i = 1; i < count; i++)
        {
            Vector3 tPrev = samples[i - 1].Tangent;
            Vector3 tCur = samples[i].Tangent;
            Vector3 axis = Vector3.Cross(tPrev, tCur);
            float axisLen = axis.Length();
            Vector3 n = normals[i - 1];

            if (axisLen > 1e-6f)
            {
                axis /= axisLen;
                float dot = Math.Clamp(Vector3.Dot(tPrev, tCur), -1f, 1f);
                float angle = MathF.Acos(dot);
                n = Vector3.TransformNormal(n, Matrix4x4.CreateFromAxisAngle(axis, angle));
            }

            // Gram-Schmidt re-orthogonalize against tCur
            n -= tCur * Vector3.Dot(n, tCur);
            n = n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : normals[i - 1];
            normals[i] = n;
            binormals[i] = Vector3.Normalize(Vector3.Cross(tCur, n));
        }

        if (closed && count > 2)
        {
            // Also transport from last sample back to sample 0 to measure loop closing twist
            Vector3 tLast = samples[count - 1].Tangent;
            Vector3 axisClose = Vector3.Cross(tLast, t0);
            Vector3 nEnd = normals[count - 1];
            if (axisClose.LengthSquared() > 1e-12f)
            {
                axisClose = Vector3.Normalize(axisClose);
                float dotClose = Math.Clamp(Vector3.Dot(tLast, t0), -1f, 1f);
                nEnd = Vector3.TransformNormal(nEnd, Matrix4x4.CreateFromAxisAngle(axisClose, MathF.Acos(dotClose)));
            }
            nEnd -= t0 * Vector3.Dot(nEnd, t0);
            if (nEnd.LengthSquared() > 1e-8f)
            {
                nEnd = Vector3.Normalize(nEnd);
                float sinA = Vector3.Dot(Vector3.Cross(nEnd, n0), t0);
                float cosA = Math.Clamp(Vector3.Dot(nEnd, n0), -1f, 1f);
                float twistErr = MathF.Atan2(sinA, cosA);

                for (int i = 0; i < count; i++)
                {
                    float frac = i / (float)count;
                    float corr = twistErr * frac;
                    if (MathF.Abs(corr) > 1e-6f)
                    {
                        var rot = Matrix4x4.CreateFromAxisAngle(samples[i].Tangent, corr);
                        normals[i] = Vector3.Normalize(Vector3.TransformNormal(normals[i], rot));
                        binormals[i] = Vector3.Normalize(Vector3.Cross(samples[i].Tangent, normals[i]));
                    }
                }
            }
        }

        // Apply per-point TiltDeg
        for (int i = 0; i < count; i++)
        {
            float tilt = samples[i].TiltRad;
            if (MathF.Abs(tilt) > 1e-6f)
            {
                var rot = Matrix4x4.CreateFromAxisAngle(samples[i].Tangent, tilt);
                normals[i] = Vector3.Normalize(Vector3.TransformNormal(normals[i], rot));
                binormals[i] = Vector3.Normalize(Vector3.Cross(samples[i].Tangent, normals[i]));
            }
        }
    }

    private static void BuildTubeMesh(
        EditableMesh mesh,
        List<CurveSample> samples,
        Vector3[] normals,
        Vector3[] binormals,
        int sides,
        bool closed,
        BezierCurveCapStyle startCap,
        BezierCurveCapStyle endCap,
        bool smoothShading)
    {
        int nSamples = samples.Count;
        float totalDist = MathF.Max(1f, samples[^1].Distance);
        var ringBaseVert = new int[nSamples];

        for (int i = 0; i < nSamples; i++)
        {
            ringBaseVert[i] = mesh.Vertices.Count;
            var s = samples[i];
            float vCoord = s.Distance / totalDist;
            for (int k = 0; k < sides; k++)
            {
                float a = MathF.Tau * k / sides;
                Vector3 radial = normals[i] * MathF.Cos(a) + binormals[i] * MathF.Sin(a);
                Vector3 pos = s.Position + radial * s.Size;
                mesh.Vertices.Add(new EditableVertex
                {
                    Position = pos,
                    Normal = radial,
                    UV = new Vector2(k / (float)sides, vCoord),
                });
            }
        }

        void ConnectRings(int rA, int rB)
        {
            for (int k = 0; k < sides; k++)
            {
                int kNext = (k + 1) % sides;
                int v00 = rA + k;
                int v01 = rA + kNext;
                int v11 = rB + kNext;
                int v10 = rB + k;
                mesh.Faces.Add(new EditableFace
                {
                    VertexIndices = new List<int> { v00, v01, v11, v10 },
                    FaceUVs = new List<Vector2>
                    {
                        mesh.Vertices[v00].UV,
                        mesh.Vertices[v01].UV,
                        mesh.Vertices[v11].UV,
                        mesh.Vertices[v10].UV,
                    },
                    MaterialIndex = 0,
                    Smooth = smoothShading,
                });
            }
        }

        for (int i = 0; i < nSamples - 1; i++)
            ConnectRings(ringBaseVert[i], ringBaseVert[i + 1]);

        if (closed)
        {
            ConnectRings(ringBaseVert[nSamples - 1], ringBaseVert[0]);
            return;
        }

        AddTubeCap(mesh, samples[0], normals[0], binormals[0], ringBaseVert[0], sides, isStart: true, startCap, smoothShading);
        AddTubeCap(mesh, samples[^1], normals[^1], binormals[^1], ringBaseVert[^1], sides, isStart: false, endCap, smoothShading);
    }

    private static void AddTubeCap(
        EditableMesh mesh,
        in CurveSample sample,
        Vector3 n,
        Vector3 b,
        int baseRingIdx,
        int sides,
        bool isStart,
        BezierCurveCapStyle capStyle,
        bool smoothShading)
    {
        if (capStyle == BezierCurveCapStyle.None)
            return;

        Vector3 outDir = isStart ? -sample.Tangent : sample.Tangent;

        if (capStyle == BezierCurveCapStyle.Flat)
        {
            var faceVerts = new List<int>(sides);
            var faceUvs = new List<Vector2>(sides);
            for (int k = 0; k < sides; k++)
            {
                int idx = baseRingIdx + (isStart ? (sides - 1 - k) : k);
                faceVerts.Add(idx);
                float a = MathF.Tau * (isStart ? (sides - 1 - k) : k) / sides;
                faceUvs.Add(new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)));
            }
            mesh.Faces.Add(new EditableFace
            {
                VertexIndices = faceVerts,
                FaceUVs = faceUvs,
                MaterialIndex = 0,
                Smooth = false,
            });
            return;
        }

        if (capStyle == BezierCurveCapStyle.Cone)
        {
            int apexIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new EditableVertex
            {
                Position = sample.Position + outDir * (sample.Size * 2.0f),
                Normal = outDir,
                UV = new Vector2(0.5f, 0.5f),
            });

            for (int k = 0; k < sides; k++)
            {
                int kNext = (k + 1) % sides;
                int v0 = baseRingIdx + k;
                int v1 = baseRingIdx + kNext;
                mesh.Faces.Add(new EditableFace
                {
                    VertexIndices = isStart
                        ? new List<int> { apexIdx, v1, v0 }
                        : new List<int> { v0, v1, apexIdx },
                    MaterialIndex = 0,
                    Smooth = smoothShading,
                });
            }
            return;
        }

        // Round (hemisphere): 4 latitude rings shrinking to pole
        const int capRings = 4;
        int prevRing = baseRingIdx;
        for (int r = 1; r < capRings; r++)
        {
            float phi = (MathF.PI * 0.5f) * (r / (float)capRings);
            float radialScale = MathF.Cos(phi) * sample.Size;
            float axialOffset = MathF.Sin(phi) * sample.Size;
            int curRing = mesh.Vertices.Count;

            for (int k = 0; k < sides; k++)
            {
                float a = MathF.Tau * k / sides;
                Vector3 radDir = n * MathF.Cos(a) + b * MathF.Sin(a);
                Vector3 pos = sample.Position + outDir * axialOffset + radDir * radialScale;
                Vector3 norm = Vector3.Normalize(radDir * MathF.Cos(phi) + outDir * MathF.Sin(phi));
                mesh.Vertices.Add(new EditableVertex
                {
                    Position = pos,
                    Normal = norm,
                    UV = new Vector2(k / (float)sides, r / (float)capRings),
                });
            }

            for (int k = 0; k < sides; k++)
            {
                int kNext = (k + 1) % sides;
                int p0 = prevRing + k;
                int p1 = prevRing + kNext;
                int c0 = curRing + k;
                int c1 = curRing + kNext;
                mesh.Faces.Add(new EditableFace
                {
                    VertexIndices = isStart
                        ? new List<int> { p1, p0, c0, c1 }
                        : new List<int> { p0, p1, c1, c0 },
                    MaterialIndex = 0,
                    Smooth = smoothShading,
                });
            }
            prevRing = curRing;
        }

        int poleIdx = mesh.Vertices.Count;
        mesh.Vertices.Add(new EditableVertex
        {
            Position = sample.Position + outDir * sample.Size,
            Normal = outDir,
            UV = new Vector2(0.5f, 1f),
        });
        for (int k = 0; k < sides; k++)
        {
            int kNext = (k + 1) % sides;
            int p0 = prevRing + k;
            int p1 = prevRing + kNext;
            mesh.Faces.Add(new EditableFace
            {
                VertexIndices = isStart
                    ? new List<int> { poleIdx, p1, p0 }
                    : new List<int> { p0, p1, poleIdx },
                MaterialIndex = 0,
                Smooth = smoothShading,
            });
        }
    }

    private static void BuildRibbonMesh(
        EditableMesh mesh,
        List<CurveSample> samples,
        Vector3[] normals,
        Vector3[] binormals,
        bool closed,
        BezierCurveCapStyle startCap,
        BezierCurveCapStyle endCap,
        bool smoothShading)
    {
        int nSamples = samples.Count;
        float totalDist = MathF.Max(1f, samples[^1].Distance);

        var leftVerts = new int[nSamples];
        var rightVerts = new int[nSamples];

        for (int i = 0; i < nSamples; i++)
        {
            var s = samples[i];
            float vCoord = s.Distance / totalDist;
            Vector3 offset = binormals[i] * s.Size;

            leftVerts[i] = mesh.Vertices.Count;
            mesh.Vertices.Add(new EditableVertex
            {
                Position = s.Position - offset,
                Normal = normals[i],
                UV = new Vector2(0f, vCoord),
            });

            rightVerts[i] = mesh.Vertices.Count;
            mesh.Vertices.Add(new EditableVertex
            {
                Position = s.Position + offset,
                Normal = normals[i],
                UV = new Vector2(1f, vCoord),
            });
        }

        int segCount = closed ? nSamples : nSamples - 1;
        for (int i = 0; i < segCount; i++)
        {
            int next = (i + 1) % nSamples;
            int l0 = leftVerts[i], r0 = rightVerts[i];
            int l1 = leftVerts[next], r1 = rightVerts[next];
            mesh.Faces.Add(new EditableFace
            {
                VertexIndices = new List<int> { l0, r0, r1, l1 },
                FaceUVs = new List<Vector2>
                {
                    mesh.Vertices[l0].UV,
                    mesh.Vertices[r0].UV,
                    mesh.Vertices[r1].UV,
                    mesh.Vertices[l1].UV,
                },
                MaterialIndex = 0,
                Smooth = smoothShading,
            });
        }

        if (closed) return;

        AddRibbonCap(mesh, samples[0], normals[0], binormals[0], leftVerts[0], rightVerts[0], isStart: true, startCap, smoothShading);
        AddRibbonCap(mesh, samples[^1], normals[^1], binormals[^1], leftVerts[^1], rightVerts[^1], isStart: false, endCap, smoothShading);
    }

    private static void AddRibbonCap(
        EditableMesh mesh,
        in CurveSample sample,
        Vector3 n,
        Vector3 b,
        int leftIdx,
        int rightIdx,
        bool isStart,
        BezierCurveCapStyle capStyle,
        bool smoothShading)
    {
        if (capStyle is BezierCurveCapStyle.Flat or BezierCurveCapStyle.None)
            return;

        Vector3 outDir = isStart ? -sample.Tangent : sample.Tangent;

        if (capStyle == BezierCurveCapStyle.Cone)
        {
            int apexIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new EditableVertex
            {
                Position = sample.Position + outDir * (sample.Size * 2.0f),
                Normal = n,
                UV = new Vector2(0.5f, isStart ? 0f : 1f),
            });
            mesh.Faces.Add(new EditableFace
            {
                VertexIndices = isStart
                    ? new List<int> { apexIdx, rightIdx, leftIdx }
                    : new List<int> { leftIdx, rightIdx, apexIdx },
                MaterialIndex = 0,
                Smooth = smoothShading,
            });
            return;
        }

        // Round cap on ribbon: semicircular fan from center
        const int arcSegs = 8;
        int centerIdx = mesh.Vertices.Count;
        mesh.Vertices.Add(new EditableVertex
        {
            Position = sample.Position,
            Normal = n,
            UV = new Vector2(0.5f, isStart ? 0f : 1f),
        });

        var arcVerts = new int[arcSegs + 1];
        arcVerts[0] = rightIdx;
        arcVerts[arcSegs] = leftIdx;
        for (int k = 1; k < arcSegs; k++)
        {
            float a = MathF.PI * (k / (float)arcSegs);
            Vector3 p = sample.Position + (b * MathF.Cos(a) + outDir * MathF.Sin(a)) * sample.Size;
            arcVerts[k] = mesh.Vertices.Count;
            mesh.Vertices.Add(new EditableVertex
            {
                Position = p,
                Normal = n,
                UV = new Vector2(0.5f + 0.5f * MathF.Cos(a), isStart ? 0f : 1f),
            });
        }

        for (int k = 0; k < arcSegs; k++)
        {
            int a = arcVerts[k];
            int c = arcVerts[k + 1];
            mesh.Faces.Add(new EditableFace
            {
                VertexIndices = isStart
                    ? new List<int> { centerIdx, c, a }
                    : new List<int> { centerIdx, a, c },
                MaterialIndex = 0,
                Smooth = smoothShading,
            });
        }
    }
}
