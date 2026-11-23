using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using Iyahon_D3D11Renderer_Core.Model3D;

namespace Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;

/// <summary>
/// 編集可能メッシュの頂点。
/// </summary>
internal sealed class EditableVertex
{
    public Vector3 Position;
    public Vector3 Normal = Vector3.UnitY;
    public Vector2 UV;
    public Vector4 Color = Vector4.One;

    public EditableVertex Clone() => new()
    {
        Position = Position,
        Normal = Normal,
        UV = UV,
        Color = Color
    };
}

/// <summary>
/// 正規化済み無向辺 (V0 &lt; V1)。
/// </summary>
internal sealed class EditableEdge
{
    public int V0;
    public int V1;

    public EditableEdge() { }

    public EditableEdge(int a, int b)
    {
        if (a <= b)
        {
            V0 = a;
            V1 = b;
        }
        else
        {
            V0 = b;
            V1 = a;
        }
    }

    public bool ContainsVertex(int v) => V0 == v || V1 == v;

    public int OtherVertex(int v) => v == V0 ? V1 : (v == V1 ? V0 : -1);

    public EditableEdge Clone() => new(V0, V1);
}

/// <summary>
/// 編集可能メッシュの面 (三角形・四角形・N角形対応)。
/// </summary>
internal sealed class EditableFace
{
    public List<int> VertexIndices = new();
    public List<Vector2> FaceUVs = new();
    public List<Vector3>? FaceCornerNormals;
    public int MaterialIndex;
    public bool Smooth;
    public Vector3 FaceNormal = Vector3.UnitY;

    public EditableFace Clone() => new()
    {
        VertexIndices = new List<int>(VertexIndices),
        FaceUVs = new List<Vector2>(FaceUVs),
        FaceCornerNormals = FaceCornerNormals != null ? new List<Vector3>(FaceCornerNormals) : null,
        MaterialIndex = MaterialIndex,
        Smooth = Smooth,
        FaceNormal = FaceNormal
    };
}

/// <summary>
/// 編集可能メッシュのマテリアル情報。
/// </summary>
internal sealed class EditableMaterial
{
    public string Name = "Material";
    public Vector4 BaseColor = Vector4.One;
    public Vector4 EmissiveColor = Vector4.Zero;
    public float Metallic;
    public float Roughness = 0.5f;
    public float Transmission;
    public float Ior = 1.5f;
    public bool ForceOpaque;
    public bool CullBackface;
    public bool CastShadow = true;
    /// <summary>元ファイルモデルのパートインデックス（テクスチャ借用用、新規追加スロットは -1）。</summary>
    public int SourcePartIndex = -1;
    /// <summary>このマテリアルに設定されたシェーダーノードグラフ (未設定時は null)。</summary>
    public Shading.MaterialNodeGraph? NodeGraph;

    public EditableMaterial Clone() => new()
    {
        Name = Name,
        BaseColor = BaseColor,
        EmissiveColor = EmissiveColor,
        Metallic = Metallic,
        Roughness = Roughness,
        Transmission = Transmission,
        Ior = Ior,
        ForceOpaque = ForceOpaque,
        CullBackface = CullBackface,
        CastShadow = CastShadow,
        SourcePartIndex = SourcePartIndex,
        NodeGraph = NodeGraph?.Clone()
    };
}

/// <summary>
/// Blender 風モデリング用の B-Rep ライクな編集可能メッシュ表現。
/// </summary>
internal sealed class EditableMesh
{
    private const string MagicPrefix = "IMSH1:";

    public List<EditableVertex> Vertices { get; } = new();
    public List<EditableEdge> Edges { get; } = new();
    public List<EditableFace> Faces { get; } = new();
    public List<EditableMaterial> Materials { get; } = new();

    /// <summary>
    /// 編集中もモデルの正規化位置・スケールが飛ばないよう固定する基準バウンズ。
    /// </summary>
    public string? SourceFilePath { get; set; }
    public bool HasRefBounds { get; set; }
    public Vector3 RefBoundsMin { get; set; } = new(-0.5f, -0.5f, -0.5f);
    public Vector3 RefBoundsMax { get; set; } = new(0.5f, 0.5f, 0.5f);
    public Vector3 RefCenter { get; set; } = Vector3.Zero;
    public float RefScale { get; set; } = 1f;

    public EditableMesh Clone()
    {
        var copy = new EditableMesh
        {
            SourceFilePath = SourceFilePath,
            HasRefBounds = HasRefBounds,
            RefBoundsMin = RefBoundsMin,
            RefBoundsMax = RefBoundsMax,
            RefCenter = RefCenter,
            RefScale = RefScale
        };
        copy.Vertices.Capacity = Vertices.Count;
        foreach (var v in Vertices) copy.Vertices.Add(v.Clone());
        copy.Edges.Capacity = Edges.Count;
        foreach (var e in Edges) copy.Edges.Add(e.Clone());
        copy.Faces.Capacity = Faces.Count;
        foreach (var f in Faces) copy.Faces.Add(f.Clone());
        copy.Materials.Capacity = Materials.Count;
        foreach (var m in Materials) copy.Materials.Add(m.Clone());
        return copy;
    }

    public void CaptureRefBoundsFromCurrentGeometry()
    {
        ComputeCurrentBounds(out var min, out var max, out var center, out float scale);
        HasRefBounds = true;
        RefBoundsMin = min;
        RefBoundsMax = max;
        RefCenter = center;
        RefScale = scale;
    }

    public void ComputeCurrentBounds(out Vector3 min, out Vector3 max, out Vector3 center, out float scale)
    {
        if (Vertices.Count == 0)
        {
            min = new Vector3(-0.5f, -0.5f, -0.5f);
            max = new Vector3(0.5f, 0.5f, 0.5f);
            center = Vector3.Zero;
            scale = 1f;
            return;
        }

        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int i = 0; i < Vertices.Count; i++)
        {
            var p = Vertices[i].Position;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        center = 0.5f * (min + max);
        var extent = max - min;
        float maxExtent = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        scale = maxExtent > 1e-6f ? 1f / maxExtent : 1f;
    }

    /// <summary>
    /// 全フェースから無向辺を再構築する。ルーズ辺 (どの面にも属さない辺) も保持可能。
    /// </summary>
    public void RebuildEdges(bool preserveLooseEdges = true)
    {
        var seen = new HashSet<ulong>();
        var result = new List<EditableEdge>(Faces.Count * 2 + Edges.Count);

        static ulong PackEdge(int a, int b)
        {
            uint lo = (uint)Math.Min(a, b);
            uint hi = (uint)Math.Max(a, b);
            return ((ulong)lo << 32) | hi;
        }

        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var verts = Faces[fi].VertexIndices;
            int n = verts.Count;
            if (n < 2) continue;
            for (int k = 0; k < n; k++)
            {
                int a = verts[k];
                int b = verts[(k + 1) % n];
                if (a == b || a < 0 || b < 0 || a >= Vertices.Count || b >= Vertices.Count) continue;
                ulong key = PackEdge(a, b);
                if (seen.Add(key))
                {
                    result.Add(new EditableEdge(a, b));
                }
            }
        }

        if (preserveLooseEdges)
        {
            for (int ei = 0; ei < Edges.Count; ei++)
            {
                var e = Edges[ei];
                if (e.V0 == e.V1 || e.V0 < 0 || e.V1 < 0 || e.V0 >= Vertices.Count || e.V1 >= Vertices.Count)
                {
                    continue;
                }
                ulong key = PackEdge(e.V0, e.V1);
                if (seen.Add(key))
                {
                    result.Add(new EditableEdge(e.V0, e.V1));
                }
            }
        }

        Edges.Clear();
        Edges.AddRange(result);
    }

    public int FindEdgeIndex(int a, int b)
    {
        int lo = Math.Min(a, b);
        int hi = Math.Max(a, b);
        for (int i = 0; i < Edges.Count; i++)
        {
            if (Edges[i].V0 == lo && Edges[i].V1 == hi)
                return i;
        }
        return -1;
    }

    public int EnsureEdge(int a, int b)
    {
        if (a == b || a < 0 || b < 0 || a >= Vertices.Count || b >= Vertices.Count)
            return -1;
        int idx = FindEdgeIndex(a, b);
        if (idx >= 0) return idx;
        Edges.Add(new EditableEdge(a, b));
        return Edges.Count - 1;
    }

    /// <summary>
    /// 面の法線 (Newell 法) と頂点の法線を再計算する。
    /// </summary>
    public void RecalculateNormals()
    {
        var smoothAccum = new Vector3[Vertices.Count];
        var anyAccum = new Vector3[Vertices.Count];

        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var face = Faces[fi];
            var vi = face.VertexIndices;
            int n = vi.Count;
            if (n < 3)
            {
                face.FaceNormal = Vector3.UnitY;
                continue;
            }

            Vector3 nSum = Vector3.Zero;
            for (int k = 0; k < n; k++)
            {
                var p0 = Vertices[vi[k]].Position;
                var p1 = Vertices[vi[(k + 1) % n]].Position;
                nSum.X += (p0.Y - p1.Y) * (p0.Z + p1.Z);
                nSum.Y += (p0.Z - p1.Z) * (p0.X + p1.X);
                nSum.Z += (p0.X - p1.X) * (p0.Y + p1.Y);
            }

            float lenSq = nSum.LengthSquared();
            Vector3 faceNormal = lenSq > 1e-12f ? nSum / MathF.Sqrt(lenSq) : Vector3.UnitY;
            face.FaceNormal = faceNormal;

            // nSum の長さの半分がおおよその面積ウェイトになるためそのまま加算
            Vector3 weighted = lenSq > 1e-12f ? nSum : faceNormal;
            for (int k = 0; k < n; k++)
            {
                int vIdx = vi[k];
                anyAccum[vIdx] += weighted;
                if (face.Smooth)
                {
                    smoothAccum[vIdx] += weighted;
                }
            }
        }

        for (int i = 0; i < Vertices.Count; i++)
        {
            Vector3 acc = smoothAccum[i].LengthSquared() > 1e-12f ? smoothAccum[i] : anyAccum[i];
            float lenSq = acc.LengthSquared();
            Vertices[i].Normal = lenSq > 1e-12f ? acc / MathF.Sqrt(lenSq) : Vector3.UnitY;
        }
    }

    public void ClearCustomNormals(IEnumerable<int>? faceIndices = null)
    {
        if (faceIndices == null)
        {
            for (int i = 0; i < Faces.Count; i++)
                Faces[i].FaceCornerNormals = null;
            return;
        }

        foreach (int fi in faceIndices)
        {
            if (fi >= 0 && fi < Faces.Count)
                Faces[fi].FaceCornerNormals = null;
        }
    }

    public void ClearCustomNormalsForVertices(IEnumerable<int> vertexIndices)
    {
        var touched = vertexIndices as HashSet<int> ?? new HashSet<int>(vertexIndices);
        if (touched.Count == 0) return;

        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var face = Faces[fi];
            if (face.FaceCornerNormals == null) continue;
            for (int k = 0; k < face.VertexIndices.Count; k++)
            {
                if (touched.Contains(face.VertexIndices[k]))
                {
                    face.FaceCornerNormals = null;
                    break;
                }
            }
        }
    }

    public void ApplyAutoSmooth(float angleDegrees = 35f)
    {
        RecalculateNormals();
        if (Faces.Count == 0 || Vertices.Count == 0) return;

        float cosThresh = MathF.Cos(Math.Clamp(angleDegrees, 0f, 180f) * (MathF.PI / 180f));
        var vFaces = new List<int>[Vertices.Count];
        for (int i = 0; i < Vertices.Count; i++)
            vFaces[i] = new List<int>(6);

        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var viList = Faces[fi].VertexIndices;
            for (int k = 0; k < viList.Count; k++)
            {
                int vi = viList[k];
                if (vi >= 0 && vi < Vertices.Count)
                    vFaces[vi].Add(fi);
            }
        }

        var faceWeights = new float[Faces.Count];
        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var vi = Faces[fi].VertexIndices;
            if (vi.Count < 3) { faceWeights[fi] = 1f; continue; }
            var p0 = Vertices[vi[0]].Position;
            Vector3 nSum = Vector3.Zero;
            for (int k = 1; k + 1 < vi.Count; k++)
                nSum += Vector3.Cross(Vertices[vi[k]].Position - p0, Vertices[vi[k + 1]].Position - p0);
            float w = nSum.Length();
            faceWeights[fi] = w > 1e-10f ? w : 1f;
        }

        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var face = Faces[fi];
            int count = face.VertexIndices.Count;
            if (count < 3)
            {
                face.FaceCornerNormals = null;
                face.Smooth = false;
                continue;
            }

            Vector3 fNorm = face.FaceNormal;
            var cornerNorms = new List<Vector3>(count);
            bool anySmooth = false;

            for (int k = 0; k < count; k++)
            {
                int vi = face.VertexIndices[k];
                Vector3 accum = fNorm * faceWeights[fi];
                if (vi >= 0 && vi < vFaces.Length)
                {
                    var adjList = vFaces[vi];
                    for (int a = 0; a < adjList.Count; a++)
                    {
                        int adjFi = adjList[a];
                        if (adjFi == fi) continue;
                        var adjFace = Faces[adjFi];
                        if (Vector3.Dot(fNorm, adjFace.FaceNormal) >= cosThresh)
                            accum += adjFace.FaceNormal * faceWeights[adjFi];
                    }
                }
                Vector3 cn = accum.LengthSquared() > 1e-12f ? Vector3.Normalize(accum) : fNorm;
                cornerNorms.Add(cn);
                if (Vector3.Dot(cn, fNorm) < 0.99995f)
                    anySmooth = true;
            }

            face.FaceCornerNormals = cornerNorms;
            face.Smooth = anySmooth;
        }

        RecalculateNormals();
    }

    /// <summary>
    /// インポート済みの ParsedModel から編集用メッシュを生成する。
    /// </summary>
    public static EditableMesh FromParsedModel(ParsedModel parsed)
    {
        var mesh = new EditableMesh
        {
            HasRefBounds = true,
            RefBoundsMin = parsed.BoundsMin,
            RefBoundsMax = parsed.BoundsMax,
            RefCenter = parsed.ModelCenter,
            RefScale = parsed.ModelScale > 1e-6f ? parsed.ModelScale : 1f
        };

        if (parsed.Parts.Count > 0)
        {
            for (int partIdx = 0; partIdx < parsed.Parts.Count; partIdx++)
            {
                var part = parsed.Parts[partIdx];
                mesh.Materials.Add(new EditableMaterial
                {
                    Name = string.IsNullOrWhiteSpace(part.Name) ? $"Material.{partIdx:D3}" : part.Name,
                    BaseColor = part.BaseColor,
                    EmissiveColor = part.EmissiveColor,
                    Metallic = part.Metallic,
                    Roughness = part.Roughness,
                    Transmission = part.Transmission,
                    Ior = part.Ior,
                    ForceOpaque = part.ForceOpaque,
                    CullBackface = part.CullBackface,
                    CastShadow = part.CastShadow,
                    SourcePartIndex = partIdx
                });
            }
        }
        else
        {
            mesh.Materials.Add(new EditableMaterial());
        }

        // 重複頂点 (UV シーム / ハードエッジ分割頂点) を位置ベースで結合しつつ FaceUVs を保持
        var posMap = new Dictionary<(long, long, long), int>(parsed.Vertices.Length);
        var remap = new int[parsed.Vertices.Length];
        for (int i = 0; i < parsed.Vertices.Length; i++)
        {
            var mv = parsed.Vertices[i];
            var key = QuantizePos(mv.Position);
            if (!posMap.TryGetValue(key, out int newIdx))
            {
                newIdx = mesh.Vertices.Count;
                posMap[key] = newIdx;
                mesh.Vertices.Add(new EditableVertex
                {
                    Position = mv.Position,
                    Normal = mv.Normal.LengthSquared() > 1e-8f ? Vector3.Normalize(mv.Normal) : Vector3.UnitY,
                    UV = mv.TexCoord,
                    Color = mv.Color
                });
            }
            remap[i] = newIdx;
        }

        // パートごとに三角形を面として追加
        if (parsed.Parts.Count > 0)
        {
            for (int partIdx = 0; partIdx < parsed.Parts.Count; partIdx++)
            {
                var part = parsed.Parts[partIdx];
                int end = Math.Min(parsed.Indices.Length, part.IndexOffset + part.IndexCount);
                AddTrianglesRange(mesh, parsed, remap, part.IndexOffset, end, partIdx);
            }
        }
        else
        {
            AddTrianglesRange(mesh, parsed, remap, 0, parsed.Indices.Length, 0);
        }

        TryMergeCoplanarQuads(mesh);
        mesh.RebuildEdges(preserveLooseEdges: false);
        mesh.RecalculateNormals();
        return mesh;
    }

    private static (long, long, long) QuantizePos(Vector3 p)
    {
        const double invEps = 100000.0; // 1e-5 精度でウェルディング
        return (
            (long)Math.Round(p.X * invEps),
            (long)Math.Round(p.Y * invEps),
            (long)Math.Round(p.Z * invEps));
    }

    private static void AddTrianglesRange(
        EditableMesh mesh, ParsedModel parsed, int[] remap, int start, int end, int matIndex)
    {
        for (int idx = start; idx + 2 < end; idx += 3)
        {
            uint r0 = parsed.Indices[idx];
            uint r1 = parsed.Indices[idx + 1];
            uint r2 = parsed.Indices[idx + 2];
            if (r0 >= (uint)remap.Length || r1 >= (uint)remap.Length || r2 >= (uint)remap.Length)
                continue;

            int v0 = remap[r0];
            int v1 = remap[r1];
            int v2 = remap[r2];
            if (v0 == v1 || v1 == v2 || v2 == v0)
                continue;

            var p0 = mesh.Vertices[v0].Position;
            var p1 = mesh.Vertices[v1].Position;
            var p2 = mesh.Vertices[v2].Position;
            var cross = Vector3.Cross(p1 - p0, p2 - p0);
            float lenSq = cross.LengthSquared();
            Vector3 fNorm = lenSq > 1e-12f ? cross / MathF.Sqrt(lenSq) : Vector3.UnitY;

            // コーナー法線同士の差異または面法線との僅かな差異を検出し、高分割シリンダー等でもスムース法線を保持
            var rn0 = parsed.Vertices[r0].Normal;
            var rn1 = parsed.Vertices[r1].Normal;
            var rn2 = parsed.Vertices[r2].Normal;
            bool hasCornerNorms = rn0.LengthSquared() > 1e-8f && rn1.LengthSquared() > 1e-8f && rn2.LengthSquared() > 1e-8f;
            var n0 = rn0.LengthSquared() > 1e-8f ? Vector3.Normalize(rn0) : fNorm;
            var n1 = rn1.LengthSquared() > 1e-8f ? Vector3.Normalize(rn1) : fNorm;
            var n2 = rn2.LengthSquared() > 1e-8f ? Vector3.Normalize(rn2) : fNorm;
            bool isSmooth =
                Vector3.Dot(n0, n1) < 0.99999f ||
                Vector3.Dot(n1, n2) < 0.99999f ||
                Vector3.Dot(n2, n0) < 0.99999f ||
                Vector3.Dot(n0, fNorm) < 0.99995f ||
                Vector3.Dot(n1, fNorm) < 0.99995f ||
                Vector3.Dot(n2, fNorm) < 0.99995f;

            mesh.Faces.Add(new EditableFace
            {
                VertexIndices = new List<int> { v0, v1, v2 },
                FaceUVs = new List<Vector2>
                {
                    parsed.Vertices[r0].TexCoord,
                    parsed.Vertices[r1].TexCoord,
                    parsed.Vertices[r2].TexCoord
                },
                FaceCornerNormals = hasCornerNorms ? new List<Vector3> { n0, n1, n2 } : null,
                MaterialIndex = matIndex,
                Smooth = isSmooth,
                FaceNormal = fNorm
            });
        }
    }

    /// <summary>
    /// キューブや平面などの同一平面三角形ペアを四角形へ統合し、ループカット等が効きやすくする。
    /// </summary>
    private static void TryMergeCoplanarQuads(EditableMesh mesh)
    {
        // 対角辺 (lo, hi) -> (faceIdx, edgeLocalIdx, oppositeVertexIdx)
        var edgeOwners = new Dictionary<ulong, List<(int FaceIdx, int EdgeK)>>();

        static ulong Pack(int a, int b)
        {
            uint lo = (uint)Math.Min(a, b);
            uint hi = (uint)Math.Max(a, b);
            return ((ulong)lo << 32) | hi;
        }

        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var f = mesh.Faces[fi];
            if (f.VertexIndices.Count != 3) continue;
            for (int k = 0; k < 3; k++)
            {
                int a = f.VertexIndices[k];
                int b = f.VertexIndices[(k + 1) % 3];
                ulong key = Pack(a, b);
                if (!edgeOwners.TryGetValue(key, out var list))
                {
                    list = new List<(int, int)>(2);
                    edgeOwners[key] = list;
                }
                list.Add((fi, k));
            }
        }

        var merged = new bool[mesh.Faces.Count];
        var newFaces = new List<EditableFace>(mesh.Faces.Count);

        foreach (var kv in edgeOwners)
        {
            var list = kv.Value;
            if (list.Count != 2) continue;

            var (fA, eA) = list[0];
            var (fB, eB) = list[1];
            if (fA == fB || merged[fA] || merged[fB]) continue;

            var faceA = mesh.Faces[fA];
            var faceB = mesh.Faces[fB];
            if (faceA.MaterialIndex != faceB.MaterialIndex || faceA.Smooth != faceB.Smooth)
                continue;
            if (Vector3.Dot(faceA.FaceNormal, faceB.FaceNormal) < 0.9999f)
                continue;

            int a0 = faceA.VertexIndices[eA];
            int a1 = faceA.VertexIndices[(eA + 1) % 3];
            int aOpp = faceA.VertexIndices[(eA + 2) % 3];

            int b0 = faceB.VertexIndices[eB];
            int b1 = faceB.VertexIndices[(eB + 1) % 3];
            int bOpp = faceB.VertexIndices[(eB + 2) % 3];

            // 逆向きに辺を共有している場合のみ正常な 2-manifold ペア
            if (!(a0 == b1 && a1 == b0) || aOpp == bOpp)
                continue;

            // 共有頂点の UV が一致しているか確認 (UV シームは維持する)
            var uvA0 = faceA.FaceUVs[eA];
            var uvA1 = faceA.FaceUVs[(eA + 1) % 3];
            var uvB0 = faceB.FaceUVs[eB];
            var uvB1 = faceB.FaceUVs[(eB + 1) % 3];
            if (Vector2.DistanceSquared(uvA0, uvB1) > 1e-6f ||
                Vector2.DistanceSquared(uvA1, uvB0) > 1e-6f)
                continue;

            bool hasFnA = faceA.FaceCornerNormals != null && faceA.FaceCornerNormals.Count == 3;
            bool hasFnB = faceB.FaceCornerNormals != null && faceB.FaceCornerNormals.Count == 3;
            if (hasFnA != hasFnB) continue;
            List<Vector3>? mergedFn = null;
            if (hasFnA)
            {
                var fnA0 = faceA.FaceCornerNormals![eA];
                var fnA1 = faceA.FaceCornerNormals![(eA + 1) % 3];
                var fnB0 = faceB.FaceCornerNormals![eB];
                var fnB1 = faceB.FaceCornerNormals![(eB + 1) % 3];
                if (Vector3.Dot(fnA0, fnB1) < 0.9999f || Vector3.Dot(fnA1, fnB0) < 0.9999f)
                    continue;
                mergedFn = new List<Vector3>
                {
                    faceA.FaceCornerNormals![(eA + 2) % 3],
                    fnA0,
                    faceB.FaceCornerNormals![(eB + 2) % 3],
                    fnA1
                };
            }

            // クワッド順: aOpp -> a0 -> bOpp -> a1
            int q0 = aOpp, q1 = a0, q2 = bOpp, q3 = a1;
            if (!IsConvexPlanarQuad(
                    mesh.Vertices[q0].Position,
                    mesh.Vertices[q1].Position,
                    mesh.Vertices[q2].Position,
                    mesh.Vertices[q3].Position,
                    faceA.FaceNormal))
                continue;

            merged[fA] = true;
            merged[fB] = true;
            newFaces.Add(new EditableFace
            {
                VertexIndices = new List<int> { q0, q1, q2, q3 },
                FaceUVs = new List<Vector2>
                {
                    faceA.FaceUVs[(eA + 2) % 3],
                    uvA0,
                    faceB.FaceUVs[(eB + 2) % 3],
                    uvA1
                },
                FaceCornerNormals = mergedFn,
                MaterialIndex = faceA.MaterialIndex,
                Smooth = faceA.Smooth,
                FaceNormal = faceA.FaceNormal
            });
        }

        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            if (!merged[fi])
                newFaces.Add(mesh.Faces[fi]);
        }

        mesh.Faces.Clear();
        mesh.Faces.AddRange(newFaces);
    }

    private static bool IsConvexPlanarQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 norm)
    {
        float distPlane = MathF.Abs(Vector3.Dot(p2 - p0, norm));
        if (distPlane > 1e-4f) return false;

        Vector3 c0 = Vector3.Cross(p1 - p0, p3 - p0);
        Vector3 c1 = Vector3.Cross(p2 - p1, p0 - p1);
        Vector3 c2 = Vector3.Cross(p3 - p2, p1 - p2);
        Vector3 c3 = Vector3.Cross(p0 - p3, p2 - p3);

        return Vector3.Dot(c0, norm) > 1e-8f &&
               Vector3.Dot(c1, norm) > 1e-8f &&
               Vector3.Dot(c2, norm) > 1e-8f &&
               Vector3.Dot(c3, norm) > 1e-8f;
    }

    /// <summary>
    /// 現在の編集メッシュを GPU アップロード可能な ParsedModel へ変換する。
    /// </summary>
    public ParsedModel ToParsedModel()
    {
        RecalculateNormals();

        int matCount = Math.Max(1, Materials.Count);
        var groupedFaces = new List<EditableFace>[matCount];
        for (int i = 0; i < matCount; i++)
            groupedFaces[i] = new List<EditableFace>();

        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var face = Faces[fi];
            if (face.VertexIndices.Count < 3) continue;
            int mi = Math.Clamp(face.MaterialIndex, 0, matCount - 1);
            groupedFaces[mi].Add(face);
        }

        var outVerts = new List<ModelVertex>(Faces.Count * 4);
        var outIndices = new List<uint>(Faces.Count * 6);
        var outParts = new List<ParsedModelPart>(matCount);

        for (int mi = 0; mi < matCount; mi++)
        {
            var list = groupedFaces[mi];
            var mat = mi < Materials.Count ? Materials[mi] : new EditableMaterial();
            int indexOffset = outIndices.Count;

            for (int fi = 0; fi < list.Count; fi++)
            {
                var face = list[fi];
                int n = face.VertexIndices.Count;
                int baseVertex = outVerts.Count;

                for (int k = 0; k < n; k++)
                {
                    int vi = face.VertexIndices[k];
                    var v = Vertices[vi];
                    Vector2 uv = k < face.FaceUVs.Count ? face.FaceUVs[k] : v.UV;
                    Vector3 normal = (face.FaceCornerNormals != null && k < face.FaceCornerNormals.Count && face.FaceCornerNormals[k].LengthSquared() > 1e-10f)
                        ? face.FaceCornerNormals[k]
                        : (face.Smooth ? v.Normal : face.FaceNormal);
                    if (normal.LengthSquared() < 1e-10f) normal = Vector3.UnitY;

                    outVerts.Add(new ModelVertex
                    {
                        Position = v.Position,
                        Normal = Vector3.Normalize(normal),
                        TexCoord = uv,
                        Color = v.Color
                    });
                }

                TriangulatePolygonInto(face, outVerts, baseVertex, outIndices);
            }

            int indexCount = outIndices.Count - indexOffset;
            if (indexCount > 0)
            {
                outParts.Add(new ParsedModelPart
                {
                    Name = mat.Name,
                    IndexOffset = indexOffset,
                    IndexCount = indexCount,
                    BaseColor = mat.BaseColor,
                    EmissiveColor = mat.EmissiveColor,
                    Metallic = mat.Metallic,
                    Roughness = mat.Roughness,
                    Transmission = mat.Transmission,
                    Ior = mat.Ior,
                    ForceOpaque = mat.ForceOpaque,
                    CullBackface = mat.CullBackface,
                    CastShadow = mat.CastShadow,
                    SourcePartIndex = mat.SourcePartIndex,
                    MaterialSlotIndex = mi,
                    NodeGraph = mat.NodeGraph
                });
            }
        }

        ComputeCurrentBounds(out var geomMin, out var geomMax, out var geomCenter, out float geomScale);

        return new ParsedModel
        {
            Vertices = outVerts.ToArray(),
            Indices = outIndices.ToArray(),
            Parts = outParts,
            BoundsMin = geomMin,
            BoundsMax = geomMax,
            ModelCenter = HasRefBounds ? RefCenter : geomCenter,
            ModelScale = HasRefBounds ? RefScale : geomScale
        };
    }

    private static void TriangulatePolygonInto(
        EditableFace face, List<ModelVertex> verts, int baseVertex, List<uint> outIndices)
    {
        int n = face.VertexIndices.Count;
        if (n == 3)
        {
            outIndices.Add((uint)baseVertex);
            outIndices.Add((uint)(baseVertex + 1));
            outIndices.Add((uint)(baseVertex + 2));
            return;
        }

        if (n == 4)
        {
            // 最短対角線で分割し、非平面クワッドの捻れを抑制する
            float d02 = Vector3.DistanceSquared(verts[baseVertex].Position, verts[baseVertex + 2].Position);
            float d13 = Vector3.DistanceSquared(verts[baseVertex + 1].Position, verts[baseVertex + 3].Position);
            if (d02 <= d13)
            {
                outIndices.Add((uint)baseVertex);
                outIndices.Add((uint)(baseVertex + 1));
                outIndices.Add((uint)(baseVertex + 2));
                outIndices.Add((uint)baseVertex);
                outIndices.Add((uint)(baseVertex + 2));
                outIndices.Add((uint)(baseVertex + 3));
            }
            else
            {
                outIndices.Add((uint)(baseVertex + 1));
                outIndices.Add((uint)(baseVertex + 2));
                outIndices.Add((uint)(baseVertex + 3));
                outIndices.Add((uint)(baseVertex + 1));
                outIndices.Add((uint)(baseVertex + 3));
                outIndices.Add((uint)baseVertex);
            }
            return;
        }

        // N-gon 用 Ear Clipping (2D 投影) + 退化時ファンフォールバック
        Vector3 normal = face.FaceNormal;
        if (normal.LengthSquared() < 1e-10f) normal = Vector3.UnitY;
        Vector3 tangent = MathF.Abs(normal.Y) < 0.9f
            ? Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal))
            : Vector3.Normalize(Vector3.Cross(Vector3.UnitX, normal));
        Vector3 bitangent = Vector3.Cross(normal, tangent);

        var pts2D = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            var p = verts[baseVertex + i].Position;
            pts2D[i] = new Vector2(Vector3.Dot(p, tangent), Vector3.Dot(p, bitangent));
        }

        var rem = new List<int>(n);
        for (int i = 0; i < n; i++) rem.Add(i);

        // 符号付き面積が負なら向きを反転認識
        float signedArea = 0f;
        for (int i = 0; i < n; i++)
        {
            var a = pts2D[i];
            var b = pts2D[(i + 1) % n];
            signedArea += a.X * b.Y - b.X * a.Y;
        }
        float windingSign = signedArea >= 0f ? 1f : -1f;

        int guard = n * n;
        while (rem.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            int m = rem.Count;
            for (int k = 0; k < m; k++)
            {
                int prev = rem[(k - 1 + m) % m];
                int curr = rem[k];
                int next = rem[(k + 1) % m];

                var a = pts2D[prev];
                var b = pts2D[curr];
                var c = pts2D[next];

                float cross2 = ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) * windingSign;
                if (cross2 <= 1e-7f) continue;

                bool containsOther = false;
                for (int j = 0; j < m; j++)
                {
                    int idx = rem[j];
                    if (idx == prev || idx == curr || idx == next) continue;
                    if (PointInTriangle2D(pts2D[idx], a, b, c))
                    {
                        containsOther = true;
                        break;
                    }
                }
                if (containsOther) continue;

                outIndices.Add((uint)(baseVertex + prev));
                outIndices.Add((uint)(baseVertex + curr));
                outIndices.Add((uint)(baseVertex + next));
                rem.RemoveAt(k);
                clipped = true;
                break;
            }
            if (!clipped) break;
        }

        if (rem.Count == 3)
        {
            outIndices.Add((uint)(baseVertex + rem[0]));
            outIndices.Add((uint)(baseVertex + rem[1]));
            outIndices.Add((uint)(baseVertex + rem[2]));
        }
        else if (rem.Count > 3)
        {
            for (int i = 1; i + 1 < rem.Count; i++)
            {
                outIndices.Add((uint)(baseVertex + rem[0]));
                outIndices.Add((uint)(baseVertex + rem[i]));
                outIndices.Add((uint)(baseVertex + rem[i + 1]));
            }
        }
    }

    private static bool PointInTriangle2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Sign2D(p, a, b);
        float d2 = Sign2D(p, b, c);
        float d3 = Sign2D(p, c, a);
        bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(hasNeg && hasPos);
    }

    private static float Sign2D(Vector2 p1, Vector2 p2, Vector2 p3)
        => (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);

    #region プリミティブ生成

    public static EditableMesh CreateCube(float size = 1f)
    {
        var mesh = new EditableMesh();
        mesh.Materials.Add(new EditableMaterial { Name = "Default" });
        AppendPrimitive(mesh, EditablePrimitiveType.Cube, Vector3.Zero, size);
        mesh.CaptureRefBoundsFromCurrentGeometry();
        return mesh;
    }

    public static EditableMesh CreateSingleVertex()
    {
        var mesh = new EditableMesh();
        mesh.Materials.Add(new EditableMaterial { Name = "Default" });
        mesh.Vertices.Add(new EditableVertex { Position = Vector3.Zero });
        mesh.RefBoundsMin = new Vector3(-0.5f, -0.5f, -0.5f);
        mesh.RefBoundsMax = new Vector3(0.5f, 0.5f, 0.5f);
        mesh.RefCenter = Vector3.Zero;
        mesh.RefScale = 1f;
        mesh.HasRefBounds = true;
        return mesh;
    }

    public static EditableMesh CreatePrimitive(
        EditablePrimitiveType type,
        float size = 1f,
        int segments = 24,
        int rings = 16,
        int icoSubdivisions = 2,
        float tubeRadiusRatio = 0.35f,
        bool smoothShading = true,
        EditableMaterial? material = null)
    {
        var mesh = new EditableMesh();
        mesh.Materials.Add(material?.Clone() ?? new EditableMaterial { Name = "Default" });
        AppendPrimitiveDetailed(
            mesh, type, Vector3.Zero, size, materialIndex: 0,
            segments: segments, rings: rings,
            icoSubdivisions: icoSubdivisions, tubeRadiusRatio: tubeRadiusRatio,
            smoothShading: smoothShading);
        mesh.CaptureRefBoundsFromCurrentGeometry();
        return mesh;
    }

    public static List<int> AppendPrimitive(
        EditableMesh mesh, EditablePrimitiveType type, Vector3 center, float size = 0.5f)
        => AppendPrimitiveDetailed(mesh, type, center, size);

    public static List<int> AppendPrimitiveDetailed(
        EditableMesh mesh,
        EditablePrimitiveType type,
        Vector3 center,
        float size = 0.5f,
        int materialIndex = 0,
        int segments = 24,
        int rings = 16,
        int icoSubdivisions = 2,
        float tubeRadiusRatio = 0.35f,
        bool smoothShading = true)
    {
        var addedVerts = new List<int>();
        if (mesh.Materials.Count == 0)
            mesh.Materials.Add(new EditableMaterial { Name = "Default" });
        int matIdx = Math.Clamp(materialIndex, 0, mesh.Materials.Count - 1);

        float h = size * 0.5f;

        switch (type)
        {
            case EditablePrimitiveType.Vertex:
            {
                int idx = mesh.Vertices.Count;
                mesh.Vertices.Add(new EditableVertex { Position = center });
                addedVerts.Add(idx);
                break;
            }

            case EditablePrimitiveType.Plane:
            {
                int b = mesh.Vertices.Count;
                Vector3[] pts =
                {
                    center + new Vector3(-h, 0f, h),
                    center + new Vector3(h, 0f, h),
                    center + new Vector3(h, 0f, -h),
                    center + new Vector3(-h, 0f, -h)
                };
                Vector2[] uvs = { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };
                for (int i = 0; i < 4; i++)
                {
                    mesh.Vertices.Add(new EditableVertex { Position = pts[i], UV = uvs[i] });
                    addedVerts.Add(b + i);
                }
                mesh.Faces.Add(new EditableFace
                {
                    VertexIndices = new List<int> { b, b + 1, b + 2, b + 3 },
                    FaceUVs = new List<Vector2>(uvs),
                    MaterialIndex = matIdx,
                    Smooth = false
                });
                break;
            }

            case EditablePrimitiveType.Circle:
            {
                int segs = Math.Clamp(segments, 3, 64);
                int b = mesh.Vertices.Count;
                var verts = new List<int>(segs);
                var uvs = new List<Vector2>(segs);
                for (int s = 0; s < segs; s++)
                {
                    float theta = 2f * MathF.PI * s / segs;
                    float cos = MathF.Cos(theta);
                    float sin = MathF.Sin(theta);
                    var uv = new Vector2(0.5f + 0.5f * cos, 0.5f - 0.5f * sin);
                    int idx = mesh.Vertices.Count;
                    mesh.Vertices.Add(new EditableVertex
                    {
                        Position = center + new Vector3(cos * h, sin * h, 0f),
                        UV = uv
                    });
                    addedVerts.Add(idx);
                    verts.Add(b + s);
                    uvs.Add(uv);
                }
                mesh.Faces.Add(new EditableFace
                {
                    VertexIndices = verts,
                    FaceUVs = uvs,
                    MaterialIndex = matIdx,
                    Smooth = false
                });
                break;
            }

            case EditablePrimitiveType.Cube:
            {
                int b = mesh.Vertices.Count;
                Vector3[] pts =
                {
                    center + new Vector3(-h, -h, -h), // 0
                    center + new Vector3( h, -h, -h), // 1
                    center + new Vector3( h,  h, -h), // 2
                    center + new Vector3(-h,  h, -h), // 3
                    center + new Vector3(-h, -h,  h), // 4
                    center + new Vector3( h, -h,  h), // 5
                    center + new Vector3( h,  h,  h), // 6
                    center + new Vector3(-h,  h,  h)  // 7
                };
                for (int i = 0; i < 8; i++)
                {
                    mesh.Vertices.Add(new EditableVertex { Position = pts[i] });
                    addedVerts.Add(b + i);
                }
                // 外向き法線 (右手系 CCW: Cross(p1-p0, p2-p0) が外向き)
                int[][] quads =
                {
                    new[] { b + 4, b + 5, b + 6, b + 7 }, // +Z
                    new[] { b + 1, b + 0, b + 3, b + 2 }, // -Z
                    new[] { b + 5, b + 1, b + 2, b + 6 }, // +X
                    new[] { b + 0, b + 4, b + 7, b + 3 }, // -X
                    new[] { b + 7, b + 6, b + 2, b + 3 }, // +Y
                    new[] { b + 0, b + 1, b + 5, b + 4 }  // -Y
                };
                var quadUvs = new List<Vector2> { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };
                foreach (var q in quads)
                {
                    mesh.Faces.Add(new EditableFace
                    {
                        VertexIndices = new List<int>(q),
                        FaceUVs = new List<Vector2>(quadUvs),
                        MaterialIndex = matIdx,
                        Smooth = false
                    });
                }
                break;
            }

            case EditablePrimitiveType.UvSphere:
            {
                int segs = Math.Clamp(segments, 3, 64);
                int rCount = Math.Clamp(rings, 3, 48);
                int topV = mesh.Vertices.Count;
                mesh.Vertices.Add(new EditableVertex { Position = center + new Vector3(0f, h, 0f), UV = new(0.5f, 0f) });
                addedVerts.Add(topV);

                int gridStart = mesh.Vertices.Count;
                for (int r = 1; r < rCount; r++)
                {
                    float phi = MathF.PI * r / rCount;
                    float y = MathF.Cos(phi) * h;
                    float rad = MathF.Sin(phi) * h;
                    for (int s = 0; s < segs; s++)
                    {
                        float theta = 2f * MathF.PI * s / segs;
                        float x = MathF.Cos(theta) * rad;
                        float z = -MathF.Sin(theta) * rad;
                        int idx = mesh.Vertices.Count;
                        mesh.Vertices.Add(new EditableVertex
                        {
                            Position = center + new Vector3(x, y, z),
                            UV = new Vector2((float)s / segs, (float)r / rCount)
                        });
                        addedVerts.Add(idx);
                    }
                }

                int botV = mesh.Vertices.Count;
                mesh.Vertices.Add(new EditableVertex { Position = center + new Vector3(0f, -h, 0f), UV = new(0.5f, 1f) });
                addedVerts.Add(botV);

                // 上極キャップ
                for (int s = 0; s < segs; s++)
                {
                    int s1 = (s + 1) % segs;
                    int v1 = gridStart + s;
                    int v2 = gridStart + s1;
                    mesh.Faces.Add(new EditableFace
                    {
                        VertexIndices = new List<int> { topV, v1, v2 },
                        FaceUVs = new List<Vector2> { mesh.Vertices[topV].UV, mesh.Vertices[v1].UV, mesh.Vertices[v2].UV },
                        MaterialIndex = matIdx,
                        Smooth = smoothShading
                    });
                }

                // 中間バンド (四角形)
                for (int r = 0; r < rCount - 2; r++)
                {
                    int row0 = gridStart + r * segs;
                    int row1 = gridStart + (r + 1) * segs;
                    for (int s = 0; s < segs; s++)
                    {
                        int s1 = (s + 1) % segs;
                        int v00 = row0 + s;
                        int v10 = row1 + s;
                        int v11 = row1 + s1;
                        int v01 = row0 + s1;
                        mesh.Faces.Add(new EditableFace
                        {
                            VertexIndices = new List<int> { v00, v10, v11, v01 },
                            FaceUVs = new List<Vector2>
                            {
                                mesh.Vertices[v00].UV, mesh.Vertices[v10].UV,
                                mesh.Vertices[v11].UV, mesh.Vertices[v01].UV
                            },
                            MaterialIndex = matIdx,
                            Smooth = smoothShading
                        });
                    }
                }

                // 下極キャップ
                int lastRow = gridStart + (rCount - 2) * segs;
                for (int s = 0; s < segs; s++)
                {
                    int s1 = (s + 1) % segs;
                    int v1 = lastRow + s1;
                    int v2 = lastRow + s;
                    mesh.Faces.Add(new EditableFace
                    {
                        VertexIndices = new List<int> { botV, v1, v2 },
                        FaceUVs = new List<Vector2> { mesh.Vertices[botV].UV, mesh.Vertices[v1].UV, mesh.Vertices[v2].UV },
                        MaterialIndex = matIdx,
                        Smooth = smoothShading
                    });
                }
                break;
            }

            case EditablePrimitiveType.IcoSphere:
            {
                int sub = Math.Clamp(icoSubdivisions, 0, 4);
                float t = (1f + MathF.Sqrt(5f)) * 0.5f;
                int baseV = mesh.Vertices.Count;

                Vector3[] baseDirs =
                {
                    Vector3.Normalize(new Vector3(-1f,  t,  0f)),
                    Vector3.Normalize(new Vector3( 1f,  t,  0f)),
                    Vector3.Normalize(new Vector3(-1f, -t,  0f)),
                    Vector3.Normalize(new Vector3( 1f, -t,  0f)),
                    Vector3.Normalize(new Vector3( 0f, -1f,  t)),
                    Vector3.Normalize(new Vector3( 0f,  1f,  t)),
                    Vector3.Normalize(new Vector3( 0f, -1f, -t)),
                    Vector3.Normalize(new Vector3( 0f,  1f, -t)),
                    Vector3.Normalize(new Vector3( t,  0f, -1f)),
                    Vector3.Normalize(new Vector3( t,  0f,  1f)),
                    Vector3.Normalize(new Vector3(-t,  0f, -1f)),
                    Vector3.Normalize(new Vector3(-t,  0f,  1f)),
                };

                Vector2 SphereUv(Vector3 d) => new(
                    MathF.Atan2(-d.Z, d.X) / (2f * MathF.PI) + 0.5f,
                    MathF.Acos(Math.Clamp(d.Y, -1f, 1f)) / MathF.PI);

                for (int i = 0; i < baseDirs.Length; i++)
                {
                    mesh.Vertices.Add(new EditableVertex
                    {
                        Position = center + baseDirs[i] * h,
                        UV = SphereUv(baseDirs[i])
                    });
                    addedVerts.Add(baseV + i);
                }

                var tris = new List<(int A, int B, int C)>
                {
                    (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
                    (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
                    (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
                    (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)
                };
                for (int i = 0; i < tris.Count; i++)
                    tris[i] = (tris[i].A + baseV, tris[i].B + baseV, tris[i].C + baseV);

                var midCache = new Dictionary<ulong, int>();
                int GetMid(int v0, int v1)
                {
                    ulong key = ((ulong)(uint)Math.Min(v0, v1) << 32) | (uint)Math.Max(v0, v1);
                    if (midCache.TryGetValue(key, out int cached)) return cached;
                    Vector3 dir = Vector3.Normalize(
                        ((mesh.Vertices[v0].Position - center) + (mesh.Vertices[v1].Position - center)) * 0.5f);
                    int idx = mesh.Vertices.Count;
                    mesh.Vertices.Add(new EditableVertex
                    {
                        Position = center + dir * h,
                        UV = SphereUv(dir)
                    });
                    addedVerts.Add(idx);
                    midCache[key] = idx;
                    return idx;
                }

                for (int step = 0; step < sub; step++)
                {
                    var nextTris = new List<(int A, int B, int C)>(tris.Count * 4);
                    foreach (var (a, b, c) in tris)
                    {
                        int ab = GetMid(a, b);
                        int bc = GetMid(b, c);
                        int ca = GetMid(c, a);
                        nextTris.Add((a, ab, ca));
                        nextTris.Add((b, bc, ab));
                        nextTris.Add((c, ca, bc));
                        nextTris.Add((ab, bc, ca));
                    }
                    tris = nextTris;
                }

                foreach (var (a, b, c) in tris)
                {
                    Vector3 pa = mesh.Vertices[a].Position;
                    Vector3 pb = mesh.Vertices[b].Position;
                    Vector3 pc = mesh.Vertices[c].Position;
                    Vector3 centroidDir = (pa + pb + pc) / 3f - center;
                    int v1 = b, v2 = c;
                    if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), centroidDir) < 0f)
                    {
                        v1 = c;
                        v2 = b;
                    }
                    mesh.Faces.Add(new EditableFace
                    {
                        VertexIndices = new List<int> { a, v1, v2 },
                        FaceUVs = new List<Vector2> { mesh.Vertices[a].UV, mesh.Vertices[v1].UV, mesh.Vertices[v2].UV },
                        MaterialIndex = matIdx,
                        Smooth = smoothShading
                    });
                }
                break;
            }

            case EditablePrimitiveType.Cylinder:
            {
                int segs = Math.Clamp(segments, 3, 64);
                int topStart = mesh.Vertices.Count;
                for (int s = 0; s < segs; s++)
                {
                    float theta = 2f * MathF.PI * s / segs;
                    mesh.Vertices.Add(new EditableVertex
                    {
                        Position = center + new Vector3(MathF.Cos(theta) * h, h, -MathF.Sin(theta) * h),
                        UV = new Vector2((float)s / segs, 0f)
                    });
                    addedVerts.Add(topStart + s);
                }

                int botStart = mesh.Vertices.Count;
                for (int s = 0; s < segs; s++)
                {
                    float theta = 2f * MathF.PI * s / segs;
                    mesh.Vertices.Add(new EditableVertex
                    {
                        Position = center + new Vector3(MathF.Cos(theta) * h, -h, -MathF.Sin(theta) * h),
                        UV = new Vector2((float)s / segs, 1f)
                    });
                    addedVerts.Add(botStart + s);
                }

                // 側面クワッド
                for (int s = 0; s < segs; s++)
                {
                    int s1 = (s + 1) % segs;
                    int t0 = topStart + s, b0 = botStart + s, b1 = botStart + s1, t1 = topStart + s1;
                    mesh.Faces.Add(new EditableFace
                    {
                        VertexIndices = new List<int> { t0, b0, b1, t1 },
                        FaceUVs = new List<Vector2>
                        {
                            mesh.Vertices[t0].UV, mesh.Vertices[b0].UV, mesh.Vertices[b1].UV, mesh.Vertices[t1].UV
                        },
                        MaterialIndex = matIdx,
                        Smooth = smoothShading
                    });
                }

                // 上面・下面 N-gon キャップ
                var topCap = new List<int>(segs);
                var topUvs = new List<Vector2>(segs);
                var botCap = new List<int>(segs);
                var botUvs = new List<Vector2>(segs);
                for (int s = 0; s < segs; s++)
                {
                    float theta = 2f * MathF.PI * s / segs;
                    topCap.Add(topStart + s);
                    topUvs.Add(new Vector2(0.5f + 0.5f * MathF.Cos(theta), 0.5f + 0.5f * MathF.Sin(theta)));

                    int bs = segs - 1 - s;
                    float bTheta = 2f * MathF.PI * bs / segs;
                    botCap.Add(botStart + bs);
                    botUvs.Add(new Vector2(0.5f + 0.5f * MathF.Cos(bTheta), 0.5f + 0.5f * MathF.Sin(bTheta)));
                }
                mesh.Faces.Add(new EditableFace
                {
                    VertexIndices = topCap,
                    FaceUVs = topUvs,
                    MaterialIndex = matIdx,
                    Smooth = false
                });
                mesh.Faces.Add(new EditableFace
                {
                    VertexIndices = botCap,
                    FaceUVs = botUvs,
                    MaterialIndex = matIdx,
                    Smooth = false
                });
                break;
            }

            case EditablePrimitiveType.Cone:
            {
                int segs = Math.Clamp(segments, 3, 64);
                int tipV = mesh.Vertices.Count;
                mesh.Vertices.Add(new EditableVertex { Position = center + new Vector3(0f, h, 0f), UV = new(0.5f, 0f) });
                addedVerts.Add(tipV);

                int botStart = mesh.Vertices.Count;
                for (int s = 0; s < segs; s++)
                {
                    float theta = 2f * MathF.PI * s / segs;
                    mesh.Vertices.Add(new EditableVertex
                    {
                        Position = center + new Vector3(MathF.Cos(theta) * h, -h, -MathF.Sin(theta) * h),
                        UV = new Vector2((float)s / segs, 1f)
                    });
                    addedVerts.Add(botStart + s);
                }

                for (int s = 0; s < segs; s++)
                {
                    int s1 = (s + 1) % segs;
                    int b0 = botStart + s, b1 = botStart + s1;
                    mesh.Faces.Add(new EditableFace
                    {
                        VertexIndices = new List<int> { tipV, b0, b1 },
                        FaceUVs = new List<Vector2> { mesh.Vertices[tipV].UV, mesh.Vertices[b0].UV, mesh.Vertices[b1].UV },
                        MaterialIndex = matIdx,
                        Smooth = smoothShading
                    });
                }

                var botCap = new List<int>(segs);
                var botUvs = new List<Vector2>(segs);
                for (int s = segs - 1; s >= 0; s--)
                {
                    float theta = 2f * MathF.PI * s / segs;
                    botCap.Add(botStart + s);
                    botUvs.Add(new Vector2(0.5f + 0.5f * MathF.Cos(theta), 0.5f + 0.5f * MathF.Sin(theta)));
                }
                mesh.Faces.Add(new EditableFace
                {
                    VertexIndices = botCap,
                    FaceUVs = botUvs,
                    MaterialIndex = matIdx,
                    Smooth = false
                });
                break;
            }

            case EditablePrimitiveType.Torus:
            {
                int majorSegs = Math.Clamp(segments, 3, 64);
                int minorSegs = Math.Clamp(rings, 3, 48);
                float tubeRatio = Math.Clamp(tubeRadiusRatio, 0.05f, 0.90f);
                float majorR = h / (1f + tubeRatio);
                float minorR = majorR * tubeRatio;

                int startV = mesh.Vertices.Count;
                for (int u = 0; u < majorSegs; u++)
                {
                    float phi = 2f * MathF.PI * u / majorSegs;
                    Vector3 radial = new(MathF.Cos(phi), 0f, -MathF.Sin(phi));
                    for (int v = 0; v < minorSegs; v++)
                    {
                        float theta = 2f * MathF.PI * v / minorSegs;
                        Vector3 p = center + radial * (majorR + minorR * MathF.Cos(theta)) + Vector3.UnitY * (minorR * MathF.Sin(theta));
                        int idx = mesh.Vertices.Count;
                        mesh.Vertices.Add(new EditableVertex
                        {
                            Position = p,
                            UV = new Vector2((float)u / majorSegs, (float)v / minorSegs)
                        });
                        addedVerts.Add(idx);
                    }
                }

                for (int u = 0; u < majorSegs; u++)
                {
                    int u1 = (u + 1) % majorSegs;
                    for (int v = 0; v < minorSegs; v++)
                    {
                        int v1 = (v + 1) % minorSegs;
                        int i00 = startV + u * minorSegs + v;
                        int i10 = startV + u1 * minorSegs + v;
                        int i11 = startV + u1 * minorSegs + v1;
                        int i01 = startV + u * minorSegs + v1;
                        mesh.Faces.Add(new EditableFace
                        {
                            VertexIndices = new List<int> { i00, i10, i11, i01 },
                            FaceUVs = new List<Vector2>
                            {
                                mesh.Vertices[i00].UV, mesh.Vertices[i10].UV,
                                mesh.Vertices[i11].UV, mesh.Vertices[i01].UV
                            },
                            MaterialIndex = matIdx,
                            Smooth = smoothShading
                        });
                    }
                }
                break;
            }
        }

        mesh.RebuildEdges(preserveLooseEdges: true);
        mesh.RecalculateNormals();
        return addedVerts;
    }

    #endregion

    #region シリアライズ (EmbeddedMeshData)

    private sealed class MeshDto
    {
        public int v { get; set; } = 1;
        public string? sf { get; set; }
        public float[] p { get; set; } = Array.Empty<float>();
        public float[] u { get; set; } = Array.Empty<float>();
        public int[][] f { get; set; } = Array.Empty<int[]>();
        public int[] fm { get; set; } = Array.Empty<int>();
        public byte[] fs { get; set; } = Array.Empty<byte>();
        public float[][] fu { get; set; } = Array.Empty<float[]>();
        public float[][]? fn { get; set; }
        public int[] le { get; set; } = Array.Empty<int>();
        public MaterialDto[]? m { get; set; }
        public bool hr { get; set; }
        public float[]? rc { get; set; }
        public float rs { get; set; } = 1f;
        public float[]? rb { get; set; }
    }

    private sealed class MaterialDto
    {
        public string n { get; set; } = "Material";
        public float[] bc { get; set; } = { 1f, 1f, 1f, 1f };
        public float[] ec { get; set; } = { 0f, 0f, 0f, 0f };
        public float mt { get; set; }
        public float rg { get; set; } = 0.5f;
        public float tr { get; set; }
        public float ir { get; set; } = 1.5f;
        public bool fo { get; set; }
        public bool cb { get; set; }
        public bool cs { get; set; } = true;
        public int? sp { get; set; }
        public Shading.MaterialNodeGraph? ng { get; set; }
    }

    public string Serialize()
    {
        var dto = new MeshDto
        {
            v = 1,
            sf = SourceFilePath,
            p = new float[Vertices.Count * 3],
            u = new float[Vertices.Count * 2],
            f = new int[Faces.Count][],
            fm = new int[Faces.Count],
            fs = new byte[Faces.Count],
            fu = new float[Faces.Count][],
            hr = HasRefBounds,
            rc = new[] { RefCenter.X, RefCenter.Y, RefCenter.Z },
            rs = RefScale,
            rb = new[]
            {
                RefBoundsMin.X, RefBoundsMin.Y, RefBoundsMin.Z,
                RefBoundsMax.X, RefBoundsMax.Y, RefBoundsMax.Z
            }
        };

        for (int i = 0; i < Vertices.Count; i++)
        {
            var vp = Vertices[i].Position;
            dto.p[i * 3] = vp.X;
            dto.p[i * 3 + 1] = vp.Y;
            dto.p[i * 3 + 2] = vp.Z;

            var vu = Vertices[i].UV;
            dto.u[i * 2] = vu.X;
            dto.u[i * 2 + 1] = vu.Y;
        }

        bool hasAnyCornerNormals = false;
        for (int fi = 0; fi < Faces.Count; fi++)
        {
            if (Faces[fi].FaceCornerNormals != null && Faces[fi].FaceCornerNormals!.Count == Faces[fi].VertexIndices.Count)
            {
                hasAnyCornerNormals = true;
                break;
            }
        }
        if (hasAnyCornerNormals)
            dto.fn = new float[Faces.Count][];

        var faceEdges = new HashSet<ulong>();
        static ulong Pack(int a, int b) => ((ulong)(uint)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

        for (int fi = 0; fi < Faces.Count; fi++)
        {
            var face = Faces[fi];
            dto.f[fi] = face.VertexIndices.ToArray();
            dto.fm[fi] = face.MaterialIndex;
            dto.fs[fi] = face.Smooth ? (byte)1 : (byte)0;

            int uvLen = face.FaceUVs.Count;
            if (uvLen > 0)
            {
                var fuArr = new float[uvLen * 2];
                for (int k = 0; k < uvLen; k++)
                {
                    fuArr[k * 2] = face.FaceUVs[k].X;
                    fuArr[k * 2 + 1] = face.FaceUVs[k].Y;
                }
                dto.fu[fi] = fuArr;
            }
            else
            {
                dto.fu[fi] = Array.Empty<float>();
            }

            if (dto.fn != null)
            {
                if (face.FaceCornerNormals != null && face.FaceCornerNormals.Count == face.VertexIndices.Count)
                {
                    int cCount = face.FaceCornerNormals.Count;
                    var fnArr = new float[cCount * 3];
                    for (int k = 0; k < cCount; k++)
                    {
                        var cn = face.FaceCornerNormals[k];
                        fnArr[k * 3] = MathF.Round(cn.X, 5);
                        fnArr[k * 3 + 1] = MathF.Round(cn.Y, 5);
                        fnArr[k * 3 + 2] = MathF.Round(cn.Z, 5);
                    }
                    dto.fn[fi] = fnArr;
                }
                else
                {
                    dto.fn[fi] = Array.Empty<float>();
                }
            }

            int n = face.VertexIndices.Count;
            for (int k = 0; k < n; k++)
                faceEdges.Add(Pack(face.VertexIndices[k], face.VertexIndices[(k + 1) % n]));
        }

        var looseList = new List<int>();
        for (int ei = 0; ei < Edges.Count; ei++)
        {
            var e = Edges[ei];
            if (!faceEdges.Contains(Pack(e.V0, e.V1)))
            {
                looseList.Add(e.V0);
                looseList.Add(e.V1);
            }
        }
        dto.le = looseList.ToArray();

        if (Materials.Count > 0)
        {
            dto.m = new MaterialDto[Materials.Count];
            for (int i = 0; i < Materials.Count; i++)
            {
                var m = Materials[i];
                dto.m[i] = new MaterialDto
                {
                    n = m.Name,
                    bc = new[] { m.BaseColor.X, m.BaseColor.Y, m.BaseColor.Z, m.BaseColor.W },
                    ec = new[] { m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z, m.EmissiveColor.W },
                    mt = m.Metallic,
                    rg = m.Roughness,
                    tr = m.Transmission,
                    ir = m.Ior,
                    fo = m.ForceOpaque,
                    cb = m.CullBackface,
                    cs = m.CastShadow,
                    sp = m.SourcePartIndex,
                    ng = m.NodeGraph
                };
            }
        }

        return MagicPrefix + JsonSerializer.Serialize(dto);
    }

    public static bool TryReadSourceFilePath(string? serialized, out string? sourceFilePath)
    {
        sourceFilePath = null;
        if (string.IsNullOrWhiteSpace(serialized) || !serialized.StartsWith(MagicPrefix, StringComparison.Ordinal))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(serialized.AsMemory(MagicPrefix.Length));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (doc.RootElement.TryGetProperty("sf", out var sfProp) && sfProp.ValueKind == JsonValueKind.String)
                sourceFilePath = sfProp.GetString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDeserialize(string? data, out EditableMesh? mesh)
    {
        mesh = null;
        if (string.IsNullOrWhiteSpace(data)) return false;

        string json = data.StartsWith(MagicPrefix, StringComparison.Ordinal)
            ? data.Substring(MagicPrefix.Length)
            : data;

        try
        {
            var dto = JsonSerializer.Deserialize<MeshDto>(json);
            if (dto == null || dto.p == null) return false;

            var result = new EditableMesh
            {
                SourceFilePath = dto.sf,
                HasRefBounds = dto.hr,
                RefScale = dto.rs > 1e-6f ? dto.rs : 1f
            };
            if (dto.rc is { Length: 3 })
                result.RefCenter = new Vector3(dto.rc[0], dto.rc[1], dto.rc[2]);
            if (dto.rb is { Length: 6 })
            {
                result.RefBoundsMin = new Vector3(dto.rb[0], dto.rb[1], dto.rb[2]);
                result.RefBoundsMax = new Vector3(dto.rb[3], dto.rb[4], dto.rb[5]);
            }

            int vCount = dto.p.Length / 3;
            result.Vertices.Capacity = vCount;
            for (int i = 0; i < vCount; i++)
            {
                Vector2 uv = (dto.u != null && i * 2 + 1 < dto.u.Length)
                    ? new Vector2(dto.u[i * 2], dto.u[i * 2 + 1])
                    : Vector2.Zero;
                result.Vertices.Add(new EditableVertex
                {
                    Position = new Vector3(dto.p[i * 3], dto.p[i * 3 + 1], dto.p[i * 3 + 2]),
                    UV = uv
                });
            }

            if (dto.m != null && dto.m.Length > 0)
            {
                for (int i = 0; i < dto.m.Length; i++)
                {
                    var md = dto.m[i];
                    result.Materials.Add(new EditableMaterial
                    {
                        Name = string.IsNullOrWhiteSpace(md.n) ? $"Material.{i:D3}" : md.n,
                        BaseColor = md.bc is { Length: 4 } ? new Vector4(md.bc[0], md.bc[1], md.bc[2], md.bc[3]) : Vector4.One,
                        EmissiveColor = md.ec is { Length: 4 } ? new Vector4(md.ec[0], md.ec[1], md.ec[2], md.ec[3]) : Vector4.Zero,
                        Metallic = md.mt,
                        Roughness = md.rg,
                        Transmission = md.tr,
                        Ior = md.ir,
                        ForceOpaque = md.fo,
                        CullBackface = md.cb,
                        CastShadow = md.cs,
                        SourcePartIndex = md.sp ?? i,
                        NodeGraph = md.ng?.Clone()
                    });
                }
            }
            else
            {
                result.Materials.Add(new EditableMaterial());
            }

            if (dto.f != null)
            {
                for (int fi = 0; fi < dto.f.Length; fi++)
                {
                    var verts = dto.f[fi];
                    if (verts == null || verts.Length < 3) continue;
                    bool valid = true;
                    foreach (int vi in verts)
                    {
                        if (vi < 0 || vi >= vCount) { valid = false; break; }
                    }
                    if (!valid) continue;

                    var faceUvs = new List<Vector2>(verts.Length);
                    if (dto.fu != null && fi < dto.fu.Length && dto.fu[fi] is { Length: > 0 } fuArr)
                    {
                        for (int k = 0; k + 1 < fuArr.Length; k += 2)
                            faceUvs.Add(new Vector2(fuArr[k], fuArr[k + 1]));
                    }

                    List<Vector3>? cornerNormals = null;
                    if (dto.fn != null && fi < dto.fn.Length && dto.fn[fi] is { Length: > 0 } fnArr && fnArr.Length == verts.Length * 3)
                    {
                        cornerNormals = new List<Vector3>(verts.Length);
                        for (int k = 0; k < verts.Length; k++)
                        {
                            var cn = new Vector3(fnArr[k * 3], fnArr[k * 3 + 1], fnArr[k * 3 + 2]);
                            float lenSq = cn.LengthSquared();
                            cornerNormals.Add(lenSq > 1e-10f ? cn / MathF.Sqrt(lenSq) : Vector3.UnitY);
                        }
                    }

                    result.Faces.Add(new EditableFace
                    {
                        VertexIndices = new List<int>(verts),
                        FaceUVs = faceUvs,
                        FaceCornerNormals = cornerNormals,
                        MaterialIndex = (dto.fm != null && fi < dto.fm.Length) ? dto.fm[fi] : 0,
                        Smooth = dto.fs != null && fi < dto.fs.Length && dto.fs[fi] != 0
                    });
                }
            }

            if (dto.le != null)
            {
                for (int i = 0; i + 1 < dto.le.Length; i += 2)
                {
                    int a = dto.le[i];
                    int b = dto.le[i + 1];
                    if (a != b && a >= 0 && b >= 0 && a < vCount && b < vCount)
                        result.Edges.Add(new EditableEdge(a, b));
                }
            }

            result.RebuildEdges(preserveLooseEdges: true);
            result.RecalculateNormals();
            if (!result.HasRefBounds)
                result.CaptureRefBoundsFromCurrentGeometry();

            mesh = result;
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}

/// <summary>
/// Shift+A で追加可能なメッシュプリミティブ種別。
/// </summary>
internal enum EditablePrimitiveType
{
    Vertex,
    Plane,
    Cube,
    UvSphere,
    Cylinder,
    Cone,
    Circle,
    IcoSphere,
    Torus
}
