using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Iyahon_D3D11Renderer_Core.Lighting;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal static class ViewportOverlayBuilder
{
    private static readonly Vector4 ColGridMinor = new(0.32f, 0.34f, 0.38f, 0.35f);
    private static readonly Vector4 ColGridMajor = new(0.46f, 0.49f, 0.54f, 0.55f);
    private static readonly Vector4 ColGridAxisX = new(0.88f, 0.26f, 0.26f, 0.85f);
    private static readonly Vector4 ColGridAxisY = new(0.32f, 0.84f, 0.30f, 0.85f);
    private static readonly Vector4 ColGridAxisZ = new(0.28f, 0.52f, 0.94f, 0.85f);

    private static readonly Vector4 ColFrustum = new(0.75f, 0.80f, 0.90f, 0.55f);
    private static readonly Vector4 ColSelectedObb = new(1.0f, 0.72f, 0.20f, 0.95f);

    private static readonly Vector4 ColAxisX = new(0.95f, 0.25f, 0.30f, 1.0f);
    private static readonly Vector4 ColAxisY = new(0.35f, 0.90f, 0.30f, 1.0f);
    private static readonly Vector4 ColAxisZ = new(0.30f, 0.55f, 1.00f, 1.0f);
    private static readonly Vector4 ColCenter = new(0.95f, 0.95f, 0.95f, 1.0f);
    private static readonly Vector4 ColHover = new(1.00f, 0.90f, 0.25f, 1.0f);

    public static void BuildFloorGrid(List<Viewport3DRenderer.OverlayVertex> lines, bool useBlenderAxis = false)
    {
        lines.Clear();
        const float halfExtent = 2000f;
        const float step = 100f;
        int steps = (int)(halfExtent / step);
        Vector4 depthAxisCol = useBlenderAxis ? ColGridAxisY : ColGridAxisZ;

        for (int i = -steps; i <= steps; i++)
        {
            float c = i * step;
            bool isOrigin = i == 0;
            bool isMajor = (i % 5) == 0;

            Vector4 colX = isOrigin ? ColGridAxisX : (isMajor ? ColGridMajor : ColGridMinor);
            lines.Add(new() { Position = new Vector3(-halfExtent, 0f, c), Color = colX });
            lines.Add(new() { Position = new Vector3(halfExtent, 0f, c), Color = colX });

            Vector4 colZ = isOrigin ? depthAxisCol : (isMajor ? ColGridMajor : ColGridMinor);
            lines.Add(new() { Position = new Vector3(c, 0f, -halfExtent), Color = colZ });
            lines.Add(new() { Position = new Vector3(c, 0f, halfExtent), Color = colZ });
        }
    }

    public static void BuildCameraFrustum(
        List<Viewport3DRenderer.OverlayVertex> lines,
        in Matrix4x4 ymmCameraMatrix,
        int videoW,
        int videoH)
    {
        if (!Matrix4x4.Invert(ymmCameraMatrix, out var invCam))
            invCam = Matrix4x4.Identity;

        float hw = Math.Max(64, videoW) * 0.5f;
        float hh = Math.Max(64, videoH) * 0.5f;

        Vector3 eye = Vector3.Transform(new Vector3(0f, 0f, 1000f), invCam);
        Vector3 n0 = Vector3.Transform(new Vector3(-hw * 0.2f, -hh * 0.2f, 800f), invCam);
        Vector3 n1 = Vector3.Transform(new Vector3(hw * 0.2f, -hh * 0.2f, 800f), invCam);
        Vector3 n2 = Vector3.Transform(new Vector3(hw * 0.2f, hh * 0.2f, 800f), invCam);
        Vector3 n3 = Vector3.Transform(new Vector3(-hw * 0.2f, hh * 0.2f, 800f), invCam);

        Vector3 f0 = Vector3.Transform(new Vector3(-hw, -hh, 0f), invCam);
        Vector3 f1 = Vector3.Transform(new Vector3(hw, -hh, 0f), invCam);
        Vector3 f2 = Vector3.Transform(new Vector3(hw, hh, 0f), invCam);
        Vector3 f3 = Vector3.Transform(new Vector3(-hw, hh, 0f), invCam);

        AddQuadLoop(lines, n0, n1, n2, n3, ColFrustum);
        AddQuadLoop(lines, f0, f1, f2, f3, ColFrustum);

        AddLine(lines, eye, f0, ColFrustum);
        AddLine(lines, eye, f1, ColFrustum);
        AddLine(lines, eye, f2, ColFrustum);
        AddLine(lines, eye, f3, ColFrustum);

        // Top-up indicator triangle above frame
        Vector3 topL = Vector3.Transform(new Vector3(-hw * 0.2f, -hh * 1.04f, 0f), invCam);
        Vector3 topR = Vector3.Transform(new Vector3(hw * 0.2f, -hh * 1.04f, 0f), invCam);
        Vector3 topP = Vector3.Transform(new Vector3(0f, -hh * 1.24f, 0f), invCam);
        AddLine(lines, topL, topR, ColFrustum);
        AddLine(lines, topR, topP, ColFrustum);
        AddLine(lines, topP, topL, ColFrustum);
    }

    public static void BuildSelectionAndLightOverlays(
        List<Viewport3DRenderer.OverlayVertex> lines,
        ViewportSceneSnapshot snapshot,
        ViewportManipulator manipulator,
        IReadOnlyList<IVideoItem> selectedItems,
        bool showLights,
        IVideoItem? skipObbItem = null)
    {
        for (int i = 0; i < snapshot.Items.Count; i++)
        {
            var entry = snapshot.Items[i];
            bool isSelected = selectedItems.Contains(entry.Item);
            var desc = manipulator.GetEffectiveDrawDescription(entry);

            if (entry.Kind == ViewportItemKind.Light)
            {
                var light = manipulator.GetEffectiveLight(entry, desc);
                if (!showLights && !isSelected && !light.ShowGizmo) continue;
                BuildLightOverlay(lines, light, isSelected);
                continue;
            }

            if (!isSelected) continue;
            if (ReferenceEquals(entry.Item, skipObbItem)) continue;

            if (entry.Kind == ViewportItemKind.BuiltInModel && entry.ModelSpec != null)
            {
                var dummy = new ObjModelData
                {
                    VertexBuffer = null!,
                    IndexBuffer = null!,
                    Parts = Array.Empty<ObjPartRenderInfo>(),
                    BoundsMin = entry.ModelSpec.BoundsMin,
                    BoundsMax = entry.ModelSpec.BoundsMax,
                    ModelCenter = entry.ModelSpec.ModelCenter,
                    ModelScale = entry.ModelSpec.ModelScale,
                    NormalizeSize = entry.ModelSpec.NormalizeSize,
                    GroundToOrigin = entry.ModelSpec.GroundToOrigin,
                    UnitScale = entry.ModelSpec.UnitScale,
                    LocalScale = entry.ModelSpec.LocalScale,
                    LocalRotationDeg = entry.ModelSpec.LocalRotationDeg,
                };
                if (manipulator.GetLiveOverride(entry.Item) is { } ov)
                {
                    var sp = entry.ModelSpec;
                    dummy = new ObjModelData
                    {
                        VertexBuffer = null!,
                        IndexBuffer = null!,
                        Parts = Array.Empty<ObjPartRenderInfo>(),
                        BoundsMin = sp.BoundsMin,
                        BoundsMax = sp.BoundsMax,
                        ModelCenter = sp.ModelCenter,
                        ModelScale = sp.ModelScale,
                        NormalizeSize = sp.NormalizeSize,
                        GroundToOrigin = sp.GroundToOrigin,
                        UnitScale = sp.UnitScale,
                        LocalScale = ViewportManipulator.ComputeEffectiveLocalScale(sp, ov),
                        LocalRotationDeg = ov.ModelLocalRotDeg ?? sp.LocalRotationDeg,
                    };
                }
                var world = DepthSortRenderer.BuildObjModelMatrix(new RenderItem { DrawDescription = desc, Srv = null, UseBlenderAxis = entry.UsesBlenderAxis }, dummy);
                Vector3 bMin = dummy.BoundsMin;
                Vector3 bMax = dummy.BoundsMax;
                if (Vector3.DistanceSquared(bMax, bMin) < 1e-6f)
                {
                    bMin = new Vector3(-1f, -1f, -1f);
                    bMax = new Vector3(1f, 1f, 1f);
                }
                AddWorldObb(lines, world, bMin, bMax, ColSelectedObb);
            }
            else
            {
                var ri = new RenderItem
                {
                    DrawDescription = desc,
                    Srv = null,
                    PixelWidth = MathF.Max(1f, entry.PixelWidth),
                    PixelHeight = MathF.Max(1f, entry.PixelHeight),
                    BoundsCenterX = entry.BoundsCenterX,
                    BoundsCenterY = entry.BoundsCenterY,
                    UseBlenderAxis = entry.UsesBlenderAxis,
                };
                var world = DepthSortRenderer.BuildModelMatrix(ri);
                float hz = entry.Kind == ViewportItemKind.D3DEffect ? 0.5f : 0.02f;
                AddWorldObb(lines, world, new Vector3(-0.5f, -0.5f, -hz), new Vector3(0.5f, 0.5f, hz), ColSelectedObb);
            }
        }
    }

    public static void BuildTransformGizmo(
        List<Viewport3DRenderer.OverlayVertex> lines,
        List<Viewport3DRenderer.OverlayVertex> tris,
        in TransformGizmoState gizmo,
        GizmoMode mode,
        GizmoHandle highlight,
        bool isDragging)
    {
        float dim = isDragging ? 0.35f : 0.85f;
        Vector4 HandleCol(GizmoHandle h, Vector4 baseCol)
        {
            if (highlight == h) return ColHover;
            return new Vector4(baseCol.X, baseCol.Y, baseCol.Z, baseCol.W * dim);
        }

        if (mode == GizmoMode.Translate)
        {
            // Plane handle quads
            Span<Vector3> q = stackalloc Vector3[4];
            BuildPlaneQuad(lines, tris, gizmo, GizmoHandle.PlaneXY, q, HandleCol(GizmoHandle.PlaneXY, ColAxisZ));
            BuildPlaneQuad(lines, tris, gizmo, GizmoHandle.PlaneYZ, q, HandleCol(GizmoHandle.PlaneYZ, ColAxisX));
            BuildPlaneQuad(lines, tris, gizmo, GizmoHandle.PlaneZX, q, HandleCol(GizmoHandle.PlaneZX, ColAxisY));

            BuildMoveAxis(lines, tris, gizmo, GizmoHandle.MoveX, HandleCol(GizmoHandle.MoveX, ColAxisX));
            BuildMoveAxis(lines, tris, gizmo, GizmoHandle.MoveY, HandleCol(GizmoHandle.MoveY, ColAxisY));
            BuildMoveAxis(lines, tris, gizmo, GizmoHandle.MoveZ, HandleCol(GizmoHandle.MoveZ, ColAxisZ));

            AddCenterDiamond(tris, gizmo.Origin, gizmo.Scale * 0.08f, HandleCol(GizmoHandle.MoveFree, ColCenter));
        }
        else if (mode == GizmoMode.Rotate)
        {
            BuildRing(lines, gizmo, Vector3.UnitY, Vector3.UnitZ, HandleCol(GizmoHandle.RotateX, ColAxisX));
            BuildRing(lines, gizmo, Vector3.UnitZ, Vector3.UnitX, HandleCol(GizmoHandle.RotateY, ColAxisY));
            BuildRing(lines, gizmo, Vector3.UnitX, Vector3.UnitY, HandleCol(GizmoHandle.RotateZ, ColAxisZ));
        }
        else
        {
            BuildScaleAxis(lines, tris, gizmo, GizmoHandle.ScaleX, HandleCol(GizmoHandle.ScaleX, ColAxisX));
            BuildScaleAxis(lines, tris, gizmo, GizmoHandle.ScaleY, HandleCol(GizmoHandle.ScaleY, ColAxisY));
            BuildScaleAxis(lines, tris, gizmo, GizmoHandle.ScaleZ, HandleCol(GizmoHandle.ScaleZ, ColAxisZ));

            AddCenterCube(tris, gizmo.Origin, gizmo.Orientation, gizmo.Scale * 0.065f, HandleCol(GizmoHandle.ScaleUniform, ColCenter));
        }
    }

    public static void BuildAxisCompassNdc(
        List<Viewport3DRenderer.OverlayVertex> ndcLines,
        in Matrix4x4 viewMatrix,
        int width,
        int height)
    {
        if (width <= 80 || height <= 80) return;

        float cx = width - 48f;
        float cy = 48f;
        const float len = 28f;
        Matrix4x4 viewMat = viewMatrix;

        Vector3 ToScreenDelta(Vector3 worldAxis)
        {
            Vector3 v = Vector3.TransformNormal(worldAxis, viewMat);
            return new Vector3(cx + v.X * len, cy - v.Y * len, 0f);
        }

        Vector3 centerNdc = ToNdc(cx, cy, width, height);
        AddLine(ndcLines, centerNdc, ToNdc(ToScreenDelta(Vector3.UnitX), width, height), ColAxisX);
        AddLine(ndcLines, centerNdc, ToNdc(ToScreenDelta(Vector3.UnitY), width, height), ColAxisY);
        AddLine(ndcLines, centerNdc, ToNdc(ToScreenDelta(Vector3.UnitZ), width, height), ColAxisZ);
    }

    private static Vector3 ToNdc(Vector3 px, int w, int h) => ToNdc(px.X, px.Y, w, h);

    private static Vector3 ToNdc(float px, float py, int w, int h)
        => new((px / w) * 2f - 1f, 1f - (py / h) * 2f, 0.2f);

    private static void BuildMoveAxis(
        List<Viewport3DRenderer.OverlayVertex> lines,
        List<Viewport3DRenderer.OverlayVertex> tris,
        in TransformGizmoState gizmo,
        GizmoHandle h,
        Vector4 col)
    {
        Vector3 dir = gizmo.GetAxisVector(h);
        Vector3 start = gizmo.Origin + dir * (gizmo.Scale * 0.10f);
        Vector3 neck = gizmo.Origin + dir * (gizmo.Scale * 0.82f);
        Vector3 tip = gizmo.Origin + dir * (gizmo.Scale * 1.0f);
        AddLine(lines, start, tip, col);

        Vector3 hint = MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 u = Vector3.Normalize(Vector3.Cross(hint, dir)) * (gizmo.Scale * 0.055f);
        Vector3 v = Vector3.Cross(dir, u);

        const int sides = 8;
        for (int i = 0; i < sides; i++)
        {
            float a0 = MathF.Tau * i / sides;
            float a1 = MathF.Tau * (i + 1) / sides;
            Vector3 p0 = neck + u * MathF.Cos(a0) + v * MathF.Sin(a0);
            Vector3 p1 = neck + u * MathF.Cos(a1) + v * MathF.Sin(a1);
            AddTri(tris, tip, p0, p1, col);
        }
    }

    private static void BuildScaleAxis(
        List<Viewport3DRenderer.OverlayVertex> lines,
        List<Viewport3DRenderer.OverlayVertex> tris,
        in TransformGizmoState gizmo,
        GizmoHandle h,
        Vector4 col)
    {
        Vector3 dir = gizmo.GetAxisVector(h);
        Vector3 start = gizmo.Origin + dir * (gizmo.Scale * 0.10f);
        Vector3 end = gizmo.Origin + dir * (gizmo.Scale * 0.88f);
        AddLine(lines, start, end, col);
        AddCenterCube(tris, end, gizmo.Orientation, gizmo.Scale * 0.055f, col);
    }

    private static void BuildPlaneQuad(
        List<Viewport3DRenderer.OverlayVertex> lines,
        List<Viewport3DRenderer.OverlayVertex> tris,
        in TransformGizmoState gizmo,
        GizmoHandle plane,
        Span<Vector3> q,
        Vector4 col)
    {
        gizmo.GetPlaneCornersWorld(plane, q);
        var fillCol = new Vector4(col.X, col.Y, col.Z, col.W * 0.25f);
        AddTri(tris, q[0], q[1], q[2], fillCol);
        AddTri(tris, q[0], q[2], q[3], fillCol);
        AddQuadLoop(lines, q[0], q[1], q[2], q[3], col);
    }

    private static void BuildRing(
        List<Viewport3DRenderer.OverlayVertex> lines,
        in TransformGizmoState gizmo,
        Vector3 a,
        Vector3 b,
        Vector4 col)
    {
        Vector3 prev = gizmo.ToWorld(a * TransformGizmoState.RingRadius);
        for (int i = 1; i <= TransformGizmoState.RingSegments; i++)
        {
            float ang = MathF.Tau * i / TransformGizmoState.RingSegments;
            Vector3 cur = gizmo.ToWorld((a * MathF.Cos(ang) + b * MathF.Sin(ang)) * TransformGizmoState.RingRadius);
            AddLine(lines, prev, cur, col);
            prev = cur;
        }
    }

    private static void BuildLightOverlay(
        List<Viewport3DRenderer.OverlayVertex> lines,
        LightData light,
        bool selected)
    {
        Vector4 col = selected ? ColSelectedObb : new Vector4(light.Color, 0.9f);
        Vector3 pos = light.Position;
        const float iconR = 24f;

        AddWireSphere(lines, pos, iconR, 12, col);

        if (light.Type == LightType.Directional)
        {
            AddLine(lines, pos, pos + light.Direction * 180f, col);
        }
        else if (light.Type == LightType.Point)
        {
            AddWireSphere(lines, pos, MathF.Min(light.Range, 2500f), 20, new Vector4(col.X, col.Y, col.Z, 0.35f));
        }
        else if (light.Type == LightType.Spot)
        {
            float drawRange = MathF.Min(light.Range, 1200f);
            Vector3 fwd = light.Direction;
            Vector3 up = MathF.Abs(fwd.Y) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            Vector3 r = Vector3.Normalize(Vector3.Cross(fwd, up));
            Vector3 u = Vector3.Normalize(Vector3.Cross(r, fwd));
            float rad = light.SpotOuterAngle * (MathF.PI / 180f);
            float coneR = MathF.Tan(Math.Clamp(rad, 0.02f, 1.45f)) * drawRange;
            Vector3 farCenter = pos + fwd * drawRange;

            for (int k = 0; k < 8; k++)
            {
                float a = MathF.Tau * k / 8f;
                Vector3 rim = farCenter + (r * MathF.Cos(a) + u * MathF.Sin(a)) * coneR;
                AddLine(lines, pos, rim, col);
                float aNext = MathF.Tau * (k + 1) / 8f;
                Vector3 rimNext = farCenter + (r * MathF.Cos(aNext) + u * MathF.Sin(aNext)) * coneR;
                AddLine(lines, rim, rimNext, col);
            }
        }
        else if (light.Type == LightType.Area)
        {
            var (fwd, r, u) = LightManager.ComputeAreaBasis(light);
            float hw = light.AreaWidth * 0.5f;
            float hh = light.AreaHeight * 0.5f;
            AddQuadLoop(lines,
                pos + r * hw + u * hh,
                pos - r * hw + u * hh,
                pos - r * hw - u * hh,
                pos + r * hw - u * hh, col);
            AddLine(lines, pos, pos + fwd * 140f, col);
        }
    }

    private static void AddWireSphere(List<Viewport3DRenderer.OverlayVertex> lines, Vector3 center, float radius, int segs, Vector4 col)
    {
        for (int i = 0; i < segs; i++)
        {
            float a0 = MathF.Tau * i / segs;
            float a1 = MathF.Tau * (i + 1) / segs;
            float c0 = MathF.Cos(a0) * radius, s0 = MathF.Sin(a0) * radius;
            float c1 = MathF.Cos(a1) * radius, s1 = MathF.Sin(a1) * radius;
            AddLine(lines, center + new Vector3(c0, s0, 0f), center + new Vector3(c1, s1, 0f), col);
            AddLine(lines, center + new Vector3(c0, 0f, s0), center + new Vector3(c1, 0f, s1), col);
            AddLine(lines, center + new Vector3(0f, c0, s0), center + new Vector3(0f, c1, s1), col);
        }
    }

    private static void AddWorldObb(
        List<Viewport3DRenderer.OverlayVertex> lines,
        in Matrix4x4 world,
        Vector3 min,
        Vector3 max,
        Vector4 col)
    {
        Span<Vector3> p = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            Vector3 local = new(
                (i & 1) == 0 ? min.X : max.X,
                (i & 2) == 0 ? min.Y : max.Y,
                (i & 4) == 0 ? min.Z : max.Z);
            p[i] = Vector3.Transform(local, world);
        }

        AddLine(lines, p[0], p[1], col); AddLine(lines, p[1], p[3], col);
        AddLine(lines, p[3], p[2], col); AddLine(lines, p[2], p[0], col);
        AddLine(lines, p[4], p[5], col); AddLine(lines, p[5], p[7], col);
        AddLine(lines, p[7], p[6], col); AddLine(lines, p[6], p[4], col);
        AddLine(lines, p[0], p[4], col); AddLine(lines, p[1], p[5], col);
        AddLine(lines, p[2], p[6], col); AddLine(lines, p[3], p[7], col);
    }

    internal static void AddCenterDiamond(List<Viewport3DRenderer.OverlayVertex> tris, Vector3 c, float r, Vector4 col)
    {
        Vector3 px = c + Vector3.UnitX * r, nx = c - Vector3.UnitX * r;
        Vector3 py = c + Vector3.UnitY * r, ny = c - Vector3.UnitY * r;
        Vector3 pz = c + Vector3.UnitZ * r, nz = c - Vector3.UnitZ * r;
        AddTri(tris, py, px, pz, col); AddTri(tris, py, pz, nx, col);
        AddTri(tris, py, nx, nz, col); AddTri(tris, py, nz, px, col);
        AddTri(tris, ny, pz, px, col); AddTri(tris, ny, nx, pz, col);
        AddTri(tris, ny, nz, nx, col); AddTri(tris, ny, px, nz, col);
    }

    private static void AddCenterCube(List<Viewport3DRenderer.OverlayVertex> tris, Vector3 center, in Matrix4x4 orient, float r, Vector4 col)
    {
        Span<Vector3> p = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            Vector3 local = new((i & 1) == 0 ? -r : r, (i & 2) == 0 ? -r : r, (i & 4) == 0 ? -r : r);
            p[i] = center + Vector3.TransformNormal(local, orient);
        }
        AddQuadTris(tris, p[0], p[1], p[3], p[2], col);
        AddQuadTris(tris, p[4], p[6], p[7], p[5], col);
        AddQuadTris(tris, p[0], p[4], p[5], p[1], col);
        AddQuadTris(tris, p[2], p[3], p[7], p[6], col);
        AddQuadTris(tris, p[0], p[2], p[6], p[4], col);
        AddQuadTris(tris, p[1], p[5], p[7], p[3], col);
    }

    internal static void AddQuadTris(List<Viewport3DRenderer.OverlayVertex> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 col)
    {
        AddTri(tris, a, b, c, col);
        AddTri(tris, a, c, d, col);
    }

    internal static void AddQuadLoop(List<Viewport3DRenderer.OverlayVertex> lines, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 col)
    {
        AddLine(lines, a, b, col);
        AddLine(lines, b, c, col);
        AddLine(lines, c, d, col);
        AddLine(lines, d, a, col);
    }

    internal static void AddLine(List<Viewport3DRenderer.OverlayVertex> lines, Vector3 a, Vector3 b, Vector4 col)
    {
        lines.Add(new() { Position = a, Color = col });
        lines.Add(new() { Position = b, Color = col });
    }

    internal static void AddTri(List<Viewport3DRenderer.OverlayVertex> tris, Vector3 a, Vector3 b, Vector3 c, Vector4 col)
    {
        tris.Add(new() { Position = a, Color = col });
        tris.Add(new() { Position = b, Color = col });
        tris.Add(new() { Position = c, Color = col });
    }
}
