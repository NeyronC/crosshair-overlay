using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CrosshairOverlay;

public sealed class PixelGridEditor : FrameworkElement
{
    Preset? _preset;
    public Preset? Preset
    {
        get => _preset;
        set
        {
            if (_preset != null) _preset.PropertyChanged -= OnPresetChanged;
            _preset = value;
            if (_preset != null) _preset.PropertyChanged += OnPresetChanged;
            InvalidateVisual();
        }
    }

    public bool MirrorX { get; set; } = true;
    public bool MirrorY { get; set; } = true;

    int _dragMode;

    static readonly Brush Bg = Frozen(new SolidColorBrush(Color.FromRgb(0x0E, 0x0F, 0x12)));
    static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    static readonly Pen AxisPen = FrozenPen(Color.FromArgb(0x70, 0xF5, 0xA5, 0x24));

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Color c) { var p = new Pen(new SolidColorBrush(c), 1); p.Freeze(); return p; }

    public PixelGridEditor()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    void OnPresetChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CrosshairOverlay.Preset.Pixels) or nameof(CrosshairOverlay.Preset.GridSize))
            InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double s = Math.Min(double.IsInfinity(available.Width) ? 400 : available.Width,
                            double.IsInfinity(available.Height) ? 400 : available.Height);
        return new Size(s, s);
    }

    int G => Math.Clamp(_preset?.GridSize ?? 21, 5, 63);
    double Cell => Math.Floor(Math.Min(ActualWidth, ActualHeight) / G);
    Point Origin => new((ActualWidth - Cell * G) / 2, (ActualHeight - Cell * G) / 2);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_preset == null || Cell < 2) return;
        int g = G;
        double c = Cell;
        var o = Origin;

        foreach (var kv in _preset.Pixels)
        {
            int x = kv.Key % g, y = kv.Key / g;
            if (y >= g) continue;
            var col = CrosshairRenderer.ParseColor(kv.Value, Colors.White);
            var b = new SolidColorBrush(col);
            dc.DrawRectangle(b, null, new Rect(o.X + x * c, o.Y + y * c, c, c));
        }

        for (int i = 0; i <= g; i++)
        {
            double v = Math.Round(i * c) + 0.5;
            dc.DrawLine(GridPen, new Point(o.X + v, o.Y), new Point(o.X + v, o.Y + g * c));
            dc.DrawLine(GridPen, new Point(o.X, o.Y + v), new Point(o.X + g * c, o.Y + v));
        }

        int m = g / 2;
        dc.DrawRectangle(null, AxisPen, new Rect(o.X + m * c + 0.5, o.Y + m * c + 0.5, c, c));
    }

    bool HitCell(Point p, out int x, out int y)
    {
        var o = Origin;
        x = (int)Math.Floor((p.X - o.X) / Cell);
        y = (int)Math.Floor((p.Y - o.Y) / Cell);
        return x >= 0 && y >= 0 && x < G && y < G;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragMode = e.ChangedButton == MouseButton.Left ? 1 : e.ChangedButton == MouseButton.Right ? 2 : 0;
        if (_dragMode != 0) { CaptureMouse(); Apply(e.GetPosition(this)); e.Handled = true; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragMode != 0) Apply(e.GetPosition(this));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        _dragMode = 0;
        ReleaseMouseCapture();
    }

    void Apply(Point p)
    {
        if (_preset == null || !HitCell(p, out int x, out int y)) return;
        int g = G;
        bool changed = false;
        void Put(int px, int py)
        {
            int idx = py * g + px;
            if (_dragMode == 1)
            {
                if (!_preset.Pixels.TryGetValue(idx, out var old) || old != _preset.Color)
                { _preset.Pixels[idx] = _preset.Color; changed = true; }
            }
            else if (_preset.Pixels.Remove(idx)) changed = true;
        }
        Put(x, y);
        if (MirrorX) Put(g - 1 - x, y);
        if (MirrorY) Put(x, g - 1 - y);
        if (MirrorX && MirrorY) Put(g - 1 - x, g - 1 - y);
        if (changed) _preset.NotifyPixelsChanged();
    }
}

public sealed class SliderRow : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SliderRow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Slider _slider = new() { VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
    readonly TextBlock _val = new() { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };

    public string Label { get => _label.Text; set => _label.Text = value; }
    public double Minimum { get => _slider.Minimum; set => _slider.Minimum = value; }
    public double Maximum { get => _slider.Maximum; set => _slider.Maximum = value; }
    public double Step { get => _slider.TickFrequency; set { _slider.TickFrequency = value; _slider.SmallChange = value; } }
    public string Format { get; set; } = "0";
    public string Suffix { get; set; } = " px";

    public SliderRow()
    {
        Margin = new Thickness(0, 4, 0, 4);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        _label.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _val.SetResourceReference(TextBlock.ForegroundProperty, "TextMain");
        _val.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        SetColumn(_slider, 1); SetColumn(_val, 2);
        Children.Add(_label); Children.Add(_slider); Children.Add(_val);
        _slider.SetBinding(RangeBase_Value, new Binding(nameof(Value)) { Source = this, Mode = BindingMode.TwoWay });
        _slider.ValueChanged += (_, _) => UpdateText();
        _slider.MouseWheel += (_, e) =>
        {
            _slider.Value = Math.Clamp(_slider.Value + Math.Sign(e.Delta) * _slider.SmallChange, _slider.Minimum, _slider.Maximum);
            e.Handled = true;
        };
    }

    static readonly DependencyProperty RangeBase_Value = System.Windows.Controls.Primitives.RangeBase.ValueProperty;

    void UpdateText() => _val.Text = Value.ToString(Format, CultureInfo.InvariantCulture) + Suffix;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ValueProperty) UpdateText();
    }
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Math.Round((double)value * 100);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => (double)value / 100.0;
}

public sealed class ModeVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value?.ToString() == (string)p ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class ModeCheckedConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == (string)p;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is true ? Enum.Parse<CrosshairMode>((string)p) : Binding.DoNothing;
}

public sealed class EnumCheckedConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == (string)p;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is true ? Enum.Parse(t, (string)p) : Binding.DoNothing;
}

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => new SolidColorBrush(CrosshairRenderer.ParseColor(value as string, Colors.Transparent));
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
