using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Iyahon_D3D11Renderer_Core.Model3D;
using Microsoft.Win32;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Viewport3D.Modifiers;

public partial class ModifierPanelControl : UserControl
{
    private Viewport3DViewModel? _vm;
    private readonly ContextMenu _addMenu;

    public ModifierPanelControl()
    {
        InitializeComponent();
        _addMenu = BuildAddModifierMenu();
    }

    public void BindViewModel(Viewport3DViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null)
            _vm.ModifiersChanged -= OnModifiersChanged;

        _vm = vm;
        if (_vm != null)
            _vm.ModifiersChanged += OnModifiersChanged;

        RefreshFromViewModel();
    }

    private void OnModifiersChanged() => RefreshFromViewModel();

    public void RefreshFromViewModel()
    {
        ModifiersListPanel.Children.Clear();

        if (_vm == null || !_vm.HasModifierTarget)
        {
            TargetLabel.Text = "未選択";
            BtnAddModifier.IsEnabled = false;
            EmptyHintText.Text = "タイムラインまたはビューポートで D3D11 3Dモデル / 図形を選択してください";
            EmptyHintText.Visibility = Visibility.Visible;
            return;
        }

        TargetLabel.Text = _vm.ModifierTargetName;
        BtnAddModifier.IsEnabled = true;

        var stack = _vm.GetCurrentModifierStack();
        if (stack.Items.Count == 0)
        {
            EmptyHintText.Text = "「+ モディファイアを追加」からミラー・ベベル・ブーリアンなどを非破壊で追加できます";
            EmptyHintText.Visibility = Visibility.Visible;
            return;
        }

        EmptyHintText.Visibility = Visibility.Collapsed;
        for (int i = 0; i < stack.Items.Count; i++)
        {
            ModifiersListPanel.Children.Add(BuildModifierCard(stack.Items[i], i, stack.Items.Count));
        }
    }

    private void OnAddModifierClicked(object sender, RoutedEventArgs e)
    {
        if (_vm == null || !_vm.HasModifierTarget) return;
        _addMenu.PlacementTarget = BtnAddModifier;
        _addMenu.Placement = PlacementMode.Bottom;
        _addMenu.IsOpen = true;
    }

    private ContextMenu BuildAddModifierMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ModDarkContextMenu") };

        void AddCatHeader(string label)
        {
            menu.Items.Add(new MenuItem
            {
                Style = (Style)FindResource("ModDarkMenuItem"),
                Header = $"── {label} ──",
                IsEnabled = false
            });
        }

        void AddItem(string label, MeshModifierType type)
        {
            var mi = new MenuItem
            {
                Style = (Style)FindResource("ModDarkMenuItem"),
                Header = label
            };
            mi.Click += (_, _) => _vm?.AddModifier(type);
            menu.Items.Add(mi);
        }

        AddCatHeader("生成 (Generate)");
        AddItem("ミラー (Mirror)", MeshModifierType.Mirror);
        AddItem("ソリッド化 (Solidify)", MeshModifierType.Solidify);
        AddItem("ブーリアン (Boolean)", MeshModifierType.Boolean);
        AddItem("配列 (Array)", MeshModifierType.Array);
        AddItem("三角形化 (Triangulate)", MeshModifierType.Triangulate);
        AddItem("ベベル (Bevel)", MeshModifierType.Bevel);
        AddItem("細分化 (シンプル)", MeshModifierType.SubdivisionSimple);
        AddItem("細分化 (曲面 Catmull-Clark)", MeshModifierType.SubdivisionCatmullClark);

        menu.Items.Add(new Separator { Style = (Style)FindResource("ModDarkMenuSeparator") });
        AddCatHeader("変形 (Deform)");
        AddItem("シュリンクラップ (Shrinkwrap)", MeshModifierType.Shrinkwrap);
        AddItem("ディスプレイス (Displace)", MeshModifierType.Displace);
        return menu;
    }

    private UIElement BuildModifierCard(MeshModifierItem mod, int index, int totalCount)
    {
        var cardBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(37, 41, 48)),
            BorderBrush = new SolidColorBrush(mod.Enabled ? Color.FromRgb(64, 74, 92) : Color.FromRgb(48, 52, 60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 6)
        };

        var outerStack = new StackPanel();

        // ── ヘッダー行: [▸/▾] 名前  [ON/OFF] [▲] [▼] [適用] [×] ──
        var hdrBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 50, 59)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(56, 63, 76)),
            BorderThickness = new Thickness(0, 0, 0, mod.Expanded ? 1 : 0),
            CornerRadius = mod.Expanded ? new CornerRadius(3, 3, 0, 0) : new CornerRadius(3),
            Padding = new Thickness(4, 3.5, 4, 3.5)
        };

        var hdrDock = new DockPanel { LastChildFill = true };

        var actionPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(actionPanel, Dock.Right);

        var visBtn = new Button
        {
            Style = (Style)FindResource("ModSmallBtn"),
            Content = mod.Enabled ? "ON" : "OFF",
            MinWidth = 28,
            Height = 19,
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(0, 0, 2, 0),
            Background = new SolidColorBrush(mod.Enabled ? Color.FromRgb(37, 99, 235) : Color.FromRgb(32, 36, 44)),
            BorderBrush = new SolidColorBrush(mod.Enabled ? Color.FromRgb(96, 165, 250) : Color.FromRgb(60, 67, 79)),
            Foreground = new SolidColorBrush(mod.Enabled ? Colors.White : Color.FromRgb(138, 148, 166)),
            FontSize = 9.5,
            ToolTip = "表示 ON / OFF（ビフォーアフター比較）"
        };
        visBtn.Click += (_, _) => _vm?.ToggleModifier(mod.Id);
        actionPanel.Children.Add(visBtn);

        var upBtn = new Button
        {
            Style = (Style)FindResource("ModSmallBtn"),
            Content = "▲",
            Width = 19,
            Height = 19,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 1, 0),
            FontSize = 8.5,
            IsEnabled = index > 0,
            ToolTip = "処理順番を1つ上へ"
        };
        upBtn.Click += (_, _) => _vm?.MoveModifier(mod.Id, -1);
        actionPanel.Children.Add(upBtn);

        var downBtn = new Button
        {
            Style = (Style)FindResource("ModSmallBtn"),
            Content = "▼",
            Width = 19,
            Height = 19,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 2, 0),
            FontSize = 8.5,
            IsEnabled = index + 1 < totalCount,
            ToolTip = "処理順番を1つ下へ"
        };
        downBtn.Click += (_, _) => _vm?.MoveModifier(mod.Id, +1);
        actionPanel.Children.Add(downBtn);

        var applyBtn = new Button
        {
            Style = (Style)FindResource("ModSmallBtn"),
            Content = "適用",
            Height = 19,
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(0, 0, 2, 0),
            Background = new SolidColorBrush(Color.FromRgb(30, 82, 54)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153)),
            Foreground = new SolidColorBrush(Color.FromRgb(220, 252, 231)),
            FontSize = 9.5,
            ToolTip = "適用 (Apply) — 実メッシュへ焼き込み確定"
        };
        applyBtn.Click += (_, _) => _vm?.ApplyModifier(mod.Id);
        actionPanel.Children.Add(applyBtn);

        var delBtn = new Button
        {
            Style = (Style)FindResource("ModSmallBtn"),
            Content = "×",
            Width = 19,
            Height = 19,
            Padding = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            ToolTip = "このモディファイアを削除"
        };
        delBtn.Click += (_, _) => _vm?.RemoveModifier(mod.Id);
        actionPanel.Children.Add(delBtn);

        hdrDock.Children.Add(actionPanel);

        var titleBtn = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 4, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            ToolTip = "折りたたみ / 展開"
        };
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = mod.Expanded ? "▾ " : "▸ ",
            Foreground = new SolidColorBrush(Color.FromRgb(156, 165, 180)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(mod.Name) ? MeshModifierItem.GetTypeDisplayName(mod.Type) : mod.Name,
            Foreground = new SolidColorBrush(mod.Enabled ? Color.FromRgb(241, 245, 249) : Color.FromRgb(138, 148, 166)),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleBtn.Content = titleStack;
        titleBtn.Click += (_, _) => _vm?.ToggleModifierExpanded(mod.Id);
        hdrDock.Children.Add(titleBtn);

        hdrBorder.Child = hdrDock;
        outerStack.Children.Add(hdrBorder);

        if (mod.Expanded)
        {
            var body = new StackPanel { Margin = new Thickness(8, 6, 8, 7) };
            BuildModifierBody(body, mod);
            outerStack.Children.Add(body);
        }

        cardBorder.Child = outerStack;
        return cardBorder;
    }

    private void BuildModifierBody(StackPanel body, MeshModifierItem mod)
    {
        bool bl = _vm?.UseBlenderAxis == true;
        switch (mod.Type)
        {
            case MeshModifierType.Mirror:
                if (bl)
                {
                    AddCheckRow(body, "ミラー軸", new[]
                    {
                        ("X", mod.MirrorX, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorX = v)),
                        ("Y", mod.MirrorZ, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorZ = v)),
                        ("Z", mod.MirrorY, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorY = v)),
                    }, mod.Id, rebuildOnToggle: false);
                    AddCheckRow(body, "二等分", new[]
                    {
                        ("X", mod.MirrorBisectX, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorBisectX = v)),
                        ("Y", mod.MirrorBisectZ, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorBisectZ = v)),
                        ("Z", mod.MirrorBisectY, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorBisectY = v)),
                    }, mod.Id, rebuildOnToggle: false);
                    AddCheckRow(body, "反転側", new[]
                    {
                        ("X", mod.MirrorFlipX, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorFlipX = v)),
                        ("Y", !mod.MirrorFlipZ, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorFlipZ = !v)),
                        ("Z", mod.MirrorFlipY, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorFlipY = v)),
                    }, mod.Id, rebuildOnToggle: false);
                }
                else
                {
                    AddCheckRow(body, "ミラー軸", new[]
                    {
                        ("X", mod.MirrorX, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorX = v)),
                        ("Y", mod.MirrorY, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorY = v)),
                        ("Z", mod.MirrorZ, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorZ = v)),
                    }, mod.Id, rebuildOnToggle: false);
                    AddCheckRow(body, "二等分", new[]
                    {
                        ("X", mod.MirrorBisectX, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorBisectX = v)),
                        ("Y", mod.MirrorBisectY, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorBisectY = v)),
                        ("Z", mod.MirrorBisectZ, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorBisectZ = v)),
                    }, mod.Id, rebuildOnToggle: false);
                    AddCheckRow(body, "反転側", new[]
                    {
                        ("X", mod.MirrorFlipX, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorFlipX = v)),
                        ("Y", mod.MirrorFlipY, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorFlipY = v)),
                        ("Z", mod.MirrorFlipZ, (Action<MeshModifierItem, bool>)((m, v) => m.MirrorFlipZ = v)),
                    }, mod.Id, rebuildOnToggle: false);
                }
                AddSingleCheck(body, "結合 (Merge)", mod.MirrorMerge, mod.Id, (m, v) => m.MirrorMerge = v);
                AddSingleCheck(body, "クリッピング", mod.MirrorClip, mod.Id, (m, v) => m.MirrorClip = v);
                AddFloatSlider(body, "結合距離", mod.MirrorMergeThreshold, 0.0001, 0.05, "0.0000", mod.Id, "mth",
                    (m, v) => m.MirrorMergeThreshold = v, hardMin: 0.0, hardMax: 100000.0);
                break;

            case MeshModifierType.Solidify:
                AddFloatSlider(body, "幅 (厚み)", mod.SolidifyThickness, -1.0, 1.0, "0.000", mod.Id, "solT",
                    (m, v) => m.SolidifyThickness = v, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "オフセット", mod.SolidifyOffset, -1.0, 1.0, "0.00", mod.Id, "solO",
                    (m, v) => m.SolidifyOffset = v, hardMin: -100000.0, hardMax: 100000.0);
                AddSingleCheck(body, "リム(側面)を充填", mod.SolidifyFillRim, mod.Id, (m, v) => m.SolidifyFillRim = v);
                AddSingleCheck(body, "リムのみ生成", mod.SolidifyOnlyRim, mod.Id, (m, v) => m.SolidifyOnlyRim = v);
                AddSingleCheck(body, "均等な厚み", mod.SolidifyEvenThickness, mod.Id, (m, v) => m.SolidifyEvenThickness = v);
                AddSingleCheck(body, "法線を反転", mod.SolidifyFlipNormals, mod.Id, (m, v) => m.SolidifyFlipNormals = v);
                AddIntSlider(body, "材質オフセット", mod.SolidifyMaterialOffset, -8, 8, mod.Id, "solMO",
                    (m, v) => m.SolidifyMaterialOffset = v, hardMin: -1000, hardMax: 1000);
                AddIntSlider(body, "リム材質差", mod.SolidifyRimMaterialOffset, -8, 8, mod.Id, "solRMO",
                    (m, v) => m.SolidifyRimMaterialOffset = v, hardMin: -1000, hardMax: 1000);
                var outlineBtn = new Button
                {
                    Style = (Style)FindResource("ModSmallBtn"),
                    Content = "背面法アウトライン設定",
                    Margin = new Thickness(0, 4, 0, 1),
                    Height = 23,
                    ToolTip = "黒・片面(CullBackface)・影なし用マテリアルを作成し、法線反転＋材質オフセットを設定します"
                };
                int solidId = mod.Id;
                outlineBtn.Click += (_, _) => _vm?.SetupSolidifyBackfaceOutline(solidId);
                body.Children.Add(outlineBtn);
                break;

            case MeshModifierType.Triangulate:
                AddEnumCombo(body, "四角形分割", mod.TriangulateQuadMethod, new[]
                {
                    (TriangulateQuadMethod.Beauty, "ビューティー (Beauty)"),
                    (TriangulateQuadMethod.Fixed, "固定 (0-2)"),
                    (TriangulateQuadMethod.FixedAlternate, "交互 (1-3)"),
                    (TriangulateQuadMethod.ShortestDiagonal, "最短対角線"),
                    (TriangulateQuadMethod.LongestDiagonal, "最長対角線"),
                }, mod.Id, (m, v) => m.TriangulateQuadMethod = v, rebuild: false);
                AddEnumCombo(body, "多角形(Nゴン)", mod.TriangulateNgonMethod, new[]
                {
                    (TriangulateNgonMethod.Beauty, "ビューティー (Beauty)"),
                    (TriangulateNgonMethod.Clip, "イヤーカット (Clip)"),
                }, mod.Id, (m, v) => m.TriangulateNgonMethod = v, rebuild: false);
                AddIntSlider(body, "最小頂点数", mod.TriangulateMinVertices, 3, 16, mod.Id, "triMin",
                    (m, v) => m.TriangulateMinVertices = v);
                AddSingleCheck(body, "法線を保持", mod.TriangulateKeepNormals, mod.Id, (m, v) => m.TriangulateKeepNormals = v);
                break;

            case MeshModifierType.Boolean:
                AddEnumCombo(body, "演算", mod.BooleanOperation, new[]
                {
                    (BooleanOperationKind.Difference, "差分 (Difference)"),
                    (BooleanOperationKind.Union, "統合 (Union)"),
                    (BooleanOperationKind.Intersect, "交差 (Intersect)"),
                }, mod.Id, (m, val) => m.BooleanOperation = val, rebuild: false);
                BuildTargetSelectorSection(body, mod);
                break;

            case MeshModifierType.Array:
                AddIntSlider(body, "配列数", mod.ArrayCount, 1, 32, mod.Id, "arrN", (m, v) => m.ArrayCount = v, hardMin: 1, hardMax: 200);
                AddSingleCheck(body, "相対オフセット", mod.ArrayUseRelativeOffset, mod.Id, (m, v) => m.ArrayUseRelativeOffset = v);
                AddFloatSlider(body, "相対 X", mod.ArrayRelX, -3.0, 3.0, "0.00", mod.Id, "arx", (m, v) => m.ArrayRelX = v, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "相対 Y", bl ? -mod.ArrayRelZ : mod.ArrayRelY, -3.0, 3.0, "0.00", mod.Id, "ary",
                    (m, v) => { if (bl) m.ArrayRelZ = -v; else m.ArrayRelY = v; }, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "相対 Z", bl ? mod.ArrayRelY : mod.ArrayRelZ, -3.0, 3.0, "0.00", mod.Id, "arz",
                    (m, v) => { if (bl) m.ArrayRelY = v; else m.ArrayRelZ = v; }, hardMin: -100000.0, hardMax: 100000.0);
                AddSingleCheck(body, "固定オフセット", mod.ArrayUseConstantOffset, mod.Id, (m, v) => m.ArrayUseConstantOffset = v);
                AddFloatSlider(body, "固定 X", mod.ArrayConstX, -2.0, 2.0, "0.00", mod.Id, "acx", (m, v) => m.ArrayConstX = v, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "固定 Y", bl ? -mod.ArrayConstZ : mod.ArrayConstY, -2.0, 2.0, "0.00", mod.Id, "acy",
                    (m, v) => { if (bl) m.ArrayConstZ = -v; else m.ArrayConstY = v; }, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "固定 Z", bl ? mod.ArrayConstY : mod.ArrayConstZ, -2.0, 2.0, "0.00", mod.Id, "acz",
                    (m, v) => { if (bl) m.ArrayConstY = v; else m.ArrayConstZ = v; }, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "回転 X", mod.ArrayRotX, -180.0, 180.0, "0.0", mod.Id, "arotx", (m, v) => m.ArrayRotX = v, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "回転 Y", bl ? -mod.ArrayRotZ : mod.ArrayRotY, -180.0, 180.0, "0.0", mod.Id, "aroty",
                    (m, v) => { if (bl) m.ArrayRotZ = -v; else m.ArrayRotY = v; }, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "回転 Z", bl ? mod.ArrayRotY : mod.ArrayRotZ, -180.0, 180.0, "0.0", mod.Id, "arotz",
                    (m, v) => { if (bl) m.ArrayRotY = v; else m.ArrayRotZ = v; }, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "段スケール", mod.ArrayScale, 0.25, 2.0, "0.00", mod.Id, "asc", (m, v) => m.ArrayScale = v, hardMin: 0.0001, hardMax: 100000.0);
                AddSingleCheck(body, "近接頂点を結合", mod.ArrayMerge, mod.Id, (m, v) => m.ArrayMerge = v);
                break;

            case MeshModifierType.Bevel:
                AddFloatSlider(body, "量 (幅)", mod.BevelAmount, 0.001, 0.5, "0.000", mod.Id, "bevA", (m, v) => m.BevelAmount = v, hardMin: 0.0, hardMax: 100000.0);
                AddIntSlider(body, "セグメント", mod.BevelSegments, 1, 8, mod.Id, "bevS", (m, v) => m.BevelSegments = v);
                AddFloatSlider(body, "角度制限°", mod.BevelAngleLimitDeg, 0.0, 180.0, "0", mod.Id, "bevAng", (m, v) => m.BevelAngleLimitDeg = v);
                AddFloatSlider(body, "形状丸み", mod.BevelProfile, 0.05, 1.0, "0.00", mod.Id, "bevP", (m, v) => m.BevelProfile = v);
                AddSingleCheck(body, "重なりクランプ", mod.BevelClampOverlap, mod.Id, (m, v) => m.BevelClampOverlap = v);
                AddSingleCheck(body, "ベベル面スムーズ", mod.BevelSmooth, mod.Id, (m, v) => m.BevelSmooth = v);
                break;

            case MeshModifierType.Shrinkwrap:
                AddEnumCombo(body, "ラップ方式", mod.ShrinkwrapMode, new[]
                {
                    (ShrinkwrapModeKind.NearestSurfacePoint, "最近接表面点"),
                    (ShrinkwrapModeKind.ProjectNormal, "法線投影"),
                    (ShrinkwrapModeKind.NearestVertex, "最近接頂点"),
                    (ShrinkwrapModeKind.ProjectAxis, "指定軸投影"),
                }, mod.Id, (m, v) => m.ShrinkwrapMode = v, rebuild: true);
                AddEnumCombo(body, "スナップ", mod.ShrinkwrapSnap, new[]
                {
                    (ShrinkwrapSnapKind.OnSurface, "表面上"),
                    (ShrinkwrapSnapKind.OutsideSurface, "表面外側"),
                    (ShrinkwrapSnapKind.Outside, "外側に保つ"),
                    (ShrinkwrapSnapKind.Inside, "内側に保つ"),
                }, mod.Id, (m, v) => m.ShrinkwrapSnap = v, rebuild: false);
                if (mod.ShrinkwrapMode == ShrinkwrapModeKind.ProjectAxis)
                {
                    AddEnumCombo(body, "投影軸", mod.ShrinkwrapAxis, bl
                        ? new[]
                        {
                            (ModifierAxisKind.X, "+X 軸"),
                            (ModifierAxisKind.NegZ, "+Y 軸"),
                            (ModifierAxisKind.Y, "+Z 軸"),
                            (ModifierAxisKind.NegX, "-X 軸"),
                            (ModifierAxisKind.Z, "-Y 軸"),
                            (ModifierAxisKind.NegY, "-Z 軸"),
                        }
                        : new[]
                        {
                            (ModifierAxisKind.X, "+X 軸"),
                            (ModifierAxisKind.Y, "+Y 軸"),
                            (ModifierAxisKind.Z, "+Z 軸"),
                            (ModifierAxisKind.NegX, "-X 軸"),
                            (ModifierAxisKind.NegY, "-Y 軸"),
                            (ModifierAxisKind.NegZ, "-Z 軸"),
                        }, mod.Id, (m, v) => m.ShrinkwrapAxis = v, rebuild: false);
                }
                AddFloatSlider(body, "オフセット", mod.ShrinkwrapOffset, -0.5, 0.5, "0.000", mod.Id, "swO", (m, v) => m.ShrinkwrapOffset = v, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "影響度", mod.ShrinkwrapMix, 0.0, 1.0, "0.00", mod.Id, "swM", (m, v) => m.ShrinkwrapMix = v);
                BuildTargetSelectorSection(body, mod);
                break;

            case MeshModifierType.SubdivisionSimple:
                AddIntSlider(body, "細分化レベル", mod.SubdivLevels, 1, 4, mod.Id, "subL", (m, v) => m.SubdivLevels = v);
                break;

            case MeshModifierType.SubdivisionCatmullClark:
                AddIntSlider(body, "細分化レベル", mod.SubdivLevels, 1, 4, mod.Id, "subCL", (m, v) => m.SubdivLevels = v);
                AddSingleCheck(body, "スムーズシェード", mod.SubdivSmoothShading, mod.Id, (m, v) => m.SubdivSmoothShading = v);
                AddSingleCheck(body, "境界の角を維持", mod.SubdivKeepCorners, mod.Id, (m, v) => m.SubdivKeepCorners = v);
                break;

            case MeshModifierType.Displace:
                AddEnumCombo(body, "模様/画像", mod.DisplaceTexture, new[]
                {
                    (DisplaceTextureKind.Noise, "ノイズ (Noise)"),
                    (DisplaceTextureKind.Voronoi, "ボロノイ (Voronoi)"),
                    (DisplaceTextureKind.Wave, "ウェーブ (Wave)"),
                    (DisplaceTextureKind.Checker, "チェッカー (Checker)"),
                    (DisplaceTextureKind.Image, "画像ファイル"),
                }, mod.Id, (m, v) => m.DisplaceTexture = v, rebuild: true);

                if (mod.DisplaceTexture == DisplaceTextureKind.Image)
                {
                    BuildImagePickerRow(body, mod);
                }

                AddEnumCombo(body, "方向", mod.DisplaceDirection, bl
                    ? new[]
                    {
                        (DisplaceDirectionKind.Normal, "法線 (Normal)"),
                        (DisplaceDirectionKind.X, "X 軸"),
                        (DisplaceDirectionKind.Z, "Y 軸"),
                        (DisplaceDirectionKind.Y, "Z 軸"),
                    }
                    : new[]
                    {
                        (DisplaceDirectionKind.Normal, "法線 (Normal)"),
                        (DisplaceDirectionKind.X, "X 軸"),
                        (DisplaceDirectionKind.Y, "Y 軸"),
                        (DisplaceDirectionKind.Z, "Z 軸"),
                    }, mod.Id, (m, v) => m.DisplaceDirection = v, rebuild: false);

                AddEnumCombo(body, "座標", mod.DisplaceCoord, new[]
                {
                    (DisplaceCoordKind.Object, "ローカル座標"),
                    (DisplaceCoordKind.UV, "UV 座標"),
                }, mod.Id, (m, v) => m.DisplaceCoord = v, rebuild: false);

                AddFloatSlider(body, "強さ", mod.DisplaceStrength, -1.0, 1.0, "0.000", mod.Id, "dpS", (m, v) => m.DisplaceStrength = v, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "中間レベル", mod.DisplaceMidlevel, 0.0, 1.0, "0.00", mod.Id, "dpMid", (m, v) => m.DisplaceMidlevel = v, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "スケール", mod.DisplaceScale, 0.1, 20.0, "0.00", mod.Id, "dpSc", (m, v) => m.DisplaceScale = v, hardMin: 0.01, hardMax: 100000.0);
                AddFloatSlider(body, "細かさ", mod.DisplaceDetail, 0.0, 6.0, "0.0", mod.Id, "dpDet", (m, v) => m.DisplaceDetail = v, hardMin: 0.0, hardMax: 8.0);
                AddFloatSlider(body, "位置 X", mod.DisplaceOffsetX, -5.0, 5.0, "0.00", mod.Id, "dpOx", (m, v) => m.DisplaceOffsetX = v, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "位置 Y", bl ? -mod.DisplaceOffsetZ : mod.DisplaceOffsetY, -5.0, 5.0, "0.00", mod.Id, "dpOy",
                    (m, v) => { if (bl) m.DisplaceOffsetZ = -v; else m.DisplaceOffsetY = v; }, hardMin: -100000.0, hardMax: 100000.0);
                AddFloatSlider(body, "位置 Z", bl ? mod.DisplaceOffsetY : mod.DisplaceOffsetZ, -5.0, 5.0, "0.00", mod.Id, "dpOz",
                    (m, v) => { if (bl) m.DisplaceOffsetY = v; else m.DisplaceOffsetZ = v; }, hardMin: -100000.0, hardMax: 100000.0);
                break;
        }
    }

    private void BuildTargetSelectorSection(StackPanel body, MeshModifierItem mod)
    {
        AddEnumCombo(body, "対象タイプ", mod.TargetSource, new[]
        {
            (ModifierTargetSourceKind.BuiltInPrimitive, "内蔵プリミティブ"),
            (ModifierTargetSourceKind.SceneLayer, "別レイヤーの3D"),
        }, mod.Id, (m, v) => m.TargetSource = v, rebuild: true);

        if (mod.TargetSource == ModifierTargetSourceKind.SceneLayer)
        {
            var targets = _vm?.GetAvailableSceneModifierTargets() ?? new List<(int Layer, string Label)>();
            var options = new List<(int Layer, string Label)>(targets.Count + 1);
            bool foundCur = false;
            foreach (var t in targets)
            {
                options.Add(t);
                if (t.Layer == mod.TargetLayer) foundCur = true;
            }
            if (!foundCur)
                options.Insert(0, (mod.TargetLayer, $"レイヤー {mod.TargetLayer} (未検出)"));

            AddEnumCombo(body, "対象レイヤー", mod.TargetLayer, options, mod.Id, (m, v) => m.TargetLayer = v, rebuild: false);
        }
        else
        {
            AddEnumCombo(body, "プリミティブ", mod.TargetPrimitive, new[]
            {
                (D3DPrimitiveShapeKind.Cube, "立方体 (Cube)"),
                (D3DPrimitiveShapeKind.UvSphere, "UV球 (Sphere)"),
                (D3DPrimitiveShapeKind.IcoSphere, "ICO球"),
                (D3DPrimitiveShapeKind.Cylinder, "円柱 (Cylinder)"),
                (D3DPrimitiveShapeKind.Cone, "円錐 (Cone)"),
                (D3DPrimitiveShapeKind.Torus, "トーラス (Torus)"),
                (D3DPrimitiveShapeKind.Circle, "円 (Circle)"),
            }, mod.Id, (m, v) => m.TargetPrimitive = v, rebuild: false);

            bool bl = _vm?.UseBlenderAxis == true;
            AddFloatSlider(body, "位置 X", mod.TargetPosX, -2.0, 2.0, "0.00", mod.Id, "tpX", (m, v) => m.TargetPosX = v, hardMin: -100000.0, hardMax: 100000.0);
            AddFloatSlider(body, "位置 Y", bl ? -mod.TargetPosZ : mod.TargetPosY, -2.0, 2.0, "0.00", mod.Id, "tpY",
                (m, v) => { if (bl) m.TargetPosZ = -v; else m.TargetPosY = v; }, hardMin: -100000.0, hardMax: 100000.0);
            AddFloatSlider(body, "位置 Z", bl ? mod.TargetPosY : mod.TargetPosZ, -2.0, 2.0, "0.00", mod.Id, "tpZ",
                (m, v) => { if (bl) m.TargetPosY = v; else m.TargetPosZ = v; }, hardMin: -100000.0, hardMax: 100000.0);
            AddFloatSlider(body, "回転 X", mod.TargetRotX, -180.0, 180.0, "0", mod.Id, "trX", (m, v) => m.TargetRotX = v, hardMin: -100000.0, hardMax: 100000.0);
            AddFloatSlider(body, "回転 Y", bl ? -mod.TargetRotZ : mod.TargetRotY, -180.0, 180.0, "0", mod.Id, "trY",
                (m, v) => { if (bl) m.TargetRotZ = -v; else m.TargetRotY = v; }, hardMin: -100000.0, hardMax: 100000.0);
            AddFloatSlider(body, "回転 Z", bl ? mod.TargetRotY : mod.TargetRotZ, -180.0, 180.0, "0", mod.Id, "trZ",
                (m, v) => { if (bl) m.TargetRotY = v; else m.TargetRotZ = v; }, hardMin: -100000.0, hardMax: 100000.0);
            AddFloatSlider(body, "サイズ X", mod.TargetScaleX, 0.05, 3.0, "0.00", mod.Id, "tsX", (m, v) => m.TargetScaleX = v, hardMin: 0.0001, hardMax: 100000.0);
            AddFloatSlider(body, "サイズ Y", bl ? mod.TargetScaleZ : mod.TargetScaleY, 0.05, 3.0, "0.00", mod.Id, "tsY",
                (m, v) => { if (bl) m.TargetScaleZ = v; else m.TargetScaleY = v; }, hardMin: 0.0001, hardMax: 100000.0);
            AddFloatSlider(body, "サイズ Z", bl ? mod.TargetScaleY : mod.TargetScaleZ, 0.05, 3.0, "0.00", mod.Id, "tsZ",
                (m, v) => { if (bl) m.TargetScaleY = v; else m.TargetScaleZ = v; }, hardMin: 0.0001, hardMax: 100000.0);
        }
    }

    private void BuildImagePickerRow(StackPanel body, MeshModifierItem mod)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tb = new TextBox
        {
            Text = mod.DisplaceImagePath ?? string.Empty,
            IsReadOnly = true,
            Background = new SolidColorBrush(Color.FromRgb(22, 25, 31)),
            Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 67, 79)),
            BorderThickness = new Thickness(1),
            FontSize = 10,
            Padding = new Thickness(4, 2, 4, 2),
            Focusable = false
        };
        Grid.SetColumn(tb, 0);
        row.Children.Add(tb);

        var btn = new Button
        {
            Style = (Style)FindResource("ModSmallBtn"),
            Content = "参照...",
            Margin = new Thickness(4, 0, 0, 0)
        };
        btn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = "画像ファイル (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.webp|すべてのファイル (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                tb.Text = dlg.FileName;
                _vm?.MutateModifier(mod.Id, m => m.DisplaceImagePath = dlg.FileName, coalesceTag: null, commitUndoRecord: true);
            }
        };
        Grid.SetColumn(btn, 1);
        row.Children.Add(btn);
        body.Children.Add(row);
    }

    private void AddCheckRow(
        StackPanel body,
        string label,
        (string Text, bool Checked, Action<MeshModifierItem, bool> Setter)[] items,
        int modId,
        bool rebuildOnToggle)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(185, 194, 208)),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (text, isChecked, setter) in items)
        {
            var cb = new CheckBox
            {
                Style = (Style)FindResource("ModDarkCheckBox"),
                Content = text,
                IsChecked = isChecked,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var localSetter = setter;
            cb.Checked += (_, _) =>
            {
                _vm?.MutateModifier(modId, m => localSetter(m, true), null, true);
                if (rebuildOnToggle) RefreshFromViewModel();
            };
            cb.Unchecked += (_, _) =>
            {
                _vm?.MutateModifier(modId, m => localSetter(m, false), null, true);
                if (rebuildOnToggle) RefreshFromViewModel();
            };
            wrap.Children.Add(cb);
        }
        Grid.SetColumn(wrap, 1);
        grid.Children.Add(wrap);
        body.Children.Add(grid);
    }

    private void AddSingleCheck(
        StackPanel body,
        string label,
        bool isChecked,
        int modId,
        Action<MeshModifierItem, bool> setter)
    {
        var cb = new CheckBox
        {
            Style = (Style)FindResource("ModDarkCheckBox"),
            Content = label,
            IsChecked = isChecked,
            Margin = new Thickness(0, 2, 0, 3)
        };
        cb.Checked += (_, _) => _vm?.MutateModifier(modId, m => setter(m, true), null, true);
        cb.Unchecked += (_, _) => _vm?.MutateModifier(modId, m => setter(m, false), null, true);
        body.Children.Add(cb);
    }

    private void AddFloatSlider(
        StackPanel body,
        string label,
        float current,
        double min,
        double max,
        string format,
        int modId,
        string coalesceTag,
        Action<MeshModifierItem, float> setter,
        double? hardMin = null,
        double? hardMax = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 1.5, 0, 1.5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        double effHardMin = hardMin.HasValue ? Math.Min(hardMin.Value, min) : min;
        double effHardMax = hardMax.HasValue ? Math.Max(hardMax.Value, max) : max;
        double clamped = Math.Clamp(current, effHardMin, effHardMax);
        var slider = new Controls.NumericTextBoxSlider
        {
            IsDarkTheme = true,
            Minimum = min,
            Maximum = max,
            HardMinimum = hardMin ?? double.NaN,
            HardMaximum = hardMax ?? double.NaN,
            SmallChange = (max - min) * 0.01,
            LargeChange = (max - min) * 0.1,
            StringFormat = format,
            TextBoxWidth = 58,
            Value = clamped,
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.ValueChanged += (_, e) =>
        {
            float f = (float)e.NewValue;
            _vm?.MutateModifier(modId, m => setter(m, f), coalesceTag, commitUndoRecord: false);
        };
        slider.ValueCommitted += (_, _) => _vm?.CommitModifierUndoRecord();

        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        body.Children.Add(grid);
    }

    private void AddIntSlider(
        StackPanel body,
        string label,
        int current,
        int min,
        int max,
        int modId,
        string coalesceTag,
        Action<MeshModifierItem, int> setter,
        int? hardMin = null,
        int? hardMax = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 1.5, 0, 1.5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        int effHardMin = hardMin.HasValue ? Math.Min(hardMin.Value, min) : min;
        int effHardMax = hardMax.HasValue ? Math.Max(hardMax.Value, max) : max;
        var slider = new Controls.NumericTextBoxSlider
        {
            IsDarkTheme = true,
            IsInteger = true,
            Minimum = min,
            Maximum = max,
            HardMinimum = hardMin.HasValue ? hardMin.Value : double.NaN,
            HardMaximum = hardMax.HasValue ? hardMax.Value : double.NaN,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 1,
            StringFormat = "0",
            TextBoxWidth = 50,
            Value = Math.Clamp(current, effHardMin, effHardMax),
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.ValueChanged += (_, e) =>
        {
            int iv = (int)Math.Round(e.NewValue);
            _vm?.MutateModifier(modId, m => setter(m, iv), coalesceTag, commitUndoRecord: false);
        };
        slider.ValueCommitted += (_, _) => _vm?.CommitModifierUndoRecord();

        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        body.Children.Add(grid);
    }

    private void AddEnumCombo<T>(
        StackPanel body,
        string label,
        T currentValue,
        IReadOnlyList<(T Value, string Label)> options,
        int modId,
        Action<MeshModifierItem, T> setter,
        bool rebuild)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var combo = new ComboBox
        {
            Style = (Style)FindResource("ModDarkComboBox")
        };
        int selIdx = 0;
        for (int i = 0; i < options.Count; i++)
        {
            combo.Items.Add(options[i].Label);
            if (EqualityComparer<T>.Default.Equals(options[i].Value, currentValue))
                selIdx = i;
        }
        combo.SelectedIndex = selIdx;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= options.Count) return;
            T chosen = options[combo.SelectedIndex].Value;
            _vm?.MutateModifier(modId, m => setter(m, chosen), null, true);
            if (rebuild) RefreshFromViewModel();
        };

        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        body.Children.Add(grid);
    }
}
