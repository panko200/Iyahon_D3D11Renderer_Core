using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using YukkuriMovieMaker.Commons;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.CameraPath;

/// <summary>
/// D3D11 カメラパスの設定点リスト・詳細パラメータ・D3D11ビューポート操作 UI。
/// </summary>
public partial class D3DCameraKeyframeEditor : UserControl, IPropertyEditorControl2, IPropertyEditorControl
{
    public static readonly DependencyProperty KeyframesProperty =
        DependencyProperty.Register(
            nameof(Keyframes),
            typeof(ImmutableList<D3DCameraKeyframe>),
            typeof(D3DCameraKeyframeEditor),
            new FrameworkPropertyMetadata(
                ImmutableList<D3DCameraKeyframe>.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (sender, _) =>
                {
                    if (sender is D3DCameraKeyframeEditor editor)
                    {
                        editor.SyncLookAtFlag();
                        editor.RebuildRows();
                        D3DCameraPathViewportBridge.NotifyPropertyEdited();
                    }
                }));

    public static readonly DependencyProperty TargetKeyframesProperty =
        DependencyProperty.Register(
            nameof(TargetKeyframes),
            typeof(IEnumerable<D3DCameraKeyframe>),
            typeof(D3DCameraKeyframeEditor),
            new PropertyMetadata(null));

    private static readonly Brush SelectedBackground = Frozen(Color.FromArgb(0x3A, 0xFF, 0xC4, 0x6B));
    private static readonly Brush HoverBackground = Frozen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SelectedAccent = Frozen(Color.FromRgb(0xFF, 0xC4, 0x6B));
    private static readonly Brush CurveStroke = Frozen(Color.FromRgb(0x3D, 0x7F, 0xB8));
    private static readonly Brush CurveStrokeActive = Frozen(Color.FromRgb(0xFF, 0xC4, 0x6B));
    private static readonly Brush SegmentGuide = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SegmentGuideActive = Frozen(Color.FromArgb(0x77, 0xFF, 0xC4, 0x6B));

    private ItemProperty[]? properties;
    private D3DCameraPathVideoEffect? effect;
    private IEditorInfo? editorInfo;

    private readonly List<RowVisual> rows = new();
    private readonly D3DCameraBezierAnimationEditorAttribute segmentAttribute = new();
    private YukkuriMovieMaker.Controls.BezierAnimationEditor? segmentEditor;
    private D3DCameraKeyframe? segmentOwner;

    private int selectedIndex = -1;
    private readonly SortedSet<int> selection = new();
    private bool _suppressBridgeSync;

    public event EventHandler? BeginEdit;
    public event EventHandler? EndEdit;

    public ImmutableList<D3DCameraKeyframe> Keyframes
    {
        get => (ImmutableList<D3DCameraKeyframe>)GetValue(KeyframesProperty);
        set => SetValue(KeyframesProperty, value);
    }

    public IEnumerable<D3DCameraKeyframe>? TargetKeyframes
    {
        get => (IEnumerable<D3DCameraKeyframe>?)GetValue(TargetKeyframesProperty);
        set => SetValue(TargetKeyframesProperty, value);
    }

    internal int SelectedIndex => selectedIndex;
    internal IReadOnlyCollection<int> SelectedIndices => selection;
    internal D3DCameraKeyframe? SegmentOwner => segmentOwner;
    internal IReadOnlyList<RowVisual> Rows => rows;

    public ItemProperty[]? Properties
    {
        get => properties;
        set
        {
            if (properties == value)
                return;

            properties = value;
            HookEffect();
            SelectKeyframe(selectedIndex);
        }
    }

    public D3DCameraKeyframeEditor()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        D3DCameraPathViewportBridge.KeyframePickedFromViewport -= OnViewportKeyframePicked;
        D3DCameraPathViewportBridge.KeyframePickedFromViewport += OnViewportKeyframePicked;
        D3DCameraPathViewportBridge.PathEditedFromViewport -= OnViewportPathEdited;
        D3DCameraPathViewportBridge.PathEditedFromViewport += OnViewportPathEdited;

        if (effect != null)
            D3DCameraPathViewportBridge.SetActiveSelection(effect, selection, selectedIndex);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        D3DCameraPathViewportBridge.KeyframePickedFromViewport -= OnViewportKeyframePicked;
        D3DCameraPathViewportBridge.PathEditedFromViewport -= OnViewportPathEdited;
        D3DCameraPathViewportBridge.ClearActiveEditor(effect);
    }

    private void OnViewportKeyframePicked(D3DCameraPathVideoEffect srcEffect, int index)
    {
        if (!ReferenceEquals(srcEffect, effect)) return;
        Dispatcher.BeginInvoke(() =>
        {
            _suppressBridgeSync = true;
            try
            {
                PickKeyframe(index);
            }
            finally
            {
                _suppressBridgeSync = false;
            }
        });
    }

    private void OnViewportPathEdited(D3DCameraPathVideoEffect srcEffect)
    {
        if (!ReferenceEquals(srcEffect, effect)) return;
        Dispatcher.BeginInvoke(() =>
        {
            UpdateButtonState();
            UpdateSelectionVisuals();
        });
    }

    private void HookEffect()
    {
        var owner = properties is { Length: > 0 }
            ? properties[0].PropertyOwner as D3DCameraPathVideoEffect
            : null;

        if (ReferenceEquals(owner, effect))
            return;

        if (effect is not null)
        {
            effect.PropertyChanged -= Effect_PropertyChanged;
            D3DCameraPathViewportBridge.ClearActiveEditor(effect);
        }

        effect = owner;

        if (effect is not null)
            effect.PropertyChanged += Effect_PropertyChanged;

        SyncLookAtFlag();
        RebuildRows();
        if (effect != null)
            D3DCameraPathViewportBridge.SetActiveSelection(effect, selection, selectedIndex);
    }

    private void Effect_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(D3DCameraPathVideoEffect.Keyframes))
            return;

        if (e.PropertyName == nameof(D3DCameraPathVideoEffect.LookAt))
            SyncLookAtFlag();

        if (e.PropertyName == nameof(D3DCameraPathVideoEffect.TimeControl))
        {
            if (effect?.TimeControl != D3DCameraTimeControl.SegmentBezier)
                CloseSegmentEditor();

            RebuildRows();
        }

        D3DCameraPathViewportBridge.NotifyPropertyEdited();
    }

    private void SyncLookAtFlag()
    {
        var lookAt = effect?.LookAt ?? false;
        foreach (var keyframe in Keyframes)
            keyframe.UseLookAt = lookAt;
    }

    private void RebuildRows()
    {
        var current = segmentOwner;
        DetachSegmentEditor();

        foreach (var row in rows)
            ReleaseRow(row);

        rows.Clear();
        rowsPanel.Children.Clear();

        if (Keyframes.Count == 0)
        {
            rowsPanel.Children.Add(new TextBlock
            {
                Text = "「＋」でカメラの設定点を追加します",
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(4, 6, 4, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var showSegments = effect?.TimeControl == D3DCameraTimeControl.SegmentBezier;

        for (var i = 0; i < Keyframes.Count; i++)
        {
            AddPointRow(i, Keyframes[i]);

            if (showSegments && i < Keyframes.Count - 1)
                AddSegmentRow(i, Keyframes[i], Keyframes[i + 1]);
        }

        segmentOwner = current is not null && Keyframes.Contains(current) ? current : null;
        selection.RemoveWhere(i => i < 0 || i >= Keyframes.Count);

        if (Keyframes.Count == 0)
            selectedIndex = -1;
        else if (!selection.Contains(selectedIndex))
            selectedIndex = selection.Count > 0 ? selection.Max : 0;

        if (selectedIndex >= 0)
            selection.Add(selectedIndex);

        ShowSegmentEditor();
        AfterSelectionChanged();
    }

    private void AddPointRow(int index, D3DCameraKeyframe keyframe)
    {
        var content = new ContentControl
        {
            Content = keyframe,
            ContentTemplate = (DataTemplate)FindResource("PointRowTemplate"),
            Focusable = false,
            IsTabStop = false,
        };

        var border = new Border
        {
            Child = content,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(3, 1, 2, 1),
            Cursor = Cursors.Hand,
            Tag = index,
            ToolTip = "クリックで選択、ダブルクリックでD3D11 3Dビューポートをこの点へ移動",
        };

        border.MouseLeftButtonDown += PointRow_MouseLeftButtonDown;
        border.MouseEnter += PointRow_MouseEnter;
        border.MouseLeave += PointRow_MouseLeave;

        rowsPanel.Children.Add(border);
        rows.Add(new RowVisual { Index = index, Border = border, Keyframe = keyframe });
    }

    private void AddSegmentRow(int index, D3DCameraKeyframe from, D3DCameraKeyframe to)
    {
        var cell = new D3DCameraSegmentCell(index, from, to)
        {
            CurveGeometry = D3DCameraTimeCurveThumbnail.Create(from.SegmentTimeBezier),
            StrokeBrush = CurveStroke,
        };

        var content = new ContentControl
        {
            Content = cell,
            ContentTemplate = (DataTemplate)FindResource("SegmentRowTemplate"),
            Focusable = false,
            IsTabStop = false,
        };

        var contentBorder = new Border
        {
            Child = content,
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 1, 2, 1),
            Cursor = Cursors.Hand,
            Tag = index,
            ToolTip = "クリックで、この区間の時間カーブを編集します（もう一度クリックで閉じます）",
        };

        contentBorder.MouseLeftButtonDown += SegmentRow_MouseLeftButtonDown;
        contentBorder.MouseEnter += SegmentRow_MouseEnter;
        contentBorder.MouseLeave += SegmentRow_MouseLeave;

        var wrapper = new Border
        {
            Child = contentBorder,
            BorderBrush = SegmentGuide,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Margin = new Thickness(12, 0, 0, 0),
        };

        var row = new RowVisual
        {
            Index = index,
            Border = wrapper,
            Cell = cell,
            Curve = from.SegmentTimeBezier,
        };

        row.CurveChanged = (_, _) => cell.CurveGeometry = D3DCameraTimeCurveThumbnail.Create(from.SegmentTimeBezier);
        from.SegmentTimeBezier.Changed += row.CurveChanged;

        rowsPanel.Children.Add(wrapper);
        rows.Add(row);
    }

    private void ReleaseRow(RowVisual row)
    {
        if (row.Curve is not null && row.CurveChanged is not null)
            row.Curve.Changed -= row.CurveChanged;

        if (row.Cell is null)
        {
            row.Border.MouseLeftButtonDown -= PointRow_MouseLeftButtonDown;
            row.Border.MouseEnter -= PointRow_MouseEnter;
            row.Border.MouseLeave -= PointRow_MouseLeave;
            return;
        }

        if (row.Border.Child is not Border contentBorder)
            return;

        contentBorder.MouseLeftButtonDown -= SegmentRow_MouseLeftButtonDown;
        contentBorder.MouseEnter -= SegmentRow_MouseEnter;
        contentBorder.MouseLeave -= SegmentRow_MouseLeave;
    }

    private void PointRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index })
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            ToggleSelection(index);
        else
            SelectKeyframe(index);

        if (e.ClickCount >= 2)
            FocusSelectedKeyframe();

        e.Handled = true;
    }

    private void PointRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border && border.Tag is int index && index != selectedIndex)
            border.Background = HoverBackground;
    }

    private void PointRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border)
            return;

        border.Background = border.Tag is int index && selection.Contains(index)
            ? SelectedBackground
            : Brushes.Transparent;
    }

    private void SegmentRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int index })
            return;

        if (index < 0 || index >= Keyframes.Count - 1)
            return;

        var owner = Keyframes[index];
        SetSegmentOwner(ReferenceEquals(segmentOwner, owner) ? null : owner);
        e.Handled = true;
    }

    private void SegmentRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = HoverBackground;
    }

    private void SegmentRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = Brushes.Transparent;
    }

    internal void SelectKeyframe(int index)
    {
        selectedIndex = Keyframes.Count == 0
            ? -1
            : Math.Clamp(index < 0 ? 0 : index, 0, Keyframes.Count - 1);

        selection.Clear();
        if (selectedIndex >= 0)
            selection.Add(selectedIndex);

        AfterSelectionChanged();
    }

    internal void PickKeyframe(int index)
    {
        if (index < 0 || index >= Keyframes.Count)
            return;

        if (selection.Count > 1 && selection.Contains(index))
        {
            selectedIndex = index;
            UpdateSelectionVisuals();
            UpdateButtonState();
            UpdateTargetKeyframes();
            if (!_suppressBridgeSync && effect != null)
                D3DCameraPathViewportBridge.SetActiveSelection(effect, selection, selectedIndex);
            return;
        }

        SelectKeyframe(index);
    }

    internal void ToggleSelection(int index)
    {
        if (index < 0 || index >= Keyframes.Count)
            return;

        if (!selection.Remove(index))
        {
            selection.Add(index);
            selectedIndex = index;
        }
        else if (selectedIndex == index)
        {
            selectedIndex = selection.Count > 0 ? selection.Max : -1;
        }

        AfterSelectionChanged();
    }

    private void AfterSelectionChanged()
    {
        UpdateSelectionVisuals();
        UpdateButtonState();
        UpdateTargetKeyframes();
        if (!_suppressBridgeSync && effect != null)
            D3DCameraPathViewportBridge.SetActiveSelection(effect, selection, selectedIndex);
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var row in rows)
        {
            if (row.Cell is not null)
            {
                var editing = ReferenceEquals(row.Cell.From, segmentOwner);
                row.Cell.IsEditing = editing;
                row.Cell.StrokeBrush = editing ? CurveStrokeActive : CurveStroke;
                row.Border.BorderBrush = editing ? SegmentGuideActive : SegmentGuide;
                continue;
            }

            var selected = selection.Contains(row.Index);
            row.Border.Background = selected ? SelectedBackground : Brushes.Transparent;
            row.Border.BorderBrush = selected ? SelectedAccent : Brushes.Transparent;
        }
    }

    private void SetSegmentOwner(D3DCameraKeyframe? owner)
    {
        if (ReferenceEquals(segmentOwner, owner))
            return;

        segmentOwner = owner;
        ShowSegmentEditor();
    }

    private void ShowSegmentEditor()
    {
        DetachSegmentEditor();

        var row = segmentOwner is null
            ? null
            : rows.FirstOrDefault(x => x.Cell is not null && ReferenceEquals(x.Cell.From, segmentOwner));

        if (row?.Cell is null)
        {
            segmentOwner = null;
            segmentPanel.Visibility = Visibility.Collapsed;
            UpdateSelectionVisuals();
            return;
        }

        EnsureSegmentEditor();
        if (segmentEditor is null)
            return;

        BindSegmentEditor(segmentOwner);

        segmentHeader.Text = $"区間 {row.Cell.NumberText} の時間カーブ";
        segmentHost.Children.Add(segmentEditor);
        segmentPanel.Visibility = Visibility.Visible;
        UpdateSelectionVisuals();
    }

    private void DetachSegmentEditor()
    {
        if (segmentEditor is not null)
            segmentHost.Children.Remove(segmentEditor);

        foreach (var row in rows)
        {
            if (row.Cell is null) continue;
            row.Cell.IsEditing = false;
            row.Cell.StrokeBrush = CurveStroke;
        }
    }

    private void CloseSegmentEditor()
    {
        segmentOwner = null;
        DetachSegmentEditor();
        segmentPanel.Visibility = Visibility.Collapsed;
        UpdateSelectionVisuals();
    }

    private void BindSegmentEditor(D3DCameraKeyframe? keyframe)
    {
        if (segmentEditor is null || keyframe is null)
            return;

        if (ReferenceEquals(segmentEditor.Value, keyframe.SegmentTimeBezier))
            return;

        segmentAttribute.ClearBindings(segmentEditor);
        var property = new ItemProperty(
            keyframe,
            keyframe,
            typeof(D3DCameraKeyframe).GetProperty(nameof(D3DCameraKeyframe.SegmentTimeBezier))!,
            null);

        segmentAttribute.SetBindings(segmentEditor, [property]);
    }

    private void EnsureSegmentEditor()
    {
        if (segmentEditor is not null)
            return;

        segmentEditor = (YukkuriMovieMaker.Controls.BezierAnimationEditor)segmentAttribute.Create();
        segmentEditor.BeginEdit += Editor_BeginEdit;
        segmentEditor.EndEdit += Editor_EndEdit;

        if (editorInfo is not null)
            segmentEditor.SetEditorInfo(editorInfo);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (Keyframes.Count == 0)
        {
            BeginEdit?.Invoke(this, EventArgs.Empty);
            Keyframes = Keyframes.Add(new D3DCameraKeyframe(0, 0, 0, 0, 0, 0, 0, D3DCameraPathSolver.DefaultFovDegrees));
            EndEdit?.Invoke(this, EventArgs.Empty);
            SelectKeyframe(0);
            return;
        }

        var index = Math.Max(0, selectedIndex);
        if (index >= Keyframes.Count - 1)
        {
            var last = Keyframes[^1];
            var newFrame = (int)last.Frame.GetValue(0L, 1L, 30) + 30;
            var added = Clone(last, newFrame);

            BeginEdit?.Invoke(this, EventArgs.Empty);
            Keyframes = Keyframes.Add(added);
            EndEdit?.Invoke(this, EventArgs.Empty);

            SelectKeyframe(Keyframes.Count - 1);
            return;
        }

        var previous = Keyframes[index];
        var next = Keyframes[index + 1];
        var previousFrame = (int)previous.Frame.GetValue(0L, 1L, 30);
        var nextFrame = (int)next.Frame.GetValue(0L, 1L, 30);
        var insertedFrame = previousFrame + Math.Max(1, (nextFrame - previousFrame) / 2);
        var inserted = Blend(previous, next, insertedFrame);

        if (effect is not null && effect.CurveType.UsesTangent())
        {
            D3DCameraTangentTools.SetAutoTangent(
                inserted,
                D3DCameraTangentTools.Position(previous),
                D3DCameraTangentTools.Position(next));
        }

        BeginEdit?.Invoke(this, EventArgs.Empty);
        Keyframes = Keyframes.Insert(index + 1, inserted);
        EndEdit?.Invoke(this, EventArgs.Empty);

        SelectKeyframe(index + 1);
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (selection.Count == 0 || Keyframes.Count <= selection.Count)
            return;

        var first = selection.Min;
        var removed = selection.Count;
        var list = Keyframes;

        foreach (var index in selection.Reverse())
            list = list.RemoveAt(index);

        BeginEdit?.Invoke(this, EventArgs.Empty);
        Keyframes = list;
        EndEdit?.Invoke(this, EventArgs.Empty);

        SelectKeyframe(Math.Max(0, first - removed + 1));
    }

    private void DuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (selection.Count == 0)
            return;

        const int gap = 30;
        var list = Keyframes;
        var duplicated = new List<int>();

        foreach (var index in selection.Reverse())
        {
            if (index < 0 || index >= list.Count)
                continue;

            var source = list[index];
            var frame = (int)Math.Round(source.Frame.GetValue(0L, 1L, 30)) + gap;
            list = list.Insert(index + 1, Clone(source, frame));
            duplicated.Add(index + 1);
        }

        BeginEdit?.Invoke(this, EventArgs.Empty);
        Keyframes = list;
        EndEdit?.Invoke(this, EventArgs.Empty);

        SelectKeyframe(duplicated.Count > 0 ? duplicated.Min() : selectedIndex);
    }

    private void ReverseButton_Click(object sender, RoutedEventArgs e)
    {
        if (Keyframes.Count < 2)
            return;

        var list = Keyframes;
        var frames = list.Select(x => x.Frame.GetValue(0L, 1L, 30)).ToList();
        var start = frames.Min();
        var end = frames.Max();

        var builder = ImmutableList.CreateBuilder<D3DCameraKeyframe>();
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var clone = Clone(list[i], (int)Math.Round(start + end - frames[i]));
            ReverseTangent(clone);
            builder.Add(clone);
        }

        BeginEdit?.Invoke(this, EventArgs.Empty);
        Keyframes = builder.ToImmutable();
        EndEdit?.Invoke(this, EventArgs.Empty);

        SelectKeyframe(selectedIndex < 0 ? 0 : Keyframes.Count - 1 - selectedIndex);
    }

    private void LoopButton_Click(object sender, RoutedEventArgs e)
    {
        if (Keyframes.Count < 2)
            return;

        var list = Keyframes;
        var frames = list.Select(x => x.Frame.GetValue(0L, 1L, 30)).ToList();
        var last = frames.Max();
        var builder = list.ToBuilder();

        for (var i = list.Count - 2; i >= 0; i--)
        {
            var clone = Clone(list[i], (int)Math.Round(last + (last - frames[i])));
            ReverseTangent(clone);
            builder.Add(clone);
        }

        BeginEdit?.Invoke(this, EventArgs.Empty);
        Keyframes = builder.ToImmutable();
        EndEdit?.Invoke(this, EventArgs.Empty);

        SelectKeyframe(Keyframes.Count - 1);
    }

    private static void ReverseTangent(D3DCameraKeyframe keyframe)
    {
        var azimuth = keyframe.TangentAngleH.GetValue(0L, 1L, 30);
        var elevation = keyframe.TangentAngleV.GetValue(0L, 1L, 30);
        var lengthIn = keyframe.TangentLengthIn.GetValue(0L, 1L, 30);
        var lengthOut = keyframe.TangentLengthOut.GetValue(0L, 1L, 30);

        keyframe.TangentAngleH.ActiveValues.First().Value = azimuth + 180.0;
        keyframe.TangentAngleV.ActiveValues.First().Value = -elevation;
        keyframe.TangentLengthIn.ActiveValues.First().Value = lengthOut;
        keyframe.TangentLengthOut.ActiveValues.First().Value = lengthIn;
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        var index = selectedIndex;
        if (index <= 0)
            return;

        BeginEdit?.Invoke(this, EventArgs.Empty);
        var keyframe = Keyframes[index];
        Keyframes = Keyframes.RemoveAt(index).Insert(index - 1, keyframe);
        EndEdit?.Invoke(this, EventArgs.Empty);

        SelectKeyframe(index - 1);
    }

    private void DownButton_Click(object sender, RoutedEventArgs e)
    {
        var index = selectedIndex;
        if (index < 0 || index >= Keyframes.Count - 1)
            return;

        BeginEdit?.Invoke(this, EventArgs.Empty);
        var keyframe = Keyframes[index];
        Keyframes = Keyframes.RemoveAt(index).Insert(index + 1, keyframe);
        EndEdit?.Invoke(this, EventArgs.Empty);

        SelectKeyframe(index + 1);
    }

    internal static D3DCameraKeyframe Clone(D3DCameraKeyframe source, int frame)
    {
        var clone = new D3DCameraKeyframe();
        clone.Frame.CopyFrom(source.Frame);
        clone.X.CopyFrom(source.X);
        clone.Y.CopyFrom(source.Y);
        clone.Z.CopyFrom(source.Z);
        clone.RotationX.CopyFrom(source.RotationX);
        clone.RotationY.CopyFrom(source.RotationY);
        clone.RotationZ.CopyFrom(source.RotationZ);
        clone.Fov.CopyFrom(source.Fov);
        clone.TangentAngleH.CopyFrom(source.TangentAngleH);
        clone.TangentAngleV.CopyFrom(source.TangentAngleV);
        clone.TangentLengthIn.CopyFrom(source.TangentLengthIn);
        clone.TangentLengthOut.CopyFrom(source.TangentLengthOut);
        clone.TargetX.CopyFrom(source.TargetX);
        clone.TargetY.CopyFrom(source.TargetY);
        clone.TargetZ.CopyFrom(source.TargetZ);
        clone.SegmentTimeBezier.CopyFrom(source.SegmentTimeBezier);
        clone.Frame.ActiveValues.First().Value = frame;
        clone.UseLookAt = source.UseLookAt;
        return clone;
    }

    private static D3DCameraKeyframe Blend(D3DCameraKeyframe a, D3DCameraKeyframe b, int frame)
    {
        var blended = new D3DCameraKeyframe();
        static double Average(Animation first, Animation second)
            => (first.GetValue(0L, 1L, 30) + second.GetValue(0L, 1L, 30)) / 2.0;

        blended.X.ActiveValues.First().Value = Average(a.X, b.X);
        blended.Y.ActiveValues.First().Value = Average(a.Y, b.Y);
        blended.Z.ActiveValues.First().Value = Average(a.Z, b.Z);
        blended.RotationX.ActiveValues.First().Value = Average(a.RotationX, b.RotationX);
        blended.RotationY.ActiveValues.First().Value = Average(a.RotationY, b.RotationY);
        blended.RotationZ.ActiveValues.First().Value = Average(a.RotationZ, b.RotationZ);
        blended.Fov.ActiveValues.First().Value = Average(a.Fov, b.Fov);
        blended.TargetX.ActiveValues.First().Value = Average(a.TargetX, b.TargetX);
        blended.TargetY.ActiveValues.First().Value = Average(a.TargetY, b.TargetY);
        blended.TargetZ.ActiveValues.First().Value = Average(a.TargetZ, b.TargetZ);
        blended.Frame.ActiveValues.First().Value = frame;
        blended.UseLookAt = a.UseLookAt;
        return blended;
    }

    private void ListResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!double.IsFinite(e.VerticalChange) || e.VerticalChange == 0.0)
            return;
        var current = double.IsNaN(listRow.Height.Value) ? 150.0 : listRow.Height.Value;
        listRow.Height = new GridLength(Math.Clamp(current + e.VerticalChange, listRow.MinHeight, listRow.MaxHeight));
    }

    private void AddCurrentViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (effect != null)
            D3DCameraPathViewportBridge.RequestAddCurrentViewportCamera(effect);
    }

    private void FocusPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (effect != null)
            D3DCameraPathViewportBridge.RequestFocusPath(effect);
    }

    private void FocusSelectedButton_Click(object sender, RoutedEventArgs e) => FocusSelectedKeyframe();

    internal void FocusSelectedKeyframe()
    {
        if (effect != null && selectedIndex >= 0)
            D3DCameraPathViewportBridge.RequestFocusKeyframe(effect, selectedIndex);
    }

    private void UpdateTargetKeyframes()
    {
        if (properties is null || properties.Length == 0)
            return;

        var targets = new List<D3DCameraKeyframe>();
        foreach (var property in properties)
        {
            var list = property.GetValue<ImmutableList<D3DCameraKeyframe>>();
            if (list is not null && selectedIndex >= 0 && selectedIndex < list.Count)
                targets.Add(list[selectedIndex]);
        }
        TargetKeyframes = targets;
    }

    private void UpdateButtonState()
    {
        var index = selectedIndex;
        var count = selection.Count;

        removeButton.IsEnabled = count > 0 && Keyframes.Count > count;
        upButton.IsEnabled = count == 1 && index > 0;
        downButton.IsEnabled = count == 1 && index >= 0 && index < Keyframes.Count - 1;
        duplicateButton.IsEnabled = count > 0;
        reverseButton.IsEnabled = Keyframes.Count >= 2;
        loopButton.IsEnabled = Keyframes.Count >= 2;
        focusSelectedButton.IsEnabled = index >= 0 && index < Keyframes.Count;
        focusPathButton.IsEnabled = Keyframes.Count > 0;
        addCurrentViewButton.IsEnabled = effect != null;
    }

    private void Editor_BeginEdit(object? sender, EventArgs e) => BeginEdit?.Invoke(this, EventArgs.Empty);
    private void Editor_EndEdit(object? sender, EventArgs e) => EndEdit?.Invoke(this, EventArgs.Empty);

    public void SetEditorInfo(IEditorInfo? info)
    {
        editorInfo = info;
        pointEditor.SetEditorInfo(info);
        segmentEditor?.SetEditorInfo(info);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal sealed class RowVisual
    {
        public int Index { get; init; }
        public Border Border { get; init; } = null!;
        public D3DCameraKeyframe? Keyframe { get; init; }
        public D3DCameraSegmentCell? Cell { get; init; }
        public BezierAnimation? Curve { get; init; }
        public EventHandler? CurveChanged { get; set; }
    }
}
