using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Shading;

public partial class ShaderNodeEditorControl : UserControl
{
    private sealed record PinTag(int NodeId, string SocketName, bool IsOutput, ShaderSocketKind Kind);

    private Viewport3DViewModel? _vm;
    private readonly HashSet<int> _selectedNodeIds = new();
    private int _activeNodeId = -1;
    private bool _suppressUiEvents;
    private int _rebuildGeneration;
    private bool _pinOffsetsValid;

    private double _zoom = 1.0;
    private double _panX = 28.0;
    private double _panY = 24.0;
    private bool _hasInitialFramed;

    // パン操作 / ボックス範囲選択
    private bool _isPanning;
    private bool _rightClickPossibleMenu;
    private Point _panStartScreen;
    private double _panStartX;
    private double _panStartY;

    private bool _isBoxSelecting;
    private bool _boxSelectAdditive;
    private Point _boxStartScreen;

    // マルチノードドラッグ移動
    private bool _isDraggingNodes;
    private Border? _draggingHeaderElement;
    private int _dragLeadNodeId = -1;
    private bool _dragCollapseToLeadOnClick;
    private MaterialNodeGraph? _dragSnapshotBeforeMove;
    private Point _dragStartWorld;
    private bool _dragMovedEnough;
    private readonly List<(int NodeId, Border Card, float OrigX, float OrigY)> _dragTargets = new();

    // ソケット接続ドラッグ
    private PinTag? _wiringStart;
    private Point _wiringStartWorld;
    private Path? _previewWire;

    // カラーピッカー編集対象
    private int _pickerNodeId = -1;
    private string _pickerKey = string.Empty;
    private int _pickerRampStopIndex = -1;
    private MaterialNodeGraph? _pickerSnapshot;
    private Button? _pickerTargetButton;
    private bool _pickerEdited;

    // ドラフト編集完了後のフル解像度確定タイマー
    private readonly DispatcherTimer _refineTimer;

    // UI マッピング
    private readonly Dictionary<int, Border> _nodeCards = new();
    private readonly Dictionary<(int NodeId, bool IsOutput, string Socket), Ellipse> _pinEllipses = new();
    private readonly Dictionary<(int NodeId, bool IsOutput, string Socket), Point> _pinRelOffsets = new();
    private readonly Dictionary<(int NodeId, string Key), Action<float>> _sliderUpdaters = new();
    private readonly Dictionary<int, Action> _rampRefreshers = new();

    private static readonly object s_brushLock = new();
    private static readonly Dictionary<string, SolidColorBrush> s_hexBrushCache = new(StringComparer.OrdinalIgnoreCase);

    private static SolidColorBrush GetFrozenBrush(string hex)
    {
        lock (s_brushLock)
        {
            if (s_hexBrushCache.TryGetValue(hex, out var b))
                return b;
            var col = (Color)ColorConverter.ConvertFromString(hex);
            b = new SolidColorBrush(col);
            b.Freeze();
            s_hexBrushCache[hex] = b;
            return b;
        }
    }

    private static SolidColorBrush MakeFrozenRgb(byte r, byte g, byte b)
    {
        string key = $"#{r:X2}{g:X2}{b:X2}";
        return GetFrozenBrush(key);
    }

    private static readonly string[] SwatchHexes =
    {
        "#FFFFFF", "#B0B0B0", "#303030", "#E05252",
        "#E59838", "#E6C845", "#4CAF50", "#3B82F6", "#A855F7"
    };

    public ShaderNodeEditorControl()
    {
        InitializeComponent();
        _refineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _refineTimer.Tick += OnRefineTimerTick;
        Unloaded += (_, _) => CancelTransientInteractions();
        InitColorPickerSwatches();
        ApplyTransform();
    }

    private void OnRefineTimerTick(object? sender, EventArgs e)
    {
        _refineTimer.Stop();
        _vm?.CommitInteractiveNodeGraph();
    }

    private void RestartRefineTimer()
    {
        _refineTimer.Stop();
        _refineTimer.Start();
    }

    internal void BindViewModel(Viewport3DViewModel? vm)
    {
        CancelTransientInteractions();
        _refineTimer.Stop();
        if (_vm != null)
            _vm.ShadingGraphChanged -= OnViewModelShadingGraphChanged;

        _vm = vm;
        if (_vm != null)
            _vm.ShadingGraphChanged += OnViewModelShadingGraphChanged;

        _selectedNodeIds.Clear();
        _activeNodeId = -1;
        _hasInitialFramed = false;
        RefreshFromActiveMaterial();
    }

    private void OnViewModelShadingGraphChanged()
    {
        RefreshFromActiveMaterial();
    }

    internal void RefreshFromActiveMaterial()
    {
        var graph = GetActiveGraph(createIfMissing: false);
        _suppressUiEvents = true;
        try
        {
            if (graph != null)
            {
                SelectResolutionCombo(graph.BakeResolution);
            }
            RebuildVisualGraph(graph);
            if (graph != null && !_hasInitialFramed && GraphSurface.ActualWidth > 40 && GraphSurface.ActualHeight > 40)
            {
                FrameAllNodes(graph);
                _hasInitialFramed = true;
            }
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private MaterialNodeGraph? GetActiveGraph(bool createIfMissing)
    {
        if (_vm == null || !_vm.EditSession.IsActive) return null;
        return createIfMissing
            ? _vm.EditSession.EnsureActiveNodeGraph()
            : _vm.EditSession.Mesh.Materials.ElementAtOrDefault(_vm.EditSession.ActiveMaterialIndex)?.NodeGraph;
    }

    private void SelectResolutionCombo(int res)
    {
        foreach (ComboBoxItem item in CmbBakeResolution.Items)
        {
            if (item.Tag is string s && int.TryParse(s, out int val) && val == res)
            {
                CmbBakeResolution.SelectedItem = item;
                return;
            }
        }
    }

    #region グラフ描画 & ノードカード生成

    internal void CancelTransientInteractions()
    {
        bool needWireRedraw = false;
        if (_wiringStart != null || _previewWire != null)
        {
            _wiringStart = null;
            if (_previewWire != null)
            {
                LinksCanvas.Children.Remove(_previewWire);
                _previewWire = null;
            }
            needWireRedraw = true;
        }

        if (_isDraggingNodes)
        {
            var live = GetActiveGraph(false);
            for (int i = 0; i < _dragTargets.Count; i++)
            {
                var (nid, card, origX, origY) = _dragTargets[i];
                if (live?.FindNode(nid) is { } tn)
                {
                    tn.X = origX;
                    tn.Y = origY;
                }
                Canvas.SetLeft(card, origX);
                Canvas.SetTop(card, origY);
            }
            if (_dragTargets.Count > 0) needWireRedraw = true;
        }

        _isDraggingNodes = false;
        _dragTargets.Clear();
        _draggingHeaderElement = null;
        _dragSnapshotBeforeMove = null;
        _dragLeadNodeId = -1;
        _dragCollapseToLeadOnClick = false;
        _dragMovedEnough = false;

        _isPanning = false;
        _rightClickPossibleMenu = false;
        _isBoxSelecting = false;
        _boxSelectAdditive = false;
        if (SelectionBoxRect != null)
            SelectionBoxRect.Visibility = Visibility.Collapsed;

        if (Mouse.Captured is DependencyObject captured && IsSelfOrVisualDescendant(captured))
        {
            try { Mouse.Capture(null); } catch { }
        }

        if (needWireRedraw && GetActiveGraph(false) is { } graph)
            RedrawAllWires(graph);
    }

    private bool IsSelfOrVisualDescendant(DependencyObject? node)
    {
        for (var cur = node; cur != null;)
        {
            if (ReferenceEquals(cur, this)) return true;
            var parent = cur is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : null;
            cur = parent ?? LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private bool IsDescendantOfNodeCard(DependencyObject? source) => FindNodeCardIdFromElement(source).HasValue;

    private int? FindNodeCardIdFromElement(DependencyObject? source)
    {
        for (var cur = source; cur != null;)
        {
            if (ReferenceEquals(cur, GraphSurface) || ReferenceEquals(cur, NodesCanvas))
                return null;
            if (cur is Border bd && bd.Tag is int id && _nodeCards.TryGetValue(id, out var mapped) && ReferenceEquals(bd, mapped))
                return id;
            var parent = cur is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : null;
            cur = parent ?? LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }

    private void RebuildVisualGraph(MaterialNodeGraph? graph)
    {
        CancelTransientInteractions();
        int gen = ++_rebuildGeneration;
        _pinOffsetsValid = false;

        NodesCanvas.Children.Clear();
        LinksCanvas.Children.Clear();
        _nodeCards.Clear();
        _pinEllipses.Clear();
        _pinRelOffsets.Clear();
        _sliderUpdaters.Clear();
        _rampRefreshers.Clear();

        if (graph == null) return;

        _selectedNodeIds.RemoveWhere(id => graph.FindNode(id) == null);
        if (!_selectedNodeIds.Contains(_activeNodeId))
            _activeNodeId = _selectedNodeIds.Count > 0 ? _selectedNodeIds.First() : -1;

        foreach (var node in graph.Nodes)
        {
            var card = BuildNodeCard(graph, node);
            Canvas.SetLeft(card, node.X);
            Canvas.SetTop(card, node.Y);
            int nodeId = node.Id;
            card.SizeChanged += (_, _) =>
            {
                if (GetActiveGraph(false) is { } live)
                {
                    RefreshCardPinOffsets(nodeId);
                    RedrawAllWires(live);
                }
            };
            NodesCanvas.Children.Add(card);
            _nodeCards[node.Id] = card;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (gen != _rebuildGeneration) return;
            var live = GetActiveGraph(false);
            if (live == null) return;
            EnsurePinOffsetsCached(live);
            RedrawAllWires(live);
        }));
    }

    private void EnsurePinOffsetsCached(MaterialNodeGraph graph)
    {
        bool needLayout = _nodeCards.Count > 0 && _nodeCards.Values.Any(
            c => !c.IsMeasureValid || !c.IsArrangeValid || c.ActualWidth <= 1.0 || c.ActualHeight <= 1.0);
        if (needLayout)
        {
            try { NodesCanvas.UpdateLayout(); } catch { }
        }
        CachePinRelativeOffsets(graph);
    }

    private void RefreshCardPinOffsets(int nodeId)
    {
        if (!_nodeCards.TryGetValue(nodeId, out var card)) return;
        if ((!card.IsLoaded && !card.IsArrangeValid) || card.ActualWidth <= 1.0 || card.ActualHeight <= 1.0) return;

        foreach (var kv in _pinEllipses)
        {
            if (kv.Key.NodeId != nodeId) continue;
            var pin = kv.Value;
            if ((!pin.IsLoaded && !pin.IsArrangeValid) || pin.ActualWidth <= 1.0 || pin.ActualHeight <= 1.0) continue;
            try
            {
                Point p = pin.TranslatePoint(new Point(pin.ActualWidth * 0.5, pin.ActualHeight * 0.5), card);
                if (p.X > 1.0 || p.Y > 1.0)
                    _pinRelOffsets[kv.Key] = p;
            }
            catch { }
        }
        _pinOffsetsValid = _pinEllipses.Count > 0 && _pinRelOffsets.Count >= _pinEllipses.Count;
    }

    private void CachePinRelativeOffsets(MaterialNodeGraph graph)
    {
        _pinRelOffsets.Clear();
        foreach (var kv in _pinEllipses)
        {
            if (!_nodeCards.TryGetValue(kv.Key.NodeId, out var card)) continue;
            var pin = kv.Value;
            if ((!pin.IsLoaded && !pin.IsArrangeValid) || (!card.IsLoaded && !card.IsArrangeValid)) continue;
            if (card.ActualWidth <= 1.0 || card.ActualHeight <= 1.0 || pin.ActualWidth <= 1.0 || pin.ActualHeight <= 1.0) continue;
            try
            {
                Point p = pin.TranslatePoint(new Point(pin.ActualWidth * 0.5, pin.ActualHeight * 0.5), card);
                if (p.X > 1.0 || p.Y > 1.0)
                    _pinRelOffsets[kv.Key] = p;
            }
            catch { }
        }
        _pinOffsetsValid = _pinEllipses.Count > 0 && _pinRelOffsets.Count >= _pinEllipses.Count;
    }

    private Border BuildNodeCard(MaterialNodeGraph graph, ShaderNode node)
    {
        var spec = ShaderNodeRegistry.GetSpec(node.Type);
        bool selected = _selectedNodeIds.Contains(node.Id);
        bool isActive = node.Id == _activeNodeId;

        var outer = new Border
        {
            Width = spec.DefaultWidth,
            Background = MakeFrozenRgb(42, 46, 54),
            BorderBrush = selected ? (isActive ? MakeFrozenRgb(251, 191, 36) : MakeFrozenRgb(245, 158, 11)) : MakeFrozenRgb(18, 20, 24),
            BorderThickness = new Thickness(selected ? 1.8 : 1.2),
            CornerRadius = new CornerRadius(5),
            SnapsToDevicePixels = true,
            Tag = node.Id
        };

        outer.MouseLeftButtonDown += (s, e) =>
        {
            this.Focus();
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                ToggleNodeSelection(node.Id);
            }
            else if (!_selectedNodeIds.Contains(node.Id))
            {
                SelectSingleNode(node.Id);
            }
            else if (_activeNodeId != node.Id)
            {
                _activeNodeId = node.Id;
                UpdateSelectionHighlight();
            }
        };

        var root = new StackPanel();

        // Blender 風カラーヘッダー（複数ノード移動ドラッグ対応ハンドル）
        var hdrBrush = GetFrozenBrush(spec.HeaderColorHex);
        var header = new Border
        {
            Background = hdrBrush,
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Padding = new Thickness(8, 3.5, 8, 3.5),
            Cursor = Cursors.SizeAll
        };
        header.Child = new TextBlock
        {
            Text = spec.Title,
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        header.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount != 1) return;
            this.Focus();
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                ToggleNodeSelection(node.Id);
                e.Handled = true;
                return;
            }

            var live = GetActiveGraph(false);
            if (live == null) return;

            bool alreadySelected = _selectedNodeIds.Contains(node.Id);
            if (!alreadySelected)
            {
                SelectSingleNode(node.Id);
                _dragCollapseToLeadOnClick = false;
            }
            else
            {
                _dragCollapseToLeadOnClick = _selectedNodeIds.Count > 1;
                _activeNodeId = node.Id;
                UpdateSelectionHighlight();
            }

            _dragTargets.Clear();
            foreach (int sid in _selectedNodeIds)
            {
                if (live.FindNode(sid) is { } sn && _nodeCards.TryGetValue(sid, out var sCard))
                    _dragTargets.Add((sid, sCard, sn.X, sn.Y));
            }
            if (_dragTargets.Count == 0) return;

            _isDraggingNodes = true;
            _draggingHeaderElement = header;
            _dragLeadNodeId = node.Id;
            _dragStartWorld = ScreenToWorld(e.GetPosition(GraphSurface));
            _dragMovedEnough = false;
            _dragSnapshotBeforeMove = live.Clone();

            if (!header.CaptureMouse())
            {
                CancelTransientInteractions();
            }
            e.Handled = true;
        };
        header.MouseMove += (s, e) =>
        {
            if (!_isDraggingNodes || !ReferenceEquals(_draggingHeaderElement, header)) return;
            var live = GetActiveGraph(false);
            if (live == null)
            {
                CancelTransientInteractions();
                return;
            }
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CommitOrFinishHeaderDrag(header);
                return;
            }
            Point curW = ScreenToWorld(e.GetPosition(GraphSurface));
            float dx = (float)(curW.X - _dragStartWorld.X);
            float dy = (float)(curW.Y - _dragStartWorld.Y);
            if (MathF.Abs(dx) > 1.5f || MathF.Abs(dy) > 1.5f)
                _dragMovedEnough = true;
            for (int i = 0; i < _dragTargets.Count; i++)
            {
                var (nid, card, origX, origY) = _dragTargets[i];
                float nx = MathF.Round(origX + dx);
                float ny = MathF.Round(origY + dy);
                if (live.FindNode(nid) is { } ln)
                {
                    ln.X = nx;
                    ln.Y = ny;
                }
                Canvas.SetLeft(card, nx);
                Canvas.SetTop(card, ny);
            }
            RedrawAllWires(live);
        };
        header.MouseLeftButtonUp += (s, e) =>
        {
            if (!_isDraggingNodes || !ReferenceEquals(_draggingHeaderElement, header)) return;
            CommitOrFinishHeaderDrag(header);
            e.Handled = true;
        };
        header.LostMouseCapture += (_, _) =>
        {
            if (_isDraggingNodes && ReferenceEquals(_draggingHeaderElement, header))
                CancelTransientInteractions();
        };

        root.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };

        // 1) 出力ソケット列 (右側ピン)
        foreach (var outSock in spec.Outputs)
        {
            body.Children.Add(BuildOutputSocketRow(node, outSock));
        }

        // 2) ノード固有モード切替・特殊UI（画像ファイル、Noiseタイプ、ColorRamp、Mixモード、Invert 等）
        AddNodeSpecialControls(body, graph, node);

        // 3) 入力ソケット列 (左側ピン + 未接続時インラインバー + Principled 折りたたみグループ)
        var ungrouped = spec.Inputs.Where(s => string.IsNullOrEmpty(s.Group));
        foreach (var inSock in ungrouped)
        {
            bool isLinked = graph.FindLinkTo(node.Id, inSock.Name) != null;
            body.Children.Add(BuildInputSocketRow(graph, node, inSock, isLinked));
        }

        var grouped = spec.Inputs.Where(s => !string.IsNullOrEmpty(s.Group)).GroupBy(s => s.Group!);
        foreach (var grp in grouped)
        {
            string gName = grp.Key;
            bool hasExplicit = node.BoolParams.TryGetValue("FoldOpen_" + gName, out bool openFlag);
            bool autoOpen = false;
            if (!hasExplicit)
            {
                foreach (var gs in grp)
                {
                    if (graph.FindLinkTo(node.Id, gs.Name) != null)
                    {
                        autoOpen = true;
                        break;
                    }
                    if (gs.Kind == ShaderSocketKind.Float && gs.ParamKey != null &&
                        MathF.Abs(node.GetFloat(gs.ParamKey, gs.DefaultFloat) - gs.DefaultFloat) > 1e-3f)
                    {
                        autoOpen = true;
                        break;
                    }
                }
            }
            bool isExpanded = hasExplicit ? openFlag : autoOpen;

            var grpHeader = new Border
            {
                Background = MakeFrozenRgb(33, 37, 45),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(6, 3, 6, 1),
                Padding = new Thickness(6, 2, 6, 2),
                Cursor = Cursors.Hand
            };
            grpHeader.Child = new TextBlock
            {
                Text = (isExpanded ? "▾ " : "▸ ") + gName,
                Foreground = MakeFrozenRgb(178, 190, 208),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            };
            grpHeader.MouseLeftButtonDown += (_, e) =>
            {
                var live = GetActiveGraph(false);
                var liveNode = live?.FindNode(node.Id) ?? node;
                liveNode.BoolParams["FoldOpen_" + gName] = !isExpanded;
                if (live != null) RebuildVisualGraph(live);
                e.Handled = true;
            };
            body.Children.Add(grpHeader);

            if (isExpanded)
            {
                foreach (var inSock in grp)
                {
                    bool isLinked = graph.FindLinkTo(node.Id, inSock.Name) != null;
                    body.Children.Add(BuildInputSocketRow(graph, node, inSock, isLinked));
                }
            }
        }

        root.Children.Add(body);
        outer.Child = root;
        return outer;
    }

    private void SelectSingleNode(int nodeId)
    {
        _selectedNodeIds.Clear();
        if (nodeId >= 0) _selectedNodeIds.Add(nodeId);
        _activeNodeId = nodeId;
        UpdateSelectionHighlight();
    }

    private void ToggleNodeSelection(int nodeId)
    {
        if (_selectedNodeIds.Contains(nodeId))
        {
            _selectedNodeIds.Remove(nodeId);
            if (_activeNodeId == nodeId)
                _activeNodeId = _selectedNodeIds.Count > 0 ? _selectedNodeIds.First() : -1;
        }
        else
        {
            _selectedNodeIds.Add(nodeId);
            _activeNodeId = nodeId;
        }
        UpdateSelectionHighlight();
    }

    private void CommitOrFinishHeaderDrag(Border header)
    {
        var live = GetActiveGraph(false);
        var finalCoords = new List<(int NodeId, float X, float Y)>(_dragTargets.Count);
        for (int i = 0; i < _dragTargets.Count; i++)
        {
            var (nid, card, _, _) = _dragTargets[i];
            float fx = (float)Canvas.GetLeft(card);
            float fy = (float)Canvas.GetTop(card);
            finalCoords.Add((nid, fx, fy));
        }

        bool moved = _dragMovedEnough;
        bool collapse = _dragCollapseToLeadOnClick;
        int leadId = _dragLeadNodeId;
        var snap = _dragSnapshotBeforeMove;

        _isDraggingNodes = false;
        _draggingHeaderElement = null;
        _dragSnapshotBeforeMove = null;
        _dragLeadNodeId = -1;
        _dragCollapseToLeadOnClick = false;
        _dragMovedEnough = false;

        try { header.ReleaseMouseCapture(); } catch { }

        if (moved && snap != null && _vm != null && live != null)
        {
            for (int i = 0; i < _dragTargets.Count; i++)
            {
                var (nid, _, origX, origY) = _dragTargets[i];
                if (live.FindNode(nid) is { } ln)
                {
                    ln.X = origX;
                    ln.Y = origY;
                }
            }
            _vm.EditSession.PushUndo("ノード移動");
            foreach (var (nid, fx, fy) in finalCoords)
            {
                if (live.FindNode(nid) is { } ln)
                {
                    ln.X = fx;
                    ln.Y = fy;
                }
            }
            _vm.EditSession.CommitToParameter();
        }
        else if (!moved && collapse && leadId >= 0)
        {
            SelectSingleNode(leadId);
        }

        _dragTargets.Clear();
    }

    private void UpdateSelectionHighlight()
    {
        var activeBrush = MakeFrozenRgb(251, 191, 36);
        var selBrush = MakeFrozenRgb(245, 158, 11);
        var normBrush = MakeFrozenRgb(18, 20, 24);
        foreach (var kv in _nodeCards)
        {
            bool sel = _selectedNodeIds.Contains(kv.Key);
            bool active = kv.Key == _activeNodeId;
            kv.Value.BorderBrush = sel ? (active ? activeBrush : selBrush) : normBrush;
            kv.Value.BorderThickness = new Thickness(sel ? 1.8 : 1.2);
        }
    }

    private FrameworkElement BuildOutputSocketRow(ShaderNode node, ShaderSocketDef sock)
    {
        var row = new Grid { Height = 20, Margin = new Thickness(8, 1, 0, 1) };
        row.Children.Add(new TextBlock
        {
            Text = sock.Label,
            Foreground = MakeFrozenRgb(220, 224, 232),
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        var pin = CreateSocketPin(new PinTag(node.Id, sock.Name, IsOutput: true, sock.Kind));
        pin.HorizontalAlignment = HorizontalAlignment.Right;
        pin.Margin = new Thickness(0, 0, -5, 0);
        row.Children.Add(pin);
        _pinEllipses[(node.Id, true, sock.Name)] = pin;
        return row;
    }

    private FrameworkElement BuildInputSocketRow(MaterialNodeGraph graph, ShaderNode node, ShaderSocketDef sock, bool isLinked)
    {
        var row = new Grid { MinHeight = 20, Margin = new Thickness(0, 2, 8, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pin = CreateSocketPin(new PinTag(node.Id, sock.Name, IsOutput: false, sock.Kind));
        pin.HorizontalAlignment = HorizontalAlignment.Left;
        pin.Margin = new Thickness(-5, 0, 0, 0);
        Grid.SetColumn(pin, 0);
        row.Children.Add(pin);
        _pinEllipses[(node.Id, false, sock.Name)] = pin;

        if (isLinked || sock.Kind is ShaderSocketKind.Vector or ShaderSocketKind.Shader || sock.ParamKey == null)
        {
            var tb = new TextBlock
            {
                Text = sock.Label,
                Foreground = MakeFrozenRgb(210, 215, 224),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            Grid.SetColumn(tb, 1);
            row.Children.Add(tb);
            return row;
        }

        if (sock.Kind == ShaderSocketKind.Color)
        {
            var cGrid = new Grid { Margin = new Thickness(4, 0, 0, 0) };
            cGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lbl = new TextBlock
            {
                Text = sock.Label,
                Foreground = MakeFrozenRgb(210, 215, 224),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(lbl, 0);
            cGrid.Children.Add(lbl);

            var swatch = CreateColorButton(node, sock.ParamKey, -1);
            swatch.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(swatch, 1);
            cGrid.Children.Add(swatch);
            Grid.SetColumn(cGrid, 1);
            row.Children.Add(cGrid);
            return row;
        }

        // Float スライダーバー (Blender 青プログレススタイル)
        var bar = CreateBlenderSliderBar(
            node, sock.ParamKey, sock.Label, sock.Min, sock.Max,
            node.GetFloat(sock.ParamKey, sock.DefaultFloat));
        bar.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        return row;
    }

    private Ellipse CreateSocketPin(PinTag tag)
    {
        var fill = GetFrozenBrush(ShaderNodeRegistry.GetSocketColorHex(tag.Kind));
        var normStroke = MakeFrozenRgb(20, 22, 26);
        var el = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = fill,
            Stroke = normStroke,
            StrokeThickness = 1.2,
            Cursor = Cursors.Cross,
            Tag = tag,
            VerticalAlignment = VerticalAlignment.Center
        };

        el.MouseEnter += (_, _) => { el.Stroke = Brushes.White; el.StrokeThickness = 1.6; };
        el.MouseLeave += (_, _) => { el.Stroke = normStroke; el.StrokeThickness = 1.2; };
        el.MouseLeftButtonDown += (s, e) =>
        {
            this.Focus();
            BeginWireDragFromPin(tag, ScreenToWorld(e.GetPosition(GraphSurface)));
            e.Handled = true;
        };
        el.MouseRightButtonDown += (_, e) => { e.Handled = true; };
        el.MouseRightButtonUp += (s, e) =>
        {
            if (!tag.IsOutput && _vm != null)
            {
                _vm.MutateActiveNodeGraph(g => g.RemoveLinkTo(tag.NodeId, tag.SocketName), description: "ノードリンク解除");
                RefreshFromActiveMaterial();
                e.Handled = true;
            }
        };
        return el;
    }

    private void AddNodeSpecialControls(StackPanel body, MaterialNodeGraph graph, ShaderNode node)
    {
        void AddDropdown(string paramKey, string[] options, string fallback)
        {
            var cb = new ComboBox
            {
                Style = (Style)FindResource("DarkNodeCombo"),
                Margin = new Thickness(10, 2, 10, 2)
            };
            string cur = node.GetString(paramKey, fallback);
            int selIdx = 0;
            for (int i = 0; i < options.Length; i++)
            {
                cb.Items.Add(options[i]);
                if (string.Equals(options[i], cur, StringComparison.OrdinalIgnoreCase))
                    selIdx = i;
            }
            cb.SelectedIndex = selIdx;
            cb.SelectionChanged += (_, _) =>
            {
                if (_suppressUiEvents || _vm == null) return;
                if (cb.SelectedItem is string val)
                {
                    _vm.MutateActiveNodeGraph(g =>
                    {
                        var tn = g.FindNode(node.Id);
                        if (tn != null) tn.StringParams[paramKey] = val;
                    }, description: $"ノード設定 ({val})", syncPanel: false);
                }
            };
            body.Children.Add(cb);
        }

        void AddCheck(string paramKey, string label, bool fallback)
        {
            var chk = new CheckBox
            {
                Content = label,
                IsChecked = node.GetBool(paramKey, fallback),
                Foreground = new SolidColorBrush(Color.FromRgb(215, 220, 228)),
                FontSize = 10.5,
                Margin = new Thickness(10, 2, 10, 2),
                Focusable = false
            };
            chk.Checked += (_, _) => ToggleBool(true);
            chk.Unchecked += (_, _) => ToggleBool(false);
            void ToggleBool(bool b)
            {
                if (_suppressUiEvents || _vm == null) return;
                _vm.MutateActiveNodeGraph(g =>
                {
                    var tn = g.FindNode(node.Id);
                    if (tn != null) tn.BoolParams[paramKey] = b;
                }, description: $"切替 ({label})", syncPanel: false);
            }
            body.Children.Add(chk);
        }

        switch (node.Type)
        {
            case ShaderNodeType.Value:
            {
                var bar = CreateBlenderSliderBar(node, "Value", "値", -100f, 100f, node.GetFloat("Value", 0.5f));
                bar.Margin = new Thickness(10, 2, 10, 2);
                body.Children.Add(bar);
                break;
            }
            case ShaderNodeType.RgbColor:
            {
                var btn = CreateColorButton(node, "Color", -1);
                btn.Margin = new Thickness(10, 2, 10, 2);
                btn.Height = 22;
                body.Children.Add(btn);
                break;
            }
            case ShaderNodeType.NoiseTexture:
                AddDropdown("Dimension", new[] { "3D", "2D" }, "3D");
                AddDropdown("NoiseType", new[] { "fBM", "Multifractal", "Ridged" }, "fBM");
                AddCheck("Normalize", "正規化", true);
                break;
            case ShaderNodeType.VoronoiTexture:
                AddDropdown("Feature", new[] { "F1", "F2", "SmoothF1" }, "F1");
                break;
            case ShaderNodeType.WaveTexture:
                AddDropdown("WaveType", new[] { "Bands", "Rings" }, "Bands");
                AddDropdown("Profile", new[] { "Sine", "Saw", "Triangle" }, "Sine");
                break;
            case ShaderNodeType.GradientTexture:
                AddDropdown("GradientType", new[] { "Linear", "Spherical", "Radial" }, "Linear");
                break;
            case ShaderNodeType.MixColor:
                AddDropdown("BlendType", new[] { "Mix", "Multiply", "Add", "Subtract", "Screen", "Overlay" }, "Mix");
                AddCheck("Clamp", "クランプ", true);
                break;
            case ShaderNodeType.Math:
                AddDropdown("Operation", new[]
                {
                    "Add", "Subtract", "Multiply", "Divide", "Power",
                    "Sine", "Cosine", "Abs", "Min", "Max", "GreaterThan", "LessThan", "Modulo", "PingPong", "SmoothMin"
                }, "Add");
                AddCheck("Clamp", "クランプ", false);
                break;
            case ShaderNodeType.MapRange:
                AddCheck("Clamp", "クランプ", true);
                break;
            case ShaderNodeType.Bump:
                AddCheck("Invert", "反転", false);
                break;
            case ShaderNodeType.NormalMap:
                AddCheck("FlipY", "Y反転 (DirectX)", false);
                break;
            case ShaderNodeType.ImageTexture:
                AddImageTextureControls(body, node);
                AddDropdown("ColorSpace", new[] { "sRGB", "Non-Color" }, "sRGB");
                AddCheck("FlipV", "上下反転 (Flip V)", false);
                break;
            case ShaderNodeType.ColorRamp:
                AddDropdown("Interpolation", new[] { "Linear", "Constant" }, "Linear");
                AddColorRampControls(body, node);
                break;
            case ShaderNodeType.TimeInfo:
                AddDropdown("UpdateFps", new[] { "フル (動画FPS)", "30 fps", "15 fps", "12 fps", "8 fps" }, "15 fps");
                AddCheck("AnimDraftRes", "アニメ時軽量ベイク (最大256px)", true);
                break;
            case ShaderNodeType.LayerTexture:
                AddLayerTextureControls(body, node);
                AddCheck("FlipV", "上下反転 (Flip V)", false);
                break;
            case ShaderNodeType.AmbientOcclusion:
                AddCheck("Inside", "内側 (凸エッジ)", false);
                AddCheck("OnlyLocal", "ローカルのみ", false);
                break;
            case ShaderNodeType.Wireframe:
                AddCheck("PixelSize", "ピクセルサイズ (Pixel)", true);
                break;
            case ShaderNodeType.VectorMath:
                AddDropdown("Operation", new[]
                {
                    "Add", "Subtract", "Multiply", "Divide", "Cross", "Dot", "Normalize",
                    "Length", "Distance", "Scale", "Reflect", "Project", "Snap", "Modulo",
                    "Absolute", "Minimum", "Maximum", "Sine", "Cosine", "Floor", "Fraction"
                }, "Add");
                break;
            case ShaderNodeType.MixValue:
                AddDropdown("DataType", new[] { "Float", "Vector" }, "Float");
                AddCheck("ClampFactor", "係数をクランプ", true);
                break;
        }
    }

    private void AddLayerTextureControls(StackPanel body, ShaderNode node)
    {
        int curLayer = Math.Clamp((int)MathF.Round(node.GetFloat("Layer", 0f)), 0, 200);
        var row = new Grid { Margin = new Thickness(10, 2, 10, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = "参照レイヤー:",
            Foreground = MakeFrozenRgb(210, 215, 224),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);
        row.Children.Add(lbl);

        var minusBtn = new Button
        {
            Style = (Style)FindResource("NodeToolBtn"),
            Content = "-",
            Width = 22,
            Margin = new Thickness(4, 0, 2, 0),
            Padding = new Thickness(0)
        };
        Grid.SetColumn(minusBtn, 1);
        row.Children.Add(minusBtn);

        var numText = new TextBlock
        {
            Text = curLayer.ToString(CultureInfo.InvariantCulture),
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(numText, 2);
        row.Children.Add(numText);

        var plusBtn = new Button
        {
            Style = (Style)FindResource("NodeToolBtn"),
            Content = "+",
            Width = 22,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(0)
        };
        Grid.SetColumn(plusBtn, 3);
        row.Children.Add(plusBtn);

        void StepLayer(int delta)
        {
            if (_vm == null) return;
            int next = Math.Clamp(curLayer + delta, 0, 200);
            if (next == curLayer) return;
            curLayer = next;
            numText.Text = curLayer.ToString(CultureInfo.InvariantCulture);
            _vm.MutateActiveNodeGraph(g =>
            {
                var tn = g.FindNode(node.Id);
                if (tn != null) tn.FloatParams["Layer"] = next;
            }, coalesceTag: $"layer:{node.Id}", description: $"レイヤー変更 ({next})");
        }

        minusBtn.Click += (_, _) => StepLayer(-1);
        plusBtn.Click += (_, _) => StepLayer(+1);
        body.Children.Add(row);
    }

    private void AddImageTextureControls(StackPanel body, ShaderNode node)
    {
        string path = node.GetString("FilePath");
        string name = string.IsNullOrWhiteSpace(path) ? "(未選択)" : System.IO.Path.GetFileName(path);

        var row = new Grid { Margin = new Thickness(10, 2, 10, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var openBtn = new Button
        {
            Style = (Style)FindResource("NodeToolBtn"),
            Content = $"画像: {name}",
            Margin = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = string.IsNullOrWhiteSpace(path) ? "画像ファイルを選択 (PNG/JPG/BMP/TGA/DDS)" : path
        };
        openBtn.Click += (_, _) =>
        {
            if (_vm == null) return;
            var dlg = new OpenFileDialog
            {
                Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.dds|すべて|*.*",
                Title = "ノード用画像テクスチャを選択"
            };
            if (dlg.ShowDialog() == true)
            {
                _vm.MutateActiveNodeGraph(g =>
                {
                    var tn = g.FindNode(node.Id);
                    if (tn != null) tn.StringParams["FilePath"] = dlg.FileName;
                }, description: "画像テクスチャ設定");
                RefreshFromActiveMaterial();
            }
        };
        Grid.SetColumn(openBtn, 0);
        row.Children.Add(openBtn);

        if (!string.IsNullOrWhiteSpace(path))
        {
            var clearBtn = new Button
            {
                Style = (Style)FindResource("NodeToolBtn"),
                Content = "×",
                Width = 22,
                Margin = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(0),
                ToolTip = "画像設定をクリア"
            };
            clearBtn.Click += (_, _) =>
            {
                if (_vm == null) return;
                _vm.MutateActiveNodeGraph(g =>
                {
                    var tn = g.FindNode(node.Id);
                    if (tn != null) tn.StringParams["FilePath"] = string.Empty;
                }, description: "画像テクスチャ解除");
                RefreshFromActiveMaterial();
            };
            Grid.SetColumn(clearBtn, 1);
            row.Children.Add(clearBtn);
        }

        body.Children.Add(row);
    }

    private void AddColorRampControls(StackPanel body, ShaderNode node)
    {
        if (node.RampStops.Count == 0)
            node.RampStops.Add(new ColorRampStop { Position = 0f, Color = new[] { 0f, 0f, 0f, 1f } });

        var gradBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        void RefreshRampGradient()
        {
            gradBrush.GradientStops.Clear();
            foreach (var st in node.RampStops.OrderBy(s => s.Position))
            {
                gradBrush.GradientStops.Add(new GradientStop(Color.FromArgb(
                    ToByte(st.Color[3]), ToByte(st.Color[0]), ToByte(st.Color[1]), ToByte(st.Color[2])), st.Position));
            }
        }
        RefreshRampGradient();
        _rampRefreshers[node.Id] = RefreshRampGradient;

        body.Children.Add(new Border
        {
            Height = 14,
            Margin = new Thickness(10, 3, 10, 3),
            CornerRadius = new CornerRadius(2),
            BorderBrush = MakeFrozenRgb(30, 34, 42),
            BorderThickness = new Thickness(1),
            Background = gradBrush
        });

        for (int i = 0; i < node.RampStops.Count; i++)
        {
            int idx = i;
            var stop = node.RampStops[i];
            var row = new Grid { Margin = new Thickness(10, 1.5, 10, 1.5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var swatch = CreateColorButton(node, "__RAMP__", idx);
            Grid.SetColumn(swatch, 0);
            row.Children.Add(swatch);

            var posBar = CreateBlenderSliderBar(
                node, $"__RAMP_POS_{idx}", $"#{idx}", 0f, 1f, stop.Position,
                customApply: (newPos, interactive) =>
                {
                    node.RampStops[idx].Position = newPos;
                    RefreshRampGradient();
                    if (interactive)
                    {
                        _vm?.MutateActiveNodeGraphInteractive(g =>
                        {
                            var tn = g.FindNode(node.Id);
                            if (tn != null && idx < tn.RampStops.Count) tn.RampStops[idx].Position = newPos;
                        }, coalesceTag: $"rampP:{node.Id}:{idx}", description: "ランプ位置");
                        RestartRefineTimer();
                    }
                    else
                    {
                        _refineTimer.Stop();
                        _vm?.MutateActiveNodeGraph(g =>
                        {
                            var tn = g.FindNode(node.Id);
                            if (tn != null && idx < tn.RampStops.Count) tn.RampStops[idx].Position = newPos;
                        }, coalesceTag: $"rampP:{node.Id}:{idx}", description: "ランプ位置", syncPanel: false);
                    }
                });
            posBar.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(posBar, 1);
            row.Children.Add(posBar);

            if (node.RampStops.Count > 2)
            {
                var delBtn = new Button
                {
                    Style = (Style)FindResource("NodeToolBtn"),
                    Content = "-",
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(0)
                };
                delBtn.Click += (_, _) =>
                {
                    _vm?.MutateActiveNodeGraph(g =>
                    {
                        var tn = g.FindNode(node.Id);
                        if (tn != null && tn.RampStops.Count > 2 && idx < tn.RampStops.Count)
                            tn.RampStops.RemoveAt(idx);
                    }, description: "カラーストップ削除");
                    RefreshFromActiveMaterial();
                };
                Grid.SetColumn(delBtn, 2);
                row.Children.Add(delBtn);
            }
            body.Children.Add(row);
        }

        var addBtn = new Button
        {
            Style = (Style)FindResource("NodeToolBtn"),
            Content = "+ ストップ追加",
            Height = 18,
            Margin = new Thickness(10, 3, 10, 2),
            FontSize = 10
        };
        addBtn.Click += (_, _) =>
        {
            _vm?.MutateActiveNodeGraph(g =>
            {
                var tn = g.FindNode(node.Id);
                if (tn != null)
                {
                    tn.RampStops.Add(new ColorRampStop { Position = 0.5f, Color = new[] { 0.5f, 0.7f, 0.9f, 1f } });
                }
            }, description: "カラーストップ追加");
            RefreshFromActiveMaterial();
        };
        body.Children.Add(addBtn);
    }

    private Border CreateBlenderSliderBar(
        ShaderNode node, string paramKey, string label, float min, float max, float initialVal,
        Action<float, bool>? customApply = null)
    {
        float curVal = Math.Clamp(initialVal, min, max);
        double norm = max > min ? (curVal - min) / (max - min) : 0.0;

        var barBg = new Border
        {
            Height = 18,
            Background = MakeFrozenRgb(64, 68, 76),
            BorderBrush = MakeFrozenRgb(30, 33, 39),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Cursor = Cursors.SizeWE
        };

        var grid = new Grid();
        var fill = new Border
        {
            Background = MakeFrozenRgb(58, 108, 174),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        var txtLabel = new TextBlock
        {
            Text = label,
            Foreground = MakeFrozenRgb(230, 235, 242),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(5, 0, 40, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var txtVal = new TextBlock
        {
            Text = curVal.ToString("0.000", CultureInfo.InvariantCulture),
            Foreground = Brushes.White,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 5, 0)
        };
        grid.Children.Add(fill);
        grid.Children.Add(txtLabel);
        grid.Children.Add(txtVal);
        barBg.Child = grid;

        void UpdateVisual(float v)
        {
            curVal = Math.Clamp(v, min, max);
            double frac = max > min ? (curVal - min) / (max - min) : 0.0;
            double avail = Math.Max(0.0, barBg.ActualWidth > 2 ? barBg.ActualWidth - 2 : 120.0);
            fill.Width = avail * Math.Clamp(frac, 0.0, 1.0);
            txtVal.Text = curVal.ToString("0.000", CultureInfo.InvariantCulture);
        }

        barBg.Loaded += (_, _) => UpdateVisual(curVal);
        barBg.SizeChanged += (_, _) => UpdateVisual(curVal);
        _sliderUpdaters[(node.Id, paramKey)] = UpdateVisual;

        void CommitValue(float v, bool syncPanel, bool interactive)
        {
            float clamped = Math.Clamp(v, min, max);
            if (interactive && MathF.Abs(clamped - curVal) < 1e-5f)
                return;

            UpdateVisual(clamped);
            if (customApply != null)
            {
                customApply(clamped, interactive);
                return;
            }
            if (interactive)
            {
                _vm?.MutateActiveNodeGraphInteractive(g =>
                {
                    var tn = g.FindNode(node.Id);
                    if (tn != null) tn.FloatParams[paramKey] = clamped;
                }, coalesceTag: $"n:{node.Id}:{paramKey}", description: $"{label} 調整");
                RestartRefineTimer();
            }
            else
            {
                _refineTimer.Stop();
                _vm?.MutateActiveNodeGraph(g =>
                {
                    var tn = g.FindNode(node.Id);
                    if (tn != null) tn.FloatParams[paramKey] = clamped;
                }, coalesceTag: $"n:{node.Id}:{paramKey}", description: $"{label} 調整", syncPanel: syncPanel);
            }
        }

        bool scrubbing = false;
        bool moved = false;
        Point downPt = default;
        float downVal = curVal;

        barBg.MouseLeftButtonDown += (s, e) =>
        {
            scrubbing = true;
            moved = false;
            downPt = e.GetPosition(barBg);
            downVal = curVal;
            barBg.CaptureMouse();
            e.Handled = true;
        };
        barBg.MouseMove += (s, e) =>
        {
            if (!scrubbing) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                scrubbing = false;
                try { barBg.ReleaseMouseCapture(); } catch { }
                return;
            }
            Point cur = e.GetPosition(barBg);
            double dx = cur.X - downPt.X;
            if (Math.Abs(dx) >= 3.0) moved = true;
            if (!moved) return;

            double scale = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.2 : 1.0;
            float span = max - min;
            float nextVal = downVal + (float)(dx / Math.Max(90.0, barBg.ActualWidth) * span * scale);
            CommitValue(nextVal, syncPanel: false, interactive: true);
        };
        barBg.LostMouseCapture += (_, _) => { scrubbing = false; moved = false; };
        barBg.MouseLeftButtonUp += (s, e) =>
        {
            if (!scrubbing) return;
            scrubbing = false;
            try { barBg.ReleaseMouseCapture(); } catch { }

            if (moved)
            {
                _refineTimer.Stop();
                _vm?.CommitInteractiveNodeGraph();
            }
            else
            {
                // クリックのみ = インライン数値入力ボックス表示
                OpenInlineNumberEditor(barBg, grid, curVal, v => CommitValue(v, syncPanel: true, interactive: false));
            }
            e.Handled = true;
        };

        return barBg;
    }

    private void OpenInlineNumberEditor(Border barBg, Grid grid, float initialVal, Action<float> onCommit)
    {
        var tb = new TextBox
        {
            Text = initialVal.ToString("0.###", CultureInfo.InvariantCulture),
            Background = new SolidColorBrush(Color.FromRgb(22, 25, 32)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 10.5,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        bool finished = false;
        void Finish(bool commit)
        {
            if (finished) return;
            finished = true;
            grid.Children.Remove(tb);
            if (commit && float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                onCommit(parsed);
            Keyboard.ClearFocus();
            this.Focus();
        }
        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
        };
        tb.LostKeyboardFocus += (_, _) => Finish(true);
        grid.Children.Add(tb);
        tb.Focus();
        tb.SelectAll();
    }

    private Button CreateColorButton(ShaderNode node, string paramKey, int rampStopIdx)
    {
        Vector4 c = rampStopIdx >= 0 && rampStopIdx < node.RampStops.Count
            ? new Vector4(node.RampStops[rampStopIdx].Color[0], node.RampStops[rampStopIdx].Color[1], node.RampStops[rampStopIdx].Color[2], node.RampStops[rampStopIdx].Color[3])
            : node.GetColor(paramKey, Vector4.One);

        var btn = new Button
        {
            Height = 16,
            Background = new SolidColorBrush(Color.FromArgb(ToByte(c.W), ToByte(c.X), ToByte(c.Y), ToByte(c.Z))),
            BorderBrush = new SolidColorBrush(Color.FromRgb(95, 105, 125)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = false
        };
        btn.Template = CreateSwatchTemplate();
        btn.Click += (_, _) =>
        {
            _pickerNodeId = node.Id;
            _pickerKey = paramKey;
            _pickerRampStopIndex = rampStopIdx;
            _pickerSnapshot = GetActiveGraph(false)?.Clone();
            _pickerTargetButton = btn;
            _pickerEdited = false;

            var g = GetActiveGraph(false);
            var curNode = g?.FindNode(node.Id);
            Vector4 curCol = Vector4.One;
            if (curNode != null)
            {
                if (rampStopIdx >= 0 && rampStopIdx < curNode.RampStops.Count)
                {
                    var rc = curNode.RampStops[rampStopIdx].Color;
                    curCol = new Vector4(rc[0], rc[1], rc[2], rc[3]);
                }
                else
                {
                    curCol = curNode.GetColor(paramKey, Vector4.One);
                }
            }

            _suppressUiEvents = true;
            try
            {
                PickerR.Value = ToByte(curCol.X);
                PickerG.Value = ToByte(curCol.Y);
                PickerB.Value = ToByte(curCol.Z);
                PickerA.Value = ToByte(curCol.W);
                UpdatePickerPreviewVisual(curCol);
            }
            finally
            {
                _suppressUiEvents = false;
            }
            ColorPickerPopup.IsOpen = true;
        };
        return btn;
    }

    private static ControlTemplate CreateSwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void InitColorPickerSwatches()
    {
        foreach (string hex in SwatchHexes)
        {
            var col = (Color)ColorConverter.ConvertFromString(hex);
            var b = new Button
            {
                Width = 18,
                Height = 16,
                Margin = new Thickness(1.5),
                Background = new SolidColorBrush(col),
                BorderBrush = new SolidColorBrush(Color.FromRgb(75, 82, 98)),
                BorderThickness = new Thickness(1),
                Focusable = false,
                Cursor = Cursors.Hand,
                Template = CreateSwatchTemplate()
            };
            b.Click += (_, _) =>
            {
                _suppressUiEvents = true;
                try
                {
                    PickerR.Value = col.R;
                    PickerG.Value = col.G;
                    PickerB.Value = col.B;
                }
                finally { _suppressUiEvents = false; }
                ApplyPickerColorToTarget(interactive: false);
            };
            PickerSwatchesPanel.Children.Add(b);
        }
    }

    private void OnPickerSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUiEvents) return;
        ApplyPickerColorToTarget(interactive: true);
    }

    private void OnPickerSliderCommitted(object? sender, EventArgs e)
    {
        if (_suppressUiEvents) return;
        ApplyPickerColorToTarget(interactive: false);
    }

    private void OnColorPickerPopupClosed(object? sender, EventArgs e)
    {
        if (_pickerEdited)
        {
            _pickerEdited = false;
            _refineTimer.Stop();
            _vm?.CommitInteractiveNodeGraph();
        }
        _pickerTargetButton = null;
    }

    private void ApplyPickerColorToTarget(bool interactive)
    {
        if (_vm == null || _pickerNodeId < 0) return;
        var vec = new Vector4(
            (float)(PickerR.Value / 255.0),
            (float)(PickerG.Value / 255.0),
            (float)(PickerB.Value / 255.0),
            (float)(PickerA.Value / 255.0));
        UpdatePickerPreviewVisual(vec);

        if (_pickerTargetButton != null)
        {
            _pickerTargetButton.Background = new SolidColorBrush(Color.FromArgb(
                ToByte(vec.W), ToByte(vec.X), ToByte(vec.Y), ToByte(vec.Z)));
        }

        if (interactive)
        {
            _pickerEdited = true;
            _vm.MutateActiveNodeGraphInteractive(g =>
            {
                var tn = g.FindNode(_pickerNodeId);
                if (tn == null) return;
                if (_pickerRampStopIndex >= 0 && _pickerRampStopIndex < tn.RampStops.Count)
                    tn.RampStops[_pickerRampStopIndex].Color = new[] { vec.X, vec.Y, vec.Z, vec.W };
                else
                    tn.SetColor(_pickerKey, vec);
            }, coalesceTag: $"col:{_pickerNodeId}:{_pickerKey}:{_pickerRampStopIndex}", description: "ノードカラー変更");
            if (_pickerRampStopIndex >= 0 && _rampRefreshers.TryGetValue(_pickerNodeId, out var refresh))
                refresh();
            RestartRefineTimer();
        }
        else
        {
            _pickerEdited = false;
            _refineTimer.Stop();
            _vm.MutateActiveNodeGraph(g =>
            {
                var tn = g.FindNode(_pickerNodeId);
                if (tn == null) return;
                if (_pickerRampStopIndex >= 0 && _pickerRampStopIndex < tn.RampStops.Count)
                    tn.RampStops[_pickerRampStopIndex].Color = new[] { vec.X, vec.Y, vec.Z, vec.W };
                else
                    tn.SetColor(_pickerKey, vec);
            }, coalesceTag: $"col:{_pickerNodeId}:{_pickerKey}:{_pickerRampStopIndex}", description: "ノードカラー変更");
            if (_pickerRampStopIndex >= 0 && _rampRefreshers.TryGetValue(_pickerNodeId, out var refresh))
                refresh();
        }
    }

    private void UpdatePickerPreviewVisual(Vector4 c)
    {
        byte r = ToByte(c.X), g = ToByte(c.Y), b = ToByte(c.Z), a = ToByte(c.W);
        PickerPreviewSwatch.Background = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        PickerHexLabel.Text = $"#{r:X2}{g:X2}{b:X2}";
    }

    #endregion

    #region ワイヤー接続 / 描画 / ヒット判定

    private Point GetPinWorldPosition(MaterialNodeGraph graph, int nodeId, bool isOutput, string socketName)
    {
        if (!_pinOffsetsValid || !_pinRelOffsets.ContainsKey((nodeId, isOutput, socketName)))
            EnsurePinOffsetsCached(graph);

        if (_pinRelOffsets.TryGetValue((nodeId, isOutput, socketName), out var rel) &&
            graph.FindNode(nodeId) is { } node)
        {
            return new Point(node.X + rel.X, node.Y + rel.Y);
        }
        if (_pinEllipses.TryGetValue((nodeId, isOutput, socketName), out var pin) &&
            (pin.IsLoaded || pin.IsArrangeValid) && pin.ActualWidth > 1.0)
        {
            try { return pin.TranslatePoint(new Point(pin.ActualWidth * 0.5, pin.ActualHeight * 0.5), WorldCanvas); }
            catch { }
        }
        return graph.FindNode(nodeId) is { } n
            ? new Point(n.X + (isOutput ? ShaderNodeRegistry.GetSpec(n.Type).DefaultWidth : 0), n.Y + 40)
            : new Point(0, 0);
    }

    private void RedrawAllWires(MaterialNodeGraph graph)
    {
        LinksCanvas.Children.Clear();
        foreach (var l in graph.Links)
        {
            var src = graph.FindNode(l.FromNodeId);
            if (src == null) continue;
            var outDef = ShaderNodeRegistry.GetSpec(src.Type).Outputs.FirstOrDefault(s => s.Name == l.FromSocket);
            var kind = outDef?.Kind ?? ShaderSocketKind.Float;

            Point p0 = GetPinWorldPosition(graph, l.FromNodeId, true, l.FromSocket);
            Point p1 = GetPinWorldPosition(graph, l.ToNodeId, false, l.ToSocket);
            var brush = GetFrozenBrush(ShaderNodeRegistry.GetSocketColorHex(kind));
            LinksCanvas.Children.Add(BuildBezierPath(p0, p1, brush, dashed: false));
        }

        if (_previewWire != null)
            LinksCanvas.Children.Add(_previewWire);
    }

    private static Path BuildBezierPath(Point p0, Point p1, Brush stroke, bool dashed)
    {
        double dist = Math.Max(42.0, Math.Abs(p1.X - p0.X) * 0.55);
        var c1 = new Point(p0.X + dist, p0.Y);
        var c2 = new Point(p1.X - dist, p1.Y);
        if (p1.X < p0.X)
        {
            c1 = new Point(p0.X + Math.Min(120.0, dist), p0.Y);
            c2 = new Point(p1.X - Math.Min(120.0, dist), p1.Y);
        }

        var fig = new PathFigure { StartPoint = p0, IsClosed = false };
        fig.Segments.Add(new BezierSegment(c1, c2, p1, true));
        var geom = new PathGeometry();
        geom.Figures.Add(fig);

        var path = new Path
        {
            Data = geom,
            Stroke = stroke,
            StrokeThickness = 2.2,
            Opacity = 0.92
        };
        if (dashed)
            path.StrokeDashArray = new DoubleCollection { 4, 3 };
        return path;
    }

    private void BeginWireDragFromPin(PinTag tag, Point posInWorld)
    {
        var graph = GetActiveGraph(false);
        if (graph == null || _vm == null) return;

        CancelTransientInteractions();

        // 既接続の入力ソケットをドラッグしたら、既存ワイヤーを引き抜いて付け替え／解除する (Blender流)
        if (!tag.IsOutput && graph.FindLinkTo(tag.NodeId, tag.SocketName) is { } existing)
        {
            var srcNode = graph.FindNode(existing.FromNodeId);
            if (srcNode != null)
            {
                var outDef = ShaderNodeRegistry.GetSpec(srcNode.Type).Outputs.FirstOrDefault(s => s.Name == existing.FromSocket);
                _vm.MutateActiveNodeGraph(g => g.RemoveLinkTo(tag.NodeId, tag.SocketName), description: "リンク解除", syncPanel: false);
                var live = GetActiveGraph(false) ?? graph;
                var detachedTag = new PinTag(existing.FromNodeId, existing.FromSocket, true, outDef?.Kind ?? ShaderSocketKind.Float);
                RebuildVisualGraph(live);
                NodesCanvas.UpdateLayout();
                EnsurePinOffsetsCached(live);
                RedrawAllWires(live);

                _wiringStart = detachedTag;
                _wiringStartWorld = GetPinWorldPosition(live, detachedTag.NodeId, true, detachedTag.SocketName);
                var brush = GetFrozenBrush(ShaderNodeRegistry.GetSocketColorHex(detachedTag.Kind));
                _previewWire = BuildBezierPath(_wiringStartWorld, posInWorld, brush, dashed: true);
                LinksCanvas.Children.Add(_previewWire);
                if (!GraphSurface.CaptureMouse())
                    CancelTransientInteractions();
                return;
            }
        }

        EnsurePinOffsetsCached(graph);
        _wiringStart = tag;
        _wiringStartWorld = GetPinWorldPosition(graph, tag.NodeId, tag.IsOutput, tag.SocketName);
        var b = GetFrozenBrush(ShaderNodeRegistry.GetSocketColorHex(tag.Kind));
        _previewWire = BuildBezierPath(_wiringStartWorld, posInWorld, b, dashed: true);
        LinksCanvas.Children.Add(_previewWire);
        if (!GraphSurface.CaptureMouse())
            CancelTransientInteractions();
    }

    private PinTag? FindNearestCompatiblePin(MaterialNodeGraph graph, PinTag src, Point worldPt)
    {
        EnsurePinOffsetsCached(graph);
        PinTag? best = null;
        double bestDist = 22.0 / Math.Max(0.45, _zoom);

        foreach (var kv in _pinEllipses)
        {
            if (kv.Value.Tag is not PinTag cand) continue;
            if (cand.IsOutput == src.IsOutput) continue;
            if (cand.NodeId == src.NodeId) continue;
            var targetNode = graph.FindNode(cand.NodeId);
            bool compat = (cand.Kind == ShaderSocketKind.Shader) == (src.Kind == ShaderSocketKind.Shader);
            if (!compat)
            {
                var inSide = !src.IsOutput ? src : cand;
                var outSide = src.IsOutput ? src : cand;
                var inNode = graph.FindNode(inSide.NodeId);
                compat = inNode?.Type == ShaderNodeType.MaterialOutput &&
                    string.Equals(inSide.SocketName, "Surface", StringComparison.Ordinal) &&
                    (outSide.Kind == ShaderSocketKind.Color || outSide.Kind == ShaderSocketKind.Float);
            }
            if (!compat) continue;

            Point cp = GetPinWorldPosition(graph, cand.NodeId, cand.IsOutput, cand.SocketName);
            double d = Math.Sqrt((cp.X - worldPt.X) * (cp.X - worldPt.X) + (cp.Y - worldPt.Y) * (cp.Y - worldPt.Y));
            if (d < bestDist)
            {
                bestDist = d;
                best = cand;
            }
        }
        return best;
    }

    #endregion

    #region キャンバス操作 (パン / ズーム / メニュー / 削除)

    private Point ScreenToWorld(Point screenPt)
    {
        Matrix inv = PanZoomTransform.Matrix;
        if (!inv.HasInverse) return screenPt;
        inv.Invert();
        return inv.Transform(screenPt);
    }

    private void ApplyTransform()
    {
        var m = new Matrix();
        m.Scale(_zoom, _zoom);
        m.Translate(_panX, _panY);
        PanZoomTransform.Matrix = m;
    }

    private void FrameAllNodes(MaterialNodeGraph graph)
    {
        if (graph.Nodes.Count == 0)
        {
            _zoom = 1.0; _panX = 32; _panY = 24;
            ApplyTransform();
            return;
        }
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        foreach (var n in graph.Nodes)
        {
            var spec = ShaderNodeRegistry.GetSpec(n.Type);
            minX = MathF.Min(minX, n.X);
            minY = MathF.Min(minY, n.Y);
            maxX = MathF.Max(maxX, n.X + spec.DefaultWidth);
            maxY = MathF.Max(maxY, n.Y + 240f);
        }
        double surfW = Math.Max(300.0, GraphSurface.ActualWidth);
        double surfH = Math.Max(220.0, GraphSurface.ActualHeight);
        double contentW = Math.Max(180.0, maxX - minX + 80.0);
        double contentH = Math.Max(160.0, maxY - minY + 80.0);
        _zoom = Math.Clamp(Math.Min(surfW / contentW, surfH / contentH), 0.45, 1.05);
        double cx = (minX + maxX) * 0.5;
        double cy = (minY + maxY) * 0.5;
        _panX = surfW * 0.5 - cx * _zoom;
        _panY = surfH * 0.5 - cy * _zoom;
        ApplyTransform();
    }

    private void OnSurfaceLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isPanning || _isBoxSelecting || _wiringStart != null)
            CancelTransientInteractions();
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        this.Focus();
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (e.ChangedButton == MouseButton.Left && !alt && !IsDescendantOfNodeCard(e.OriginalSource as DependencyObject))
        {
            CancelTransientInteractions();
            _isBoxSelecting = true;
            _boxSelectAdditive = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            _boxStartScreen = e.GetPosition(GraphSurface);
            SelectionBoxRect.Width = 0;
            SelectionBoxRect.Height = 0;
            SelectionBoxRect.Visibility = Visibility.Collapsed;
            if (!GraphSurface.CaptureMouse())
                CancelTransientInteractions();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && alt) ||
            e.ChangedButton == MouseButton.Right)
        {
            CancelTransientInteractions();
            _isPanning = true;
            _rightClickPossibleMenu = e.ChangedButton == MouseButton.Right;
            _panStartScreen = e.GetPosition(GraphSurface);
            _panStartX = _panX;
            _panStartY = _panY;
            if (!GraphSurface.CaptureMouse())
            {
                if (_rightClickPossibleMenu && e.ChangedButton == MouseButton.Right)
                {
                    _isPanning = false;
                    _rightClickPossibleMenu = false;
                    OpenContextMenuAt(e.GetPosition(GraphSurface), FindNodeCardIdFromElement(e.OriginalSource as DependencyObject));
                }
                else
                {
                    CancelTransientInteractions();
                }
            }
            e.Handled = true;
        }
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_wiringStart != null && _previewWire != null)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CancelTransientInteractions();
                return;
            }
            var graph = GetActiveGraph(false);
            if (graph == null)
            {
                CancelTransientInteractions();
                return;
            }
            Point curW = ScreenToWorld(e.GetPosition(GraphSurface));
            Point p0 = _wiringStart.IsOutput ? _wiringStartWorld : curW;
            Point p1 = _wiringStart.IsOutput ? curW : _wiringStartWorld;
            var brush = GetFrozenBrush(ShaderNodeRegistry.GetSocketColorHex(_wiringStart.Kind));
            LinksCanvas.Children.Remove(_previewWire);
            _previewWire = BuildBezierPath(p0, p1, brush, dashed: true);
            LinksCanvas.Children.Add(_previewWire);
            return;
        }

        if (_isBoxSelecting)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CancelTransientInteractions();
                return;
            }
            Point cur = e.GetPosition(GraphSurface);
            double x = Math.Min(_boxStartScreen.X, cur.X);
            double y = Math.Min(_boxStartScreen.Y, cur.Y);
            double w = Math.Abs(cur.X - _boxStartScreen.X);
            double h = Math.Abs(cur.Y - _boxStartScreen.Y);
            Canvas.SetLeft(SelectionBoxRect, x);
            Canvas.SetTop(SelectionBoxRect, y);
            SelectionBoxRect.Width = w;
            SelectionBoxRect.Height = h;
            SelectionBoxRect.Visibility = (w >= 4.0 || h >= 4.0) ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (_isPanning)
        {
            if (e.MiddleButton != MouseButtonState.Pressed &&
                e.RightButton != MouseButtonState.Pressed &&
                e.LeftButton != MouseButtonState.Pressed)
            {
                CancelTransientInteractions();
                return;
            }
            Point cur = e.GetPosition(GraphSurface);
            double dx = cur.X - _panStartScreen.X;
            double dy = cur.Y - _panStartScreen.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 4.0)
                _rightClickPossibleMenu = false;
            _panX = _panStartX + dx;
            _panY = _panStartY + dy;
            ApplyTransform();
        }
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_wiringStart != null)
        {
            var graph = GetActiveGraph(false);
            var start = _wiringStart;
            _wiringStart = null;
            if (_previewWire != null)
            {
                LinksCanvas.Children.Remove(_previewWire);
                _previewWire = null;
            }
            try { GraphSurface.ReleaseMouseCapture(); } catch { }

            if (graph != null && _vm != null)
            {
                Point dropW = ScreenToWorld(e.GetPosition(GraphSurface));
                var target = FindNearestCompatiblePin(graph, start, dropW);
                if (target != null)
                {
                    var fromPin = start.IsOutput ? start : target;
                    var toPin = start.IsOutput ? target : start;
                    bool linked = false;
                    _vm.MutateActiveNodeGraph(g =>
                    {
                        linked = g.TryAddLink(fromPin.NodeId, fromPin.SocketName, toPin.NodeId, toPin.SocketName);
                    }, description: "ノード接続", syncPanel: false);
                    if (linked)
                        RefreshFromActiveMaterial();
                }
            }
            e.Handled = true;
            return;
        }

        if (_isBoxSelecting)
        {
            bool additive = _boxSelectAdditive;
            _isBoxSelecting = false;
            _boxSelectAdditive = false;
            SelectionBoxRect.Visibility = Visibility.Collapsed;
            try { GraphSurface.ReleaseMouseCapture(); } catch { }

            Point endPt = e.GetPosition(GraphSurface);
            double dx = Math.Abs(endPt.X - _boxStartScreen.X);
            double dy = Math.Abs(endPt.Y - _boxStartScreen.Y);
            if (dx < 4.0 && dy < 4.0)
            {
                if (!additive && _selectedNodeIds.Count > 0)
                {
                    _selectedNodeIds.Clear();
                    _activeNodeId = -1;
                    UpdateSelectionHighlight();
                }
            }
            else if (GetActiveGraph(false) is { } graph)
            {
                Point w1 = ScreenToWorld(_boxStartScreen);
                Point w2 = ScreenToWorld(endPt);
                var boxRect = new Rect(
                    Math.Min(w1.X, w2.X),
                    Math.Min(w1.Y, w2.Y),
                    Math.Max(1.0, Math.Abs(w2.X - w1.X)),
                    Math.Max(1.0, Math.Abs(w2.Y - w1.Y)));

                if (!additive)
                    _selectedNodeIds.Clear();

                int lastHit = -1;
                foreach (var n in graph.Nodes)
                {
                    var spec = ShaderNodeRegistry.GetSpec(n.Type);
                    double cardW = spec.DefaultWidth;
                    double cardH = 140.0;
                    if (_nodeCards.TryGetValue(n.Id, out var card) && card.ActualHeight > 1.0)
                    {
                        cardW = card.ActualWidth > 1.0 ? card.ActualWidth : cardW;
                        cardH = card.ActualHeight;
                    }
                    var nodeRect = new Rect(n.X, n.Y, cardW, cardH);
                    if (boxRect.IntersectsWith(nodeRect))
                    {
                        _selectedNodeIds.Add(n.Id);
                        lastHit = n.Id;
                    }
                }
                _activeNodeId = lastHit >= 0 ? lastHit : (_selectedNodeIds.Count > 0 ? _selectedNodeIds.First() : -1);
                UpdateSelectionHighlight();
            }
            e.Handled = true;
            return;
        }

        if (_isPanning)
        {
            bool openMenu = _rightClickPossibleMenu && e.ChangedButton == MouseButton.Right;
            _isPanning = false;
            _rightClickPossibleMenu = false;
            try { GraphSurface.ReleaseMouseCapture(); } catch { }
            if (openMenu)
            {
                Point pt = e.GetPosition(GraphSurface);
                var hitResult = VisualTreeHelper.HitTest(NodesCanvas, ScreenToWorld(pt))?.VisualHit;
                OpenContextMenuAt(pt, FindNodeCardIdFromElement(hitResult));
            }
            e.Handled = true;
        }
    }

    private void OnSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point mouseScreen = e.GetPosition(GraphSurface);
        Point beforeWorld = ScreenToWorld(mouseScreen);
        double factor = e.Delta > 0 ? 1.12 : 1.0 / 1.12;
        _zoom = Math.Clamp(_zoom * factor, 0.35, 2.2);
        _panX = mouseScreen.X - beforeWorld.X * _zoom;
        _panY = mouseScreen.Y - beforeWorld.Y * _zoom;
        ApplyTransform();
        e.Handled = true;
    }

    internal void OpenAddNodeMenuAtCursor()
    {
        Point screen = Mouse.GetPosition(GraphSurface);
        if (screen.X < 0 || screen.Y < 0 || screen.X > GraphSurface.ActualWidth || screen.Y > GraphSurface.ActualHeight)
            screen = new Point(GraphSurface.ActualWidth * 0.5, GraphSurface.ActualHeight * 0.5);
        OpenAddNodeMenuAt(screen);
    }

    private void OnAddNodeMenuClick(object sender, RoutedEventArgs e)
        => OpenAddNodeMenuAt(new Point(Math.Max(60, GraphSurface.ActualWidth * 0.45), Math.Max(40, GraphSurface.ActualHeight * 0.4)), BtnAddShaderNode);

    private void OpenAddNodeMenuAt(Point surfacePt, UIElement? anchor = null)
    {
        if (GetActiveGraph(true) == null) return;
        Point worldPt = ScreenToWorld(surfacePt);

        var menu = CreateDarkContextMenu();
        PopulateAddNodeMenuItems(menu, worldPt);

        menu.PlacementTarget = anchor ?? GraphSurface;
        menu.Placement = anchor != null ? PlacementMode.Bottom : PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void OpenContextMenuAt(Point surfacePt, int? rightClickedNodeId)
    {
        var graph = GetActiveGraph(true);
        if (graph == null) return;

        if (rightClickedNodeId.HasValue && !_selectedNodeIds.Contains(rightClickedNodeId.Value))
            SelectSingleNode(rightClickedNodeId.Value);

        Point worldPt = ScreenToWorld(surfacePt);
        var menu = CreateDarkContextMenu();

        bool hasCopyable = _selectedNodeIds.Any(id =>
            graph.FindNode(id) is { } sn && sn.Type != ShaderNodeType.MaterialOutput);

        var miCopy = CreateDarkMenuItem("コピー (Ctrl+C)");
        miCopy.IsEnabled = hasCopyable;
        miCopy.Click += (_, _) => CopySelectedNodes();
        menu.Items.Add(miCopy);

        var miPaste = CreateDarkMenuItem("ここに貼り付け (Ctrl+V)");
        miPaste.IsEnabled = CanPasteFromClipboard();
        miPaste.Click += (_, _) => PasteNodesAtCursorOrOffset(worldPt);
        menu.Items.Add(miPaste);

        var miDup = CreateDarkMenuItem("複製 (Shift+D)");
        miDup.IsEnabled = hasCopyable;
        miDup.Click += (_, _) => DuplicateSelectedNodes();
        menu.Items.Add(miDup);

        var miDel = CreateDarkMenuItem("選択ノードを削除 (Del/X)");
        miDel.IsEnabled = hasCopyable;
        miDel.Click += (_, _) => DeleteSelectedNodes();
        menu.Items.Add(miDel);

        menu.Items.Add(CreateDarkSeparator());
        PopulateAddNodeMenuItems(menu, worldPt);

        menu.PlacementTarget = GraphSurface;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void PopulateAddNodeMenuItems(ItemsControl menu, Point worldPt)
    {
        var byCat = ShaderNodeRegistry.GetAddableSpecs().GroupBy(s => s.Category);
        foreach (var group in byCat)
        {
            string catHex = group.First().HeaderColorHex;
            var catItem = CreateDarkMenuItem(group.Key, catHex);
            foreach (var spec in group)
            {
                var nodeType = spec.Type;
                var mi = CreateDarkMenuItem(spec.Title, spec.HeaderColorHex);
                mi.Click += (_, _) =>
                {
                    _vm?.MutateActiveNodeGraph(g =>
                    {
                        var added = g.AddNode(nodeType, (float)Math.Round(worldPt.X - 80), (float)Math.Round(worldPt.Y - 30));
                        SelectSingleNode(added.Id);
                    }, description: $"ノード追加 ({spec.Title})");
                    RefreshFromActiveMaterial();
                };
                catItem.Items.Add(mi);
            }
            menu.Items.Add(catItem);
        }
    }

    private void OnPresetMenuClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null || !_vm.EditSession.IsActive) return;

        var menu = CreateDarkContextMenu();
        void AddPreset(string label, string key, string dotHex)
        {
            var mi = CreateDarkMenuItem(label, dotHex);
            mi.Click += (_, _) =>
            {
                _vm.ApplyNodeGraphPreset(key);
                _hasInitialFramed = false;
                RefreshFromActiveMaterial();
            };
            menu.Items.Add(mi);
        }

        AddPreset("ノイズ ＋ バンプ (Blender風凹凸/ガラス向け)", "NoiseBump", "#9B5224");
        AddPreset("ノイズ ＋ カラーランプ (大理石 / マーブル)", "NoiseColorRamp", "#857427");
        AddPreset("ボロノイ凹凸 (細胞 / 石畳風バンプ)", "VoronoiBump", "#4B4794");
        AddPreset("チェッカー模様", "Checker", "#9B5224");
        AddPreset("レンガ壁 ＋ 目地バンプ", "BrickBump", "#9B5224");
        AddPreset("画像ノーマルマップ配線", "ImageNormal", "#4B4794");
        menu.Items.Add(CreateDarkSeparator());
        AddPreset("トゥーン / セルシェーディング (Shader to RGB)", "ToonCel", "#356478");
        AddPreset("薄膜干渉 / シャボン玉・オーロラ", "IridescentSoap", "#2D6F47");
        AddPreset("フレネル発光リム (Layer Weight)", "FresnelRim", "#7D3048");
        AddPreset("ワイヤーフレーム発光", "WireframeGlow", "#7D3048");
        AddPreset("ベルベット / シーン生地", "VelvetSheen", "#2D6F47");
        AddPreset("異方性ヘアライン金属", "BrushedAniso", "#2D6F47");
        AddPreset("サブサーフェス (ワックス / 翡翠)", "SubsurfaceWax", "#2D6F47");
        AddPreset("YMM4 別レイヤー貼付 (Layer 0)", "LayerScreen", "#9B5224");
        menu.Items.Add(CreateDarkSeparator());
        AddPreset("基本 プリンシプルBSDF (リセット)", "Basic", "#2D6F47");

        menu.PlacementTarget = BtnNodePresets;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private ContextMenu CreateDarkContextMenu() => new()
    {
        Style = (Style)FindResource("DarkContextMenu")
    };

    private MenuItem CreateDarkMenuItem(string header, string? dotColorHex = null)
    {
        var mi = new MenuItem
        {
            Style = (Style)FindResource("DarkMenuItem"),
            Header = header
        };
        if (!string.IsNullOrEmpty(dotColorHex))
        {
            mi.Icon = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = GetFrozenBrush(dotColorHex)
            };
        }
        return mi;
    }

    private Separator CreateDarkSeparator() => new()
    {
        Style = (Style)FindResource("DarkMenuSeparator")
    };

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase) return;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;

        if (ctrl && !alt && !shift && e.Key == Key.C) { CopySelectedNodes(); e.Handled = true; }
        else if (ctrl && !alt && !shift && e.Key == Key.V) { PasteNodesAtCursorOrOffset(); e.Handled = true; }
        else if (shift && !ctrl && !alt && e.Key == Key.D) { DuplicateSelectedNodes(); e.Handled = true; }
        else if (ctrl && !alt && !shift && e.Key == Key.A) { SelectAllNodes(); e.Handled = true; }
        else if (shift && !ctrl && !alt && e.Key == Key.A) { OpenAddNodeMenuAtCursor(); e.Handled = true; }
        else if (!ctrl && !alt && e.Key is Key.Delete or Key.X) { DeleteSelectedNodes(); e.Handled = true; }
        else if (!ctrl && !alt && e.Key == Key.Escape) { CancelTransientInteractions(); e.Handled = true; }
    }

    private void OnCopySelectedNodesClick(object sender, RoutedEventArgs e) => CopySelectedNodes();

    private void OnPasteNodesClick(object sender, RoutedEventArgs e)
    {
        Point? centerWorld = (GraphSurface.ActualWidth > 40 && GraphSurface.ActualHeight > 40)
            ? ScreenToWorld(new Point(GraphSurface.ActualWidth * 0.5, GraphSurface.ActualHeight * 0.5))
            : null;
        PasteNodesAtCursorOrOffset(centerWorld);
    }

    private void OnShadeMenuClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null || !_vm.EditSession.IsActive) return;

        var menu = CreateDarkContextMenu();
        var miSmooth = CreateDarkMenuItem("スムーズシェード（滑らか）");
        miSmooth.Click += (_, _) => _vm.ShadeSmoothCommand.Execute(null);
        menu.Items.Add(miSmooth);

        var miAuto = CreateDarkMenuItem("自動スムーズ (35° / 角を保持)");
        miAuto.Click += (_, _) => _vm.AutoSmoothCommand.Execute(null);
        menu.Items.Add(miAuto);

        var miFlat = CreateDarkMenuItem("フラットシェード（カクカク）");
        miFlat.Click += (_, _) => _vm.ShadeFlatCommand.Execute(null);
        menu.Items.Add(miFlat);

        menu.Items.Add(CreateDarkSeparator());
        var miReload = CreateDarkMenuItem("元モデルから法線を再読込");
        miReload.IsEnabled = _vm.ReloadSourceNormalsCommand.CanExecute(null);
        miReload.Click += (_, _) => _vm.ReloadSourceNormalsCommand.Execute(null);
        menu.Items.Add(miReload);

        menu.PlacementTarget = BtnShadeMenu;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static string? TryReadExternalClipboardText()
    {
        try
        {
            if (Clipboard.ContainsText())
                return Clipboard.GetText();
        }
        catch { }
        return null;
    }

    private static bool CanPasteFromClipboard()
    {
        if (ShaderNodeClipboard.HasData) return true;
        return ShaderNodeClipboard.TryDeserializePayload(TryReadExternalClipboardText(), out _);
    }

    internal bool CopySelectedNodes()
    {
        var graph = GetActiveGraph(false);
        if (graph == null || _selectedNodeIds.Count == 0) return false;

        string? text = ShaderNodeClipboard.CopySubgraph(graph, _selectedNodeIds);
        if (string.IsNullOrEmpty(text)) return false;

        try { Clipboard.SetText(text); } catch { }
        return true;
    }

    internal bool PasteNodesAtCursorOrOffset(Point? worldTarget = null)
    {
        if (_vm == null || !_vm.EditSession.IsActive) return false;
        if (GetActiveGraph(true) == null) return false;

        Point? center = worldTarget;
        if (!center.HasValue && GraphSurface.ActualWidth > 40 && GraphSurface.ActualHeight > 40)
        {
            Point mouse = Mouse.GetPosition(GraphSurface);
            if (mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= GraphSurface.ActualWidth && mouse.Y <= GraphSurface.ActualHeight)
                center = ScreenToWorld(mouse);
        }

        string? extText = TryReadExternalClipboardText();
        List<int>? pastedIds = null;
        _vm.MutateActiveNodeGraph(g =>
        {
            if (ShaderNodeClipboard.PasteSubgraph(
                g,
                center.HasValue ? (float)center.Value.X : null,
                center.HasValue ? (float)center.Value.Y : null,
                out var ids,
                extText))
            {
                pastedIds = ids;
            }
        }, description: "ノード貼り付け");

        if (pastedIds == null || pastedIds.Count == 0) return false;

        _selectedNodeIds.Clear();
        foreach (int id in pastedIds) _selectedNodeIds.Add(id);
        _activeNodeId = pastedIds[^1];
        RefreshFromActiveMaterial();
        return true;
    }

    internal bool DuplicateSelectedNodes()
    {
        var graph = GetActiveGraph(false);
        if (_vm == null || graph == null || _selectedNodeIds.Count == 0) return false;

        string? serialized = ShaderNodeClipboard.CopySubgraph(graph, _selectedNodeIds);
        if (string.IsNullOrEmpty(serialized)) return false;

        List<int>? dupIds = null;
        _vm.MutateActiveNodeGraph(g =>
        {
            if (ShaderNodeClipboard.PasteSubgraph(g, null, null, out var ids, serialized))
                dupIds = ids;
        }, description: "ノード複製");

        if (dupIds == null || dupIds.Count == 0) return false;

        _selectedNodeIds.Clear();
        foreach (int id in dupIds) _selectedNodeIds.Add(id);
        _activeNodeId = dupIds[^1];
        RefreshFromActiveMaterial();
        return true;
    }

    internal void SelectAllNodes()
    {
        var graph = GetActiveGraph(false);
        if (graph == null) return;
        _selectedNodeIds.Clear();
        int last = -1;
        foreach (var n in graph.Nodes)
        {
            _selectedNodeIds.Add(n.Id);
            last = n.Id;
        }
        _activeNodeId = last;
        UpdateSelectionHighlight();
    }

    private void OnDeleteSelectedNodeClick(object sender, RoutedEventArgs e) => DeleteSelectedNodes();

    internal void DeleteSelectedNode() => DeleteSelectedNodes();

    internal void DeleteSelectedNodes()
    {
        if (_vm == null || _selectedNodeIds.Count == 0) return;
        var toRemove = _selectedNodeIds.ToList();
        bool removedAny = false;
        _vm.MutateActiveNodeGraph(g =>
        {
            foreach (int id in toRemove)
            {
                if (g.RemoveNode(id))
                    removedAny = true;
            }
        }, description: toRemove.Count > 1 ? $"ノード削除 ({toRemove.Count})" : "ノード削除");

        if (removedAny)
        {
            var live = GetActiveGraph(false);
            _selectedNodeIds.RemoveWhere(id => live?.FindNode(id) == null);
            _activeNodeId = _selectedNodeIds.Count > 0 ? _selectedNodeIds.First() : -1;
            RefreshFromActiveMaterial();
        }
    }

    private void OnBakeResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _vm == null) return;
        if (CmbBakeResolution.SelectedItem is ComboBoxItem ci && ci.Tag is string s && int.TryParse(s, out int res))
        {
            _vm.MutateActiveNodeGraph(g => g.BakeResolution = res, description: $"ベイク解像度 ({res})");
        }
    }

    private void OnFrameAllNodesClick(object sender, RoutedEventArgs e)
    {
        if (GetActiveGraph(false) is { } g)
            FrameAllNodes(g);
    }

    #endregion

    private static byte ToByte(float v) => (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);
}
