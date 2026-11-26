using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Input;
using Iyahon_D3D11Renderer_Core.Model3D;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.UndoRedo;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal enum BezierHandleKind
{
    None,
    Anchor,
    HandleIn,
    HandleOut,
}

internal readonly record struct BezierHandleRef(BezierHandleKind Kind, int PointIndex)
{
    public static BezierHandleRef None => new(BezierHandleKind.None, -1);
}

/// <summary>
/// D3D11 3Dビューポート上 (オブジェクトモード) での「3Dベジェ曲線」制御点・ハンドル編集コントローラ。
/// </summary>
internal sealed class ViewportBezierCurveController
{
    private const float PickRadiusPx = 14f;

    private static readonly Vector4 ColSpline = new(0.95f, 0.72f, 0.25f, 0.90f);
    private static readonly Vector4 ColAnchor = new(0.28f, 0.88f, 0.96f, 0.95f);
    private static readonly Vector4 ColAnchorSel = new(1.00f, 0.80f, 0.25f, 1.00f);
    private static readonly Vector4 ColHandleArm = new(0.75f, 0.78f, 0.85f, 0.78f);
    private static readonly Vector4 ColHandlePt = new(0.35f, 0.95f, 0.50f, 0.95f);
    private static readonly Vector4 ColHover = new(1.00f, 1.00f, 1.00f, 1.00f);

    private D3DPrimitiveShapeParameter? _dragParam;
    private Matrix4x4 _dragWorld;
    private Matrix4x4 _dragInvWorld;
    private BezierHandleRef _dragHandle = BezierHandleRef.None;
    private BezierCurve3DData? _dragStartData;
    private Vector3 _dragPlanePoint;
    private Vector3 _dragPlaneNormal;
    private Vector3 _dragStartHit;
    private bool _dragModified;

    public BezierHandleRef HoveredHandle { get; private set; } = BezierHandleRef.None;
    public bool IsDragging => _dragHandle.Kind != BezierHandleKind.None && _dragParam != null;

    public bool TryComputeTargetWorldMatrix(
        D3DPrimitiveShapeParameter param,
        ViewportSceneSnapshot? snapshot,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportManipulator manipulator,
        out Matrix4x4 worldMatrix)
    {
        worldMatrix = Matrix4x4.Identity;
        if (snapshot == null) return false;

        ViewportItemEntry? entry = null;
        for (int i = 0; i < snapshot.Items.Count; i++)
        {
            var e = snapshot.Items[i];
            if (e.Kind == ViewportItemKind.BuiltInModel &&
                selectedItems.Contains(e.Item) &&
                e.Item is ShapeItem si &&
                ReferenceEquals(si.ShapeParameter, param))
            {
                entry = e;
                break;
            }
        }
        if (entry == null || entry.ModelSpec == null) return false;

        var effDesc = manipulator.GetEffectiveDrawDescription(entry);
        var sp = entry.ModelSpec;
        Vector3 lScale = sp.LocalScale;
        Vector3 lRot = sp.LocalRotationDeg;
        if (manipulator.GetLiveOverride(entry.Item) is { } ov)
        {
            lScale = ViewportManipulator.ComputeEffectiveLocalScale(sp, ov);
            lRot = ov.ModelLocalRotDeg ?? sp.LocalRotationDeg;
        }

        var dummy = new ObjModelData
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
            LocalScale = lScale,
            LocalRotationDeg = lRot,
        };
        worldMatrix = DepthSortRenderer.BuildObjModelMatrix(new RenderItem { DrawDescription = effDesc, Srv = null, UseBlenderAxis = entry.UsesBlenderAxis }, dummy);
        return true;
    }

    public void BuildOverlays(
        D3DPrimitiveShapeParameter? param,
        ViewportSceneSnapshot? snapshot,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportManipulator manipulator,
        List<Viewport3DRenderer.OverlayVertex> depthLines,
        List<Viewport3DRenderer.OverlayVertex> topTris,
        ViewportCamera camera)
    {
        if (param == null ||
            param.ShapeKind != D3DPrimitiveShapeKind.BezierCurve ||
            !string.IsNullOrWhiteSpace(param.EmbeddedMeshData))
            return;

        if (!TryComputeTargetWorldMatrix(param, snapshot, selectedItems, manipulator, out var world))
            return;

        BezierCurve3DData.TryDeserialize(param.BezierCurveData, out var data);
        if (data.Points.Count == 0) return;

        bool showHandles = param.BezierInterpolation is BezierCurveInterpolation.CubicBezier or BezierCurveInterpolation.QuadraticBezier;
        int selIdx = ReferenceEquals(ViewportBezierCurveBridge.ActiveParameter, param)
            ? ViewportBezierCurveBridge.SelectedPointIndex
            : 0;

        float baseDist = camera.IsOrthographic ? camera.Distance * camera.OrthoScale : camera.Distance;

        for (int i = 0; i < data.Points.Count; i++)
        {
            var pt = data.Points[i];
            Vector3 anchorW = Vector3.Transform(pt.Position, world);
            float dist = MathF.Max(80f, camera.IsOrthographic ? baseDist : Vector3.Distance(camera.CameraPosition, anchorW));
            float rAnchor = Math.Clamp(dist * 0.015f, 5f, 64f);
            bool hoverA = HoveredHandle == new BezierHandleRef(BezierHandleKind.Anchor, i);
            Vector4 colA = hoverA ? ColHover : (i == selIdx ? ColAnchorSel : ColAnchor);
            ViewportOverlayBuilder.AddCenterDiamond(topTris, anchorW, rAnchor, colA);

            if (i > 0)
            {
                Vector3 prevW = Vector3.Transform(data.Points[i - 1].Position, world);
                ViewportOverlayBuilder.AddLine(depthLines, prevW, anchorW, new Vector4(ColSpline.X, ColSpline.Y, ColSpline.Z, 0.30f));
            }

            if (showHandles)
            {
                float rHandle = rAnchor * 0.72f;
                if (param.BezierClosed || i > 0)
                {
                    Vector3 hInW = Vector3.Transform(pt.Position + pt.HandleIn, world);
                    bool hoverIn = HoveredHandle == new BezierHandleRef(BezierHandleKind.HandleIn, i);
                    ViewportOverlayBuilder.AddLine(depthLines, anchorW, hInW, ColHandleArm);
                    ViewportOverlayBuilder.AddCenterDiamond(topTris, hInW, rHandle, hoverIn ? ColHover : ColHandlePt);
                }
                if (param.BezierClosed || i < data.Points.Count - 1)
                {
                    Vector3 hOutW = Vector3.Transform(pt.Position + pt.HandleOut, world);
                    bool hoverOut = HoveredHandle == new BezierHandleRef(BezierHandleKind.HandleOut, i);
                    ViewportOverlayBuilder.AddLine(depthLines, anchorW, hOutW, ColHandleArm);
                    ViewportOverlayBuilder.AddCenterDiamond(topTris, hOutW, rHandle, hoverOut ? ColHover : ColHandlePt);
                }
            }
        }
    }

    public BezierHandleRef HitTest(
        D3DPrimitiveShapeParameter? param,
        ViewportSceneSnapshot? snapshot,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportManipulator manipulator,
        Vector2 mousePx,
        ViewportCamera camera,
        float vw,
        float vh,
        out Vector3 hitWorldPos,
        out Matrix4x4 worldMatrix)
    {
        hitWorldPos = Vector3.Zero;
        worldMatrix = Matrix4x4.Identity;

        if (param == null ||
            param.ShapeKind != D3DPrimitiveShapeKind.BezierCurve ||
            !string.IsNullOrWhiteSpace(param.EmbeddedMeshData))
            return BezierHandleRef.None;

        if (!TryComputeTargetWorldMatrix(param, snapshot, selectedItems, manipulator, out worldMatrix))
            return BezierHandleRef.None;

        BezierCurve3DData.TryDeserialize(param.BezierCurveData, out var data);
        bool showHandles = param.BezierInterpolation is BezierCurveInterpolation.CubicBezier or BezierCurveInterpolation.QuadraticBezier;

        Matrix4x4 worldMat = worldMatrix;
        Vector3 bestWorldPos = Vector3.Zero;
        float bestDist = PickRadiusPx;
        var best = BezierHandleRef.None;

        void Test(BezierHandleRef hr, Vector3 localPos)
        {
            Vector3 wp = Vector3.Transform(localPos, worldMat);
            var sp = camera.WorldToScreen(wp, vw, vh);
            if (sp is not { } s) return;
            float d = Vector2.Distance(s, mousePx);
            if (d <= bestDist)
            {
                bestDist = d;
                best = hr;
                bestWorldPos = wp;
            }
        }

        if (showHandles)
        {
            for (int i = 0; i < data.Points.Count; i++)
            {
                var pt = data.Points[i];
                if (param.BezierClosed || i < data.Points.Count - 1)
                    Test(new BezierHandleRef(BezierHandleKind.HandleOut, i), pt.Position + pt.HandleOut);
                if (param.BezierClosed || i > 0)
                    Test(new BezierHandleRef(BezierHandleKind.HandleIn, i), pt.Position + pt.HandleIn);
            }
        }

        for (int i = 0; i < data.Points.Count; i++)
            Test(new BezierHandleRef(BezierHandleKind.Anchor, i), data.Points[i].Position);

        hitWorldPos = bestWorldPos;
        return best;
    }

    public bool UpdateHover(
        D3DPrimitiveShapeParameter? param,
        ViewportSceneSnapshot? snapshot,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportManipulator manipulator,
        Vector2 mousePx,
        ViewportCamera camera,
        float vw,
        float vh)
    {
        if (IsDragging) return false;
        var next = HitTest(param, snapshot, selectedItems, manipulator, mousePx, camera, vw, vh, out _, out _);
        if (next != HoveredHandle)
        {
            HoveredHandle = next;
            return true;
        }
        return false;
    }

    public bool TryBeginDrag(
        D3DPrimitiveShapeParameter? param,
        ViewportSceneSnapshot? snapshot,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportManipulator manipulator,
        Vector2 mousePx,
        ViewportCamera camera,
        float vw,
        float vh)
    {
        var hit = HitTest(param, snapshot, selectedItems, manipulator, mousePx, camera, vw, vh, out var hitWorld, out var world);
        if (hit.Kind == BezierHandleKind.None || param == null)
            return false;

        if (!Matrix4x4.Invert(world, out var invWorld))
            return false;

        camera.GetCameraAxes(out _, out _, out var camFwd);
        var ray = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);
        var startHit = ray.IntersectPlaneUnbounded(hitWorld, camFwd) ?? hitWorld;

        BezierCurve3DData.TryDeserialize(param.BezierCurveData, out var data);

        _dragParam = param;
        _dragWorld = world;
        _dragInvWorld = invWorld;
        _dragHandle = hit;
        _dragStartData = data.Clone();
        _dragPlanePoint = hitWorld;
        _dragPlaneNormal = camFwd;
        _dragStartHit = startHit;
        _dragModified = false;
        HoveredHandle = hit;

        ViewportBezierCurveBridge.NotifyPointPickedFromViewport(param, hit.PointIndex);
        return true;
    }

    public bool UpdateDrag(Vector2 mousePx, ViewportCamera camera, float vw, float vh, ModifierKeys mods)
    {
        if (!IsDragging || _dragParam == null || _dragStartData == null)
            return false;

        var ray = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);
        var curHit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
        if (!curHit.HasValue)
            return false;

        Vector3 deltaWorld = curHit.Value - _dragStartHit;
        if ((mods & ModifierKeys.Shift) != 0 && _dragHandle.Kind == BezierHandleKind.Anchor)
        {
            float ax = MathF.Abs(deltaWorld.X), ay = MathF.Abs(deltaWorld.Y), az = MathF.Abs(deltaWorld.Z);
            if (ax >= ay && ax >= az) deltaWorld = new Vector3(deltaWorld.X, 0f, 0f);
            else if (ay >= ax && ay >= az) deltaWorld = new Vector3(0f, deltaWorld.Y, 0f);
            else deltaWorld = new Vector3(0f, 0f, deltaWorld.Z);
        }

        Vector3 localDelta = Vector3.TransformNormal(deltaWorld, _dragInvWorld);
        var next = _dragStartData.Clone();
        int idx = _dragHandle.PointIndex;
        if (idx < 0 || idx >= next.Points.Count) return false;
        var pt = next.Points[idx];

        if (_dragHandle.Kind == BezierHandleKind.Anchor)
        {
            pt.Position += localDelta;
        }
        else if (_dragHandle.Kind == BezierHandleKind.HandleIn)
        {
            pt.HandleIn += localDelta;
            if (pt.AutoSmooth && (mods & ModifierKeys.Shift) == 0 && pt.HandleIn.LengthSquared() > 1e-6f)
            {
                float outLen = pt.HandleOut.Length();
                pt.HandleOut = -Vector3.Normalize(pt.HandleIn) * MathF.Max(1f, outLen);
            }
        }
        else if (_dragHandle.Kind == BezierHandleKind.HandleOut)
        {
            pt.HandleOut += localDelta;
            if (pt.AutoSmooth && (mods & ModifierKeys.Shift) == 0 && pt.HandleOut.LengthSquared() > 1e-6f)
            {
                float inLen = pt.HandleIn.Length();
                pt.HandleIn = -Vector3.Normalize(pt.HandleOut) * MathF.Max(1f, inLen);
            }
        }

        _dragParam.BezierCurveData = next.Serialize();
        _dragModified = true;
        ViewportBezierCurveBridge.NotifyCurveEditedFromViewport(_dragParam);
        return true;
    }

    public void EndDrag(UndoRedoManager? undoRedo)
    {
        if (!IsDragging) return;
        if (_dragModified)
            undoRedo?.Record();
        _dragParam = null;
        _dragStartData = null;
        _dragHandle = BezierHandleRef.None;
        _dragModified = false;
    }

    public void CancelDrag()
    {
        if (!IsDragging || _dragParam == null || _dragStartData == null)
        {
            _dragParam = null;
            _dragStartData = null;
            _dragHandle = BezierHandleRef.None;
            return;
        }

        _dragParam.BezierCurveData = _dragStartData.Serialize();
        ViewportBezierCurveBridge.NotifyCurveEditedFromViewport(_dragParam);
        _dragParam = null;
        _dragStartData = null;
        _dragHandle = BezierHandleRef.None;
        _dragModified = false;
    }
}
