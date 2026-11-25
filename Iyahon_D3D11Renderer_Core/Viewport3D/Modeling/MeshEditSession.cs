using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows.Input;
using Iyahon_D3D11Renderer_Core.Model3D;
using Iyahon_D3D11Renderer_Core.Model3D.Parsers;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modifiers;
using Vortice.Direct3D11;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;

internal enum ModalOpKind
{
    None,
    Grab,
    Rotate,
    Scale,
    LoopCutPickRing,
    LoopCutSlide
}

internal enum AxisConstraintMode
{
    None,
    X,
    Y,
    Z,
    ExcludeX,
    ExcludeY,
    ExcludeZ
}

internal enum AxisConstraintSpace
{
    World,
    Local
}

internal sealed class MeshUndoEntry
{
    public required EditableMesh MeshSnapshot { get; init; }
    public required MeshSelection SelectionSnapshot { get; init; }
    public Modifiers.MeshModifierStack? ModifierStackSnapshot { get; init; }
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// 3Dビューポート上でのインタラクティブなメッシュ編集セッションを管理する。
/// </summary>
internal sealed class MeshEditSession : IDisposable
{
    private const int MaxUndoSteps = 100;

    private readonly List<MeshUndoEntry> _undoStack = new();
    private readonly List<MeshUndoEntry> _redoStack = new();

    private CachedGpuModel? _baseDonorModel;
    private CachedGpuModel? _transientGpuModel;
    private bool _meshGpuDirty = true;
    private bool _materialGpuDirty;
    private string _cachedBaseFilePath = string.Empty;
    private IntPtr _cachedDevicePtr;
    private string _loadedFilePath = string.Empty;
    private D3DPrimitiveShapeKind? _loadedPrimitiveKind;
    private bool _reloadingSource;
    private string _lastSceneModKey = string.Empty;

    public event Action? SourceModelReloaded;
    public bool IsInteractiveMaterialPreview { get; set; }
    public Modifiers.MeshModifierStack Modifiers { get; private set; } = new();

    // モーダル変形ステート
    private readonly Dictionary<int, Vector3> _modalStartLocalPos = new();
    private readonly Dictionary<int, List<Vector3>?> _modalSavedCornerNormals = new();
    private Vector3 _modalPivotWorld;
    private Vector2 _modalStartMousePx;
    private Vector2 _modalLastMousePx;
    private float _modalAccumAngleRad;
    private string _modalNumericBuffer = string.Empty;

    // ギズモドラッグステート
    private GizmoHandle _activeGizmoHandle = GizmoHandle.None;
    private TransformGizmoState _dragGizmo;
    private Vector3 _gizmoDragPlanePoint;
    private Vector3 _gizmoDragPlaneNormal;
    private Vector3 _gizmoDragHitStart;
    private Vector3 _gizmoDragAxis;
    private float _gizmoDragAxisStartParam;
    private Vector3 _gizmoDragTurnRight;
    private Vector3 _gizmoDragTurnUp;
    private float _gizmoDragStartAngle;
    private float _gizmoDragScaleStartDist;

    // ループカットステート
    private LoopCutRing? _activeRing;
    private int _loopCutCount = 1;
    private float _loopCutSlide;
    private Vector2 _loopCutScreenAxis = Vector2.UnitX;
    private float _loopCutHalfSpanPx = 140f;

    // ボックス選択ステート
    private bool _isBoxSelecting;
    private Vector2 _boxStartPx;
    private Vector2 _boxEndPx;

    private int _activeMaterialIndex;

    public bool IsActive => TargetParameter != null;
    public IVideoItem? TargetItem { get; private set; }
    public ID3DModelTransformParameter? TargetParameter { get; private set; }

    public int ActiveMaterialIndex
    {
        get => Mesh.Materials.Count == 0 ? 0 : Math.Clamp(_activeMaterialIndex, 0, Mesh.Materials.Count - 1);
        set => _activeMaterialIndex = Mesh.Materials.Count == 0 ? 0 : Math.Clamp(value, 0, Mesh.Materials.Count - 1);
    }

    public EditableMesh Mesh { get; private set; } = new();
    public MeshSelection Selection { get; } = new();
    public EditElementMode SelectMode { get; set; } = EditElementMode.Vertex;
    public bool XRayEnabled { get; set; }

    public Matrix4x4 ModelToWorld { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 WorldToModel { get; private set; } = Matrix4x4.Identity;

    public ModalOpKind ActiveModalOp { get; private set; } = ModalOpKind.None;
    public AxisConstraintMode AxisConstraint { get; private set; } = AxisConstraintMode.None;
    public AxisConstraintSpace ConstraintSpace { get; private set; } = AxisConstraintSpace.World;
    public GizmoMode EditGizmoMode { get; set; } = GizmoMode.Translate;
    public GizmoHandle HoveredGizmoHandle { get; set; } = GizmoHandle.None;
    public bool IsGizmoDragging => _activeGizmoHandle != GizmoHandle.None;
    public bool IsBoxSelecting => _isBoxSelecting;
    public bool IsModalOrLoopCut => ActiveModalOp != ModalOpKind.None;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// タイムライン上のアイテムから編集セッションを開始する。
    /// </summary>
    public bool Enter(IVideoItem? item, ViewportSceneSnapshot? snapshot, ViewportManipulator? manipulator = null)
    {
        if (item is not ShapeItem si || si.ShapeParameter is not ID3DModelTransformParameter param)
            return false;

        return Enter(param, item, snapshot, manipulator);
    }

    public bool Enter(
        D3DModelShapeParameter param,
        IVideoItem? item = null,
        ViewportSceneSnapshot? snapshot = null,
        ViewportManipulator? manipulator = null)
        => Enter((ID3DModelTransformParameter)param, item, snapshot, manipulator);

    public bool Enter(
        ID3DModelTransformParameter param,
        IVideoItem? item = null,
        ViewportSceneSnapshot? snapshot = null,
        ViewportManipulator? manipulator = null)
    {
        if (IsActive && IsInteractiveMaterialPreview)
            FlushInteractiveMaterialPreview();
        Exit(commitChanges: false);

        TargetItem = item;
        TargetParameter = param;
        _loadedFilePath = param.FilePath ?? string.Empty;
        _loadedPrimitiveKind = param is D3DPrimitiveShapeParameter primParam ? primParam.ShapeKind : null;
        _undoStack.Clear();
        _redoStack.Clear();
        Selection.Clear();
        ResetTransientOpState();
        IsInteractiveMaterialPreview = false;

        EditableMesh? loaded = null;
        bool createdNewEmpty = false;

        if (!string.IsNullOrWhiteSpace(param.EmbeddedMeshData) &&
            EditableMesh.TryDeserialize(param.EmbeddedMeshData, out var emb) &&
            emb != null &&
            (emb.SourceFilePath == null || string.Equals(emb.SourceFilePath, _loadedFilePath, StringComparison.OrdinalIgnoreCase)))
        {
            loaded = emb;
        }
        else if (!string.IsNullOrWhiteSpace(_loadedFilePath))
        {
            var parsed = ModelParserDispatcher.Parse(_loadedFilePath);
            if (parsed != null)
            {
                loaded = EditableMesh.FromParsedModel(parsed);
                if (!string.IsNullOrEmpty(param.EmbeddedMeshData))
                    param.EmbeddedMeshData = string.Empty;
            }
        }

        if (loaded == null)
        {
            loaded = param.CreateDefaultEditableMesh();
            createdNewEmpty = true;
        }
        loaded.SourceFilePath ??= _loadedFilePath;

        Mesh = loaded;
        MeshModifierStack.TryDeserialize(param.ModifierStackData, out var parsedMods);
        Modifiers = parsedMods;
        _lastSceneModKey = string.Empty;
        _lastModUndoTag = null;
        _activeMaterialIndex = 0;
        InvalidateGpuCache();
        UpdateWorldTransform(snapshot, manipulator);

        if (createdNewEmpty)
        {
            CommitToParameter();
        }

        if (param is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged += OnTargetParameterPropertyChanged;

        return true;
    }

    public void Exit(bool commitChanges = true)
    {
        if (TargetParameter is System.ComponentModel.INotifyPropertyChanged npc)
            npc.PropertyChanged -= OnTargetParameterPropertyChanged;

        if (IsModalOrLoopCut)
            CancelModalOrLoopCut();
        if (IsGizmoDragging)
            CancelGizmoDrag();
        _isBoxSelecting = false;
        bool hadInteractive = IsInteractiveMaterialPreview;
        IsInteractiveMaterialPreview = false;

        if (TargetParameter != null && ((commitChanges && _undoStack.Count > 0) || hadInteractive))
        {
            CommitToParameter();
        }

        DisposeTransientGpu();
        _baseDonorModel = null;
        _cachedBaseFilePath = string.Empty;
        _loadedFilePath = string.Empty;
        _loadedPrimitiveKind = null;
        _lastSceneModKey = string.Empty;
        Modifiers = new Modifiers.MeshModifierStack();
        _undoStack.Clear();
        _redoStack.Clear();
        Selection.Clear();
        TargetItem = null;
        TargetParameter = null;
    }

    public void Dispose()
    {
        Exit(commitChanges: false);
    }

    public void CommitToParameter()
    {
        if (TargetParameter == null) return;
        Mesh.SourceFilePath = TargetParameter.FilePath ?? string.Empty;
        TargetParameter.EmbeddedMeshData = Mesh.Serialize();
        TargetParameter.ModifierStackData = Modifiers.Serialize();
        DepthSortVideoPlayerPatch.SetTimelineChanged();
    }

    public void UpdateWorldTransform(ViewportSceneSnapshot? snapshot, ViewportManipulator? manipulator = null)
    {
        if (TargetParameter == null)
        {
            ModelToWorld = Matrix4x4.Identity;
            WorldToModel = Matrix4x4.Identity;
            return;
        }
        SyncWithTargetSourceIfChanged();

        Mesh.ComputeCurrentBounds(out var geomMin, out var geomMax, out var geomCenter, out float geomScale);
        Vector3 modelCenter = Mesh.HasRefBounds ? Mesh.RefCenter : geomCenter;
        float modelScale = Mesh.HasRefBounds ? Mesh.RefScale : geomScale;

        ViewportItemEntry? entry = null;
        if (snapshot != null && TargetItem != null)
        {
            for (int i = 0; i < snapshot.Items.Count; i++)
            {
                if (ReferenceEquals(snapshot.Items[i].Item, TargetItem))
                {
                    entry = snapshot.Items[i];
                    break;
                }
            }
        }

        DrawDescription drawDesc;
        bool normalizeSize;
        bool groundToOrigin;
        float unitScale;
        Vector3 localScale;
        Vector3 localRotDeg;

        if (entry != null && entry.ModelSpec != null)
        {
            drawDesc = manipulator != null
                ? manipulator.GetEffectiveDrawDescription(entry)
                : entry.DrawDescription;
            var sp = entry.ModelSpec;
            normalizeSize = sp.NormalizeSize;
            groundToOrigin = sp.GroundToOrigin;
            unitScale = sp.UnitScale;
            localScale = sp.LocalScale;
            localRotDeg = sp.LocalRotationDeg;
            if (manipulator?.GetLiveOverride(entry.Item) is { } ov)
            {
                localScale = ViewportManipulator.ComputeEffectiveLocalScale(sp, ov);
                localRotDeg = ov.ModelLocalRotDeg ?? sp.LocalRotationDeg;
            }
        }
        else
        {
            var p = TargetParameter;
            float ms = p.ModelScale != null ? (float)p.ModelScale.GetValue(0, 1, 60) / 100f : 1f;
            localScale = new Vector3(
                (p.ScaleX != null ? (float)p.ScaleX.GetValue(0, 1, 60) / 100f : 1f) * ms,
                (p.ScaleY != null ? (float)p.ScaleY.GetValue(0, 1, 60) / 100f : 1f) * ms,
                (p.ScaleZ != null ? (float)p.ScaleZ.GetValue(0, 1, 60) / 100f : 1f) * ms);
            localRotDeg = new Vector3(
                p.LocalRotX != null ? (float)p.LocalRotX.GetValue(0, 1, 60) : 0f,
                p.LocalRotY != null ? (float)p.LocalRotY.GetValue(0, 1, 60) : 0f,
                p.LocalRotZ != null ? (float)p.LocalRotZ.GetValue(0, 1, 60) : 0f);
            normalizeSize = p.NormalizeSize;
            groundToOrigin = p.GroundToOrigin;
            unitScale = p.UnitScale != null ? (float)p.UnitScale.GetValue(0, 1, 60) : 100f;

            Vector3 itemPos = new(
                TargetItem?.X != null ? (float)TargetItem.X.GetValue(0, 1, 60) : 0f,
                TargetItem?.Y != null ? (float)TargetItem.Y.GetValue(0, 1, 60) : 0f,
                TargetItem?.Z != null ? (float)TargetItem.Z.GetValue(0, 1, 60) : 0f);
            float zoom = TargetItem?.Zoom != null ? (float)TargetItem.Zoom.GetValue(0, 1, 60) / 100f : 1f;
            float rz = TargetItem?.Rotation != null ? (float)TargetItem.Rotation.GetValue(0, 1, 60) : 0f;

            drawDesc = new DrawDescription(
                itemPos, Vector2.Zero, new Vector2(zoom, zoom), new Vector3(0f, 0f, rz),
                Matrix4x4.Identity, Vortice.Direct2D1.InterpolationMode.Linear, 1.0, false,
                System.Collections.Immutable.ImmutableList<VideoEffectController>.Empty);
        }

        var dummy = new ObjModelData
        {
            VertexBuffer = null!,
            IndexBuffer = null!,
            Parts = Array.Empty<ObjPartRenderInfo>(),
            BoundsMin = geomMin,
            BoundsMax = geomMax,
            ModelCenter = modelCenter,
            ModelScale = modelScale,
            NormalizeSize = normalizeSize,
            GroundToOrigin = groundToOrigin,
            UnitScale = unitScale,
            LocalScale = localScale,
            LocalRotationDeg = localRotDeg
        };

        ModelToWorld = DepthSortRenderer.BuildObjModelMatrix(
            new RenderItem { DrawDescription = drawDesc, Srv = null }, dummy);
        if (!Matrix4x4.Invert(ModelToWorld, out var inv))
            inv = Matrix4x4.Identity;
        WorldToModel = inv;
    }

    #region GPU バッファ管理

    public void InvalidateGpuCache()
    {
        _meshGpuDirty = true;
    }

    public void InvalidateMaterialGpuCache()
    {
        _materialGpuDirty = true;
    }

    public CachedGpuModel? GetOrBuildGpuModel(ID3D11Device device, ID3D11DeviceContext ctx)
        => GetOrBuildGpuModelWithSceneAtTime(device, ctx, null, null, -1);

    public CachedGpuModel? GetOrBuildGpuModelAtTime(ID3D11Device device, ID3D11DeviceContext ctx, Shading.ShaderEvalTime? evalTime)
        => GetOrBuildGpuModelWithSceneAtTime(device, ctx, evalTime, null, -1);

    public CachedGpuModel? GetOrBuildGpuModelWithSceneAtTime(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        Shading.ShaderEvalTime? evalTime,
        SceneModifierContext? sceneCtx,
        int selfLayer)
    {
        if (!IsActive) return null;
        SyncWithTargetSourceIfChanged();

        if (_cachedDevicePtr != device.NativePointer)
        {
            DisposeTransientGpu();
            _baseDonorModel = null;
            _cachedBaseFilePath = string.Empty;
            _cachedDevicePtr = device.NativePointer;
            _meshGpuDirty = true;
        }

        string filePath = TargetParameter?.FilePath ?? string.Empty;
        if (!string.Equals(_cachedBaseFilePath, filePath, StringComparison.OrdinalIgnoreCase) ||
            (_baseDonorModel != null && _baseDonorModel.IsDisposed))
        {
            _cachedBaseFilePath = filePath;
            int maxTex = (int)D3D11RendererSettings.Default.ModelMaxTextureSize;
            _baseDonorModel = !string.IsNullOrWhiteSpace(filePath)
                ? ModelGpuCache.GetOrLoad(device, ctx, filePath, maxTex)
                : null;
            _meshGpuDirty = true;
        }

        string sceneKey = (sceneCtx != null && Modifiers.HasSceneDependentEnabledModifiers)
            ? sceneCtx.ComputeSceneTargetsCacheKey(Modifiers, selfLayer, ModelToWorld)
            : string.Empty;
        if (!string.Equals(sceneKey, _lastSceneModKey, StringComparison.Ordinal))
        {
            _lastSceneModKey = sceneKey;
            _meshGpuDirty = true;
        }

        int? resOverride = IsInteractiveMaterialPreview ? 128 : null;

        bool timeDirty = false;
        if (evalTime.HasValue && _transientGpuModel != null && _transientGpuModel.HasTimeDependentMaterial)
        {
            long tKey = ModelGpuCache.ComputeCombinedTimeKey(Mesh, evalTime.Value);
            if (tKey != _transientGpuModel.LastTimeKey)
                timeDirty = true;
        }

        if (_transientGpuModel != null && _transientGpuModel.HasDisposedResources())
        {
            if (_transientGpuModel.VertexBuffer.NativePointer == IntPtr.Zero ||
                _transientGpuModel.IndexBuffer.NativePointer == IntPtr.Zero)
            {
                _meshGpuDirty = true;
            }
            else
            {
                _materialGpuDirty = true;
            }
        }

        if (!_meshGpuDirty && (_materialGpuDirty || timeDirty) && _transientGpuModel != null && !_transientGpuModel.IsDisposed)
        {
            if (ModelGpuCache.RefreshTransientMaterials(device, ctx, _transientGpuModel, Mesh, _baseDonorModel, resOverride, evalTime))
            {
                _materialGpuDirty = false;
                return _transientGpuModel;
            }
            _meshGpuDirty = true;
        }

        if (_meshGpuDirty || _transientGpuModel == null || _transientGpuModel.IsDisposed)
        {
            DisposeTransientGpu();
            EditableMesh meshForGpu = Modifiers.HasEnabledModifiers
                ? MeshModifierEvaluator.Evaluate(Mesh, Modifiers, sceneCtx, selfLayer, ModelToWorld)
                : Mesh;
            _transientGpuModel = ModelGpuCache.BuildTransientGpuModel(device, meshForGpu, _baseDonorModel, ctx, resOverride, evalTime);
            _meshGpuDirty = false;
            _materialGpuDirty = false;
        }

        return _transientGpuModel;
    }

    private void DisposeTransientGpu()
    {
        if (_transientGpuModel != null)
        {
            _transientGpuModel.Dispose();
            _transientGpuModel = null;
        }
    }

    private void OnTargetParameterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_reloadingSource || !IsActive || TargetParameter == null) return;
        if (e.PropertyName is nameof(ID3DModelTransformParameter.FilePath) or nameof(D3DPrimitiveShapeParameter.ShapeKind))
        {
            SyncWithTargetSourceIfChanged();
        }
    }

    internal bool SyncWithTargetSourceIfChanged()
    {
        if (_reloadingSource || !IsActive || TargetParameter == null) return false;

        string curFile = TargetParameter.FilePath ?? string.Empty;
        D3DPrimitiveShapeKind? curKind = TargetParameter is D3DPrimitiveShapeParameter pp ? pp.ShapeKind : null;
        bool fileChanged = !string.Equals(_loadedFilePath, curFile, StringComparison.OrdinalIgnoreCase);
        bool kindChanged = curKind != _loadedPrimitiveKind;
        if (!fileChanged && !kindChanged)
            return false;

        ReloadFromTargetSource();
        return true;
    }

    internal void ReloadFromTargetSource()
    {
        if (_reloadingSource || !IsActive || TargetParameter == null) return;
        _reloadingSource = true;
        try
        {
            if (IsModalOrLoopCut)
                CancelModalOrLoopCut();
            if (IsGizmoDragging)
                CancelGizmoDrag();
            _isBoxSelecting = false;
            IsInteractiveMaterialPreview = false;

            _undoStack.Clear();
            _redoStack.Clear();
            Selection.Clear();
            _lastMatUndoTag = null;
            ResetTransientOpState();

            string nextPath = TargetParameter.FilePath ?? string.Empty;
            _loadedFilePath = nextPath;
            _loadedPrimitiveKind = TargetParameter is D3DPrimitiveShapeParameter pp ? pp.ShapeKind : null;

            if (!string.IsNullOrEmpty(TargetParameter.EmbeddedMeshData))
                TargetParameter.EmbeddedMeshData = string.Empty;

            EditableMesh? nextMesh = null;
            if (!string.IsNullOrWhiteSpace(nextPath))
            {
                var parsed = ModelParserDispatcher.Parse(nextPath);
                if (parsed != null)
                    nextMesh = EditableMesh.FromParsedModel(parsed);
            }
            nextMesh ??= TargetParameter.CreateDefaultEditableMesh();
            nextMesh.SourceFilePath = nextPath;

            Mesh = nextMesh;
            _activeMaterialIndex = 0;
            _baseDonorModel = null;
            _cachedBaseFilePath = string.Empty;
            DisposeTransientGpu();
            InvalidateGpuCache();

            DepthSortVideoPlayerPatch.SetTimelineChanged();
        }
        finally
        {
            _reloadingSource = false;
        }
        SourceModelReloaded?.Invoke();
    }

    #endregion

    #region Undo / Redo

    public void PushUndo(string description)
    {
        _lastMatUndoTag = null;
        _lastModUndoTag = null;
        _undoStack.Add(new MeshUndoEntry
        {
            MeshSnapshot = Mesh.Clone(),
            SelectionSnapshot = Selection.Clone(),
            ModifierStackSnapshot = Modifiers.Clone(),
            Description = description
        });
        while (_undoStack.Count > MaxUndoSteps)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;
        if (IsModalOrLoopCut) CancelModalOrLoopCut();
        if (IsGizmoDragging) CancelGizmoDrag();

        _lastMatUndoTag = null;
        _lastModUndoTag = null;
        _redoStack.Add(new MeshUndoEntry
        {
            MeshSnapshot = Mesh.Clone(),
            SelectionSnapshot = Selection.Clone(),
            ModifierStackSnapshot = Modifiers.Clone(),
            Description = "Redo"
        });

        var entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        Mesh = entry.MeshSnapshot;
        if (entry.ModifierStackSnapshot != null)
            Modifiers = entry.ModifierStackSnapshot.Clone();
        RestoreSelection(entry.SelectionSnapshot);
        InvalidateGpuCache();
        CommitToParameter();
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0) return false;
        if (IsModalOrLoopCut) CancelModalOrLoopCut();
        if (IsGizmoDragging) CancelGizmoDrag();

        _lastMatUndoTag = null;
        _lastModUndoTag = null;
        _undoStack.Add(new MeshUndoEntry
        {
            MeshSnapshot = Mesh.Clone(),
            SelectionSnapshot = Selection.Clone(),
            ModifierStackSnapshot = Modifiers.Clone(),
            Description = "Undo"
        });

        var entry = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        Mesh = entry.MeshSnapshot;
        if (entry.ModifierStackSnapshot != null)
            Modifiers = entry.ModifierStackSnapshot.Clone();
        RestoreSelection(entry.SelectionSnapshot);
        InvalidateGpuCache();
        CommitToParameter();
        return true;
    }

    private void RestoreSelection(MeshSelection snap)
    {
        Selection.Clear();
        Selection.Vertices.UnionWith(snap.Vertices);
        Selection.Edges.UnionWith(snap.Edges);
        Selection.Faces.UnionWith(snap.Faces);
        Selection.ActiveVertex = snap.ActiveVertex;
        Selection.ActiveEdge = snap.ActiveEdge;
        Selection.ActiveFace = snap.ActiveFace;
        Selection.SyncByMode(Mesh, SelectMode);
    }

    #endregion

    #region ピッキング & ボックス選択

    public bool PickAndSelect(Vector2 mousePx, ViewportCamera camera, float vw, float vh, bool additive)
    {
        if (!IsActive) return false;

        switch (SelectMode)
        {
            case EditElementMode.Vertex:
            {
                int hitV = PickVertexIndex(mousePx, camera, vw, vh, 14f);
                if (hitV < 0)
                    return false;

                if (additive)
                {
                    if (!Selection.Vertices.Remove(hitV))
                    {
                        Selection.Vertices.Add(hitV);
                        Selection.ActiveVertex = hitV;
                    }
                }
                else
                {
                    Selection.Clear();
                    Selection.Vertices.Add(hitV);
                    Selection.ActiveVertex = hitV;
                }
                Selection.SyncFromVertices(Mesh);
                return true;
            }

            case EditElementMode.Edge:
            {
                int hitE = PickEdgeIndex(mousePx, camera, vw, vh, 12f);
                if (hitE < 0)
                    return false;

                if (additive)
                {
                    if (!Selection.Edges.Remove(hitE))
                    {
                        Selection.Edges.Add(hitE);
                        Selection.ActiveEdge = hitE;
                    }
                }
                else
                {
                    Selection.Clear();
                    Selection.Edges.Add(hitE);
                    Selection.ActiveEdge = hitE;
                }
                Selection.SyncFromEdges(Mesh);
                return true;
            }

            case EditElementMode.Face:
            default:
            {
                int hitF = PickFaceIndex(mousePx, camera, vw, vh);
                if (hitF < 0)
                    return false;

                if (additive)
                {
                    if (!Selection.Faces.Remove(hitF))
                    {
                        Selection.Faces.Add(hitF);
                        Selection.ActiveFace = hitF;
                    }
                }
                else
                {
                    Selection.Clear();
                    Selection.Faces.Add(hitF);
                    Selection.ActiveFace = hitF;
                }
                Selection.SyncFromFaces(Mesh);
                return true;
            }
        }
    }

    private int PickVertexIndex(Vector2 mousePx, ViewportCamera camera, float vw, float vh, float radiusPx)
    {
        int bestIdx = -1;
        float bestDepth = float.MaxValue;
        float bestDist = radiusPx;

        Vector3 camPos = camera.CameraPosition;
        camera.GetCameraAxes(out _, out _, out var camFwd);
        List<OcclusionTri>? occlTris = null;

        for (int i = 0; i < Mesh.Vertices.Count; i++)
        {
            Vector3 localPos = Mesh.Vertices[i].Position;
            Vector3 wp = Vector3.Transform(localPos, ModelToWorld);
            var sp = camera.WorldToScreen(wp, vw, vh);
            if (!sp.HasValue) continue;

            float d = Vector2.Distance(mousePx, sp.Value);
            if (d > radiusPx) continue;

            if (!XRayEnabled)
            {
                occlTris ??= BuildLocalOcclusionTriangles();
                if (IsLocalPointOccluded(occlTris, camera, localPos, skipVertIdx: i))
                    continue;
            }

            float depth = camera.IsOrthographic
                ? Vector3.Dot(wp - camPos, camFwd)
                : Vector3.DistanceSquared(wp, camPos);

            // ピクセル距離が十分近ければ手前の頂点を優先
            if (bestIdx < 0 || depth < bestDepth - 1e-2f || (MathF.Abs(depth - bestDepth) <= 1e-2f && d < bestDist))
            {
                bestIdx = i;
                bestDepth = depth;
                bestDist = d;
            }
        }
        return bestIdx;
    }

    private int PickEdgeIndex(Vector2 mousePx, ViewportCamera camera, float vw, float vh, float radiusPx)
    {
        int bestIdx = -1;
        float bestDist = radiusPx;
        float bestDepth = float.MaxValue;

        Vector3 camPos = camera.CameraPosition;
        camera.GetCameraAxes(out _, out _, out var camFwd);
        List<OcclusionTri>? occlTris = null;
        Dictionary<(int, int), HashSet<int>>? edgeFaceMap = null;

        for (int ei = 0; ei < Mesh.Edges.Count; ei++)
        {
            var e = Mesh.Edges[ei];
            Vector3 wp0 = Vector3.Transform(Mesh.Vertices[e.V0].Position, ModelToWorld);
            Vector3 wp1 = Vector3.Transform(Mesh.Vertices[e.V1].Position, ModelToWorld);
            var sp0 = camera.WorldToScreen(wp0, vw, vh);
            var sp1 = camera.WorldToScreen(wp1, vw, vh);
            if (!sp0.HasValue || !sp1.HasValue) continue;

            float d = DistancePointToSegment2D(mousePx, sp0.Value, sp1.Value, out float segT);
            if (d > radiusPx) continue;

            if (!XRayEnabled)
            {
                occlTris ??= BuildLocalOcclusionTriangles();
                edgeFaceMap ??= BuildEdgeIncidentFaceMap();
                Vector3 sampleLocal = Vector3.Lerp(Mesh.Vertices[e.V0].Position, Mesh.Vertices[e.V1].Position, segT);
                edgeFaceMap.TryGetValue((e.V0, e.V1), out var incFaces);
                if (IsLocalPointOccluded(occlTris, camera, sampleLocal, skipFaces: incFaces))
                    continue;
            }

            Vector3 wpClosest = Vector3.Lerp(wp0, wp1, segT);
            float depth = camera.IsOrthographic
                ? Vector3.Dot(wpClosest - camPos, camFwd)
                : Vector3.DistanceSquared(wpClosest, camPos);

            if (bestIdx < 0 || d + 2f < bestDist || (MathF.Abs(d - bestDist) <= 2f && depth < bestDepth))
            {
                bestIdx = ei;
                bestDist = d;
                bestDepth = depth;
            }
        }
        return bestIdx;
    }

    private int PickFaceIndex(Vector2 mousePx, ViewportCamera camera, float vw, float vh)
    {
        var rayWorld = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);
        Vector3 oLocal = Vector3.Transform(rayWorld.Origin, WorldToModel);
        Vector3 dLocal = Vector3.TransformNormal(rayWorld.Direction, WorldToModel);
        float len = dLocal.Length();
        if (len < 1e-8f) return -1;
        dLocal /= len;

        int bestFace = -1;
        float bestWorldDist = float.MaxValue;

        for (int fi = 0; fi < Mesh.Faces.Count; fi++)
        {
            var f = Mesh.Faces[fi];
            var vi = f.VertexIndices;
            if (vi.Count < 3) continue;

            Vector3 p0 = Mesh.Vertices[vi[0]].Position;
            for (int k = 1; k + 1 < vi.Count; k++)
            {
                Vector3 p1 = Mesh.Vertices[vi[k]].Position;
                Vector3 p2 = Mesh.Vertices[vi[k + 1]].Position;
                if (IntersectRayTriangle(oLocal, dLocal, p0, p1, p2, out float tLocal) && tLocal > 1e-4f)
                {
                    Vector3 hitWorld = Vector3.Transform(oLocal + dLocal * tLocal, ModelToWorld);
                    float wDist = Vector3.DistanceSquared(hitWorld, rayWorld.Origin);
                    if (wDist < bestWorldDist)
                    {
                        bestWorldDist = wDist;
                        bestFace = fi;
                    }
                }
            }
        }

        if (bestFace >= 0)
            return bestFace;

        // 極小面用のスクリーン重心フォールバック
        float bestPxDist = 14f;
        for (int fi = 0; fi < Mesh.Faces.Count; fi++)
        {
            var cLocal = ComputeFaceCenterLocal(Mesh, Mesh.Faces[fi]);
            var sp = camera.WorldToScreen(Vector3.Transform(cLocal, ModelToWorld), vw, vh);
            if (!sp.HasValue) continue;
            float d = Vector2.Distance(mousePx, sp.Value);
            if (d < bestPxDist)
            {
                bestPxDist = d;
                bestFace = fi;
            }
        }
        return bestFace;
    }

    public void BeginBoxSelect(Vector2 startPx)
    {
        _isBoxSelecting = true;
        _boxStartPx = startPx;
        _boxEndPx = startPx;
    }

    public void UpdateBoxSelect(Vector2 currentPx)
    {
        if (!_isBoxSelecting) return;
        _boxEndPx = currentPx;
    }

    public bool EndBoxSelect(ViewportCamera camera, float vw, float vh, bool additive)
    {
        if (!_isBoxSelecting) return false;
        _isBoxSelecting = false;

        float minX = MathF.Min(_boxStartPx.X, _boxEndPx.X);
        float maxX = MathF.Max(_boxStartPx.X, _boxEndPx.X);
        float minY = MathF.Min(_boxStartPx.Y, _boxEndPx.Y);
        float maxY = MathF.Max(_boxStartPx.Y, _boxEndPx.Y);

        if (maxX - minX < 4f && maxY - minY < 4f)
            return false;

        if (!additive) Selection.Clear();

        bool InBox(Vector2 p) => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
        List<OcclusionTri>? occlTris = XRayEnabled ? null : BuildLocalOcclusionTriangles();

        switch (SelectMode)
        {
            case EditElementMode.Vertex:
                for (int i = 0; i < Mesh.Vertices.Count; i++)
                {
                    Vector3 localPos = Mesh.Vertices[i].Position;
                    var sp = camera.WorldToScreen(Vector3.Transform(localPos, ModelToWorld), vw, vh);
                    if (!sp.HasValue || !InBox(sp.Value)) continue;
                    if (XRayEnabled || !IsLocalPointOccluded(occlTris!, camera, localPos, skipVertIdx: i))
                        Selection.Vertices.Add(i);
                }
                Selection.SyncFromVertices(Mesh);
                break;

            case EditElementMode.Edge:
            {
                Dictionary<(int, int), HashSet<int>>? edgeFaceMap = XRayEnabled ? null : BuildEdgeIncidentFaceMap();
                ReadOnlySpan<float> sampleTs = stackalloc float[] { 0.5f, 0.25f, 0.75f };
                for (int ei = 0; ei < Mesh.Edges.Count; ei++)
                {
                    var e = Mesh.Edges[ei];
                    Vector3 p0Local = Mesh.Vertices[e.V0].Position;
                    Vector3 p1Local = Mesh.Vertices[e.V1].Position;

                    if (XRayEnabled)
                    {
                        Vector3 mid = 0.5f * (p0Local + p1Local);
                        var spMid = camera.WorldToScreen(Vector3.Transform(mid, ModelToWorld), vw, vh);
                        if (spMid.HasValue && InBox(spMid.Value))
                        {
                            Selection.Edges.Add(ei);
                            continue;
                        }
                        var sp0 = camera.WorldToScreen(Vector3.Transform(p0Local, ModelToWorld), vw, vh);
                        var sp1 = camera.WorldToScreen(Vector3.Transform(p1Local, ModelToWorld), vw, vh);
                        if ((sp0.HasValue && InBox(sp0.Value)) && (sp1.HasValue && InBox(sp1.Value)))
                        {
                            Selection.Edges.Add(ei);
                        }
                        continue;
                    }

                    edgeFaceMap!.TryGetValue((e.V0, e.V1), out var incFaces);
                    bool selected = false;
                    // 透過OFF時は辺の可視部(0.25/0.5/0.75)がボックス内にあり且つ手前の面に遮蔽されていない場合のみ選択
                    foreach (float st in sampleTs)
                    {
                        Vector3 sampleLocal = Vector3.Lerp(p0Local, p1Local, st);
                        var spSample = camera.WorldToScreen(Vector3.Transform(sampleLocal, ModelToWorld), vw, vh);
                        if (spSample.HasValue && InBox(spSample.Value) &&
                            !IsLocalPointOccluded(occlTris!, camera, sampleLocal, skipFaces: incFaces))
                        {
                            selected = true;
                            break;
                        }
                    }

                    if (!selected)
                    {
                        var sp0 = camera.WorldToScreen(Vector3.Transform(p0Local, ModelToWorld), vw, vh);
                        var sp1 = camera.WorldToScreen(Vector3.Transform(p1Local, ModelToWorld), vw, vh);
                        if (sp0.HasValue && InBox(sp0.Value) && sp1.HasValue && InBox(sp1.Value) &&
                            !IsLocalPointOccluded(occlTris!, camera, p0Local, skipVertIdx: e.V0) &&
                            !IsLocalPointOccluded(occlTris!, camera, p1Local, skipVertIdx: e.V1))
                        {
                            selected = true;
                        }
                    }

                    if (selected)
                        Selection.Edges.Add(ei);
                }
                Selection.SyncFromEdges(Mesh);
                break;
            }

            case EditElementMode.Face:
                for (int fi = 0; fi < Mesh.Faces.Count; fi++)
                {
                    Vector3 center = ComputeFaceCenterLocal(Mesh, Mesh.Faces[fi]);
                    var sp = camera.WorldToScreen(Vector3.Transform(center, ModelToWorld), vw, vh);
                    if (!sp.HasValue || !InBox(sp.Value)) continue;
                    if (XRayEnabled || !IsLocalPointOccluded(occlTris!, camera, center, skipFaceIdx: fi))
                        Selection.Faces.Add(fi);
                }
                Selection.SyncFromFaces(Mesh);
                break;
        }

        return true;
    }

    private readonly struct OcclusionTri
    {
        public readonly Vector3 P0, P1, P2;
        public readonly Vector3 Min, Max;
        public readonly int FaceIndex;
        public readonly int V0, V1, V2;

        public OcclusionTri(Vector3 p0, Vector3 p1, Vector3 p2, int faceIndex, int v0, int v1, int v2)
        {
            P0 = p0; P1 = p1; P2 = p2;
            Min = Vector3.Min(p0, Vector3.Min(p1, p2));
            Max = Vector3.Max(p0, Vector3.Max(p1, p2));
            FaceIndex = faceIndex;
            V0 = v0; V1 = v1; V2 = v2;
        }
    }

    private List<OcclusionTri> BuildLocalOcclusionTriangles()
    {
        var tris = new List<OcclusionTri>(Mesh.Faces.Count * 2);
        for (int fi = 0; fi < Mesh.Faces.Count; fi++)
        {
            var vi = Mesh.Faces[fi].VertexIndices;
            int n = vi.Count;
            if (n < 3) continue;
            int i0 = vi[0];
            if (i0 < 0 || i0 >= Mesh.Vertices.Count) continue;
            Vector3 p0 = Mesh.Vertices[i0].Position;
            for (int k = 1; k + 1 < n; k++)
            {
                int i1 = vi[k];
                int i2 = vi[k + 1];
                if (i1 < 0 || i1 >= Mesh.Vertices.Count || i2 < 0 || i2 >= Mesh.Vertices.Count) continue;
                tris.Add(new OcclusionTri(p0, Mesh.Vertices[i1].Position, Mesh.Vertices[i2].Position, fi, i0, i1, i2));
            }
        }
        return tris;
    }

    private Dictionary<(int, int), HashSet<int>> BuildEdgeIncidentFaceMap()
    {
        var map = new Dictionary<(int, int), HashSet<int>>(Mesh.Edges.Count);
        for (int fi = 0; fi < Mesh.Faces.Count; fi++)
        {
            var vi = Mesh.Faces[fi].VertexIndices;
            int n = vi.Count;
            if (n < 2) continue;
            for (int k = 0; k < n; k++)
            {
                int a = vi[k];
                int b = vi[(k + 1) % n];
                var key = a <= b ? (a, b) : (b, a);
                if (!map.TryGetValue(key, out var set))
                {
                    set = new HashSet<int>();
                    map[key] = set;
                }
                set.Add(fi);
            }
        }
        return map;
    }

    private bool IsLocalPointOccluded(
        List<OcclusionTri> tris,
        ViewportCamera camera,
        Vector3 pLocal,
        int skipFaceIdx = -1,
        int skipVertIdx = -1,
        HashSet<int>? skipFaces = null)
    {
        if (tris.Count == 0) return false;

        Vector3 rayOriginLocal;
        if (!camera.IsOrthographic)
        {
            rayOriginLocal = Vector3.Transform(camera.CameraPosition, WorldToModel);
        }
        else
        {
            camera.GetCameraAxes(out _, out _, out var camFwd);
            Vector3 pWorld = Vector3.Transform(pLocal, ModelToWorld);
            float backDist = MathF.Max(camera.Distance * 4f, 10000f);
            rayOriginLocal = Vector3.Transform(pWorld - camFwd * backDist, WorldToModel);
        }

        Vector3 toTarget = pLocal - rayOriginLocal;
        float targetDist = toTarget.Length();
        if (targetDist <= 1e-5f) return false;
        Vector3 dir = toTarget / targetDist;
        Vector3 invDir = new(
            MathF.Abs(dir.X) > 1e-12f ? 1f / dir.X : 1e30f,
            MathF.Abs(dir.Y) > 1e-12f ? 1f / dir.Y : 1e30f,
            MathF.Abs(dir.Z) > 1e-12f ? 1f / dir.Z : 1e30f);

        float maxT = targetDist - MathF.Max(1e-4f, targetDist * 1.5e-3f);
        if (maxT <= 1e-5f) return false;

        for (int i = 0; i < tris.Count; i++)
        {
            var tri = tris[i];
            if (skipFaceIdx >= 0 && tri.FaceIndex == skipFaceIdx) continue;
            if (skipVertIdx >= 0 && (tri.V0 == skipVertIdx || tri.V1 == skipVertIdx || tri.V2 == skipVertIdx)) continue;
            if (skipFaces != null && skipFaces.Contains(tri.FaceIndex)) continue;

            if (!IntersectsRayAabb(rayOriginLocal, invDir, tri.Min, tri.Max, maxT))
                continue;

            if (IntersectRayTriangleInclusive(rayOriginLocal, dir, tri.P0, tri.P1, tri.P2, out float tHit) &&
                tHit > 1e-5f && tHit < maxT)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IntersectsRayAabb(Vector3 origin, Vector3 invDir, Vector3 bmin, Vector3 bmax, float maxT)
    {
        const float pad = 1e-4f;
        float tx1 = (bmin.X - pad - origin.X) * invDir.X;
        float tx2 = (bmax.X + pad - origin.X) * invDir.X;
        float tmin = MathF.Min(tx1, tx2);
        float tmax = MathF.Max(tx1, tx2);

        float ty1 = (bmin.Y - pad - origin.Y) * invDir.Y;
        float ty2 = (bmax.Y + pad - origin.Y) * invDir.Y;
        tmin = MathF.Max(tmin, MathF.Min(ty1, ty2));
        tmax = MathF.Min(tmax, MathF.Max(ty1, ty2));

        float tz1 = (bmin.Z - pad - origin.Z) * invDir.Z;
        float tz2 = (bmax.Z + pad - origin.Z) * invDir.Z;
        tmin = MathF.Max(tmin, MathF.Min(tz1, tz2));
        tmax = MathF.Min(tmax, MathF.Max(tz1, tz2));

        return tmax >= MathF.Max(0f, tmin) && tmin <= maxT;
    }

    private static bool IntersectRayTriangleInclusive(
        Vector3 orig, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0f;
        const float edgeEps = 1e-4f;
        Vector3 e1 = v1 - v0;
        Vector3 e2 = v2 - v0;
        Vector3 pvec = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, pvec);
        if (MathF.Abs(det) < 1e-7f) return false;

        float invDet = 1f / det;
        Vector3 tvec = orig - v0;
        float u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < -edgeEps || u > 1f + edgeEps) return false;

        Vector3 qvec = Vector3.Cross(tvec, e1);
        float v = Vector3.Dot(dir, qvec) * invDet;
        if (v < -edgeEps || u + v > 1f + edgeEps) return false;

        t = Vector3.Dot(e2, qvec) * invDet;
        return true;
    }

    #endregion

    #region モーダル変形 (G / R / S)

    public bool TryBeginModalTransform(ModalOpKind op, Vector2 mousePx, Vector3? initialLocalAxis = null)
    {
        if (!IsActive || Selection.Vertices.Count == 0) return false;
        if (op != ModalOpKind.Grab && op != ModalOpKind.Rotate && op != ModalOpKind.Scale) return false;

        if (IsGizmoDragging) CancelGizmoDrag();
        if (IsModalOrLoopCut) CancelModalOrLoopCut();

        PushUndo(op switch
        {
            ModalOpKind.Grab => "移動 (G)",
            ModalOpKind.Rotate => "回転 (R)",
            ModalOpKind.Scale => "拡大縮小 (S)",
            _ => "変形"
        });

        StartModalOpCore(op, mousePx, initialLocalAxis);
        return true;
    }

    private void StartModalOpCore(ModalOpKind op, Vector2 mousePx, Vector3? customAxisWorld = null)
    {
        ActiveModalOp = op;
        AxisConstraint = AxisConstraintMode.None;
        ConstraintSpace = AxisConstraintSpace.World;
        _modalNumericBuffer = string.Empty;
        _modalStartMousePx = mousePx;
        _modalLastMousePx = mousePx;
        _modalAccumAngleRad = 0f;
        _customModalAxisWorld = customAxisWorld;
        if (customAxisWorld.HasValue)
            AxisConstraint = AxisConstraintMode.Z; // customAxisWorld があるときは独自軸モード扱い (_customModalAxisWorld 優先)

        _modalStartLocalPos.Clear();
        Vector3 sumWorld = Vector3.Zero;
        int n = 0;
        foreach (int vi in Selection.Vertices)
        {
            if (vi < 0 || vi >= Mesh.Vertices.Count) continue;
            var lp = Mesh.Vertices[vi].Position;
            _modalStartLocalPos[vi] = lp;
            sumWorld += Vector3.Transform(lp, ModelToWorld);
            n++;
        }
        SnapshotAndClearTouchedCustomNormals(_modalStartLocalPos.Keys);
        _modalPivotWorld = n > 0 ? sumWorld / n : ModelToWorld.Translation;
    }

    private Vector3? _customModalAxisWorld;

    public void ToggleAxisConstraint(AxisConstraintMode singleAxis, bool excludePlane)
    {
        if (ActiveModalOp is not (ModalOpKind.Grab or ModalOpKind.Rotate or ModalOpKind.Scale))
            return;

        _customModalAxisWorld = null;
        AxisConstraintMode target = excludePlane
            ? singleAxis switch
            {
                AxisConstraintMode.X => AxisConstraintMode.ExcludeX,
                AxisConstraintMode.Y => AxisConstraintMode.ExcludeY,
                AxisConstraintMode.Z => AxisConstraintMode.ExcludeZ,
                _ => AxisConstraintMode.None
            }
            : singleAxis;

        if (AxisConstraint != target)
        {
            AxisConstraint = target;
            ConstraintSpace = AxisConstraintSpace.World;
        }
        else if (ConstraintSpace == AxisConstraintSpace.World)
        {
            ConstraintSpace = AxisConstraintSpace.Local;
        }
        else
        {
            AxisConstraint = AxisConstraintMode.None;
            ConstraintSpace = AxisConstraintSpace.World;
        }
    }

    public bool TryHandleModalNumericKey(Key key)
    {
        if (ActiveModalOp is not (ModalOpKind.Grab or ModalOpKind.Rotate or ModalOpKind.Scale))
            return false;

        if (key == Key.Back)
        {
            if (_modalNumericBuffer.Length > 0)
            {
                _modalNumericBuffer = _modalNumericBuffer[..^1];
                return true;
            }
            return false;
        }

        if (key == Key.OemMinus || key == Key.Subtract)
        {
            if (_modalNumericBuffer.StartsWith("-", StringComparison.Ordinal))
                _modalNumericBuffer = _modalNumericBuffer[1..];
            else
                _modalNumericBuffer = "-" + _modalNumericBuffer;
            return true;
        }

        if (key == Key.OemPeriod || key == Key.Decimal)
        {
            if (!_modalNumericBuffer.Contains('.'))
            {
                if (_modalNumericBuffer.Length == 0 || _modalNumericBuffer == "-")
                    _modalNumericBuffer += "0.";
                else
                    _modalNumericBuffer += ".";
            }
            return true;
        }

        char digit = key switch
        {
            Key.D0 or Key.NumPad0 => '0',
            Key.D1 or Key.NumPad1 => '1',
            Key.D2 or Key.NumPad2 => '2',
            Key.D3 or Key.NumPad3 => '3',
            Key.D4 or Key.NumPad4 => '4',
            Key.D5 or Key.NumPad5 => '5',
            Key.D6 or Key.NumPad6 => '6',
            Key.D7 or Key.NumPad7 => '7',
            Key.D8 or Key.NumPad8 => '8',
            Key.D9 or Key.NumPad9 => '9',
            _ => '\0'
        };

        if (digit != '\0' && _modalNumericBuffer.Length < 12)
        {
            _modalNumericBuffer += digit;
            return true;
        }

        return false;
    }

    public void UpdateModalTransform(Vector2 mousePx, ViewportCamera camera, float vw, float vh, ModifierKeys mods)
    {
        if (ActiveModalOp is not (ModalOpKind.Grab or ModalOpKind.Rotate or ModalOpKind.Scale))
            return;

        if (ActiveModalOp == ModalOpKind.Rotate)
        {
            var pivotPx = camera.WorldToScreen(_modalPivotWorld, vw, vh) ?? _modalStartMousePx;
            Vector2 prevDir = _modalLastMousePx - pivotPx;
            Vector2 curDir = mousePx - pivotPx;
            if (prevDir.LengthSquared() > 1f && curDir.LengthSquared() > 1f)
            {
                float dAng = MathF.Atan2(curDir.Y, curDir.X) - MathF.Atan2(prevDir.Y, prevDir.X);
                if (dAng > MathF.PI) dAng -= MathF.Tau;
                else if (dAng < -MathF.PI) dAng += MathF.Tau;
                float fine = (mods & ModifierKeys.Shift) != 0 ? 0.25f : 1f;
                _modalAccumAngleRad += dAng * fine;
            }
        }

        _modalLastMousePx = mousePx;
        ApplyModalTransformWithCurrentInputs(camera, vw, vh, mods);
    }

    public void ReapplyModalAfterConstraintOrNumeric(ViewportCamera camera, float vw, float vh, ModifierKeys mods)
    {
        if (ActiveModalOp is not (ModalOpKind.Grab or ModalOpKind.Rotate or ModalOpKind.Scale))
            return;

        ApplyModalTransformWithCurrentInputs(camera, vw, vh, mods);
    }

    private void ApplyModalTransformWithCurrentInputs(ViewportCamera camera, float vw, float vh, ModifierKeys mods)
    {
        bool hasNum = float.TryParse(_modalNumericBuffer, NumberStyles.Float, CultureInfo.InvariantCulture, out float numVal);
        bool snap = (mods & ModifierKeys.Control) != 0;
        bool fine = (mods & ModifierKeys.Shift) != 0 && AxisConstraint == AxisConstraintMode.None;

        camera.GetCameraAxes(out var camRight, out _, out var camFwd);

        Vector3 axisWorld = ResolveConstraintAxisWorld(camera);

        switch (ActiveModalOp)
        {
            case ModalOpKind.Grab:
            {
                Vector3 worldDelta = Vector3.Zero;
                if (hasNum)
                {
                    Vector3 dir = (AxisConstraint == AxisConstraintMode.None) ? camRight : axisWorld;
                    worldDelta = dir * numVal;
                }
                else
                {
                    var r0 = camera.ScreenToRay(_modalStartMousePx.X, _modalStartMousePx.Y, vw, vh);
                    var r1 = camera.ScreenToRay(_modalLastMousePx.X, _modalLastMousePx.Y, vw, vh);

                    if (_customModalAxisWorld.HasValue || AxisConstraint is AxisConstraintMode.X or AxisConstraintMode.Y or AxisConstraintMode.Z)
                    {
                        var t0 = r0.ClosestParameterOnAxis(_modalPivotWorld, axisWorld);
                        var t1 = r1.ClosestParameterOnAxis(_modalPivotWorld, axisWorld);
                        if (t0.HasValue && t1.HasValue)
                        {
                            float deltaS = t1.Value - t0.Value;
                            if (fine) deltaS *= 0.2f;
                            if (snap) deltaS = MathF.Round(deltaS / 10f) * 10f;
                            worldDelta = axisWorld * deltaS;
                        }
                    }
                    else
                    {
                        Vector3 planeNorm = AxisConstraint is AxisConstraintMode.ExcludeX or AxisConstraintMode.ExcludeY or AxisConstraintMode.ExcludeZ
                            ? axisWorld
                            : camFwd;
                        var h0 = r0.IntersectPlaneUnbounded(_modalPivotWorld, planeNorm);
                        var h1 = r1.IntersectPlaneUnbounded(_modalPivotWorld, planeNorm);
                        if (h0.HasValue && h1.HasValue)
                        {
                            worldDelta = h1.Value - h0.Value;
                            if (fine) worldDelta *= 0.2f;
                            if (snap)
                            {
                                worldDelta = new Vector3(
                                    MathF.Round(worldDelta.X / 10f) * 10f,
                                    MathF.Round(worldDelta.Y / 10f) * 10f,
                                    MathF.Round(worldDelta.Z / 10f) * 10f);
                            }
                        }
                    }
                }

                foreach (var kv in _modalStartLocalPos)
                {
                    Vector3 wStart = Vector3.Transform(kv.Value, ModelToWorld);
                    Mesh.Vertices[kv.Key].Position = Vector3.Transform(wStart + worldDelta, WorldToModel);
                }
                break;
            }

            case ModalOpKind.Rotate:
            {
                Vector3 rotAxis = (AxisConstraint == AxisConstraintMode.None && !_customModalAxisWorld.HasValue)
                    ? camFwd
                    : axisWorld;

                float angleRad = hasNum
                    ? numVal * (MathF.PI / 180f)
                    : _modalAccumAngleRad;

                if (!hasNum && snap)
                {
                    float step = 5f * (MathF.PI / 180f);
                    angleRad = MathF.Round(angleRad / step) * step;
                }

                var qRot = Matrix4x4.CreateFromAxisAngle(rotAxis, angleRad);
                foreach (var kv in _modalStartLocalPos)
                {
                    Vector3 wStart = Vector3.Transform(kv.Value, ModelToWorld);
                    Vector3 rel = Vector3.TransformNormal(wStart - _modalPivotWorld, qRot);
                    Mesh.Vertices[kv.Key].Position = Vector3.Transform(_modalPivotWorld + rel, WorldToModel);
                }
                break;
            }

            case ModalOpKind.Scale:
            {
                float s = 1f;
                if (hasNum)
                {
                    s = numVal;
                }
                else
                {
                    var pivotPx = camera.WorldToScreen(_modalPivotWorld, vw, vh) ?? _modalStartMousePx;
                    Vector2 d0 = _modalStartMousePx - pivotPx;
                    Vector2 d1 = _modalLastMousePx - pivotPx;
                    float len0 = d0.Length();
                    if (len0 > 6f)
                    {
                        s = Vector2.Dot(d1, d0) / (len0 * len0);
                    }
                    else
                    {
                        s = 1f + (_modalLastMousePx.X - _modalStartMousePx.X) * 0.01f;
                    }
                    if (snap) s = MathF.Round(s * 10f) * 0.1f;
                }

                bool singleAxis = _customModalAxisWorld.HasValue || AxisConstraint is AxisConstraintMode.X or AxisConstraintMode.Y or AxisConstraintMode.Z;
                bool excludeAxis = !_customModalAxisWorld.HasValue && AxisConstraint is AxisConstraintMode.ExcludeX or AxisConstraintMode.ExcludeY or AxisConstraintMode.ExcludeZ;

                foreach (var kv in _modalStartLocalPos)
                {
                    Vector3 wStart = Vector3.Transform(kv.Value, ModelToWorld);
                    Vector3 rel = wStart - _modalPivotWorld;

                    if (singleAxis)
                    {
                        float proj = Vector3.Dot(rel, axisWorld);
                        rel += axisWorld * (proj * (s - 1f));
                    }
                    else if (excludeAxis)
                    {
                        Vector3 axial = axisWorld * Vector3.Dot(rel, axisWorld);
                        rel = axial + (rel - axial) * s;
                    }
                    else
                    {
                        rel *= s;
                    }

                    Mesh.Vertices[kv.Key].Position = Vector3.Transform(_modalPivotWorld + rel, WorldToModel);
                }
                break;
            }
        }

        Mesh.RecalculateNormals();
        InvalidateGpuCache();
    }

    public void ConfirmModalOrLoopCut(bool zeroSlideIfLoopCut = false)
    {
        if (ActiveModalOp is ModalOpKind.LoopCutPickRing or ModalOpKind.LoopCutSlide)
        {
            if (_activeRing != null && _activeRing.Steps.Count > 0)
            {
                PushUndo("ループカット (Ctrl+R)");
                var ringVerts = new HashSet<int>();
                foreach (var st in _activeRing.Steps)
                {
                    ringVerts.Add(st.EnterA);
                    ringVerts.Add(st.EnterB);
                    ringVerts.Add(st.ExitA);
                    ringVerts.Add(st.ExitB);
                }
                Mesh.ClearCustomNormalsForVertices(ringVerts);
                MeshEditOperations.ApplyLoopCut(Mesh, Selection, _activeRing, _loopCutCount, zeroSlideIfLoopCut ? 0f : _loopCutSlide);
                Mesh.ClearCustomNormalsForVertices(Selection.Vertices);
                CommitToParameter();
                InvalidateGpuCache();
            }
            ResetTransientOpState();
            return;
        }

        if (ActiveModalOp is ModalOpKind.Grab or ModalOpKind.Rotate or ModalOpKind.Scale)
        {
            Mesh.RecalculateNormals();
            CommitToParameter();
            InvalidateGpuCache();
            ResetTransientOpState();
        }
    }

    public void CancelModalOrLoopCut()
    {
        if (ActiveModalOp is ModalOpKind.Grab or ModalOpKind.Rotate or ModalOpKind.Scale)
        {
            foreach (var kv in _modalStartLocalPos)
            {
                if (kv.Key >= 0 && kv.Key < Mesh.Vertices.Count)
                    Mesh.Vertices[kv.Key].Position = kv.Value;
            }
            RestoreSavedCustomNormals();
            Mesh.RecalculateNormals();
            InvalidateGpuCache();
        }
        ResetTransientOpState();
    }

    private void ResetTransientOpState()
    {
        ActiveModalOp = ModalOpKind.None;
        AxisConstraint = AxisConstraintMode.None;
        ConstraintSpace = AxisConstraintSpace.World;
        _customModalAxisWorld = null;
        _modalStartLocalPos.Clear();
        _modalSavedCornerNormals.Clear();
        _modalNumericBuffer = string.Empty;
        _activeRing = null;
        _loopCutCount = 1;
        _loopCutSlide = 0f;
        _loopCutScreenAxis = Vector2.UnitX;
        _loopCutHalfSpanPx = 140f;
    }

    private Vector3 ResolveConstraintAxisWorld(ViewportCamera camera)
    {
        if (_customModalAxisWorld.HasValue)
        {
            float l = _customModalAxisWorld.Value.Length();
            return l > 1e-6f ? _customModalAxisWorld.Value / l : Vector3.UnitY;
        }

        Vector3 baseUnit = AxisConstraint switch
        {
            AxisConstraintMode.X or AxisConstraintMode.ExcludeX => Vector3.UnitX,
            AxisConstraintMode.Y or AxisConstraintMode.ExcludeY => Vector3.UnitY,
            AxisConstraintMode.Z or AxisConstraintMode.ExcludeZ => Vector3.UnitZ,
            _ => Vector3.UnitY
        };

        if (ConstraintSpace == AxisConstraintSpace.Local)
        {
            Vector3 localWorld = Vector3.TransformNormal(baseUnit, ModelToWorld);
            float len = localWorld.Length();
            if (len > 1e-6f) return localWorld / len;
        }

        return baseUnit;
    }

    #endregion

    #region 編集ギズモドラッグ

    public bool TryComputeEditGizmo(ViewportCamera camera, GizmoSpace space, out TransformGizmoState gizmo)
    {
        gizmo = default;
        if (!IsActive || IsModalOrLoopCut || Selection.Vertices.Count == 0)
            return false;

        Vector3 sum = Vector3.Zero;
        int n = 0;
        foreach (int vi in Selection.Vertices)
        {
            if (vi >= 0 && vi < Mesh.Vertices.Count)
            {
                sum += Vector3.Transform(Mesh.Vertices[vi].Position, ModelToWorld);
                n++;
            }
        }
        if (n == 0) return false;

        Vector3 pivotWorld = sum / n;
        Matrix4x4 orientation = space == GizmoSpace.Local
            ? ComputeModelOrientationWorld()
            : Matrix4x4.Identity;

        gizmo = TransformGizmoState.Create(pivotWorld, orientation, camera);
        return true;
    }

    private Matrix4x4 ComputeModelOrientationWorld()
    {
        var yFlip = Matrix4x4.CreateScale(1f, -1f, 1f);
        var noReflect = yFlip * ModelToWorld;
        return RotationEulerMath.Orthonormalize(noReflect);
    }

    public bool TryBeginGizmoDrag(GizmoHandle handle, Vector2 mousePx, ViewportCamera camera, float vw, float vh, in TransformGizmoState gizmo)
    {
        if (handle == GizmoHandle.None || !IsActive || Selection.Vertices.Count == 0)
            return false;

        PushUndo(EditGizmoMode switch
        {
            GizmoMode.Rotate => "ギズモ回転",
            GizmoMode.Scale => "ギズモ拡大縮小",
            _ => "ギズモ移動"
        });

        _activeGizmoHandle = handle;
        _dragGizmo = gizmo;
        _modalPivotWorld = gizmo.Origin;
        _modalStartMousePx = mousePx;
        _modalLastMousePx = mousePx;

        _modalStartLocalPos.Clear();
        foreach (int vi in Selection.Vertices)
        {
            if (vi >= 0 && vi < Mesh.Vertices.Count)
                _modalStartLocalPos[vi] = Mesh.Vertices[vi].Position;
        }
        SnapshotAndClearTouchedCustomNormals(_modalStartLocalPos.Keys);

        var ray = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);
        camera.GetCameraAxes(out _, out _, out var camFwd);

        if (handle is GizmoHandle.MoveX or GizmoHandle.MoveY or GizmoHandle.MoveZ)
        {
            _gizmoDragAxis = gizmo.GetAxisVector(handle);
            _gizmoDragAxisStartParam = ray.ClosestParameterOnAxis(gizmo.Origin, _gizmoDragAxis) ?? 0f;
        }
        else if (handle is GizmoHandle.PlaneXY or GizmoHandle.PlaneYZ or GizmoHandle.PlaneZX or GizmoHandle.MoveFree)
        {
            _gizmoDragPlanePoint = gizmo.Origin;
            _gizmoDragPlaneNormal = handle == GizmoHandle.MoveFree ? camFwd : gizmo.GetPlaneNormal(handle);
            _gizmoDragHitStart = ray.IntersectPlaneUnbounded(_gizmoDragPlanePoint, _gizmoDragPlaneNormal) ?? gizmo.Origin;
        }
        else if (handle is GizmoHandle.RotateX or GizmoHandle.RotateY or GizmoHandle.RotateZ)
        {
            _gizmoDragAxis = gizmo.GetAxisVector(handle);
            _gizmoDragPlanePoint = gizmo.Origin;
            _gizmoDragPlaneNormal = _gizmoDragAxis;

            Vector3 hint = MathF.Abs(_gizmoDragAxis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
            _gizmoDragTurnRight = Vector3.Normalize(Vector3.Cross(hint, _gizmoDragAxis));
            _gizmoDragTurnUp = Vector3.Cross(_gizmoDragAxis, _gizmoDragTurnRight);

            var hit = ray.IntersectPlaneUnbounded(_gizmoDragPlanePoint, _gizmoDragPlaneNormal);
            if (hit.HasValue)
            {
                Vector3 rel = hit.Value - gizmo.Origin;
                _gizmoDragStartAngle = MathF.Atan2(Vector3.Dot(rel, _gizmoDragTurnUp), Vector3.Dot(rel, _gizmoDragTurnRight));
            }
            else
            {
                _gizmoDragStartAngle = 0f;
            }
        }
        else
        {
            _gizmoDragPlanePoint = gizmo.Origin;
            _gizmoDragPlaneNormal = camFwd;
            _gizmoDragHitStart = ray.IntersectPlaneUnbounded(_gizmoDragPlanePoint, _gizmoDragPlaneNormal) ?? gizmo.Origin;
            _gizmoDragAxis = handle == GizmoHandle.ScaleUniform ? Vector3.One : gizmo.GetAxisVector(handle);
            _gizmoDragScaleStartDist = MathF.Max(10f, Vector3.Distance(_gizmoDragHitStart, gizmo.Origin));
        }

        return true;
    }

    public void UpdateGizmoDrag(Vector2 mousePx, ViewportCamera camera, float vw, float vh, ModifierKeys mods)
    {
        if (!IsGizmoDragging) return;

        bool fine = (mods & ModifierKeys.Shift) != 0;
        bool snap = (mods & ModifierKeys.Control) != 0;
        var ray = camera.ScreenToRay(mousePx.X, mousePx.Y, vw, vh);

        switch (_activeGizmoHandle)
        {
            case GizmoHandle.MoveX:
            case GizmoHandle.MoveY:
            case GizmoHandle.MoveZ:
            {
                var p = ray.ClosestParameterOnAxis(_dragGizmo.Origin, _gizmoDragAxis);
                if (!p.HasValue) return;
                float ds = p.Value - _gizmoDragAxisStartParam;
                if (fine) ds *= 0.2f;
                if (snap) ds = MathF.Round(ds / 10f) * 10f;
                Vector3 wd = _gizmoDragAxis * ds;
                foreach (var kv in _modalStartLocalPos)
                {
                    Mesh.Vertices[kv.Key].Position = Vector3.Transform(Vector3.Transform(kv.Value, ModelToWorld) + wd, WorldToModel);
                }
                break;
            }

            case GizmoHandle.PlaneXY:
            case GizmoHandle.PlaneYZ:
            case GizmoHandle.PlaneZX:
            case GizmoHandle.MoveFree:
            {
                var hit = ray.IntersectPlaneUnbounded(_gizmoDragPlanePoint, _gizmoDragPlaneNormal);
                if (!hit.HasValue) return;
                Vector3 wd = hit.Value - _gizmoDragHitStart;
                if (fine) wd *= 0.2f;
                if (snap)
                {
                    wd = new Vector3(
                        MathF.Round(wd.X / 10f) * 10f,
                        MathF.Round(wd.Y / 10f) * 10f,
                        MathF.Round(wd.Z / 10f) * 10f);
                }
                foreach (var kv in _modalStartLocalPos)
                {
                    Mesh.Vertices[kv.Key].Position = Vector3.Transform(Vector3.Transform(kv.Value, ModelToWorld) + wd, WorldToModel);
                }
                break;
            }

            case GizmoHandle.RotateX:
            case GizmoHandle.RotateY:
            case GizmoHandle.RotateZ:
            {
                var hit = ray.IntersectPlaneUnbounded(_gizmoDragPlanePoint, _gizmoDragPlaneNormal);
                if (!hit.HasValue) return;
                Vector3 rel = hit.Value - _gizmoDragPlanePoint;
                float ang = MathF.Atan2(Vector3.Dot(rel, _gizmoDragTurnUp), Vector3.Dot(rel, _gizmoDragTurnRight)) - _gizmoDragStartAngle;
                while (ang > MathF.PI) ang -= MathF.Tau;
                while (ang < -MathF.PI) ang += MathF.Tau;
                if (fine) ang *= 0.2f;
                if (snap)
                {
                    float s = 5f * (MathF.PI / 180f);
                    ang = MathF.Round(ang / s) * s;
                }
                var rot = Matrix4x4.CreateFromAxisAngle(_gizmoDragAxis, ang);
                foreach (var kv in _modalStartLocalPos)
                {
                    Vector3 wStart = Vector3.Transform(kv.Value, ModelToWorld);
                    Vector3 newW = _dragGizmo.Origin + Vector3.TransformNormal(wStart - _dragGizmo.Origin, rot);
                    Mesh.Vertices[kv.Key].Position = Vector3.Transform(newW, WorldToModel);
                }
                break;
            }

            default:
            {
                float ratio;
                if (_activeGizmoHandle == GizmoHandle.ScaleUniform)
                {
                    ratio = 1f + (mousePx.X - _modalStartMousePx.X) * 0.008f;
                }
                else
                {
                    var p = ray.ClosestParameterOnAxis(_dragGizmo.Origin, _gizmoDragAxis);
                    if (!p.HasValue) return;
                    float curDist = MathF.Max(1f, MathF.Abs(p.Value));
                    ratio = curDist / MathF.Max(10f, _dragGizmo.Scale * 0.88f);
                }
                if (fine) ratio = 1f + (ratio - 1f) * 0.25f;
                if (snap) ratio = MathF.Round(ratio * 10f) * 0.1f;

                foreach (var kv in _modalStartLocalPos)
                {
                    Vector3 wStart = Vector3.Transform(kv.Value, ModelToWorld);
                    Vector3 rel = wStart - _dragGizmo.Origin;
                    if (_activeGizmoHandle == GizmoHandle.ScaleUniform)
                        rel *= ratio;
                    else
                    {
                        float proj = Vector3.Dot(rel, _gizmoDragAxis);
                        rel += _gizmoDragAxis * (proj * (ratio - 1f));
                    }
                    Mesh.Vertices[kv.Key].Position = Vector3.Transform(_dragGizmo.Origin + rel, WorldToModel);
                }
                break;
            }
        }

        Mesh.RecalculateNormals();
        InvalidateGpuCache();
    }

    private void SnapshotAndClearTouchedCustomNormals(IEnumerable<int> vertexIndices)
    {
        _modalSavedCornerNormals.Clear();
        var touched = vertexIndices as HashSet<int> ?? new HashSet<int>(vertexIndices);
        if (touched.Count == 0) return;

        for (int fi = 0; fi < Mesh.Faces.Count; fi++)
        {
            var face = Mesh.Faces[fi];
            if (face.FaceCornerNormals == null) continue;
            for (int k = 0; k < face.VertexIndices.Count; k++)
            {
                if (touched.Contains(face.VertexIndices[k]))
                {
                    _modalSavedCornerNormals[fi] = new List<Vector3>(face.FaceCornerNormals);
                    face.FaceCornerNormals = null;
                    break;
                }
            }
        }
    }

    private void RestoreSavedCustomNormals()
    {
        if (_modalSavedCornerNormals.Count == 0) return;
        foreach (var kv in _modalSavedCornerNormals)
        {
            if (kv.Key >= 0 && kv.Key < Mesh.Faces.Count)
                Mesh.Faces[kv.Key].FaceCornerNormals = kv.Value != null ? new List<Vector3>(kv.Value) : null;
        }
        _modalSavedCornerNormals.Clear();
    }

    public void EndGizmoDrag()
    {
        if (!IsGizmoDragging) return;
        _activeGizmoHandle = GizmoHandle.None;
        _modalStartLocalPos.Clear();
        _modalSavedCornerNormals.Clear();
        Mesh.RecalculateNormals();
        CommitToParameter();
        InvalidateGpuCache();
    }

    public void CancelGizmoDrag()
    {
        if (!IsGizmoDragging) return;
        foreach (var kv in _modalStartLocalPos)
        {
            if (kv.Key >= 0 && kv.Key < Mesh.Vertices.Count)
                Mesh.Vertices[kv.Key].Position = kv.Value;
        }
        RestoreSavedCustomNormals();
        _activeGizmoHandle = GizmoHandle.None;
        _modalStartLocalPos.Clear();
        Mesh.RecalculateNormals();
        InvalidateGpuCache();
    }

    #endregion

    #region モデリングコマンド実行

    public void ToggleSelectAll()
    {
        if (!IsActive) return;
        MeshEditOperations.ToggleSelectAll(Mesh, Selection);
    }

    public void InvertSelection()
    {
        if (!IsActive) return;
        MeshEditOperations.InvertSelection(Mesh, Selection, SelectMode);
    }

    public void SelectLinked()
    {
        if (!IsActive) return;
        MeshEditOperations.SelectLinked(Mesh, Selection);
    }

    public bool ExtrudeAndBeginMove(Vector2 currentMousePx)
    {
        if (!IsActive) return false;
        PushUndo("押し出し / 頂点追加 (E)");

        Mesh.ClearCustomNormalsForVertices(Selection.Vertices);
        // 未選択時の頂点追加先はメッシュ基準中心
        Vector3 newVertDefault = Mesh.HasRefBounds ? Mesh.RefCenter : Vector3.Zero;
        if (!MeshEditOperations.ExtrudeWithMaterial(Mesh, Selection, SelectMode, newVertDefault, out var suggestedLocalNormal, ActiveMaterialIndex))
        {
            _undoStack.RemoveAt(_undoStack.Count - 1);
            return false;
        }
        Mesh.ClearCustomNormalsForVertices(Selection.Vertices);

        Vector3? axisWorld = null;
        if (suggestedLocalNormal.HasValue)
        {
            Vector3 wn = Vector3.TransformNormal(suggestedLocalNormal.Value, ModelToWorld);
            if (wn.LengthSquared() > 1e-8f) axisWorld = Vector3.Normalize(wn);
        }

        CommitToParameter();
        InvalidateGpuCache();

        if (currentMousePx.X >= 0f && currentMousePx.Y >= 0f)
        {
            StartModalOpCore(ModalOpKind.Grab, currentMousePx, axisWorld);
        }
        return true;
    }

    public bool MakeEdgeOrFace(ViewportCamera? camera = null)
    {
        if (!IsActive || Selection.Vertices.Count < 2) return false;
        PushUndo("辺 / 面の作成 (F)");

        Vector3? hintLocal = null;
        if (camera != null)
        {
            camera.GetCameraAxes(out _, out _, out var camFwd);
            Vector3 toCamLocal = Vector3.TransformNormal(-camFwd, WorldToModel);
            if (toCamLocal.LengthSquared() > 1e-8f) hintLocal = Vector3.Normalize(toCamLocal);
        }

        Mesh.ClearCustomNormalsForVertices(Selection.Vertices);
        if (!MeshEditOperations.MakeEdgeOrFaceWithMaterial(Mesh, Selection, hintLocal, ActiveMaterialIndex))
        {
            _undoStack.RemoveAt(_undoStack.Count - 1);
            return false;
        }
        Mesh.ClearCustomNormalsForVertices(Selection.Vertices);

        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public bool Delete(MeshDeleteMode mode)
    {
        if (!IsActive) return false;
        PushUndo($"削除 ({mode})");

        Mesh.ClearCustomNormals();
        if (!MeshEditOperations.Delete(Mesh, Selection, mode))
        {
            _undoStack.RemoveAt(_undoStack.Count - 1);
            return false;
        }

        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public bool BeginLoopCutMode(Vector2 mousePx, ViewportCamera camera, float vw, float vh)
    {
        if (!IsActive) return false;
        if (IsModalOrLoopCut) CancelModalOrLoopCut();

        ActiveModalOp = ModalOpKind.LoopCutPickRing;
        _loopCutCount = 1;
        _loopCutSlide = 0f;
        UpdateLoopCutHover(mousePx, camera, vw, vh);
        return true;
    }

    public void UpdateLoopCutHover(Vector2 mousePx, ViewportCamera camera, float vw, float vh)
    {
        if (ActiveModalOp == ModalOpKind.LoopCutPickRing)
        {
            int hitEdge = PickEdgeIndex(mousePx, camera, vw, vh, 24f);
            _activeRing = hitEdge >= 0 ? MeshEditOperations.TraceQuadEdgeRing(Mesh, hitEdge) : null;
        }
        else if (ActiveModalOp == ModalOpKind.LoopCutSlide)
        {
            Vector2 delta = mousePx - _modalStartMousePx;
            float along = Vector2.Dot(delta, _loopCutScreenAxis);
            _loopCutSlide = Math.Clamp(along / MathF.Max(16f, _loopCutHalfSpanPx), -0.95f, 0.95f);
        }
    }

    public void AdjustLoopCutCount(int delta)
    {
        if (ActiveModalOp is ModalOpKind.LoopCutPickRing or ModalOpKind.LoopCutSlide)
        {
            _loopCutCount = Math.Clamp(_loopCutCount + delta, 1, 24);
        }
    }

    public void HandleLoopCutLeftClick(Vector2 mousePx)
        => HandleLoopCutLeftClick(mousePx, null, 0f, 0f);

    public void HandleLoopCutLeftClick(Vector2 mousePx, ViewportCamera? camera, float vw, float vh)
    {
        if (ActiveModalOp == ModalOpKind.LoopCutPickRing)
        {
            if (_activeRing != null && _activeRing.Steps.Count > 0)
            {
                ActiveModalOp = ModalOpKind.LoopCutSlide;
                _modalStartMousePx = mousePx;
                _loopCutSlide = 0f;
                ComputeLoopCutScreenAxis(camera, vw, vh);
            }
            else
            {
                CancelModalOrLoopCut();
            }
        }
        else if (ActiveModalOp == ModalOpKind.LoopCutSlide)
        {
            ConfirmModalOrLoopCut(zeroSlideIfLoopCut: false);
        }
    }

    private void ComputeLoopCutScreenAxis(ViewportCamera? camera, float vw, float vh)
    {
        _loopCutScreenAxis = Vector2.UnitX;
        _loopCutHalfSpanPx = 140f;
        if (camera == null || vw <= 0f || vh <= 0f || _activeRing == null || _activeRing.Steps.Count == 0)
            return;

        Vector2 bestDir = Vector2.Zero;
        float bestLen = 0f;
        for (int i = 0; i < _activeRing.Steps.Count; i++)
        {
            var step = _activeRing.Steps[i];
            if (step.EnterA < 0 || step.EnterA >= Mesh.Vertices.Count ||
                step.EnterB < 0 || step.EnterB >= Mesh.Vertices.Count)
                continue;

            Vector3 wA = Vector3.Transform(Mesh.Vertices[step.EnterA].Position, ModelToWorld);
            Vector3 wB = Vector3.Transform(Mesh.Vertices[step.EnterB].Position, ModelToWorld);
            var sA = camera.WorldToScreen(wA, vw, vh);
            var sB = camera.WorldToScreen(wB, vw, vh);
            if (!sA.HasValue || !sB.HasValue) continue;

            Vector2 seg = sB.Value - sA.Value;
            float len = seg.Length();
            if (i == 0 && len >= 6f)
            {
                bestDir = seg;
                bestLen = len;
                break;
            }
            if (len > bestLen)
            {
                bestDir = seg;
                bestLen = len;
            }
        }

        if (bestLen >= 6f)
        {
            _loopCutScreenAxis = bestDir / bestLen;
            _loopCutHalfSpanPx = Math.Clamp(bestLen * 0.5f, 36f, 260f);
        }
    }

    public bool ApplyLoopCutImmediate(int edgeIndex, int cuts = 1, float slide = 0f)
    {
        if (!IsActive) return false;
        var ring = MeshEditOperations.TraceQuadEdgeRing(Mesh, edgeIndex);
        if (ring == null || ring.Steps.Count == 0) return false;

        PushUndo("ループカット");
        bool ok = MeshEditOperations.ApplyLoopCut(Mesh, Selection, ring, cuts, slide);
        if (!ok)
        {
            _undoStack.RemoveAt(_undoStack.Count - 1);
            return false;
        }
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public bool AddPrimitive(EditablePrimitiveType type)
    {
        if (!IsActive) return false;
        PushUndo($"メッシュ追加 ({type})");

        Vector3 center = Mesh.HasRefBounds ? Mesh.RefCenter : Vector3.Zero;
        float size = Mesh.RefScale > 1e-5f ? 0.5f / Mesh.RefScale : 1f;
        var added = EditableMesh.AppendPrimitiveDetailed(Mesh, type, center, size, materialIndex: ActiveMaterialIndex);
        if (added.Count == 0)
        {
            _undoStack.RemoveAt(_undoStack.Count - 1);
            return false;
        }

        Selection.Clear();
        foreach (int v in added) Selection.Vertices.Add(v);
        Selection.SyncFromVertices(Mesh);
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    #region マテリアル編集

    private string? _lastMatUndoTag;
    private long _lastMatUndoTicksMs;

    public int AddMaterialSlot(string? name = null)
    {
        if (!IsActive) return -1;
        PushUndo("マテリアルスロット追加");
        _lastMatUndoTag = null;

        string matName = string.IsNullOrWhiteSpace(name) ? $"Material.{Mesh.Materials.Count:D3}" : name;
        Mesh.Materials.Add(new EditableMaterial
        {
            Name = matName,
            BaseColor = Vector4.One,
            Roughness = 0.5f,
            SourcePartIndex = -1
        });
        int idx = Mesh.Materials.Count - 1;
        ActiveMaterialIndex = idx;
        CommitToParameter();
        InvalidateGpuCache();
        return idx;
    }

    public int DuplicateMaterialSlot(int index)
    {
        if (!IsActive || index < 0 || index >= Mesh.Materials.Count) return -1;
        PushUndo("マテリアルスロット複製");
        _lastMatUndoTag = null;

        var copy = Mesh.Materials[index].Clone();
        copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? $"Material.{Mesh.Materials.Count:D3}" : copy.Name + ".Copy";
        Mesh.Materials.Add(copy);
        int idx = Mesh.Materials.Count - 1;
        ActiveMaterialIndex = idx;
        CommitToParameter();
        InvalidateGpuCache();
        return idx;
    }

    public bool RemoveMaterialSlot(int index)
    {
        if (!IsActive || Mesh.Materials.Count <= 1 || index < 0 || index >= Mesh.Materials.Count)
            return false;

        PushUndo("マテリアルスロット削除");
        _lastMatUndoTag = null;

        Mesh.Materials.RemoveAt(index);
        foreach (var f in Mesh.Faces)
        {
            if (f.MaterialIndex == index) f.MaterialIndex = 0;
            else if (f.MaterialIndex > index) f.MaterialIndex--;
        }

        ActiveMaterialIndex = Math.Clamp(_activeMaterialIndex, 0, Mesh.Materials.Count - 1);
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public bool AssignMaterialToSelectedFaces(int matIndex)
    {
        if (!IsActive || Mesh.Materials.Count == 0 || Selection.Faces.Count == 0)
            return false;

        int clamped = Math.Clamp(matIndex, 0, Mesh.Materials.Count - 1);
        PushUndo("選択面へマテリアル割当");
        _lastMatUndoTag = null;

        foreach (int fi in Selection.Faces)
        {
            if (fi >= 0 && fi < Mesh.Faces.Count)
                Mesh.Faces[fi].MaterialIndex = clamped;
        }

        ActiveMaterialIndex = clamped;
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public int SelectFacesByMaterial(int matIndex)
    {
        if (!IsActive || Mesh.Materials.Count == 0) return 0;
        int clamped = Math.Clamp(matIndex, 0, Mesh.Materials.Count - 1);

        Selection.Clear();
        int count = 0;
        for (int fi = 0; fi < Mesh.Faces.Count; fi++)
        {
            if (Mesh.Faces[fi].MaterialIndex == clamped)
            {
                Selection.Faces.Add(fi);
                count++;
            }
        }
        Selection.SyncFromFaces(Mesh);
        ActiveMaterialIndex = clamped;
        return count;
    }

    public bool UpdateMaterial(int matIndex, Action<EditableMaterial> mutator, string? coalesceTag = null, bool pushUndo = true)
    {
        if (!IsActive || matIndex < 0 || matIndex >= Mesh.Materials.Count) return false;

        if (pushUndo)
        {
            long now = Environment.TickCount64;
            string fullTag = $"{matIndex}:{coalesceTag ?? string.Empty}";
            if (string.IsNullOrEmpty(coalesceTag) ||
                !string.Equals(_lastMatUndoTag, fullTag, StringComparison.Ordinal) ||
                (now - _lastMatUndoTicksMs) > 650 ||
                _undoStack.Count == 0)
            {
                PushUndo("マテリアル編集");
                _lastMatUndoTag = fullTag;
            }
            _lastMatUndoTicksMs = now;
        }

        mutator(Mesh.Materials[matIndex]);
        Mesh.Materials[matIndex].NodeGraph?.SyncPrincipledScalarsFromMaterial(Mesh.Materials[matIndex]);
        CommitToParameter();
        InvalidateMaterialGpuCache();
        return true;
    }

    public bool ClearMaterialTexture(int matIndex)
    {
        if (!IsActive || matIndex < 0 || matIndex >= Mesh.Materials.Count) return false;
        if (Mesh.Materials[matIndex].SourcePartIndex < 0) return false;

        PushUndo("元テクスチャ参照解除");
        Mesh.Materials[matIndex].SourcePartIndex = -1;
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public int CountFacesUsingMaterial(int matIndex)
    {
        int count = 0;
        for (int fi = 0; fi < Mesh.Faces.Count; fi++)
        {
            if (Mesh.Faces[fi].MaterialIndex == matIndex) count++;
        }
        return count;
    }

    public Shading.MaterialNodeGraph? EnsureActiveNodeGraph()
    {
        if (!IsActive || Mesh.Materials.Count == 0) return null;
        int idx = ActiveMaterialIndex;
        var mat = Mesh.Materials[idx];
        mat.NodeGraph ??= Shading.MaterialNodeGraph.CreateDefaultFromMaterial(mat);
        return mat.NodeGraph;
    }

    public bool UpdateMaterialNodeGraph(
        int matIndex,
        Action<Shading.MaterialNodeGraph> mutator,
        string? coalesceTag = null,
        bool pushUndo = true,
        string description = "ノード編集")
    {
        if (!IsActive || matIndex < 0 || matIndex >= Mesh.Materials.Count) return false;

        if (pushUndo)
        {
            long now = Environment.TickCount64;
            string fullTag = $"ng:{matIndex}:{coalesceTag ?? string.Empty}";
            if (string.IsNullOrEmpty(coalesceTag) ||
                !string.Equals(_lastMatUndoTag, fullTag, StringComparison.Ordinal) ||
                (now - _lastMatUndoTicksMs) > 650 ||
                _undoStack.Count == 0)
            {
                PushUndo(description);
                _lastMatUndoTag = fullTag;
            }
            _lastMatUndoTicksMs = now;
        }

        var mat = Mesh.Materials[matIndex];
        mat.NodeGraph ??= Shading.MaterialNodeGraph.CreateDefaultFromMaterial(mat);
        mutator(mat.NodeGraph);
        mat.NodeGraph.MirrorPrincipledScalarsToMaterial(mat);
        IsInteractiveMaterialPreview = false;
        CommitToParameter();
        InvalidateMaterialGpuCache();
        return true;
    }

    public bool UpdateMaterialNodeGraphInteractive(
        int matIndex,
        Action<Shading.MaterialNodeGraph> mutator,
        string? coalesceTag = null,
        bool pushUndo = true,
        string description = "ノード編集")
    {
        if (!IsActive || matIndex < 0 || matIndex >= Mesh.Materials.Count) return false;

        if (pushUndo)
        {
            long now = Environment.TickCount64;
            string fullTag = $"ng:{matIndex}:{coalesceTag ?? string.Empty}";
            if (string.IsNullOrEmpty(coalesceTag) ||
                !string.Equals(_lastMatUndoTag, fullTag, StringComparison.Ordinal) ||
                (now - _lastMatUndoTicksMs) > 650 ||
                _undoStack.Count == 0)
            {
                PushUndo(description);
                _lastMatUndoTag = fullTag;
            }
            _lastMatUndoTicksMs = now;
        }

        var mat = Mesh.Materials[matIndex];
        mat.NodeGraph ??= Shading.MaterialNodeGraph.CreateDefaultFromMaterial(mat);
        mutator(mat.NodeGraph);
        mat.NodeGraph.MirrorPrincipledScalarsToMaterial(mat);
        IsInteractiveMaterialPreview = true;
        InvalidateMaterialGpuCache();
        return true;
    }

    public void FlushInteractiveMaterialPreview()
    {
        if (!IsActive) return;
        IsInteractiveMaterialPreview = false;
        InvalidateMaterialGpuCache();
        CommitToParameter();
    }

    public bool ApplyNodeGraphPreset(int matIndex, string presetKey)
    {
        if (!IsActive || matIndex < 0 || matIndex >= Mesh.Materials.Count) return false;
        PushUndo($"ノードプリセット ({presetKey})");
        var mat = Mesh.Materials[matIndex];
        mat.NodeGraph = Shading.MaterialNodeGraph.CreatePreset(presetKey, mat);
        mat.NodeGraph.MirrorPrincipledScalarsToMaterial(mat);
        IsInteractiveMaterialPreview = false;
        CommitToParameter();
        InvalidateMaterialGpuCache();
        return true;
    }

    #endregion

    #region モディファイア編集

    private string? _lastModUndoTag;
    private long _lastModUndoTicksMs;

    public MeshModifierItem? AddModifier(
        MeshModifierType type,
        Action<MeshModifierItem>? init = null)
    {
        if (!IsActive) return null;
        PushUndo($"モディファイア追加 ({MeshModifierItem.GetTypeDisplayName(type)})");
        var added = Modifiers.AddModifier(type);
        init?.Invoke(added);
        CommitToParameter();
        InvalidateGpuCache();
        return added;
    }

    public bool RemoveModifier(int modifierId)
    {
        if (!IsActive) return false;
        int idx = Modifiers.Items.FindIndex(m => m.Id == modifierId);
        if (idx < 0) return false;
        PushUndo($"モディファイア削除 ({Modifiers.Items[idx].Name})");
        Modifiers.Items.RemoveAt(idx);
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public bool MoveModifier(int modifierId, int delta)
    {
        if (!IsActive) return false;
        int idx = Modifiers.Items.FindIndex(m => m.Id == modifierId);
        int nextIdx = idx + delta;
        if (idx < 0 || nextIdx < 0 || nextIdx >= Modifiers.Items.Count || idx == nextIdx) return false;
        PushUndo("モディファイア順変更");
        var item = Modifiers.Items[idx];
        Modifiers.Items.RemoveAt(idx);
        Modifiers.Items.Insert(nextIdx, item);
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public bool ToggleModifier(int modifierId)
    {
        if (!IsActive) return false;
        int idx = Modifiers.Items.FindIndex(m => m.Id == modifierId);
        if (idx < 0) return false;
        PushUndo("モディファイア表示切替");
        Modifiers.Items[idx].Enabled = !Modifiers.Items[idx].Enabled;
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public bool ToggleModifierExpanded(int modifierId)
    {
        if (!IsActive) return false;
        int idx = Modifiers.Items.FindIndex(m => m.Id == modifierId);
        if (idx < 0) return false;
        Modifiers.Items[idx].Expanded = !Modifiers.Items[idx].Expanded;
        CommitToParameter();
        return true;
    }

    public bool UpdateModifier(
        int modifierId,
        Action<MeshModifierItem> mutator,
        string? coalesceTag = null,
        bool pushUndo = true)
    {
        if (!IsActive) return false;
        int idx = Modifiers.Items.FindIndex(m => m.Id == modifierId);
        if (idx < 0) return false;

        if (pushUndo)
        {
            long now = Environment.TickCount64;
            string fullTag = $"mod:{modifierId}:{coalesceTag ?? string.Empty}";
            if (string.IsNullOrEmpty(coalesceTag) ||
                !string.Equals(_lastModUndoTag, fullTag, StringComparison.Ordinal) ||
                (now - _lastModUndoTicksMs) > 650 ||
                _undoStack.Count == 0)
            {
                PushUndo($"モディファイア編集 ({Modifiers.Items[idx].Name})");
                _lastModUndoTag = fullTag;
            }
            _lastModUndoTicksMs = now;
        }

        mutator(Modifiers.Items[idx]);
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public bool ApplyModifier(
        int modifierId,
        SceneModifierContext? sceneCtx = null,
        int selfLayer = -1)
    {
        if (!IsActive) return false;
        int idx = Modifiers.Items.FindIndex(m => m.Id == modifierId);
        if (idx < 0) return false;

        var mod = Modifiers.Items[idx];
        PushUndo($"モディファイア適用 ({mod.Name})");

        var baked = MeshModifierEvaluator.EvaluateSingle(
            Mesh, mod, sceneCtx, selfLayer, ModelToWorld, ignoreEnabled: true);
        Mesh = baked;
        Modifiers.Items.RemoveAt(idx);
        Selection.Clear();
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    #endregion

    public void SetSelectionSmooth(bool smooth)
    {
        if (!IsActive || Mesh.Faces.Count == 0) return;
        PushUndo(smooth ? "スムーズシェード" : "フラットシェード");

        if (Selection.Faces.Count > 0)
        {
            foreach (int fi in Selection.Faces)
            {
                if (fi >= 0 && fi < Mesh.Faces.Count)
                {
                    Mesh.Faces[fi].FaceCornerNormals = null;
                    Mesh.Faces[fi].Smooth = smooth;
                }
            }
        }
        else
        {
            foreach (var f in Mesh.Faces)
            {
                f.FaceCornerNormals = null;
                f.Smooth = smooth;
            }
        }

        Mesh.RecalculateNormals();
        CommitToParameter();
        InvalidateGpuCache();
    }

    public void ApplyAutoSmooth(float angleDegrees = 35f)
    {
        if (!IsActive || Mesh.Faces.Count == 0) return;
        PushUndo($"自動スムーズ ({angleDegrees:0}°)");
        Mesh.ApplyAutoSmooth(angleDegrees);
        CommitToParameter();
        InvalidateGpuCache();
    }

    public bool ReloadNormalsFromSourceFile()
    {
        if (!IsActive || string.IsNullOrWhiteSpace(TargetParameter?.FilePath)) return false;
        var parsed = ModelParserDispatcher.Parse(TargetParameter.FilePath);
        if (parsed == null) return false;

        var refMesh = EditableMesh.FromParsedModel(parsed);
        if (refMesh.Faces.Count == 0) return false;

        PushUndo("元モデルから法線を再読込");

        static (long, long, long) QPos(Vector3 p)
            => ((long)Math.Round(p.X * 10000.0), (long)Math.Round(p.Y * 10000.0), (long)Math.Round(p.Z * 10000.0));

        var refVertexNormals = new Dictionary<(long, long, long), List<(Vector3 FNorm, Vector3 CNorm)>>(refMesh.Vertices.Count);
        for (int fi = 0; fi < refMesh.Faces.Count; fi++)
        {
            var rf = refMesh.Faces[fi];
            for (int k = 0; k < rf.VertexIndices.Count; k++)
            {
                int rvi = rf.VertexIndices[k];
                Vector3 cn = (rf.FaceCornerNormals != null && k < rf.FaceCornerNormals.Count)
                    ? rf.FaceCornerNormals[k]
                    : (rf.Smooth ? refMesh.Vertices[rvi].Normal : rf.FaceNormal);
                var qp = QPos(refMesh.Vertices[rvi].Position);
                if (!refVertexNormals.TryGetValue(qp, out var list))
                {
                    list = new List<(Vector3, Vector3)>(4);
                    refVertexNormals[qp] = list;
                }
                list.Add((rf.FaceNormal, cn));
            }
        }

        for (int fi = 0; fi < Mesh.Faces.Count; fi++)
        {
            var face = Mesh.Faces[fi];
            int count = face.VertexIndices.Count;
            if (count < 3) continue;

            Vector3 fNorm = face.FaceNormal;
            var cornerNorms = new List<Vector3>(count);
            bool matchedAll = true;
            bool anySmooth = false;

            for (int k = 0; k < count; k++)
            {
                int vi = face.VertexIndices[k];
                if (vi < 0 || vi >= Mesh.Vertices.Count ||
                    !refVertexNormals.TryGetValue(QPos(Mesh.Vertices[vi].Position), out var cands) ||
                    cands.Count == 0)
                {
                    matchedAll = false;
                    break;
                }

                Vector3 best = cands[0].CNorm;
                float bestDot = -2f;
                for (int c = 0; c < cands.Count; c++)
                {
                    float d = Vector3.Dot(fNorm, cands[c].FNorm);
                    if (d > bestDot)
                    {
                        bestDot = d;
                        best = cands[c].CNorm;
                    }
                }
                cornerNorms.Add(best);
                if (Vector3.Dot(best, fNorm) < 0.99995f)
                    anySmooth = true;
            }

            if (matchedAll)
            {
                face.FaceCornerNormals = cornerNorms;
                face.Smooth = anySmooth;
            }
        }

        Mesh.RecalculateNormals();
        CommitToParameter();
        InvalidateGpuCache();
        return true;
    }

    public void FlipSelectedNormals()
    {
        if (!IsActive || Mesh.Faces.Count == 0) return;
        PushUndo("法線を反転");

        static void FlipFace(EditableFace f)
        {
            f.VertexIndices.Reverse();
            f.FaceUVs.Reverse();
            if (f.FaceCornerNormals != null)
            {
                f.FaceCornerNormals.Reverse();
                for (int i = 0; i < f.FaceCornerNormals.Count; i++)
                    f.FaceCornerNormals[i] = -f.FaceCornerNormals[i];
            }
        }

        if (Selection.Faces.Count > 0)
        {
            foreach (int fi in Selection.Faces)
            {
                if (fi >= 0 && fi < Mesh.Faces.Count)
                    FlipFace(Mesh.Faces[fi]);
            }
        }
        else
        {
            foreach (var f in Mesh.Faces)
                FlipFace(f);
        }

        Mesh.RecalculateNormals();
        CommitToParameter();
        InvalidateGpuCache();
    }

    public void RecalculateNormals()
    {
        if (!IsActive) return;
        Mesh.ClearCustomNormals();
        Mesh.RecalculateNormals();
        CommitToParameter();
        InvalidateGpuCache();
    }

    public void ExportObj(string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Exported from Iyahon D3D11 Renderer Modeling");
        foreach (var v in Mesh.Vertices)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "v {0:G9} {1:G9} {2:G9}\n", v.Position.X, v.Position.Y, v.Position.Z);
        }
        foreach (var v in Mesh.Vertices)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "vt {0:G9} {1:G9}\n", v.UV.X, v.UV.Y);
        }
        foreach (var f in Mesh.Faces)
        {
            if (f.VertexIndices.Count < 3) continue;
            sb.Append('f');
            foreach (int vi in f.VertexIndices)
            {
                int objIdx = vi + 1;
                sb.Append(' ').Append(objIdx).Append('/').Append(objIdx);
            }
            sb.Append('\n');
        }
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
    }

    #endregion

    #region オーバーレイ生成・ステータス文字列

    public void BuildOverlays(
        List<Viewport3DRenderer.OverlayVertex> depthLines,
        List<Viewport3DRenderer.OverlayVertex> depthTris,
        List<Viewport3DRenderer.OverlayVertex> topLines,
        List<Viewport3DRenderer.OverlayVertex> topTris,
        List<Viewport3DRenderer.OverlayVertex> ndcLines,
        ViewportCamera camera,
        int width,
        int height,
        GizmoSpace gizmoSpace)
    {
        if (!IsActive) return;

        var wireLines = XRayEnabled ? topLines : depthLines;
        var fillTris = XRayEnabled ? topTris : depthTris;

        Vector4 colEdgeNormal = XRayEnabled ? new(0.28f, 0.34f, 0.42f, 0.65f) : new(0.18f, 0.22f, 0.28f, 0.72f);
        Vector4 colEdgeSelected = new(1.00f, 0.56f, 0.12f, 0.98f);
        Vector4 colFaceSelected = new(1.00f, 0.56f, 0.12f, XRayEnabled ? 0.20f : 0.28f);
        Vector4 colFaceDot = new(1.00f, 0.68f, 0.20f, 0.95f);
        Vector4 colVertNormal = new(0.10f, 0.10f, 0.12f, 0.92f);
        Vector4 colVertSelected = new(1.00f, 0.60f, 0.10f, 1.00f);
        Vector4 colVertActive = new(1.00f, 1.00f, 1.00f, 1.00f);

        // 深度テスト有効時、自身に接するポリゴンとのZファイティングを防ぐため微小量カメラ側へオフセットする
        camera.GetCameraAxes(out var camRight, out var camUp, out var camFwd);
        Vector3 camPos = camera.CameraPosition;

        Vector3 BiasTowardCam(Vector3 p, float factor)
        {
            if (XRayEnabled) return p;
            Vector3 toCam = camera.IsOrthographic ? -camFwd : (camPos - p);
            float dist = camera.IsOrthographic ? (camera.Distance * camera.OrthoScale) : toCam.Length();
            Vector3 dir = camera.IsOrthographic ? -camFwd : (dist > 1e-4f ? toCam / dist : -camFwd);
            return p + dir * MathF.Max(0.25f, dist * factor);
        }

        // キャッシュしたワールド座標を使うことで大モデルでも高速に構築する
        var worldVerts = new Vector3[Mesh.Vertices.Count];
        var edgeVerts = new Vector3[Mesh.Vertices.Count];
        for (int i = 0; i < worldVerts.Length; i++)
        {
            Vector3 wp = Vector3.Transform(Mesh.Vertices[i].Position, ModelToWorld);
            worldVerts[i] = wp;
            edgeVerts[i] = BiasTowardCam(wp, 0.0022f);
        }

        // 辺ワイヤーフレーム
        for (int ei = 0; ei < Mesh.Edges.Count; ei++)
        {
            var e = Mesh.Edges[ei];
            if (e.V0 < 0 || e.V1 < 0 || e.V0 >= edgeVerts.Length || e.V1 >= edgeVerts.Length) continue;
            bool isSel = Selection.Edges.Contains(ei);
            ViewportOverlayBuilder.AddLine(wireLines, edgeVerts[e.V0], edgeVerts[e.V1], isSel ? colEdgeSelected : colEdgeNormal);
        }

        // 選択面の半透明ハイライト + 面選択モード時の面中心ドット
        for (int fi = 0; fi < Mesh.Faces.Count; fi++)
        {
            var face = Mesh.Faces[fi];
            var vi = face.VertexIndices;
            int n = vi.Count;
            if (n < 3) continue;

            bool isSel = Selection.Faces.Contains(fi);
            if (isSel)
            {
                Vector3 p0 = BiasTowardCam(worldVerts[vi[0]], 0.0012f);
                for (int k = 1; k + 1 < n; k++)
                {
                    ViewportOverlayBuilder.AddTri(
                        fillTris,
                        p0,
                        BiasTowardCam(worldVerts[vi[k]], 0.0012f),
                        BiasTowardCam(worldVerts[vi[k + 1]], 0.0012f),
                        colFaceSelected);
                }
            }

            if (SelectMode == EditElementMode.Face)
            {
                Vector3 c = Vector3.Zero;
                for (int k = 0; k < n; k++) c += worldVerts[vi[k]];
                c /= n;
                float r = ComputeMarkerWorldSize(c, camera) * (isSel ? 1.25f : 0.85f);
                AddBillboardQuad(fillTris, BiasTowardCam(c, 0.0040f), camRight, camUp, r, isSel ? colFaceDot : new Vector4(0.35f, 0.38f, 0.44f, 0.7f));
            }
        }

        // 頂点ドット (頂点選択モード、またはルーズ頂点がある場合)
        if (SelectMode == EditElementMode.Vertex || Mesh.Faces.Count == 0)
        {
            for (int vi = 0; vi < worldVerts.Length; vi++)
            {
                Vector3 wp = worldVerts[vi];
                bool isSel = Selection.Vertices.Contains(vi);
                bool isAct = Selection.ActiveVertex == vi;
                float r = ComputeMarkerWorldSize(wp, camera) * (isSel ? 1.45f : 0.95f);
                Vector4 col = isAct ? colVertActive : (isSel ? colVertSelected : colVertNormal);
                AddBillboardQuad(fillTris, BiasTowardCam(wp, 0.0040f), camRight, camUp, r, col);
            }
        }

        // ループカットプレビュー
        if (ActiveModalOp is ModalOpKind.LoopCutPickRing or ModalOpKind.LoopCutSlide && _activeRing != null)
        {
            var previewSegs = MeshEditOperations.BuildLoopCutPreviewLines(Mesh, _activeRing, _loopCutCount, _loopCutSlide);
            Vector4 previewCol = ActiveModalOp == ModalOpKind.LoopCutSlide
                ? new Vector4(1.0f, 0.90f, 0.15f, 1.0f)
                : new Vector4(0.15f, 0.95f, 1.0f, 1.0f);
            foreach (var (a, b) in previewSegs)
            {
                ViewportOverlayBuilder.AddLine(topLines,
                    Vector3.Transform(a, ModelToWorld),
                    Vector3.Transform(b, ModelToWorld),
                    previewCol);
            }
        }

        // モーダル軸固定ガイド線
        if (ActiveModalOp is ModalOpKind.Grab or ModalOpKind.Rotate or ModalOpKind.Scale &&
            (AxisConstraint != AxisConstraintMode.None || _customModalAxisWorld.HasValue))
        {
            DrawConstraintGuideLines(topLines, camera);
        }

        // 編集用トランスフォームギズモ
        if (TryComputeEditGizmo(camera, gizmoSpace, out var gizmo))
        {
            var highlight = IsGizmoDragging ? _activeGizmoHandle : HoveredGizmoHandle;
            ViewportOverlayBuilder.BuildTransformGizmo(topLines, topTris, gizmo, EditGizmoMode, highlight, IsGizmoDragging);
        }

        // ボックス選択マーキー枠 (NDC)
        if (_isBoxSelecting)
        {
            float w = MathF.Max(1f, width);
            float h = MathF.Max(1f, height);
            float x0 = (_boxStartPx.X / w) * 2f - 1f;
            float x1 = (_boxEndPx.X / w) * 2f - 1f;
            float y0 = 1f - (_boxStartPx.Y / h) * 2f;
            float y1 = 1f - (_boxEndPx.Y / h) * 2f;
            var colMarquee = new Vector4(0.95f, 0.98f, 1.0f, 0.92f);
            ViewportOverlayBuilder.AddQuadLoop(
                ndcLines,
                new Vector3(x0, y0, 0.1f),
                new Vector3(x1, y0, 0.1f),
                new Vector3(x1, y1, 0.1f),
                new Vector3(x0, y1, 0.1f),
                colMarquee);
        }
    }

    private void DrawConstraintGuideLines(List<Viewport3DRenderer.OverlayVertex> topLines, ViewportCamera camera)
    {
        const float halfLen = 4000f;
        if (_customModalAxisWorld.HasValue)
        {
            Vector3 ax = ResolveConstraintAxisWorld(camera);
            ViewportOverlayBuilder.AddLine(topLines, _modalPivotWorld - ax * halfLen, _modalPivotWorld + ax * halfLen, new(0.25f, 0.85f, 1.0f, 0.95f));
            return;
        }

        if (AxisConstraint is AxisConstraintMode.X or AxisConstraintMode.Y or AxisConstraintMode.Z)
        {
            Vector3 ax = ResolveConstraintAxisWorld(camera);
            Vector4 col = AxisConstraint switch
            {
                AxisConstraintMode.X => new(1.0f, 0.25f, 0.25f, 0.95f),
                AxisConstraintMode.Y => new(0.25f, 0.95f, 0.35f, 0.95f),
                _ => new(0.28f, 0.58f, 1.0f, 0.95f)
            };
            ViewportOverlayBuilder.AddLine(topLines, _modalPivotWorld - ax * halfLen, _modalPivotWorld + ax * halfLen, col);
        }
        else
        {
            // ExcludeX -> Y & Z の2軸を表示
            Vector3 ax1, ax2;
            Vector4 c1, c2;
            Vector3 ux = ConstraintSpace == AxisConstraintSpace.Local ? Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, ModelToWorld)) : Vector3.UnitX;
            Vector3 uy = ConstraintSpace == AxisConstraintSpace.Local ? Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, ModelToWorld)) : Vector3.UnitY;
            Vector3 uz = ConstraintSpace == AxisConstraintSpace.Local ? Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, ModelToWorld)) : Vector3.UnitZ;
            Vector4 rCol = new(1.0f, 0.25f, 0.25f, 0.85f);
            Vector4 gCol = new(0.25f, 0.95f, 0.35f, 0.85f);
            Vector4 bCol = new(0.28f, 0.58f, 1.0f, 0.85f);

            switch (AxisConstraint)
            {
                case AxisConstraintMode.ExcludeX: ax1 = uy; c1 = gCol; ax2 = uz; c2 = bCol; break;
                case AxisConstraintMode.ExcludeY: ax1 = ux; c1 = rCol; ax2 = uz; c2 = bCol; break;
                default: ax1 = ux; c1 = rCol; ax2 = uy; c2 = gCol; break;
            }
            ViewportOverlayBuilder.AddLine(topLines, _modalPivotWorld - ax1 * halfLen, _modalPivotWorld + ax1 * halfLen, c1);
            ViewportOverlayBuilder.AddLine(topLines, _modalPivotWorld - ax2 * halfLen, _modalPivotWorld + ax2 * halfLen, c2);
        }
    }

    private static float ComputeMarkerWorldSize(Vector3 worldPos, ViewportCamera camera)
    {
        float dist = camera.IsOrthographic
            ? camera.Distance * camera.OrthoScale
            : Vector3.Distance(worldPos, camera.CameraPosition);
        return MathF.Max(1.2f, dist * 0.0055f);
    }

    private static void AddBillboardQuad(
        List<Viewport3DRenderer.OverlayVertex> tris, Vector3 center, Vector3 right, Vector3 up, float halfSize, Vector4 col)
    {
        Vector3 rx = right * halfSize;
        Vector3 uy = up * halfSize;
        ViewportOverlayBuilder.AddQuadTris(
            tris,
            center - rx - uy,
            center + rx - uy,
            center + rx + uy,
            center - rx + uy,
            col);
    }

    public string BuildStatusText()
    {
        if (!IsActive) return "モデル編集: 対象の D3D11 3Dモデル / D3D11 図形 を選択してください";

        string modeLabel = SelectMode switch
        {
            EditElementMode.Edge => "辺選択 [2]",
            EditElementMode.Face => "面選択 [3]",
            _ => "頂点選択 [1]"
        };

        int matIdx = ActiveMaterialIndex;
        string matName = (matIdx >= 0 && matIdx < Mesh.Materials.Count) ? Mesh.Materials[matIdx].Name : "Default";
        string matLabel = $"材質:[{matIdx}] {matName}";

        string axisText = _customModalAxisWorld.HasValue
            ? " [法線軸]"
            : AxisConstraint switch
            {
                AxisConstraintMode.X => ConstraintSpace == AxisConstraintSpace.Local ? " [X軸(ローカル)]" : " [X軸]",
                AxisConstraintMode.Y => ConstraintSpace == AxisConstraintSpace.Local ? " [Y軸(ローカル)]" : " [Y軸]",
                AxisConstraintMode.Z => ConstraintSpace == AxisConstraintSpace.Local ? " [Z軸(ローカル)]" : " [Z軸]",
                AxisConstraintMode.ExcludeX => ConstraintSpace == AxisConstraintSpace.Local ? " [Y・Z面(ローカル)]" : " [Y・Z面]",
                AxisConstraintMode.ExcludeY => ConstraintSpace == AxisConstraintSpace.Local ? " [X・Z面(ローカル)]" : " [X・Z面]",
                AxisConstraintMode.ExcludeZ => ConstraintSpace == AxisConstraintSpace.Local ? " [X・Y面(ローカル)]" : " [X・Y面]",
                _ => string.Empty
            };

        string numText = string.IsNullOrEmpty(_modalNumericBuffer) ? string.Empty : $" 値:{_modalNumericBuffer}";

        if (ActiveModalOp is ModalOpKind.Grab or ModalOpKind.Rotate or ModalOpKind.Scale)
        {
            string opName = ActiveModalOp switch
            {
                ModalOpKind.Grab => "移動 (G)",
                ModalOpKind.Rotate => "回転 (R)",
                _ => "拡大縮小 (S)"
            };
            return $"[編集] {opName}{axisText}{numText} | X/Y/Z: 軸固定 | Shift+軸: 平面固定 | 数値入力 | LMB/Enter: 確定 / RMB/Esc: 戻す";
        }

        if (ActiveModalOp == ModalOpKind.LoopCutPickRing)
        {
            return $"[編集] ループカット (Ctrl+R) 分割数: {_loopCutCount} | ホイール/+-: 分割数 | クリック: 決定 / Esc: 終了";
        }

        if (ActiveModalOp == ModalOpKind.LoopCutSlide)
        {
            return $"[編集] ループカット スライド: {_loopCutSlide:+0.00;-0.00;0.00} (分割:{_loopCutCount}) | 左クリック: 確定 / 右クリック: 中央 / Esc: 戻る";
        }

        return $"[編集: {modeLabel}] {matLabel} | 頂点:{Selection.Vertices.Count}/{Mesh.Vertices.Count} 辺:{Selection.Edges.Count}/{Mesh.Edges.Count} 面:{Selection.Faces.Count}/{Mesh.Faces.Count}"
             + (XRayEnabled ? " [透過ON]" : string.Empty)
             + " | G:移動 R:回転 S:拡大 E:押出/頂点 F:面張 X:削除 Ctrl+R:ループカット Shift+A:追加 A:全選択 Tab:終了";
    }

    #endregion

    #region 数学ヘルパー

    private static Vector3 ComputeFaceCenterLocal(EditableMesh mesh, EditableFace face)
    {
        if (face.VertexIndices.Count == 0) return Vector3.Zero;
        Vector3 sum = Vector3.Zero;
        foreach (int vi in face.VertexIndices) sum += mesh.Vertices[vi].Position;
        return sum / face.VertexIndices.Count;
    }

    private static float DistancePointToSegment2D(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-4f)
        {
            t = 0f;
            return Vector2.Distance(p, a);
        }
        t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    private static bool IntersectRayTriangle(
        Vector3 orig, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0f;
        Vector3 e1 = v1 - v0;
        Vector3 e2 = v2 - v0;
        Vector3 pvec = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, pvec);
        if (MathF.Abs(det) < 1e-7f) return false;

        float invDet = 1f / det;
        Vector3 tvec = orig - v0;
        float u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < 0f || u > 1f) return false;

        Vector3 qvec = Vector3.Cross(tvec, e1);
        float v = Vector3.Dot(dir, qvec) * invDet;
        if (v < 0f || u + v > 1f) return false;

        t = Vector3.Dot(e2, qvec) * invDet;
        return true;
    }

    #endregion
}
