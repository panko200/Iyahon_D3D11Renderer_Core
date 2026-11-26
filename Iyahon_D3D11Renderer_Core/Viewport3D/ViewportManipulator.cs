using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Effects;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Effects;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.UndoRedo;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal sealed class LiveTransformOverride
{
    public Vector3? AnimPos { get; set; }
    public float? AnimZoom { get; set; }
    public float? AnimRotZ { get; set; }
    public Vector3? RotateEffectDeg { get; set; }

    public Vector3? ModelLocalRotDeg { get; set; }
    public Vector3? ModelLocalScale { get; set; }
    public float? ModelMasterScale { get; set; }

    public Vector3? LightDirection { get; set; }
    public float? LightRange { get; set; }
    public Vector2? LightAreaSize { get; set; }
}

internal sealed class ViewportManipulator
{
    private readonly Dictionary<IVideoItem, LiveTransformOverride> _liveOverrides = new();
    private readonly List<DragTargetState> _targets = new();
    private readonly List<(Animation Anim, double[] Values)> _savedAnims = new();
    private readonly List<(IVideoItem Item, IVideoEffect AddedEffect)> _addedEffects = new();

    private GizmoMode _mode = GizmoMode.Translate;
    private GizmoSpace _space = GizmoSpace.World;
    private bool _editKeyFrameOnly;
    private GizmoHandle _hoveredHandle = GizmoHandle.None;
    private GizmoHandle _activeHandle = GizmoHandle.None;

    private TransformGizmoState _dragGizmo;
    private Vector3 _dragPlanePoint;
    private Vector3 _dragPlaneNormal;
    private Vector3 _dragHitStart;
    private Vector3 _dragAxis;
    private float _dragAxisStartParam;
    private Vector3 _dragTurnRight;
    private Vector3 _dragTurnUp;
    private float _dragStartAngle;
    private float _dragScaleStartDist;

    public GizmoMode Mode
    {
        get => _mode;
        set { if (!IsDragging) _mode = value; }
    }

    public GizmoSpace Space
    {
        get => _space;
        set { if (!IsDragging) _space = value; }
    }

    public bool EditKeyFrameOnly
    {
        get => _editKeyFrameOnly;
        set { if (!IsDragging) _editKeyFrameOnly = value; }
    }

    public GizmoHandle HoveredHandle
    {
        get => _hoveredHandle;
        set => _hoveredHandle = value;
    }

    public GizmoHandle ActiveHandle => _activeHandle;
    public bool IsDragging => _targets.Count > 0 && _activeHandle != GizmoHandle.None;
    public float ActiveDragAngleRad { get; private set; }
    public bool UseBlenderAxis { get; set; }

    private sealed class DragTargetState
    {
        public required IVideoItem Item;
        public required ViewportItemEntry SnapshotEntry;
        public Model3D.ID3DModelTransformParameter? ModelParam;
        public Lighting.D3DLightShapeParameter? LightParam;
        public RotateEffect? Rotate3DEffect;

        public Vector3 StartAnimPos;
        public float StartAnimZoom;
        public float StartAnimRotZ;
        public Vector3 StartRotateEffectDeg;

        public Vector3 StartModelLocalRotDeg;
        public Vector3 StartModelLocalScale;
        public float StartModelMasterScale;
        public Matrix4x4 StartItemRotMatrix;
        public Matrix4x4 StartModelLocalRotMatrix;

        public Vector3 StartLightDir;
        public float StartLightRange;
        public Vector2 StartLightArea;
    }

    public LiveTransformOverride? GetLiveOverride(IVideoItem item)
        => _liveOverrides.TryGetValue(item, out var ov) ? ov : null;

    public DrawDescription GetEffectiveDrawDescription(ViewportItemEntry entry)
    {
        if (!_liveOverrides.TryGetValue(entry.Item, out var ov))
            return entry.DrawDescription;

        DragTargetState? active = null;
        if (!entry.HasCapturedAnimBaseline)
        {
            for (int i = 0; i < _targets.Count; i++)
            {
                if (_targets[i].Item == entry.Item)
                {
                    active = _targets[i];
                    break;
                }
            }
        }

        var dd = entry.DrawDescription;
        Vector3 draw = dd.Draw;
        Vector2 zoom = dd.Zoom;
        Vector3 rot = dd.Rotation;

        if (ov.AnimPos is { } targetPos)
        {
            Vector3 captured = entry.HasCapturedAnimBaseline
                ? entry.CapturedAnimPos
                : (active?.StartAnimPos ?? GetCapturedAnimPos(entry));
            Vector3 animDelta = targetPos - captured;
            draw += entry.UsesBlenderAxis ? BlenderAxisMath.BlenderToWorld(animDelta) : animDelta;
        }

        if (ov.AnimZoom is { } targetZoom)
        {
            float capturedZoom = entry.HasCapturedAnimBaseline
                ? entry.CapturedAnimZoom
                : (active?.StartAnimZoom ?? (float)entry.Item.Zoom.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps));
            float ratio = MathF.Abs(capturedZoom) > 1e-4f ? (targetZoom / capturedZoom) : 1f;
            zoom *= ratio;
        }

        if (ov.AnimRotZ is { } targetRz)
        {
            float capturedRz = entry.HasCapturedAnimBaseline
                ? entry.CapturedAnimRotZ
                : (active?.StartAnimRotZ ?? (float)entry.Item.Rotation.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps));
            rot.Z += (targetRz - capturedRz);
        }

        if (ov.RotateEffectDeg is { } targetEffRot)
        {
            if (entry.HasCapturedAnimBaseline)
            {
                Vector3 capturedEff = entry.HasCapturedRotateEffect ? entry.CapturedRotateEffectDeg : Vector3.Zero;
                rot += (targetEffRot - capturedEff);
            }
            else if (active != null)
            {
                rot += (targetEffRot - active.StartRotateEffectDeg);
            }
            else
            {
                var eff = Find3DRotateEffect(entry.Item);
                if (eff != null)
                {
                    Vector3 capturedEff = new(
                        (float)eff.X.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps),
                        (float)eff.Y.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps),
                        (float)eff.Z.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps));
                    rot += (targetEffRot - capturedEff);
                }
                else
                {
                    rot += targetEffRot;
                }
            }
        }

        return dd with { Draw = draw, Zoom = zoom, Rotation = rot };
    }

    internal static Vector3 ComputeEffectiveLocalScale(ViewportModelSpec spec, LiveTransformOverride? ov)
    {
        if (ov == null) return spec.LocalScale;
        if (ov.ModelLocalScale is { } ls && ov.ModelMasterScale is { } ms)
            return (ls / 100f) * (ms / 100f);
        if (ov.ModelLocalScale is { } lsOnly)
            return lsOnly / 100f;
        return spec.LocalScale;
    }

    public ObjModelData? CreateEffectiveModelData(ViewportItemEntry entry, Model3D.CachedGpuModel cached)
    {
        if (entry.ModelSpec == null) return null;
        var spec = entry.ModelSpec;

        Vector3 localScale = spec.LocalScale;
        Vector3 localRot = spec.LocalRotationDeg;
        bool normalize = spec.NormalizeSize;
        bool ground = spec.GroundToOrigin;
        float unitScale = spec.UnitScale;

        var mParam = GetModelParam(entry.Item);
        if (_liveOverrides.TryGetValue(entry.Item, out var ov))
        {
            if (ov.ModelLocalRotDeg is { } lr) localRot = lr;
            localScale = ComputeEffectiveLocalScale(spec, ov);
        }
        else if (mParam != null)
        {
            // Keep animation-evaluated snapshot values unless dragging.
        }

        var renderData = cached.CreateRenderData(
            normalize,
            ground,
            unitScale,
            localScale,
            localRot,
            spec.TintColor,
            spec.OverrideMaterial,
            spec.OverrideMetallic,
            spec.OverrideRoughness,
            spec.EmissiveMultiplier,
            spec.CullBackface);

        if (mParam is Model3D.D3DPrimitiveShapeParameter primParam)
        {
            renderData.Noise3D = primParam.ShapeKind == Model3D.D3DPrimitiveShapeKind.Noise3D
                ? primParam.BuildNoise3DAt(entry.ItemFrame, entry.ItemLength, entry.Fps, cached.BoundsMin, cached.BoundsMax)
                : default;
        }
        else if (spec.Noise3D.Enabled)
        {
            var np = spec.Noise3D;
            np.BoundsMin = cached.BoundsMin;
            np.BoundsMax = cached.BoundsMax;
            renderData.Noise3D = np;
        }

        return renderData;
    }

    public Lighting.LightData GetEffectiveLight(ViewportItemEntry entry, in DrawDescription effectiveDesc)
    {
        var src = entry.Light ?? new Lighting.LightData();
        Vector3 dir = src.Direction;
        float range = src.Range;
        float areaW = src.AreaWidth;
        float areaH = src.AreaHeight;

        if (_liveOverrides.TryGetValue(entry.Item, out var ov))
        {
            if (ov.LightDirection is { } ld) dir = ld;
            if (ov.LightRange is { } lr) range = lr;
            if (ov.LightAreaSize is { } la) { areaW = la.X; areaH = la.Y; }
        }

        return new Lighting.LightData
        {
            Type = src.Type,
            Position = effectiveDesc.Draw,
            Direction = dir,
            AreaRight = src.AreaRight,
            HasExplicitAreaRight = src.HasExplicitAreaRight,
            Color = src.Color,
            Intensity = src.Intensity,
            Range = range,
            SpotInnerAngle = src.SpotInnerAngle,
            SpotOuterAngle = src.SpotOuterAngle,
            AreaWidth = areaW,
            AreaHeight = areaH,
            CastShadow = src.CastShadow,
            ShadowIntensity = src.ShadowIntensity,
            ShadowBias = src.ShadowBias,
            ShadowNormalBias = src.ShadowNormalBias,
            ShadowSoftness = src.ShadowSoftness,
            ShowGizmo = src.ShowGizmo,
            Layer = src.Layer,
        };
    }

    public bool TryComputeGizmo(
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportSceneSnapshot? snapshot,
        ViewportCamera camera,
        out TransformGizmoState gizmo)
    {
        gizmo = default;
        if (snapshot == null || selectedItems.Count == 0)
            return false;

        var entries = new List<ViewportItemEntry>();
        for (int i = 0; i < snapshot.Items.Count; i++)
        {
            if (selectedItems.Contains(snapshot.Items[i].Item))
                entries.Add(snapshot.Items[i]);
        }
        if (entries.Count == 0) return false;

        Vector3 sumOrigin = Vector3.Zero;
        for (int i = 0; i < entries.Count; i++)
        {
            sumOrigin += GetItemWorldPivot(entries[i]);
        }
        Vector3 origin = sumOrigin / entries.Count;

        Matrix4x4 orientation = UseBlenderAxis ? BlenderAxisMath.BlenderWorldBasis : Matrix4x4.Identity;
        if (_space == GizmoSpace.Local && entries.Count == 1)
        {
            orientation = GetItemWorldOrientation(entries[0]);
        }

        gizmo = TransformGizmoState.Create(origin, orientation, camera);
        return true;
    }

    public ViewportItemEntry? PickItem(
        Vector2 mousePx,
        ViewportSceneSnapshot snapshot,
        ViewportCamera camera,
        float vw,
        float vh,
        bool includeLights = true)
    {
        var ray = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);
        float bestDist = float.MaxValue;
        ViewportItemEntry? best = null;

        for (int i = 0; i < snapshot.Items.Count; i++)
        {
            var entry = snapshot.Items[i];
            var desc = GetEffectiveDrawDescription(entry);

            if (entry.Kind == ViewportItemKind.Light)
            {
                if (!includeLights) continue;
                var sp = camera.WorldToScreen(desc.Draw, vw, vh);
                if (sp.HasValue && Vector2.Distance(mousePx, sp.Value) <= 18f)
                {
                    float d = Vector3.Distance(ray.Origin, desc.Draw);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = entry;
                    }
                }
                continue;
            }

            if (entry.Kind == ViewportItemKind.BuiltInModel && entry.ModelSpec != null)
            {
                var dummyModel = new ObjModelData
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
                if (_liveOverrides.TryGetValue(entry.Item, out var ov))
                {
                    var spec = entry.ModelSpec;
                    dummyModel = new ObjModelData
                    {
                        VertexBuffer = null!,
                        IndexBuffer = null!,
                        Parts = Array.Empty<ObjPartRenderInfo>(),
                        BoundsMin = spec.BoundsMin,
                        BoundsMax = spec.BoundsMax,
                        ModelCenter = spec.ModelCenter,
                        ModelScale = spec.ModelScale,
                        NormalizeSize = spec.NormalizeSize,
                        GroundToOrigin = spec.GroundToOrigin,
                        UnitScale = spec.UnitScale,
                        LocalScale = ComputeEffectiveLocalScale(spec, ov),
                        LocalRotationDeg = ov.ModelLocalRotDeg ?? spec.LocalRotationDeg,
                    };
                }

                var ri = new RenderItem { DrawDescription = desc, Srv = null, UseBlenderAxis = entry.UsesBlenderAxis };
                var world = DepthSortRenderer.BuildObjModelMatrix(ri, dummyModel);
                Vector3 bMin = dummyModel.BoundsMin;
                Vector3 bMax = dummyModel.BoundsMax;
                if (Vector3.DistanceSquared(bMax, bMin) < 1e-6f)
                {
                    bMin = new Vector3(-1f, -1f, -1f);
                    bMax = new Vector3(1f, 1f, 1f);
                }
                if (ray.IntersectObb(world, bMin, bMax, out float dist) && dist < bestDist)
                {
                    bestDist = dist;
                    best = entry;
                }
            }
            else
            {
                float pw = MathF.Max(1f, entry.PixelWidth);
                float ph = MathF.Max(1f, entry.PixelHeight);
                var ri = new RenderItem
                {
                    DrawDescription = desc,
                    Srv = null,
                    PixelWidth = pw,
                    PixelHeight = ph,
                    BoundsCenterX = entry.BoundsCenterX,
                    BoundsCenterY = entry.BoundsCenterY,
                    UseBlenderAxis = entry.UsesBlenderAxis,
                };
                var world = DepthSortRenderer.BuildModelMatrix(ri);
                float halfZ = entry.Kind == ViewportItemKind.D3DEffect ? 0.5f : 0.05f;
                var lMin = new Vector3(-0.5f, -0.5f, -halfZ);
                var lMax = new Vector3(0.5f, 0.5f, halfZ);

                if (ray.IntersectObb(world, lMin, lMax, out float dist) && dist < bestDist)
                {
                    bestDist = dist;
                    best = entry;
                }
            }
        }

        return best;
    }

    public bool TryBeginDrag(
        GizmoHandle handle,
        Vector2 mousePx,
        IReadOnlyList<IVideoItem> selectedItems,
        ViewportSceneSnapshot snapshot,
        ViewportCamera camera,
        float vw,
        float vh,
        in TransformGizmoState gizmo)
    {
        if (handle == GizmoHandle.None || selectedItems.Count == 0)
            return false;

        var ray = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);
        _dragGizmo = gizmo;
        _activeHandle = handle;
        ActiveDragAngleRad = 0f;

        if (handle is GizmoHandle.MoveX or GizmoHandle.MoveY or GizmoHandle.MoveZ)
        {
            _dragAxis = gizmo.GetAxisVector(handle);
            var param = ray.ClosestParameterOnAxis(gizmo.Origin, _dragAxis);
            if (!param.HasValue) { _activeHandle = GizmoHandle.None; return false; }
            _dragAxisStartParam = param.Value;
        }
        else if (handle is GizmoHandle.PlaneXY or GizmoHandle.PlaneYZ or GizmoHandle.PlaneZX)
        {
            _dragPlanePoint = gizmo.Origin;
            _dragPlaneNormal = gizmo.GetPlaneNormal(handle);
            var hit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
            if (!hit.HasValue) { _activeHandle = GizmoHandle.None; return false; }
            _dragHitStart = hit.Value;
        }
        else if (handle == GizmoHandle.MoveFree)
        {
            camera.GetCameraAxes(out _, out _, out var fwd);
            _dragPlanePoint = gizmo.Origin;
            _dragPlaneNormal = -fwd;
            var hit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
            if (!hit.HasValue) { _activeHandle = GizmoHandle.None; return false; }
            _dragHitStart = hit.Value;
        }
        else if (handle is GizmoHandle.RotateX or GizmoHandle.RotateY or GizmoHandle.RotateZ)
        {
            _dragAxis = gizmo.GetAxisVector(handle);
            _dragPlanePoint = gizmo.Origin;
            _dragPlaneNormal = _dragAxis;
            var hit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
            if (!hit.HasValue)
            {
                // Grazing view: fallback to screen plane perpendicular to camera forward
                camera.GetCameraAxes(out _, out _, out var fwd);
                _dragPlaneNormal = -fwd;
                hit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
            }
            if (!hit.HasValue) { _activeHandle = GizmoHandle.None; return false; }

            var (r, u) = BuildPlaneBasis(_dragPlaneNormal);
            _dragTurnRight = r;
            _dragTurnUp = u;
            Vector3 rel = hit.Value - _dragPlanePoint;
            _dragStartAngle = MathF.Atan2(Vector3.Dot(rel, _dragTurnUp), Vector3.Dot(rel, _dragTurnRight));
        }
        else if (handle is GizmoHandle.ScaleX or GizmoHandle.ScaleY or GizmoHandle.ScaleZ or GizmoHandle.ScaleUniform)
        {
            camera.GetCameraAxes(out _, out _, out var fwd);
            _dragPlanePoint = gizmo.Origin;
            _dragPlaneNormal = -fwd;
            var hit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
            if (!hit.HasValue) { _activeHandle = GizmoHandle.None; return false; }
            _dragHitStart = hit.Value;
            _dragAxis = handle == GizmoHandle.ScaleUniform ? Vector3.One : gizmo.GetAxisVector(handle);
            _dragScaleStartDist = MathF.Max(10f, Vector3.Distance(_dragHitStart, _dragPlanePoint));
        }

        PrepareDragTargets(selectedItems, snapshot);
        if (_targets.Count == 0)
        {
            _activeHandle = GizmoHandle.None;
            return false;
        }
        return true;
    }

    public void UpdateDrag(
        Vector2 mousePx,
        ViewportCamera camera,
        float vw,
        float vh,
        System.Windows.Input.ModifierKeys modifiers)
    {
        if (!IsDragging) return;

        bool fine = (modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        bool snap = (modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        var ray = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);

        switch (_activeHandle)
        {
            case GizmoHandle.MoveX:
            case GizmoHandle.MoveY:
            case GizmoHandle.MoveZ:
            {
                var p = ray.ClosestParameterOnAxis(_dragGizmo.Origin, _dragAxis);
                if (!p.HasValue) return;
                float deltaScalar = p.Value - _dragAxisStartParam;
                if (fine) deltaScalar *= 0.2f;
                if (snap) deltaScalar = MathF.Round(deltaScalar / 10f) * 10f;
                ApplyTranslationDelta(_dragAxis * deltaScalar);
                break;
            }

            case GizmoHandle.PlaneXY:
            case GizmoHandle.PlaneYZ:
            case GizmoHandle.PlaneZX:
            case GizmoHandle.MoveFree:
            {
                var hit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
                if (!hit.HasValue) return;
                Vector3 delta = hit.Value - _dragHitStart;
                if (fine) delta *= 0.2f;
                if (snap)
                {
                    delta = new Vector3(
                        MathF.Round(delta.X / 10f) * 10f,
                        MathF.Round(delta.Y / 10f) * 10f,
                        MathF.Round(delta.Z / 10f) * 10f);
                }
                ApplyTranslationDelta(delta);
                break;
            }

            case GizmoHandle.RotateX:
            case GizmoHandle.RotateY:
            case GizmoHandle.RotateZ:
            {
                var hit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
                if (!hit.HasValue) return;
                Vector3 rel = hit.Value - _dragPlanePoint;
                if (rel.LengthSquared() < 1e-4f) return;

                float curAngle = MathF.Atan2(Vector3.Dot(rel, _dragTurnUp), Vector3.Dot(rel, _dragTurnRight));
                float deltaRad = WrapAnglePi(curAngle - _dragStartAngle);
                if (fine) deltaRad *= 0.25f;
                if (snap)
                {
                    float stepRad = 15f * (MathF.PI / 180f);
                    deltaRad = MathF.Round(deltaRad / stepRad) * stepRad;
                }
                ActiveDragAngleRad = deltaRad;
                ApplyRotationDelta(_dragAxis, deltaRad);
                break;
            }

            case GizmoHandle.ScaleX:
            case GizmoHandle.ScaleY:
            case GizmoHandle.ScaleZ:
            case GizmoHandle.ScaleUniform:
            {
                var hit = ray.IntersectPlaneUnbounded(_dragPlanePoint, _dragPlaneNormal);
                if (!hit.HasValue) return;

                float ratio;
                if (_activeHandle == GizmoHandle.ScaleUniform)
                {
                    float curDist = Vector3.Distance(hit.Value, _dragPlanePoint);
                    ratio = curDist / _dragScaleStartDist;
                }
                else
                {
                    float projStart = Vector3.Dot(_dragHitStart - _dragPlanePoint, _dragAxis);
                    float projCur = Vector3.Dot(hit.Value - _dragPlanePoint, _dragAxis);
                    float denom = MathF.Abs(projStart) > 5f ? projStart : (_dragGizmo.Scale * 0.5f);
                    ratio = 1f + (projCur - projStart) / denom;
                }

                if (fine) ratio = 1f + (ratio - 1f) * 0.25f;
                if (snap) ratio = MathF.Round(ratio * 10f) * 0.1f;
                ratio = Math.Clamp(ratio, 0.01f, 50f);
                ApplyScaleRatio(_activeHandle, ratio);
                break;
            }
        }
    }

    public void EndDrag(UndoRedoManager? undoRedoManager)
    {
        if (!IsDragging) return;
        _targets.Clear();
        _savedAnims.Clear();
        _addedEffects.Clear();
        _liveOverrides.Clear();
        _activeHandle = GizmoHandle.None;
        ActiveDragAngleRad = 0f;

        undoRedoManager?.Record();
        DepthSortVideoPlayerPatch.SetTimelineChanged();
    }

    public void CancelDrag()
    {
        if (!IsDragging && _savedAnims.Count == 0) return;

        for (int i = 0; i < _savedAnims.Count; i++)
        {
            var (anim, vals) = _savedAnims[i];
            int cnt = Math.Min(anim.Values.Count, vals.Length);
            for (int k = 0; k < cnt; k++)
            {
                anim.Values[k].Value = vals[k];
            }
        }

        for (int i = 0; i < _addedEffects.Count; i++)
        {
            var (item, eff) = _addedEffects[i];
            if (item.VideoEffects != null && item.VideoEffects.Contains(eff))
            {
                item.VideoEffects = item.VideoEffects.Remove(eff);
            }
        }

        _targets.Clear();
        _savedAnims.Clear();
        _addedEffects.Clear();
        _liveOverrides.Clear();
        _activeHandle = GizmoHandle.None;
        ActiveDragAngleRad = 0f;
        DepthSortVideoPlayerPatch.SetTimelineChanged();
    }

    private void PrepareDragTargets(IReadOnlyList<IVideoItem> selectedItems, ViewportSceneSnapshot snapshot)
    {
        _targets.Clear();
        _savedAnims.Clear();
        _addedEffects.Clear();
        _liveOverrides.Clear();

        for (int i = 0; i < selectedItems.Count; i++)
        {
            var item = selectedItems[i];
            if (item.IsLocked) continue;

            var entry = snapshot.Items.FirstOrDefault(x => x.Item == item);
            if (entry == null) continue;

            long f = entry.ItemFrame;
            long len = entry.ItemLength;
            int fps = entry.Fps;

            var mParam = GetModelParam(item);
            var lParam = GetLightParam(item);
            var rotEff = Find3DRotateEffect(item);

            if ((_activeHandle == GizmoHandle.RotateX || _activeHandle == GizmoHandle.RotateY)
                && mParam == null && lParam == null && rotEff == null)
            {
                rotEff = new RotateEffect { Is3D = true };
                item.VideoEffects = item.VideoEffects.Add(rotEff);
                _addedEffects.Add((item, rotEff));
            }

            Vector3 animPos = new(
                (float)item.X.GetValue(f, len, fps),
                (float)item.Y.GetValue(f, len, fps),
                (float)item.Z.GetValue(f, len, fps));

            Vector3 startEffRot = rotEff != null
                ? new Vector3(
                    (float)rotEff.X.GetValue(f, len, fps),
                    (float)rotEff.Y.GetValue(f, len, fps),
                    (float)rotEff.Z.GetValue(f, len, fps))
                : Vector3.Zero;

            Vector3 startModelRot = mParam != null
                ? new Vector3(
                    (float)mParam.LocalRotX.GetValue(f, len, fps),
                    (float)mParam.LocalRotY.GetValue(f, len, fps),
                    (float)mParam.LocalRotZ.GetValue(f, len, fps))
                : Vector3.Zero;

            Vector3 startModelScale = mParam != null
                ? new Vector3(
                    (float)mParam.ScaleX.GetValue(f, len, fps),
                    (float)mParam.ScaleY.GetValue(f, len, fps),
                    (float)mParam.ScaleZ.GetValue(f, len, fps))
                : new Vector3(100f, 100f, 100f);

            float startMasterScale = mParam != null ? (float)mParam.ModelScale.GetValue(f, len, fps) : 100f;

            Vector3 startLightDirWorld;
            if (lParam != null)
            {
                Vector3 rawDir = new(
                    (float)lParam.DirectionX.GetValue(f, len, fps),
                    (float)lParam.DirectionY.GetValue(f, len, fps),
                    (float)lParam.DirectionZ.GetValue(f, len, fps));
                startLightDirWorld = entry.UsesBlenderAxis ? BlenderAxisMath.BlenderToWorld(rawDir) : rawDir;
                if (startLightDirWorld.LengthSquared() > 1e-6f)
                    startLightDirWorld = Vector3.Normalize(startLightDirWorld);
                else
                    startLightDirWorld = entry.UsesBlenderAxis ? new Vector3(0f, 1f, 0f) : new Vector3(0f, -1f, 0f);
            }
            else
            {
                startLightDirWorld = entry.UsesBlenderAxis ? new Vector3(0f, 1f, 0f) : new Vector3(0f, -1f, 0f);
            }

            float startLightRange = lParam != null ? (float)lParam.Range.GetValue(f, len, fps) : 5000f;
            Vector2 startLightArea = lParam != null
                ? new Vector2((float)lParam.AreaWidth.GetValue(f, len, fps), (float)lParam.AreaHeight.GetValue(f, len, fps))
                : new Vector2(200f, 200f);

            SnapshotAnims(item, mParam, lParam, rotEff);

            _targets.Add(new DragTargetState
            {
                Item = item,
                SnapshotEntry = entry,
                ModelParam = mParam,
                LightParam = lParam,
                Rotate3DEffect = rotEff,
                StartAnimPos = animPos,
                StartAnimZoom = (float)item.Zoom.GetValue(f, len, fps),
                StartAnimRotZ = (float)item.Rotation.GetValue(f, len, fps),
                StartRotateEffectDeg = startEffRot,
                StartModelLocalRotDeg = startModelRot,
                StartModelLocalScale = startModelScale,
                StartModelMasterScale = startMasterScale,
                StartItemRotMatrix = entry.UsesBlenderAxis
                    ? BlenderAxisMath.ComposeItemRotationBlender(entry.DrawDescription.Rotation)
                    : RotationEulerMath.ComposeYmmRotation(entry.DrawDescription.Rotation),
                StartModelLocalRotMatrix = entry.UsesBlenderAxis
                    ? BlenderAxisMath.ComposeModelLocalRotationBlender(startModelRot)
                    : RotationEulerMath.ComposeModelLocalRotation(startModelRot),
                StartLightDir = startLightDirWorld,
                StartLightRange = startLightRange,
                StartLightArea = startLightArea,
            });

            _liveOverrides[item] = new LiveTransformOverride();
        }
    }

    private void SnapshotAnims(
        IVideoItem item,
        Model3D.ID3DModelTransformParameter? mParam,
        Lighting.D3DLightShapeParameter? lParam,
        RotateEffect? rotEff)
    {
        void Save(Animation a) => _savedAnims.Add((a, a.Values.Select(v => v.Value).ToArray()));

        Save(item.X);
        Save(item.Y);
        Save(item.Z);
        Save(item.Zoom);
        Save(item.Rotation);

        if (mParam != null)
        {
            Save(mParam.ModelScale);
            Save(mParam.ScaleX);
            Save(mParam.ScaleY);
            Save(mParam.ScaleZ);
            Save(mParam.LocalRotX);
            Save(mParam.LocalRotY);
            Save(mParam.LocalRotZ);
        }
        if (lParam != null)
        {
            Save(lParam.DirectionX);
            Save(lParam.DirectionY);
            Save(lParam.DirectionZ);
            Save(lParam.Range);
            Save(lParam.AreaWidth);
            Save(lParam.AreaHeight);
        }
        if (rotEff != null)
        {
            Save(rotEff.X);
            Save(rotEff.Y);
            Save(rotEff.Z);
        }
    }

    private void ApplyTranslationDelta(Vector3 worldDelta)
    {
        for (int i = 0; i < _targets.Count; i++)
        {
            var t = _targets[i];
            Vector3 animDelta = t.SnapshotEntry.UsesBlenderAxis
                ? BlenderAxisMath.WorldToBlender(worldDelta)
                : worldDelta;
            Vector3 targetPos = t.StartAnimPos + animDelta;

            SetAnimTarget(t.Item, t.Item.X, t.SnapshotEntry, t.StartAnimPos.X, targetPos.X);
            SetAnimTarget(t.Item, t.Item.Y, t.SnapshotEntry, t.StartAnimPos.Y, targetPos.Y);
            SetAnimTarget(t.Item, t.Item.Z, t.SnapshotEntry, t.StartAnimPos.Z, targetPos.Z);

            _liveOverrides[t.Item].AnimPos = targetPos;
        }
        DepthSortVideoPlayerPatch.SetTimelineChanged();
    }

    private void ApplyRotationDelta(Vector3 worldAxis, float angleRad)
    {
        var worldRotDelta = Matrix4x4.CreateFromAxisAngle(worldAxis, angleRad);

        for (int i = 0; i < _targets.Count; i++)
        {
            var t = _targets[i];
            var ov = _liveOverrides[t.Item];
            bool useBlender = t.SnapshotEntry.UsesBlenderAxis;

            if (t.ModelParam != null)
            {
                var localDelta = RotationEulerMath.WorldDeltaToModelLocalDelta(worldRotDelta, t.StartItemRotMatrix);
                var newLocalMat = RotationEulerMath.Orthonormalize(t.StartModelLocalRotMatrix * localDelta);
                Vector3 deg = useBlender
                    ? BlenderAxisMath.DecomposeModelLocalRotationBlender(newLocalMat)
                    : RotationEulerMath.DecomposeModelLocalRotation(newLocalMat);

                SetAnimTarget(t.Item, t.ModelParam.LocalRotX, t.SnapshotEntry, t.StartModelLocalRotDeg.X, deg.X);
                SetAnimTarget(t.Item, t.ModelParam.LocalRotY, t.SnapshotEntry, t.StartModelLocalRotDeg.Y, deg.Y);
                SetAnimTarget(t.Item, t.ModelParam.LocalRotZ, t.SnapshotEntry, t.StartModelLocalRotDeg.Z, deg.Z);

                ov.ModelLocalRotDeg = deg;
            }
            else if (t.LightParam != null)
            {
                Vector3 newWorldDir = Vector3.TransformNormal(t.StartLightDir, worldRotDelta);
                if (newWorldDir.LengthSquared() > 1e-6f) newWorldDir = Vector3.Normalize(newWorldDir);

                Vector3 startParamDir = useBlender ? BlenderAxisMath.WorldToBlender(t.StartLightDir) : t.StartLightDir;
                Vector3 newParamDir = useBlender ? BlenderAxisMath.WorldToBlender(newWorldDir) : newWorldDir;

                SetAnimTarget(t.Item, t.LightParam.DirectionX, t.SnapshotEntry, startParamDir.X, newParamDir.X);
                SetAnimTarget(t.Item, t.LightParam.DirectionY, t.SnapshotEntry, startParamDir.Y, newParamDir.Y);
                SetAnimTarget(t.Item, t.LightParam.DirectionZ, t.SnapshotEntry, startParamDir.Z, newParamDir.Z);

                ov.LightDirection = newWorldDir;
            }
            else if (t.Rotate3DEffect != null)
            {
                var newMat = RotationEulerMath.Orthonormalize(t.StartItemRotMatrix * worldRotDelta);
                Vector3 decomposed = useBlender
                    ? BlenderAxisMath.DecomposeItemRotationBlender(newMat)
                    : RotationEulerMath.DecomposeYmmRotation(newMat);
                Vector3 deltaWorldDeg = decomposed - t.SnapshotEntry.DrawDescription.Rotation;
                Vector3 targetEff = t.StartRotateEffectDeg + deltaWorldDeg;

                SetAnimTarget(t.Item, t.Rotate3DEffect.X, t.SnapshotEntry, t.StartRotateEffectDeg.X, targetEff.X);
                SetAnimTarget(t.Item, t.Rotate3DEffect.Y, t.SnapshotEntry, t.StartRotateEffectDeg.Y, targetEff.Y);
                SetAnimTarget(t.Item, t.Rotate3DEffect.Z, t.SnapshotEntry, t.StartRotateEffectDeg.Z, targetEff.Z);

                ov.RotateEffectDeg = targetEff;
            }
            else
            {
                float deltaDeg = angleRad * (180f / MathF.PI);
                Vector3 refAxis = useBlender ? Vector3.UnitY : Vector3.UnitZ;
                if (Vector3.Dot(worldAxis, refAxis) < 0f) deltaDeg = -deltaDeg;
                float targetRz = t.StartAnimRotZ + deltaDeg;
                SetAnimTarget(t.Item, t.Item.Rotation, t.SnapshotEntry, t.StartAnimRotZ, targetRz);
                ov.AnimRotZ = targetRz;
            }
        }
        DepthSortVideoPlayerPatch.SetTimelineChanged();
    }

    private void ApplyScaleRatio(GizmoHandle handle, float ratio)
    {
        for (int i = 0; i < _targets.Count; i++)
        {
            var t = _targets[i];
            var ov = _liveOverrides[t.Item];

            if (t.ModelParam != null)
            {
                if (handle == GizmoHandle.ScaleUniform)
                {
                    float targetMaster = MathF.Max(0.1f, t.StartModelMasterScale * ratio);
                    SetAnimTarget(t.Item, t.ModelParam.ModelScale, t.SnapshotEntry, t.StartModelMasterScale, targetMaster);
                    ov.ModelMasterScale = targetMaster;
                    ov.ModelLocalScale = t.StartModelLocalScale;
                }
                else
                {
                    Vector3 s = t.StartModelLocalScale;
                    if (handle == GizmoHandle.ScaleX)
                    {
                        float tx = MathF.Max(0.1f, s.X * ratio);
                        SetAnimTarget(t.Item, t.ModelParam.ScaleX, t.SnapshotEntry, s.X, tx);
                        s.X = tx;
                    }
                    else if (handle == GizmoHandle.ScaleY)
                    {
                        float ty = MathF.Max(0.1f, s.Y * ratio);
                        SetAnimTarget(t.Item, t.ModelParam.ScaleY, t.SnapshotEntry, s.Y, ty);
                        s.Y = ty;
                    }
                    else if (handle == GizmoHandle.ScaleZ)
                    {
                        float tz = MathF.Max(0.1f, s.Z * ratio);
                        SetAnimTarget(t.Item, t.ModelParam.ScaleZ, t.SnapshotEntry, s.Z, tz);
                        s.Z = tz;
                    }
                    ov.ModelMasterScale = t.StartModelMasterScale;
                    ov.ModelLocalScale = s;
                }
            }
            else if (t.LightParam != null)
            {
                GizmoHandle areaHeightHandle = t.SnapshotEntry.UsesBlenderAxis ? GizmoHandle.ScaleZ : GizmoHandle.ScaleY;
                if (t.SnapshotEntry.Light?.Type == Lighting.LightType.Area && (handle == GizmoHandle.ScaleX || handle == areaHeightHandle))
                {
                    Vector2 area = t.StartLightArea;
                    if (handle == GizmoHandle.ScaleX)
                    {
                        area.X = MathF.Max(1f, area.X * ratio);
                        SetAnimTarget(t.Item, t.LightParam.AreaWidth, t.SnapshotEntry, t.StartLightArea.X, area.X);
                    }
                    else
                    {
                        area.Y = MathF.Max(1f, area.Y * ratio);
                        SetAnimTarget(t.Item, t.LightParam.AreaHeight, t.SnapshotEntry, t.StartLightArea.Y, area.Y);
                    }
                    ov.LightAreaSize = area;
                }
                else
                {
                    float targetRange = MathF.Max(1f, t.StartLightRange * ratio);
                    SetAnimTarget(t.Item, t.LightParam.Range, t.SnapshotEntry, t.StartLightRange, targetRange);
                    ov.LightRange = targetRange;
                }
            }
            else
            {
                float targetZoom = MathF.Max(0.1f, t.StartAnimZoom * ratio);
                SetAnimTarget(t.Item, t.Item.Zoom, t.SnapshotEntry, t.StartAnimZoom, targetZoom);
                ov.AnimZoom = targetZoom;
            }
        }
        DepthSortVideoPlayerPatch.SetTimelineChanged();
    }

    private void SetAnimTarget(
        IVideoItem item,
        Animation anim,
        ViewportItemEntry entry,
        double startEvalValue,
        double newEvalValue)
    {
        if (!_editKeyFrameOnly || anim.AnimationType == AnimationType.なし || !anim.AnimationType.IsKeyFrameSupported() || anim.Values.Count <= 1)
        {
            double curEval = anim.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps);
            double delta = newEvalValue - curEval;
            if (Math.Abs(delta) < 1e-6) return;

            if (anim.AnimationType == AnimationType.移動量指定)
            {
                anim.Values[0].Value = Math.Clamp(anim.Values[0].Value + delta, anim.MinValue, anim.MaxValue);
            }
            else
            {
                for (int i = 0; i < anim.Values.Count; i++)
                {
                    anim.Values[i].Value = Math.Clamp(anim.Values[i].Value + delta, anim.MinValue, anim.MaxValue);
                }
            }
            return;
        }

        int kfSlot = FindNearestKeyFrameValueIndex(item, entry.ItemFrame, anim.Values.Count);
        double curSlot = anim.Values[kfSlot].Value;
        double diff = newEvalValue - startEvalValue;
        // Adjust saved baseline slot by diff for precise keyframe dragging
        double? savedSlot = FindSavedSlotValue(anim, kfSlot);
        double targetVal = (savedSlot ?? curSlot) + diff;
        anim.Values[kfSlot].Value = Math.Clamp(targetVal, anim.MinValue, anim.MaxValue);
    }

    private double? FindSavedSlotValue(Animation anim, int slot)
    {
        for (int i = 0; i < _savedAnims.Count; i++)
        {
            if (ReferenceEquals(_savedAnims[i].Anim, anim) && (uint)slot < (uint)_savedAnims[i].Values.Length)
                return _savedAnims[i].Values[slot];
        }
        return null;
    }

    private static int FindNearestKeyFrameValueIndex(IVideoItem item, long itemFrame, int valueCount)
    {
        var kfs = item.KeyFrames;
        if (kfs == null || kfs.Frames.Count == 0 || valueCount <= 1)
            return 0;

        int bestIdx = 0;
        long bestDist = Math.Abs(itemFrame - 0);

        for (int i = 0; i < kfs.Frames.Count && (i + 1) < valueCount; i++)
        {
            long d = Math.Abs(itemFrame - kfs.Frames[i]);
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i + 1;
            }
        }

        int endIdx = Math.Min(kfs.Frames.Count + 1, valueCount - 1);
        long endDist = Math.Abs(itemFrame - Math.Max(0, item.Length - 1));
        if (endDist < bestDist)
            bestIdx = endIdx;

        return Math.Clamp(bestIdx, 0, valueCount - 1);
    }

    public Vector3 GetItemWorldPivot(ViewportItemEntry entry)
        => GetEffectiveDrawDescription(entry).Draw;

    public Matrix4x4 GetItemWorldOrientation(ViewportItemEntry entry)
    {
        var desc = GetEffectiveDrawDescription(entry);
        bool useBlender = entry.UsesBlenderAxis;
        var itemRot = useBlender
            ? BlenderAxisMath.ComposeItemRotationBlender(desc.Rotation)
            : RotationEulerMath.ComposeYmmRotation(desc.Rotation);

        if (entry.Kind == ViewportItemKind.BuiltInModel && entry.ModelSpec != null)
        {
            Vector3 localDeg = entry.ModelSpec.LocalRotationDeg;
            if (_liveOverrides.TryGetValue(entry.Item, out var ov) && ov.ModelLocalRotDeg is { } lr)
                localDeg = lr;

            var yFlip = Matrix4x4.CreateScale(1f, -1f, 1f);
            if (useBlender)
            {
                var localMat = BlenderAxisMath.ComposeModelLocalRotationBlender(localDeg);
                var combined = BlenderAxisMath.BlenderLocalToModelLocal * localMat * yFlip * itemRot;
                return BlenderAxisMath.OrthonormalizePreserveHandedness(combined);
            }
            else
            {
                var localMat = RotationEulerMath.ComposeModelLocalRotation(localDeg);
                var combined = localMat * yFlip * itemRot;
                // Remove reflection from YFlip so gizmo remains right-handed orthogonal axes along model orientation
                var absMat = yFlip * combined;
                return RotationEulerMath.Orthonormalize(absMat);
            }
        }

        if (useBlender)
        {
            return BlenderAxisMath.OrthonormalizePreserveHandedness(BlenderAxisMath.BlenderWorldBasis * itemRot);
        }

        return RotationEulerMath.Orthonormalize(itemRot);
    }

    public string BuildSelectionStatus(IReadOnlyList<IVideoItem> selectedItems, ViewportSceneSnapshot? snapshot)
    {
        string axisTag = UseBlenderAxis ? "[Blender軸] " : string.Empty;
        if (selectedItems.Count == 0)
            return $"{axisTag}選択なし（左クリックで選択 / 右ドラッグで視点回転 / 中ドラッグで移動 / ホイールで拡大）";

        var primary = selectedItems[0];
        var entry = snapshot?.Items.FirstOrDefault(x => x.Item == primary);
        if (entry == null)
            return $"{axisTag}選択: {primary.GetType().Name} (現在フレーム範囲外)";

        var desc = GetEffectiveDrawDescription(entry);
        Vector3 dispPos = entry.UsesBlenderAxis ? BlenderAxisMath.WorldToBlender(desc.Draw) : desc.Draw;
        string prefix = selectedItems.Count > 1 ? $"{axisTag}選択 {selectedItems.Count}件 [{entry.DisplayName}]" : $"{axisTag}選択: {entry.DisplayName}";

        if (entry.Kind == ViewportItemKind.BuiltInModel && entry.ModelSpec != null)
        {
            Vector3 lr = entry.ModelSpec.LocalRotationDeg;
            float ms = GetModelParam(primary) is { } mp
                ? (float)mp.ModelScale.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps)
                : 100f;
            if (_liveOverrides.TryGetValue(primary, out var ov))
            {
                if (ov.ModelLocalRotDeg is { } olr) lr = olr;
                if (ov.ModelMasterScale is { } oms) ms = oms;
            }
            return $"{prefix} | X={dispPos.X:F1} Y={dispPos.Y:F1} Z={dispPos.Z:F1} | 回転=({lr.X:F1}°, {lr.Y:F1}°, {lr.Z:F1}°) | 倍率={ms:F0}%";
        }

        if (entry.Kind == ViewportItemKind.Light && entry.Light != null)
        {
            var l = GetEffectiveLight(entry, desc);
            Vector3 dispDir = entry.UsesBlenderAxis ? BlenderAxisMath.WorldToBlender(l.Direction) : l.Direction;
            return $"{prefix} | X={dispPos.X:F1} Y={dispPos.Y:F1} Z={dispPos.Z:F1} | 向き=({dispDir.X:F2}, {dispDir.Y:F2}, {dispDir.Z:F2}) | 範囲={l.Range:F0}";
        }

        return $"{prefix} | X={dispPos.X:F1} Y={dispPos.Y:F1} Z={dispPos.Z:F1} | 回転=({desc.Rotation.X:F1}°, {desc.Rotation.Y:F1}°, {desc.Rotation.Z:F1}°) | 拡大={desc.Zoom.X * 100f:F0}%";
    }

    private static Vector3 GetCapturedAnimPos(ViewportItemEntry entry) => new(
        (float)entry.Item.X.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps),
        (float)entry.Item.Y.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps),
        (float)entry.Item.Z.GetValue(entry.ItemFrame, entry.ItemLength, entry.Fps));

    private static Model3D.ID3DModelTransformParameter? GetModelParam(IVideoItem item)
        => item is ShapeItem si ? si.ShapeParameter as Model3D.ID3DModelTransformParameter : null;

    private static Lighting.D3DLightShapeParameter? GetLightParam(IVideoItem item)
        => item is ShapeItem si ? si.ShapeParameter as Lighting.D3DLightShapeParameter : null;

    private static RotateEffect? Find3DRotateEffect(IVideoItem item)
    {
        if (item.VideoEffects == null) return null;
        for (int i = item.VideoEffects.Count - 1; i >= 0; i--)
        {
            if (item.VideoEffects[i] is RotateEffect re && re.IsEnabled)
                return re;
        }
        return null;
    }

    private static (Vector3 Right, Vector3 Up) BuildPlaneBasis(Vector3 normal)
    {
        Vector3 hint = MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(hint, normal));
        Vector3 up = Vector3.Cross(normal, right);
        return (right, up);
    }

    private static float WrapAnglePi(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}
