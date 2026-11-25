using System;
using System.Collections.Generic;
using System.Numerics;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal enum AxisCompassTarget
{
    None,
    Background,
    PosX,
    NegX,
    NegY,
    PosY,
    PosZ,
    NegZ,
}

/// <summary>
/// 右上表示のインタラクティブ 3D 軸オリエンテーションギズモ。
/// YMM4 座標系準拠（-Y = 上 [緑], +X = 右 [赤], +Z = 正面 [青]）で 6 軸を表示・ヒットテストする。
/// </summary>
internal static class AxisCompassGizmo
{
    private readonly struct ProjectedAxis
    {
        public readonly AxisCompassTarget Target;
        public readonly Vector2 ScreenPos;
        public readonly float Depth;
        public readonly bool IsPrimary;
        public readonly Vector4 Color;

        public ProjectedAxis(AxisCompassTarget target, Vector2 screenPos, float depth, bool isPrimary, Vector4 color)
        {
            Target = target;
            ScreenPos = screenPos;
            Depth = depth;
            IsPrimary = isPrimary;
            Color = color;
        }
    }

    private static readonly Vector4 ColAxisX = new(0.92f, 0.24f, 0.30f, 1.0f);
    private static readonly Vector4 ColAxisNegY = new(0.32f, 0.80f, 0.28f, 1.0f); // YMM4 上 (-Y)
    private static readonly Vector4 ColAxisZ = new(0.24f, 0.56f, 0.96f, 1.0f);

    private static readonly (AxisCompassTarget Target, Vector3 Dir, bool Primary, Vector4 Col)[] Axes =
    {
        (AxisCompassTarget.PosX,  Vector3.UnitX,  true,  ColAxisX),
        (AxisCompassTarget.NegX, -Vector3.UnitX,  false, ColAxisX),
        (AxisCompassTarget.NegY, -Vector3.UnitY,  true,  ColAxisNegY),
        (AxisCompassTarget.PosY,  Vector3.UnitY,  false, ColAxisNegY),
        (AxisCompassTarget.PosZ,  Vector3.UnitZ,  true,  ColAxisZ),
        (AxisCompassTarget.NegZ, -Vector3.UnitZ,  false, ColAxisZ),
    };

    private static void GetLayout(float width, float height, float dpiScale, out float cx, out float cy, out float outerR, out float armLen, out float s)
    {
        s = MathF.Max(0.5f, dpiScale);
        cx = width - 58f * s;
        cy = 58f * s;
        outerR = 46f * s;
        armLen = 30f * s;
    }

    private static bool IsDrawableSize(float width, float height, float dpiScale)
    {
        float s = MathF.Max(0.5f, dpiScale);
        return width >= 120f * s && height >= 120f * s;
    }

    public static ViewportCameraPreset? GetPreset(AxisCompassTarget target) => target switch
    {
        AxisCompassTarget.PosX => ViewportCameraPreset.Right,
        AxisCompassTarget.NegX => ViewportCameraPreset.Left,
        AxisCompassTarget.NegY => ViewportCameraPreset.Top,
        AxisCompassTarget.PosY => ViewportCameraPreset.Bottom,
        AxisCompassTarget.PosZ => ViewportCameraPreset.Front,
        AxisCompassTarget.NegZ => ViewportCameraPreset.Back,
        _ => null,
    };

    public static string GetTooltipText(AxisCompassTarget target) => target switch
    {
        AxisCompassTarget.NegY => "上 (-Y)  [Num7]",
        AxisCompassTarget.PosY => "下 (+Y)  [Ctrl+Num7]",
        AxisCompassTarget.PosX => "右 (X)  [Num3]",
        AxisCompassTarget.NegX => "左 (-X)  [Ctrl+Num3]",
        AxisCompassTarget.PosZ => "正面 (Z)  [Num1]",
        AxisCompassTarget.NegZ => "背面 (-Z)  [Ctrl+Num1]",
        AxisCompassTarget.Background => "視点コンパス (ドラッグで回転 / 軸クリックで視点切替)",
        _ => string.Empty,
    };

    public static bool TryGetAxisScreenPos(
        AxisCompassTarget target,
        in Matrix4x4 viewMatrix,
        float width,
        float height,
        float dpiScale,
        out Vector2 posPx)
    {
        posPx = default;
        if (!IsDrawableSize(width, height, dpiScale)) return false;

        GetLayout(width, height, dpiScale, out float cx, out float cy, out _, out float armLen, out _);
        for (int i = 0; i < Axes.Length; i++)
        {
            if (Axes[i].Target != target) continue;
            Vector3 v = Vector3.TransformNormal(Axes[i].Dir, viewMatrix);
            posPx = new Vector2(cx + v.X * armLen, cy - v.Y * armLen);
            return true;
        }
        return false;
    }

    public static AxisCompassTarget HitTest(
        Vector2 mousePx,
        in Matrix4x4 viewMatrix,
        float width,
        float height,
        float dpiScale = 1f)
    {
        if (!IsDrawableSize(width, height, dpiScale)) return AxisCompassTarget.None;

        GetLayout(width, height, dpiScale, out float cx, out float cy, out float outerR, out float armLen, out float s);
        var center = new Vector2(cx, cy);
        float distFromCenter = Vector2.Distance(mousePx, center);
        if (distFromCenter > outerR + 6f * s) return AxisCompassTarget.None;

        Span<ProjectedAxis> projected = stackalloc ProjectedAxis[Axes.Length];
        for (int i = 0; i < Axes.Length; i++)
        {
            var ax = Axes[i];
            Vector3 v = Vector3.TransformNormal(ax.Dir, viewMatrix);
            projected[i] = new ProjectedAxis(
                ax.Target,
                new Vector2(cx + v.X * armLen, cy - v.Y * armLen),
                v.Z,
                ax.Primary,
                ax.Col);
        }

        // Front-to-back order (smaller v.Z is closer to camera)
        SortProjected(projected, ascending: true);

        for (int i = 0; i < projected.Length; i++)
        {
            float hitR = (projected[i].IsPrimary ? 13f : 11f) * s;
            if (Vector2.DistanceSquared(mousePx, projected[i].ScreenPos) <= hitR * hitR)
            {
                return projected[i].Target;
            }
        }

        return distFromCenter <= outerR ? AxisCompassTarget.Background : AxisCompassTarget.None;
    }

    public static void BuildNdcGeometry(
        List<Viewport3DRenderer.OverlayVertex> ndcTris,
        in Matrix4x4 viewMatrix,
        int width,
        int height,
        float dpiScale,
        AxisCompassTarget hovered)
    {
        if (!IsDrawableSize(width, height, dpiScale)) return;

        GetLayout(width, height, dpiScale, out float cx, out float cy, out float outerR, out float armLen, out float s);
        var center = new Vector2(cx, cy);

        // 1. Dark circular backdrop (and subtle highlight rim on hover)
        float bgAlpha = hovered != AxisCompassTarget.None ? 0.58f : 0.28f;
        AddCirclePx(ndcTris, center, outerR, 36, new Vector4(0.11f, 0.13f, 0.16f, bgAlpha), width, height);
        if (hovered != AxisCompassTarget.None)
        {
            AddRingPx(ndcTris, center, outerR - 1.2f * s, outerR, 36, new Vector4(0.65f, 0.72f, 0.85f, 0.45f), width, height);
        }

        Span<ProjectedAxis> projected = stackalloc ProjectedAxis[Axes.Length];
        for (int i = 0; i < Axes.Length; i++)
        {
            var ax = Axes[i];
            Vector3 v = Vector3.TransformNormal(ax.Dir, viewMatrix);
            projected[i] = new ProjectedAxis(
                ax.Target,
                new Vector2(cx + v.X * armLen, cy - v.Y * armLen),
                v.Z,
                ax.Primary,
                ax.Col);
        }

        // 2. Back-to-front painter's order (largest v.Z drawn first)
        SortProjected(projected, ascending: false);

        // Center pivot cap
        AddCirclePx(ndcTris, center, 2.5f * s, 12, new Vector4(0.85f, 0.88f, 0.92f, 0.75f), width, height);

        for (int i = 0; i < projected.Length; i++)
        {
            var ax = projected[i];
            bool isHov = hovered == ax.Target;

            // Fade slightly when pointing away from camera (v.Z > 0)
            float depthFade = Math.Clamp(1.0f - MathF.Max(0f, ax.Depth) * 0.45f, 0.48f, 1.0f);

            // Draw stick from center to badge
            float stickAlpha = ax.IsPrimary ? (0.92f * depthFade) : (0.55f * depthFade);
            var stickCol = new Vector4(ax.Color.X, ax.Color.Y, ax.Color.Z, isHov ? 1.0f : stickAlpha);
            float badgeR = (ax.IsPrimary ? (isHov ? 12.0f : 10.5f) : (isHov ? 10.5f : 8.0f)) * s;

            Vector2 toEnd = ax.ScreenPos - center;
            float len = toEnd.Length();
            if (len > badgeR + 1f * s)
            {
                Vector2 dir = toEnd / len;
                Vector2 rodEnd = center + dir * MathF.Max(0f, len - badgeR * 0.82f);
                AddLineQuadPx(ndcTris, center, rodEnd, (isHov ? 2.4f : 2.0f) * s, stickCol, width, height);
            }

            // Draw disc badge
            if (ax.IsPrimary)
            {
                AddCirclePx(ndcTris, ax.ScreenPos, badgeR + 1.2f * s, 28, new Vector4(0.06f, 0.07f, 0.09f, 0.85f * depthFade), width, height);
                var fillCol = new Vector4(
                    MathF.Min(1f, ax.Color.X * (isHov ? 1.15f : 1.0f)),
                    MathF.Min(1f, ax.Color.Y * (isHov ? 1.15f : 1.0f)),
                    MathF.Min(1f, ax.Color.Z * (isHov ? 1.15f : 1.0f)),
                    isHov ? 1.0f : 0.96f * depthFade);
                AddCirclePx(ndcTris, ax.ScreenPos, badgeR, 28, fillCol, width, height);
                if (isHov)
                {
                    AddRingPx(ndcTris, ax.ScreenPos, badgeR - 0.5f * s, badgeR + 1.4f * s, 28, new Vector4(1f, 1f, 1f, 0.98f), width, height);
                }

                var glyphCol = new Vector4(1f, 1f, 1f, isHov ? 1.0f : 0.95f * depthFade + 0.05f);
                DrawAxisGlyphPx(ndcTris, ax.Target, ax.ScreenPos, badgeR * 0.50f, 1.85f * s, glyphCol, width, height);
            }
            else
            {
                float fillAlpha = isHov ? 0.92f : 0.52f * depthFade;
                var innerCol = isHov
                    ? new Vector4(ax.Color.X * 0.85f, ax.Color.Y * 0.85f, ax.Color.Z * 0.85f, 0.95f)
                    : new Vector4(0.12f, 0.14f, 0.18f, fillAlpha);
                AddCirclePx(ndcTris, ax.ScreenPos, badgeR, 28, innerCol, width, height);

                var ringCol = new Vector4(ax.Color.X, ax.Color.Y, ax.Color.Z, isHov ? 1.0f : 0.85f * depthFade);
                AddRingPx(ndcTris, ax.ScreenPos, badgeR - 1.7f * s, badgeR, 28, ringCol, width, height);

                if (isHov)
                {
                    AddRingPx(ndcTris, ax.ScreenPos, badgeR, badgeR + 1.3f * s, 28, new Vector4(1f, 1f, 1f, 0.95f), width, height);
                    DrawAxisGlyphPx(ndcTris, ax.Target, ax.ScreenPos, badgeR * 0.44f, 1.55f * s, Vector4.One, width, height);
                }
                else if (ax.Depth < -0.15f)
                {
                    DrawAxisGlyphPx(ndcTris, ax.Target, ax.ScreenPos, badgeR * 0.42f, 1.35f * s, new Vector4(0.92f, 0.94f, 0.98f, 0.82f), width, height);
                }
            }
        }
    }

    private static void SortProjected(Span<ProjectedAxis> items, bool ascending)
    {
        for (int i = 1; i < items.Length; i++)
        {
            var cur = items[i];
            int j = i - 1;
            while (j >= 0 && (ascending ? items[j].Depth > cur.Depth : items[j].Depth < cur.Depth))
            {
                items[j + 1] = items[j];
                j--;
            }
            items[j + 1] = cur;
        }
    }

    private static void DrawAxisGlyphPx(
        List<Viewport3DRenderer.OverlayVertex> tris,
        AxisCompassTarget target,
        Vector2 c,
        float r,
        float strokeW,
        Vector4 col,
        int w,
        int h)
    {
        switch (target)
        {
            case AxisCompassTarget.PosX:
                DrawGlyphX(tris, c, r, strokeW, col, w, h);
                break;

            case AxisCompassTarget.PosZ:
                DrawGlyphZ(tris, c, r, strokeW, col, w, h);
                break;

            case AxisCompassTarget.NegY:
                // "-Y" (minus on left, Y on right)
                DrawGlyphSign(tris, new Vector2(c.X - r * 0.68f, c.Y), r * 0.36f, strokeW * 0.9f, isPlus: false, col, w, h);
                DrawGlyphY(tris, new Vector2(c.X + r * 0.42f, c.Y), r * 0.78f, strokeW * 0.92f, col, w, h);
                break;

            case AxisCompassTarget.NegX:
                DrawGlyphSign(tris, new Vector2(c.X - r * 0.68f, c.Y), r * 0.36f, strokeW * 0.9f, isPlus: false, col, w, h);
                DrawGlyphX(tris, new Vector2(c.X + r * 0.42f, c.Y), r * 0.72f, strokeW * 0.92f, col, w, h);
                break;

            case AxisCompassTarget.PosY:
                DrawGlyphSign(tris, new Vector2(c.X - r * 0.68f, c.Y), r * 0.38f, strokeW * 0.9f, isPlus: true, col, w, h);
                DrawGlyphY(tris, new Vector2(c.X + r * 0.44f, c.Y), r * 0.76f, strokeW * 0.92f, col, w, h);
                break;

            case AxisCompassTarget.NegZ:
                DrawGlyphSign(tris, new Vector2(c.X - r * 0.68f, c.Y), r * 0.36f, strokeW * 0.9f, isPlus: false, col, w, h);
                DrawGlyphZ(tris, new Vector2(c.X + r * 0.42f, c.Y), r * 0.72f, strokeW * 0.92f, col, w, h);
                break;
        }
    }

    private static void DrawGlyphSign(
        List<Viewport3DRenderer.OverlayVertex> tris,
        Vector2 c,
        float halfLen,
        float strokeW,
        bool isPlus,
        Vector4 col,
        int w,
        int h)
    {
        AddLineQuadPx(tris, new Vector2(c.X - halfLen, c.Y), new Vector2(c.X + halfLen, c.Y), strokeW, col, w, h);
        if (isPlus)
            AddLineQuadPx(tris, new Vector2(c.X, c.Y - halfLen), new Vector2(c.X, c.Y + halfLen), strokeW, col, w, h);
    }

    private static void DrawGlyphX(List<Viewport3DRenderer.OverlayVertex> tris, Vector2 c, float r, float strokeW, Vector4 col, int w, int h)
    {
        float rx = r * 0.78f;
        float ry = r * 0.95f;
        AddLineQuadPx(tris, new Vector2(c.X - rx, c.Y - ry), new Vector2(c.X + rx, c.Y + ry), strokeW, col, w, h);
        AddLineQuadPx(tris, new Vector2(c.X + rx, c.Y - ry), new Vector2(c.X - rx, c.Y + ry), strokeW, col, w, h);
    }

    private static void DrawGlyphY(List<Viewport3DRenderer.OverlayVertex> tris, Vector2 c, float r, float strokeW, Vector4 col, int w, int h)
    {
        float rx = r * 0.76f;
        float ry = r * 0.96f;
        Vector2 fork = new(c.X, c.Y + r * 0.08f);
        AddLineQuadPx(tris, new Vector2(c.X - rx, c.Y - ry), fork, strokeW, col, w, h);
        AddLineQuadPx(tris, new Vector2(c.X + rx, c.Y - ry), fork, strokeW, col, w, h);
        AddLineQuadPx(tris, fork, new Vector2(c.X, c.Y + ry), strokeW, col, w, h);
    }

    private static void DrawGlyphZ(List<Viewport3DRenderer.OverlayVertex> tris, Vector2 c, float r, float strokeW, Vector4 col, int w, int h)
    {
        float rx = r * 0.78f;
        float ry = r * 0.90f;
        Vector2 tl = new(c.X - rx, c.Y - ry);
        Vector2 tr = new(c.X + rx, c.Y - ry);
        Vector2 bl = new(c.X - rx, c.Y + ry);
        Vector2 br = new(c.X + rx, c.Y + ry);
        AddLineQuadPx(tris, tl, tr, strokeW, col, w, h);
        AddLineQuadPx(tris, tr, bl, strokeW, col, w, h);
        AddLineQuadPx(tris, bl, br, strokeW, col, w, h);
    }

    private static void AddCirclePx(
        List<Viewport3DRenderer.OverlayVertex> tris,
        Vector2 center,
        float radius,
        int segs,
        Vector4 col,
        int w,
        int h)
    {
        Vector3 cNdc = ToNdc(center, w, h);
        for (int i = 0; i < segs; i++)
        {
            float a0 = MathF.Tau * i / segs;
            float a1 = MathF.Tau * (i + 1) / segs;
            Vector3 p0 = ToNdc(new Vector2(center.X + MathF.Cos(a0) * radius, center.Y + MathF.Sin(a0) * radius), w, h);
            Vector3 p1 = ToNdc(new Vector2(center.X + MathF.Cos(a1) * radius, center.Y + MathF.Sin(a1) * radius), w, h);
            tris.Add(new() { Position = cNdc, Color = col });
            tris.Add(new() { Position = p0, Color = col });
            tris.Add(new() { Position = p1, Color = col });
        }
    }

    private static void AddRingPx(
        List<Viewport3DRenderer.OverlayVertex> tris,
        Vector2 center,
        float rInner,
        float rOuter,
        int segs,
        Vector4 col,
        int w,
        int h)
    {
        rInner = MathF.Max(0f, rInner);
        for (int i = 0; i < segs; i++)
        {
            float a0 = MathF.Tau * i / segs;
            float a1 = MathF.Tau * (i + 1) / segs;
            float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0);
            float c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);

            Vector3 i0 = ToNdc(new Vector2(center.X + c0 * rInner, center.Y + s0 * rInner), w, h);
            Vector3 o0 = ToNdc(new Vector2(center.X + c0 * rOuter, center.Y + s0 * rOuter), w, h);
            Vector3 o1 = ToNdc(new Vector2(center.X + c1 * rOuter, center.Y + s1 * rOuter), w, h);
            Vector3 i1 = ToNdc(new Vector2(center.X + c1 * rInner, center.Y + s1 * rInner), w, h);

            tris.Add(new() { Position = i0, Color = col });
            tris.Add(new() { Position = o0, Color = col });
            tris.Add(new() { Position = o1, Color = col });
            tris.Add(new() { Position = i0, Color = col });
            tris.Add(new() { Position = o1, Color = col });
            tris.Add(new() { Position = i1, Color = col });
        }
    }

    private static void AddLineQuadPx(
        List<Viewport3DRenderer.OverlayVertex> tris,
        Vector2 a,
        Vector2 b,
        float thickness,
        Vector4 col,
        int w,
        int h)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;

        Vector2 dir = d / len;
        Vector2 n = new(-dir.Y, dir.X);
        float halfT = thickness * 0.5f;
        // Extend ends slightly by halfT*0.35 so sharp stroke corners join smoothly
        Vector2 aExt = a - dir * (halfT * 0.35f);
        Vector2 bExt = b + dir * (halfT * 0.35f);

        Vector3 p0 = ToNdc(aExt + n * halfT, w, h);
        Vector3 p1 = ToNdc(aExt - n * halfT, w, h);
        Vector3 p2 = ToNdc(bExt - n * halfT, w, h);
        Vector3 p3 = ToNdc(bExt + n * halfT, w, h);

        tris.Add(new() { Position = p0, Color = col });
        tris.Add(new() { Position = p1, Color = col });
        tris.Add(new() { Position = p2, Color = col });
        tris.Add(new() { Position = p0, Color = col });
        tris.Add(new() { Position = p2, Color = col });
        tris.Add(new() { Position = p3, Color = col });
    }

    private static Vector3 ToNdc(Vector2 px, int w, int h)
        => new((px.X / w) * 2f - 1f, 1f - (px.Y / h) * 2f, 0.1f);
}
