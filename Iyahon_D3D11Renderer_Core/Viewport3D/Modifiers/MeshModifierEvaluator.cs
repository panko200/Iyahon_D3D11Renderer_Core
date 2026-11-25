using System;
using System.Collections.Generic;
using System.Numerics;
using Iyahon_D3D11Renderer_Core.Model3D;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;
using Iyahon_D3D11Renderer_Core.Viewport3D.Shading;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Modifiers;

/// <summary>
/// 非破壊モディファイアスタック（Mirror / Solidify / Boolean / Array / Bevel / Shrinkwrap / Subdivision / Displace）の評価器。
/// </summary>
internal static class MeshModifierEvaluator
{
    public const int MaxOutputFaces = 120_000;
    public const int MaxOutputVertices = 150_000;

    public static EditableMesh Evaluate(
        EditableMesh baseMesh,
        MeshModifierStack? stack,
        SceneModifierContext? sceneCtx = null,
        int selfLayer = -1,
        Matrix4x4? selfModelToWorld = null,
        HashSet<int>? visitingLayers = null)
    {
        if (stack == null || !stack.HasEnabledModifiers)
            return baseMesh.Clone();

        EditableMesh current = baseMesh.Clone();
        Matrix4x4 selfWorld = selfModelToWorld ?? Matrix4x4.Identity;

        for (int i = 0; i < stack.Items.Count; i++)
        {
            var mod = stack.Items[i];
            if (!mod.Enabled) continue;

            EditableMesh next = EvaluateSingleInternal(
                current, mod, sceneCtx, selfLayer, selfWorld, visitingLayers);

            if (next.Faces.Count > MaxOutputFaces || next.Vertices.Count > MaxOutputVertices)
                break;

            current = next;
        }

        PreserveRefBounds(baseMesh, current);
        return current;
    }

    public static EditableMesh EvaluateSingle(
        EditableMesh baseMesh,
        MeshModifierItem mod,
        SceneModifierContext? sceneCtx = null,
        int selfLayer = -1,
        Matrix4x4? selfModelToWorld = null,
        bool ignoreEnabled = true)
    {
        if (!ignoreEnabled && !mod.Enabled)
            return baseMesh.Clone();

        var result = EvaluateSingleInternal(
            baseMesh, mod, sceneCtx, selfLayer, selfModelToWorld ?? Matrix4x4.Identity, visitingLayers: null);
        PreserveRefBounds(baseMesh, result);
        return result;
    }

    private static void PreserveRefBounds(EditableMesh src, EditableMesh dst)
    {
        dst.SourceFilePath = src.SourceFilePath;
        if (src.HasRefBounds)
        {
            dst.HasRefBounds = true;
            dst.RefBoundsMin = src.RefBoundsMin;
            dst.RefBoundsMax = src.RefBoundsMax;
            dst.RefCenter = src.RefCenter;
            dst.RefScale = src.RefScale;
        }
    }

    private static EditableMesh EvaluateSingleInternal(
        EditableMesh input,
        MeshModifierItem mod,
        SceneModifierContext? sceneCtx,
        int selfLayer,
        in Matrix4x4 selfWorld,
        HashSet<int>? visitingLayers)
    {
        return mod.Type switch
        {
            MeshModifierType.Mirror => ApplyMirror(input, mod),
            MeshModifierType.Solidify => ApplySolidify(input, mod),
            MeshModifierType.Boolean => ApplyBoolean(input, mod, sceneCtx, selfLayer, selfWorld, visitingLayers),
            MeshModifierType.Array => ApplyArray(input, mod),
            MeshModifierType.Triangulate => ApplyTriangulate(input, mod),
            MeshModifierType.Bevel => MeshBevelSolver.Solve(
                input, mod.BevelAmount, mod.BevelSegments, mod.BevelAngleLimitDeg,
                mod.BevelProfile, mod.BevelClampOverlap, mod.BevelSmooth),
            MeshModifierType.Shrinkwrap => ApplyShrinkwrap(input, mod, sceneCtx, selfLayer, selfWorld, visitingLayers),
            MeshModifierType.SubdivisionSimple => ApplySubdivision(input, mod.SubdivLevels, catmullClark: false,
                keepCorners: true, smoothShading: false),
            MeshModifierType.SubdivisionCatmullClark => ApplySubdivision(input, mod.SubdivLevels, catmullClark: true,
                keepCorners: mod.SubdivKeepCorners, smoothShading: mod.SubdivSmoothShading),
            MeshModifierType.Displace => ApplyDisplace(input, mod),
            _ => input.Clone()
        };
    }

    public static EditableMesh? BuildBuiltInTargetMesh(MeshModifierItem mod, EditableMaterial? material = null)
    {
        var primType = D3DPrimitiveShapeParameter.ToEditablePrimitive(mod.TargetPrimitive);
        var mesh = EditableMesh.CreatePrimitive(
            primType,
            size: 1f,
            segments: 24,
            rings: 16,
            icoSubdivisions: 2,
            tubeRadiusRatio: 0.35f,
            smoothShading: true,
            material: material);

        const float d2r = MathF.PI / 180f;
        var s = Matrix4x4.CreateScale(
            NonZeroScale(mod.TargetScaleX),
            NonZeroScale(mod.TargetScaleY),
            NonZeroScale(mod.TargetScaleZ));
        var r = Matrix4x4.CreateRotationX(mod.TargetRotX * d2r)
              * Matrix4x4.CreateRotationY(mod.TargetRotY * d2r)
              * Matrix4x4.CreateRotationZ(mod.TargetRotZ * d2r);
        var t = Matrix4x4.CreateTranslation(mod.TargetPosX, mod.TargetPosY, mod.TargetPosZ);
        var mat = s * r * t;

        SceneModifierContext.TransformMeshInPlace(mesh, mat);
        mesh.RebuildEdges(false);
        return mesh;
    }

    private static float NonZeroScale(float v) => MathF.Abs(v) < 1e-4f ? (v < 0f ? -1e-4f : 1e-4f) : v;

    private static EditableMesh? ResolveTargetMesh(
        MeshModifierItem mod,
        SceneModifierContext? sceneCtx,
        int selfLayer,
        in Matrix4x4 selfWorld,
        HashSet<int>? visitingLayers)
    {
        if (mod.TargetSource == ModifierTargetSourceKind.SceneLayer)
        {
            if (sceneCtx != null && sceneCtx.TryResolveTargetMeshInLocalSpace(
                    mod.TargetLayer, selfLayer, selfWorld, out var resolved, out _, visitingLayers))
            {
                return resolved;
            }
            return null;
        }
        return BuildBuiltInTargetMesh(mod);
    }

    #region 1. ミラー (Mirror)

    private static EditableMesh ApplyMirror(EditableMesh input, MeshModifierItem mod)
    {
        var current = input.Clone();
        if (mod.MirrorX) current = MirrorSingleAxis(current, 0, mod.MirrorBisectX, mod.MirrorFlipX, mod.MirrorMerge, mod.MirrorMergeThreshold, mod.MirrorClip);
        if (mod.MirrorY) current = MirrorSingleAxis(current, 1, mod.MirrorBisectY, mod.MirrorFlipY, mod.MirrorMerge, mod.MirrorMergeThreshold, mod.MirrorClip);
        if (mod.MirrorZ) current = MirrorSingleAxis(current, 2, mod.MirrorBisectZ, mod.MirrorFlipZ, mod.MirrorMerge, mod.MirrorMergeThreshold, mod.MirrorClip);
        return current;
    }

    private static EditableMesh MirrorSingleAxis(
        EditableMesh src, int axis, bool bisect, bool flipSide, bool merge, float mergeThresh, bool clip)
    {
        if (src.Vertices.Count == 0) return src;
        if (src.Faces.Count * 2 > MaxOutputFaces || src.Vertices.Count * 2 > MaxOutputVertices)
            return src;

        float GetC(Vector3 p) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
        Vector3 SetC(Vector3 p, float val) => axis switch
        {
            0 => new Vector3(val, p.Y, p.Z),
            1 => new Vector3(p.X, val, p.Z),
            _ => new Vector3(p.X, p.Y, val)
        };
        Vector3 MirrorVec(Vector3 p) => axis switch
        {
            0 => new Vector3(-p.X, p.Y, p.Z),
            1 => new Vector3(p.X, -p.Y, p.Z),
            _ => new Vector3(p.X, p.Y, -p.Z)
        };

        var work = src;
        if (bisect)
            work = BisectMeshOnAxis(src, axis, keepNegativeSide: flipSide);

        float halfWeld = merge ? MathF.Max(1e-6f, mergeThresh * 0.5f) : 0f;
        float clipEps = clip ? MathF.Max(halfWeld, 0.005f) : halfWeld;

        var dst = CreateMetadataClone(work);
        var origMap = new int[work.Vertices.Count];
        var mirMap = new int[work.Vertices.Count];

        for (int i = 0; i < work.Vertices.Count; i++)
        {
            var ov = work.Vertices[i].Clone();
            float c = GetC(ov.Position);
            bool onPlane = merge && MathF.Abs(c) <= clipEps;
            if (onPlane)
                ov.Position = SetC(ov.Position, 0f);

            int idx = dst.Vertices.Count;
            dst.Vertices.Add(ov);
            origMap[i] = idx;

            if (onPlane)
            {
                mirMap[i] = idx;
            }
            else
            {
                var mv = ov.Clone();
                mv.Position = MirrorVec(ov.Position);
                mv.Normal = MirrorVec(ov.Normal);
                int mIdx = dst.Vertices.Count;
                dst.Vertices.Add(mv);
                mirMap[i] = mIdx;
            }
        }

        for (int fi = 0; fi < work.Faces.Count; fi++)
        {
            var f = work.Faces[fi];
            if (f.VertexIndices.Count < 3) continue;

            // オリジナル側
            var fOrig = f.Clone();
            for (int k = 0; k < fOrig.VertexIndices.Count; k++)
                fOrig.VertexIndices[k] = origMap[fOrig.VertexIndices[k]];
            dst.Faces.Add(fOrig);

            // ミラー側 (ワインディング反転 + 法線ミラー)
            var fMir = new EditableFace
            {
                MaterialIndex = Math.Clamp(f.MaterialIndex, 0, Math.Max(0, dst.Materials.Count - 1)),
                Smooth = f.Smooth,
            };
            int n = f.VertexIndices.Count;
            var distinctCheck = new HashSet<int>(n);
            for (int k = n - 1; k >= 0; k--)
            {
                int mVi = mirMap[f.VertexIndices[k]];
                fMir.VertexIndices.Add(mVi);
                distinctCheck.Add(mVi);
                if (k < f.FaceUVs.Count)
                    fMir.FaceUVs.Add(f.FaceUVs[k]);
                if (f.FaceCornerNormals != null && k < f.FaceCornerNormals.Count)
                    fMir.FaceCornerNormals ??= new List<Vector3>(n);
                if (f.FaceCornerNormals != null && k < f.FaceCornerNormals.Count)
                    fMir.FaceCornerNormals!.Add(MirrorVec(f.FaceCornerNormals[k]));
            }
            if (distinctCheck.Count >= 3)
                dst.Faces.Add(fMir);
        }

        // 元のルーズ辺も複製
        for (int ei = 0; ei < work.Edges.Count; ei++)
        {
            var e = work.Edges[ei];
            dst.EnsureEdge(origMap[e.V0], origMap[e.V1]);
            dst.EnsureEdge(mirMap[e.V0], mirMap[e.V1]);
        }

        dst.RebuildEdges(preserveLooseEdges: true);
        dst.RecalculateNormals();
        return dst;
    }

    private static EditableMesh BisectMeshOnAxis(EditableMesh src, int axis, bool keepNegativeSide)
    {
        float GetC(Vector3 p) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
        float sign = keepNegativeSide ? -1f : 1f;
        const float eps = 1e-5f;

        var dst = CreateMetadataClone(src);
        var copiedVerts = new Dictionary<int, int>();
        var edgeCutVerts = new Dictionary<ulong, int>();

        int GetCopied(int srcIdx)
        {
            if (copiedVerts.TryGetValue(srcIdx, out int idx)) return idx;
            idx = dst.Vertices.Count;
            dst.Vertices.Add(src.Vertices[srcIdx].Clone());
            copiedVerts[srcIdx] = idx;
            return idx;
        }

        for (int fi = 0; fi < src.Faces.Count; fi++)
        {
            var f = src.Faces[fi];
            var vi = f.VertexIndices;
            int n = vi.Count;
            if (n < 3) continue;

            var outVerts = new List<int>(n + 2);
            var outUvs = new List<Vector2>(n + 2);
            var outNorms = f.FaceCornerNormals != null ? new List<Vector3>(n + 2) : null;

            for (int k = 0; k < n; k++)
            {
                int iA = vi[k];
                int iB = vi[(k + 1) % n];
                var vA = src.Vertices[iA];
                var vB = src.Vertices[iB];
                float dA = GetC(vA.Position) * sign;
                float dB = GetC(vB.Position) * sign;
                bool inA = dA >= -eps;
                bool inB = dB >= -eps;

                Vector2 uvA = k < f.FaceUVs.Count ? f.FaceUVs[k] : vA.UV;
                Vector2 uvB = ((k + 1) % n) < f.FaceUVs.Count ? f.FaceUVs[(k + 1) % n] : vB.UV;

                if (inA)
                {
                    int outA = GetCopied(iA);
                    if (outVerts.Count == 0 || outVerts[^1] != outA)
                    {
                        outVerts.Add(outA);
                        outUvs.Add(uvA);
                        if (outNorms != null && k < f.FaceCornerNormals!.Count)
                            outNorms.Add(f.FaceCornerNormals[k]);
                    }
                }

                if (inA != inB && MathF.Abs(dA - dB) > 1e-7f)
                {
                    float t = Math.Clamp(dA / (dA - dB), 0f, 1f);
                    ulong eKey = MeshSelection.PackEdge(iA, iB);
                    if (!edgeCutVerts.TryGetValue(eKey, out int cutIdx))
                    {
                        Vector3 cutPos = Vector3.Lerp(vA.Position, vB.Position, t);
                        cutPos = axis switch
                        {
                            0 => new Vector3(0f, cutPos.Y, cutPos.Z),
                            1 => new Vector3(cutPos.X, 0f, cutPos.Z),
                            _ => new Vector3(cutPos.X, cutPos.Y, 0f)
                        };
                        cutIdx = dst.Vertices.Count;
                        dst.Vertices.Add(new EditableVertex
                        {
                            Position = cutPos,
                            Normal = SafeNormalize(Vector3.Lerp(vA.Normal, vB.Normal, t), Vector3.UnitY),
                            UV = Vector2.Lerp(vA.UV, vB.UV, t),
                            Color = Vector4.Lerp(vA.Color, vB.Color, t)
                        });
                        edgeCutVerts[eKey] = cutIdx;
                    }

                    if (outVerts.Count == 0 || outVerts[^1] != cutIdx)
                    {
                        outVerts.Add(cutIdx);
                        outUvs.Add(Vector2.Lerp(uvA, uvB, t));
                        if (outNorms != null && k < f.FaceCornerNormals!.Count && ((k + 1) % n) < f.FaceCornerNormals.Count)
                        {
                            outNorms.Add(SafeNormalize(
                                Vector3.Lerp(f.FaceCornerNormals[k], f.FaceCornerNormals[(k + 1) % n], t),
                                f.FaceNormal));
                        }
                    }
                }
            }

            if (outVerts.Count >= 2 && outVerts[0] == outVerts[^1])
            {
                outVerts.RemoveAt(outVerts.Count - 1);
                if (outUvs.Count > outVerts.Count) outUvs.RemoveAt(outUvs.Count - 1);
                if (outNorms != null && outNorms.Count > outVerts.Count) outNorms.RemoveAt(outNorms.Count - 1);
            }

            if (outVerts.Count >= 3)
            {
                dst.Faces.Add(new EditableFace
                {
                    VertexIndices = outVerts,
                    FaceUVs = outUvs,
                    FaceCornerNormals = (outNorms != null && outNorms.Count == outVerts.Count) ? outNorms : null,
                    MaterialIndex = Math.Clamp(f.MaterialIndex, 0, Math.Max(0, dst.Materials.Count - 1)),
                    Smooth = f.Smooth
                });
            }
        }

        dst.RebuildEdges(false);
        dst.RecalculateNormals();
        return dst;
    }

    #endregion

    #region 2. ソリッド化 (Solidify)

    private static EditableMesh ApplySolidify(EditableMesh input, MeshModifierItem mod)
    {
        if (input.Faces.Count == 0 || input.Vertices.Count < 3 || MathF.Abs(mod.SolidifyThickness) < 1e-6f)
            return input.Clone();

        var work = input.Clone();
        work.RecalculateNormals();

        int vCount = work.Vertices.Count;
        var vertDirSum = new Vector3[vCount];
        var vFaces = new List<int>[vCount];
        for (int i = 0; i < vCount; i++) vFaces[i] = new List<int>(6);

        var directedEdges = new HashSet<ulong>(work.Faces.Count * 4);
        var boundaryOrder = new List<(int A, int B, int MatIdx, bool Smooth)>();

        static ulong PackDir(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;

        for (int fi = 0; fi < work.Faces.Count; fi++)
        {
            var face = work.Faces[fi];
            var vi = face.VertexIndices;
            int n = vi.Count;
            if (n < 3) continue;

            for (int k = 0; k < n; k++)
            {
                int a = vi[k];
                int b = vi[(k + 1) % n];
                int prev = vi[(k - 1 + n) % n];

                Vector3 e1 = SafeNormalize(work.Vertices[b].Position - work.Vertices[a].Position, Vector3.UnitX);
                Vector3 e2 = SafeNormalize(work.Vertices[prev].Position - work.Vertices[a].Position, Vector3.UnitZ);
                float angle = MathF.Acos(Math.Clamp(Vector3.Dot(e1, e2), -1f, 1f));
                vertDirSum[a] += face.FaceNormal * MathF.Max(0.05f, angle);
                vFaces[a].Add(fi);

                directedEdges.Add(PackDir(a, b));
                boundaryOrder.Add((a, b, face.MaterialIndex, face.Smooth));
            }
        }

        var shellDirs = new Vector3[vCount];
        var scales = new float[vCount];
        for (int i = 0; i < vCount; i++)
        {
            Vector3 dir = vertDirSum[i].LengthSquared() > 1e-10f
                ? Vector3.Normalize(vertDirSum[i])
                : SafeNormalize(work.Vertices[i].Normal, Vector3.UnitY);
            shellDirs[i] = dir;

            if (mod.SolidifyEvenThickness && vFaces[i].Count > 0)
            {
                float dotSum = 0f;
                for (int k = 0; k < vFaces[i].Count; k++)
                    dotSum += MathF.Max(0f, Vector3.Dot(dir, work.Faces[vFaces[i][k]].FaceNormal));
                float cosAvg = dotSum / vFaces[i].Count;
                scales[i] = cosAvg > 0.25f ? MathF.Min(1f / cosAvg, 2.5f) : 1f;
            }
            else
            {
                scales[i] = 1f;
            }
        }

        float t = mod.SolidifyThickness;
        float off = mod.SolidifyOffset;
        float shiftOuter = t * (off + 1f) * 0.5f;
        float shiftInner = t * (off - 1f) * 0.5f;
        bool flipOrientation = (t < 0f) ^ mod.SolidifyFlipNormals;

        var dst = CreateMetadataClone(work);
        dst.Vertices.Capacity = vCount * 2;

        // Outer shell vertices: 0 .. vCount-1
        for (int i = 0; i < vCount; i++)
        {
            var ov = work.Vertices[i].Clone();
            ov.Position += shellDirs[i] * (shiftOuter * scales[i]);
            dst.Vertices.Add(ov);
        }

        // Inner shell vertices: vCount .. 2*vCount-1
        for (int i = 0; i < vCount; i++)
        {
            var iv = work.Vertices[i].Clone();
            iv.Position += shellDirs[i] * (shiftInner * scales[i]);
            dst.Vertices.Add(iv);
        }

        if (!mod.SolidifyOnlyRim)
        {
            bool newShellIsOuter = mod.SolidifyOffset > 0f;
            for (int fi = 0; fi < work.Faces.Count; fi++)
            {
                var f = work.Faces[fi];
                if (f.VertexIndices.Count < 3) continue;
                int origMatIdx = Math.Clamp(f.MaterialIndex, 0, dst.Materials.Count - 1);
                int shellMatIdx = mod.SolidifyMaterialOffset != 0
                    ? Math.Clamp(origMatIdx + mod.SolidifyMaterialOffset, 0, dst.Materials.Count - 1)
                    : origMatIdx;

                // 外側シェル
                var fOuter = f.Clone();
                fOuter.FaceCornerNormals = null;
                fOuter.MaterialIndex = newShellIsOuter ? shellMatIdx : origMatIdx;
                if (flipOrientation)
                {
                    fOuter.VertexIndices.Reverse();
                    fOuter.FaceUVs.Reverse();
                }
                dst.Faces.Add(fOuter);

                // 内側シェル (通常 t>0 時は反転して外向きにする)
                var fInner = new EditableFace
                {
                    MaterialIndex = newShellIsOuter ? origMatIdx : shellMatIdx,
                    Smooth = f.Smooth
                };
                int n = f.VertexIndices.Count;
                if (!flipOrientation)
                {
                    for (int k = n - 1; k >= 0; k--)
                    {
                        fInner.VertexIndices.Add(vCount + f.VertexIndices[k]);
                        if (k < f.FaceUVs.Count) fInner.FaceUVs.Add(f.FaceUVs[k]);
                    }
                }
                else
                {
                    for (int k = 0; k < n; k++)
                    {
                        fInner.VertexIndices.Add(vCount + f.VertexIndices[k]);
                        if (k < f.FaceUVs.Count) fInner.FaceUVs.Add(f.FaceUVs[k]);
                    }
                }
                dst.Faces.Add(fInner);
            }
        }

        // リム側面 (閉じていない境界辺に対して側面クワッドを生成)
        if (mod.SolidifyFillRim)
        {
            for (int i = 0; i < boundaryOrder.Count; i++)
            {
                var (a, b, mat, smooth) = boundaryOrder[i];
                if (directedEdges.Contains(PackDir(b, a)))
                    continue; // 内部共有辺

                int oa = a;
                int ob = b;
                int ia = vCount + a;
                int ib = vCount + b;
                int origMatIdx = Math.Clamp(mat, 0, dst.Materials.Count - 1);
                int matIdx = mod.SolidifyRimMaterialOffset != 0
                    ? Math.Clamp(origMatIdx + mod.SolidifyRimMaterialOffset, 0, dst.Materials.Count - 1)
                    : origMatIdx;

                List<int> rimVerts = !flipOrientation
                    ? new List<int> { ob, oa, ia, ib }
                    : new List<int> { oa, ob, ib, ia };

                var rimUvs = new List<Vector2>
                {
                    dst.Vertices[rimVerts[0]].UV,
                    dst.Vertices[rimVerts[1]].UV,
                    dst.Vertices[rimVerts[2]].UV,
                    dst.Vertices[rimVerts[3]].UV
                };
                dst.Faces.Add(new EditableFace
                {
                    VertexIndices = rimVerts,
                    FaceUVs = rimUvs,
                    MaterialIndex = matIdx,
                    Smooth = smooth
                });
            }
        }

        dst.RebuildEdges(false);
        dst.RecalculateNormals();
        return dst;
    }

    #endregion

    #region 3. ブーリアン (Boolean)

    private static EditableMesh ApplyBoolean(
        EditableMesh input,
        MeshModifierItem mod,
        SceneModifierContext? sceneCtx,
        int selfLayer,
        in Matrix4x4 selfWorld,
        HashSet<int>? visitingLayers)
    {
        var target = ResolveTargetMesh(mod, sceneCtx, selfLayer, selfWorld, visitingLayers);
        if (target == null || target.Faces.Count == 0)
            return input.Clone();

        return MeshBooleanSolver.Solve(
            input,
            target,
            mod.BooleanOperation,
            mapBMaterialsToA: mod.TargetSource == ModifierTargetSourceKind.BuiltInPrimitive);
    }

    #endregion

    #region 4. 配列 (Array)

    private static EditableMesh ApplyArray(EditableMesh input, MeshModifierItem mod)
    {
        int count = Math.Clamp(mod.ArrayCount, 1, 200);
        if (count <= 1 || input.Vertices.Count == 0)
            return input.Clone();

        input.ComputeCurrentBounds(out var bMin, out var bMax, out var bCenter, out _);
        Vector3 ext = bMax - bMin;

        Vector3 rel = mod.ArrayUseRelativeOffset
            ? new Vector3(mod.ArrayRelX * ext.X, mod.ArrayRelY * ext.Y, mod.ArrayRelZ * ext.Z)
            : Vector3.Zero;
        Vector3 cst = mod.ArrayUseConstantOffset
            ? new Vector3(mod.ArrayConstX, mod.ArrayConstY, mod.ArrayConstZ)
            : Vector3.Zero;
        Vector3 stepOffset = rel + cst;

        const float d2r = MathF.PI / 180f;
        float sc = NonZeroScale(mod.ArrayScale);
        bool hasRotOrScale = MathF.Abs(mod.ArrayRotX) > 1e-4f || MathF.Abs(mod.ArrayRotY) > 1e-4f ||
                             MathF.Abs(mod.ArrayRotZ) > 1e-4f || MathF.Abs(sc - 1f) > 1e-4f;

        Matrix4x4 stepTransform = hasRotOrScale
            ? Matrix4x4.CreateTranslation(-bCenter)
              * Matrix4x4.CreateScale(sc)
              * Matrix4x4.CreateRotationX(mod.ArrayRotX * d2r)
              * Matrix4x4.CreateRotationY(mod.ArrayRotY * d2r)
              * Matrix4x4.CreateRotationZ(mod.ArrayRotZ * d2r)
              * Matrix4x4.CreateTranslation(bCenter + stepOffset)
            : Matrix4x4.CreateTranslation(stepOffset);

        var dst = CreateMetadataClone(input);
        int baseV = input.Vertices.Count;
        int baseF = input.Faces.Count;
        if (baseF > 0)
            count = Math.Min(count, Math.Max(1, MaxOutputFaces / baseF));
        if (baseV > 0)
            count = Math.Min(count, Math.Max(1, MaxOutputVertices / baseV));

        Matrix4x4 curMat = Matrix4x4.Identity;
        var vertOffsetPerCopy = new int[count];

        for (int c = 0; c < count; c++)
        {
            int startV = dst.Vertices.Count;
            vertOffsetPerCopy[c] = startV;

            for (int vi = 0; vi < input.Vertices.Count; vi++)
            {
                var v = input.Vertices[vi].Clone();
                v.Position = Vector3.Transform(v.Position, curMat);
                if (hasRotOrScale)
                    v.Normal = SafeNormalize(Vector3.TransformNormal(v.Normal, curMat), v.Normal);
                dst.Vertices.Add(v);
            }

            for (int fi = 0; fi < input.Faces.Count; fi++)
            {
                var nf = input.Faces[fi].Clone();
                for (int k = 0; k < nf.VertexIndices.Count; k++)
                    nf.VertexIndices[k] += startV;
                if (hasRotOrScale && nf.FaceCornerNormals != null)
                {
                    for (int k = 0; k < nf.FaceCornerNormals.Count; k++)
                        nf.FaceCornerNormals[k] = SafeNormalize(
                            Vector3.TransformNormal(nf.FaceCornerNormals[k], curMat), Vector3.UnitY);
                }
                dst.Faces.Add(nf);
            }

            curMat = hasRotOrScale
                ? curMat * stepTransform
                : Matrix4x4.CreateTranslation(stepOffset * (c + 1));
        }

        if (mod.ArrayMerge && mod.ArrayMergeThreshold > 1e-6f && count > 1)
            WeldVerticesByThreshold(dst, mod.ArrayMergeThreshold);

        dst.RebuildEdges(false);
        dst.RecalculateNormals();
        return dst;
    }

    private static void WeldVerticesByThreshold(EditableMesh mesh, float threshold)
    {
        float cell = MathF.Max(1e-5f, threshold);
        float t2 = cell * cell;
        float invCell = 1f / cell;

        var buckets = new Dictionary<(long, long, long), List<int>>(mesh.Vertices.Count);
        var remap = new int[mesh.Vertices.Count];
        var kept = new List<EditableVertex>(mesh.Vertices.Count);

        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var v = mesh.Vertices[i];
            long gx = (long)MathF.Floor(v.Position.X * invCell);
            long gy = (long)MathF.Floor(v.Position.Y * invCell);
            long gz = (long)MathF.Floor(v.Position.Z * invCell);

            int found = -1;
            for (long dx = -1; dx <= 1 && found < 0; dx++)
            for (long dy = -1; dy <= 1 && found < 0; dy++)
            for (long dz = -1; dz <= 1 && found < 0; dz++)
            {
                if (!buckets.TryGetValue((gx + dx, gy + dy, gz + dz), out var list)) continue;
                for (int k = 0; k < list.Count; k++)
                {
                    int cand = list[k];
                    if (Vector3.DistanceSquared(v.Position, kept[cand].Position) <= t2)
                    {
                        found = cand;
                        break;
                    }
                }
            }

            if (found >= 0)
            {
                remap[i] = found;
            }
            else
            {
                int newIdx = kept.Count;
                kept.Add(v);
                remap[i] = newIdx;
                var bKey = (gx, gy, gz);
                if (!buckets.TryGetValue(bKey, out var list))
                {
                    list = new List<int>(2);
                    buckets[bKey] = list;
                }
                list.Add(newIdx);
            }
        }

        mesh.Vertices.Clear();
        mesh.Vertices.AddRange(kept);

        for (int fi = mesh.Faces.Count - 1; fi >= 0; fi--)
        {
            var f = mesh.Faces[fi];
            for (int k = 0; k < f.VertexIndices.Count; k++)
                f.VertexIndices[k] = remap[f.VertexIndices[k]];
            // 連続重複除去
            for (int k = f.VertexIndices.Count - 1; k >= 0; k--)
            {
                int nextK = (k + 1) % f.VertexIndices.Count;
                if (f.VertexIndices.Count > 1 && f.VertexIndices[k] == f.VertexIndices[nextK])
                {
                    f.VertexIndices.RemoveAt(k);
                    if (k < f.FaceUVs.Count) f.FaceUVs.RemoveAt(k);
                    if (f.FaceCornerNormals != null && k < f.FaceCornerNormals.Count)
                        f.FaceCornerNormals.RemoveAt(k);
                }
            }
            if (f.VertexIndices.Count < 3)
                mesh.Faces.RemoveAt(fi);
        }
    }

    #endregion

    #region 6. シュリンクラップ (Shrinkwrap)

    private static EditableMesh ApplyShrinkwrap(
        EditableMesh input,
        MeshModifierItem mod,
        SceneModifierContext? sceneCtx,
        int selfLayer,
        in Matrix4x4 selfWorld,
        HashSet<int>? visitingLayers)
    {
        var target = ResolveTargetMesh(mod, sceneCtx, selfLayer, selfWorld, visitingLayers);
        if (target == null || target.Faces.Count == 0 || input.Vertices.Count == 0)
            return input.Clone();

        var work = input.Clone();
        work.RecalculateNormals();

        var bvh = TriangleBvh.FromMesh(target);
        float offset = mod.ShrinkwrapOffset;
        float mix = Math.Clamp(mod.ShrinkwrapMix, 0f, 1f);
        Vector3 projAxis = mod.ShrinkwrapAxis switch
        {
            ModifierAxisKind.X => Vector3.UnitX,
            ModifierAxisKind.Y => Vector3.UnitY,
            ModifierAxisKind.Z => Vector3.UnitZ,
            ModifierAxisKind.NegX => -Vector3.UnitX,
            ModifierAxisKind.NegY => -Vector3.UnitY,
            _ => -Vector3.UnitZ
        };

        for (int i = 0; i < work.Vertices.Count; i++)
        {
            var v = work.Vertices[i];
            Vector3 p = v.Position;
            Vector3 targetPos = p;
            bool resolved = false;

            switch (mod.ShrinkwrapMode)
            {
                case ShrinkwrapModeKind.NearestVertex:
                {
                    float bestD2 = float.MaxValue;
                    int bestVi = -1;
                    for (int ti = 0; ti < target.Vertices.Count; ti++)
                    {
                        float d2 = Vector3.DistanceSquared(p, target.Vertices[ti].Position);
                        if (d2 < bestD2)
                        {
                            bestD2 = d2;
                            bestVi = ti;
                        }
                    }
                    if (bestVi >= 0)
                    {
                        var tv = target.Vertices[bestVi];
                        targetPos = ApplySnapOffset(p, tv.Position, SafeNormalize(tv.Normal, Vector3.UnitY), bvh, mod.ShrinkwrapSnap, offset);
                        resolved = true;
                    }
                    break;
                }

                case ShrinkwrapModeKind.ProjectNormal:
                {
                    Vector3 nDir = SafeNormalize(v.Normal, Vector3.UnitY);
                    bool hitFwd = bvh.RaycastNearest(p, nDir, out float tF, out var nF);
                    bool hitBwd = bvh.RaycastNearest(p, -nDir, out float tB, out var nB);
                    if (hitFwd && (!hitBwd || tF <= tB))
                    {
                        targetPos = ApplySnapOffset(p, p + nDir * tF, nF, bvh, mod.ShrinkwrapSnap, offset);
                        resolved = true;
                    }
                    else if (hitBwd)
                    {
                        targetPos = ApplySnapOffset(p, p - nDir * tB, nB, bvh, mod.ShrinkwrapSnap, offset);
                        resolved = true;
                    }
                    break;
                }

                case ShrinkwrapModeKind.ProjectAxis:
                {
                    if (bvh.RaycastNearest(p, projAxis, out float tF, out var nF))
                    {
                        targetPos = ApplySnapOffset(p, p + projAxis * tF, nF, bvh, mod.ShrinkwrapSnap, offset);
                        resolved = true;
                    }
                    else if (mod.ShrinkwrapSnap == ShrinkwrapSnapKind.OnSurface &&
                             bvh.RaycastNearest(p, -projAxis, out float tB, out var nB))
                    {
                        targetPos = ApplySnapOffset(p, p - projAxis * tB, nB, bvh, mod.ShrinkwrapSnap, offset);
                        resolved = true;
                    }
                    break;
                }

                default: // NearestSurfacePoint
                {
                    if (bvh.FindClosestPoint(p, out var cp, out var cNorm, out _))
                    {
                        targetPos = ApplySnapOffset(p, cp, cNorm, bvh, mod.ShrinkwrapSnap, offset);
                        resolved = true;
                    }
                    break;
                }
            }

            if (resolved)
                v.Position = Vector3.Lerp(p, targetPos, mix);
        }

        work.ClearCustomNormals();
        work.RecalculateNormals();
        return work;
    }

    private static Vector3 ApplySnapOffset(
        Vector3 orig, Vector3 surfacePt, Vector3 surfaceNormal, TriangleBvh bvh,
        ShrinkwrapSnapKind snap, float offset)
    {
        switch (snap)
        {
            case ShrinkwrapSnapKind.Outside:
                return bvh.IsPointInside(orig) ? surfacePt + surfaceNormal * offset : orig;
            case ShrinkwrapSnapKind.Inside:
                return !bvh.IsPointInside(orig) ? surfacePt - surfaceNormal * offset : orig;
            case ShrinkwrapSnapKind.OutsideSurface:
                return bvh.IsPointInside(orig)
                    ? surfacePt + surfaceNormal * offset
                    : orig + SafeNormalize(orig - surfacePt, surfaceNormal) * MathF.Max(0f, offset - Vector3.Distance(orig, surfacePt));
            default: // OnSurface
                return surfacePt + surfaceNormal * offset;
        }
    }

    #endregion

    #region 7 & 8. 細分化 (Subdivision Simple / Catmull-Clark)

    private static EditableMesh ApplySubdivision(
        EditableMesh input, int levels, bool catmullClark, bool keepCorners, bool smoothShading)
    {
        int lv = Math.Clamp(levels, 1, 4);
        var current = input.Clone();
        current.RecalculateNormals();

        for (int step = 0; step < lv; step++)
        {
            int nextFaces = 0;
            for (int fi = 0; fi < current.Faces.Count; fi++)
                nextFaces += Math.Max(0, current.Faces[fi].VertexIndices.Count);
            if (nextFaces == 0 || nextFaces > MaxOutputFaces ||
                current.Vertices.Count + nextFaces + current.Edges.Count > MaxOutputVertices)
                break;

            current = SubdivideOneStep(current, catmullClark, keepCorners, smoothShading);
        }
        return current;
    }

    private static EditableMesh SubdivideOneStep(
        EditableMesh src, bool catmullClark, bool keepCorners, bool smoothShading)
    {
        int origVCount = src.Vertices.Count;
        int origFCount = src.Faces.Count;

        var facePoints = new Vector3[origFCount];
        var facePointIdx = new int[origFCount];
        var faceAvgUvs = new Vector2[origFCount];

        // 無向辺ごとの隣接面と各頂点の隣接情報
        var edgeFaces = new Dictionary<ulong, List<int>>(src.Edges.Count + origFCount);
        var vIncFaces = new List<int>[origVCount];
        var vIncEdges = new List<ulong>[origVCount];
        for (int i = 0; i < origVCount; i++)
        {
            vIncFaces[i] = new List<int>(6);
            vIncEdges[i] = new List<ulong>(6);
        }

        for (int fi = 0; fi < origFCount; fi++)
        {
            var face = src.Faces[fi];
            var vi = face.VertexIndices;
            int n = vi.Count;
            if (n < 3) continue;

            Vector3 pSum = Vector3.Zero;
            Vector2 uvSum = Vector2.Zero;
            for (int k = 0; k < n; k++)
            {
                int vIdx = vi[k];
                pSum += src.Vertices[vIdx].Position;
                uvSum += k < face.FaceUVs.Count ? face.FaceUVs[k] : src.Vertices[vIdx].UV;
                vIncFaces[vIdx].Add(fi);

                int vNext = vi[(k + 1) % n];
                ulong eKey = MeshSelection.PackEdge(vIdx, vNext);
                if (!edgeFaces.TryGetValue(eKey, out var fList))
                {
                    fList = new List<int>(2);
                    edgeFaces[eKey] = fList;
                }
                fList.Add(fi);
                if (!vIncEdges[vIdx].Contains(eKey)) vIncEdges[vIdx].Add(eKey);
                if (!vIncEdges[vNext].Contains(eKey)) vIncEdges[vNext].Add(eKey);
            }

            facePoints[fi] = pSum / n;
            faceAvgUvs[fi] = uvSum / n;
        }

        var dst = CreateMetadataClone(src);
        dst.Vertices.Capacity = origVCount + origFCount + edgeFaces.Count;

        // 1. 新しい元頂点座標 (0 .. origVCount-1)
        for (int vi = 0; vi < origVCount; vi++)
        {
            var nv = src.Vertices[vi].Clone();
            if (catmullClark && vIncFaces[vi].Count >= 2 && vIncEdges[vi].Count >= 2)
            {
                var incE = vIncEdges[vi];
                var boundaryOthers = new List<int>(2);
                Vector3 avgM = Vector3.Zero;
                for (int i = 0; i < incE.Count; i++)
                {
                    ulong key = incE[i];
                    int v0 = (int)(key >> 32);
                    int v1 = (int)(key & 0xFFFFFFFFu);
                    avgM += (src.Vertices[v0].Position + src.Vertices[v1].Position) * 0.5f;
                    if (edgeFaces.TryGetValue(key, out var efl) && efl.Count == 1)
                        boundaryOthers.Add(v0 == vi ? v1 : v0);
                }

                if (boundaryOthers.Count == 0)
                {
                    int k = vIncFaces[vi].Count;
                    if (k >= 3 && incE.Count == k)
                    {
                        Vector3 avgF = Vector3.Zero;
                        for (int i = 0; i < k; i++) avgF += facePoints[vIncFaces[vi][i]];
                        avgF /= k;
                        avgM /= incE.Count;
                        nv.Position = (avgF + 2f * avgM + (k - 3f) * nv.Position) / k;
                    }
                }
                else if (boundaryOthers.Count == 2 && !keepCorners)
                {
                    nv.Position = 0.75f * nv.Position +
                        0.125f * (src.Vertices[boundaryOthers[0]].Position + src.Vertices[boundaryOthers[1]].Position);
                }
            }
            dst.Vertices.Add(nv);
        }

        // 2. 面中心点 (origVCount .. )
        for (int fi = 0; fi < origFCount; fi++)
        {
            facePointIdx[fi] = dst.Vertices.Count;
            dst.Vertices.Add(new EditableVertex
            {
                Position = facePoints[fi],
                UV = faceAvgUvs[fi]
            });
        }

        // 3. 辺分割点
        var edgePointIdx = new Dictionary<ulong, int>(edgeFaces.Count);
        foreach (var kv in edgeFaces)
        {
            ulong key = kv.Key;
            int v0 = (int)(key >> 32);
            int v1 = (int)(key & 0xFFFFFFFFu);
            var list = kv.Value;

            Vector3 ep;
            if (catmullClark && list.Count == 2)
                ep = (src.Vertices[v0].Position + src.Vertices[v1].Position + facePoints[list[0]] + facePoints[list[1]]) * 0.25f;
            else
                ep = (src.Vertices[v0].Position + src.Vertices[v1].Position) * 0.5f;

            edgePointIdx[key] = dst.Vertices.Count;
            dst.Vertices.Add(new EditableVertex
            {
                Position = ep,
                UV = (src.Vertices[v0].UV + src.Vertices[v1].UV) * 0.5f
            });
        }

        // 4. 各 N角形面を N 枚のクワッドへ分割
        for (int fi = 0; fi < origFCount; fi++)
        {
            var f = src.Faces[fi];
            var vi = f.VertexIndices;
            int n = vi.Count;
            if (n < 3) continue;

            int fpIdx = facePointIdx[fi];
            Vector2 uvF = faceAvgUvs[fi];
            int matIdx = Math.Clamp(f.MaterialIndex, 0, dst.Materials.Count - 1);
            bool childSmooth = catmullClark ? smoothShading : f.Smooth;

            for (int k = 0; k < n; k++)
            {
                int vCur = vi[k];
                int vNext = vi[(k + 1) % n];
                int vPrev = vi[(k - 1 + n) % n];

                int eNextIdx = edgePointIdx[MeshSelection.PackEdge(vCur, vNext)];
                int ePrevIdx = edgePointIdx[MeshSelection.PackEdge(vPrev, vCur)];

                Vector2 uvCur = k < f.FaceUVs.Count ? f.FaceUVs[k] : src.Vertices[vCur].UV;
                Vector2 uvNext = ((k + 1) % n) < f.FaceUVs.Count ? f.FaceUVs[(k + 1) % n] : src.Vertices[vNext].UV;
                Vector2 uvPrev = ((k - 1 + n) % n) < f.FaceUVs.Count ? f.FaceUVs[(k - 1 + n) % n] : src.Vertices[vPrev].UV;

                dst.Faces.Add(new EditableFace
                {
                    VertexIndices = new List<int> { vCur, eNextIdx, fpIdx, ePrevIdx },
                    FaceUVs = new List<Vector2>
                    {
                        uvCur,
                        (uvCur + uvNext) * 0.5f,
                        uvF,
                        (uvPrev + uvCur) * 0.5f
                    },
                    MaterialIndex = matIdx,
                    Smooth = childSmooth
                });
            }
        }

        dst.RebuildEdges(false);
        dst.RecalculateNormals();
        return dst;
    }

    #endregion

    #region 9. ディスプレイス (Displace)

    private static EditableMesh ApplyDisplace(EditableMesh input, MeshModifierItem mod)
    {
        if (input.Vertices.Count == 0 || MathF.Abs(mod.DisplaceStrength) < 1e-6f)
            return input.Clone();

        var work = input.Clone();
        work.RecalculateNormals();

        // 頂点ごとのフォールバック UV (FaceUVs から代表 UV を取得)
        var vertexUvs = new Vector2[work.Vertices.Count];
        var uvCounts = new int[work.Vertices.Count];
        for (int fi = 0; fi < work.Faces.Count; fi++)
        {
            var f = work.Faces[fi];
            for (int k = 0; k < f.VertexIndices.Count; k++)
            {
                int vi = f.VertexIndices[k];
                Vector2 uv = k < f.FaceUVs.Count ? f.FaceUVs[k] : work.Vertices[vi].UV;
                vertexUvs[vi] += uv;
                uvCounts[vi]++;
            }
        }
        for (int i = 0; i < work.Vertices.Count; i++)
        {
            vertexUvs[i] = uvCounts[i] > 0 ? vertexUvs[i] / uvCounts[i] : work.Vertices[i].UV;
        }

        var origPositions = new Vector3[work.Vertices.Count];
        var origNormals = new Vector3[work.Vertices.Count];
        for (int i = 0; i < work.Vertices.Count; i++)
        {
            origPositions[i] = work.Vertices[i].Position;
            origNormals[i] = SafeNormalize(work.Vertices[i].Normal, Vector3.UnitY);
        }

        float scale = MathF.Max(0.01f, mod.DisplaceScale);
        float detail = Math.Clamp(mod.DisplaceDetail, 0f, 8f);
        Vector3 offset = new(mod.DisplaceOffsetX, mod.DisplaceOffsetY, mod.DisplaceOffsetZ);

        for (int i = 0; i < work.Vertices.Count; i++)
        {
            Vector3 coord = mod.DisplaceCoord == DisplaceCoordKind.UV
                ? new Vector3(vertexUvs[i].X, vertexUvs[i].Y, 0f)
                : origPositions[i];

            float h = mod.DisplaceTexture switch
            {
                DisplaceTextureKind.Voronoi =>
                    ShaderNodeEvaluator.EvalVoronoi2DF1(
                        coord.X * scale + offset.X,
                        coord.Y * scale + offset.Y + coord.Z * scale * 0.37f),
                DisplaceTextureKind.Wave => EvalWaveHeight(coord, scale, detail, offset),
                DisplaceTextureKind.Checker => EvalCheckerHeight(coord, scale, offset, mod.DisplaceCoord == DisplaceCoordKind.UV),
                DisplaceTextureKind.Image => ShaderNodeEvaluator.SampleImageHeight(
                    mod.DisplaceImagePath,
                    coord.X * (mod.DisplaceCoord == DisplaceCoordKind.UV ? 1f : 0.5f * scale) + offset.X,
                    coord.Y * (mod.DisplaceCoord == DisplaceCoordKind.UV ? 1f : 0.5f * scale) + offset.Y),
                _ => ShaderNodeEvaluator.EvalFractalNoise3D(coord * scale + offset, detail, 0.5f, 2f)
            };

            Vector3 dir = mod.DisplaceDirection switch
            {
                DisplaceDirectionKind.X => Vector3.UnitX,
                DisplaceDirectionKind.Y => Vector3.UnitY,
                DisplaceDirectionKind.Z => Vector3.UnitZ,
                _ => origNormals[i]
            };

            work.Vertices[i].Position = origPositions[i] + dir * ((h - mod.DisplaceMidlevel) * mod.DisplaceStrength);
        }

        work.ClearCustomNormals();
        work.RecalculateNormals();
        return work;
    }

    private static float EvalWaveHeight(Vector3 coord, float scale, float detail, Vector3 offset)
    {
        Vector3 p = coord * scale + offset;
        float dist = ShaderNodeEvaluator.EvalFractalNoise3D(p * 0.5f, MathF.Min(detail, 3f), 0.5f, 2f) * (detail * 0.8f);
        return 0.5f + 0.5f * MathF.Sin((p.X + p.Y + p.Z) * MathF.PI + dist);
    }

    private static float EvalCheckerHeight(Vector3 coord, float scale, Vector3 offset, bool isUv)
    {
        Vector3 p = coord * scale + offset;
        int ix = (int)MathF.Floor(p.X);
        int iy = (int)MathF.Floor(p.Y);
        int iz = isUv ? 0 : (int)MathF.Floor(p.Z);
        return ((ix + iy + iz) & 1) == 0 ? 1f : 0f;
    }

    #endregion

    #region 10. 三角形化 (Triangulate)

    private static EditableMesh ApplyTriangulate(EditableMesh input, MeshModifierItem mod)
    {
        var src = input;
        if (mod.TriangulateKeepNormals)
        {
            src = input.Clone();
            src.RecalculateNormals();
        }

        var dst = CreateMetadataClone(src);
        dst.Vertices.Capacity = src.Vertices.Count;
        foreach (var v in src.Vertices)
            dst.Vertices.Add(v.Clone());

        int minVerts = Math.Clamp(mod.TriangulateMinVertices, 3, 16);
        bool keepNormals = mod.TriangulateKeepNormals;

        for (int fi = 0; fi < src.Faces.Count; fi++)
        {
            var f = src.Faces[fi];
            int count = f.VertexIndices.Count;
            if (count < 3) continue;

            if (count < minVerts || count == 3)
            {
                dst.Faces.Add(f.Clone());
                continue;
            }

            List<Vector3>? polyCornerNormals = null;
            if (keepNormals)
            {
                if (f.FaceCornerNormals != null && f.FaceCornerNormals.Count == count)
                {
                    polyCornerNormals = f.FaceCornerNormals;
                }
                else
                {
                    Vector3 flatN = ComputeFaceNormalSafe(src, f);
                    polyCornerNormals = new List<Vector3>(count);
                    for (int k = 0; k < count; k++)
                    {
                        int vi = f.VertexIndices[k];
                        Vector3 n = f.Smooth && vi >= 0 && vi < src.Vertices.Count
                            ? SafeNormalize(src.Vertices[vi].Normal, flatN)
                            : flatN;
                        polyCornerNormals.Add(n);
                    }
                }
            }

            void EmitTri(int i0, int i1, int i2)
            {
                var tri = new EditableFace
                {
                    MaterialIndex = f.MaterialIndex,
                    Smooth = f.Smooth,
                    VertexIndices = new List<int>
                    {
                        f.VertexIndices[i0],
                        f.VertexIndices[i1],
                        f.VertexIndices[i2]
                    },
                    FaceUVs = new List<Vector2>
                    {
                        GetCornerUv(src, f, i0),
                        GetCornerUv(src, f, i1),
                        GetCornerUv(src, f, i2)
                    }
                };
                if (polyCornerNormals != null)
                {
                    tri.FaceCornerNormals = new List<Vector3>
                    {
                        polyCornerNormals[i0],
                        polyCornerNormals[i1],
                        polyCornerNormals[i2]
                    };
                }
                dst.Faces.Add(tri);
            }

            if (count == 4)
            {
                Vector3 p0 = src.Vertices[f.VertexIndices[0]].Position;
                Vector3 p1 = src.Vertices[f.VertexIndices[1]].Position;
                Vector3 p2 = src.Vertices[f.VertexIndices[2]].Position;
                Vector3 p3 = src.Vertices[f.VertexIndices[3]].Position;

                bool useDiag02 = mod.TriangulateQuadMethod switch
                {
                    TriangulateQuadMethod.Fixed => true,
                    TriangulateQuadMethod.FixedAlternate => false,
                    TriangulateQuadMethod.ShortestDiagonal => Vector3.DistanceSquared(p0, p2) <= Vector3.DistanceSquared(p1, p3),
                    TriangulateQuadMethod.LongestDiagonal => Vector3.DistanceSquared(p0, p2) >= Vector3.DistanceSquared(p1, p3),
                    _ => EvaluateQuadBeauty02(p0, p1, p2, p3)
                };

                if (useDiag02)
                {
                    EmitTri(0, 1, 2);
                    EmitTri(0, 2, 3);
                }
                else
                {
                    EmitTri(0, 1, 3);
                    EmitTri(1, 2, 3);
                }
            }
            else
            {
                TriangulateNgon(src, f, mod.TriangulateNgonMethod, EmitTri);
            }
        }

        dst.RebuildEdges(false);
        if (!keepNormals)
            dst.RecalculateNormals();
        return dst;
    }

    private static Vector2 GetCornerUv(EditableMesh mesh, EditableFace f, int corner)
    {
        if (corner < f.FaceUVs.Count)
            return f.FaceUVs[corner];
        int vi = f.VertexIndices[corner];
        return vi >= 0 && vi < mesh.Vertices.Count ? mesh.Vertices[vi].UV : Vector2.Zero;
    }

    private static Vector3 ComputeFaceNormalSafe(EditableMesh mesh, EditableFace f)
    {
        Vector3 n = Vector3.Zero;
        int c = f.VertexIndices.Count;
        for (int i = 0; i < c; i++)
        {
            Vector3 a = mesh.Vertices[f.VertexIndices[i]].Position;
            Vector3 b = mesh.Vertices[f.VertexIndices[(i + 1) % c]].Position;
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return SafeNormalize(n, Vector3.UnitY);
    }

    private static float ComputeTriangleQuality(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a;
        Vector3 bc = c - b;
        Vector3 ca = a - c;
        float denom = ab.LengthSquared() + bc.LengthSquared() + ca.LengthSquared();
        if (denom < 1e-12f) return 0f;
        float area2 = Vector3.Cross(ab, c - a).Length();
        const float InvEquilateral = 1.7320508f * 2f; // 2*sqrt(3) for 2*area
        return InvEquilateral * area2 / denom;
    }

    private static bool EvaluateQuadBeauty02(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        Vector3 nA0 = Vector3.Cross(p1 - p0, p2 - p0);
        Vector3 nA1 = Vector3.Cross(p2 - p0, p3 - p0);
        Vector3 nB0 = Vector3.Cross(p1 - p0, p3 - p0);
        Vector3 nB1 = Vector3.Cross(p2 - p1, p3 - p1);
        float q02 = MathF.Min(ComputeTriangleQuality(p0, p1, p2), ComputeTriangleQuality(p0, p2, p3));
        float q13 = MathF.Min(ComputeTriangleQuality(p0, p1, p3), ComputeTriangleQuality(p1, p2, p3));
        if (Vector3.Dot(nA0, nA1) < 0f) q02 *= 0.25f;
        if (Vector3.Dot(nB0, nB1) < 0f) q13 *= 0.25f;
        return q02 >= q13;
    }

    private static void TriangulateNgon(
        EditableMesh mesh,
        EditableFace f,
        TriangulateNgonMethod method,
        Action<int, int, int> emitTri)
    {
        int n = f.VertexIndices.Count;
        Vector3 normal = ComputeFaceNormalSafe(mesh, f);
        Vector3 refUp = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 tangent = SafeNormalize(Vector3.Cross(refUp, normal), Vector3.UnitX);
        Vector3 bitangent = SafeNormalize(Vector3.Cross(normal, tangent), Vector3.UnitZ);

        var pts2D = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 p = mesh.Vertices[f.VertexIndices[i]].Position;
            pts2D[i] = new Vector2(Vector3.Dot(p, tangent), Vector3.Dot(p, bitangent));
        }

        // Signed 2D area check to match CCW orientation in local plane
        float signedArea2 = 0f;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            signedArea2 += pts2D[i].X * pts2D[j].Y - pts2D[j].X * pts2D[i].Y;
        }
        bool ccw = signedArea2 >= 0f;

        var remaining = new List<int>(n);
        for (int i = 0; i < n; i++) remaining.Add(i);

        bool beauty = method == TriangulateNgonMethod.Beauty;
        int guard = n * n + 8;
        while (remaining.Count > 3 && guard-- > 0)
        {
            int m = remaining.Count;
            int bestIdx = -1;
            float bestScore = float.NegativeInfinity;

            for (int r = 0; r < m; r++)
            {
                int iPrev = remaining[(r - 1 + m) % m];
                int iCurr = remaining[r];
                int iNext = remaining[(r + 1) % m];

                Vector2 a = pts2D[iPrev];
                Vector2 b = pts2D[iCurr];
                Vector2 c = pts2D[iNext];
                float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (ccw ? (cross <= 1e-8f) : (cross >= -1e-8f))
                    continue;

                bool containsOther = false;
                for (int k = 0; k < m; k++)
                {
                    int ik = remaining[k];
                    if (ik == iPrev || ik == iCurr || ik == iNext) continue;
                    if (PointInTri2D(pts2D[ik], a, b, c))
                    {
                        containsOther = true;
                        break;
                    }
                }
                if (containsOther) continue;

                if (!beauty)
                {
                    bestIdx = r;
                    break;
                }

                float q = ComputeTriangleQuality(
                    mesh.Vertices[f.VertexIndices[iPrev]].Position,
                    mesh.Vertices[f.VertexIndices[iCurr]].Position,
                    mesh.Vertices[f.VertexIndices[iNext]].Position);
                if (q > bestScore)
                {
                    bestScore = q;
                    bestIdx = r;
                }
            }

            if (bestIdx < 0)
                break;

            int pm = remaining.Count;
            int aIdx = remaining[(bestIdx - 1 + pm) % pm];
            int bIdx = remaining[bestIdx];
            int cIdx = remaining[(bestIdx + 1) % pm];
            emitTri(aIdx, bIdx, cIdx);
            remaining.RemoveAt(bestIdx);
        }

        for (int k = 1; k + 1 < remaining.Count; k++)
            emitTri(remaining[0], remaining[k], remaining[k + 1]);
    }

    private static bool PointInTri2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
        float d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
        float d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
        bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(hasNeg && hasPos);
    }

    #endregion

    private static EditableMesh CreateMetadataClone(EditableMesh src)
    {
        var dst = new EditableMesh
        {
            SourceFilePath = src.SourceFilePath,
            HasRefBounds = src.HasRefBounds,
            RefBoundsMin = src.RefBoundsMin,
            RefBoundsMax = src.RefBoundsMax,
            RefCenter = src.RefCenter,
            RefScale = src.RefScale,
        };
        foreach (var m in src.Materials) dst.Materials.Add(m.Clone());
        if (dst.Materials.Count == 0) dst.Materials.Add(new EditableMaterial());
        return dst;
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float lenSq = v.LengthSquared();
        return lenSq > 1e-10f ? v / MathF.Sqrt(lenSq) : fallback;
    }
}
