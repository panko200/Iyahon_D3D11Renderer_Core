using System;
using System.Collections.Generic;
using System.Numerics;

namespace Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;

/// <summary>
/// 編集モードの選択要素種別。
/// </summary>
internal enum EditElementMode
{
    Vertex,
    Edge,
    Face
}

/// <summary>
/// X キー削除メニューの削除モード。
/// </summary>
internal enum MeshDeleteMode
{
    Vertices,
    Edges,
    Faces,
    OnlyEdgesAndFaces,
    OnlyFaces
}

/// <summary>
/// ループカット対象の四角形エッジリング上の1ステップ。
/// SideA -> SideB がカット横断方向 (t=0 が SideA 側、t=1 が SideB 側)。
/// </summary>
internal readonly struct LoopCutStep
{
    public readonly int FaceIndex;
    public readonly int EnterA;
    public readonly int EnterB;
    public readonly int ExitA;
    public readonly int ExitB;
    public readonly int CornerEnterA;
    public readonly int CornerEnterB;
    public readonly int CornerExitA;
    public readonly int CornerExitB;

    public LoopCutStep(
        int faceIndex,
        int enterA, int enterB,
        int exitA, int exitB,
        int cornerEnterA, int cornerEnterB,
        int cornerExitA, int cornerExitB)
    {
        FaceIndex = faceIndex;
        EnterA = enterA;
        EnterB = enterB;
        ExitA = exitA;
        ExitB = exitB;
        CornerEnterA = cornerEnterA;
        CornerEnterB = cornerEnterB;
        CornerExitA = cornerExitA;
        CornerExitB = cornerExitB;
    }
}

/// <summary>
/// Ctrl+R ループカットで検出された四角形エッジリング。
/// </summary>
internal sealed class LoopCutRing
{
    public List<LoopCutStep> Steps { get; } = new();
    public bool IsClosed { get; set; }
}

/// <summary>
/// 編集中の頂点・辺・面の選択集合。
/// </summary>
internal sealed class MeshSelection
{
    public HashSet<int> Vertices { get; } = new();
    public HashSet<int> Edges { get; } = new();
    public HashSet<int> Faces { get; } = new();

    public int ActiveVertex { get; set; } = -1;
    public int ActiveEdge { get; set; } = -1;
    public int ActiveFace { get; set; } = -1;

    public void Clear()
    {
        Vertices.Clear();
        Edges.Clear();
        Faces.Clear();
        ActiveVertex = -1;
        ActiveEdge = -1;
        ActiveFace = -1;
    }

    public MeshSelection Clone()
    {
        var copy = new MeshSelection
        {
            ActiveVertex = ActiveVertex,
            ActiveEdge = ActiveEdge,
            ActiveFace = ActiveFace
        };
        copy.Vertices.UnionWith(Vertices);
        copy.Edges.UnionWith(Edges);
        copy.Faces.UnionWith(Faces);
        return copy;
    }

    public void SyncFromVertices(EditableMesh mesh)
    {
        Vertices.RemoveWhere(v => v < 0 || v >= mesh.Vertices.Count);
        if (ActiveVertex < 0 || ActiveVertex >= mesh.Vertices.Count || !Vertices.Contains(ActiveVertex))
            ActiveVertex = -1;

        Edges.Clear();
        for (int ei = 0; ei < mesh.Edges.Count; ei++)
        {
            var e = mesh.Edges[ei];
            if (Vertices.Contains(e.V0) && Vertices.Contains(e.V1))
                Edges.Add(ei);
        }
        if (!Edges.Contains(ActiveEdge)) ActiveEdge = -1;

        Faces.Clear();
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var vi = mesh.Faces[fi].VertexIndices;
            if (vi.Count < 3) continue;
            bool all = true;
            for (int k = 0; k < vi.Count; k++)
            {
                if (!Vertices.Contains(vi[k])) { all = false; break; }
            }
            if (all) Faces.Add(fi);
        }
        if (!Faces.Contains(ActiveFace)) ActiveFace = -1;
    }

    public void SyncFromEdges(EditableMesh mesh)
    {
        Edges.RemoveWhere(ei => ei < 0 || ei >= mesh.Edges.Count);
        if (!Edges.Contains(ActiveEdge)) ActiveEdge = -1;

        Vertices.Clear();
        foreach (int ei in Edges)
        {
            var e = mesh.Edges[ei];
            Vertices.Add(e.V0);
            Vertices.Add(e.V1);
        }
        if (!Vertices.Contains(ActiveVertex)) ActiveVertex = -1;

        var edgeMap = BuildEdgeLookup(mesh);
        Faces.Clear();
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var vi = mesh.Faces[fi].VertexIndices;
            int n = vi.Count;
            if (n < 3) continue;
            bool allEdges = true;
            for (int k = 0; k < n; k++)
            {
                ulong key = PackEdge(vi[k], vi[(k + 1) % n]);
                if (!edgeMap.TryGetValue(key, out int ei) || !Edges.Contains(ei))
                {
                    allEdges = false;
                    break;
                }
            }
            if (allEdges) Faces.Add(fi);
        }
        if (!Faces.Contains(ActiveFace)) ActiveFace = -1;
    }

    public void SyncFromFaces(EditableMesh mesh)
    {
        Faces.RemoveWhere(fi => fi < 0 || fi >= mesh.Faces.Count);
        if (!Faces.Contains(ActiveFace)) ActiveFace = -1;

        var edgeMap = BuildEdgeLookup(mesh);
        Vertices.Clear();
        Edges.Clear();

        foreach (int fi in Faces)
        {
            var vi = mesh.Faces[fi].VertexIndices;
            int n = vi.Count;
            for (int k = 0; k < n; k++)
            {
                int a = vi[k];
                int b = vi[(k + 1) % n];
                Vertices.Add(a);
                if (edgeMap.TryGetValue(PackEdge(a, b), out int ei))
                    Edges.Add(ei);
            }
        }

        if (!Vertices.Contains(ActiveVertex)) ActiveVertex = -1;
        if (!Edges.Contains(ActiveEdge)) ActiveEdge = -1;
    }

    public void SyncByMode(EditableMesh mesh, EditElementMode mode)
    {
        switch (mode)
        {
            case EditElementMode.Vertex:
                SyncFromVertices(mesh);
                break;
            case EditElementMode.Edge:
                SyncFromEdges(mesh);
                break;
            case EditElementMode.Face:
                SyncFromFaces(mesh);
                break;
        }
    }

    internal static ulong PackEdge(int a, int b)
    {
        uint lo = (uint)Math.Min(a, b);
        uint hi = (uint)Math.Max(a, b);
        return ((ulong)lo << 32) | hi;
    }

    internal static Dictionary<ulong, int> BuildEdgeLookup(EditableMesh mesh)
    {
        var map = new Dictionary<ulong, int>(mesh.Edges.Count);
        for (int i = 0; i < mesh.Edges.Count; i++)
        {
            var e = mesh.Edges[i];
            map[PackEdge(e.V0, e.V1)] = i;
        }
        return map;
    }
}

/// <summary>
/// モデリング操作 (選択・削除・面張り・押し出し・ループカット) の静的アルゴリズム群。
/// </summary>
internal static class MeshEditOperations
{
    #region 選択操作

    public static void SelectAll(EditableMesh mesh, MeshSelection sel)
    {
        sel.Vertices.Clear();
        for (int i = 0; i < mesh.Vertices.Count; i++) sel.Vertices.Add(i);
        sel.Edges.Clear();
        for (int i = 0; i < mesh.Edges.Count; i++) sel.Edges.Add(i);
        sel.Faces.Clear();
        for (int i = 0; i < mesh.Faces.Count; i++) sel.Faces.Add(i);
    }

    public static void ToggleSelectAll(EditableMesh mesh, MeshSelection sel)
    {
        if (sel.Vertices.Count > 0 || sel.Edges.Count > 0 || sel.Faces.Count > 0)
            sel.Clear();
        else
            SelectAll(mesh, sel);
    }

    public static void InvertSelection(EditableMesh mesh, MeshSelection sel, EditElementMode mode)
    {
        switch (mode)
        {
            case EditElementMode.Vertex:
            {
                var next = new HashSet<int>();
                for (int i = 0; i < mesh.Vertices.Count; i++)
                {
                    if (!sel.Vertices.Contains(i)) next.Add(i);
                }
                sel.Vertices.Clear();
                sel.Vertices.UnionWith(next);
                sel.SyncFromVertices(mesh);
                break;
            }
            case EditElementMode.Edge:
            {
                var next = new HashSet<int>();
                for (int i = 0; i < mesh.Edges.Count; i++)
                {
                    if (!sel.Edges.Contains(i)) next.Add(i);
                }
                sel.Edges.Clear();
                sel.Edges.UnionWith(next);
                sel.SyncFromEdges(mesh);
                break;
            }
            case EditElementMode.Face:
            {
                var next = new HashSet<int>();
                for (int i = 0; i < mesh.Faces.Count; i++)
                {
                    if (!sel.Faces.Contains(i)) next.Add(i);
                }
                sel.Faces.Clear();
                sel.Faces.UnionWith(next);
                sel.SyncFromFaces(mesh);
                break;
            }
        }
    }

    public static void SelectLinked(EditableMesh mesh, MeshSelection sel)
    {
        if (sel.Vertices.Count == 0) return;

        var adj = new List<int>[mesh.Vertices.Count];
        for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>();

        foreach (var e in mesh.Edges)
        {
            if (e.V0 >= 0 && e.V0 < adj.Length && e.V1 >= 0 && e.V1 < adj.Length)
            {
                adj[e.V0].Add(e.V1);
                adj[e.V1].Add(e.V0);
            }
        }
        foreach (var f in mesh.Faces)
        {
            int n = f.VertexIndices.Count;
            for (int k = 0; k < n; k++)
            {
                int a = f.VertexIndices[k];
                int b = f.VertexIndices[(k + 1) % n];
                if (a >= 0 && a < adj.Length && b >= 0 && b < adj.Length)
                {
                    adj[a].Add(b);
                    adj[b].Add(a);
                }
            }
        }

        var queue = new Queue<int>(sel.Vertices);
        while (queue.Count > 0)
        {
            int v = queue.Dequeue();
            foreach (int nb in adj[v])
            {
                if (sel.Vertices.Add(nb))
                    queue.Enqueue(nb);
            }
        }

        sel.SyncFromVertices(mesh);
    }

    #endregion

    #region 削除 (X / Delete)

    public static bool Delete(EditableMesh mesh, MeshSelection sel, MeshDeleteMode mode)
    {
        switch (mode)
        {
            case MeshDeleteMode.OnlyFaces:
            {
                if (sel.Faces.Count == 0) return false;
                var keptFaces = new List<EditableFace>(mesh.Faces.Count);
                for (int fi = 0; fi < mesh.Faces.Count; fi++)
                {
                    if (!sel.Faces.Contains(fi))
                        keptFaces.Add(mesh.Faces[fi]);
                }
                mesh.Faces.Clear();
                mesh.Faces.AddRange(keptFaces);
                mesh.RebuildEdges(preserveLooseEdges: true);
                mesh.RecalculateNormals();
                sel.Faces.Clear();
                sel.ActiveFace = -1;
                return true;
            }

            case MeshDeleteMode.OnlyEdgesAndFaces:
            {
                if (sel.Edges.Count == 0 && sel.Faces.Count == 0) return false;
                var deletedEdgeKeys = new HashSet<ulong>();
                foreach (int ei in sel.Edges)
                {
                    if (ei >= 0 && ei < mesh.Edges.Count)
                    {
                        var e = mesh.Edges[ei];
                        deletedEdgeKeys.Add(MeshSelection.PackEdge(e.V0, e.V1));
                    }
                }

                var keptFaces = new List<EditableFace>(mesh.Faces.Count);
                for (int fi = 0; fi < mesh.Faces.Count; fi++)
                {
                    if (sel.Faces.Contains(fi)) continue;
                    var vi = mesh.Faces[fi].VertexIndices;
                    bool touchesDeletedEdge = false;
                    for (int k = 0; k < vi.Count; k++)
                    {
                        if (deletedEdgeKeys.Contains(MeshSelection.PackEdge(vi[k], vi[(k + 1) % vi.Count])))
                        {
                            touchesDeletedEdge = true;
                            break;
                        }
                    }
                    if (!touchesDeletedEdge)
                        keptFaces.Add(mesh.Faces[fi]);
                }

                var keptEdges = new List<EditableEdge>(mesh.Edges.Count);
                for (int ei = 0; ei < mesh.Edges.Count; ei++)
                {
                    if (!sel.Edges.Contains(ei))
                        keptEdges.Add(mesh.Edges[ei]);
                }

                mesh.Faces.Clear();
                mesh.Faces.AddRange(keptFaces);
                mesh.Edges.Clear();
                mesh.Edges.AddRange(keptEdges);
                mesh.RebuildEdges(preserveLooseEdges: true);
                mesh.RecalculateNormals();
                sel.Edges.Clear();
                sel.Faces.Clear();
                sel.ActiveEdge = -1;
                sel.ActiveFace = -1;
                return true;
            }

            case MeshDeleteMode.Faces:
            {
                if (sel.Faces.Count == 0) return false;

                // 元々いずれの面にも属していなかったルーズ辺は保護する
                var usedByAnyFace = new HashSet<ulong>();
                var usedByDeletedFace = new HashSet<ulong>();
                var usedByKeptFace = new HashSet<ulong>();

                var keptFaces = new List<EditableFace>(mesh.Faces.Count);
                for (int fi = 0; fi < mesh.Faces.Count; fi++)
                {
                    var face = mesh.Faces[fi];
                    bool del = sel.Faces.Contains(fi);
                    if (!del) keptFaces.Add(face);

                    var vi = face.VertexIndices;
                    for (int k = 0; k < vi.Count; k++)
                    {
                        ulong key = MeshSelection.PackEdge(vi[k], vi[(k + 1) % vi.Count]);
                        usedByAnyFace.Add(key);
                        if (del) usedByDeletedFace.Add(key);
                        else usedByKeptFace.Add(key);
                    }
                }

                var keptEdges = new List<EditableEdge>(mesh.Edges.Count);
                foreach (var e in mesh.Edges)
                {
                    ulong key = MeshSelection.PackEdge(e.V0, e.V1);
                    if (!usedByAnyFace.Contains(key) || usedByKeptFace.Contains(key))
                        keptEdges.Add(e);
                }

                mesh.Faces.Clear();
                mesh.Faces.AddRange(keptFaces);
                mesh.Edges.Clear();
                mesh.Edges.AddRange(keptEdges);
                RemoveOrphanVerticesUsedOnlyByDeleted(mesh, usedByDeletedFace);
                mesh.RebuildEdges(preserveLooseEdges: true);
                mesh.RecalculateNormals();
                sel.Clear();
                return true;
            }

            case MeshDeleteMode.Edges:
            {
                if (sel.Edges.Count == 0) return false;
                var deletedEdgeKeys = new HashSet<ulong>();
                var touchedVerts = new HashSet<int>();
                foreach (int ei in sel.Edges)
                {
                    if (ei >= 0 && ei < mesh.Edges.Count)
                    {
                        var e = mesh.Edges[ei];
                        deletedEdgeKeys.Add(MeshSelection.PackEdge(e.V0, e.V1));
                        touchedVerts.Add(e.V0);
                        touchedVerts.Add(e.V1);
                    }
                }

                var keptFaces = new List<EditableFace>(mesh.Faces.Count);
                foreach (var face in mesh.Faces)
                {
                    var vi = face.VertexIndices;
                    bool drop = false;
                    for (int k = 0; k < vi.Count; k++)
                    {
                        if (deletedEdgeKeys.Contains(MeshSelection.PackEdge(vi[k], vi[(k + 1) % vi.Count])))
                        {
                            drop = true;
                            break;
                        }
                    }
                    if (!drop) keptFaces.Add(face);
                }

                var keptEdges = new List<EditableEdge>(mesh.Edges.Count);
                for (int ei = 0; ei < mesh.Edges.Count; ei++)
                {
                    if (!sel.Edges.Contains(ei))
                        keptEdges.Add(mesh.Edges[ei]);
                }

                mesh.Faces.Clear();
                mesh.Faces.AddRange(keptFaces);
                mesh.Edges.Clear();
                mesh.Edges.AddRange(keptEdges);
                PruneDeadVerticesInSet(mesh, touchedVerts);
                mesh.RebuildEdges(preserveLooseEdges: true);
                mesh.RecalculateNormals();
                sel.Clear();
                return true;
            }

            case MeshDeleteMode.Vertices:
            default:
            {
                if (sel.Vertices.Count == 0) return false;
                var oldToNew = new int[mesh.Vertices.Count];
                var keptVerts = new List<EditableVertex>(mesh.Vertices.Count);
                for (int i = 0; i < mesh.Vertices.Count; i++)
                {
                    if (sel.Vertices.Contains(i))
                    {
                        oldToNew[i] = -1;
                    }
                    else
                    {
                        oldToNew[i] = keptVerts.Count;
                        keptVerts.Add(mesh.Vertices[i]);
                    }
                }

                var keptFaces = new List<EditableFace>(mesh.Faces.Count);
                foreach (var face in mesh.Faces)
                {
                    bool keep = true;
                    foreach (int vi in face.VertexIndices)
                    {
                        if (vi < 0 || vi >= oldToNew.Length || oldToNew[vi] < 0)
                        {
                            keep = false;
                            break;
                        }
                    }
                    if (!keep) continue;

                    for (int k = 0; k < face.VertexIndices.Count; k++)
                        face.VertexIndices[k] = oldToNew[face.VertexIndices[k]];
                    keptFaces.Add(face);
                }

                var keptEdges = new List<EditableEdge>(mesh.Edges.Count);
                foreach (var e in mesh.Edges)
                {
                    if (e.V0 >= 0 && e.V1 >= 0 &&
                        e.V0 < oldToNew.Length && e.V1 < oldToNew.Length &&
                        oldToNew[e.V0] >= 0 && oldToNew[e.V1] >= 0)
                    {
                        keptEdges.Add(new EditableEdge(oldToNew[e.V0], oldToNew[e.V1]));
                    }
                }

                mesh.Vertices.Clear();
                mesh.Vertices.AddRange(keptVerts);
                mesh.Faces.Clear();
                mesh.Faces.AddRange(keptFaces);
                mesh.Edges.Clear();
                mesh.Edges.AddRange(keptEdges);
                mesh.RebuildEdges(preserveLooseEdges: true);
                mesh.RecalculateNormals();
                sel.Clear();
                return true;
            }
        }
    }

    private static void RemoveOrphanVerticesUsedOnlyByDeleted(EditableMesh mesh, HashSet<ulong> deletedEdgeKeys)
    {
        var candidateVerts = new HashSet<int>();
        foreach (ulong k in deletedEdgeKeys)
        {
            candidateVerts.Add((int)(k >> 32));
            candidateVerts.Add((int)(k & 0xFFFFFFFF));
        }
        PruneDeadVerticesInSet(mesh, candidateVerts);
    }

    private static void PruneDeadVerticesInSet(EditableMesh mesh, HashSet<int> candidateVerts)
    {
        var stillUsed = new HashSet<int>();
        foreach (var e in mesh.Edges)
        {
            stillUsed.Add(e.V0);
            stillUsed.Add(e.V1);
        }
        foreach (var f in mesh.Faces)
        {
            foreach (int v in f.VertexIndices)
                stillUsed.Add(v);
        }

        var oldToNew = new int[mesh.Vertices.Count];
        var keptVerts = new List<EditableVertex>(mesh.Vertices.Count);
        bool removedAny = false;

        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            if (candidateVerts.Contains(i) && !stillUsed.Contains(i))
            {
                oldToNew[i] = -1;
                removedAny = true;
            }
            else
            {
                oldToNew[i] = keptVerts.Count;
                keptVerts.Add(mesh.Vertices[i]);
            }
        }

        if (!removedAny) return;

        mesh.Vertices.Clear();
        mesh.Vertices.AddRange(keptVerts);
        foreach (var f in mesh.Faces)
        {
            for (int k = 0; k < f.VertexIndices.Count; k++)
                f.VertexIndices[k] = oldToNew[f.VertexIndices[k]];
        }
        for (int ei = 0; ei < mesh.Edges.Count; ei++)
        {
            var e = mesh.Edges[ei];
            mesh.Edges[ei] = new EditableEdge(oldToNew[e.V0], oldToNew[e.V1]);
        }
    }

    #endregion

    #region 辺/面の作成 (F)

    public static bool MakeEdgeOrFace(EditableMesh mesh, MeshSelection sel, Vector3? viewHintNormal = null)
        => MakeEdgeOrFaceWithMaterial(mesh, sel, viewHintNormal, materialIndex: 0);

    public static bool MakeEdgeOrFaceWithMaterial(
        EditableMesh mesh, MeshSelection sel, Vector3? viewHintNormal = null, int materialIndex = 0)
    {
        if (sel.Vertices.Count < 2)
            return false;

        if (sel.Vertices.Count == 2)
        {
            using var it = sel.Vertices.GetEnumerator();
            it.MoveNext(); int a = it.Current;
            it.MoveNext(); int b = it.Current;
            if (mesh.FindEdgeIndex(a, b) >= 0)
                return false;
            mesh.EnsureEdge(a, b);
            sel.SyncFromVertices(mesh);
            return true;
        }

        var selectedList = new List<int>(sel.Vertices);
        List<int> ordered = OrderLoopVertices(mesh, selectedList, viewHintNormal);
        if (ordered.Count < 3)
            return false;

        // 全く同じ頂点集合の面が既に存在する場合は重複作成しない
        var orderedSet = new HashSet<int>(ordered);
        foreach (var f in mesh.Faces)
        {
            if (f.VertexIndices.Count == orderedSet.Count && orderedSet.IsSubsetOf(f.VertexIndices))
                return false;
        }

        // 隣接面のエッジ向きと逆方向 (2-manifold 外向き法線統一) になるよう向きを調整
        int voteKeep = 0;
        int voteReverse = 0;
        var directedEdges = new HashSet<ulong>();
        for (int k = 0; k < ordered.Count; k++)
        {
            int a = ordered[k];
            int b = ordered[(k + 1) % ordered.Count];
            directedEdges.Add(((ulong)(uint)a << 32) | (uint)b);
        }

        foreach (var f in mesh.Faces)
        {
            int fn = f.VertexIndices.Count;
            for (int k = 0; k < fn; k++)
            {
                int fa = f.VertexIndices[k];
                int fb = f.VertexIndices[(k + 1) % fn];
                ulong sameDir = ((ulong)(uint)fa << 32) | (uint)fb;
                ulong oppDir = ((ulong)(uint)fb << 32) | (uint)fa;
                if (directedEdges.Contains(sameDir)) voteReverse++;
                if (directedEdges.Contains(oppDir)) voteKeep++;
            }
        }

        if (voteReverse > voteKeep)
        {
            ordered.Reverse();
        }
        else if (voteReverse == voteKeep && viewHintNormal.HasValue)
        {
            Vector3 fNorm = ComputeNewellNormal(mesh, ordered);
            if (Vector3.Dot(fNorm, viewHintNormal.Value) < 0f)
                ordered.Reverse();
        }

        var uvs = new List<Vector2>(ordered.Count);
        foreach (int vi in ordered)
            uvs.Add(mesh.Vertices[vi].UV);

        int matIdx = mesh.Materials.Count > 0 ? Math.Clamp(materialIndex, 0, mesh.Materials.Count - 1) : 0;
        mesh.Faces.Add(new EditableFace
        {
            VertexIndices = ordered,
            FaceUVs = uvs,
            MaterialIndex = matIdx,
            Smooth = false
        });
        mesh.RebuildEdges(preserveLooseEdges: true);
        mesh.RecalculateNormals();
        sel.SyncFromVertices(mesh);
        return true;
    }

    private static List<int> OrderLoopVertices(
        EditableMesh mesh, List<int> verts, Vector3? viewHintNormal)
    {
        var vertSet = new HashSet<int>(verts);
        var adj = new Dictionary<int, List<int>>(verts.Count);
        foreach (int v in verts) adj[v] = new List<int>();

        foreach (var e in mesh.Edges)
        {
            if (vertSet.Contains(e.V0) && vertSet.Contains(e.V1))
            {
                adj[e.V0].Add(e.V1);
                adj[e.V1].Add(e.V0);
            }
        }

        // トポロジー上の単純鎖または単純閉路になっていればそのまま辿る
        bool allDegLe2 = true;
        int endpoints = 0;
        int start = verts[0];
        foreach (var kv in adj)
        {
            int deg = kv.Value.Count;
            if (deg == 0 || deg > 2) { allDegLe2 = false; break; }
            if (deg == 1)
            {
                endpoints++;
                start = kv.Key;
            }
        }

        if (allDegLe2 && (endpoints == 0 || endpoints == 2))
        {
            var chain = new List<int>(verts.Count);
            var visited = new HashSet<int>();
            int curr = start;
            int prev = -1;
            while (visited.Add(curr))
            {
                chain.Add(curr);
                int next = -1;
                foreach (int nb in adj[curr])
                {
                    if (nb != prev && !visited.Contains(nb))
                    {
                        next = nb;
                        break;
                    }
                }
                if (next < 0) break;
                prev = curr;
                curr = next;
            }
            if (chain.Count == verts.Count)
                return chain;
        }

        // ジオメトリック角度ソート (重心周り最適平面への射影)
        Vector3 center = Vector3.Zero;
        foreach (int v in verts) center += mesh.Vertices[v].Position;
        center /= verts.Count;

        Vector3 planeNormal = Vector3.Zero;
        for (int i = 0; i < verts.Count; i++)
        {
            var p1 = mesh.Vertices[verts[i]].Position - center;
            for (int j = i + 1; j < verts.Count; j++)
            {
                var p2 = mesh.Vertices[verts[j]].Position - center;
                var cr = Vector3.Cross(p1, p2);
                if (planeNormal.LengthSquared() > 1e-8f && Vector3.Dot(cr, planeNormal) < 0f)
                    cr = -cr;
                planeNormal += cr;
            }
        }

        if (planeNormal.LengthSquared() < 1e-8f)
            planeNormal = viewHintNormal ?? Vector3.UnitY;
        else
            planeNormal = Vector3.Normalize(planeNormal);

        Vector3 uAxis = MathF.Abs(planeNormal.Y) < 0.9f
            ? Vector3.Normalize(Vector3.Cross(Vector3.UnitY, planeNormal))
            : Vector3.Normalize(Vector3.Cross(Vector3.UnitX, planeNormal));
        Vector3 vAxis = Vector3.Cross(planeNormal, uAxis);

        var sorted = new List<int>(verts);
        sorted.Sort((a, b) =>
        {
            var da = mesh.Vertices[a].Position - center;
            var db = mesh.Vertices[b].Position - center;
            float angA = MathF.Atan2(Vector3.Dot(da, vAxis), Vector3.Dot(da, uAxis));
            float angB = MathF.Atan2(Vector3.Dot(db, vAxis), Vector3.Dot(db, uAxis));
            return angA.CompareTo(angB);
        });
        return sorted;
    }

    private static Vector3 ComputeNewellNormal(EditableMesh mesh, List<int> vi)
    {
        Vector3 nSum = Vector3.Zero;
        int n = vi.Count;
        for (int k = 0; k < n; k++)
        {
            var p0 = mesh.Vertices[vi[k]].Position;
            var p1 = mesh.Vertices[vi[(k + 1) % n]].Position;
            nSum.X += (p0.Y - p1.Y) * (p0.Z + p1.Z);
            nSum.Y += (p0.Z - p1.Z) * (p0.X + p1.X);
            nSum.Z += (p0.X - p1.X) * (p0.Y + p1.Y);
        }
        return nSum.LengthSquared() > 1e-12f ? Vector3.Normalize(nSum) : Vector3.UnitY;
    }

    #endregion

    #region 押し出し / 頂点追加 (E)

    public static bool Extrude(
        EditableMesh mesh,
        MeshSelection sel,
        EditElementMode mode,
        Vector3 newVertexDefaultPos,
        out Vector3? suggestedAxis)
        => ExtrudeWithMaterial(mesh, sel, mode, newVertexDefaultPos, out suggestedAxis, materialIndex: -1);

    public static bool ExtrudeWithMaterial(
        EditableMesh mesh,
        MeshSelection sel,
        EditElementMode mode,
        Vector3 newVertexDefaultPos,
        out Vector3? suggestedAxis,
        int materialIndex = -1)
    {
        suggestedAxis = null;

        // 未選択時の E キー = 新規頂点の追加
        if (sel.Vertices.Count == 0)
        {
            int newIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new EditableVertex { Position = newVertexDefaultPos });
            sel.Clear();
            sel.Vertices.Add(newIdx);
            sel.ActiveVertex = newIdx;
            return true;
        }

        if (mode == EditElementMode.Face && sel.Faces.Count > 0)
            return ExtrudeFaces(mesh, sel, out suggestedAxis, materialIndex);

        if (mode == EditElementMode.Edge && sel.Edges.Count > 0)
            return ExtrudeEdges(mesh, sel, materialIndex);

        if (sel.Faces.Count > 0)
            return ExtrudeFaces(mesh, sel, out suggestedAxis, materialIndex);

        if (sel.Edges.Count > 0 && mode != EditElementMode.Vertex)
            return ExtrudeEdges(mesh, sel, materialIndex);

        return ExtrudeVertices(mesh, sel);
    }

    private static bool ExtrudeVertices(EditableMesh mesh, MeshSelection sel)
    {
        var srcVerts = new List<int>(sel.Vertices);
        var newVerts = new HashSet<int>();
        int lastNew = -1;

        foreach (int vIdx in srcVerts)
        {
            if (vIdx < 0 || vIdx >= mesh.Vertices.Count) continue;
            int newIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(mesh.Vertices[vIdx].Clone());
            mesh.EnsureEdge(vIdx, newIdx);
            newVerts.Add(newIdx);
            lastNew = newIdx;
        }

        mesh.RebuildEdges(preserveLooseEdges: true);
        sel.Clear();
        sel.Vertices.UnionWith(newVerts);
        sel.ActiveVertex = lastNew;
        sel.SyncFromVertices(mesh);
        return newVerts.Count > 0;
    }

    private static bool ExtrudeEdges(EditableMesh mesh, MeshSelection sel, int materialIndex)
    {
        int clampedMat = (materialIndex >= 0 && mesh.Materials.Count > 0) ? Math.Clamp(materialIndex, 0, mesh.Materials.Count - 1) : 0;
        var srcEdgeIndices = new List<int>(sel.Edges);
        var vertMap = new Dictionary<int, int>();

        int GetOrDup(int oldV)
        {
            if (vertMap.TryGetValue(oldV, out int nv)) return nv;
            nv = mesh.Vertices.Count;
            mesh.Vertices.Add(mesh.Vertices[oldV].Clone());
            vertMap[oldV] = nv;
            return nv;
        }

        // 既存面の辺向きを確認し、押し出し面の法線が連続するように揃える
        var dirLookup = new Dictionary<ulong, int>();
        foreach (var f in mesh.Faces)
        {
            int n = f.VertexIndices.Count;
            for (int k = 0; k < n; k++)
            {
                int a = f.VertexIndices[k];
                int b = f.VertexIndices[(k + 1) % n];
                dirLookup[((ulong)(uint)a << 32) | (uint)b] = 1;
            }
        }

        var newEdgesKey = new List<(int, int)>();
        foreach (int ei in srcEdgeIndices)
        {
            if (ei < 0 || ei >= mesh.Edges.Count) continue;
            var e = mesh.Edges[ei];
            int a = e.V0;
            int b = e.V1;
            int na = GetOrDup(a);
            int nb = GetOrDup(b);

            // 既存面が a->b を使っていれば新面は b->a->na->nb、そうでなければ a->b->nb->na
            bool usedForward = dirLookup.ContainsKey(((ulong)(uint)a << 32) | (uint)b);
            bool usedBackward = dirLookup.ContainsKey(((ulong)(uint)b << 32) | (uint)a);

            List<int> quadVerts = (usedForward && !usedBackward)
                ? new List<int> { b, a, na, nb }
                : new List<int> { a, b, nb, na };

            var uvs = new List<Vector2>
            {
                mesh.Vertices[quadVerts[0]].UV,
                mesh.Vertices[quadVerts[1]].UV,
                mesh.Vertices[quadVerts[2]].UV,
                mesh.Vertices[quadVerts[3]].UV
            };

            mesh.Faces.Add(new EditableFace
            {
                VertexIndices = quadVerts,
                FaceUVs = uvs,
                MaterialIndex = clampedMat,
                Smooth = false
            });
            newEdgesKey.Add((na, nb));
        }

        mesh.RebuildEdges(preserveLooseEdges: true);
        mesh.RecalculateNormals();

        sel.Clear();
        foreach (var (u, v) in newEdgesKey)
        {
            int ei = mesh.FindEdgeIndex(u, v);
            if (ei >= 0) sel.Edges.Add(ei);
        }
        sel.SyncFromEdges(mesh);
        return true;
    }

    private static bool ExtrudeFaces(EditableMesh mesh, MeshSelection sel, out Vector3? suggestedAxis, int materialIndex = -1)
    {
        suggestedAxis = null;
        mesh.RecalculateNormals();
        int? overrideMat = (materialIndex >= 0 && mesh.Materials.Count > 0)
            ? Math.Clamp(materialIndex, 0, mesh.Materials.Count - 1)
            : null;

        var selectedFaceSet = new HashSet<int>(sel.Faces);
        if (selectedFaceSet.Count == 0) return false;

        // 押し出し推奨軸 = 選択面の平均法線
        Vector3 avgNormal = Vector3.Zero;
        foreach (int fi in selectedFaceSet)
        {
            if (fi >= 0 && fi < mesh.Faces.Count)
                avgNormal += mesh.Faces[fi].FaceNormal;
        }
        if (avgNormal.LengthSquared() > 1e-8f)
            suggestedAxis = Vector3.Normalize(avgNormal);

        // 選択領域の外周境界辺と隣接状態を収集
        var directedEdgeCount = new Dictionary<ulong, int>();
        var undirectedCount = new Dictionary<ulong, int>();
        var selectedVerts = new HashSet<int>();
        var boundaryDirected = new List<(int A, int B, Vector2 UvA, Vector2 UvB, int MatIdx, bool Smooth)>();

        foreach (int fi in selectedFaceSet)
        {
            var f = mesh.Faces[fi];
            int n = f.VertexIndices.Count;
            for (int k = 0; k < n; k++)
            {
                int a = f.VertexIndices[k];
                int b = f.VertexIndices[(k + 1) % n];
                selectedVerts.Add(a);
                ulong uk = MeshSelection.PackEdge(a, b);
                undirectedCount[uk] = undirectedCount.GetValueOrDefault(uk) + 1;
            }
        }

        // 未選択面にも選択領域の頂点・辺が繋がっているか (単一Planeなど孤立シートか否か) を確認
        bool hasUnselectedNeighbor = false;
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            if (selectedFaceSet.Contains(fi)) continue;
            foreach (int v in mesh.Faces[fi].VertexIndices)
            {
                if (selectedVerts.Contains(v))
                {
                    hasUnselectedNeighbor = true;
                    break;
                }
            }
            if (hasUnselectedNeighbor) break;
        }

        foreach (int fi in selectedFaceSet)
        {
            var f = mesh.Faces[fi];
            int n = f.VertexIndices.Count;
            for (int k = 0; k < n; k++)
            {
                int a = f.VertexIndices[k];
                int b = f.VertexIndices[(k + 1) % n];
                ulong uk = MeshSelection.PackEdge(a, b);
                if (undirectedCount[uk] == 1)
                {
                    Vector2 uvA = k < f.FaceUVs.Count ? f.FaceUVs[k] : mesh.Vertices[a].UV;
                    Vector2 uvB = ((k + 1) % n) < f.FaceUVs.Count ? f.FaceUVs[(k + 1) % n] : mesh.Vertices[b].UV;
                    boundaryDirected.Add((a, b, uvA, uvB, f.MaterialIndex, f.Smooth));
                }
            }
        }

        // 頂点を複製
        var vertMap = new Dictionary<int, int>(selectedVerts.Count);
        foreach (int oldV in selectedVerts)
        {
            int newV = mesh.Vertices.Count;
            mesh.Vertices.Add(mesh.Vertices[oldV].Clone());
            vertMap[oldV] = newV;
        }

        // 開いた独立シート (Plane 等) の全選択押し出しなら底面キャップを残して閉じ立体化する
        if (!hasUnselectedNeighbor && boundaryDirected.Count > 0)
        {
            foreach (int fi in selectedFaceSet)
            {
                var orig = mesh.Faces[fi];
                var bottom = orig.Clone();
                bottom.VertexIndices.Reverse();
                bottom.FaceUVs.Reverse();
                mesh.Faces.Add(bottom);
            }
        }

        // 元の選択面は複製した頂点側へ付け替える
        foreach (int fi in selectedFaceSet)
        {
            var f = mesh.Faces[fi];
            for (int k = 0; k < f.VertexIndices.Count; k++)
                f.VertexIndices[k] = vertMap[f.VertexIndices[k]];
            if (overrideMat.HasValue)
                f.MaterialIndex = overrideMat.Value;
        }

        // 境界辺ごとに側壁クワッドを生成
        foreach (var (a, b, uvA, uvB, matIdx, smooth) in boundaryDirected)
        {
            int na = vertMap[a];
            int nb = vertMap[b];
            mesh.Faces.Add(new EditableFace
            {
                VertexIndices = new List<int> { a, b, nb, na },
                FaceUVs = new List<Vector2> { uvA, uvB, uvB, uvA },
                MaterialIndex = overrideMat ?? matIdx,
                Smooth = smooth
            });
        }

        mesh.RebuildEdges(preserveLooseEdges: true);
        mesh.RecalculateNormals();

        // 選択状態はそのまま押し出し後の天面 (selectedFaceSet) とその頂点に更新
        sel.SyncFromFaces(mesh);
        return true;
    }

    #endregion

    #region ループカット (Ctrl+R)

    public static LoopCutRing? TraceQuadEdgeRing(EditableMesh mesh, int edgeIndex)
    {
        if (edgeIndex < 0 || edgeIndex >= mesh.Edges.Count)
            return null;

        var startEdge = mesh.Edges[edgeIndex];
        return TraceQuadEdgeRing(mesh, startEdge.V0, startEdge.V1);
    }

    public static LoopCutRing? TraceQuadEdgeRing(EditableMesh mesh, int vA, int vB)
    {
        // 辺 -> 属する四角形リスト
        var edgeFaces = new Dictionary<ulong, List<(int FaceIdx, int EdgeK)>>();
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var f = mesh.Faces[fi];
            if (f.VertexIndices.Count != 4) continue;
            for (int k = 0; k < 4; k++)
            {
                ulong key = MeshSelection.PackEdge(f.VertexIndices[k], f.VertexIndices[(k + 1) % 4]);
                if (!edgeFaces.TryGetValue(key, out var list))
                {
                    list = new List<(int, int)>(2);
                    edgeFaces[key] = list;
                }
                list.Add((fi, k));
            }
        }

        ulong startKey = MeshSelection.PackEdge(vA, vB);
        if (!edgeFaces.TryGetValue(startKey, out var seedList) || seedList.Count == 0)
            return null;

        var visitedFaces = new HashSet<int>();
        var forwardSteps = new List<LoopCutStep>();
        var backwardSteps = new List<LoopCutStep>();

        // 第1方向へ辿る (seedList[0])
        WalkDirection(mesh, edgeFaces, seedList[0].FaceIdx, vA, vB, visitedFaces, forwardSteps, out bool closedLoop);

        // 閉じておらず、開始辺を共有する2枚目の四角形があれば逆方向も辿る
        if (!closedLoop && seedList.Count >= 2)
        {
            // vA -> vB を逆向きに辿って先頭へ接続したいので Enter(vA,vB) に対する反対辺へ進んだものを反転積みする
            WalkDirection(mesh, edgeFaces, seedList[1].FaceIdx, vA, vB, visitedFaces, backwardSteps, out _);
        }

        var result = new LoopCutRing { IsClosed = closedLoop };
        // backwardSteps は seed から逆向きに離れていく順なので反転して結合する
        for (int i = backwardSteps.Count - 1; i >= 0; i--)
        {
            var s = backwardSteps[i];
            // 反転時は Enter/Exit をスワップすると全リングで t 方向と進行順が揃う
            result.Steps.Add(new LoopCutStep(
                s.FaceIndex,
                s.ExitA, s.ExitB,
                s.EnterA, s.EnterB,
                s.CornerExitA, s.CornerExitB,
                s.CornerEnterA, s.CornerEnterB));
        }
        result.Steps.AddRange(forwardSteps);

        return result.Steps.Count > 0 ? result : null;
    }

    private static void WalkDirection(
        EditableMesh mesh,
        Dictionary<ulong, List<(int FaceIdx, int EdgeK)>> edgeFaces,
        int firstFaceIdx,
        int enterA,
        int enterB,
        HashSet<int> visitedFaces,
        List<LoopCutStep> steps,
        out bool closedLoop)
    {
        closedLoop = false;
        int currFaceIdx = firstFaceIdx;
        int curA = enterA;
        int curB = enterB;
        int startFace = firstFaceIdx;

        while (currFaceIdx >= 0)
        {
            if (visitedFaces.Contains(currFaceIdx))
            {
                if (currFaceIdx == startFace && steps.Count > 0)
                    closedLoop = true;
                break;
            }

            var face = mesh.Faces[currFaceIdx];
            if (face.VertexIndices.Count != 4) break;
            var vi = face.VertexIndices;

            // クワッド内で curA, curB に対応するコーナーを特定
            int cA = -1, cB = -1;
            for (int k = 0; k < 4; k++)
            {
                int v0 = vi[k];
                int v1 = vi[(k + 1) % 4];
                if (v0 == curA && v1 == curB) { cA = k; cB = (k + 1) % 4; break; }
                if (v0 == curB && v1 == curA) { cA = (k + 1) % 4; cB = k; break; }
            }
            if (cA < 0) break;

            // クワッド内の対辺のコーナー番号:
            // cB が cA+1 のとき対辺は cA 隣 (cA+3) -> exitA, cB 隣 (cB+1 = cA+2) -> exitB
            // cB が cA-1 のとき対辺は cA 隣 (cA+1) -> exitA, cB 隣 (cB+3 = cA+2) -> exitB
            int cExitA, cExitB;
            if ((cA + 1) % 4 == cB)
            {
                cExitA = (cA + 3) % 4;
                cExitB = (cA + 2) % 4;
            }
            else
            {
                cExitA = (cA + 1) % 4;
                cExitB = (cA + 2) % 4;
            }

            int exitA = vi[cExitA];
            int exitB = vi[cExitB];

            visitedFaces.Add(currFaceIdx);
            steps.Add(new LoopCutStep(
                currFaceIdx,
                curA, curB,
                exitA, exitB,
                cA, cB,
                cExitA, cExitB));

            // exit 辺に接続している次のクワッドを探す
            ulong nextKey = MeshSelection.PackEdge(exitA, exitB);
            int nextFace = -1;
            if (edgeFaces.TryGetValue(nextKey, out var adjList))
            {
                foreach (var (fIdx, _) in adjList)
                {
                    if (fIdx == currFaceIdx) continue;
                    nextFace = fIdx;
                    break;
                }
            }

            if (nextFace < 0) break;
            if (nextFace == startFace)
            {
                closedLoop = true;
                break;
            }

            currFaceIdx = nextFace;
            curA = exitA;
            curB = exitB;
        }
    }

    public static float ComputeCutT(int cutIdx, int totalCuts, float slide)
    {
        float baseT = (cutIdx + 1f) / (totalCuts + 1f);
        float s = Math.Clamp(slide, -0.95f, 0.95f);
        return s >= 0f
            ? baseT + s * (1f - baseT)
            : baseT + s * baseT;
    }

    public static List<(Vector3 A, Vector3 B)> BuildLoopCutPreviewLines(
        EditableMesh mesh, LoopCutRing ring, int cuts, float slide)
    {
        var lines = new List<(Vector3, Vector3)>(ring.Steps.Count * Math.Max(1, cuts));
        int totalCuts = Math.Max(1, cuts);

        for (int c = 0; c < totalCuts; c++)
        {
            float t = ComputeCutT(c, totalCuts, slide);
            foreach (var step in ring.Steps)
            {
                var pEnter = Vector3.Lerp(mesh.Vertices[step.EnterA].Position, mesh.Vertices[step.EnterB].Position, t);
                var pExit = Vector3.Lerp(mesh.Vertices[step.ExitA].Position, mesh.Vertices[step.ExitB].Position, t);
                lines.Add((pEnter, pExit));
            }
        }
        return lines;
    }

    public static bool ApplyLoopCut(
        EditableMesh mesh, MeshSelection sel, LoopCutRing ring, int cuts, float slide)
    {
        if (ring.Steps.Count == 0) return false;
        int totalCuts = Math.Clamp(cuts, 1, 32);

        // (無向辺, cutIdx) -> 作成した分割頂点インデックス
        var splitCache = new Dictionary<(ulong EdgeKey, int CutIdx), int>();

        int GetOrCreateSplitVertex(int vA, int vB, int cutIdx, float t)
        {
            ulong eKey = MeshSelection.PackEdge(vA, vB);
            if (splitCache.TryGetValue((eKey, cutIdx), out int existing))
                return existing;

            var va = mesh.Vertices[vA];
            var vb = mesh.Vertices[vB];
            int newIdx = mesh.Vertices.Count;
            mesh.Vertices.Add(new EditableVertex
            {
                Position = Vector3.Lerp(va.Position, vb.Position, t),
                Normal = Vector3.Normalize(Vector3.Lerp(va.Normal, vb.Normal, t)),
                UV = Vector2.Lerp(va.UV, vb.UV, t),
                Color = Vector4.Lerp(va.Color, vb.Color, t)
            });
            splitCache[(eKey, cutIdx)] = newIdx;
            return newIdx;
        }

        var cutEdgesPairs = new List<(int U, int V)>(ring.Steps.Count * totalCuts);
        var facesToRemove = new HashSet<int>();
        var facesToAdd = new List<EditableFace>(ring.Steps.Count * (totalCuts + 1));

        foreach (var step in ring.Steps)
        {
            if (step.FaceIndex < 0 || step.FaceIndex >= mesh.Faces.Count) continue;
            facesToRemove.Add(step.FaceIndex);
            var orig = mesh.Faces[step.FaceIndex];

            static Vector2 GetCornerUv(EditableFace f, EditableMesh m, int corner, int vert)
                => corner < f.FaceUVs.Count ? f.FaceUVs[corner] : m.Vertices[vert].UV;

            Vector2 uvEnterA = GetCornerUv(orig, mesh, step.CornerEnterA, step.EnterA);
            Vector2 uvEnterB = GetCornerUv(orig, mesh, step.CornerEnterB, step.EnterB);
            Vector2 uvExitA = GetCornerUv(orig, mesh, step.CornerExitA, step.ExitA);
            Vector2 uvExitB = GetCornerUv(orig, mesh, step.CornerExitB, step.ExitB);

            // t 方向の向きが orig.VertexIndices と一致するか判定 (step.CornerEnterA -> CornerEnterB が +1 周りか -1 周りか)
            bool forwardWinding = (step.CornerEnterA + 1) % 4 == step.CornerEnterB;

            // 分割点列: strip[0] = (EnterA, ExitA), strip[1..totalCuts] = splits, strip[totalCuts+1] = (EnterB, ExitB)
            int strips = totalCuts + 2;
            var enterChain = new int[strips];
            var exitChain = new int[strips];
            var enterUvs = new Vector2[strips];
            var exitUvs = new Vector2[strips];

            enterChain[0] = step.EnterA;
            exitChain[0] = step.ExitA;
            enterUvs[0] = uvEnterA;
            exitUvs[0] = uvExitA;

            for (int c = 0; c < totalCuts; c++)
            {
                float t = ComputeCutT(c, totalCuts, slide);
                int sEnter = GetOrCreateSplitVertex(step.EnterA, step.EnterB, c, t);
                int sExit = GetOrCreateSplitVertex(step.ExitA, step.ExitB, c, t);
                enterChain[c + 1] = sEnter;
                exitChain[c + 1] = sExit;
                enterUvs[c + 1] = Vector2.Lerp(uvEnterA, uvEnterB, t);
                exitUvs[c + 1] = Vector2.Lerp(uvExitA, uvExitB, t);
                cutEdgesPairs.Add((sEnter, sExit));
            }

            enterChain[strips - 1] = step.EnterB;
            exitChain[strips - 1] = step.ExitB;
            enterUvs[strips - 1] = uvEnterB;
            exitUvs[strips - 1] = uvExitB;

            for (int s = 0; s < strips - 1; s++)
            {
                int ea0 = enterChain[s], ea1 = enterChain[s + 1];
                int ex0 = exitChain[s], ex1 = exitChain[s + 1];
                Vector2 uea0 = enterUvs[s], uea1 = enterUvs[s + 1];
                Vector2 uex0 = exitUvs[s], uex1 = exitUvs[s + 1];

                List<int> quadVerts = forwardWinding
                    ? new List<int> { ea0, ea1, ex1, ex0 }
                    : new List<int> { ea0, ex0, ex1, ea1 };
                List<Vector2> quadUvs = forwardWinding
                    ? new List<Vector2> { uea0, uea1, uex1, uex0 }
                    : new List<Vector2> { uea0, uex0, uex1, uea1 };

                facesToAdd.Add(new EditableFace
                {
                    VertexIndices = quadVerts,
                    FaceUVs = quadUvs,
                    MaterialIndex = orig.MaterialIndex,
                    Smooth = orig.Smooth
                });
            }
        }

        var nextFaces = new List<EditableFace>(mesh.Faces.Count - facesToRemove.Count + facesToAdd.Count);
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            if (!facesToRemove.Contains(fi))
                nextFaces.Add(mesh.Faces[fi]);
        }
        nextFaces.AddRange(facesToAdd);

        mesh.Faces.Clear();
        mesh.Faces.AddRange(nextFaces);
        mesh.RebuildEdges(preserveLooseEdges: true);
        mesh.RecalculateNormals();

        // 新規挿入したカットエッジループを選択状態にする
        sel.Clear();
        foreach (var (u, v) in cutEdgesPairs)
        {
            int ei = mesh.FindEdgeIndex(u, v);
            if (ei >= 0) sel.Edges.Add(ei);
        }
        sel.SyncFromEdges(mesh);
        return true;
    }

    #endregion
}
