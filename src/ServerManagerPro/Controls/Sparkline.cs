using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ServerManagerPro.Core;

namespace ServerManagerPro.Controls;

/// <summary>Verlaufsdiagramm im Stil des Task-Managers.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>Feste Obergrenze der Skala; 0 = automatisch.</summary>
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }

    readonly List<double> _values = new();
    public int Capacity { get; set; } = 90;

    public void Push(double v)
    {
        _values.Add(v);
        while (_values.Count > Capacity) _values.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
        for (int i = 1; i < 4; i++) dc.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));
        if (_values.Count < 2) return;

        double max = MaxValue > 0 ? MaxValue : Math.Max(1, _values.Max() * 1.15);
        double step = w / (Capacity - 1);
        double x0 = w - (_values.Count - 1) * step;
        var pts = _values.Select((v, i) => new Point(x0 + i * step, h - Math.Clamp(v / max, 0, 1) * (h - 2) - 1)).ToList();

        var fig = new PathFigure { StartPoint = new Point(pts[0].X, h), IsClosed = true };
        foreach (var p in pts) fig.Segments.Add(new LineSegment(p, true));
        fig.Segments.Add(new LineSegment(new Point(pts[^1].X, h), true));
        var fillGeo = new PathGeometry(new[] { fig });
        fillGeo.Freeze();

        Color c = (Stroke as SolidColorBrush)?.Color ?? Colors.Teal;
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(55, c.R, c.G, c.B)), null, fillGeo);

        var line = new PathFigure { StartPoint = pts[0] };
        foreach (var p in pts.Skip(1)) line.Segments.Add(new LineSegment(p, true));
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, new PathGeometry(new[] { line }));
    }
}

public sealed class HealthBrushConverter : IValueConverter
{
    public static Brush For(Health h) => h switch
    {
        Health.Ok => Palette.Ok,
        Health.Warning => Palette.Warn,
        Health.Critical => Palette.Crit,
        Health.Info => Palette.Info,
        _ => Palette.Muted,
    };

    public object Convert(object value, Type t, object p, CultureInfo c) => value is Health h ? For(h) : Palette.Muted;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class HealthLabelConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is Health h ? h.Label() : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class PercentBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double v = value is double d ? d : 0;
        return v >= 90 ? Palette.Crit : v >= 75 ? Palette.Warn : Palette.Accent;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public static class Palette
{
    static SolidColorBrush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    public static readonly Brush Ok = B("#22C55E"), Warn = B("#F59E0B"), Crit = B("#EF4444"), Info = B("#60A5FA"),
        Muted = B("#8B9BAB"), Accent = B("#2DD4BF"), Violet = B("#A78BFA"), Orange = B("#FB923C");
}
