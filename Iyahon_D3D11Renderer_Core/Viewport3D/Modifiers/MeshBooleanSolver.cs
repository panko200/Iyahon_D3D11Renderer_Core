using System;
using System.Collections.Generic;
using System.Numerics;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Modifiers;

/// <summary>
/// 三角形メッシュ用の高速 BVH (最近接点 / レイキャスト / 交差クエリ / Winding Number 内外判定)。
/// Boolean モディファイアと Shrinkwrap モディファイアで共用する。
/// </summary>
internal sealed class TriangleBvh
{
    internal readonly struct TriData
    {
        public readonly Vector3 P0;
        public readonly Vector3 P1;
        public readonly Vector3 P2;
        public readonly Vector2 UV0;
        public readonly Vector2 UV1;
        public readonly Vector2 UV2;
        public readonly Vector3 Normal;
        public readonly int MaterialIndex;
        public readonly bool Smooth;
        public readonly Vector3 AabbMin;
        public readonly Vector3 AabbMax;
        public readonly Vector3 Centroid;

        public TriData(
            Vector3 p0, Vector3 p1, Vector3 p2,
            Vector2 uv0, Vector2 uv1, Vector2 uv2,
            int materialIndex, bool smooth)
        {
            P0 = p0; P1 = p1; P2 = p2;
            UV0 = uv0; UV1 = uv1; UV2 = uv2;
            MaterialIndex = materialIndex;
            Smooth = smooth;
            Vector3 c = Vector3.Cross(p1 - p0, p2 - p0);
            float lsq = c.LengthSquared();
            Normal = lsq > 1e-12f ? c / MathF.Sqrt(lsq) : Vector3.UnitY;
            AabbMin = Vector3.Min(p0, Vector3.Min(p1, p2));
            AabbMax = Vector3.Max(p0, Vector3.Max(p1, p2));
            Centroid = (p0 + p1 + p2) * (1f / 3f);
        }
    }

    private struct BvhNode
    {
        public Vector3 Min;
        public Vector3 Max;
        public int LeftOrStart;
        public int RightOrCount;
        public bool IsLeaf;
    }

    public readonly TriData[] Tris;
    private readonly int[] _indices;
    private readonly List<BvhNode> _nodes = new();
    public readonly Vector3 BoundsMin;
    public readonly Vector3 BoundsMax;

    internal TriangleBvh(TriData[] tris)
    {
        Tris = tris;
        _indices = new int[tris.Length];
        for (int i = 0; i < tris.Length; i++) _indices[i] = i;

        if (tris.Length == 0)
        {
            BoundsMin = BoundsMax = Vector3.Zero;
            return;
        }

        BuildNode(0, tris.Length);
        BoundsMin = _nodes[0].Min;
        BoundsMax = _nodes[0].Max;
    }

    public static TriangleBvh FromMesh(EditableMesh mesh)
    {
        var list = ExtractTriangles(mesh);
        return new TriangleBvh(list.ToArray());
    }

    public static List<TriData> ExtractTriangles(EditableMesh mesh)
    {
        var outTris = new List<TriData>(mesh.Faces.Count * 2);
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var face = mesh.Faces[fi];
            var vi = face.VertexIndices;
            int n = vi.Count;
            if (n < 3) continue;

            if (n == 3)
            {
                AddTri(outTris, mesh, face, 0, 1, 2);
            }
            else if (n == 4)
            {
                float d02 = Vector3.DistanceSquared(mesh.Vertices[vi[0]].Position, mesh.Vertices[vi[2]].Position);
                float d13 = Vector3.DistanceSquared(mesh.Vertices[vi[1]].Position, mesh.Vertices[vi[3]].Position);
                if (d02 <= d13)
                {
                    AddTri(outTris, mesh, face, 0, 1, 2);
                    AddTri(outTris, mesh, face, 0, 2, 3);
                }
                else
                {
                    AddTri(outTris, mesh, face, 1, 2, 3);
                    AddTri(outTris, mesh, face, 1, 3, 0);
                }
            }
            else
            {
                for (int k = 1; k + 1 < n; k++)
                    AddTri(outTris, mesh, face, 0, k, k + 1);
            }
        }
        return outTris;
    }

    private static void AddTri(List<TriData> outTris, EditableMesh mesh, EditableFace face, int c0, int c1, int c2)
    {
        int i0 = face.VertexIndices[c0];
        int i1 = face.VertexIndices[c1];
        int i2 = face.VertexIndices[c2];
        if (i0 == i1 || i1 == i2 || i2 == i0) return;

        Vector3 p0 = mesh.Vertices[i0].Position;
        Vector3 p1 = mesh.Vertices[i1].Position;
        Vector3 p2 = mesh.Vertices[i2].Position;
        if (Vector3.Cross(p1 - p0, p2 - p0).LengthSquared() < 1e-12f) return;

        Vector2 uv0 = c0 < face.FaceUVs.Count ? face.FaceUVs[c0] : mesh.Vertices[i0].UV;
        Vector2 uv1 = c1 < face.FaceUVs.Count ? face.FaceUVs[c1] : mesh.Vertices[i1].UV;
        Vector2 uv2 = c2 < face.FaceUVs.Count ? face.FaceUVs[c2] : mesh.Vertices[i2].UV;
        outTris.Add(new TriData(p0, p1, p2, uv0, uv1, uv2, face.MaterialIndex, face.Smooth));
    }

    private int BuildNode(int start, int count)
    {
        Vector3 bMin = new(float.MaxValue);
        Vector3 bMax = new(float.MinValue);
        Vector3 cMin = new(float.MaxValue);
        Vector3 cMax = new(float.MinValue);

        for (int i = start; i < start + count; i++)
        {
            ref readonly var t = ref Tris[_indices[i]];
            bMin = Vector3.Min(bMin, t.AabbMin);
            bMax = Vector3.Max(bMax, t.AabbMax);
            cMin = Vector3.Min(cMin, t.Centroid);
            cMax = Vector3.Max(cMax, t.Centroid);
        }

        int nodeIdx = _nodes.Count;
        _nodes.Add(default);

        if (count <= 4)
        {
            _nodes[nodeIdx] = new BvhNode { Min = bMin, Max = bMax, LeftOrStart = start, RightOrCount = count, IsLeaf = true };
            return nodeIdx;
        }

        Vector3 ext = cMax - cMin;
        int axis = ext.X >= ext.Y && ext.X >= ext.Z ? 0 : (ext.Y >= ext.Z ? 1 : 2);
        if (GetComp(ext, axis) < 1e-6f)
        {
            _nodes[nodeIdx] = new BvhNode { Min = bMin, Max = bMax, LeftOrStart = start, RightOrCount = count, IsLeaf = true };
            return nodeIdx;
        }

        float split = 0.5f * (GetComp(cMin, axis) + GetComp(cMax, axis));
        int mid = start;
        for (int i = start; i < start + count; i++)
        {
            if (GetComp(Tris[_indices[i]].Centroid, axis) < split)
            {
                (_indices[i], _indices[mid]) = (_indices[mid], _indices[i]);
                mid++;
            }
        }

        if (mid == start || mid == start + count)
            mid = start + count / 2;

        int left = BuildNode(start, mid - start);
        int right = BuildNode(mid, start + count - mid);
        _nodes[nodeIdx] = new BvhNode { Min = bMin, Max = bMax, LeftOrStart = left, RightOrCount = right, IsLeaf = false };
        return nodeIdx;
    }

    private static float GetComp(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    public void QueryOverlap(Vector3 min, Vector3 max, List<int> results)
    {
        if (_nodes.Count == 0) return;
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;

        while (sp > 0)
        {
            var node = _nodes[stack[--sp]];
            if (!AabbOverlap(node.Min, node.Max, min, max)) continue;
            if (node.IsLeaf)
            {
                for (int i = node.LeftOrStart; i < node.LeftOrStart + node.RightOrCount; i++)
                {
                    int ti = _indices[i];
                    ref readonly var t = ref Tris[ti];
                    if (AabbOverlap(t.AabbMin, t.AabbMax, min, max))
                        results.Add(ti);
                }
            }
            else if (sp + 2 <= 64)
            {
                stack[sp++] = node.LeftOrStart;
                stack[sp++] = node.RightOrCount;
            }
        }
    }

    public bool FindClosestPoint(Vector3 point, out Vector3 closestPt, out Vector3 closestNormal, out float bestDistSq)
    {
        closestPt = point;
        closestNormal = Vector3.UnitY;
        bestDistSq = float.MaxValue;
        if (_nodes.Count == 0) return false;

        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;

        while (sp > 0)
        {
            var node = _nodes[stack[--sp]];
            if (AabbDistSq(point, node.Min, node.Max) > bestDistSq) continue;

            if (node.IsLeaf)
            {
                for (int i = node.LeftOrStart; i < node.LeftOrStart + node.RightOrCount; i++)
                {
                    ref readonly var t = ref Tris[_indices[i]];
                    Vector3 cp = ClosestPointOnTriangle(point, t.P0, t.P1, t.P2);
                    float d2 = Vector3.DistanceSquared(point, cp);
                    if (d2 < bestDistSq)
                    {
                        bestDistSq = d2;
                        closestPt = cp;
                        closestNormal = t.Normal;
                    }
                }
            }
            else if (sp + 2 <= 64)
            {
                float dL = AabbDistSq(point, _nodes[node.LeftOrStart].Min, _nodes[node.LeftOrStart].Max);
                float dR = AabbDistSq(point, _nodes[node.RightOrCount].Min, _nodes[node.RightOrCount].Max);
                if (dL < dR)
                {
                    if (dR <= bestDistSq) stack[sp++] = node.RightOrCount;
                    if (dL <= bestDistSq) stack[sp++] = node.LeftOrStart;
                }
                else
                {
                    if (dL <= bestDistSq) stack[sp++] = node.LeftOrStart;
                    if (dR <= bestDistSq) stack[sp++] = node.RightOrCount;
                }
            }
        }

        return bestDistSq < float.MaxValue;
    }

    public bool RaycastNearest(Vector3 origin, Vector3 dir, out float bestT, out Vector3 hitNormal)
    {
        bestT = float.MaxValue;
        hitNormal = Vector3.UnitY;
        if (_nodes.Count == 0) return false;

        Vector3 invDir = new(
            MathF.Abs(dir.X) > 1e-8f ? 1f / dir.X : 1e8f,
            MathF.Abs(dir.Y) > 1e-8f ? 1f / dir.Y : 1e8f,
            MathF.Abs(dir.Z) > 1e-8f ? 1f / dir.Z : 1e8f);

        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;

        while (sp > 0)
        {
            var node = _nodes[stack[--sp]];
            if (!RayAabbHit(origin, invDir, node.Min, node.Max, 0f, bestT)) continue;

            if (node.IsLeaf)
            {
                for (int i = node.LeftOrStart; i < node.LeftOrStart + node.RightOrCount; i++)
                {
                    ref readonly var tr = ref Tris[_indices[i]];
                    if (RayTriangle(origin, dir, tr.P0, tr.P1, tr.P2, out float t, out _, out _) && t > 1e-5f && t < bestT)
                    {
                        bestT = t;
                        hitNormal = tr.Normal;
                    }
                }
            }
            else if (sp + 2 <= 64)
            {
                stack[sp++] = node.LeftOrStart;
                stack[sp++] = node.RightOrCount;
            }
        }

        return bestT < float.MaxValue;
    }

    public int CountRayHits(Vector3 origin, Vector3 dir, out bool nearEdgeHit)
    {
        nearEdgeHit = false;
        if (_nodes.Count == 0) return 0;

        Vector3 invDir = new(
            MathF.Abs(dir.X) > 1e-8f ? 1f / dir.X : 1e8f,
            MathF.Abs(dir.Y) > 1e-8f ? 1f / dir.Y : 1e8f,
            MathF.Abs(dir.Z) > 1e-8f ? 1f / dir.Z : 1e8f);

        int count = 0;
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;

        while (sp > 0)
        {
            var node = _nodes[stack[--sp]];
            if (!RayAabbHit(origin, invDir, node.Min, node.Max, 0f, float.MaxValue)) continue;

            if (node.IsLeaf)
            {
                for (int i = node.LeftOrStart; i < node.LeftOrStart + node.RightOrCount; i++)
                {
                    ref readonly var tr = ref Tris[_indices[i]];
                    if (RayTriangle(origin, dir, tr.P0, tr.P1, tr.P2, out float t, out float u, out float v) && t > 1e-5f)
                    {
                        if (u < 1e-4f || v < 1e-4f || u + v > 1f - 1e-4f)
                            nearEdgeHit = true;
                        count++;
                    }
                }
            }
            else if (sp + 2 <= 64)
            {
                stack[sp++] = node.LeftOrStart;
                stack[sp++] = node.RightOrCount;
            }
        }
        return count;
    }

    public float ComputeWindingNumber(Vector3 p)
    {
        double solidAngleSum = 0.0;
        for (int i = 0; i < Tris.Length; i++)
        {
            ref readonly var tr = ref Tris[i];
            Vector3 a = tr.P0 - p;
            Vector3 b = tr.P1 - p;
            Vector3 c = tr.P2 - p;
            double la = a.Length();
            double lb = b.Length();
            double lc = c.Length();
            if (la < 1e-6 || lb < 1e-6 || lc < 1e-6) continue;

            double num = Vector3.Dot(a, Vector3.Cross(b, c));
            double den = la * lb * lc + Vector3.Dot(a, b) * lc + Vector3.Dot(b, c) * la + Vector3.Dot(c, a) * lb;
            solidAngleSum += 2.0 * Math.Atan2(num, den);
        }
        return (float)(solidAngleSum / (4.0 * Math.PI));
    }

    private static readonly Vector3[] s_probeDirs =
    {
        Vector3.Normalize(new Vector3(0.917321f, 0.312743f, 0.251987f)),
        Vector3.Normalize(new Vector3(-0.412391f, 0.854129f, 0.317041f)),
        Vector3.Normalize(new Vector3(0.219107f, -0.381263f, 0.898143f)),
        Vector3.Normalize(new Vector3(-0.62319f, -0.71231f, 0.32291f)),
        Vector3.Normalize(new Vector3(0.37123f, -0.51921f, -0.76971f)),
    };

    public bool IsPointInside(Vector3 p)
    {
        if (Tris.Length == 0) return false;
        const float pad = 1e-4f;
        if (p.X < BoundsMin.X - pad || p.X > BoundsMax.X + pad ||
            p.Y < BoundsMin.Y - pad || p.Y > BoundsMax.Y + pad ||
            p.Z < BoundsMin.Z - pad || p.Z > BoundsMax.Z + pad)
            return false;

        if (Tris.Length <= 1000)
            return ComputeWindingNumber(p) > 0.5f;

        int insideVotes = 0;
        int validVotes = 0;
        for (int i = 0; i < s_probeDirs.Length; i++)
        {
            int hits = CountRayHits(p, s_probeDirs[i], out bool nearEdge);
            if (!nearEdge)
            {
                validVotes++;
                if ((hits & 1) == 1) insideVotes++;
            }
        }
        if (validVotes > 0)
            return insideVotes * 2 >= validVotes;

        return ComputeWindingNumber(p) > 0.5f;
    }

    private static bool AabbOverlap(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB)
        => minA.X <= maxB.X && maxA.X >= minB.X &&
           minA.Y <= maxB.Y && maxA.Y >= minB.Y &&
           minA.Z <= maxB.Z && maxA.Z >= minB.Z;

    private static float AabbDistSq(Vector3 p, Vector3 min, Vector3 max)
    {
        float dx = p.X < min.X ? min.X - p.X : (p.X > max.X ? p.X - max.X : 0f);
        float dy = p.Y < min.Y ? min.Y - p.Y : (p.Y > max.Y ? p.Y - max.Y : 0f);
        float dz = p.Z < min.Z ? min.Z - p.Z : (p.Z > max.Z ? p.Z - max.Z : 0f);
        return dx * dx + dy * dy + dz * dz;
    }

    private static bool RayAabbHit(Vector3 o, Vector3 invDir, Vector3 bMin, Vector3 bMax, float tMin, float tMax)
    {
        float tx1 = (bMin.X - o.X) * invDir.X;
        float tx2 = (bMax.X - o.X) * invDir.X;
        tMin = MathF.Max(tMin, MathF.Min(tx1, tx2));
        tMax = MathF.Min(tMax, MathF.Max(tx1, tx2));

        float ty1 = (bMin.Y - o.Y) * invDir.Y;
        float ty2 = (bMax.Y - o.Y) * invDir.Y;
        tMin = MathF.Max(tMin, MathF.Min(ty1, ty2));
        tMax = MathF.Min(tMax, MathF.Max(ty1, ty2));

        float tz1 = (bMin.Z - o.Z) * invDir.Z;
        float tz2 = (bMax.Z - o.Z) * invDir.Z;
        tMin = MathF.Max(tMin, MathF.Min(tz1, tz2));
        tMax = MathF.Min(tMax, MathF.Max(tz1, tz2));

        return tMax >= MathF.Max(0f, tMin);
    }

    private static bool RayTriangle(
        Vector3 orig, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2,
        out float t, out float u, out float v)
    {
        t = u = v = 0f;
        Vector3 e1 = v1 - v0;
        Vector3 e2 = v2 - v0;
        Vector3 pvec = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, pvec);
        if (MathF.Abs(det) < 1e-8f) return false;

        float invDet = 1f / det;
        Vector3 tvec = orig - v0;
        u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < -1e-6f || u > 1f + 1e-6f) return false;

        Vector3 qvec = Vector3.Cross(tvec, e1);
        v = Vector3.Dot(dir, qvec) * invDet;
        if (v < -1e-6f || u + v > 1f + 1e-6f) return false;

        t = Vector3.Dot(e2, qvec) * invDet;
        return t > 0f;
    }

    private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a;
        Vector3 ac = c - a;
        Vector3 ap = p - a;
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float v = d1 / (d1 - d3);
            return a + v * ab;
        }

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float w = d2 / (d2 - d6);
            return a + w * ac;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return b + w * (c - b);
        }

        float denom = 1f / (va + vb + vc);
        float v2 = vb * denom;
        float w2 = vc * denom;
        return a + ab * v2 + ac * w2;
    }
}

/// <summary>
/// 正確な交差線分細分化 + Winding Number / BVH 分類による CSG ブーリアンソルバー (差 / 和 / 交差)。
/// </summary>
internal static class MeshBooleanSolver
{
    private const int MaxSubTrisPerFace = 2048;

    private struct SubTri
    {
        public Vector3 P0;
        public Vector3 P1;
        public Vector3 P2;
        public Vector2 UV0;
        public Vector2 UV1;
        public Vector2 UV2;
        public Vector2 Q0;
        public Vector2 Q1;
        public Vector2 Q2;

        public SubTri(
            Vector3 p0, Vector3 p1, Vector3 p2,
            Vector2 uv0, Vector2 uv1, Vector2 uv2,
            Vector3 origin, Vector3 tAxis, Vector3 bAxis)
        {
            P0 = p0; P1 = p1; P2 = p2;
            UV0 = uv0; UV1 = uv1; UV2 = uv2;
            Q0 = Project(p0, origin, tAxis, bAxis);
            Q1 = Project(p1, origin, tAxis, bAxis);
            Q2 = Project(p2, origin, tAxis, bAxis);
        }

        private static Vector2 Project(Vector3 p, Vector3 orig, Vector3 t, Vector3 b)
        {
            Vector3 d = p - orig;
            return new Vector2(Vector3.Dot(d, t), Vector3.Dot(d, b));
        }
    }

    public static EditableMesh Solve(
        EditableMesh meshA,
        EditableMesh meshB,
        BooleanOperationKind op,
        bool mapBMaterialsToA = true)
    {
        var trisA = TriangleBvh.ExtractTriangles(meshA);
        var trisB = TriangleBvh.ExtractTriangles(meshB);

        if (trisA.Count == 0 && trisB.Count == 0)
            return meshA.Clone();
        if (trisA.Count == 0)
            return op == BooleanOperationKind.Union ? meshB.Clone() : EmptyCopyOf(meshA);
        if (trisB.Count == 0)
            return op == BooleanOperationKind.Intersect ? EmptyCopyOf(meshA) : meshA.Clone();

        var bvhA = new TriangleBvh(trisA.ToArray());
        var bvhB = new TriangleBvh(trisB.ToArray());

        bool disjoint =
            bvhA.BoundsMax.X < bvhB.BoundsMin.X - 1e-4f || bvhA.BoundsMin.X > bvhB.BoundsMax.X + 1e-4f ||
            bvhA.BoundsMax.Y < bvhB.BoundsMin.Y - 1e-4f || bvhA.BoundsMin.Y > bvhB.BoundsMax.Y + 1e-4f ||
            bvhA.BoundsMax.Z < bvhB.BoundsMin.Z - 1e-4f || bvhA.BoundsMin.Z > bvhB.BoundsMax.Z + 1e-4f;

        if (disjoint)
        {
            if (op == BooleanOperationKind.Difference) return meshA.Clone();
            if (op == BooleanOperationKind.Intersect) return EmptyCopyOf(meshA);
            var combined = meshA.Clone();
            AppendMesh(combined, meshB, mapBMaterialsToA);
            combined.RebuildEdges(false);
            combined.RecalculateNormals();
            return combined;
        }

        var splitsA = new Dictionary<int, List<(Vector3 A, Vector3 B)>>();
        var splitsB = new Dictionary<int, List<(Vector3 A, Vector3 B)>>();
        var query = new List<int>(32);

        for (int ia = 0; ia < trisA.Count; ia++)
        {
            var tA = trisA[ia];
            query.Clear();
            Vector3 qMin = tA.AabbMin - new Vector3(1e-4f);
            Vector3 qMax = tA.AabbMax + new Vector3(1e-4f);
            bvhB.QueryOverlap(qMin, qMax, query);
            for (int k = 0; k < query.Count; k++)
            {
                int ib = query[k];
                var tB = trisB[ib];
                if (TryIntersectTriangles(in tA, in tB, out var seg0, out var seg1))
                {
                    AddSegment(splitsA, ia, seg0, seg1);
                    AddSegment(splitsB, ib, seg0, seg1);
                }
            }
        }

        var result = EmptyCopyOf(meshA);
        int matOffsetB = 0;
        if (!mapBMaterialsToA && meshB.Materials.Count > 0)
        {
            matOffsetB = result.Materials.Count;
            foreach (var m in meshB.Materials)
                result.Materials.Add(m.Clone());
        }

        var outTris = new List<(Vector3 P0, Vector3 P1, Vector3 P2, Vector2 UV0, Vector2 UV1, Vector2 UV2, int Mat, bool Smooth)>(
            trisA.Count + trisB.Count);

        // A 側のサブトライアングル分類・収集
        for (int ia = 0; ia < trisA.Count; ia++)
        {
            var tA = trisA[ia];
            splitsA.TryGetValue(ia, out var segs);
            var subs = SubdivideTriangle(in tA, segs);
            int matIdx = Math.Clamp(tA.MaterialIndex, 0, result.Materials.Count - 1);

            for (int si = 0; si < subs.Count; si++)
            {
                var st = subs[si];
                if (Vector3.Cross(st.P1 - st.P0, st.P2 - st.P0).LengthSquared() < 1e-12f) continue;

                Vector3 c = (st.P0 + st.P1 + st.P2) * (1f / 3f);
                bool insideB = ClassifyPoint(c, tA.Normal, bvhB, isSourceA: true, op, out bool skipCoplanar);
                if (skipCoplanar) continue;

                bool keep = op switch
                {
                    BooleanOperationKind.Union => !insideB,
                    BooleanOperationKind.Difference => !insideB,
                    BooleanOperationKind.Intersect => insideB,
                    _ => !insideB
                };
                if (keep)
                    outTris.Add((st.P0, st.P1, st.P2, st.UV0, st.UV1, st.UV2, matIdx, tA.Smooth));
            }
        }

        // B 側のサブトライアングル分類・収集
        for (int ib = 0; ib < trisB.Count; ib++)
        {
            var tB = trisB[ib];
            splitsB.TryGetValue(ib, out var segs);
            var subs = SubdivideTriangle(in tB, segs);
            int matIdx = mapBMaterialsToA
                ? 0
                : Math.Clamp(matOffsetB + tB.MaterialIndex, 0, result.Materials.Count - 1);

            for (int si = 0; si < subs.Count; si++)
            {
                var st = subs[si];
                if (Vector3.Cross(st.P1 - st.P0, st.P2 - st.P0).LengthSquared() < 1e-12f) continue;

                Vector3 c = (st.P0 + st.P1 + st.P2) * (1f / 3f);
                bool insideA = ClassifyPoint(c, tB.Normal, bvhA, isSourceA: false, op, out bool skipCoplanar);
                if (skipCoplanar) continue;

                bool keep = op switch
                {
                    BooleanOperationKind.Union => !insideA,
                    BooleanOperationKind.Difference => insideA,
                    BooleanOperationKind.Intersect => insideA,
                    _ => false
                };
                if (keep)
                {
                    if (op == BooleanOperationKind.Difference)
                        outTris.Add((st.P0, st.P2, st.P1, st.UV0, st.UV2, st.UV1, matIdx, tB.Smooth));
                    else
                        outTris.Add((st.P0, st.P1, st.P2, st.UV0, st.UV1, st.UV2, matIdx, tB.Smooth));
                }
            }
        }

        // 頂点ウェルディングしつつ EditableMesh へ格納 (境界丸め誤差を防ぐため 3x3x3 近傍セル探索)
        const float weldThresh = 1e-5f;
        const float weldThreshSq = weldThresh * weldThresh;
        const float invWeldCell = 1f / weldThresh;
        var vertBuckets = new Dictionary<(long, long, long), List<int>>(outTris.Count);
        int GetOrAddVertex(Vector3 pos, Vector2 uv)
        {
            long gx = (long)MathF.Floor(pos.X * invWeldCell);
            long gy = (long)MathF.Floor(pos.Y * invWeldCell);
            long gz = (long)MathF.Floor(pos.Z * invWeldCell);
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            for (long dz = -1; dz <= 1; dz++)
            {
                if (!vertBuckets.TryGetValue((gx + dx, gy + dy, gz + dz), out var bucket)) continue;
                for (int k = 0; k < bucket.Count; k++)
                {
                    int ci = bucket[k];
                    if (Vector3.DistanceSquared(result.Vertices[ci].Position, pos) <= weldThreshSq)
                        return ci;
                }
            }
            int idx = result.Vertices.Count;
            result.Vertices.Add(new EditableVertex { Position = pos, UV = uv });
            var key = (gx, gy, gz);
            if (!vertBuckets.TryGetValue(key, out var list))
            {
                list = new List<int>(2);
                vertBuckets[key] = list;
            }
            list.Add(idx);
            return idx;
        }

        for (int i = 0; i < outTris.Count; i++)
        {
            var t = outTris[i];
            int i0 = GetOrAddVertex(t.P0, t.UV0);
            int i1 = GetOrAddVertex(t.P1, t.UV1);
            int i2 = GetOrAddVertex(t.P2, t.UV2);
            if (i0 == i1 || i1 == i2 || i2 == i0) continue;

            result.Faces.Add(new EditableFace
            {
                VertexIndices = new List<int> { i0, i1, i2 },
                FaceUVs = new List<Vector2> { t.UV0, t.UV1, t.UV2 },
                MaterialIndex = t.Mat,
                Smooth = t.Smooth
            });
        }

        RepairTJunctions(result);
        result.RebuildEdges(false);
        result.RecalculateNormals();
        return result;
    }

    private static void RepairTJunctions(EditableMesh mesh)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            var edgeOwners = new Dictionary<ulong, (int FaceIdx, int EdgeSlot, int U, int V, int Count)>(mesh.Faces.Count * 2);
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var vi = mesh.Faces[fi].VertexIndices;
                if (vi.Count != 3) continue;
                for (int k = 0; k < 3; k++)
                {
                    int a = vi[k];
                    int b = vi[(k + 1) % 3];
                    if (a == b) continue;
                    ulong key = MeshSelection.PackEdge(a, b);
                    if (edgeOwners.TryGetValue(key, out var cur))
                        edgeOwners[key] = (cur.FaceIdx, cur.EdgeSlot, cur.U, cur.V, cur.Count + 1);
                    else
                        edgeOwners[key] = (fi, k, a, b, 1);
                }
            }

            var openEdges = new List<(int FaceIdx, int EdgeSlot, int U, int V)>();
            var openVertsSet = new HashSet<int>();
            foreach (var kv in edgeOwners)
            {
                if (kv.Value.Count == 1)
                {
                    openEdges.Add((kv.Value.FaceIdx, kv.Value.EdgeSlot, kv.Value.U, kv.Value.V));
                    openVertsSet.Add(kv.Value.U);
                    openVertsSet.Add(kv.Value.V);
                }
            }
            if (openEdges.Count == 0)
                return;

            var openVerts = new List<int>(openVertsSet);
            var faceSplits = new Dictionary<int, (int EdgeSlot, List<(int VertIdx, float T)> Splits)>();

            for (int ei = 0; ei < openEdges.Count; ei++)
            {
                var (fi, slot, u, v) = openEdges[ei];
                if (faceSplits.ContainsKey(fi)) continue;

                var f = mesh.Faces[fi];
                int opp = f.VertexIndices[(slot + 2) % 3];
                Vector3 pu = mesh.Vertices[u].Position;
                Vector3 pv = mesh.Vertices[v].Position;
                Vector3 seg = pv - pu;
                float lenSq = seg.LengthSquared();
                if (lenSq < 1e-12f) continue;

                List<(int VertIdx, float T)>? hits = null;
                for (int vi = 0; vi < openVerts.Count; vi++)
                {
                    int m = openVerts[vi];
                    if (m == u || m == v || m == opp) continue;
                    Vector3 pm = mesh.Vertices[m].Position;
                    float t = Vector3.Dot(pm - pu, seg) / lenSq;
                    if (t <= 1e-5f || t >= 1f - 1e-5f) continue;
                    Vector3 proj = pu + seg * t;
                    if (Vector3.DistanceSquared(pm, proj) < 2e-8f)
                    {
                        hits ??= new List<(int, float)>(2);
                        hits.Add((m, t));
                    }
                }

                if (hits != null && hits.Count > 0)
                {
                    hits.Sort((a, b) => a.T.CompareTo(b.T));
                    faceSplits[fi] = (slot, hits);
                }
            }

            if (faceSplits.Count == 0)
                return;

            var newFaces = new List<EditableFace>(mesh.Faces.Count + faceSplits.Count * 2);
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var f = mesh.Faces[fi];
                if (!faceSplits.TryGetValue(fi, out var sp) || f.VertexIndices.Count != 3)
                {
                    newFaces.Add(f);
                    continue;
                }

                int s0 = sp.EdgeSlot;
                int s1 = (s0 + 1) % 3;
                int s2 = (s0 + 2) % 3;
                int va = f.VertexIndices[s0];
                int vb = f.VertexIndices[s1];
                int vc = f.VertexIndices[s2];
                Vector2 uva = s0 < f.FaceUVs.Count ? f.FaceUVs[s0] : mesh.Vertices[va].UV;
                Vector2 uvb = s1 < f.FaceUVs.Count ? f.FaceUVs[s1] : mesh.Vertices[vb].UV;
                Vector2 uvc = s2 < f.FaceUVs.Count ? f.FaceUVs[s2] : mesh.Vertices[vc].UV;

                int prevV = va;
                Vector2 prevUv = uva;
                for (int hi = 0; hi < sp.Splits.Count; hi++)
                {
                    var (m, t) = sp.Splits[hi];
                    if (m == prevV || m == vb || m == vc) continue;
                    Vector2 uvM = Vector2.Lerp(uva, uvb, t);
                    newFaces.Add(new EditableFace
                    {
                        VertexIndices = new List<int> { prevV, m, vc },
                        FaceUVs = new List<Vector2> { prevUv, uvM, uvc },
                        MaterialIndex = f.MaterialIndex,
                        Smooth = f.Smooth
                    });
                    prevV = m;
                    prevUv = uvM;
                }
                if (prevV != vb && prevV != vc && vb != vc)
                {
                    newFaces.Add(new EditableFace
                    {
                        VertexIndices = new List<int> { prevV, vb, vc },
                        FaceUVs = new List<Vector2> { prevUv, uvb, uvc },
                        MaterialIndex = f.MaterialIndex,
                        Smooth = f.Smooth
                    });
                }
            }

            mesh.Faces.Clear();
            mesh.Faces.AddRange(newFaces);
        }
    }

    private static bool ClassifyPoint(
        Vector3 c, Vector3 selfNorm, TriangleBvh otherBvh, bool isSourceA, BooleanOperationKind op, out bool skipCoplanar)
    {
        skipCoplanar = false;
        if (otherBvh.FindClosestPoint(c, out _, out var otherNorm, out float distSq) && distSq < 1e-8f)
        {
            bool sameNormal = Vector3.Dot(selfNorm, otherNorm) > 0f;
            if (op == BooleanOperationKind.Union)
            {
                skipCoplanar = !isSourceA || !sameNormal;
                return false;
            }
            if (op == BooleanOperationKind.Intersect)
            {
                skipCoplanar = !isSourceA || !sameNormal;
                return sameNormal;
            }
            if (op == BooleanOperationKind.Difference)
            {
                skipCoplanar = !isSourceA || sameNormal;
                return sameNormal;
            }
        }
        return otherBvh.IsPointInside(c);
    }

    private static EditableMesh EmptyCopyOf(EditableMesh src)
    {
        var m = new EditableMesh
        {
            SourceFilePath = src.SourceFilePath,
            HasRefBounds = src.HasRefBounds,
            RefBoundsMin = src.RefBoundsMin,
            RefBoundsMax = src.RefBoundsMax,
            RefCenter = src.RefCenter,
            RefScale = src.RefScale,
        };
        foreach (var mat in src.Materials) m.Materials.Add(mat.Clone());
        if (m.Materials.Count == 0) m.Materials.Add(new EditableMaterial());
        return m;
    }

    private static void AppendMesh(EditableMesh dst, EditableMesh src, bool mapMaterialsToZero)
    {
        int baseV = dst.Vertices.Count;
        int matOff = mapMaterialsToZero ? 0 : dst.Materials.Count;
        if (!mapMaterialsToZero)
            foreach (var m in src.Materials) dst.Materials.Add(m.Clone());

        foreach (var v in src.Vertices) dst.Vertices.Add(v.Clone());
        foreach (var f in src.Faces)
        {
            var nf = f.Clone();
            for (int k = 0; k < nf.VertexIndices.Count; k++) nf.VertexIndices[k] += baseV;
            nf.MaterialIndex = mapMaterialsToZero ? 0 : Math.Clamp(nf.MaterialIndex + matOff, 0, Math.Max(0, dst.Materials.Count - 1));
            dst.Faces.Add(nf);
        }
    }

    private static void AddSegment(Dictionary<int, List<(Vector3 A, Vector3 B)>> map, int idx, Vector3 a, Vector3 b)
    {
        if (Vector3.DistanceSquared(a, b) < 1e-10f) return;
        if (!map.TryGetValue(idx, out var list))
        {
            list = new List<(Vector3, Vector3)>(8);
            map[idx] = list;
        }
        list.Add((a, b));
    }

    private static bool TryIntersectTriangles(
        in TriangleBvh.TriData a,
        in TriangleBvh.TriData b,
        out Vector3 segStart,
        out Vector3 segEnd)
    {
        segStart = segEnd = Vector3.Zero;
        const float eps = 1e-5f;

        float dB = Vector3.Dot(b.Normal, b.P0);
        float da0 = Vector3.Dot(b.Normal, a.P0) - dB;
        float da1 = Vector3.Dot(b.Normal, a.P1) - dB;
        float da2 = Vector3.Dot(b.Normal, a.P2) - dB;
        if ((da0 > eps && da1 > eps && da2 > eps) || (da0 < -eps && da1 < -eps && da2 < -eps))
            return false;

        float dA = Vector3.Dot(a.Normal, a.P0);
        float db0 = Vector3.Dot(a.Normal, b.P0) - dA;
        float db1 = Vector3.Dot(a.Normal, b.P1) - dA;
        float db2 = Vector3.Dot(a.Normal, b.P2) - dA;
        if ((db0 > eps && db1 > eps && db2 > eps) || (db0 < -eps && db1 < -eps && db2 < -eps))
            return false;

        Vector3 lineCross = Vector3.Cross(a.Normal, b.Normal);
        float denom = lineCross.LengthSquared();
        if (denom < 1e-6f)
            return false;

        Vector3 D = lineCross / MathF.Sqrt(denom);
        Vector3 linePt = (dA * Vector3.Cross(b.Normal, lineCross) + dB * Vector3.Cross(lineCross, a.Normal)) / denom;

        if (!ComputeTriIntervalOnLine(a.P0, a.P1, a.P2, da0, da1, da2, linePt, D, eps, out float sA0, out float sA1))
            return false;
        if (!ComputeTriIntervalOnLine(b.P0, b.P1, b.P2, db0, db1, db2, linePt, D, eps, out float sB0, out float sB1))
            return false;

        float sLo = MathF.Max(sA0, sB0);
        float sHi = MathF.Min(sA1, sB1);
        if (sHi - sLo <= 1e-5f)
            return false;

        segStart = linePt + D * sLo;
        segEnd = linePt + D * sHi;
        return true;
    }

    private static bool ComputeTriIntervalOnLine(
        Vector3 p0, Vector3 p1, Vector3 p2,
        float d0, float d1, float d2,
        Vector3 linePt, Vector3 D, float eps,
        out float sMin, out float sMax)
    {
        float minVal = float.MaxValue;
        float maxVal = float.MinValue;
        int count = 0;

        void CheckVert(Vector3 p, float d)
        {
            if (MathF.Abs(d) <= eps)
            {
                float s = Vector3.Dot(p - linePt, D);
                minVal = MathF.Min(minVal, s);
                maxVal = MathF.Max(maxVal, s);
                count++;
            }
        }
        void CheckEdge(Vector3 u, Vector3 v, float du, float dv)
        {
            if ((du > eps && dv < -eps) || (du < -eps && dv > eps))
            {
                float t = du / (du - dv);
                Vector3 ip = u + (v - u) * t;
                float s = Vector3.Dot(ip - linePt, D);
                minVal = MathF.Min(minVal, s);
                maxVal = MathF.Max(maxVal, s);
                count++;
            }
        }

        CheckVert(p0, d0);
        CheckVert(p1, d1);
        CheckVert(p2, d2);
        CheckEdge(p0, p1, d0, d1);
        CheckEdge(p1, p2, d1, d2);
        CheckEdge(p2, p0, d2, d0);

        sMin = minVal;
        sMax = maxVal;
        return count >= 2 && sMax > sMin;
    }

    private static List<SubTri> SubdivideTriangle(in TriangleBvh.TriData tri, List<(Vector3 A, Vector3 B)>? segments)
    {
        Vector3 orig = tri.P0;
        Vector3 tAxis = Vector3.Normalize(tri.P1 - tri.P0);
        Vector3 bAxis = Vector3.Normalize(Vector3.Cross(tri.Normal, tAxis));

        var list = new List<SubTri>(segments == null ? 1 : 8)
        {
            new SubTri(tri.P0, tri.P1, tri.P2, tri.UV0, tri.UV1, tri.UV2, orig, tAxis, bAxis)
        };
        if (segments == null || segments.Count == 0)
            return list;

        for (int si = 0; si < segments.Count; si++)
        {
            if (list.Count >= MaxSubTrisPerFace) break;

            var (segA, segB) = segments[si];
            Vector2 a2 = new(Vector3.Dot(segA - orig, tAxis), Vector3.Dot(segA - orig, bAxis));
            Vector2 b2 = new(Vector3.Dot(segB - orig, tAxis), Vector3.Dot(segB - orig, bAxis));
            if (Vector2.DistanceSquared(a2, b2) < 1e-10f) continue;

            // まず端点が既存サブトライアングルの内部・辺上にある場合に分割
            list = InsertPointIfInsideOrOnEdge(list, segA, a2, orig, tAxis, bAxis);
            if (list.Count >= MaxSubTrisPerFace) break;
            list = InsertPointIfInsideOrOnEdge(list, segB, b2, orig, tAxis, bAxis);
            if (list.Count >= MaxSubTrisPerFace) break;

            // 次に線分 (a2 -> b2) がサブトライアングルの2辺を横切る箇所で分割
            list = SplitByLineSegment(list, a2, b2, orig, tAxis, bAxis);
        }

        return list;
    }

    private static List<SubTri> InsertPointIfInsideOrOnEdge(
        List<SubTri> input, Vector3 pt3, Vector2 pt2, Vector3 orig, Vector3 tAxis, Vector3 bAxis)
    {
        const float vEps2 = 1e-10f;
        var output = new List<SubTri>(input.Count + 2);
        for (int i = 0; i < input.Count; i++)
        {
            var st = input[i];
            if (output.Count >= MaxSubTrisPerFace ||
                Vector2.DistanceSquared(pt2, st.Q0) < vEps2 ||
                Vector2.DistanceSquared(pt2, st.Q1) < vEps2 ||
                Vector2.DistanceSquared(pt2, st.Q2) < vEps2)
            {
                output.Add(st);
                continue;
            }

            if (TryComputeBarycentric2D(pt2, st.Q0, st.Q1, st.Q2, out float b0, out float b1, out float b2))
            {
                const float eTol = 1e-5f;
                Vector2 uvP = st.UV0 * b0 + st.UV1 * b1 + st.UV2 * b2;
                if (b0 > eTol && b1 > eTol && b2 > eTol)
                {
                    output.Add(new SubTri(st.P0, st.P1, pt3, st.UV0, st.UV1, uvP, orig, tAxis, bAxis));
                    output.Add(new SubTri(st.P1, st.P2, pt3, st.UV1, st.UV2, uvP, orig, tAxis, bAxis));
                    output.Add(new SubTri(st.P2, st.P0, pt3, st.UV2, st.UV0, uvP, orig, tAxis, bAxis));
                    continue;
                }
                if (b2 >= -eTol && b2 <= eTol && b0 > eTol && b1 > eTol)
                {
                    output.Add(new SubTri(st.P0, pt3, st.P2, st.UV0, uvP, st.UV2, orig, tAxis, bAxis));
                    output.Add(new SubTri(pt3, st.P1, st.P2, uvP, st.UV1, st.UV2, orig, tAxis, bAxis));
                    continue;
                }
                if (b0 >= -eTol && b0 <= eTol && b1 > eTol && b2 > eTol)
                {
                    output.Add(new SubTri(st.P1, pt3, st.P0, st.UV1, uvP, st.UV0, orig, tAxis, bAxis));
                    output.Add(new SubTri(pt3, st.P2, st.P0, uvP, st.UV2, st.UV0, orig, tAxis, bAxis));
                    continue;
                }
                if (b1 >= -eTol && b1 <= eTol && b2 > eTol && b0 > eTol)
                {
                    output.Add(new SubTri(st.P2, pt3, st.P1, st.UV2, uvP, st.UV1, orig, tAxis, bAxis));
                    output.Add(new SubTri(pt3, st.P0, st.P1, uvP, st.UV0, st.UV1, orig, tAxis, bAxis));
                    continue;
                }
            }
            output.Add(st);
        }
        return output;
    }

    private static List<SubTri> SplitByLineSegment(
        List<SubTri> input, Vector2 a, Vector2 b, Vector3 orig, Vector3 tAxis, Vector3 bAxis)
    {
        Vector2 ab = b - a;
        float abLen = ab.Length();
        if (abLen < 1e-5f) return input;
        Vector2 dir = ab / abLen;
        Vector2 lineN = new(-dir.Y, dir.X);

        var output = new List<SubTri>(input.Count + 4);
        const float eps = 1e-5f;
        Span<Vector3> pArr = stackalloc Vector3[3];
        Span<Vector2> uvArr = stackalloc Vector2[3];
        Span<Vector2> qArr = stackalloc Vector2[3];
        Span<float> dArr = stackalloc float[3];

        for (int i = 0; i < input.Count; i++)
        {
            var st = input[i];
            if (output.Count >= MaxSubTrisPerFace)
            {
                output.Add(st);
                continue;
            }

            float d0 = Vector2.Dot(st.Q0 - a, lineN);
            float d1 = Vector2.Dot(st.Q1 - a, lineN);
            float d2 = Vector2.Dot(st.Q2 - a, lineN);

            int pos = (d0 > eps ? 1 : 0) + (d1 > eps ? 1 : 0) + (d2 > eps ? 1 : 0);
            int neg = (d0 < -eps ? 1 : 0) + (d1 < -eps ? 1 : 0) + (d2 < -eps ? 1 : 0);

            if (pos == 0 || neg == 0)
            {
                output.Add(st);
                continue;
            }

            // 異なる符号を持つ2辺の交点が線分 [0, abLen] に重なるか判定
            pArr[0] = st.P0; pArr[1] = st.P1; pArr[2] = st.P2;
            uvArr[0] = st.UV0; uvArr[1] = st.UV1; uvArr[2] = st.UV2;
            qArr[0] = st.Q0; qArr[1] = st.Q1; qArr[2] = st.Q2;
            dArr[0] = d0; dArr[1] = d1; dArr[2] = d2;

            if (pos + neg == 3)
            {
                // 1頂点のみ異符号の側 (1 vs 2)
                int iso = ((pos == 1 && d0 > eps) || (neg == 1 && d0 < -eps)) ? 0
                        : ((pos == 1 && d1 > eps) || (neg == 1 && d1 < -eps)) ? 1 : 2;
                int n1 = (iso + 1) % 3;
                int n2 = (iso + 2) % 3;

                float tA = dArr[iso] / (dArr[iso] - dArr[n1]);
                float tB = dArr[iso] / (dArr[iso] - dArr[n2]);
                Vector2 qHitA = Vector2.Lerp(qArr[iso], qArr[n1], tA);
                Vector2 qHitB = Vector2.Lerp(qArr[iso], qArr[n2], tB);

                float sHitA = Vector2.Dot(qHitA - a, dir);
                float sHitB = Vector2.Dot(qHitB - a, dir);
                if (MathF.Min(sHitA, sHitB) < -2e-4f ||
                    MathF.Max(sHitA, sHitB) > abLen + 2e-4f ||
                    MathF.Max(sHitA, sHitB) <= 1e-4f ||
                    MathF.Min(sHitA, sHitB) >= abLen - 1e-4f)
                {
                    output.Add(st);
                    continue;
                }

                Vector3 pHitA = Vector3.Lerp(pArr[iso], pArr[n1], tA);
                Vector3 pHitB = Vector3.Lerp(pArr[iso], pArr[n2], tB);
                Vector2 uvHitA = Vector2.Lerp(uvArr[iso], uvArr[n1], tA);
                Vector2 uvHitB = Vector2.Lerp(uvArr[iso], uvArr[n2], tB);

                output.Add(new SubTri(pArr[iso], pHitA, pHitB, uvArr[iso], uvHitA, uvHitB, orig, tAxis, bAxis));
                output.Add(new SubTri(pHitA, pArr[n1], pArr[n2], uvHitA, uvArr[n1], uvArr[n2], orig, tAxis, bAxis));
                output.Add(new SubTri(pHitA, pArr[n2], pHitB, uvHitA, uvArr[n2], uvHitB, orig, tAxis, bAxis));
            }
            else
            {
                // 1頂点が線上、残りの2頂点が異符号 (1 + 1 + 1)
                int onLine = MathF.Abs(d0) <= eps ? 0 : (MathF.Abs(d1) <= eps ? 1 : 2);
                int n1 = (onLine + 1) % 3;
                int n2 = (onLine + 2) % 3;
                float tCross = dArr[n1] / (dArr[n1] - dArr[n2]);
                Vector2 qHit = Vector2.Lerp(qArr[n1], qArr[n2], tCross);
                float sOn = Vector2.Dot(qArr[onLine] - a, dir);
                float sCross = Vector2.Dot(qHit - a, dir);
                if (sCross < -2e-4f ||
                    sCross > abLen + 2e-4f ||
                    sOn < -2e-4f ||
                    sOn > abLen + 2e-4f ||
                    MathF.Min(sOn, sCross) >= abLen - 1e-4f ||
                    MathF.Max(sOn, sCross) <= 1e-4f)
                {
                    output.Add(st);
                    continue;
                }

                Vector3 pHit = Vector3.Lerp(pArr[n1], pArr[n2], tCross);
                Vector2 uvHit = Vector2.Lerp(uvArr[n1], uvArr[n2], tCross);
                output.Add(new SubTri(pArr[onLine], pArr[n1], pHit, uvArr[onLine], uvArr[n1], uvHit, orig, tAxis, bAxis));
                output.Add(new SubTri(pArr[onLine], pHit, pArr[n2], uvArr[onLine], uvHit, uvArr[n2], orig, tAxis, bAxis));
            }
        }
        return output;
    }

    private static bool TryComputeBarycentric2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out float u, out float v, out float w)
    {
        Vector2 v0 = b - a;
        Vector2 v1 = c - a;
        Vector2 v2 = p - a;
        float den = v0.X * v1.Y - v1.X * v0.Y;
        if (MathF.Abs(den) < 1e-10f)
        {
            u = v = w = 0f;
            return false;
        }
        float inv = 1f / den;
        v = (v2.X * v1.Y - v1.X * v2.Y) * inv;
        w = (v0.X * v2.Y - v2.X * v0.Y) * inv;
        u = 1f - v - w;
        return u >= -1e-4f && v >= -1e-4f && w >= -1e-4f;
    }
}
