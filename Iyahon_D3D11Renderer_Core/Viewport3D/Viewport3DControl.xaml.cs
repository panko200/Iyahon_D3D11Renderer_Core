using System;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Iyahon_D3D11Renderer_Core.Viewport3D.Modeling;
using Vortice.Direct3D11;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D;

public partial class Viewport3DControl : UserControl
{
    private ViewportD3D11Host? _host;
    private Viewport3DViewModel? _vm;
    private bool _registeredBridge;

    private Point _lastMousePos;
    private Point _leftDownPos;
    private bool _leftDownActive;
    private bool _leftWasEmptySpace;
    private bool _isOrbitDragging;
    private bool _isPanDragging;
    private bool _isRollDragging;
    private bool _compassDownActive;
    private AxisCompassTarget _compassDownTarget = AxisCompassTarget.None;
    private readonly ToolTip _compassToolTip;
    private readonly ContextMenu _addMeshMenu;
    private readonly ContextMenu _deleteMeshMenu;
    private GridLength _savedShadingRowHeight = new(1.2, GridUnitType.Star);

    public Viewport3DControl()
    {
        InitializeComponent();
        _compassToolTip = new ToolTip
        {
            Style = TryFindResource("DarkToolTipStyle") as Style,
            Placement = PlacementMode.Relative,
            PlacementTarget = ViewportContainer,
            StaysOpen = true,
            IsHitTestVisible = false,
        };
        _addMeshMenu = BuildAddMeshMenu();
        _deleteMeshMenu = BuildDeleteMeshMenu();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private ContextMenu CreateStyledMenu() => new()
    {
        Style = (Style)FindResource("DarkContextMenu")
    };

    private MenuItem CreateActionItem(string header, Action onClick)
    {
        var mi = new MenuItem
        {
            Style = (Style)FindResource("DarkMenuItem"),
            Header = header
        };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private ContextMenu BuildAddMeshMenu()
    {
        var menu = CreateStyledMenu();
        void AddPrim(string label, EditablePrimitiveType type)
        {
            menu.Items.Add(CreateActionItem(label, () =>
            {
                if (_vm is { IsModelEditMode: true } && _vm.EditSession.IsActive &&
                    _vm.EditSession.AddPrimitive(type))
                {
                    _vm.MarkDirty();
                }
            }));
        }
        AddPrim("頂点 (Single Vertex)", EditablePrimitiveType.Vertex);
        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparator") });
        AddPrim("平面 (Plane)", EditablePrimitiveType.Plane);
        AddPrim("円 (Circle)", EditablePrimitiveType.Circle);
        AddPrim("立方体 (Cube)", EditablePrimitiveType.Cube);
        AddPrim("UV球 (UV Sphere)", EditablePrimitiveType.UvSphere);
        AddPrim("ICO球 (Ico Sphere)", EditablePrimitiveType.IcoSphere);
        AddPrim("円柱 (Cylinder)", EditablePrimitiveType.Cylinder);
        AddPrim("円錐 (Cone)", EditablePrimitiveType.Cone);
        AddPrim("トーラス (Torus)", EditablePrimitiveType.Torus);
        return menu;
    }

    private ContextMenu BuildDeleteMeshMenu()
    {
        var menu = CreateStyledMenu();
        void AddDel(string label, MeshDeleteMode mode)
        {
            menu.Items.Add(CreateActionItem(label, () =>
            {
                if (_vm is { IsModelEditMode: true } && _vm.EditSession.IsActive &&
                    _vm.EditSession.Delete(mode))
                {
                    _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                    _vm.MarkDirty();
                }
                _host?.FocusHost();
            }));
        }
        AddDel("頂点 (Vertices)", MeshDeleteMode.Vertices);
        AddDel("辺 (Edges)", MeshDeleteMode.Edges);
        AddDel("面 (Faces)", MeshDeleteMode.Faces);
        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparator") });
        AddDel("辺と面のみ (Only Edges & Faces)", MeshDeleteMode.OnlyEdgesAndFaces);
        AddDel("面のみ (Only Faces)", MeshDeleteMode.OnlyFaces);
        return menu;
    }

    private void OnAddMeshButtonClick(object sender, RoutedEventArgs e)
    {
        _addMeshMenu.PlacementTarget = sender as UIElement ?? ViewportContainer;
        _addMeshMenu.Placement = PlacementMode.Bottom;
        _addMeshMenu.IsOpen = true;
    }

    private void OnDeleteMeshButtonClick(object sender, RoutedEventArgs e)
    {
        _deleteMeshMenu.PlacementTarget = sender as UIElement ?? ViewportContainer;
        _deleteMeshMenu.Placement = PlacementMode.Bottom;
        _deleteMeshMenu.IsOpen = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is Viewport3DViewModel oldVm)
        {
            oldVm.Disposed -= OnViewModelDisposed;
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = e.NewValue as Viewport3DViewModel;
        NodeEditorControl.BindViewModel(_vm);
        ModifierPanelControl.BindViewModel(_vm);
        if (_vm != null)
        {
            _vm.Disposed += OnViewModelDisposed;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyWorkspaceRowHeight();
            _vm.MarkDirty();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Viewport3DViewModel.ActiveWorkspaceTab) or nameof(Viewport3DViewModel.IsShadingWorkspace))
        {
            ApplyWorkspaceRowHeight();
        }
    }

    private void ApplyWorkspaceRowHeight()
    {
        if (_vm == null) return;
        if (_vm.IsShadingWorkspace)
        {
            ShadingRow.MinHeight = 150;
            ShadingRow.Height = _savedShadingRowHeight.Value > 0 ? _savedShadingRowHeight : new GridLength(1.2, GridUnitType.Star);
            NodeEditorControl.RefreshFromActiveMaterial();
        }
        else
        {
            if (ShadingRow.ActualHeight > 60)
                _savedShadingRowHeight = new GridLength(ShadingRow.ActualHeight, GridUnitType.Pixel);
            ShadingRow.MinHeight = 0;
            ShadingRow.Height = new GridLength(0);
        }
    }

    private void OnViewModelDisposed(object? sender, EventArgs e)
    {
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        if (sender is Viewport3DViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        NodeEditorControl.BindViewModel(null);
        ModifierPanelControl.BindViewModel(null);
        CloseCompassTooltip();
        TeardownHost();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_registeredBridge)
        {
            Viewport3DBridge.RegisterViewport();
            _registeredBridge = true;
            DepthSortVideoPlayerPatch.SetTimelineChanged();
        }

        if (_host == null)
        {
            _host = new ViewportD3D11Host();
            _host.Render += OnHostRender;
            _host.MouseAction += OnHostMouseAction;
            _host.KeyAction += OnHostKeyAction;
            _host.KeyDownInterceptor = HandleShortcut;
            _host.DeviceLostOrRecreated += OnHostDeviceReset;
            ViewportContainer.Child = _host;
        }

        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        ComponentDispatcher.ThreadFilterMessage += OnThreadFilterMessage;

        _vm?.RefreshSnapshotFromBridge();
        _vm?.MarkDirty();
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ComponentDispatcher.ThreadFilterMessage -= OnThreadFilterMessage;
        CompositionTarget.Rendering -= OnCompositionRendering;
        _vm?.Manipulator.CancelDrag();
        if (_vm is { IsModelEditMode: true })
        {
            _vm.EditSession.CancelModalOrLoopCut();
            _vm.EditSession.CancelGizmoDrag();
        }
        CloseCompassTooltip();

        if (_registeredBridge)
        {
            Viewport3DBridge.UnregisterViewport();
            _registeredBridge = false;
        }

        TeardownHost();
    }

    private bool IsOwnTextInputFocused(DependencyObject? source)
        => IsOwnTextInputElement(source) || IsOwnTextInputElement(Keyboard.FocusedElement as DependencyObject);

    private bool IsOwnTextInputElement(DependencyObject? node)
    {
        bool seenTextBox = false;
        for (var cur = node; cur != null;)
        {
            if (cur is TextBoxBase)
                seenTextBox = true;
            if (ReferenceEquals(cur, this))
                return seenTextBox;

            var parent = cur is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : null;
            cur = parent ?? LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        for (var cur = node; cur != null;)
        {
            if (cur is T match)
                return match;

            var parent = cur is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : null;
            cur = parent ?? LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }

    private void ReturnFocusToViewport()
    {
        if (Keyboard.FocusedElement is TextBoxBase)
            Keyboard.ClearFocus();
        _host?.FocusHost();
    }

    private void OnThreadFilterMessage(ref MSG msg, ref bool handled)
    {
        if (handled || !IsVisible || _vm == null || !(_vm.IsModelEditMode || _vm.IsShadingWorkspace) || !_vm.EditSession.IsActive)
            return;

        if (IsOwnTextInputFocused(Keyboard.FocusedElement as DependencyObject))
            return;

        bool inModal = _vm.IsModelEditMode && _vm.EditSession.IsModalOrLoopCut;
        bool hostFocused = _host != null && _host.Handle != IntPtr.Zero && msg.hwnd == _host.Handle;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        if (msg.message is 0x0100 or 0x0104) // WM_KEYDOWN / WM_SYSKEYDOWN
        {
            Key wpfKey = ViewportD3D11Host.ResolveKeyFromWin32Message(msg.wParam, msg.lParam);
            if (wpfKey == Key.None) return;

            if (ctrl && !alt && wpfKey is Key.Z or Key.Y &&
                Keyboard.FocusedElement is not TextBoxBase &&
                (hostFocused || IsKeyboardFocusWithin || IsMouseOver))
            {
                if (wpfKey == Key.Y || (wpfKey == Key.Z && shift)) _vm.TryRedo();
                else _vm.TryUndo();
                handled = true;
                return;
            }

            if (_vm.IsShadingWorkspace && !inModal &&
                (NodeEditorControl.IsKeyboardFocusWithin || NodeEditorControl.IsMouseOver) &&
                Keyboard.FocusedElement is not TextBoxBase)
            {
                if (ctrl && !alt && !shift && wpfKey == Key.C)
                {
                    NodeEditorControl.CopySelectedNodes();
                    handled = true;
                    return;
                }
                if (ctrl && !alt && !shift && wpfKey == Key.V)
                {
                    NodeEditorControl.PasteNodesAtCursorOrOffset();
                    handled = true;
                    return;
                }
                if (shift && !ctrl && !alt && wpfKey == Key.D)
                {
                    NodeEditorControl.DuplicateSelectedNodes();
                    handled = true;
                    return;
                }
                if (ctrl && !alt && !shift && wpfKey == Key.A)
                {
                    NodeEditorControl.SelectAllNodes();
                    handled = true;
                    return;
                }
                if (shift && !ctrl && !alt && wpfKey == Key.A)
                {
                    NodeEditorControl.OpenAddNodeMenuAtCursor();
                    handled = true;
                    return;
                }
                if (!ctrl && !alt && wpfKey is Key.Delete or Key.X)
                {
                    NodeEditorControl.DeleteSelectedNodes();
                    handled = true;
                    return;
                }
                if (!ctrl && !alt && wpfKey == Key.Escape)
                {
                    NodeEditorControl.CancelTransientInteractions();
                    handled = true;
                    return;
                }
            }

            if ((inModal || (_vm.IsModelEditMode && hostFocused)) && HandleShortcut(wpfKey, mods))
            {
                handled = true;
                return;
            }
        }
        else if (inModal && msg.message is 0x0101 or 0x0102 or 0x0103 or 0x0105 or 0x0106 or 0x010D or 0x010F or 0x0286)
        {
            handled = true;
        }
    }

    private void OnMaterialTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return or Key.Escape)
        {
            ReturnFocusToViewport();
            e.Handled = true;
        }
    }

    private void OnMaterialPanelPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsOwnTextInputFocused(e.OriginalSource as DependencyObject))
        {
            ReturnFocusToViewport();
        }
    }

    private void OnToolbarActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is TextBoxBase)
            return;
        if (e.OriginalSource is DependencyObject dep && IsOwnTextInputFocused(dep))
            return;
        ReturnFocusToViewport();
    }

    private void TeardownHost()
    {
        CloseCompassTooltip();
        if (_host != null)
        {
            _host.Render -= OnHostRender;
            _host.MouseAction -= OnHostMouseAction;
            _host.KeyAction -= OnHostKeyAction;
            _host.KeyDownInterceptor = null;
            _host.DeviceLostOrRecreated -= OnHostDeviceReset;
            ViewportContainer.Child = null;
            _host.Dispose();
            _host = null;
        }
    }

    private void CloseCompassTooltip()
    {
        _compassToolTip.IsOpen = false;
        if (_vm != null && _vm.RenderOptions.HoveredCompassTarget != AxisCompassTarget.None)
        {
            _vm.RenderOptions.HoveredCompassTarget = AxisCompassTarget.None;
            _vm.MarkDirty();
        }
    }

    private void OnHostDeviceReset()
    {
        _vm?.MarkDirty();
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (_host == null || _vm == null) return;
        _vm.RefreshSnapshotFromBridge();
        if (!_vm.IsDirty) return;

        _vm.ClearDirty();
        _host.RenderFrame();
    }

    private void OnHostRender(
        ID3D11Device device,
        ID3D11DeviceContext ctx,
        ID3D11RenderTargetView rtv,
        ID3D11DepthStencilView dsv,
        int width,
        int height)
    {
        if (_vm == null) return;

        if (_host != null)
            _vm.RenderOptions.DpiScale = (float)_host.DpiScaleX;

        var selected = _vm.GetSelectedVideoItems();
        bool useEditMesh = (_vm.IsModelEditMode || _vm.IsShadingWorkspace || _vm.IsMaterialPanelOpen) && _vm.EditSession.IsActive;
        _vm.RenderOptions.ShowEditOverlays = _vm.IsModelEditMode;
        _vm.RenderOptions.ShowObjectGizmoAndBoundsWhenEditActive = _vm.IsLayoutWorkspace && !_vm.IsModelEditMode;
        var activeEdit = useEditMesh ? _vm.EditSession : null;
        var activeCamPath = !_vm.IsModelEditMode ? _vm.FindActiveCameraPathEffect() : null;
        var activeBezier = !_vm.IsModelEditMode ? _vm.FindActiveBezierCurveParameter() : null;
        _vm.Renderer.RenderWithSceneTools(
            device, ctx, rtv, dsv, width, height,
            _vm.Camera, _vm.CurrentSnapshot, _vm.Manipulator, selected, _vm.RenderOptions, activeEdit,
            _vm.CameraPathController, activeCamPath,
            _vm.BezierCurveController, activeBezier);
    }

    private void OnHostMouseAction(Point pos, ViewportD3D11Host.MouseEventKind kind, int delta)
    {
        if (_vm == null || _host == null) return;

        float vw = Math.Max(1f, _host.PixelWidth);
        float vh = Math.Max(1f, _host.PixelHeight);
        float dpi = MathF.Max(0.5f, (float)_host.DpiScaleX);
        _vm.RenderOptions.DpiScale = dpi;
        var mousePx = new Vector2((float)pos.X, (float)pos.Y);
        var mods = Keyboard.Modifiers;

        bool inEditMode = _vm.IsModelEditMode && _vm.EditSession.IsActive;

        switch (kind)
        {
            case ViewportD3D11Host.MouseEventKind.LeftDown:
            {
                ReturnFocusToViewport();

                _lastMousePos = pos;
                _leftDownPos = pos;
                _leftDownActive = true;
                _leftWasEmptySpace = false;

                if (_vm.RenderOptions.ShowAxisCompass)
                {
                    var hitCompass = AxisCompassGizmo.HitTest(mousePx, _vm.Camera.GetViewMatrix(), vw, vh, dpi, _vm.UseBlenderAxis);
                    if (hitCompass != AxisCompassTarget.None)
                    {
                        _compassToolTip.IsOpen = false;
                        _compassDownActive = true;
                        _compassDownTarget = hitCompass;
                        _vm.RenderOptions.HoveredCompassTarget = hitCompass;
                        _vm.MarkDirty();
                        break;
                    }
                }

                if ((mods & ModifierKeys.Alt) != 0)
                {
                    _compassToolTip.IsOpen = false;
                    _isOrbitDragging = true;
                    break;
                }

                if (inEditMode)
                {
                    var session = _vm.EditSession;
                    if (session.ActiveModalOp is ModalOpKind.LoopCutPickRing or ModalOpKind.LoopCutSlide)
                    {
                        bool wasSlide = session.ActiveModalOp == ModalOpKind.LoopCutSlide;
                        session.HandleLoopCutLeftClick(mousePx, _vm.Camera, vw, vh);
                        if (wasSlide && !session.IsModalOrLoopCut)
                            _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                        _vm.MarkDirty();
                        break;
                    }
                    if (session.IsModalOrLoopCut)
                    {
                        session.ConfirmModalOrLoopCut();
                        _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                        _vm.MarkDirty();
                        break;
                    }

                    if (session.TryComputeEditGizmo(_vm.Camera, _vm.Manipulator.Space, out var editGizmo))
                    {
                        var hit = editGizmo.HitTest(mousePx, session.EditGizmoMode, _vm.Camera, vw, vh);
                        if (hit != GizmoHandle.None &&
                            session.TryBeginGizmoDrag(hit, mousePx, _vm.Camera, vw, vh, editGizmo))
                        {
                            _compassToolTip.IsOpen = false;
                            _vm.MarkDirty();
                            break;
                        }
                    }

                    if (session.PickAndSelect(mousePx, _vm.Camera, vw, vh, (mods & ModifierKeys.Shift) != 0))
                    {
                        _vm.MarkDirty();
                        break;
                    }

                    _leftWasEmptySpace = true;
                    break;
                }

                var selected = _vm.GetSelectedVideoItems();
                var activeCamPath = _vm.FindActiveCameraPathEffect();
                if (activeCamPath != null &&
                    _vm.CameraPathController.TryBeginDrag(activeCamPath, mousePx, _vm.Camera, vw, vh, mods))
                {
                    _compassToolTip.IsOpen = false;
                    _vm.MarkDirty();
                    break;
                }

                var activeBezier = _vm.FindActiveBezierCurveParameter();
                if (activeBezier != null &&
                    _vm.BezierCurveController.TryBeginDrag(activeBezier, _vm.CurrentSnapshot, selected, _vm.Manipulator, mousePx, _vm.Camera, vw, vh))
                {
                    _compassToolTip.IsOpen = false;
                    _vm.MarkDirty();
                    break;
                }

                if (_vm.Manipulator.TryComputeGizmo(selected, _vm.CurrentSnapshot, _vm.Camera, out var gizmo))
                {
                    var hit = gizmo.HitTest(mousePx, _vm.Manipulator.Mode, _vm.Camera, vw, vh);
                    if (hit != GizmoHandle.None &&
                        _vm.CurrentSnapshot != null &&
                        _vm.Manipulator.TryBeginDrag(hit, mousePx, selected, _vm.CurrentSnapshot, _vm.Camera, vw, vh, gizmo))
                    {
                        _compassToolTip.IsOpen = false;
                        _vm.MarkDirty();
                        break;
                    }
                }

                if (_vm.CurrentSnapshot != null)
                {
                    var picked = _vm.Manipulator.PickItem(mousePx, _vm.CurrentSnapshot, _vm.Camera, vw, vh, _vm.RenderOptions.ShowLights);
                    if (picked != null)
                    {
                        _vm.SelectPickedItem(picked.Item, (mods & ModifierKeys.Control) != 0);
                        break;
                    }
                }

                _leftWasEmptySpace = true;
                break;
            }

            case ViewportD3D11Host.MouseEventKind.LeftUp:
            {
                if (_compassDownActive)
                {
                    var releasedTarget = _compassDownTarget;
                    bool wasOrbit = _isOrbitDragging;
                    _compassDownActive = false;
                    _compassDownTarget = AxisCompassTarget.None;
                    _isOrbitDragging = false;
                    _leftDownActive = false;
                    if (!wasOrbit && releasedTarget != AxisCompassTarget.None && releasedTarget != AxisCompassTarget.Background)
                    {
                        _vm.ApplyAxisCompassTarget(releasedTarget);
                    }
                    break;
                }

                if (inEditMode)
                {
                    var session = _vm.EditSession;
                    if (session.IsGizmoDragging)
                    {
                        session.EndGizmoDrag();
                        _vm.MarkDirty();
                    }
                    else if (session.IsBoxSelecting)
                    {
                        session.EndBoxSelect(_vm.Camera, vw, vh, (mods & ModifierKeys.Shift) != 0);
                        _vm.MarkDirty();
                    }
                    else if (_leftDownActive && _leftWasEmptySpace && !_isOrbitDragging)
                    {
                        double moved = Math.Abs(pos.X - _leftDownPos.X) + Math.Abs(pos.Y - _leftDownPos.Y);
                        if (moved < 4.0 && (mods & ModifierKeys.Shift) == 0 && session.Selection.Vertices.Count > 0)
                        {
                            session.Selection.Clear();
                            _vm.MarkDirty();
                        }
                    }
                    _leftDownActive = false;
                    _isOrbitDragging = false;
                    break;
                }

                if (_vm.CameraPathController.IsDragging)
                {
                    _vm.CameraPathController.EndDrag(_vm.UndoRedoManager);
                    _vm.MarkDirty();
                }
                else if (_vm.BezierCurveController.IsDragging)
                {
                    _vm.BezierCurveController.EndDrag(_vm.UndoRedoManager);
                    _vm.MarkDirty();
                }
                else if (_vm.Manipulator.IsDragging)
                {
                    _vm.Manipulator.EndDrag(_vm.UndoRedoManager);
                    _vm.MarkDirty();
                }
                else if (_leftDownActive && _leftWasEmptySpace && !_isOrbitDragging)
                {
                    double moved = Math.Abs(pos.X - _leftDownPos.X) + Math.Abs(pos.Y - _leftDownPos.Y);
                    if (moved < 4.0 && (mods & ModifierKeys.Control) == 0)
                    {
                        _vm.SelectPickedItem(null, false);
                        _vm.MarkDirty();
                    }
                }
                _leftDownActive = false;
                _isOrbitDragging = false;
                break;
            }

            case ViewportD3D11Host.MouseEventKind.RightDown:
            {
                ReturnFocusToViewport();
                _compassToolTip.IsOpen = false;
                if (inEditMode)
                {
                    var session = _vm.EditSession;
                    if (session.ActiveModalOp == ModalOpKind.LoopCutSlide)
                    {
                        session.ConfirmModalOrLoopCut(zeroSlideIfLoopCut: true);
                        _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                        _vm.MarkDirty();
                        break;
                    }
                    if (session.IsModalOrLoopCut)
                    {
                        session.CancelModalOrLoopCut();
                        _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                        _vm.MarkDirty();
                        break;
                    }
                    if (session.IsGizmoDragging)
                    {
                        session.CancelGizmoDrag();
                        _vm.MarkDirty();
                        break;
                    }
                }
                if (_vm.CameraPathController.IsDragging)
                {
                    _vm.CameraPathController.CancelDrag();
                    _vm.MarkDirty();
                    break;
                }
                if (_vm.BezierCurveController.IsDragging)
                {
                    _vm.BezierCurveController.CancelDrag();
                    _vm.MarkDirty();
                    break;
                }
                if (_vm.Manipulator.IsDragging)
                {
                    _vm.Manipulator.CancelDrag();
                    _vm.MarkDirty();
                    break;
                }
                _lastMousePos = pos;
                if ((mods & ModifierKeys.Alt) != 0) _isRollDragging = true;
                else if ((mods & ModifierKeys.Shift) != 0) _isPanDragging = true;
                else _isOrbitDragging = true;
                break;
            }

            case ViewportD3D11Host.MouseEventKind.RightUp:
                _isOrbitDragging = false;
                _isPanDragging = false;
                _isRollDragging = false;
                break;

            case ViewportD3D11Host.MouseEventKind.MiddleDown:
                ReturnFocusToViewport();
                _compassToolTip.IsOpen = false;
                _lastMousePos = pos;
                if ((mods & ModifierKeys.Shift) != 0) _isOrbitDragging = true;
                else _isPanDragging = true;
                break;

            case ViewportD3D11Host.MouseEventKind.MiddleUp:
                _isPanDragging = false;
                _isOrbitDragging = false;
                break;

            case ViewportD3D11Host.MouseEventKind.Move:
            {
                float dx = (float)(pos.X - _lastMousePos.X);
                float dy = (float)(pos.Y - _lastMousePos.Y);
                _lastMousePos = pos;

                if (_compassDownActive)
                {
                    double moved = Math.Abs(pos.X - _leftDownPos.X) + Math.Abs(pos.Y - _leftDownPos.Y);
                    if (moved >= 3.0)
                    {
                        _isOrbitDragging = true;
                        _compassDownTarget = AxisCompassTarget.None;
                        _vm.Camera.Orbit(dx, dy);
                        _vm.MarkDirty();
                    }
                    break;
                }

                if (_isRollDragging)
                {
                    _vm.Camera.AddRoll(dx);
                    _vm.MarkDirty();
                    break;
                }
                if (_isOrbitDragging)
                {
                    _vm.Camera.Orbit(dx, dy);
                    _vm.MarkDirty();
                    break;
                }
                if (_isPanDragging)
                {
                    _vm.Camera.Pan(dx, dy, vw, vh);
                    _vm.MarkDirty();
                    break;
                }

                if (inEditMode)
                {
                    var session = _vm.EditSession;
                    if (session.ActiveModalOp is ModalOpKind.LoopCutPickRing or ModalOpKind.LoopCutSlide)
                    {
                        session.UpdateLoopCutHover(mousePx, _vm.Camera, vw, vh);
                        _vm.MarkDirty();
                        break;
                    }
                    if (session.IsModalOrLoopCut)
                    {
                        session.UpdateModalTransform(mousePx, _vm.Camera, vw, vh, mods);
                        _vm.MarkDirty();
                        break;
                    }
                    if (session.IsGizmoDragging)
                    {
                        session.UpdateGizmoDrag(mousePx, _vm.Camera, vw, vh, mods);
                        _vm.MarkDirty();
                        break;
                    }
                    if (_leftDownActive && _leftWasEmptySpace)
                    {
                        double moved = Math.Abs(pos.X - _leftDownPos.X) + Math.Abs(pos.Y - _leftDownPos.Y);
                        if (!session.IsBoxSelecting && moved >= 4.0)
                        {
                            session.BeginBoxSelect(new Vector2((float)_leftDownPos.X, (float)_leftDownPos.Y));
                        }
                        if (session.IsBoxSelecting)
                        {
                            session.UpdateBoxSelect(mousePx);
                            _vm.MarkDirty();
                        }
                        break;
                    }
                }
                else
                {
                    if (_vm.CameraPathController.IsDragging)
                    {
                        _vm.CameraPathController.UpdateDrag(mousePx, _vm.Camera, vw, vh, mods);
                        _vm.MarkDirty();
                        break;
                    }
                    if (_vm.BezierCurveController.IsDragging)
                    {
                        _vm.BezierCurveController.UpdateDrag(mousePx, _vm.Camera, vw, vh, mods);
                        _vm.MarkDirty();
                        break;
                    }
                    if (_vm.Manipulator.IsDragging)
                    {
                        _vm.Manipulator.UpdateDrag(mousePx, _vm.Camera, vw, vh, mods);
                        _vm.MarkDirty();
                        break;
                    }

                    // 空白ドラッグで自動的に回転（誤選択・誤移動なし）
                    if (_leftDownActive && _leftWasEmptySpace)
                    {
                        double moved = Math.Abs(pos.X - _leftDownPos.X) + Math.Abs(pos.Y - _leftDownPos.Y);
                        if (moved >= 4.0)
                        {
                            _isOrbitDragging = true;
                            _vm.Camera.Orbit(dx, dy);
                            _vm.MarkDirty();
                        }
                        break;
                    }
                }

                // 右上 3D コンパス ホバー判定
                AxisCompassTarget compassHover = _vm.RenderOptions.ShowAxisCompass
                    ? AxisCompassGizmo.HitTest(mousePx, _vm.Camera.GetViewMatrix(), vw, vh, dpi, _vm.UseBlenderAxis)
                    : AxisCompassTarget.None;

                if (compassHover != AxisCompassTarget.None)
                {
                    bool changed = false;
                    if (_vm.RenderOptions.HoveredCompassTarget != compassHover)
                    {
                        _vm.RenderOptions.HoveredCompassTarget = compassHover;
                        changed = true;
                    }
                    if (_vm.Manipulator.HoveredHandle != GizmoHandle.None)
                    {
                        _vm.Manipulator.HoveredHandle = GizmoHandle.None;
                        changed = true;
                    }
                    if (inEditMode && _vm.EditSession.HoveredGizmoHandle != GizmoHandle.None)
                    {
                        _vm.EditSession.HoveredGizmoHandle = GizmoHandle.None;
                        changed = true;
                    }

                    string tip = AxisCompassGizmo.GetTooltipText(compassHover, _vm.UseBlenderAxis);
                    if (!string.IsNullOrEmpty(tip))
                    {
                        double dipX = pos.X / Math.Max(0.5, _host.DpiScaleX);
                        double dipY = pos.Y / Math.Max(0.5, _host.DpiScaleY);
                        double maxDipX = Math.Max(4.0, ViewportContainer.ActualWidth - 160.0);
                        _compassToolTip.Content = tip;
                        _compassToolTip.HorizontalOffset = Math.Clamp(dipX - 85.0, 4.0, maxDipX);
                        _compassToolTip.VerticalOffset = Math.Max(4.0, dipY + 18.0);
                        if (!_compassToolTip.IsOpen) _compassToolTip.IsOpen = true;
                    }

                    if (changed) _vm.MarkDirty();
                    break;
                }

                if (_vm.RenderOptions.HoveredCompassTarget != AxisCompassTarget.None || _compassToolTip.IsOpen)
                {
                    _vm.RenderOptions.HoveredCompassTarget = AxisCompassTarget.None;
                    _compassToolTip.IsOpen = false;
                    _vm.MarkDirty();
                }

                // ホバー強調判定
                if (inEditMode)
                {
                    GizmoHandle newHover = GizmoHandle.None;
                    if (_vm.EditSession.TryComputeEditGizmo(_vm.Camera, _vm.Manipulator.Space, out var eg))
                    {
                        newHover = eg.HitTest(mousePx, _vm.EditSession.EditGizmoMode, _vm.Camera, vw, vh);
                    }
                    if (newHover != _vm.EditSession.HoveredGizmoHandle)
                    {
                        _vm.EditSession.HoveredGizmoHandle = newHover;
                        _vm.MarkDirty();
                    }
                }
                else
                {
                    var selected = _vm.GetSelectedVideoItems();
                    bool cpHoverChanged = _vm.CameraPathController.UpdateHover(_vm.FindActiveCameraPathEffect(), mousePx, _vm.Camera, vw, vh);
                    bool bzHoverChanged = _vm.BezierCurveController.UpdateHover(_vm.FindActiveBezierCurveParameter(), _vm.CurrentSnapshot, selected, _vm.Manipulator, mousePx, _vm.Camera, vw, vh);

                    GizmoHandle newHover = GizmoHandle.None;
                    if (_vm.CameraPathController.HoveredHandle.Kind == CameraPathHandleKind.None &&
                        _vm.BezierCurveController.HoveredHandle.Kind == BezierHandleKind.None &&
                        _vm.Manipulator.TryComputeGizmo(selected, _vm.CurrentSnapshot, _vm.Camera, out var gizmo))
                    {
                        newHover = gizmo.HitTest(mousePx, _vm.Manipulator.Mode, _vm.Camera, vw, vh);
                    }
                    if (newHover != _vm.Manipulator.HoveredHandle || cpHoverChanged || bzHoverChanged)
                    {
                        _vm.Manipulator.HoveredHandle = newHover;
                        _vm.MarkDirty();
                    }
                }
                break;
            }

            case ViewportD3D11Host.MouseEventKind.Wheel:
                _compassToolTip.IsOpen = false;
                if (inEditMode && _vm.EditSession.ActiveModalOp == ModalOpKind.LoopCutPickRing)
                {
                    _vm.EditSession.AdjustLoopCutCount(delta > 0 ? 1 : -1);
                    _vm.MarkDirty();
                    break;
                }
                if ((mods & ModifierKeys.Shift) != 0)
                    _vm.Camera.AddRoll(delta * 0.1f);
                else
                    _vm.Camera.Zoom(delta);
                _vm.MarkDirty();
                break;

            case ViewportD3D11Host.MouseEventKind.Leave:
            {
                bool dirty = false;
                _compassToolTip.IsOpen = false;
                if (_vm.RenderOptions.HoveredCompassTarget != AxisCompassTarget.None)
                {
                    _vm.RenderOptions.HoveredCompassTarget = AxisCompassTarget.None;
                    dirty = true;
                }
                if (_vm.Manipulator.HoveredHandle != GizmoHandle.None && !_vm.Manipulator.IsDragging)
                {
                    _vm.Manipulator.HoveredHandle = GizmoHandle.None;
                    dirty = true;
                }
                if (inEditMode && _vm.EditSession.HoveredGizmoHandle != GizmoHandle.None && !_vm.EditSession.IsGizmoDragging)
                {
                    _vm.EditSession.HoveredGizmoHandle = GizmoHandle.None;
                    dirty = true;
                }
                if (dirty) _vm.MarkDirty();
                break;
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsOwnTextInputFocused(e.OriginalSource as DependencyObject))
        {
            if (e.Key is Key.Enter or Key.Return or Key.Escape)
            {
                var numSlider = FindAncestor<Controls.NumericTextBoxSlider>(
                    (e.OriginalSource as DependencyObject) ?? (Keyboard.FocusedElement as DependencyObject));
                if (numSlider != null)
                {
                    if (e.Key == Key.Escape) numSlider.CancelTextEdit();
                    else numSlider.CommitTextEdit();
                }
                ReturnFocusToViewport();
                e.Handled = true;
            }
            return;
        }

        var key = e.Key == Key.ImeProcessed
            ? e.ImeProcessedKey
            : (e.Key == Key.System ? e.SystemKey : e.Key);
        var mods = Keyboard.Modifiers;

        if (_vm is { IsShadingWorkspace: true } && _vm.EditSession.IsActive &&
            (NodeEditorControl.IsKeyboardFocusWithin || NodeEditorControl.IsMouseOver))
        {
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            if (shift && !ctrl && !alt && key == Key.A)
            {
                NodeEditorControl.OpenAddNodeMenuAtCursor();
                e.Handled = true;
                return;
            }
            if (!ctrl && !alt && key is Key.Delete or Key.X)
            {
                NodeEditorControl.DeleteSelectedNode();
                e.Handled = true;
                return;
            }
        }

        if (HandleShortcut(key, mods))
        {
            e.Handled = true;
            return;
        }

        if (_vm is { IsModelEditMode: true } &&
            (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 &&
            ((key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9) ||
             key is Key.Tab or Key.Enter or Key.Return or Key.Escape or Key.Space))
        {
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (IsOwnTextInputFocused(e.OriginalSource as DependencyObject))
            return;

        if (_vm is { IsModelEditMode: true } &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            var key = e.Key == Key.ImeProcessed
                ? e.ImeProcessedKey
                : (e.Key == Key.System ? e.SystemKey : e.Key);
            if ((key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9) ||
                key is Key.Tab or Key.Enter or Key.Return or Key.Escape or Key.Space or Key.Delete)
            {
                e.Handled = true;
            }
        }
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (IsOwnTextInputFocused(e.OriginalSource as DependencyObject))
            return;

        if (_vm is { IsModelEditMode: true })
            e.Handled = true;
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (IsOwnTextInputFocused(e.OriginalSource as DependencyObject))
            return;

        if (_vm is { IsModelEditMode: true })
            e.Handled = true;
    }

    private void OnHostKeyAction(Key key, ModifierKeys mods)
    {
        HandleShortcut(key, mods);
    }

    private bool HandleShortcut(Key key, ModifierKeys mods)
    {
        if (_vm == null) return false;

        // Alt キー併用時の System キー実キー取得
        if (key == Key.System) return false;

        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;

        float vw = Math.Max(1f, _host?.PixelWidth ?? 1280f);
        float vh = Math.Max(1f, _host?.PixelHeight ?? 720f);
        var mousePx = new Vector2((float)_lastMousePos.X, (float)_lastMousePos.Y);

        // 編集モード / オブジェクトモード共通ショートカット
        if (_vm.IsLayoutWorkspace && key == Key.Tab && !ctrl && !alt && !shift)
        {
            _vm.IsModelEditMode = !_vm.IsModelEditMode;
            if (_vm.IsModelEditMode)
                ReturnFocusToViewport();
            return true;
        }

        if (ctrl && !alt)
        {
            if (key == Key.Z)
            {
                if (shift) _vm.TryRedo();
                else _vm.TryUndo();
                return true;
            }
            if (key == Key.Y) { _vm.TryRedo(); return true; }
            if (key == Key.NumPad1) { _vm.ApplyPreset(ViewportCameraPreset.Back); return true; }
            if (key == Key.NumPad3) { _vm.ApplyPreset(ViewportCameraPreset.Left); return true; }
            if (key == Key.NumPad7) { _vm.ApplyPreset(ViewportCameraPreset.Bottom); return true; }
        }

        if (!ctrl && !alt)
        {
            switch (key)
            {
                case Key.NumPad0: _vm.MatchYmmCamera(); return true;
                case Key.NumPad1: _vm.ApplyPreset(ViewportCameraPreset.Front); return true;
                case Key.NumPad3: _vm.ApplyPreset(ViewportCameraPreset.Right); return true;
                case Key.NumPad7: _vm.ApplyPreset(ViewportCameraPreset.Top); return true;
                case Key.NumPad5: _vm.ToggleProjection(); return true;
            }
        }

        // ── モデル編集モード中の Blender 風ショートカット ──
        if (_vm.IsModelEditMode && _vm.EditSession.IsActive)
        {
            var session = _vm.EditSession;

            // モーダル移動・回転・拡大縮小中
            if (session.ActiveModalOp is ModalOpKind.Grab or ModalOpKind.Rotate or ModalOpKind.Scale)
            {
                if (!ctrl && !alt)
                {
                    if (key == Key.X)
                    {
                        session.ToggleAxisConstraint(AxisConstraintMode.X, shift);
                        session.ReapplyModalAfterConstraintOrNumeric(_vm.Camera, vw, vh, mods);
                        _vm.MarkDirty();
                        return true;
                    }
                    if (key == Key.Y)
                    {
                        session.ToggleAxisConstraint(AxisConstraintMode.Y, shift);
                        session.ReapplyModalAfterConstraintOrNumeric(_vm.Camera, vw, vh, mods);
                        _vm.MarkDirty();
                        return true;
                    }
                    if (key == Key.Z)
                    {
                        session.ToggleAxisConstraint(AxisConstraintMode.Z, shift);
                        session.ReapplyModalAfterConstraintOrNumeric(_vm.Camera, vw, vh, mods);
                        _vm.MarkDirty();
                        return true;
                    }
                    if (key == Key.Enter || key == Key.Return)
                    {
                        session.ConfirmModalOrLoopCut();
                        _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                        _vm.MarkDirty();
                        return true;
                    }
                    if (key == Key.Escape)
                    {
                        session.CancelModalOrLoopCut();
                        _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                        _vm.MarkDirty();
                        return true;
                    }
                    if (session.TryHandleModalNumericKey(key))
                    {
                        session.ReapplyModalAfterConstraintOrNumeric(_vm.Camera, vw, vh, mods);
                        _vm.MarkDirty();
                        return true;
                    }
                }
                return true;
            }

            // ループカットモード中
            if (session.ActiveModalOp is ModalOpKind.LoopCutPickRing or ModalOpKind.LoopCutSlide)
            {
                if (key == Key.Escape)
                {
                    session.CancelModalOrLoopCut();
                    _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                    _vm.MarkDirty();
                    return true;
                }
                if (key == Key.Enter || key == Key.Return)
                {
                    session.ConfirmModalOrLoopCut();
                    _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                    _vm.MarkDirty();
                    return true;
                }
                if (key == Key.OemPlus || key == Key.Add)
                {
                    session.AdjustLoopCutCount(+1);
                    _vm.MarkDirty();
                    return true;
                }
                if (key == Key.OemMinus || key == Key.Subtract)
                {
                    session.AdjustLoopCutCount(-1);
                    _vm.MarkDirty();
                    return true;
                }
                return true;
            }

            // Ctrl 併用編集操作
            if (ctrl && !alt)
            {
                if (key == Key.R)
                {
                    session.BeginLoopCutMode(mousePx, _vm.Camera, vw, vh);
                    ReturnFocusToViewport();
                    _vm.MarkDirty();
                    return true;
                }
                if (key == Key.I)
                {
                    session.InvertSelection();
                    _vm.MarkDirty();
                    return true;
                }
                return false;
            }

            // Alt 併用編集操作
            if (alt && !ctrl)
            {
                if (key == Key.A)
                {
                    session.Selection.Clear();
                    _vm.MarkDirty();
                    return true;
                }
                if (key == Key.Z)
                {
                    _vm.IsXRayEnabled = !_vm.IsXRayEnabled;
                    return true;
                }
                return false;
            }

            // Shift+A: プリミティブ/頂点追加メニュー
            if (shift && !ctrl && !alt && key == Key.A)
            {
                _addMeshMenu.PlacementTarget = ViewportContainer;
                _addMeshMenu.Placement = PlacementMode.MousePoint;
                _addMeshMenu.IsOpen = true;
                return true;
            }

            if (!ctrl && !alt)
            {
                switch (key)
                {
                    case Key.D1: _vm.CurrentMeshSelectMode = MeshSelectMode.Vertex; return true;
                    case Key.D2: _vm.CurrentMeshSelectMode = MeshSelectMode.Edge; return true;
                    case Key.D3: _vm.CurrentMeshSelectMode = MeshSelectMode.Face; return true;
                    case Key.A:
                        session.ToggleSelectAll();
                        _vm.MarkDirty();
                        return true;
                    case Key.L:
                        session.SelectLinked();
                        _vm.MarkDirty();
                        return true;
                    case Key.G:
                        session.TryBeginModalTransform(ModalOpKind.Grab, mousePx);
                        ReturnFocusToViewport();
                        _vm.MarkDirty();
                        return true;
                    case Key.R:
                        session.TryBeginModalTransform(ModalOpKind.Rotate, mousePx);
                        ReturnFocusToViewport();
                        _vm.MarkDirty();
                        return true;
                    case Key.S:
                        session.TryBeginModalTransform(ModalOpKind.Scale, mousePx);
                        ReturnFocusToViewport();
                        _vm.MarkDirty();
                        return true;
                    case Key.E:
                        if (session.ExtrudeAndBeginMove(mousePx))
                        {
                            _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                        }
                        ReturnFocusToViewport();
                        _vm.MarkDirty();
                        return true;
                    case Key.F:
                        if (session.MakeEdgeOrFace(_vm.Camera))
                        {
                            _vm.SyncMaterialPropertiesFromSession(rebuildSlots: true);
                        }
                        _vm.MarkDirty();
                        return true;
                    case Key.X:
                    case Key.Delete:
                        _deleteMeshMenu.PlacementTarget = ViewportContainer;
                        _deleteMeshMenu.Placement = PlacementMode.MousePoint;
                        _deleteMeshMenu.IsOpen = true;
                        return true;
                    case Key.Q:
                        _vm.ToggleSpace();
                        return true;
                    case Key.OemPeriod:
                    case Key.Decimal:
                        _vm.FocusSelected();
                        return true;
                    case Key.Escape:
                        if (session.IsGizmoDragging)
                        {
                            session.CancelGizmoDrag();
                            _vm.MarkDirty();
                            return true;
                        }
                        return false;
                }
            }

            return false;
        }

        // ── 通常オブジェクトモードショートカット ──
        switch (key)
        {
            case Key.D1: if (_vm.IsModelEditMode) { _vm.CurrentMeshSelectMode = MeshSelectMode.Vertex; return true; } return false;
            case Key.D2: if (_vm.IsModelEditMode) { _vm.CurrentMeshSelectMode = MeshSelectMode.Edge; return true; } return false;
            case Key.D3: if (_vm.IsModelEditMode) { _vm.CurrentMeshSelectMode = MeshSelectMode.Face; return true; } return false;
            case Key.W: _vm.SetMode(GizmoMode.Translate); return true;
            case Key.E: _vm.SetMode(GizmoMode.Rotate); return true;
            case Key.R: _vm.SetMode(GizmoMode.Scale); return true;
            case Key.Q: _vm.ToggleSpace(); return true;
            case Key.F: _vm.FocusSelected(); return true;
            case Key.Escape:
                if (_vm.CameraPathController.IsDragging)
                {
                    _vm.CameraPathController.CancelDrag();
                    _vm.MarkDirty();
                    return true;
                }
                if (_vm.BezierCurveController.IsDragging)
                {
                    _vm.BezierCurveController.CancelDrag();
                    _vm.MarkDirty();
                    return true;
                }
                if (_vm.Manipulator.IsDragging)
                {
                    _vm.Manipulator.CancelDrag();
                    _vm.MarkDirty();
                    return true;
                }
                return false;
            default:
                return false;
        }
    }
}
