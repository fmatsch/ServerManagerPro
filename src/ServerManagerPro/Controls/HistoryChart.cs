using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ServerManagerPro.Controls;

public readonly record struct ChartPoint(DateTime T, double Avg, double Max);

/// <summary>Liniendiagramm über die Zeit mit Achsen, optionalem Höchstwert-Band, Lücken bei fehlenden Daten und Mauszeiger.</summary>
public sealed class HistoryChart : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(HistoryChart),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Feste Obergrenze der Skala; 0 = automatisch.</summary>
    public double YMax { get; set; } = 100;
    /// <summary>Beschriftung der Werte.</summary>
    public Func<double, string> Format { get; set; } = v => $"{v:0} %";
    public bool ShowMax { get; set; } = true;
    public string EmptyText { get; set; } = "Keine Daten im gewählten Zeitraum";

    List<ChartPoint> _pts = new();
    DateTime _from, _to;
    double _hoverX = -1;

    public void SetData(IEnumerable<ChartPoint> points, DateTime from, DateTime to)
    {
        _pts = points.Where(p => !double.IsNaN(p.Avg)).OrderBy(p => p.T).ToList();
        _from = from; _to = to;
        InvalidateVisual();
    }

    const double L = 54, R = 10, T = 8, B = 22;

    /// <summary>Erster runder Zeitpunkt ab <paramref name="from"/> (lokale Zeit, Vielfache des Schritts seit Mitternacht bzw. Tagesbeginn).</summary>
    static DateTime StepStart(DateTime from, double step)
    {
        var day = from.Date;
        if (step >= 86400) { var d = day; return d < from ? d.AddSeconds(step >= 604800 ? 86400 : step) : d; }
        double secs = (from - day).TotalSeconds;
        double n = Math.Ceiling(secs / step);
        return day.AddSeconds(n * step);
    }

    /// <summary>Nur für Tests: setzt den Mauszeiger auf einen Bruchteil der Breite.</summary>
    public void DebugHover(double frac) { _hoverX = L + frac * (ActualWidth - L - R); InvalidateVisual(); }

    protected override void OnMouseMove(MouseEventArgs e) { _hoverX = e.GetPosition(this).X; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hoverX = -1; InvalidateVisual(); }

    FormattedText Text(string s, double size, Brush b, double dpi) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, dpi);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 120 || h < 60) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // damit die Maus überall trifft
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x9B, 0xAB));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)), 1);
        double pw = w - L - R, ph = h - T - B;
        double span = Math.Max(1, (_to - _from).TotalSeconds);

        double max = YMax > 0 ? YMax : Math.Max(1, _pts.Count == 0 ? 1 : _pts.Max(p => ShowMax && !double.IsNaN(p.Max) ? Math.Max(p.Avg, p.Max) : p.Avg) * 1.15);
        double X(DateTime t) => L + Math.Clamp((t - _from).TotalSeconds / span, 0, 1) * pw;
        double Y(double v) => T + ph - Math.Clamp(v / max, 0, 1) * ph;

        // Raster und Werteachse
        for (int i = 0; i <= 4; i++)
        {
            double y = T + ph - ph * i / 4;
            dc.DrawLine(grid, new Point(L, y), new Point(L + pw, y));
            var ft = Text(Format(max * i / 4), 10.5, muted, dpi);
            dc.DrawText(ft, new Point(L - 6 - ft.Width, y - ft.Height / 2));
        }
        // Zeitachse: runde Zeitpunkte (volle Stunden, Tage …) in lokaler Zeit
        double[] steps = { 600, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800 };
        int maxTicks = Math.Clamp((int)(pw / 80), 2, 10);
        double step = steps.FirstOrDefault(x => span / x <= maxTicks);
        if (step == 0) step = steps[^1];
        var tick = StepStart(_from, step);
        string fmt = step >= 86400 ? "dd.MM." : span > 36 * 3600 ? "ddd HH:mm" : "HH:mm";
        for (; tick <= _to; tick = tick.AddSeconds(step))
        {
            if (tick < _from) continue;
            double x = X(tick);
            dc.DrawLine(grid, new Point(x, T), new Point(x, T + ph));
            var ft = Text(tick.ToString(fmt), 10.5, muted, dpi);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, 0, w - ft.Width), T + ph + 4));
        }

        if (_pts.Count == 0)
        {
            var ft = Text(EmptyText, 12, muted, dpi);
            dc.DrawText(ft, new Point(L + (pw - ft.Width) / 2, T + (ph - ft.Height) / 2));
            return;
        }

        // Lücken: größerer Abstand als das 3-fache des typischen Abstands
        var gaps = _pts.Zip(_pts.Skip(1), (a, b) => (b.T - a.T).TotalSeconds).OrderBy(x => x).ToList();
        double typical = gaps.Count == 0 ? 60 : gaps[gaps.Count / 2];
        double breakAt = Math.Max(150, typical * 3);

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.Teal;
        var line = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        var band = new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B));

        var seg = new List<ChartPoint>();
        void Draw()
        {
            if (seg.Count == 0) return;
            if (seg.Count == 1)
            {
                dc.DrawEllipse(Stroke, null, new Point(X(seg[0].T), Y(seg[0].Avg)), 2.2, 2.2);
                return;
            }
            if (ShowMax && seg.Any(p => !double.IsNaN(p.Max)))
            {
                var fig = new PathFigure { StartPoint = new Point(X(seg[0].T), Y(double.IsNaN(seg[0].Max) ? seg[0].Avg : Math.Max(seg[0].Max, seg[0].Avg))), IsClosed = true };
                foreach (var p in seg.Skip(1)) fig.Segments.Add(new LineSegment(new Point(X(p.T), Y(double.IsNaN(p.Max) ? p.Avg : Math.Max(p.Max, p.Avg))), true));
                for (int i = seg.Count - 1; i >= 0; i--) fig.Segments.Add(new LineSegment(new Point(X(seg[i].T), Y(seg[i].Avg)), true));
                var geo = new PathGeometry(new[] { fig }); geo.Freeze();
                dc.DrawGeometry(band, null, geo);
            }
            var lf = new PathFigure { StartPoint = new Point(X(seg[0].T), Y(seg[0].Avg)) };
            foreach (var p in seg.Skip(1)) lf.Segments.Add(new LineSegment(new Point(X(p.T), Y(p.Avg)), true));
            dc.DrawGeometry(null, line, new PathGeometry(new[] { lf }));
        }
        foreach (var p in _pts)
        {
            if (seg.Count > 0 && (p.T - seg[^1].T).TotalSeconds > breakAt) { Draw(); seg = new(); }
            seg.Add(p);
        }
        Draw();

        // Mauszeiger: nächster Messpunkt
        if (_hoverX >= L && _hoverX <= L + pw)
        {
            var hoverT = _from.AddSeconds((_hoverX - L) / pw * span);
            var near = _pts.OrderBy(p => Math.Abs((p.T - hoverT).TotalSeconds)).First();
            if (Math.Abs((near.T - hoverT).TotalSeconds) <= Math.Max(breakAt, span / 60))
            {
                double x = X(near.T), y = Y(near.Avg);
                var cross = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1);
                dc.DrawLine(cross, new Point(x, T), new Point(x, T + ph));
                dc.DrawEllipse(Stroke, new Pen(Brushes.White, 1.2), new Point(x, y), 4, 4);
                string txt = $"{near.T:ddd dd.MM. HH:mm}\n{Format(near.Avg)}" + (ShowMax && !double.IsNaN(near.Max) && near.Max > near.Avg + 0.5 ? $"  (max. {Format(near.Max)})" : "");
                var ft = Text(txt, 11.5, Brushes.White, dpi);
                double bx = x + 12 + ft.Width + 12 > w ? x - 12 - ft.Width - 12 : x + 12;
                double by = Math.Clamp(y - ft.Height / 2 - 6, T, T + ph - ft.Height - 12);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 11, 18, 26)), new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1),
                    new Rect(bx, by, ft.Width + 12, ft.Height + 10), 6, 6);
                dc.DrawText(ft, new Point(bx + 6, by + 5));
            }
        }
    }
}
