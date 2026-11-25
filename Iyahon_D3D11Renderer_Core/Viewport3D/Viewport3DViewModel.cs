using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Iyahon_D3D11Renderer_Core.Model3D;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;
using Microsoft.Win32;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.UndoRedo;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

internal enum MeshSelectMode
{
    Vertex,
    Edge,
    Face,
}

public enum ViewportWorkspaceTab
{
    Layout,
    Shading,
}

public sealed class MaterialSlotItem
{
    public int Index { get; init; }
    public string Name { get; init; } = "Material";
    public Brush ColorSwatch { get; init; } = Brushes.White;
    public int FaceCount { get; init; }
    public string DisplayText => $"[{Index}] {Name} ({FaceCount}面)";
    public override string ToString() => DisplayText;
}

public sealed class Viewport3DViewModel : INotifyPropertyChanged, ITimelineToolViewModel, IToolViewModel, IDisposable
{
    private Timeline? _timeline;
    private UndoRedoManager? _undoRedoManager;
    private ViewportSceneSnapshot? _snapshot;
    private bool _disposed;
    private bool _dirty = true;
    private int _refreshPending;
    private bool _isModelEditMode;
    private MeshSelectMode _meshSelectMode = MeshSelectMode.Vertex;
    private string? _editModeNotice;
    private bool _isMaterialPanelOpen;
    private bool _isModifierPanelOpen;
    private bool _syncingMaterialUi;
    private bool _mutatingModifierData;
    private Model3D.ID3DModelTransformParameter? _watchedModifierParam;
    private ViewportWorkspaceTab _activeWorkspaceTab = ViewportWorkspaceTab.Layout;

    internal ViewportCamera Camera { get; } = new();
    internal ViewportManipulator Manipulator { get; } = new();
    internal Viewport3DRenderer Renderer { get; } = new();
    internal ViewportRenderOptions RenderOptions { get; } = new();
    internal MeshEditSession EditSession { get; } = new();
    internal ViewportCameraPathController CameraPathController { get; } = new();
    internal ViewportBezierCurveController BezierCurveController { get; } = new();

    public event Action? ShadingGraphChanged;
    public event Action? ModifiersChanged;

    public ViewportWorkspaceTab ActiveWorkspaceTab
    {
        get => _activeWorkspaceTab;
        set => SetWorkspaceTab(value);
    }

    public bool IsLayoutWorkspace => _activeWorkspaceTab == ViewportWorkspaceTab.Layout;
    public bool IsShadingWorkspace => _activeWorkspaceTab == ViewportWorkspaceTab.Shading;
    public bool HasShadingTarget => EditSession.IsActive;
    public bool IsShadingTargetMissing => !EditSession.IsActive;
    public string ShadingTargetName => EditSession.TargetParameter switch
    {
        D3DPrimitiveShapeParameter p => $"D3D11 図形 ({p.ShapeKind})",
        D3DModelShapeParameter => "D3D11 3Dモデル",
        _ => "未選択"
    };

    public ObservableCollection<MaterialSlotItem> MaterialSlots { get; } = new();

    public UndoRedoManager? UndoRedoManager => _undoRedoManager;
    public Timeline? Timeline => _timeline;
    internal ViewportSceneSnapshot? CurrentSnapshot => _snapshot;

    public bool IsDirty => _dirty;
    public void ClearDirty() => _dirty = false;

    public void MarkDirty()
    {
        _dirty = true;
        UpdateSelectionStatus();
    }

    public bool IsTranslateMode => !IsModelEditMode && Manipulator.Mode == GizmoMode.Translate;
    public bool IsRotateMode => !IsModelEditMode && Manipulator.Mode == GizmoMode.Rotate;
    public bool IsScaleMode => !IsModelEditMode && Manipulator.Mode == GizmoMode.Scale;

    public bool IsEditGizmoTranslate => EditSession.EditGizmoMode == GizmoMode.Translate;
    public bool IsEditGizmoRotate => EditSession.EditGizmoMode == GizmoMode.Rotate;
    public bool IsEditGizmoScale => EditSession.EditGizmoMode == GizmoMode.Scale;

    public bool IsXRayEnabled
    {
        get => EditSession.XRayEnabled;
        set
        {
            if (EditSession.XRayEnabled != value)
            {
                EditSession.XRayEnabled = value;
                OnPropertyChanged(nameof(IsXRayEnabled));
                MarkDirty();
            }
        }
    }

    public bool IsMaterialPanelOpen
    {
        get => _isMaterialPanelOpen;
        set
        {
            if (_isMaterialPanelOpen != value)
            {
                _isMaterialPanelOpen = value;
                if (value && _isModifierPanelOpen)
                {
                    _isModifierPanelOpen = false;
                    OnPropertyChanged(nameof(IsModifierPanelOpen));
                    OnPropertyChanged(nameof(IsLayoutModifierPanelVisible));
                }
                if (value && !_isModelEditMode && IsLayoutWorkspace)
                {
                    EnsureMaterialSessionActive();
                    SyncMaterialPropertiesFromSession(rebuildSlots: true);
                    NotifyEditModeStateChanged();
                }
                else if (!value && !_isModelEditMode && !IsShadingWorkspace && EditSession.IsActive)
                {
                    EditSession.Exit(commitChanges: true);
                    SyncMaterialPropertiesFromSession(rebuildSlots: true);
                    NotifyEditModeStateChanged();
                }
                OnPropertyChanged(nameof(IsMaterialPanelOpen));
                OnPropertyChanged(nameof(IsMaterialPanelVisible));
                OnPropertyChanged(nameof(IsLayoutMaterialPanelVisible));
                MarkDirty();
            }
        }
    }

    public bool IsMaterialPanelVisible => _isMaterialPanelOpen;
    public bool IsLayoutMaterialPanelVisible => IsLayoutWorkspace && _isMaterialPanelOpen;

    public bool IsModifierPanelOpen
    {
        get => _isModifierPanelOpen;
        set
        {
            if (_isModifierPanelOpen != value)
            {
                _isModifierPanelOpen = value;
                if (value && _isMaterialPanelOpen)
                {
                    _isMaterialPanelOpen = false;
                    OnPropertyChanged(nameof(IsMaterialPanelOpen));
                    OnPropertyChanged(nameof(IsMaterialPanelVisible));
                    OnPropertyChanged(nameof(IsLayoutMaterialPanelVisible));
                }
                OnPropertyChanged(nameof(IsModifierPanelOpen));
                OnPropertyChanged(nameof(IsLayoutModifierPanelVisible));
                if (value) ModifiersChanged?.Invoke();
            }
        }
    }

    public bool IsLayoutModifierPanelVisible => IsLayoutWorkspace && _isModifierPanelOpen;

    public IVideoItem? ModifierTargetItem => EditSession.IsActive ? EditSession.TargetItem : FindEditableModelItem();

    internal Model3D.ID3DModelTransformParameter? ModifierTargetParameter =>
        EditSession.IsActive
            ? EditSession.TargetParameter
            : (FindEditableModelItem() is ShapeItem si ? si.ShapeParameter as Model3D.ID3DModelTransformParameter : null);

    public bool HasModifierTarget => ModifierTargetParameter != null;

    public string ModifierTargetName => ModifierTargetParameter switch
    {
        D3DPrimitiveShapeParameter p => $"D3D11 図形 ({p.ShapeKind})",
        D3DModelShapeParameter => "D3D11 3Dモデル",
        _ => "3Dモデル / 図形を未選択"
    };

    public int ModifierCount => GetCurrentModifierStack().Items.Count;

    public string ModifierButtonLabel => ModifierCount > 0 ? $"モディファイア ({ModifierCount})" : "モディファイア";

    public bool IsModelEditMode
    {
        get => _isModelEditMode;
        set
        {
            if (_isModelEditMode == value) return;

            if (value)
            {
                var target = FindEditableModelItem();
                if (target == null || (!EditSession.IsActive && !EditSession.Enter(target, _snapshot, Manipulator)) ||
                    (!ReferenceEquals(EditSession.TargetItem, target) && !EditSession.Enter(target, _snapshot, Manipulator)))
                {
                    _isModelEditMode = false;
                    _editModeNotice = "モデル編集: タイムラインまたはビューポートで「D3D11 3Dモデル / D3D11 図形」を選択してください";
                    OnPropertyChanged(nameof(IsModelEditMode));
                    MarkDirty();
                    return;
                }

                _editModeNotice = null;
                Manipulator.CancelDrag();
                EditSession.SelectMode = ToEditElementMode(_meshSelectMode);
                _isModelEditMode = true;
                RenderOptions.ShowEditOverlays = true;
            }
            else
            {
                if (!IsShadingWorkspace && !IsMaterialPanelOpen)
                    EditSession.Exit(commitChanges: true);
                RenderOptions.ShowEditOverlays = IsLayoutWorkspace;
                _editModeNotice = null;
                _isModelEditMode = false;
            }

            SyncMaterialPropertiesFromSession(rebuildSlots: true);
            SyncWatchedModifierParam();
            NotifyEditModeStateChanged();
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
        }
    }

    internal MeshSelectMode CurrentMeshSelectMode
    {
        get => _meshSelectMode;
        set
        {
            if (_meshSelectMode != value)
            {
                _meshSelectMode = value;
                if (_isModelEditMode && EditSession.IsActive)
                {
                    EditSession.SelectMode = ToEditElementMode(value);
                    EditSession.Selection.SyncByMode(EditSession.Mesh, EditSession.SelectMode);
                }
                OnPropertyChanged(nameof(CurrentMeshSelectMode));
                OnPropertyChanged(nameof(IsVertexSelectMode));
                OnPropertyChanged(nameof(IsEdgeSelectMode));
                OnPropertyChanged(nameof(IsFaceSelectMode));
                MarkDirty();
            }
        }
    }

    private IVideoItem? FindEditableModelItem()
    {
        var selected = GetSelectedVideoItems();
        for (int i = 0; i < selected.Count; i++)
        {
            if (selected[i] is ShapeItem si && si.ShapeParameter is ID3DModelTransformParameter)
                return selected[i];
        }
        return null;
    }

    private static EditElementMode ToEditElementMode(MeshSelectMode mode) => mode switch
    {
        MeshSelectMode.Edge => EditElementMode.Edge,
        MeshSelectMode.Face => EditElementMode.Face,
        _ => EditElementMode.Vertex,
    };

    private void NotifyEditModeStateChanged()
    {
        OnPropertyChanged(nameof(IsModelEditMode));
        OnPropertyChanged(nameof(IsMaterialPanelVisible));
        OnPropertyChanged(nameof(IsLayoutMaterialPanelVisible));
        OnPropertyChanged(nameof(HasShadingTarget));
        OnPropertyChanged(nameof(IsShadingTargetMissing));
        OnPropertyChanged(nameof(ShadingTargetName));
        OnPropertyChanged(nameof(IsTranslateMode));
        OnPropertyChanged(nameof(IsRotateMode));
        OnPropertyChanged(nameof(IsScaleMode));
        OnPropertyChanged(nameof(IsXRayEnabled));
        OnPropertyChanged(nameof(IsEditGizmoTranslate));
        OnPropertyChanged(nameof(IsEditGizmoRotate));
        OnPropertyChanged(nameof(IsEditGizmoScale));
    }

    public bool IsVertexSelectMode => CurrentMeshSelectMode == MeshSelectMode.Vertex;
    public bool IsEdgeSelectMode => CurrentMeshSelectMode == MeshSelectMode.Edge;
    public bool IsFaceSelectMode => CurrentMeshSelectMode == MeshSelectMode.Face;

    public bool IsWorldSpace => Manipulator.Space == GizmoSpace.World;
    public bool IsLocalSpace => Manipulator.Space == GizmoSpace.Local;
    public bool IsEditAll => !Manipulator.EditKeyFrameOnly;
    public bool IsEditKeyFrameOnly => Manipulator.EditKeyFrameOnly;
    public bool IsPerspective => !Camera.IsOrthographic;
    public bool IsOrthographic => Camera.IsOrthographic;

    public string SpaceLabel => Manipulator.Space == GizmoSpace.World ? "座標: ワールド (Q)" : "座標: ローカル (Q)";
    public string ProjectionLabel => Camera.IsOrthographic ? "平行投影 (5)" : "透視投影 (5)";
    public string KeyFrameModeLabel => Manipulator.EditKeyFrameOnly ? "編集: KF個別" : "編集: 全体";

    public bool ShowGrid
    {
        get => RenderOptions.ShowGrid;
        set
        {
            if (RenderOptions.ShowGrid != value)
            {
                RenderOptions.ShowGrid = value;
                OnPropertyChanged(nameof(ShowGrid));
                MarkDirty();
            }
        }
    }

    public bool ShowCameraFrustum
    {
        get => RenderOptions.ShowCameraFrustum;
        set
        {
            if (RenderOptions.ShowCameraFrustum != value)
            {
                RenderOptions.ShowCameraFrustum = value;
                OnPropertyChanged(nameof(ShowCameraFrustum));
                MarkDirty();
            }
        }
    }

    public bool ShowLights
    {
        get => RenderOptions.ShowLights;
        set
        {
            if (RenderOptions.ShowLights != value)
            {
                RenderOptions.ShowLights = value;
                OnPropertyChanged(nameof(ShowLights));
                MarkDirty();
            }
        }
    }

    private string _statusText = "タイムライン再生または変更を待機中...";
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    private string _sceneSummaryText = "アイテム: 0";
    public string SceneSummaryText
    {
        get => _sceneSummaryText;
        set
        {
            if (_sceneSummaryText != value)
            {
                _sceneSummaryText = value;
                OnPropertyChanged(nameof(SceneSummaryText));
            }
        }
    }

    public ICommand ModeTranslateCommand { get; }
    public ICommand ModeRotateCommand { get; }
    public ICommand ModeScaleCommand { get; }
    public ICommand ToggleModelEditModeCommand { get; }
    public ICommand SelectVertexModeCommand { get; }
    public ICommand SelectEdgeModeCommand { get; }
    public ICommand SelectFaceModeCommand { get; }
    public ICommand ToggleXRayCommand { get; }
    public ICommand EditGizmoTranslateCommand { get; }
    public ICommand EditGizmoRotateCommand { get; }
    public ICommand EditGizmoScaleCommand { get; }
    public ICommand ExtrudeMeshCommand { get; }
    public ICommand MakeFaceCommand { get; }
    public ICommand SelectAllMeshCommand { get; }
    public ICommand InvertSelectionMeshCommand { get; }
    public ICommand SelectLinkedMeshCommand { get; }
    public ICommand AddPrimitiveCommand { get; }
    public ICommand DeleteMeshCommand { get; }
    public ICommand ShadeSmoothCommand { get; }
    public ICommand ShadeFlatCommand { get; }
    public ICommand AutoSmoothCommand { get; }
    public ICommand ReloadSourceNormalsCommand { get; }
    public ICommand FlipNormalsCommand { get; }
    public ICommand RecalcNormalsCommand { get; }
    public ICommand ExportObjCommand { get; }
    public ICommand AddMaterialSlotCommand { get; }
    public ICommand DuplicateMaterialSlotCommand { get; }
    public ICommand RemoveMaterialSlotCommand { get; }
    public ICommand AssignMaterialCommand { get; }
    public ICommand SelectByMaterialCommand { get; }
    public ICommand ClearMaterialTextureCommand { get; }
    public ICommand ApplyMaterialPresetCommand { get; }
    public ICommand ApplyBaseColorSwatchCommand { get; }
    public ICommand ToggleSpaceCommand { get; }
    public ICommand SetWorldSpaceCommand { get; }
    public ICommand SetLocalSpaceCommand { get; }
    public ICommand ToggleProjectionCommand { get; }
    public ICommand SetPerspectiveCommand { get; }
    public ICommand SetOrthographicCommand { get; }
    public ICommand ToggleKeyFrameModeCommand { get; }
    public ICommand SetEditAllCommand { get; }
    public ICommand SetEditKeyFrameOnlyCommand { get; }
    public ICommand ViewYmmCameraCommand { get; }
    public ICommand ViewFrontCommand { get; }
    public ICommand ViewRightCommand { get; }
    public ICommand ViewTopCommand { get; }
    public ICommand FocusSelectedCommand { get; }
    public ICommand ResetCameraCommand { get; }
    public ICommand SetLayoutWorkspaceCommand { get; }
    public ICommand SetShadingWorkspaceCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }

    public Viewport3DViewModel()
    {
        Camera.SetPreset(ViewportCameraPreset.Perspective);
        Camera.Distance = 1400f;

        ModeTranslateCommand = new ActionCommand(_ => true, _ => SetMode(GizmoMode.Translate));
        ModeRotateCommand = new ActionCommand(_ => true, _ => SetMode(GizmoMode.Rotate));
        ModeScaleCommand = new ActionCommand(_ => true, _ => SetMode(GizmoMode.Scale));
        ToggleModelEditModeCommand = new ActionCommand(_ => true, _ => IsModelEditMode = !IsModelEditMode);
        SelectVertexModeCommand = new ActionCommand(_ => true, _ => CurrentMeshSelectMode = MeshSelectMode.Vertex);
        SelectEdgeModeCommand = new ActionCommand(_ => true, _ => CurrentMeshSelectMode = MeshSelectMode.Edge);
        SelectFaceModeCommand = new ActionCommand(_ => true, _ => CurrentMeshSelectMode = MeshSelectMode.Face);

        ToggleXRayCommand = new ActionCommand(_ => true, _ => IsXRayEnabled = !IsXRayEnabled);
        EditGizmoTranslateCommand = new ActionCommand(_ => true, _ => SetEditGizmoMode(GizmoMode.Translate));
        EditGizmoRotateCommand = new ActionCommand(_ => true, _ => SetEditGizmoMode(GizmoMode.Rotate));
        EditGizmoScaleCommand = new ActionCommand(_ => true, _ => SetEditGizmoMode(GizmoMode.Scale));
        ExtrudeMeshCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ =>
        {
            if (EditSession.ExtrudeAndBeginMove(new Vector2(-1f, -1f)))
            {
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                MarkDirty();
            }
        });
        MakeFaceCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ =>
        {
            if (EditSession.MakeEdgeOrFace(Camera))
            {
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                MarkDirty();
            }
        });
        SelectAllMeshCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ =>
        {
            EditSession.ToggleSelectAll();
            MarkDirty();
        });
        InvertSelectionMeshCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ =>
        {
            EditSession.InvertSelection();
            MarkDirty();
        });
        SelectLinkedMeshCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ =>
        {
            EditSession.SelectLinked();
            MarkDirty();
        });
        AddPrimitiveCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, p =>
        {
            EditablePrimitiveType? primType = p switch
            {
                EditablePrimitiveType t => t,
                string s when Enum.TryParse<EditablePrimitiveType>(s, true, out var parsed) => parsed,
                _ => null
            };
            if (primType.HasValue && EditSession.AddPrimitive(primType.Value))
            {
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                MarkDirty();
            }
        });
        DeleteMeshCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, p =>
        {
            MeshDeleteMode? delMode = p switch
            {
                MeshDeleteMode m => m,
                string s when Enum.TryParse<MeshDeleteMode>(s, true, out var parsed) => parsed,
                _ => null
            };
            if (delMode.HasValue && EditSession.Delete(delMode.Value))
            {
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                MarkDirty();
            }
        });
        ShadeSmoothCommand = new ActionCommand(_ => (IsModelEditMode || IsShadingWorkspace) && EditSession.IsActive, _ =>
        {
            EditSession.SetSelectionSmooth(true);
            MarkDirty();
        });
        ShadeFlatCommand = new ActionCommand(_ => (IsModelEditMode || IsShadingWorkspace) && EditSession.IsActive, _ =>
        {
            EditSession.SetSelectionSmooth(false);
            MarkDirty();
        });
        AutoSmoothCommand = new ActionCommand(_ => (IsModelEditMode || IsShadingWorkspace) && EditSession.IsActive, _ =>
        {
            EditSession.ApplyAutoSmooth(35f);
            MarkDirty();
        });
        ReloadSourceNormalsCommand = new ActionCommand(
            _ => (IsModelEditMode || IsShadingWorkspace) && EditSession.IsActive && !string.IsNullOrWhiteSpace(EditSession.TargetParameter?.FilePath),
            _ =>
            {
                if (EditSession.ReloadNormalsFromSourceFile())
                    MarkDirty();
            });
        FlipNormalsCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ =>
        {
            EditSession.FlipSelectedNormals();
            MarkDirty();
        });
        RecalcNormalsCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ =>
        {
            EditSession.RecalculateNormals();
            MarkDirty();
        });
        ExportObjCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ => ExportCurrentMeshToObj());

        AddMaterialSlotCommand = new ActionCommand(_ => CanEditMaterialNow, _ =>
        {
            if (EditSession.AddMaterialSlot() >= 0)
            {
                if (IsShadingWorkspace) EditSession.EnsureActiveNodeGraph();
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                ShadingGraphChanged?.Invoke();
                MarkDirty();
            }
        });
        DuplicateMaterialSlotCommand = new ActionCommand(_ => CanEditMaterialNow, _ =>
        {
            if (EditSession.DuplicateMaterialSlot(ActiveMaterialIndex) >= 0)
            {
                if (IsShadingWorkspace) EditSession.EnsureActiveNodeGraph();
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                ShadingGraphChanged?.Invoke();
                MarkDirty();
            }
        });
        RemoveMaterialSlotCommand = new ActionCommand(_ => CanEditMaterialNow, _ =>
        {
            if (EditSession.RemoveMaterialSlot(ActiveMaterialIndex))
            {
                if (IsShadingWorkspace) EditSession.EnsureActiveNodeGraph();
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                ShadingGraphChanged?.Invoke();
                MarkDirty();
            }
        });
        AssignMaterialCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ =>
        {
            if (EditSession.AssignMaterialToSelectedFaces(ActiveMaterialIndex))
            {
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                MarkDirty();
            }
        });
        SelectByMaterialCommand = new ActionCommand(_ => IsModelEditMode && EditSession.IsActive, _ =>
        {
            EditSession.SelectFacesByMaterial(ActiveMaterialIndex);
            MarkDirty();
        });
        ClearMaterialTextureCommand = new ActionCommand(_ => CanEditMaterialNow, _ =>
        {
            if (EditSession.ClearMaterialTexture(ActiveMaterialIndex))
            {
                SyncMaterialPropertiesFromSession(rebuildSlots: false);
                ShadingGraphChanged?.Invoke();
                MarkDirty();
            }
        });
        ApplyMaterialPresetCommand = new ActionCommand(_ => CanEditMaterialNow, p =>
        {
            if (p is string preset) ApplyActiveMaterialPreset(preset);
        });
        ApplyBaseColorSwatchCommand = new ActionCommand(_ => CanEditMaterialNow, p =>
        {
            if (p is string hex) ApplyActiveBaseColorHex(hex);
        });

        SetLayoutWorkspaceCommand = new ActionCommand(_ => true, _ => ActiveWorkspaceTab = ViewportWorkspaceTab.Layout);
        SetShadingWorkspaceCommand = new ActionCommand(_ => true, _ => ActiveWorkspaceTab = ViewportWorkspaceTab.Shading);
        UndoCommand = new ActionCommand(_ => true, _ => TryUndo());
        RedoCommand = new ActionCommand(_ => true, _ => TryRedo());

        ToggleSpaceCommand = new ActionCommand(_ => true, _ => ToggleSpace());
        SetWorldSpaceCommand = new ActionCommand(_ => true, _ => SetSpace(GizmoSpace.World));
        SetLocalSpaceCommand = new ActionCommand(_ => true, _ => SetSpace(GizmoSpace.Local));

        ToggleProjectionCommand = new ActionCommand(_ => true, _ => ToggleProjection());
        SetPerspectiveCommand = new ActionCommand(_ => true, _ => SetProjection(false));
        SetOrthographicCommand = new ActionCommand(_ => true, _ => SetProjection(true));

        ToggleKeyFrameModeCommand = new ActionCommand(_ => true, _ => ToggleKeyFrameMode());
        SetEditAllCommand = new ActionCommand(_ => true, _ => SetEditKeyFrameOnly(false));
        SetEditKeyFrameOnlyCommand = new ActionCommand(_ => true, _ => SetEditKeyFrameOnly(true));

        ViewYmmCameraCommand = new ActionCommand(_ => true, _ => MatchYmmCamera());
        ViewFrontCommand = new ActionCommand(_ => true, _ => ApplyPreset(ViewportCameraPreset.Front));
        ViewRightCommand = new ActionCommand(_ => true, _ => ApplyPreset(ViewportCameraPreset.Right));
        ViewTopCommand = new ActionCommand(_ => true, _ => ApplyPreset(ViewportCameraPreset.Top));
        FocusSelectedCommand = new ActionCommand(_ => true, _ => FocusSelected());
        ResetCameraCommand = new ActionCommand(_ => true, _ =>
        {
            Camera.SetPreset(ViewportCameraPreset.Perspective);
            Camera.Target = Vector3.Zero;
            Camera.Distance = 1400f;
            OnPropertyChanged(nameof(ProjectionLabel));
            OnPropertyChanged(nameof(IsPerspective));
            OnPropertyChanged(nameof(IsOrthographic));
            MarkDirty();
        });

        EditSession.SourceModelReloaded += OnEditSessionSourceModelReloaded;
        Viewport3DBridge.FrameUpdated += OnBridgeFrameUpdated;
        CameraPath.D3DCameraPathViewportBridge.StateChanged += OnCameraPathBridgeChanged;
        CameraPath.D3DCameraPathViewportBridge.FocusPathRequested += OnCameraPathFocusRequested;
        CameraPath.D3DCameraPathViewportBridge.FocusKeyframeRequested += OnCameraPathFocusKeyframeRequested;
        CameraPath.D3DCameraPathViewportBridge.AddCurrentViewportCameraRequested += OnCameraPathAddCurrentViewRequested;
        Model3D.ViewportBezierCurveBridge.SelectionChangedFromEditor += OnBezierCurveEditorSelectionChanged;
    }

    private void SetWorkspaceTab(ViewportWorkspaceTab tab)
    {
        if (_activeWorkspaceTab == tab) return;
        _activeWorkspaceTab = tab;

        if (tab == ViewportWorkspaceTab.Shading)
        {
            EnsureShadingSessionActive();
            RenderOptions.ShowEditOverlays = false;
        }
        else
        {
            if (!_isModelEditMode && !IsMaterialPanelOpen && EditSession.IsActive)
            {
                EditSession.Exit(commitChanges: true);
            }
            else if (EditSession.IsActive && EditSession.IsInteractiveMaterialPreview)
            {
                EditSession.FlushInteractiveMaterialPreview();
            }
            RenderOptions.ShowEditOverlays = true;
        }

        OnPropertyChanged(nameof(ActiveWorkspaceTab));
        OnPropertyChanged(nameof(IsLayoutWorkspace));
        OnPropertyChanged(nameof(IsShadingWorkspace));
        OnPropertyChanged(nameof(IsMaterialPanelVisible));
        OnPropertyChanged(nameof(IsLayoutMaterialPanelVisible));
        OnPropertyChanged(nameof(IsLayoutModifierPanelVisible));
        OnPropertyChanged(nameof(HasShadingTarget));
        OnPropertyChanged(nameof(IsShadingTargetMissing));
        OnPropertyChanged(nameof(ShadingTargetName));
        SyncMaterialPropertiesFromSession(rebuildSlots: true);
        SyncWatchedModifierParam();
        NotifyModifierStateChanged();
        ModifiersChanged?.Invoke();
        ShadingGraphChanged?.Invoke();
        MarkDirty();
    }

    private void EnsureShadingSessionActive()
    {
        var target = FindEditableModelItem();
        if (target != null)
        {
            if (!EditSession.IsActive || !ReferenceEquals(EditSession.TargetItem, target))
            {
                Manipulator.CancelDrag();
                EditSession.Enter(target, _snapshot, Manipulator);
            }
            EditSession.EnsureActiveNodeGraph();
        }
        else if (EditSession.IsActive && !_isModelEditMode)
        {
            EditSession.Exit(commitChanges: true);
        }
    }

    private void EnsureMaterialSessionActive()
    {
        var target = FindEditableModelItem();
        if (target != null)
        {
            if (!EditSession.IsActive || !ReferenceEquals(EditSession.TargetItem, target))
            {
                Manipulator.CancelDrag();
                EditSession.Enter(target, _snapshot, Manipulator);
            }
        }
        else if (EditSession.IsActive && !_isModelEditMode && !IsShadingWorkspace)
        {
            EditSession.Exit(commitChanges: true);
        }
    }

    internal void MutateActiveNodeGraph(
        Action<Shading.MaterialNodeGraph> mutator,
        string? coalesceTag = null,
        bool pushUndo = true,
        string description = "ノード編集",
        bool syncPanel = true)
    {
        if (!EditSession.IsActive) return;
        if (EditSession.UpdateMaterialNodeGraph(ActiveMaterialIndex, mutator, coalesceTag, pushUndo, description))
        {
            if (syncPanel)
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
            MarkDirty();
        }
    }

    internal void MutateActiveNodeGraphInteractive(
        Action<Shading.MaterialNodeGraph> mutator,
        string? coalesceTag = null,
        string description = "ノード編集")
    {
        if (!EditSession.IsActive) return;
        if (EditSession.UpdateMaterialNodeGraphInteractive(ActiveMaterialIndex, mutator, coalesceTag, true, description))
        {
            MarkDirty();
        }
    }

    internal void CommitInteractiveNodeGraph()
    {
        if (!EditSession.IsActive) return;
        EditSession.FlushInteractiveMaterialPreview();
        SyncMaterialPropertiesFromSession(rebuildSlots: false);
        MarkDirty();
    }

    internal void ApplyNodeGraphPreset(string presetKey)
    {
        if (!EditSession.IsActive) return;
        if (EditSession.ApplyNodeGraphPreset(ActiveMaterialIndex, presetKey))
        {
            SyncMaterialPropertiesFromSession(rebuildSlots: true);
            ShadingGraphChanged?.Invoke();
            MarkDirty();
        }
    }

    #region マテリアル編集プロパティ

    private bool CanEditMaterialNow => (IsModelEditMode || IsShadingWorkspace || IsMaterialPanelOpen) && EditSession.IsActive;

    private EditableMaterial? GetActiveMaterial()
    {
        if (!EditSession.IsActive || EditSession.Mesh.Materials.Count == 0) return null;
        int idx = EditSession.ActiveMaterialIndex;
        return (idx >= 0 && idx < EditSession.Mesh.Materials.Count) ? EditSession.Mesh.Materials[idx] : null;
    }

    public int ActiveMaterialIndex
    {
        get => EditSession.IsActive ? EditSession.ActiveMaterialIndex : 0;
        set
        {
            if (_syncingMaterialUi || value < 0 || !CanEditMaterialNow) return;
            if (EditSession.ActiveMaterialIndex != value)
            {
                if (EditSession.IsActive && EditSession.IsInteractiveMaterialPreview)
                    EditSession.FlushInteractiveMaterialPreview();
                EditSession.ActiveMaterialIndex = value;
                if (IsShadingWorkspace) EditSession.EnsureActiveNodeGraph();
                SyncMaterialPropertiesFromSession(rebuildSlots: false);
                ShadingGraphChanged?.Invoke();
                MarkDirty();
            }
        }
    }

    public string ActiveMaterialName
    {
        get => GetActiveMaterial()?.Name ?? "Material";
        set
        {
            if (_syncingMaterialUi || !CanEditMaterialNow) return;
            string cleaned = string.IsNullOrEmpty(value) ? "Material" : value;
            EditSession.UpdateMaterial(ActiveMaterialIndex, m => m.Name = cleaned, coalesceTag: "name");
            OnPropertyChanged(nameof(ActiveMaterialName));
            SyncMaterialPropertiesFromSession(rebuildSlots: true);
            UpdateSelectionStatus();
        }
    }

    public double ActiveMatR
    {
        get => Math.Round((GetActiveMaterial()?.BaseColor.X ?? 1f) * 255.0);
        set => UpdateActiveBaseChannel(0, value);
    }

    public double ActiveMatG
    {
        get => Math.Round((GetActiveMaterial()?.BaseColor.Y ?? 1f) * 255.0);
        set => UpdateActiveBaseChannel(1, value);
    }

    public double ActiveMatB
    {
        get => Math.Round((GetActiveMaterial()?.BaseColor.Z ?? 1f) * 255.0);
        set => UpdateActiveBaseChannel(2, value);
    }

    public double ActiveMatA
    {
        get => Math.Round((GetActiveMaterial()?.BaseColor.W ?? 1f) * 255.0);
        set => UpdateActiveBaseChannel(3, value);
    }

    private void UpdateActiveBaseChannel(int ch, double val255)
    {
        if (_syncingMaterialUi || !CanEditMaterialNow) return;
        float f = Math.Clamp((float)(val255 / 255.0), 0f, 1f);
        EditSession.UpdateMaterial(ActiveMaterialIndex, m =>
        {
            var c = m.BaseColor;
            m.BaseColor = ch switch
            {
                0 => new Vector4(f, c.Y, c.Z, c.W),
                1 => new Vector4(c.X, f, c.Z, c.W),
                2 => new Vector4(c.X, c.Y, f, c.W),
                _ => new Vector4(c.X, c.Y, c.Z, f)
            };
        }, coalesceTag: $"bc{ch}");
        NotifyBaseColorChanged();
        MarkDirty();
    }

    public double ActiveMatMetallic
    {
        get => GetActiveMaterial()?.Metallic ?? 0f;
        set
        {
            if (_syncingMaterialUi || !CanEditMaterialNow) return;
            float f = Math.Clamp((float)value, 0f, 1f);
            EditSession.UpdateMaterial(ActiveMaterialIndex, m => m.Metallic = f, coalesceTag: "metal");
            OnPropertyChanged(nameof(ActiveMatMetallic));
            MarkDirty();
        }
    }

    public double ActiveMatRoughness
    {
        get
        {
            var m = GetActiveMaterial();
            if (m == null) return 0.5;
            return m.Roughness >= 0f ? m.Roughness : 0.5;
        }
        set
        {
            if (_syncingMaterialUi || !CanEditMaterialNow) return;
            float f = Math.Clamp((float)value, 0.04f, 1f);
            EditSession.UpdateMaterial(ActiveMaterialIndex, m => m.Roughness = f, coalesceTag: "rough");
            OnPropertyChanged(nameof(ActiveMatRoughness));
            MarkDirty();
        }
    }

    public double ActiveMatTransmission
    {
        get => GetActiveMaterial()?.Transmission ?? 0f;
        set
        {
            if (_syncingMaterialUi || !CanEditMaterialNow) return;
            float f = Math.Clamp((float)value, 0f, 1f);
            EditSession.UpdateMaterial(ActiveMaterialIndex, m => m.Transmission = f, coalesceTag: "trans");
            OnPropertyChanged(nameof(ActiveMatTransmission));
            MarkDirty();
        }
    }

    public double ActiveMatIor
    {
        get => GetActiveMaterial()?.Ior ?? 1.5f;
        set
        {
            if (_syncingMaterialUi || !CanEditMaterialNow) return;
            float f = Math.Clamp((float)value, 1.0f, 2.5f);
            EditSession.UpdateMaterial(ActiveMaterialIndex, m => m.Ior = f, coalesceTag: "ior");
            OnPropertyChanged(nameof(ActiveMatIor));
            MarkDirty();
        }
    }

    public double ActiveMatEmR
    {
        get => Math.Round((GetActiveMaterial()?.EmissiveColor.X ?? 0f) * 255.0);
        set => UpdateActiveEmissiveChannel(0, value);
    }

    public double ActiveMatEmG
    {
        get => Math.Round((GetActiveMaterial()?.EmissiveColor.Y ?? 0f) * 255.0);
        set => UpdateActiveEmissiveChannel(1, value);
    }

    public double ActiveMatEmB
    {
        get => Math.Round((GetActiveMaterial()?.EmissiveColor.Z ?? 0f) * 255.0);
        set => UpdateActiveEmissiveChannel(2, value);
    }

    public double ActiveMatEmStrength
    {
        get => GetActiveMaterial()?.EmissiveColor.W ?? 0f;
        set
        {
            if (_syncingMaterialUi || !CanEditMaterialNow) return;
            float f = Math.Clamp((float)value, 0f, 20f);
            EditSession.UpdateMaterial(ActiveMaterialIndex, m =>
            {
                var ec = m.EmissiveColor;
                if (f > 0.001f && ec.X <= 0.001f && ec.Y <= 0.001f && ec.Z <= 0.001f)
                    m.EmissiveColor = new Vector4(1f, 1f, 1f, f);
                else
                    m.EmissiveColor = new Vector4(ec.X, ec.Y, ec.Z, f);
            }, coalesceTag: "emW");
            NotifyEmissiveChanged();
            MarkDirty();
        }
    }

    private void UpdateActiveEmissiveChannel(int ch, double val255)
    {
        if (_syncingMaterialUi || !CanEditMaterialNow) return;
        float f = Math.Clamp((float)(val255 / 255.0), 0f, 1f);
        EditSession.UpdateMaterial(ActiveMaterialIndex, m =>
        {
            var ec = m.EmissiveColor;
            m.EmissiveColor = ch switch
            {
                0 => new Vector4(f, ec.Y, ec.Z, ec.W),
                1 => new Vector4(ec.X, f, ec.Z, ec.W),
                _ => new Vector4(ec.X, ec.Y, f, ec.W)
            };
        }, coalesceTag: $"em{ch}");
        NotifyEmissiveChanged();
        MarkDirty();
    }

    public string ActiveMatBaseColorHex
    {
        get
        {
            var c = GetActiveMaterial()?.BaseColor ?? Vector4.One;
            return $"#{ToByte(c.X):X2}{ToByte(c.Y):X2}{ToByte(c.Z):X2}";
        }
    }

    public Brush ActiveMatBaseBrush
    {
        get
        {
            var c = GetActiveMaterial()?.BaseColor ?? Vector4.One;
            return new SolidColorBrush(Color.FromArgb(ToByte(c.W), ToByte(c.X), ToByte(c.Y), ToByte(c.Z)));
        }
    }

    public Brush ActiveMatEmissiveBrush
    {
        get
        {
            var ec = GetActiveMaterial()?.EmissiveColor ?? Vector4.Zero;
            return new SolidColorBrush(Color.FromRgb(ToByte(ec.X), ToByte(ec.Y), ToByte(ec.Z)));
        }
    }

    public bool ActiveMatHasOriginalTexture => (GetActiveMaterial()?.SourcePartIndex ?? -1) >= 0;

    public bool ActiveMatCullBackface
    {
        get => GetActiveMaterial()?.CullBackface ?? false;
        set
        {
            if (_syncingMaterialUi || !CanEditMaterialNow) return;
            EditSession.UpdateMaterial(ActiveMaterialIndex, m => m.CullBackface = value, coalesceTag: null, pushUndo: true);
            OnPropertyChanged(nameof(ActiveMatCullBackface));
            MarkDirty();
        }
    }

    public bool ActiveMatCastShadow
    {
        get => GetActiveMaterial()?.CastShadow ?? true;
        set
        {
            if (_syncingMaterialUi || !CanEditMaterialNow) return;
            EditSession.UpdateMaterial(ActiveMaterialIndex, m => m.CastShadow = value, coalesceTag: null, pushUndo: true);
            OnPropertyChanged(nameof(ActiveMatCastShadow));
            MarkDirty();
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);

    private void NotifyBaseColorChanged()
    {
        OnPropertyChanged(nameof(ActiveMatR));
        OnPropertyChanged(nameof(ActiveMatG));
        OnPropertyChanged(nameof(ActiveMatB));
        OnPropertyChanged(nameof(ActiveMatA));
        OnPropertyChanged(nameof(ActiveMatBaseColorHex));
        OnPropertyChanged(nameof(ActiveMatBaseBrush));
        RefreshActiveSlotSwatchInPlace();
    }

    private void NotifyEmissiveChanged()
    {
        OnPropertyChanged(nameof(ActiveMatEmR));
        OnPropertyChanged(nameof(ActiveMatEmG));
        OnPropertyChanged(nameof(ActiveMatEmB));
        OnPropertyChanged(nameof(ActiveMatEmStrength));
        OnPropertyChanged(nameof(ActiveMatEmissiveBrush));
    }

    private void RefreshActiveSlotSwatchInPlace()
    {
        int idx = ActiveMaterialIndex;
        if (idx < 0 || idx >= MaterialSlots.Count || !EditSession.IsActive || idx >= EditSession.Mesh.Materials.Count)
            return;
        var m = EditSession.Mesh.Materials[idx];
        var swatch = new SolidColorBrush(Color.FromArgb(ToByte(m.BaseColor.W), ToByte(m.BaseColor.X), ToByte(m.BaseColor.Y), ToByte(m.BaseColor.Z)));
        _syncingMaterialUi = true;
        try
        {
            MaterialSlots[idx] = new MaterialSlotItem
            {
                Index = idx,
                Name = m.Name,
                ColorSwatch = swatch,
                FaceCount = EditSession.CountFacesUsingMaterial(idx)
            };
            OnPropertyChanged(nameof(ActiveMaterialIndex));
        }
        finally
        {
            _syncingMaterialUi = false;
        }
    }

    private void ApplyActiveMaterialPreset(string preset)
    {
        if (!CanEditMaterialNow) return;
        EditSession.UpdateMaterial(ActiveMaterialIndex, m =>
        {
            switch (preset)
            {
                case "Matte":
                    m.Metallic = 0f; m.Roughness = 1.0f; m.Transmission = 0f;
                    m.EmissiveColor = new Vector4(m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z, 0f);
                    break;
                case "Plastic":
                    m.Metallic = 0f; m.Roughness = 0.25f; m.Transmission = 0f;
                    m.EmissiveColor = new Vector4(m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z, 0f);
                    break;
                case "Chrome":
                    m.BaseColor = new Vector4(0.95f, 0.95f, 0.96f, 1f);
                    m.Metallic = 1.0f; m.Roughness = 0.04f; m.Transmission = 0f;
                    m.EmissiveColor = new Vector4(m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z, 0f);
                    break;
                case "Gold":
                    m.BaseColor = new Vector4(1.0f, 0.78f, 0.34f, 1f);
                    m.Metallic = 1.0f; m.Roughness = 0.15f; m.Transmission = 0f;
                    m.EmissiveColor = new Vector4(m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z, 0f);
                    break;
                case "Glass":
                    m.BaseColor = Vector4.One;
                    m.Metallic = 0f; m.Roughness = 0.04f; m.Transmission = 1.0f; m.Ior = 1.45f;
                    m.EmissiveColor = new Vector4(m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z, 0f);
                    break;
                case "Emissive":
                    m.BaseColor = Vector4.One;
                    m.Metallic = 0f; m.Roughness = 0.5f; m.Transmission = 0f;
                    m.EmissiveColor = new Vector4(1f, 1f, 1f, 3.5f);
                    break;
                case "OutlineBackface":
                    m.BaseColor = new Vector4(0f, 0f, 0f, 1f);
                    m.Metallic = 0f; m.Roughness = 1.0f; m.Transmission = 0f;
                    m.EmissiveColor = Vector4.Zero;
                    m.ForceOpaque = true;
                    m.CullBackface = true;
                    m.CastShadow = false;
                    m.SourcePartIndex = -1;
                    m.NodeGraph = null;
                    break;
            }
        });
        SyncMaterialPropertiesFromSession(rebuildSlots: true);
        ShadingGraphChanged?.Invoke();
        MarkDirty();
    }

    private void ApplyActiveBaseColorHex(string hex)
    {
        if (!CanEditMaterialNow || string.IsNullOrWhiteSpace(hex)) return;
        try
        {
            var col = (Color)ColorConverter.ConvertFromString(hex);
            EditSession.UpdateMaterial(ActiveMaterialIndex, m =>
            {
                m.BaseColor = new Vector4(col.R / 255f, col.G / 255f, col.B / 255f, m.BaseColor.W);
            });
            NotifyBaseColorChanged();
            ShadingGraphChanged?.Invoke();
            MarkDirty();
        }
        catch { }
    }

    internal void SyncMaterialPropertiesFromSession(bool rebuildSlots)
    {
        _syncingMaterialUi = true;
        try
        {
            if (rebuildSlots)
            {
                MaterialSlots.Clear();
                if (CanEditMaterialNow)
                {
                    var mats = EditSession.Mesh.Materials;
                    for (int i = 0; i < mats.Count; i++)
                    {
                        var m = mats[i];
                        var swatch = new SolidColorBrush(Color.FromArgb(
                            ToByte(m.BaseColor.W), ToByte(m.BaseColor.X), ToByte(m.BaseColor.Y), ToByte(m.BaseColor.Z)));
                        MaterialSlots.Add(new MaterialSlotItem
                        {
                            Index = i,
                            Name = m.Name,
                            ColorSwatch = swatch,
                            FaceCount = EditSession.CountFacesUsingMaterial(i)
                        });
                    }
                }
            }

            OnPropertyChanged(nameof(ActiveMaterialIndex));
            OnPropertyChanged(nameof(ActiveMaterialName));
            OnPropertyChanged(nameof(ActiveMatR));
            OnPropertyChanged(nameof(ActiveMatG));
            OnPropertyChanged(nameof(ActiveMatB));
            OnPropertyChanged(nameof(ActiveMatA));
            OnPropertyChanged(nameof(ActiveMatBaseColorHex));
            OnPropertyChanged(nameof(ActiveMatBaseBrush));
            OnPropertyChanged(nameof(ActiveMatMetallic));
            OnPropertyChanged(nameof(ActiveMatRoughness));
            OnPropertyChanged(nameof(ActiveMatTransmission));
            OnPropertyChanged(nameof(ActiveMatIor));
            OnPropertyChanged(nameof(ActiveMatEmR));
            OnPropertyChanged(nameof(ActiveMatEmG));
            OnPropertyChanged(nameof(ActiveMatEmB));
            OnPropertyChanged(nameof(ActiveMatEmStrength));
            OnPropertyChanged(nameof(ActiveMatEmissiveBrush));
            OnPropertyChanged(nameof(ActiveMatHasOriginalTexture));
            OnPropertyChanged(nameof(ActiveMatCullBackface));
            OnPropertyChanged(nameof(ActiveMatCastShadow));
        }
        finally
        {
            _syncingMaterialUi = false;
        }
    }

    #endregion

    internal void SetMode(GizmoMode mode)
    {
        if (_isModelEditMode)
        {
            IsModelEditMode = false;
        }
        Manipulator.Mode = mode;
        OnPropertyChanged(nameof(IsTranslateMode));
        OnPropertyChanged(nameof(IsRotateMode));
        OnPropertyChanged(nameof(IsScaleMode));
        MarkDirty();
    }

    private void SetEditGizmoMode(GizmoMode mode)
    {
        if (EditSession.EditGizmoMode == mode) return;
        EditSession.EditGizmoMode = mode;
        OnPropertyChanged(nameof(IsEditGizmoTranslate));
        OnPropertyChanged(nameof(IsEditGizmoRotate));
        OnPropertyChanged(nameof(IsEditGizmoScale));
        MarkDirty();
    }

    private void ExportCurrentMeshToObj()
    {
        if (!EditSession.IsActive) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Wavefront OBJ (*.obj)|*.obj",
            DefaultExt = ".obj",
            FileName = "model_edited.obj"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                EditSession.ExportObj(dlg.FileName);
                StatusText = $"OBJ 保存完了: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                StatusText = $"OBJ 保存失敗: {ex.Message}";
            }
        }
    }

    public void ToggleSpace()
        => SetSpace(Manipulator.Space == GizmoSpace.World ? GizmoSpace.Local : GizmoSpace.World);

    private void SetSpace(GizmoSpace space)
    {
        if (Manipulator.Space == space) return;
        Manipulator.Space = space;
        OnPropertyChanged(nameof(SpaceLabel));
        OnPropertyChanged(nameof(IsWorldSpace));
        OnPropertyChanged(nameof(IsLocalSpace));
        MarkDirty();
    }

    public void ToggleProjection()
        => SetProjection(!Camera.IsOrthographic);

    private void SetProjection(bool orthographic)
    {
        if (Camera.IsOrthographic == orthographic) return;
        Camera.IsOrthographic = orthographic;
        OnPropertyChanged(nameof(ProjectionLabel));
        OnPropertyChanged(nameof(IsPerspective));
        OnPropertyChanged(nameof(IsOrthographic));
        MarkDirty();
    }

    public void ToggleKeyFrameMode()
        => SetEditKeyFrameOnly(!Manipulator.EditKeyFrameOnly);

    private void SetEditKeyFrameOnly(bool kfOnly)
    {
        if (Manipulator.EditKeyFrameOnly == kfOnly) return;
        Manipulator.EditKeyFrameOnly = kfOnly;
        OnPropertyChanged(nameof(KeyFrameModeLabel));
        OnPropertyChanged(nameof(IsEditAll));
        OnPropertyChanged(nameof(IsEditKeyFrameOnly));
        MarkDirty();
    }

    internal void ApplyPreset(ViewportCameraPreset preset)
    {
        Camera.SetPreset(preset);
        OnPropertyChanged(nameof(ProjectionLabel));
        OnPropertyChanged(nameof(IsPerspective));
        OnPropertyChanged(nameof(IsOrthographic));
        MarkDirty();
    }

    internal void ApplyAxisCompassTarget(AxisCompassTarget target)
    {
        if (AxisCompassGizmo.GetPreset(target) is { } preset)
        {
            ApplyPreset(preset);
        }
    }

    public void MatchYmmCamera()
    {
        if (_snapshot != null)
        {
            Camera.MatchYmmCamera(_snapshot.SceneCameraMatrix, _snapshot.VideoHeight);
        }
        else
        {
            Camera.Reset();
        }
        OnPropertyChanged(nameof(ProjectionLabel));
        OnPropertyChanged(nameof(IsPerspective));
        OnPropertyChanged(nameof(IsOrthographic));
        MarkDirty();
    }

    public void FocusSelected()
    {
        var sel = GetSelectedVideoItems();
        if (_snapshot == null || sel.Count == 0)
        {
            Camera.FocusOn(Vector3.Zero, 400f);
            MarkDirty();
            return;
        }

        Vector3 sum = Vector3.Zero;
        int n = 0;
        for (int i = 0; i < _snapshot.Items.Count; i++)
        {
            var e = _snapshot.Items[i];
            if (sel.Contains(e.Item))
            {
                sum += Manipulator.GetItemWorldPivot(e);
                n++;
            }
        }
        if (n > 0)
        {
            Camera.FocusOn(sum / n, 250f);
            MarkDirty();
        }
    }

    public void RefreshSnapshotFromBridge()
    {
        var next = Viewport3DBridge.TryAcquireLatestSnapshot();
        if (next == null) return;
        if (_snapshot != null && next.SequenceId == _snapshot.SequenceId)
        {
            next.Release();
            return;
        }

        _snapshot?.Release();
        _snapshot = next;
        Camera.SetVideoHeight(next.VideoHeight);

        int modelCount = 0;
        int lightCount = 0;
        for (int i = 0; i < next.Items.Count; i++)
        {
            if (next.Items[i].Kind == ViewportItemKind.BuiltInModel) modelCount++;
            else if (next.Items[i].Kind == ViewportItemKind.Light) lightCount++;
        }
        SceneSummaryText = $"要素: {next.Items.Count} (モデル:{modelCount} 光源:{lightCount}) | {next.VideoWidth}x{next.VideoHeight}";
        MarkDirty();
    }

    public IReadOnlyList<IVideoItem> GetSelectedVideoItems()
    {
        if (_timeline == null) return Array.Empty<IVideoItem>();
        return _timeline.SelectedItems.OfType<IVideoItem>().ToArray();
    }

    internal CameraPath.D3DCameraPathVideoEffect? FindActiveCameraPathEffect()
    {
        if (_timeline != null)
        {
            foreach (var item in _timeline.SelectedItems)
            {
                if (item is IVideoItem vi && vi.VideoEffects != null)
                {
                    foreach (var eff in vi.VideoEffects)
                    {
                        if (eff is CameraPath.D3DCameraPathVideoEffect cp && cp.IsEnabled)
                            return cp;
                    }
                }
                else if (item != null)
                {
                    var effProp = item.GetType().GetProperty("Effects");
                    if (effProp?.GetValue(item) is System.Collections.IEnumerable effects)
                    {
                        foreach (var eff in effects)
                        {
                            if (eff is CameraPath.D3DCameraPathVideoEffect cp && cp.IsEnabled)
                                return cp;
                        }
                    }
                }
            }
        }
        return CameraPath.D3DCameraPathViewportBridge.ActiveEffect;
    }

    internal D3DPrimitiveShapeParameter? FindActiveBezierCurveParameter()
    {
        var selected = GetSelectedVideoItems();
        for (int i = 0; i < selected.Count; i++)
        {
            if (selected[i] is ShapeItem si &&
                si.ShapeParameter is D3DPrimitiveShapeParameter p &&
                p.ShapeKind == D3DPrimitiveShapeKind.BezierCurve &&
                string.IsNullOrWhiteSpace(p.EmbeddedMeshData))
            {
                return p;
            }
        }
        return null;
    }

    private void OnCameraPathBridgeChanged() => MarkDirty();

    private void OnCameraPathFocusRequested(CameraPath.D3DCameraPathVideoEffect effect)
    {
        void DoFocus()
        {
            if (_disposed) return;
            CameraPathController.FocusPath(effect, Camera);
            MarkDirty();
        }
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.BeginInvoke(DoFocus);
        else DoFocus();
    }

    private void OnCameraPathFocusKeyframeRequested(CameraPath.D3DCameraPathVideoEffect effect, int index)
    {
        void DoFocus()
        {
            if (_disposed) return;
            CameraPathController.FocusKeyframe(effect, index, Camera);
            MarkDirty();
        }
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.BeginInvoke(DoFocus);
        else DoFocus();
    }

    private void OnCameraPathAddCurrentViewRequested(CameraPath.D3DCameraPathVideoEffect effect)
    {
        void DoAdd()
        {
            if (_disposed) return;
            CameraPathController.AddCurrentViewportCamera(effect, Camera, _undoRedoManager);
            MarkDirty();
        }
        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.BeginInvoke(DoAdd);
        else DoAdd();
    }

    private void OnBezierCurveEditorSelectionChanged(D3DPrimitiveShapeParameter param, int index)
        => MarkDirty();

    public void SelectPickedItem(IVideoItem? item, bool toggle)
    {
        if (_timeline == null) return;

        if (item == null)
        {
            if (!toggle && !_timeline.SelectedItems.IsEmpty)
            {
                _timeline.SelectedItems = ImmutableList<IItem>.Empty;
            }
            return;
        }

        if (toggle)
        {
            _timeline.ToggleSelectionStatus(item);
        }
        else if (!_timeline.SelectedItems.Contains(item) || _timeline.SelectedItems.Count > 1)
        {
            _timeline.SelectItem(item);
        }
        MarkDirty();
    }

    public void UpdateSelectionStatus()
    {
        if (IsShadingWorkspace)
        {
            if (EditSession.IsActive)
            {
                string mName = EditSession.Mesh.Materials.ElementAtOrDefault(ActiveMaterialIndex)?.Name ?? "Material";
                StatusText = $"[シェーディング] {ShadingTargetName} | スロット: [{ActiveMaterialIndex}] {mName} | 右クリック/Shift+A: ノード追加 | Del/X: 削除 | Ctrl+Z/Y: 戻す/やり直す";
            }
            else
            {
                StatusText = "[シェーディング] タイムライン上で「D3D11 3Dモデル」または「D3D11 図形」を選択してください";
            }
            return;
        }
        if (!string.IsNullOrEmpty(_editModeNotice) && !IsModelEditMode)
        {
            StatusText = _editModeNotice;
            return;
        }
        if (IsModelEditMode && EditSession.IsActive)
        {
            StatusText = EditSession.BuildStatusText();
            return;
        }
        StatusText = Manipulator.BuildSelectionStatus(GetSelectedVideoItems(), _snapshot);
    }

    public async void TryUndo()
    {
        if (IsModelEditMode && EditSession.IsActive)
        {
            if (EditSession.Undo())
            {
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                ShadingGraphChanged?.Invoke();
                NotifyModifierStateChanged();
                ModifiersChanged?.Invoke();
                MarkDirty();
            }
            return;
        }
        if (CanEditMaterialNow && EditSession.Undo())
        {
            SyncMaterialPropertiesFromSession(rebuildSlots: true);
            ShadingGraphChanged?.Invoke();
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
            return;
        }
        if (_undoRedoManager == null || !_undoRedoManager.IsUndoable) return;
        try
        {
            await _undoRedoManager.UndoAsync();
            DepthSortVideoPlayerPatch.SetTimelineChanged();
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
        }
        catch { }
    }

    public async void TryRedo()
    {
        if (IsModelEditMode && EditSession.IsActive)
        {
            if (EditSession.Redo())
            {
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                ShadingGraphChanged?.Invoke();
                NotifyModifierStateChanged();
                ModifiersChanged?.Invoke();
                MarkDirty();
            }
            return;
        }
        if (CanEditMaterialNow && EditSession.Redo())
        {
            SyncMaterialPropertiesFromSession(rebuildSlots: true);
            ShadingGraphChanged?.Invoke();
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
            return;
        }
        if (_undoRedoManager == null || !_undoRedoManager.IsRedoable) return;
        try
        {
            await _undoRedoManager.RedoAsync();
            DepthSortVideoPlayerPatch.SetTimelineChanged();
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
        }
        catch { }
    }

    public void SetTimelineToolInfo(TimelineToolInfo info)
    {
        if (_timeline != null)
            _timeline.PropertyChanged -= OnTimelinePropertyChanged;

        _timeline = info.Timeline;
        _undoRedoManager = info.UndoRedoManager;

        if (_timeline != null)
        {
            _timeline.PropertyChanged += OnTimelinePropertyChanged;
            Camera.SetVideoHeight(_timeline.VideoInfo.Height);
        }

        DepthSortVideoPlayerPatch.SetTimelineChanged();
        MarkDirty();
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Timeline.SelectedItem) or nameof(Timeline.SelectedItems))
        {
            _editModeNotice = null;
            if ((_isModelEditMode || IsShadingWorkspace || IsMaterialPanelOpen) && EditSession.IsActive)
            {
                var sel = GetSelectedVideoItems();
                if (!sel.Contains(EditSession.TargetItem))
                {
                    var nextTarget = FindEditableModelItem();
                    if (nextTarget != null && EditSession.Enter(nextTarget, _snapshot, Manipulator))
                    {
                        EditSession.SelectMode = ToEditElementMode(_meshSelectMode);
                        if (IsShadingWorkspace) EditSession.EnsureActiveNodeGraph();
                        SyncMaterialPropertiesFromSession(rebuildSlots: true);
                        NotifyEditModeStateChanged();
                        ShadingGraphChanged?.Invoke();
                    }
                    else
                    {
                        if (_isModelEditMode) IsModelEditMode = false;
                        else
                        {
                            EditSession.Exit(commitChanges: true);
                            SyncMaterialPropertiesFromSession(rebuildSlots: true);
                            NotifyEditModeStateChanged();
                            ShadingGraphChanged?.Invoke();
                        }
                        return;
                    }
                }
            }
            else if ((IsShadingWorkspace || IsMaterialPanelOpen) && !EditSession.IsActive)
            {
                if (IsShadingWorkspace) EnsureShadingSessionActive();
                else EnsureMaterialSessionActive();
                SyncMaterialPropertiesFromSession(rebuildSlots: true);
                NotifyEditModeStateChanged();
                ShadingGraphChanged?.Invoke();
            }
            SyncWatchedModifierParam();
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
        }
    }

    private void OnBridgeFrameUpdated()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _refreshPending, 1) != 0) return;

        var appDisp = Application.Current?.Dispatcher;
        if (appDisp != null && !appDisp.CheckAccess())
        {
            appDisp.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                Volatile.Write(ref _refreshPending, 0);
                if (!_disposed) RefreshSnapshotFromBridge();
            }));
        }
        else
        {
            Volatile.Write(ref _refreshPending, 0);
            RefreshSnapshotFromBridge();
        }
    }

    // ── IToolViewModel ──
    public string? Title => "D3D11 3Dビューポート";
#pragma warning disable CS0067
    public event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested;
#pragma warning restore CS0067
    public ToolState SaveState() => new ToolState();
    public void LoadState(ToolState stateData) { }

    // ── INotifyPropertyChanged ──
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnEditSessionSourceModelReloaded()
    {
        void Apply()
        {
            if (_disposed || !EditSession.IsActive) return;
            if (IsShadingWorkspace) EditSession.EnsureActiveNodeGraph();
            SyncMaterialPropertiesFromSession(rebuildSlots: true);
            SyncWatchedModifierParam();
            NotifyEditModeStateChanged();
            NotifyModifierStateChanged();
            ShadingGraphChanged?.Invoke();
            ModifiersChanged?.Invoke();
            UpdateSelectionStatus();
            MarkDirty();
        }

        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess())
            disp.BeginInvoke(DispatcherPriority.Render, new Action(Apply));
        else
            Apply();
    }

    #region モディファイア管理 (オブジェクトモード / 編集モード共通)

    internal Modifiers.MeshModifierStack GetCurrentModifierStack()
    {
        if (EditSession.IsActive)
            return EditSession.Modifiers;

        var param = ModifierTargetParameter;
        if (param == null)
            return new Modifiers.MeshModifierStack();

        Modifiers.MeshModifierStack.TryDeserialize(param.ModifierStackData, out var stack);
        return stack;
    }

    internal List<(int Layer, string Label)> GetAvailableSceneModifierTargets()
    {
        var list = new List<(int Layer, string Label)>();
        if (_snapshot == null) return list;

        var selfItem = ModifierTargetItem;
        var seenLayers = new HashSet<int>();
        for (int i = 0; i < _snapshot.Items.Count; i++)
        {
            var e = _snapshot.Items[i];
            if (e.Kind != ViewportItemKind.BuiltInModel) continue;
            if (ReferenceEquals(e.Item, selfItem)) continue;
            if (!seenLayers.Add(e.Layer)) continue;
            list.Add((e.Layer, $"L{e.Layer}: {e.DisplayName}"));
        }
        list.Sort((a, b) => a.Layer.CompareTo(b.Layer));
        return list;
    }

    public Modifiers.MeshModifierItem? AddModifier(Modifiers.MeshModifierType type)
    {
        var param = ModifierTargetParameter;
        if (param == null) return null;

        Action<Modifiers.MeshModifierItem>? init = null;
        if (type is Modifiers.MeshModifierType.Boolean or Modifiers.MeshModifierType.Shrinkwrap)
        {
            var others = GetAvailableSceneModifierTargets();
            if (others.Count > 0)
            {
                int targetLayer = others[0].Layer;
                init = m =>
                {
                    m.TargetSource = Modifiers.ModifierTargetSourceKind.SceneLayer;
                    m.TargetLayer = targetLayer;
                };
            }
        }

        Modifiers.MeshModifierItem? added;
        if (EditSession.IsActive)
        {
            added = EditSession.AddModifier(type, init);
        }
        else
        {
            Modifiers.MeshModifierStack.TryDeserialize(param.ModifierStackData, out var stack);
            added = stack.AddModifier(type);
            init?.Invoke(added);
            WriteObjectModifierStack(param, stack, recordUndo: true);
        }

        if (added != null)
        {
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
        }
        return added;
    }

    public bool RemoveModifier(int modifierId)
    {
        bool ok;
        if (EditSession.IsActive)
        {
            ok = EditSession.RemoveModifier(modifierId);
        }
        else if (ModifierTargetParameter is { } param)
        {
            Modifiers.MeshModifierStack.TryDeserialize(param.ModifierStackData, out var stack);
            int idx = stack.Items.FindIndex(m => m.Id == modifierId);
            if (idx < 0) return false;
            stack.Items.RemoveAt(idx);
            WriteObjectModifierStack(param, stack, recordUndo: true);
            ok = true;
        }
        else return false;

        if (ok)
        {
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
        }
        return ok;
    }

    public bool MoveModifier(int modifierId, int delta)
    {
        bool ok;
        if (EditSession.IsActive)
        {
            ok = EditSession.MoveModifier(modifierId, delta);
        }
        else if (ModifierTargetParameter is { } param)
        {
            Modifiers.MeshModifierStack.TryDeserialize(param.ModifierStackData, out var stack);
            int idx = stack.Items.FindIndex(m => m.Id == modifierId);
            int nextIdx = idx + delta;
            if (idx < 0 || nextIdx < 0 || nextIdx >= stack.Items.Count || idx == nextIdx) return false;
            var item = stack.Items[idx];
            stack.Items.RemoveAt(idx);
            stack.Items.Insert(nextIdx, item);
            WriteObjectModifierStack(param, stack, recordUndo: true);
            ok = true;
        }
        else return false;

        if (ok)
        {
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
        }
        return ok;
    }

    public bool ToggleModifier(int modifierId)
    {
        bool ok;
        if (EditSession.IsActive)
        {
            ok = EditSession.ToggleModifier(modifierId);
        }
        else if (ModifierTargetParameter is { } param)
        {
            Modifiers.MeshModifierStack.TryDeserialize(param.ModifierStackData, out var stack);
            int idx = stack.Items.FindIndex(m => m.Id == modifierId);
            if (idx < 0) return false;
            stack.Items[idx].Enabled = !stack.Items[idx].Enabled;
            WriteObjectModifierStack(param, stack, recordUndo: true);
            ok = true;
        }
        else return false;

        if (ok)
        {
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
        }
        return ok;
    }

    public bool ToggleModifierExpanded(int modifierId)
    {
        bool ok;
        if (EditSession.IsActive)
        {
            ok = EditSession.ToggleModifierExpanded(modifierId);
        }
        else if (ModifierTargetParameter is { } param)
        {
            Modifiers.MeshModifierStack.TryDeserialize(param.ModifierStackData, out var stack);
            int idx = stack.Items.FindIndex(m => m.Id == modifierId);
            if (idx < 0) return false;
            stack.Items[idx].Expanded = !stack.Items[idx].Expanded;
            WriteObjectModifierStack(param, stack, recordUndo: false);
            ok = true;
        }
        else return false;

        if (ok) ModifiersChanged?.Invoke();
        return ok;
    }

    internal bool MutateModifier(
        int modifierId,
        Action<Modifiers.MeshModifierItem> mutator,
        string? coalesceTag = null,
        bool commitUndoRecord = true)
    {
        bool ok;
        if (EditSession.IsActive)
        {
            ok = EditSession.UpdateModifier(modifierId, mutator, coalesceTag, pushUndo: true);
        }
        else if (ModifierTargetParameter is { } param)
        {
            Modifiers.MeshModifierStack.TryDeserialize(param.ModifierStackData, out var stack);
            int idx = stack.Items.FindIndex(m => m.Id == modifierId);
            if (idx < 0) return false;
            mutator(stack.Items[idx]);
            WriteObjectModifierStack(param, stack, recordUndo: commitUndoRecord);
            ok = true;
        }
        else return false;

        if (ok) MarkDirty();
        return ok;
    }

    public void CommitModifierUndoRecord()
    {
        if (!EditSession.IsActive && ModifierTargetParameter != null)
            _undoRedoManager?.Record();
    }

    public bool ApplyModifier(int modifierId)
    {
        var sceneCtx = BuildSceneModifierContextFromSnapshot(out int selfLayer, out var selfWorld);
        if (EditSession.IsActive)
        {
            int curLayer = selfLayer >= 0 ? selfLayer : (EditSession.TargetItem?.Layer ?? -1);
            if (!EditSession.ApplyModifier(modifierId, sceneCtx, curLayer))
                return false;

            SyncMaterialPropertiesFromSession(rebuildSlots: true);
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            MarkDirty();
            return true;
        }

        var param = ModifierTargetParameter;
        if (param == null) return false;

        Modifiers.MeshModifierStack.TryDeserialize(param.ModifierStackData, out var stack);
        int idx = stack.Items.FindIndex(m => m.Id == modifierId);
        if (idx < 0) return false;

        var mod = stack.Items[idx];
        var targetItem = ModifierTargetItem;
        Func<EditableMesh>? procFactory = null;
        if (param is D3DPrimitiveShapeParameter primParam && string.IsNullOrWhiteSpace(param.EmbeddedMeshData))
        {
            ViewportItemEntry? snapEntry = null;
            if (_snapshot != null && targetItem != null)
                snapEntry = _snapshot.Items.FirstOrDefault(e => ReferenceEquals(e.Item, targetItem));
            if (snapEntry?.ModelSpec?.ProceduralMeshFactory != null)
                procFactory = snapEntry.ModelSpec.ProceduralMeshFactory;
            else
                procFactory = () => primParam.CreateDefaultEditableMesh();
        }

        var baseMesh = ModelGpuCache.GetOrLoadBaseEditableMesh(
            param.EmbeddedMeshData, param.FilePath, procFactory, out _);
        if (baseMesh == null) return false;

        var baked = Modifiers.MeshModifierEvaluator.EvaluateSingle(
            baseMesh, mod, sceneCtx, selfLayer, selfWorld, ignoreEnabled: true);
        baked.SourceFilePath = param.FilePath ?? string.Empty;

        _mutatingModifierData = true;
        try
        {
            param.EmbeddedMeshData = baked.Serialize();
            stack.Items.RemoveAt(idx);
            param.ModifierStackData = stack.Serialize();
        }
        finally
        {
            _mutatingModifierData = false;
        }

        _undoRedoManager?.Record();
        DepthSortVideoPlayerPatch.SetTimelineChanged();
        NotifyModifierStateChanged();
        ModifiersChanged?.Invoke();
        MarkDirty();
        return true;
    }

    public bool SetupSolidifyBackfaceOutline(int modifierId)
    {
        static void ApplyOutlineProps(EditableMaterial m)
        {
            m.Name = "輪郭(背面)";
            m.BaseColor = new Vector4(0f, 0f, 0f, 1f);
            m.Metallic = 0f;
            m.Roughness = 1.0f;
            m.Transmission = 0f;
            m.EmissiveColor = Vector4.Zero;
            m.ForceOpaque = true;
            m.CullBackface = true;
            m.CastShadow = false;
            m.SourcePartIndex = -1;
            m.NodeGraph = null;
        }

        if (EditSession.IsActive)
        {
            var mats = EditSession.Mesh.Materials;
            int outlineSlotIdx = mats.FindIndex(m => (m.CullBackface && !m.CastShadow) || m.Name.Contains("輪郭", StringComparison.Ordinal));
            if (outlineSlotIdx < 0)
            {
                if (mats.Count <= 1)
                    outlineSlotIdx = EditSession.AddMaterialSlot("輪郭(背面)");
                else
                    outlineSlotIdx = mats.Count - 1;
            }
            if (outlineSlotIdx < 0) return false;

            EditSession.UpdateMaterial(outlineSlotIdx, ApplyOutlineProps, coalesceTag: null, pushUndo: true);
            EditSession.UpdateModifier(modifierId, m =>
            {
                m.SolidifyThickness = MathF.Max(MathF.Abs(m.SolidifyThickness), 0.03f);
                m.SolidifyOffset = 1.0f;
                m.SolidifyFlipNormals = true;
                m.SolidifyMaterialOffset = outlineSlotIdx;
            }, coalesceTag: null, pushUndo: true);

            SyncMaterialPropertiesFromSession(rebuildSlots: true);
            NotifyModifierStateChanged();
            ModifiersChanged?.Invoke();
            ShadingGraphChanged?.Invoke();
            MarkDirty();
            return true;
        }

        var param = ModifierTargetParameter;
        if (param == null) return false;

        Modifiers.MeshModifierStack.TryDeserialize(param.ModifierStackData, out var stack);
        int idx = stack.Items.FindIndex(m => m.Id == modifierId);
        if (idx < 0) return false;

        var targetItem = ModifierTargetItem;
        Func<EditableMesh>? procFactory = null;
        if (param is D3DPrimitiveShapeParameter primParam && string.IsNullOrWhiteSpace(param.EmbeddedMeshData))
        {
            ViewportItemEntry? snapEntry = null;
            if (_snapshot != null && targetItem != null)
                snapEntry = _snapshot.Items.FirstOrDefault(e => ReferenceEquals(e.Item, targetItem));
            if (snapEntry?.ModelSpec?.ProceduralMeshFactory != null)
                procFactory = snapEntry.ModelSpec.ProceduralMeshFactory;
            else
                procFactory = () => primParam.CreateDefaultEditableMesh();
        }

        var baseMesh = ModelGpuCache.GetOrLoadBaseEditableMesh(
            param.EmbeddedMeshData, param.FilePath, procFactory, out _);
        if (baseMesh == null) return false;

        int slotIdx = baseMesh.Materials.FindIndex(m => (m.CullBackface && !m.CastShadow) || m.Name.Contains("輪郭", StringComparison.Ordinal));
        if (slotIdx < 0)
        {
            if (baseMesh.Materials.Count <= 1)
            {
                slotIdx = baseMesh.Materials.Count;
                baseMesh.Materials.Add(new EditableMaterial());
            }
            else
            {
                slotIdx = baseMesh.Materials.Count - 1;
            }
        }
        ApplyOutlineProps(baseMesh.Materials[slotIdx]);
        baseMesh.SourceFilePath = param.FilePath ?? string.Empty;

        var mod = stack.Items[idx];
        mod.SolidifyThickness = MathF.Max(MathF.Abs(mod.SolidifyThickness), 0.03f);
        mod.SolidifyOffset = 1.0f;
        mod.SolidifyFlipNormals = true;
        mod.SolidifyMaterialOffset = slotIdx;

        _mutatingModifierData = true;
        try
        {
            param.EmbeddedMeshData = baseMesh.Serialize();
            param.ModifierStackData = stack.Serialize();
        }
        finally
        {
            _mutatingModifierData = false;
        }

        _undoRedoManager?.Record();
        DepthSortVideoPlayerPatch.SetTimelineChanged();
        NotifyModifierStateChanged();
        ModifiersChanged?.Invoke();
        MarkDirty();
        return true;
    }

    private Modifiers.SceneModifierContext? BuildSceneModifierContextFromSnapshot(out int selfLayer, out Matrix4x4 selfWorld)
    {
        selfLayer = -1;
        selfWorld = Matrix4x4.Identity;
        var selfItem = ModifierTargetItem;
        if (selfItem != null) selfLayer = selfItem.Layer;
        if (_snapshot == null) return null;

        var ctx = new Modifiers.SceneModifierContext();
        for (int i = 0; i < _snapshot.Items.Count; i++)
        {
            var entry = _snapshot.Items[i];
            if (entry.Kind != ViewportItemKind.BuiltInModel || entry.ModelSpec == null) continue;

            var effDesc = Manipulator.GetEffectiveDrawDescription(entry);
            var sp = entry.ModelSpec;
            Vector3 lScale = sp.LocalScale;
            Vector3 lRot = sp.LocalRotationDeg;
            if (Manipulator.GetLiveOverride(entry.Item) is { } ov)
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
            Matrix4x4 world = DepthSortRenderer.BuildObjModelMatrix(new RenderItem { DrawDescription = effDesc, Srv = null }, dummy);
            if (ReferenceEquals(entry.Item, selfItem))
                selfWorld = world;

            bool isEdit = EditSession.IsActive && ReferenceEquals(EditSession.TargetItem, entry.Item);
            if (isEdit)
            {
                ctx.RegisterTarget(new Modifiers.SceneModifierTarget
                {
                    Layer = entry.Layer,
                    DisplayName = entry.DisplayName,
                    BaseKey = $"edit:{entry.Item.GetHashCode()}",
                    ModelToWorld = EditSession.ModelToWorld,
                    ResolveBaseMesh = () => EditSession.Mesh.Clone(),
                    ModifierStackData = EditSession.Modifiers.Serialize(),
                });
            }
            else
            {
                var lp = entry.Item is ShapeItem si ? si.ShapeParameter as Model3D.ID3DModelTransformParameter : null;
                string emb = lp?.EmbeddedMeshData ?? sp.EmbeddedMeshData;
                string modData = lp?.ModifierStackData ?? sp.ModifierStackData;
                ctx.RegisterTarget(new Modifiers.SceneModifierTarget
                {
                    Layer = entry.Layer,
                    DisplayName = entry.DisplayName,
                    BaseKey = !string.IsNullOrWhiteSpace(emb) ? $"emb:{emb.GetHashCode(StringComparison.Ordinal)}" : sp.ProceduralCacheKey,
                    ModelToWorld = world,
                    ResolveBaseMesh = () => ModelGpuCache.GetOrLoadBaseEditableMesh(emb, sp.FilePath, sp.ProceduralMeshFactory, out _),
                    ModifierStackData = modData,
                });
            }
        }
        return ctx;
    }

    private void WriteObjectModifierStack(Model3D.ID3DModelTransformParameter param, Modifiers.MeshModifierStack stack, bool recordUndo)
    {
        _mutatingModifierData = true;
        try
        {
            param.ModifierStackData = stack.Serialize();
        }
        finally
        {
            _mutatingModifierData = false;
        }
        if (recordUndo) _undoRedoManager?.Record();
        DepthSortVideoPlayerPatch.SetTimelineChanged();
    }

    private void SyncWatchedModifierParam()
    {
        var cur = ModifierTargetParameter;
        if (ReferenceEquals(_watchedModifierParam, cur)) return;
        if (_watchedModifierParam is INotifyPropertyChanged oldNpc)
            oldNpc.PropertyChanged -= OnWatchedModifierParamChanged;
        _watchedModifierParam = cur;
        if (_watchedModifierParam is INotifyPropertyChanged newNpc)
            newNpc.PropertyChanged += OnWatchedModifierParamChanged;
    }

    private void OnWatchedModifierParamChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || _mutatingModifierData || EditSession.IsActive) return;
        if (e.PropertyName is nameof(Model3D.ID3DModelTransformParameter.ModifierStackData)
            or nameof(D3DPrimitiveShapeParameter.ShapeKind)
            or nameof(Model3D.ID3DModelTransformParameter.FilePath))
        {
            void Refresh()
            {
                if (_disposed || EditSession.IsActive) return;
                NotifyModifierStateChanged();
                ModifiersChanged?.Invoke();
                MarkDirty();
            }
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.BeginInvoke(DispatcherPriority.Render, new Action(Refresh));
            else
                Refresh();
        }
    }

    private void NotifyModifierStateChanged()
    {
        OnPropertyChanged(nameof(HasModifierTarget));
        OnPropertyChanged(nameof(ModifierTargetName));
        OnPropertyChanged(nameof(ModifierCount));
        OnPropertyChanged(nameof(ModifierButtonLabel));
        OnPropertyChanged(nameof(IsLayoutModifierPanelVisible));
    }

    #endregion

    public event EventHandler? Disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_watchedModifierParam is INotifyPropertyChanged oldNpc)
            oldNpc.PropertyChanged -= OnWatchedModifierParamChanged;
        _watchedModifierParam = null;

        EditSession.SourceModelReloaded -= OnEditSessionSourceModelReloaded;
        Viewport3DBridge.FrameUpdated -= OnBridgeFrameUpdated;
        CameraPath.D3DCameraPathViewportBridge.StateChanged -= OnCameraPathBridgeChanged;
        CameraPath.D3DCameraPathViewportBridge.FocusPathRequested -= OnCameraPathFocusRequested;
        CameraPath.D3DCameraPathViewportBridge.FocusKeyframeRequested -= OnCameraPathFocusKeyframeRequested;
        CameraPath.D3DCameraPathViewportBridge.AddCurrentViewportCameraRequested -= OnCameraPathAddCurrentViewRequested;
        Model3D.ViewportBezierCurveBridge.SelectionChangedFromEditor -= OnBezierCurveEditorSelectionChanged;
        if (_timeline != null)
        {
            _timeline.PropertyChanged -= OnTimelinePropertyChanged;
            _timeline = null;
        }

        EditSession.Dispose();
        _snapshot?.Release();
        _snapshot = null;

        Renderer.Dispose();
        Disposed?.Invoke(this, EventArgs.Empty);
    }
}
