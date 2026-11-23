using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

#nullable enable
namespace Iyahon_D3D11Renderer_Core.Controls;

/// <summary>
/// スライダー操作と数値テキスト直接入力を両立する共通 NumericTextBoxSlider コントロール。
/// </summary>
public partial class NumericTextBoxSlider : UserControl
{
    private bool _suppressSync;
    private bool _isEditingText;
    private double _valueBeforeEdit;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(NumericTextBoxSlider),
        new FrameworkPropertyMetadata(
            0.0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValuePropertyChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(double),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(0.0, OnRangePropertyChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(1.0, OnRangePropertyChanged));

    public static readonly DependencyProperty SmallChangeProperty = DependencyProperty.Register(
        nameof(SmallChange),
        typeof(double),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(0.01, OnRangePropertyChanged));

    public static readonly DependencyProperty LargeChangeProperty = DependencyProperty.Register(
        nameof(LargeChange),
        typeof(double),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(0.1, OnRangePropertyChanged));

    public static readonly DependencyProperty TickFrequencyProperty = DependencyProperty.Register(
        nameof(TickFrequency),
        typeof(double),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(0.0, OnRangePropertyChanged));

    public static readonly DependencyProperty IsSnapToTickEnabledProperty = DependencyProperty.Register(
        nameof(IsSnapToTickEnabled),
        typeof(bool),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(false, OnRangePropertyChanged));

    public static readonly DependencyProperty IsIntegerProperty = DependencyProperty.Register(
        nameof(IsInteger),
        typeof(bool),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(false, OnFormatPropertyChanged));

    public static readonly DependencyProperty StringFormatProperty = DependencyProperty.Register(
        nameof(StringFormat),
        typeof(string),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata("0.00", OnFormatPropertyChanged));

    public static readonly DependencyProperty TextBoxWidthProperty = DependencyProperty.Register(
        nameof(TextBoxWidth),
        typeof(double),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(48.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SliderWidthProperty = DependencyProperty.Register(
        nameof(SliderWidth),
        typeof(double),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(double.NaN, OnLayoutPropertyChanged));

    public static readonly DependencyProperty IsDarkThemeProperty = DependencyProperty.Register(
        nameof(IsDarkTheme),
        typeof(bool),
        typeof(NumericTextBoxSlider),
        new PropertyMetadata(false, OnLayoutPropertyChanged));

    public event RoutedPropertyChangedEventHandler<double>? ValueChanged;
    public event EventHandler? ValueCommitted;

    public NumericTextBoxSlider()
    {
        InitializeComponent();
        ApplyVisualStyle();
        SyncSliderAndText();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double SmallChange
    {
        get => (double)GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    public double LargeChange
    {
        get => (double)GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    public double TickFrequency
    {
        get => (double)GetValue(TickFrequencyProperty);
        set => SetValue(TickFrequencyProperty, value);
    }

    public bool IsSnapToTickEnabled
    {
        get => (bool)GetValue(IsSnapToTickEnabledProperty);
        set => SetValue(IsSnapToTickEnabledProperty, value);
    }

    public bool IsInteger
    {
        get => (bool)GetValue(IsIntegerProperty);
        set => SetValue(IsIntegerProperty, value);
    }

    public string StringFormat
    {
        get => (string)GetValue(StringFormatProperty);
        set => SetValue(StringFormatProperty, value);
    }

    public double TextBoxWidth
    {
        get => (double)GetValue(TextBoxWidthProperty);
        set => SetValue(TextBoxWidthProperty, value);
    }

    public double SliderWidth
    {
        get => (double)GetValue(SliderWidthProperty);
        set => SetValue(SliderWidthProperty, value);
    }

    public bool IsDarkTheme
    {
        get => (bool)GetValue(IsDarkThemeProperty);
        set => SetValue(IsDarkThemeProperty, value);
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        ApplyVisualStyle();
        SyncSliderAndText();
    }

    private static void OnValuePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NumericTextBoxSlider ctrl)
        {
            double nv = (double)e.NewValue;
            if (!ctrl._isEditingText)
                ctrl._valueBeforeEdit = nv;
            ctrl.SyncSliderAndText();
        }
    }

    private static void OnRangePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NumericTextBoxSlider ctrl)
            ctrl.SyncSliderAndText();
    }

    private static void OnFormatPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NumericTextBoxSlider ctrl)
            ctrl.UpdateTextFromValue();
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NumericTextBoxSlider ctrl)
            ctrl.ApplyVisualStyle();
    }

    private void ApplyVisualStyle()
    {
        if (PART_TextBox == null || SliderCol == null) return;

        PART_TextBox.Width = double.IsNaN(TextBoxWidth) || TextBoxWidth <= 0 ? 48.0 : TextBoxWidth;
        SliderCol.Width = double.IsNaN(SliderWidth) || SliderWidth <= 0
            ? new GridLength(1.0, GridUnitType.Star)
            : new GridLength(Math.Max(20.0, SliderWidth));

        string resKey = IsDarkTheme ? "DarkNumericBoxStyle" : "LightNumericBoxStyle";
        if (TryFindResource(resKey) is Style style)
            PART_TextBox.Style = style;
    }

    private void SyncSliderAndText()
    {
        if (PART_Slider == null || PART_TextBox == null || !IsInitialized) return;

        _suppressSync = true;
        try
        {
            double lo = Minimum;
            double hi = Maximum >= Minimum ? Maximum : Minimum;
            PART_Slider.Minimum = lo;
            PART_Slider.Maximum = hi;
            PART_Slider.SmallChange = SmallChange > 0 ? SmallChange : (hi - lo) * 0.01;
            PART_Slider.LargeChange = LargeChange > 0 ? LargeChange : (hi - lo) * 0.1;
            PART_Slider.TickFrequency = IsInteger && TickFrequency <= 0 ? 1.0 : TickFrequency;
            PART_Slider.IsSnapToTickEnabled = IsSnapToTickEnabled;

            double clamped = ClampAndRound(Value);
            PART_Slider.Value = clamped;
        }
        finally
        {
            _suppressSync = false;
        }

        if (!_isEditingText)
            UpdateTextFromValue();
    }

    private void UpdateTextFromValue()
    {
        if (PART_TextBox == null) return;
        PART_TextBox.Text = FormatValue(Value);
    }

    private double ClampAndRound(double v)
    {
        double lo = Minimum;
        double hi = Maximum >= Minimum ? Maximum : Minimum;
        double c = Math.Clamp(double.IsNaN(v) ? lo : v, lo, hi);
        if (IsInteger)
            c = Math.Round(c);
        return c;
    }

    private string FormatValue(double v)
    {
        double normalized = IsInteger ? Math.Round(v) : v;
        if (IsInteger)
            return ((int)normalized).ToString(CultureInfo.InvariantCulture);

        string fmt = string.IsNullOrWhiteSpace(StringFormat) ? "0.00" : StringFormat;
        return normalized.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private void OnPartSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSync || !IsInitialized) return;

        double next = IsInteger ? Math.Round(e.NewValue) : e.NewValue;
        double prev = Value;

        _suppressSync = true;
        try
        {
            SetCurrentValue(ValueProperty, next);
            if (!_isEditingText)
            {
                _valueBeforeEdit = next;
                UpdateTextFromValue();
            }
        }
        finally
        {
            _suppressSync = false;
        }

        if (Math.Abs(next - prev) > 1e-12 || !IsInteger)
            ValueChanged?.Invoke(this, new RoutedPropertyChangedEventArgs<double>(prev, next, e.RoutedEvent));
    }

    private void OnPartSliderPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void OnPartTextBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!PART_TextBox.IsKeyboardFocusWithin)
        {
            PART_TextBox.Focus();
            PART_TextBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnPartTextBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _isEditingText = true;
        _valueBeforeEdit = Value;
        PART_TextBox.SelectAll();
    }

    private void OnPartTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isEditingText)
            CommitTextEdit();
    }

    private void OnPartTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitTextEdit();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelTextEdit();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            double step = IsInteger
                ? Math.Max(1.0, SmallChange)
                : ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? SmallChange * 0.1 : SmallChange);
            if (step <= 0) step = IsInteger ? 1.0 : 0.01;
            if (e.Key == Key.Down) step = -step;

            double prev = Value;
            double baseVal = TryParseNormalized(PART_TextBox.Text, out double curParsed) ? curParsed : prev;
            double next = ClampAndRound(baseVal + step);
            ApplyValueFromTextInteraction(prev, next, raiseCommitted: false);
            PART_TextBox.SelectAll();
            e.Handled = true;
        }
    }

    public void CommitTextEdit()
    {
        _isEditingText = false;
        double prev = Value;
        if (TryParseNormalized(PART_TextBox.Text, out double parsed) && double.IsFinite(parsed))
        {
            double next = ClampAndRound(parsed);
            ApplyValueFromTextInteraction(prev, next, raiseCommitted: true);
        }
        else
        {
            PART_TextBox.Text = FormatValue(Value);
        }
    }

    public void CancelTextEdit()
    {
        _isEditingText = false;
        double cur = Value;
        if (Math.Abs(cur - _valueBeforeEdit) > 1e-12)
        {
            ApplyValueFromTextInteraction(cur, _valueBeforeEdit, raiseCommitted: false);
        }
        else
        {
            PART_TextBox.Text = FormatValue(_valueBeforeEdit);
        }
    }

    private void ApplyValueFromTextInteraction(double prev, double next, bool raiseCommitted)
    {
        _suppressSync = true;
        try
        {
            SetCurrentValue(ValueProperty, next);
            if (PART_Slider != null)
                PART_Slider.Value = ClampAndRound(next);
            PART_TextBox.Text = FormatValue(next);
            if (raiseCommitted)
                _valueBeforeEdit = next;
        }
        finally
        {
            _suppressSync = false;
        }

        if (Math.Abs(next - prev) > 1e-12)
            ValueChanged?.Invoke(this, new RoutedPropertyChangedEventArgs<double>(prev, next));

        if (raiseCommitted)
            ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryParseNormalized(string? raw, out double result)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            result = 0;
            return false;
        }

        Span<char> buf = raw.Length <= 64 ? stackalloc char[raw.Length] : new char[raw.Length];
        int len = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (char.IsWhiteSpace(c)) continue;
            if (c >= '０' && c <= '９') c = (char)('0' + (c - '０'));
            else if (c is '．' or '。') c = '.';
            else if (c is '－' or 'ー' or '−' or '―') c = '-';
            else if (c == '＋') c = '+';
            else if (c == '，') c = ',';
            buf[len++] = c;
        }

        var span = buf[..len];
        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
               double.TryParse(span, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
    }
}
