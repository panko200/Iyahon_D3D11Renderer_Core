using System;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YukkuriMovieMaker.Commons;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Model3D;

public partial class BezierCurvePointEditor : UserControl, IPropertyEditorControl2, IPropertyEditorControl
{
    public static readonly DependencyProperty CurveJsonProperty =
        DependencyProperty.Register(
            nameof(CurveJson),
            typeof(string),
            typeof(BezierCurvePointEditor),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, _) =>
                {
                    if (d is BezierCurvePointEditor ed && !ed._updatingInternal)
                        ed.ReloadFromJson();
                }));

    private ItemProperty[]? _properties;
    private D3DPrimitiveShapeParameter? _ownerParam;
    private BezierCurve3DData _data = BezierCurve3DData.CreateDefault();
    private int _selectedIndex;
    private bool _updatingUi;
    private bool _updatingInternal;

    public event EventHandler? BeginEdit;
    public event EventHandler? EndEdit;

    public string CurveJson
    {
        get => (string)GetValue(CurveJsonProperty);
        set => SetValue(CurveJsonProperty, value);
    }

    public ItemProperty[]? Properties
    {
        get => _properties;
        set
        {
            if (ReferenceEquals(_properties, value)) return;
            _properties = value;
            var nextOwner = _properties is { Length: > 0 }
                ? _properties[0].PropertyOwner as D3DPrimitiveShapeParameter
                : null;
            if (!ReferenceEquals(_ownerParam, nextOwner))
            {
                if (_ownerParam != null)
                    ViewportBezierCurveBridge.ClearEditor(_ownerParam);
                _ownerParam = nextOwner;
            }
            ReloadFromJson();
            if (_ownerParam != null)
                ViewportBezierCurveBridge.SetEditorSelection(_ownerParam, _selectedIndex);
        }
    }

    public BezierCurvePointEditor()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewportBezierCurveBridge.PointSelectedFromViewport -= OnViewportPointSelected;
        ViewportBezierCurveBridge.PointSelectedFromViewport += OnViewportPointSelected;
        ViewportBezierCurveBridge.CurveEditedFromViewport -= OnViewportCurveEdited;
        ViewportBezierCurveBridge.CurveEditedFromViewport += OnViewportCurveEdited;
        if (_ownerParam != null)
            ViewportBezierCurveBridge.SetEditorSelection(_ownerParam, _selectedIndex);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewportBezierCurveBridge.PointSelectedFromViewport -= OnViewportPointSelected;
        ViewportBezierCurveBridge.CurveEditedFromViewport -= OnViewportCurveEdited;
        ViewportBezierCurveBridge.ClearEditor(_ownerParam);
    }

    private void OnViewportPointSelected(D3DPrimitiveShapeParameter param, int index)
    {
        if (!ReferenceEquals(param, _ownerParam)) return;
        Dispatcher.BeginInvoke(() =>
        {
            _updatingUi = true;
            try
            {
                _selectedIndex = Math.Clamp(index, 0, Math.Max(0, _data.Points.Count - 1));
                if (_selectedIndex < pointsList.Items.Count)
                    pointsList.SelectedIndex = _selectedIndex;
                SyncDetailFields();
            }
            finally
            {
                _updatingUi = false;
            }
        });
    }

    private void OnViewportCurveEdited(D3DPrimitiveShapeParameter param)
    {
        if (!ReferenceEquals(param, _ownerParam)) return;
        Dispatcher.BeginInvoke(ReloadFromJson);
    }

    public void SetEditorInfo(IEditorInfo? info) { }

    private void ReloadFromJson()
    {
        BezierCurve3DData.TryDeserialize(CurveJson, out _data);
        RefreshListAndFields();
    }

    private void RefreshListAndFields()
    {
        _updatingUi = true;
        try
        {
            pointsList.Items.Clear();
            for (int i = 0; i < _data.Points.Count; i++)
            {
                var p = _data.Points[i];
                pointsList.Items.Add($"#{i}: ({p.X:F0}, {p.Y:F0}, {p.Z:F0})  太さ:{p.Size:F1}");
            }

            if (_data.Points.Count == 0)
            {
                _selectedIndex = -1;
            }
            else
            {
                _selectedIndex = Math.Clamp(_selectedIndex, 0, _data.Points.Count - 1);
                pointsList.SelectedIndex = _selectedIndex;
            }
            SyncDetailFields();
            btnRemove.IsEnabled = _data.Points.Count > 2;
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void SyncDetailFields()
    {
        bool prev = _updatingUi;
        _updatingUi = true;
        try
        {
            if (_selectedIndex < 0 || _selectedIndex >= _data.Points.Count)
            {
                selectedHeader.Text = "選択なし";
                return;
            }

            var pt = _data.Points[_selectedIndex];
            selectedHeader.Text = $"制御点 #{_selectedIndex} の設定";
            tbPosX.Text = pt.X.ToString("0.##", CultureInfo.InvariantCulture);
            tbPosY.Text = pt.Y.ToString("0.##", CultureInfo.InvariantCulture);
            tbPosZ.Text = pt.Z.ToString("0.##", CultureInfo.InvariantCulture);
            tbSize.Text = pt.Size.ToString("0.##", CultureInfo.InvariantCulture);
            tbTilt.Text = pt.TiltDeg.ToString("0.#", CultureInfo.InvariantCulture);
            tbInX.Text = pt.HandleInX.ToString("0.##", CultureInfo.InvariantCulture);
            tbInY.Text = pt.HandleInY.ToString("0.##", CultureInfo.InvariantCulture);
            tbInZ.Text = pt.HandleInZ.ToString("0.##", CultureInfo.InvariantCulture);
            tbOutX.Text = pt.HandleOutX.ToString("0.##", CultureInfo.InvariantCulture);
            tbOutY.Text = pt.HandleOutY.ToString("0.##", CultureInfo.InvariantCulture);
            tbOutZ.Text = pt.HandleOutZ.ToString("0.##", CultureInfo.InvariantCulture);
            cbAutoSmooth.IsChecked = pt.AutoSmooth;
        }
        finally
        {
            _updatingUi = prev;
        }
    }

    private void CommitData(bool refreshList = true)
    {
        _updatingInternal = true;
        try
        {
            BeginEdit?.Invoke(this, EventArgs.Empty);
            CurveJson = _data.Serialize();
            EndEdit?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _updatingInternal = false;
        }

        if (refreshList)
            RefreshListAndFields();
        if (_ownerParam != null)
            ViewportBezierCurveBridge.SetEditorSelection(_ownerParam, _selectedIndex);
    }

    private void PointsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi) return;
        _selectedIndex = pointsList.SelectedIndex;
        SyncDetailFields();
        if (_ownerParam != null)
            ViewportBezierCurveBridge.SetEditorSelection(_ownerParam, _selectedIndex);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data.Points.Count == 0)
        {
            _data = BezierCurve3DData.CreateDefault();
            _selectedIndex = 0;
            CommitData();
            return;
        }

        int insertAfter = Math.Clamp(_selectedIndex, 0, _data.Points.Count - 1);
        var a = _data.Points[insertAfter];
        BezierCurvePoint3D newPt;
        if (insertAfter < _data.Points.Count - 1)
        {
            var b = _data.Points[insertAfter + 1];
            newPt = new BezierCurvePoint3D
            {
                Position = (a.Position + b.Position) * 0.5f,
                Size = (a.Size + b.Size) * 0.5f,
                TiltDeg = (a.TiltDeg + b.TiltDeg) * 0.5f,
                AutoSmooth = true,
            };
        }
        else
        {
            Vector3 dir = a.HandleOut.LengthSquared() > 1f
                ? Vector3.Normalize(a.HandleOut) * 120f
                : new Vector3(120f, 0f, 0f);
            newPt = new BezierCurvePoint3D
            {
                Position = a.Position + dir,
                Size = a.Size,
                TiltDeg = a.TiltDeg,
                AutoSmooth = true,
            };
        }

        _data.Points.Insert(insertAfter + 1, newPt);
        _data.AutoComputeHandles(onlyZero: true, closed: _ownerParam?.BezierClosed ?? false);
        _selectedIndex = insertAfter + 1;
        CommitData();
    }

    private void DuplicateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _data.Points.Count) return;
        var clone = _data.Points[_selectedIndex].Clone();
        clone.Position += new Vector3(60f, 0f, 0f);
        _data.Points.Insert(_selectedIndex + 1, clone);
        _selectedIndex++;
        CommitData();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data.Points.Count <= 2 || _selectedIndex < 0 || _selectedIndex >= _data.Points.Count)
            return;
        _data.Points.RemoveAt(_selectedIndex);
        _selectedIndex = Math.Clamp(_selectedIndex, 0, _data.Points.Count - 1);
        CommitData();
    }

    private void AutoSmoothAll_Click(object sender, RoutedEventArgs e)
    {
        _data.AutoComputeHandles(onlyZero: false, closed: _ownerParam?.BezierClosed ?? false);
        CommitData();
    }

    private void ReverseButton_Click(object sender, RoutedEventArgs e)
    {
        _data.Points.Reverse();
        foreach (var pt in _data.Points)
        {
            (pt.HandleIn, pt.HandleOut) = (pt.HandleOut, pt.HandleIn);
        }
        _selectedIndex = Math.Max(0, _data.Points.Count - 1 - _selectedIndex);
        CommitData();
    }

    private void AutoSmoothCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _selectedIndex < 0 || _selectedIndex >= _data.Points.Count) return;
        _data.Points[_selectedIndex].AutoSmooth = cbAutoSmooth.IsChecked == true;
        CommitData(refreshList: false);
    }

    private void NumericField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            CommitNumericFields();
            e.Handled = true;
        }
    }

    private void NumericField_Commit(object sender, RoutedEventArgs e)
    {
        if (!_updatingUi)
            CommitNumericFields();
    }

    private void CommitNumericFields()
    {
        if (_updatingUi || _selectedIndex < 0 || _selectedIndex >= _data.Points.Count) return;
        var pt = _data.Points[_selectedIndex];

        static float Parse(string s, float fallback)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v)
                ? v
                : fallback;

        pt.X = Parse(tbPosX.Text, pt.X);
        pt.Y = Parse(tbPosY.Text, pt.Y);
        pt.Z = Parse(tbPosZ.Text, pt.Z);
        pt.Size = MathF.Max(0.25f, Parse(tbSize.Text, pt.Size));
        pt.TiltDeg = Parse(tbTilt.Text, pt.TiltDeg);
        pt.HandleInX = Parse(tbInX.Text, pt.HandleInX);
        pt.HandleInY = Parse(tbInY.Text, pt.HandleInY);
        pt.HandleInZ = Parse(tbInZ.Text, pt.HandleInZ);
        pt.HandleOutX = Parse(tbOutX.Text, pt.HandleOutX);
        pt.HandleOutY = Parse(tbOutY.Text, pt.HandleOutY);
        pt.HandleOutZ = Parse(tbOutZ.Text, pt.HandleOutZ);
        CommitData();
    }
}
