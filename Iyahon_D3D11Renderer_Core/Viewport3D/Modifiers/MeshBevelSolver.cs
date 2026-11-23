using System;
using System.Collections.Generic;
using System.Numerics;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Modifiers;

/// <summary>
/// Blender 風 ベベル (Bevel) モディファイアソルバー。
/// 面インセット + 辺ベベルストリップ + 頂点コーナーパッチにより水密なベベルジオメトリを生成する。
/// </summary>
internal static class MeshBevelSolver
{
    private readonly struct IncidentFaceCorner
    {
        public readonly int FaceIndex;
        public readonly int CornerIndex;
        public readonly bool Forward;

        public IncidentFaceCorner(int faceIndex, int cornerIndex, bool forward)
        {
            FaceIndex = faceIndex;
            CornerIndex = cornerIndex;
            Forward = forward;
        }
    }

    public static EditableMesh Solve(
        EditableMesh source,
        float amount,
        int segments,
        float angleLimitDeg,
        float profile,
        bool clampOverlap,
        bool smoothBevel)
    {
        if (source.Faces.Count == 0 || source.Vertices.Count < 3 || amount <= 1e-5f)
            return source.Clone();

        var work = source.Clone();
        work.RecalculateNormals();

        int segs = Math.Clamp(segments, 1, 8);
        float prof = Math.Clamp(profile, 0.05f, 1.0f);
        float angleLimitRad = Math.Clamp(angleLimitDeg, 0f, 180f) * (MathF.PI / 180f);
        float cosMax = MathF.Cos(MathF.Max(angleLimitRad, 0.008f));

        // 1. 辺ごとの隣接面コーナー収集
        var edgeFaces = new Dictionary<ulong, List<IncidentFaceCorner>>(work.Edges.Count + work.Faces.Count);
        for (int fi = 0; fi < work.Faces.Count; fi++)
        {
            var vi = work.Faces[fi].VertexIndices;
            int n = vi.Count;
            if (n < 3) continue;
            for (int k = 0; k < n; k++)
            {
                int a = vi[k];
                int b = vi[(k + 1) % n];
                if (a == b) continue;
                ulong key = MeshSelection.PackEdge(a, b);
                if (!edgeFaces.TryGetValue(key, out var list))
                {
                    list = new List<IncidentFaceCorner>(2);
                    edgeFaces[key] = list;
                }
                list.Add(new IncidentFaceCorner(fi, k, a < b));
            }
        }

        // 2. ベベル対象辺の特定とクランプ幅決定
        var beveledEdgeWidth = new Dictionary<ulong, float>();
        var edgeAdjLengths = new Dictionary<int, float>();
        foreach (var kv in edgeFaces)
        {
            int v0 = (int)(kv.Key >> 32);
            int v1 = (int)(kv.Key & 0xFFFFFFFFu);
            float len = Vector3.Distance(work.Vertices[v0].Position, work.Vertices[v1].Position);
            if (len < 1e-6f) continue;

            if (!edgeAdjLengths.TryGetValue(v0, out float min0) || len < min0) edgeAdjLengths[v0] = len;
            if (!edgeAdjLengths.TryGetValue(v1, out float min1) || len < min1) edgeAdjLengths[v1] = len;
        }

        foreach (var kv in edgeFaces)
        {
            var list = kv.Value;
            if (list.Count != 2) continue;
            if (list[0].Forward == list[1].Forward) continue;

            var nA = work.Faces[list[0].FaceIndex].FaceNormal;
            var nB = work.Faces[list[1].FaceIndex].FaceNormal;
            float dot = Math.Clamp(Vector3.Dot(nA, nB), -1f, 1f);
            if (dot > cosMax) continue;

            int v0 = (int)(kv.Key >> 32);
            int v1 = (int)(kv.Key & 0xFFFFFFFFu);
            float w = amount;
            if (clampOverlap)
            {
                float minLen = MathF.Min(
                    edgeAdjLengths.TryGetValue(v0, out float m0) ? m0 : 1f,
                    edgeAdjLengths.TryGetValue(v1, out float m1) ? m1 : 1f);
                w = MathF.Min(w, minLen * 0.44f);
            }
            if (w > 1e-5f)
                beveledEdgeWidth[kv.Key] = w;
        }

        if (beveledEdgeWidth.Count == 0)
            return work;

        var result = new EditableMesh
        {
            SourceFilePath = work.SourceFilePath,
            HasRefBounds = work.HasRefBounds,
            RefBoundsMin = work.RefBoundsMin,
            RefBoundsMax = work.RefBoundsMax,
            RefCenter = work.RefCenter,
            RefScale = work.RefScale,
        };
        foreach (var m in work.Materials) result.Materials.Add(m.Clone());
        if (result.Materials.Count == 0) result.Materials.Add(new EditableMaterial());

        // 未ベベル辺上で共有頂点を再利用するためのマップ
        var origVertShared = new Dictionary<int, int>();
        var slidVertShared = new Dictionary<(int OrigVert, ulong UnbeveledEdgeKey), int>();
        var faceCornerOutIdx = new int[work.Faces.Count][];
        var vFaces = new List<int>[work.Vertices.Count];
        for (int i = 0; i < vFaces.Length; i++) vFaces[i] = new List<int>(6);

        int GetOrCreateUnmodifiedVert(int origVi)
        {
            if (origVertShared.TryGetValue(origVi, out int outIdx)) return outIdx;
            outIdx = result.Vertices.Count;
            result.Vertices.Add(work.Vertices[origVi].Clone());
            origVertShared[origVi] = outIdx;
            return outIdx;
        }

        static Vector2 ComputeUvGradientStep(
            EditableMesh mesh,
            EditableFace face,
            int k,
            Vector3 insetOffset)
        {
            var vi = face.VertexIndices;
            int n = vi.Count;
            if (face.FaceUVs.Count != n || insetOffset.LengthSquared() < 1e-12f)
                return k < face.FaceUVs.Count ? face.FaceUVs[k] : mesh.Vertices[vi[k]].UV;

            int kPrev = (k - 1 + n) % n;
            int kNext = (k + 1) % n;
            Vector3 p0 = mesh.Vertices[vi[k]].Position;
            Vector3 e1 = mesh.Vertices[vi[kNext]].Position - p0;
            Vector3 e2 = mesh.Vertices[vi[kPrev]].Position - p0;
            Vector2 uv0 = face.FaceUVs[k];
            Vector2 dUv1 = face.FaceUVs[kNext] - uv0;
            Vector2 dUv2 = face.FaceUVs[kPrev] - uv0;

            float g00 = Vector3.Dot(e1, e1);
            float g01 = Vector3.Dot(e1, e2);
            float g11 = Vector3.Dot(e2, e2);
            float det = g00 * g11 - g01 * g01;
            if (det < 1e-10f) return uv0;

            float r0 = Vector3.Dot(insetOffset, e1);
            float r1 = Vector3.Dot(insetOffset, e2);
            float a = (r0 * g11 - r1 * g01) / det;
            float b = (g00 * r1 - g01 * r0) / det;
            return uv0 + dUv1 * a + dUv2 * b;
        }

        // 3. 各面のインセットコーナー構築
        for (int fi = 0; fi < work.Faces.Count; fi++)
        {
            var face = work.Faces[fi];
            var vi = face.VertexIndices;
            int n = vi.Count;
            if (n < 3)
            {
                faceCornerOutIdx[fi] = Array.Empty<int>();
                continue;
            }

            Vector3 fNorm = face.FaceNormal;
            if (fNorm.LengthSquared() < 1e-10f) fNorm = Vector3.UnitY;

            // 面内の2D基底 (T, B)
            Vector3 tVec = work.Vertices[vi[1]].Position - work.Vertices[vi[0]].Position;
            if (tVec.LengthSquared() < 1e-10f)
                tVec = MathF.Abs(fNorm.Y) < 0.9f ? Vector3.Cross(Vector3.UnitY, fNorm) : Vector3.Cross(Vector3.UnitX, fNorm);
            tVec = SafeNormalize(tVec, Vector3.UnitX);
            Vector3 bVec = SafeNormalize(Vector3.Cross(fNorm, tVec), Vector3.UnitZ);

            var outIndices = new int[n];
            var outUvs = new List<Vector2>(n);

            for (int k = 0; k < n; k++)
            {
                int vCur = vi[k];
                vFaces[vCur].Add(fi);

                int vPrev = vi[(k - 1 + n) % n];
                int vNext = vi[(k + 1) % n];

                ulong ePrevKey = MeshSelection.PackEdge(vPrev, vCur);
                ulong eNextKey = MeshSelection.PackEdge(vCur, vNext);
                float wPrev = beveledEdgeWidth.TryGetValue(ePrevKey, out float wp) ? wp : 0f;
                float wNext = beveledEdgeWidth.TryGetValue(eNextKey, out float wn) ? wn : 0f;

                Vector3 pCur = work.Vertices[vCur].Position;
                Vector3 pPrev = work.Vertices[vPrev].Position;
                Vector3 pNext = work.Vertices[vNext].Position;

                Vector3 uPrevToCur = SafeNormalize(pCur - pPrev, tVec);
                Vector3 uNext = SafeNormalize(pNext - pCur, bVec);

                Vector3 inPrev = SafeNormalize(Vector3.Cross(fNorm, uPrevToCur), Vector3.Zero);
                Vector3 inNext = SafeNormalize(Vector3.Cross(fNorm, uNext), Vector3.Zero);

                if (wPrev <= 1e-6f && wNext <= 1e-6f)
                {
                    outIndices[k] = GetOrCreateUnmodifiedVert(vCur);
                    outUvs.Add(k < face.FaceUVs.Count ? face.FaceUVs[k] : work.Vertices[vCur].UV);
                    continue;
                }

                // 面内で inset 直線 2本を解く: (inPrev・offset) = wPrev, (inNext・offset) = wNext
                float a1 = Vector3.Dot(inPrev, tVec), b1 = Vector3.Dot(inPrev, bVec);
                float a2 = Vector3.Dot(inNext, tVec), b2 = Vector3.Dot(inNext, bVec);
                float det = a1 * b2 - a2 * b1;

                Vector3 offset;
                if (MathF.Abs(det) > 1e-5f)
                {
                    float x = (wPrev * b2 - wNext * b1) / det;
                    float y = (a1 * wNext - a2 * wPrev) / det;
                    offset = tVec * x + bVec * y;
                }
                else
                {
                    Vector3 avgIn = SafeNormalize(inPrev + inNext, inNext.LengthSquared() > 0f ? inNext : inPrev);
                    offset = avgIn * MathF.Max(wPrev, wNext);
                }

                float maxEdge = MathF.Min(
                    Vector3.Distance(pCur, pPrev),
                    Vector3.Distance(pCur, pNext));
                float offLen = offset.Length();
                float maxAllowed = clampOverlap ? MathF.Max(1e-5f, maxEdge * 0.48f) : MathF.Max(1e-5f, maxEdge * 0.9f);
                if (offLen > maxAllowed && offLen > 1e-6f)
                    offset *= maxAllowed / offLen;

                int outVIdx;
                if (wPrev <= 1e-6f && wNext > 1e-6f)
                {
                    if (!slidVertShared.TryGetValue((vCur, ePrevKey), out outVIdx))
                    {
                        var nv = work.Vertices[vCur].Clone();
                        nv.Position = pCur + offset;
                        nv.UV = ComputeUvGradientStep(work, face, k, offset);
                        outVIdx = result.Vertices.Count;
                        result.Vertices.Add(nv);
                        slidVertShared[(vCur, ePrevKey)] = outVIdx;
                    }
                    outUvs.Add(ComputeUvGradientStep(work, face, k, result.Vertices[outVIdx].Position - pCur));
                }
                else if (wNext <= 1e-6f && wPrev > 1e-6f)
                {
                    if (!slidVertShared.TryGetValue((vCur, eNextKey), out outVIdx))
                    {
                        var nv = work.Vertices[vCur].Clone();
                        nv.Position = pCur + offset;
                        nv.UV = ComputeUvGradientStep(work, face, k, offset);
                        outVIdx = result.Vertices.Count;
                        result.Vertices.Add(nv);
                        slidVertShared[(vCur, eNextKey)] = outVIdx;
                    }
                    outUvs.Add(ComputeUvGradientStep(work, face, k, result.Vertices[outVIdx].Position - pCur));
                }
                else
                {
                    var nv = work.Vertices[vCur].Clone();
                    nv.Position = pCur + offset;
                    nv.UV = ComputeUvGradientStep(work, face, k, offset);
                    outVIdx = result.Vertices.Count;
                    result.Vertices.Add(nv);
                    outUvs.Add(nv.UV);
                }

                outIndices[k] = outVIdx;
            }

            faceCornerOutIdx[fi] = outIndices;
            result.Faces.Add(new EditableFace
            {
                VertexIndices = new List<int>(outIndices),
                FaceUVs = outUvs,
                MaterialIndex = Math.Clamp(face.MaterialIndex, 0, result.Materials.Count - 1),
                Smooth = face.Smooth
            });
        }

        // 4. 辺ベベルストリップ生成 (頂点単位でアークと制御点をキー化し完全共有)
        // キー: (origVert, min(cA, cB), max(cA, cB)) -> cMin..cMax 順の segs+1 頂点インデックス配列
        var endpointArcCache = new Dictionary<(int OrigV, int CMin, int CMax), int[]>();
        var endpointCtrlCache = new Dictionary<(int OrigV, int CMin, int CMax), Vector3>();

        int[] GetOrCreateEndpointArc(int origV, int cA, int cB, Vector3 ctrlPos)
        {
            if (cA == cB)
            {
                var same = new int[segs + 1];
                Array.Fill(same, cA);
                return same;
            }

            int cMin = Math.Min(cA, cB);
            int cMax = Math.Max(cA, cB);
            var key = (origV, cMin, cMax);
            if (!endpointArcCache.TryGetValue(key, out var arc))
            {
                arc = new int[segs + 1];
                arc[0] = cMin;
                arc[segs] = cMax;
                Vector3 pMin = result.Vertices[cMin].Position;
                Vector3 pMax = result.Vertices[cMax].Position;
                Vector2 uvMin = result.Vertices[cMin].UV;
                Vector2 uvMax = result.Vertices[cMax].UV;

                for (int s = 1; s < segs; s++)
                {
                    float t = (float)s / segs;
                    Vector3 arcPos = EvalBevelProfile(pMin, pMax, ctrlPos, t, prof);
                    arc[s] = result.Vertices.Count;
                    result.Vertices.Add(new EditableVertex
                    {
                        Position = arcPos,
                        UV = Vector2.Lerp(uvMin, uvMax, t),
                        Color = Vector4.Lerp(result.Vertices[cMin].Color, result.Vertices[cMax].Color, t)
                    });
                }
                endpointArcCache[key] = arc;
                endpointCtrlCache[key] = ctrlPos;
            }

            if (cA == cMin) return arc;
            var rev = new int[segs + 1];
            for (int s = 0; s <= segs; s++) rev[s] = arc[segs - s];
            return rev;
        }

        foreach (var kv in beveledEdgeWidth)
        {
            var list = edgeFaces[kv.Key];
            var refC0 = list[0];
            var refC1 = list[1];
            var f0 = work.Faces[refC0.FaceIndex];
            var f1 = work.Faces[refC1.FaceIndex];
            int n0 = f0.VertexIndices.Count;
            int n1 = f1.VertexIndices.Count;

            int u = f0.VertexIndices[refC0.CornerIndex];
            int v = f0.VertexIndices[(refC0.CornerIndex + 1) % n0];

            // f0 では (u -> v), f1 では逆向き (v -> u) がコーナー refC1.CornerIndex -> (refC1.CornerIndex+1)%n1
            int cu0 = faceCornerOutIdx[refC0.FaceIndex][refC0.CornerIndex];
            int cv0 = faceCornerOutIdx[refC0.FaceIndex][(refC0.CornerIndex + 1) % n0];
            int cv1 = faceCornerOutIdx[refC1.FaceIndex][refC1.CornerIndex];
            int cu1 = faceCornerOutIdx[refC1.FaceIndex][(refC1.CornerIndex + 1) % n1];

            Vector3 posOrigU = work.Vertices[u].Position;
            Vector3 posOrigV = work.Vertices[v].Position;
            Vector3 eDirUV = SafeNormalize(posOrigV - posOrigU, Vector3.UnitX);
            Vector3 midU = 0.5f * (result.Vertices[cu0].Position + result.Vertices[cu1].Position);
            float sU = MathF.Max(0f, Vector3.Dot(midU - posOrigU, eDirUV));
            Vector3 ctrlU = posOrigU + eDirUV * sU;

            Vector3 eDirVU = -eDirUV;
            Vector3 midV = 0.5f * (result.Vertices[cv0].Position + result.Vertices[cv1].Position);
            float sV = MathF.Max(0f, Vector3.Dot(midV - posOrigV, eDirVU));
            Vector3 ctrlV = posOrigV + eDirVU * sV;

            int[] arcU = GetOrCreateEndpointArc(u, cu0, cu1, ctrlU);
            int[] arcV = GetOrCreateEndpointArc(v, cv0, cv1, ctrlV);

            Vector3 expectedOut = SafeNormalize(f0.FaceNormal + f1.FaceNormal, f0.FaceNormal);
            int matIdx = Math.Clamp(f0.MaterialIndex, 0, result.Materials.Count - 1);

            for (int s = 0; s < segs; s++)
            {
                int a0 = arcU[s];
                int a1 = arcU[s + 1];
                int b1 = arcV[s + 1];
                int b0 = arcV[s];

                // 向き確認
                Vector3 q0 = result.Vertices[a0].Position;
                Vector3 q1 = result.Vertices[b0].Position;
                Vector3 q2 = result.Vertices[b1].Position;
                Vector3 triN = Vector3.Cross(q1 - q0, q2 - q0);

                List<int> quad = Vector3.Dot(triN, expectedOut) >= 0f
                    ? new List<int> { a0, b0, b1, a1 }
                    : new List<int> { a0, a1, b1, b0 };

                var qUvs = new List<Vector2>(quad.Count);
                foreach (int qi in quad) qUvs.Add(result.Vertices[qi].UV);

                result.Faces.Add(new EditableFace
                {
                    VertexIndices = quad,
                    FaceUVs = qUvs,
                    MaterialIndex = matIdx,
                    Smooth = smoothBevel
                });
            }
        }

        // 5. 頂点コーナーパッチ (3辺以上が集まるベベルコーナーの滑らかなクワッドグリッド構築)
        for (int vi = 0; vi < work.Vertices.Count; vi++)
        {
            var incFaces = vFaces[vi];
            if (incFaces.Count < 2) continue;

            var adjBeveled = new List<(int OtherV, ulong Key)>();
            var cornerList = new List<(int OutCorner, Vector3 FaceNormal, int MatIdx)>();

            Vector3 avgNorm = Vector3.Zero;
            for (int i = 0; i < incFaces.Count; i++)
            {
                int fi = incFaces[i];
                var face = work.Faces[fi];
                avgNorm += face.FaceNormal;
                int k = face.VertexIndices.IndexOf(vi);
                if (k >= 0 && faceCornerOutIdx[fi].Length == face.VertexIndices.Count)
                {
                    cornerList.Add((faceCornerOutIdx[fi][k], face.FaceNormal, face.MaterialIndex));
                    int n = face.VertexIndices.Count;
                    int vp = face.VertexIndices[(k - 1 + n) % n];
                    int vn = face.VertexIndices[(k + 1) % n];
                    ulong kp = MeshSelection.PackEdge(vp, vi);
                    ulong kn = MeshSelection.PackEdge(vi, vn);
                    if (beveledEdgeWidth.ContainsKey(kp) && !adjBeveled.Exists(x => x.Key == kp))
                        adjBeveled.Add((vp, kp));
                    if (beveledEdgeWidth.ContainsKey(kn) && !adjBeveled.Exists(x => x.Key == kn))
                        adjBeveled.Add((vn, kn));
                }
            }

            if (adjBeveled.Count < 2 || cornerList.Count < 3) continue;

            Vector3 nV = SafeNormalize(avgNorm, Vector3.UnitY);
            Vector3 pOrig = work.Vertices[vi].Position;

            Vector3 refT = MathF.Abs(nV.Y) < 0.9f
                ? SafeNormalize(Vector3.Cross(Vector3.UnitY, nV), Vector3.UnitX)
                : SafeNormalize(Vector3.Cross(Vector3.UnitX, nV), Vector3.UnitX);
            Vector3 refB = SafeNormalize(Vector3.Cross(nV, refT), Vector3.UnitZ);

            var uniqueCorners = new List<(int OutIdx, float Angle, int MatIdx)>();
            var seenOutIdx = new HashSet<int>();
            foreach (var c in cornerList)
            {
                if (!seenOutIdx.Add(c.OutCorner)) continue;
                Vector3 dir = result.Vertices[c.OutCorner].Position - pOrig;
                float ang = MathF.Atan2(Vector3.Dot(dir, refB), Vector3.Dot(dir, refT));
                uniqueCorners.Add((c.OutCorner, ang, c.MatIdx));
            }

            if (uniqueCorners.Count < 3) continue;
            uniqueCorners.Sort((a, b) => a.Angle.CompareTo(b.Angle));

            Vector3 polyN = Vector3.Zero;
            for (int i = 0; i < uniqueCorners.Count; i++)
            {
                Vector3 p0 = result.Vertices[uniqueCorners[i].OutIdx].Position - pOrig;
                Vector3 p1 = result.Vertices[uniqueCorners[(i + 1) % uniqueCorners.Count].OutIdx].Position - pOrig;
                polyN += Vector3.Cross(p0, p1);
            }
            if (Vector3.Dot(polyN, nV) < 0f)
                uniqueCorners.Reverse();

            bool allEdgesBeveled = true;
            for (int i = 0; i < uniqueCorners.Count; i++)
            {
                int cA = uniqueCorners[i].OutIdx;
                int cB = uniqueCorners[(i + 1) % uniqueCorners.Count].OutIdx;
                var aKey = (vi, Math.Min(cA, cB), Math.Max(cA, cB));
                if (segs > 1 && !endpointArcCache.ContainsKey(aKey))
                {
                    allEdgesBeveled = false;
                    break;
                }
            }
            if (!allEdgesBeveled && adjBeveled.Count < 3) continue;

            int patchMat = Math.Clamp(uniqueCorners[0].MatIdx, 0, result.Materials.Count - 1);

            void EmitCornerFace(List<int> verts)
            {
                if (verts.Count < 3) return;
                Vector3 newell = Vector3.Zero;
                Vector3 basePos = result.Vertices[verts[0]].Position;
                for (int fi = 1; fi + 1 < verts.Count; fi++)
                {
                    Vector3 pa = result.Vertices[verts[fi]].Position - basePos;
                    Vector3 pb = result.Vertices[verts[fi + 1]].Position - basePos;
                    newell += Vector3.Cross(pa, pb);
                }
                if (Vector3.Dot(newell, nV) < 0f)
                    verts.Reverse();

                var uvs = new List<Vector2>(verts.Count);
                for (int fi = 0; fi < verts.Count; fi++)
                    uvs.Add(result.Vertices[verts[fi]].UV);

                result.Faces.Add(new EditableFace
                {
                    VertexIndices = verts,
                    FaceUVs = uvs,
                    MaterialIndex = patchMat,
                    Smooth = smoothBevel
                });
            }

            if (segs == 1)
            {
                var ring = new List<int>(uniqueCorners.Count);
                foreach (var uc in uniqueCorners)
                    ring.Add(uc.OutIdx);
                EmitCornerFace(ring);
            }
            else if (allEdgesBeveled)
            {
                int m = uniqueCorners.Count;
                int half = segs / 2;
                bool isEven = (segs & 1) == 0;

                var cIds = new int[m];
                var arcs = new int[m][];
                var ctrls = new Vector3[m];
                Vector3 oSum = Vector3.Zero;
                for (int s = 0; s < m; s++)
                {
                    cIds[s] = uniqueCorners[s].OutIdx;
                }
                for (int s = 0; s < m; s++)
                {
                    int cNext = cIds[(s + 1) % m];
                    var cKey = (vi, Math.Min(cIds[s], cNext), Math.Max(cIds[s], cNext));
                    ctrls[s] = endpointCtrlCache.TryGetValue(cKey, out var cp) ? cp : pOrig;
                    arcs[s] = GetOrCreateEndpointArc(vi, cIds[s], cNext, ctrls[s]);
                    oSum += result.Vertices[cIds[s]].Position + result.Vertices[cNext].Position - ctrls[s];
                }
                Vector3 cornerOrigin = oSum / m;
                float pExp = MathF.Pow(2f, (0.5f - prof) * 4f);

                var grids = new int[m][,];
                for (int s = 0; s < m; s++)
                {
                    grids[s] = new int[half + 1, half + 1];
                    int sPrev = (s - 1 + m) % m;
                    grids[s][0, 0] = cIds[s];
                    for (int i = 1; i <= half; i++)
                        grids[s][i, 0] = arcs[s][i];
                    for (int j = 1; j <= half; j++)
                        grids[s][0, j] = arcs[sPrev][segs - j];
                }

                if (m == 3)
                {
                    var ax = new Vector3[3];
                    var cUvs = new Vector2[3];
                    for (int s = 0; s < 3; s++)
                    {
                        ax[s] = result.Vertices[cIds[s]].Position - cornerOrigin;
                        cUvs[s] = result.Vertices[cIds[s]].UV;
                    }

                    void EvalSuper3(int s, float fi, float fj, out Vector3 outPos, out Vector2 outUv)
                    {
                        float ti = MathF.Tan(fi * (MathF.PI * 0.5f));
                        float tj = MathF.Tan(fj * (MathF.PI * 0.5f));
                        float invL = 1f / MathF.Sqrt(1f + ti * ti + tj * tj);
                        float wMain = invL;
                        float wI = ti * invL;
                        float wJ = tj * invL;
                        int sI = (s + 1) % 3;
                        int sJ = (s + 2) % 3;
                        outPos = cornerOrigin
                            + ax[s] * MathF.Pow(wMain, pExp)
                            + ax[sI] * MathF.Pow(wI, pExp)
                            + ax[sJ] * MathF.Pow(wJ, pExp);
                        outUv = (cUvs[s] * wMain + cUvs[sI] * wI + cUvs[sJ] * wJ) / (wMain + wI + wJ);
                    }

                    if (isEven)
                    {
                        float wCenter = 1f / MathF.Sqrt(3f);
                        float wPow = MathF.Pow(wCenter, pExp);
                        Vector3 centerPos = cornerOrigin + (ax[0] + ax[1] + ax[2]) * wPow;
                        Vector2 centerUv = (cUvs[0] + cUvs[1] + cUvs[2]) * (1f / 3f);
                        int centerIdx = result.Vertices.Count;
                        result.Vertices.Add(new EditableVertex { Position = centerPos, UV = centerUv });

                        for (int s = 0; s < 3; s++)
                            grids[s][half, half] = centerIdx;

                        for (int s = 0; s < 3; s++)
                        {
                            int sNext = (s + 1) % 3;
                            for (int r = 1; r < half; r++)
                            {
                                EvalSuper3(s, 0.5f, (float)r / segs, out var spPos, out var spUv);
                                int spIdx = result.Vertices.Count;
                                result.Vertices.Add(new EditableVertex { Position = spPos, UV = spUv });
                                grids[s][half, r] = spIdx;
                                grids[sNext][r, half] = spIdx;
                            }
                        }

                        for (int s = 0; s < 3; s++)
                        {
                            for (int i = 1; i < half; i++)
                            {
                                for (int j = 1; j < half; j++)
                                {
                                    EvalSuper3(s, (float)i / segs, (float)j / segs, out var inPos, out var inUv);
                                    int inIdx = result.Vertices.Count;
                                    result.Vertices.Add(new EditableVertex { Position = inPos, UV = inUv });
                                    grids[s][i, j] = inIdx;
                                }
                            }
                        }
                    }
                    else
                    {
                        for (int s = 0; s < 3; s++)
                        {
                            for (int i = 1; i <= half; i++)
                            {
                                for (int j = 1; j <= half; j++)
                                {
                                    EvalSuper3(s, (float)i / segs, (float)j / segs, out var inPos, out var inUv);
                                    int inIdx = result.Vertices.Count;
                                    result.Vertices.Add(new EditableVertex { Position = inPos, UV = inUv });
                                    grids[s][i, j] = inIdx;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // m >= 4 角: 同心セクタクワッドグリッドを補間・平滑化して滑らかな凸パッチを構築
                    Vector3 avgBPos = Vector3.Zero;
                    Vector2 avgBUv = Vector2.Zero;
                    for (int s = 0; s < m; s++)
                    {
                        int midV = arcs[s][half];
                        avgBPos += result.Vertices[midV].Position;
                        avgBUv += result.Vertices[midV].UV;
                    }
                    avgBPos /= m;
                    avgBUv /= m;

                    float avgR = 0f;
                    for (int s = 0; s < m; s++)
                        avgR += Vector3.Distance(result.Vertices[cIds[s]].Position, cornerOrigin);
                    avgR /= m;

                    Vector3 flatCenter = avgBPos;
                    Vector3 sphereDir = SafeNormalize(avgBPos - cornerOrigin, nV);
                    Vector3 sphereCenter = cornerOrigin + sphereDir * avgR;
                    Vector3 centerTarget;
                    if (prof <= 0.25f)
                    {
                        float tFlat = Math.Clamp((0.25f - prof) / 0.25f, 0f, 1f);
                        centerTarget = Vector3.Lerp(flatCenter, cornerOrigin + sphereDir * MathF.Max(0f, avgR * 0.55f), tFlat);
                    }
                    else if (prof <= 0.5f)
                    {
                        float tSph = (prof - 0.25f) / 0.25f;
                        centerTarget = Vector3.Lerp(flatCenter, sphereCenter, tSph);
                    }
                    else
                    {
                        float tSharp = (prof - 0.5f) / 0.5f;
                        centerTarget = Vector3.Lerp(sphereCenter, pOrig, tSharp);
                    }

                    int centerIdx = -1;
                    var interiorList = new List<(int VertIdx, List<int> Neighbors)>();
                    var spokeIds = new int[m, half + 1];

                    Vector3 EvalSectorInitPos(int s, float uFrac, float vFrac)
                    {
                        int sPrev = (s - 1 + m) % m;
                        Vector3 pu0 = result.Vertices[arcs[s][Math.Clamp((int)MathF.Round(uFrac * segs), 0, segs)]].Position;
                        Vector3 p0v = result.Vertices[arcs[sPrev][segs - Math.Clamp((int)MathF.Round(vFrac * segs), 0, segs)]].Position;
                        Vector3 p00 = result.Vertices[cIds[s]].Position;
                        float rDist = Math.Clamp((uFrac + vFrac), 0f, 1f);
                        Vector3 edgeBlend = 0.5f * (pu0 + p0v) + 0.5f * ((pu0 - p00) + (p0v - p00)) * 0.5f;
                        return Vector3.Lerp(edgeBlend, centerTarget, rDist * rDist * 0.65f);
                    }

                    if (isEven)
                    {
                        centerIdx = result.Vertices.Count;
                        result.Vertices.Add(new EditableVertex { Position = centerTarget, UV = avgBUv });
                        for (int s = 0; s < m; s++)
                            grids[s][half, half] = centerIdx;

                        for (int s = 0; s < m; s++)
                        {
                            int sNext = (s + 1) % m;
                            for (int r = 1; r < half; r++)
                            {
                                float fR = (float)r / segs;
                                Vector3 spPos = Vector3.Lerp(result.Vertices[arcs[s][half]].Position, centerTarget, (float)r / half);
                                Vector2 spUv = Vector2.Lerp(result.Vertices[arcs[s][half]].UV, avgBUv, (float)r / half);
                                int spIdx = result.Vertices.Count;
                                result.Vertices.Add(new EditableVertex { Position = spPos, UV = spUv });
                                grids[s][half, r] = spIdx;
                                grids[sNext][r, half] = spIdx;
                                spokeIds[s, r] = spIdx;
                            }
                        }

                        for (int s = 0; s < m; s++)
                        {
                            for (int i = 1; i < half; i++)
                            {
                                for (int j = 1; j < half; j++)
                                {
                                    float fI = (float)i / segs;
                                    float fJ = (float)j / segs;
                                    Vector3 pos = EvalSectorInitPos(s, fI, fJ);
                                    Vector2 uv = Vector2.Lerp(result.Vertices[cIds[s]].UV, avgBUv, Math.Clamp(fI + fJ, 0f, 1f));
                                    int idx = result.Vertices.Count;
                                    result.Vertices.Add(new EditableVertex { Position = pos, UV = uv });
                                    grids[s][i, j] = idx;
                                }
                            }
                        }

                        for (int s = 0; s < m; s++)
                        {
                            int sPrev = (s - 1 + m) % m;
                            for (int r = 1; r < half; r++)
                            {
                                int spIdx = spokeIds[s, r];
                                int prevSp = r == 1 ? arcs[s][half] : spokeIds[s, r - 1];
                                int nextSp = r + 1 == half ? centerIdx : spokeIds[s, r + 1];
                                interiorList.Add((spIdx, new List<int>
                                {
                                    prevSp,
                                    nextSp,
                                    grids[s][half - 1, r],
                                    grids[(s + 1) % m][r, half - 1]
                                }));
                            }
                            for (int i = 1; i < half; i++)
                            {
                                for (int j = 1; j < half; j++)
                                {
                                    interiorList.Add((grids[s][i, j], new List<int>
                                    {
                                        grids[s][i - 1, j],
                                        grids[s][i + 1, j],
                                        grids[s][i, j - 1],
                                        grids[s][i, j + 1]
                                    }));
                                }
                            }
                        }
                    }
                    else
                    {
                        for (int s = 0; s < m; s++)
                        {
                            for (int i = 1; i <= half; i++)
                            {
                                for (int j = 1; j <= half; j++)
                                {
                                    float fI = (float)i / segs;
                                    float fJ = (float)j / segs;
                                    Vector3 pos = EvalSectorInitPos(s, fI, fJ);
                                    Vector2 uv = Vector2.Lerp(result.Vertices[cIds[s]].UV, avgBUv, Math.Clamp(fI + fJ, 0f, 1f));
                                    int idx = result.Vertices.Count;
                                    result.Vertices.Add(new EditableVertex { Position = pos, UV = uv });
                                    grids[s][i, j] = idx;
                                }
                            }
                        }

                        for (int s = 0; s < m; s++)
                        {
                            int sPrev = (s - 1 + m) % m;
                            int sNext = (s + 1) % m;
                            for (int i = 1; i <= half; i++)
                            {
                                for (int j = 1; j <= half; j++)
                                {
                                    var nbrs = new List<int>(4) { grids[s][i - 1, j], grids[s][i, j - 1] };
                                    nbrs.Add(i < half ? grids[s][i + 1, j] : grids[sNext][j, half]);
                                    nbrs.Add(j < half ? grids[s][i, j + 1] : grids[sPrev][half, i]);
                                    interiorList.Add((grids[s][i, j], nbrs));
                                }
                            }
                        }
                    }

                    // 境界固定でラプラシアン平滑化＋目標プロファイル球面への投影で滑らかなコーナー曲面を形成
                    for (int pass = 0; pass < 6; pass++)
                    {
                        var nextPos = new Vector3[interiorList.Count];
                        for (int idx = 0; idx < interiorList.Count; idx++)
                        {
                            var (vId, nbrs) = interiorList[idx];
                            Vector3 avgN = Vector3.Zero;
                            for (int ni = 0; ni < nbrs.Count; ni++)
                                avgN += result.Vertices[nbrs[ni]].Position;
                            avgN /= nbrs.Count;
                            if (prof > 0.32f && prof < 0.85f && avgR > 1e-5f)
                            {
                                Vector3 dir = SafeNormalize(avgN - cornerOrigin, nV);
                                Vector3 onSphere = cornerOrigin + dir * avgR;
                                float sphBlend = 0.25f * Math.Clamp((prof - 0.30f) / 0.20f, 0f, 1f);
                                avgN = Vector3.Lerp(avgN, onSphere, sphBlend);
                            }
                            nextPos[idx] = avgN;
                        }
                        for (int idx = 0; idx < interiorList.Count; idx++)
                            result.Vertices[interiorList[idx].VertIdx].Position = nextPos[idx];
                    }
                }

                // クワッド面・ブリッジストリップ・中心多角形を出力
                for (int s = 0; s < m; s++)
                {
                    for (int i = 0; i < half; i++)
                    {
                        for (int j = 0; j < half; j++)
                        {
                            EmitCornerFace(new List<int>
                            {
                                grids[s][i, j],
                                grids[s][i + 1, j],
                                grids[s][i + 1, j + 1],
                                grids[s][i, j + 1]
                            });
                        }
                    }
                }

                if (!isEven)
                {
                    for (int s = 0; s < m; s++)
                    {
                        int sNext = (s + 1) % m;
                        for (int r = 0; r < half; r++)
                        {
                            EmitCornerFace(new List<int>
                            {
                                grids[s][half, r],
                                grids[sNext][r, half],
                                grids[sNext][r + 1, half],
                                grids[s][half, r + 1]
                            });
                        }
                    }

                    var centerPoly = new List<int>(m);
                    for (int s = 0; s < m; s++)
                        centerPoly.Add(grids[s][half, half]);
                    EmitCornerFace(centerPoly);
                }
            }
            else
            {
                // 一部のみベベル辺のコーナー: 境界ループ + 中心頂点ファンによるフォールバック
                int m = uniqueCorners.Count;
                var boundaryLoop = new List<int>(m * segs);
                Vector3 posSum = Vector3.Zero;
                Vector2 uvSum = Vector2.Zero;

                for (int r = 0; r < m; r++)
                {
                    int cA = uniqueCorners[r].OutIdx;
                    int cB = uniqueCorners[(r + 1) % m].OutIdx;
                    var aKey = (vi, Math.Min(cA, cB), Math.Max(cA, cB));
                    if (endpointArcCache.ContainsKey(aKey))
                    {
                        int[] arc = GetOrCreateEndpointArc(vi, cA, cB, pOrig);
                        for (int s = 0; s < segs; s++)
                        {
                            boundaryLoop.Add(arc[s]);
                            posSum += result.Vertices[arc[s]].Position;
                            uvSum += result.Vertices[arc[s]].UV;
                        }
                    }
                    else
                    {
                        boundaryLoop.Add(cA);
                        posSum += result.Vertices[cA].Position;
                        uvSum += result.Vertices[cA].UV;
                    }
                }

                int totalB = boundaryLoop.Count;
                if (totalB < 3) continue;

                Vector3 avgBPos = posSum / totalB;
                Vector2 avgBUv = uvSum / totalB;
                float domeFactor = Math.Clamp((prof - 0.25f) * 0.65f, -0.2f, 0.45f);
                float avgRadius = Vector3.Distance(avgBPos, pOrig);
                Vector3 centerPos = avgBPos + nV * (avgRadius * domeFactor * 0.4f);

                int centerIdx = result.Vertices.Count;
                result.Vertices.Add(new EditableVertex { Position = centerPos, UV = avgBUv });

                for (int i = 0; i < totalB; i++)
                {
                    int a = boundaryLoop[i];
                    int b = boundaryLoop[(i + 1) % totalB];
                    if (a == b) continue;
                    EmitCornerFace(new List<int> { centerIdx, a, b });
                }
            }
        }

        result.RebuildEdges(preserveLooseEdges: false);
        result.RecalculateNormals();
        return result;
    }

    private static Vector3 EvalBevelProfile(Vector3 a, Vector3 b, Vector3 origCorner, float t, float profile)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;
        float theta = t * (MathF.PI * 0.5f);
        float p = MathF.Pow(2f, (0.5f - Math.Clamp(profile, 0.05f, 1.0f)) * 4f);
        float aW = 1f - MathF.Pow(MathF.Sin(theta), p);
        float bW = 1f - MathF.Pow(MathF.Cos(theta), p);
        return origCorner + (a - origCorner) * aW + (b - origCorner) * bW;
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float lenSq = v.LengthSquared();
        return lenSq > 1e-10f ? v / MathF.Sqrt(lenSq) : fallback;
    }
}
