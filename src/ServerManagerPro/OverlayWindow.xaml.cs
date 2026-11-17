using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ServerManagerPro.Controls;
using ServerManagerPro.Core;

namespace ServerManagerPro;

/// <summary>
/// Kleines Dauer-Overlay mit frei gewählten Kennzahlen. Klickdurchlässig, nimmt nie den Fokus und liest nur die
/// ohnehin vorhandenen Messwerte – es verursacht keine eigenen Abfragen.
/// </summary>
public partial class OverlayWindow : Window
{
    // ---- Win32 ----
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
    const int WM_HOTKEY = 0x0312;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    readonly HealthMonitor _mon;
    readonly AppSettings _cfg;
    MetricsSnapshot? _m;
    Snapshot? _s;
    List<ProcRecord> _p = new();
    IntPtr _hwnd;
    bool _moving;
    string _sig = "";
    readonly DispatcherTimer _top = new() { Interval = TimeSpan.FromSeconds(15) };

    public event Action? ToggleRequested, MainRequested, Moved;

    public static readonly (string key, string name, bool needsHealth, bool needsProcs)[] Catalog =
    {
        ("health", "Gesundheit (Ampel + Wert)", true, false),
        ("cpu", "CPU", false, false),
        ("ram", "Arbeitsspeicher", false, false),
        ("disk", "Datenträger-Aktivität", false, false),
        ("net", "Netzwerk (Down/Up)", false, false),
        ("free", "Wenigster freier Platz", true, false),
        ("topproc", "Prozess mit höchster CPU", false, true),
        ("dbconn", "Datenbank (Verbindungen, CPU)", true, false),
        ("rdp", "Remote-Sitzungen", true, false),
        ("smb", "SMB-Clients", true, false),
        ("inet", "Internet-Verbindungen", true, false),
        ("backup", "Sicherung läuft", true, false),
        ("dhcp", "DHCP: wirklich freie Adressen", true, false),
        ("ad", "Active Directory: gesperrte Konten", true, false),
    };

    public OverlayWindow(HealthMonitor mon, AppSettings cfg)
    {
        InitializeComponent();
        _mon = mon; _cfg = cfg;
        mon.MetricsUpdated += m => Dispatcher.BeginInvoke(() => { _m = m; Render(); }, DispatcherPriority.Background);
        mon.ProcessesUpdated += p => Dispatcher.BeginInvoke(() => _p = p, DispatcherPriority.Background);
        mon.HealthUpdated += s => Dispatcher.BeginInvoke(() => { _s = s; Render(); }, DispatcherPriority.Background);
        _top.Tick += (_, _) => KeepOnTop();
        SizeChanged += (_, _) => Reposition();
        MouseLeftButtonDown += (_, e) => { if (_moving) { try { DragMove(); } catch { } } };
        MouseLeftButtonUp += (_, _) => { if (_moving) EndMove(); };

        // Handle sofort anlegen, damit Hotkeys auch bei verstecktem Overlay funktionieren
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var src = HwndSource.FromHwnd(_hwnd);
        src?.AddHook(Hook);
        RegisterHotKey(_hwnd, 1, 0x0003 | 0x4000, 0x4F); // Strg+Alt+O: Overlay ein/aus
        RegisterHotKey(_hwnd, 2, 0x0003 | 0x4000, 0x4D); // Strg+Alt+M: Hauptfenster
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        ApplySettings();
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            if (w.ToInt32() == 1) ToggleRequested?.Invoke();
            else if (w.ToInt32() == 2) MainRequested?.Invoke();
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKey(_hwnd, 1); UnregisterHotKey(_hwnd, 2);
        base.OnClosed(e);
    }

    // ---------- Anzeige ----------
    public bool IsShown => IsVisible;

    public void ShowOverlay()
    {
        ApplySettings();
        if (!IsVisible) Show();
        SetClickThrough(!_moving);
        KeepOnTop();
        _top.Start();
        Render();
    }

    public void HideOverlay()
    {
        if (_moving) EndMove();
        _top.Stop();
        Hide();
    }

    void KeepOnTop() => SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    void SetClickThrough(bool on)
    {
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
    }

    /// <summary>Verschieben-Modus: Overlay wird greifbar, Loslassen speichert die Position.</summary>
    public void ToggleMove()
    {
        if (!IsVisible) ShowOverlay();
        if (_moving) { EndMove(); return; }
        _moving = true;
        SetClickThrough(false);
        Cursor = Cursors.SizeAll;
        Frame.BorderBrush = Palette.Accent;
        Frame.BorderThickness = new Thickness(2);
    }

    void EndMove()
    {
        _moving = false;
        SetClickThrough(true);
        Cursor = Cursors.Arrow;
        Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
        Frame.BorderThickness = new Thickness(1);
        _cfg.OverlayCorner = "Custom";
        _cfg.OverlayX = Left; _cfg.OverlayY = Top;
        _cfg.Save();
        Moved?.Invoke();
    }

    public void ApplySettings()
    {
        Opacity = Math.Clamp(_cfg.OverlayOpacity, 0.3, 1.0);
        System.Windows.Automation.AutomationProperties.SetName(this, "ServerManagerPro Overlay");
        FontSize = Math.Clamp(_cfg.OverlayFont, 9, 28);
        _sig = "";
        Reposition();
        Render();
    }

    void Reposition()
    {
        double w = ActualWidth, h = ActualHeight;
        if (_cfg.OverlayCorner == "Custom")
        {
            Left = Math.Clamp(_cfg.OverlayX, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Math.Max(w, 40));
            Top = Math.Clamp(_cfg.OverlayY, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Math.Max(h, 20));
            return;
        }
        // Ecke des gewählten Bildschirms; in Pixeln gesetzt, damit unterschiedliche Skalierung der Bildschirme keine Rolle spielt
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = _cfg.OverlayMonitor > 0 && _cfg.OverlayMonitor <= screens.Length ? screens[_cfg.OverlayMonitor - 1] : System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
        var wa = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Ceiling(w * dpi.DpiScaleX), ph = (int)Math.Ceiling(h * dpi.DpiScaleY), m = (int)(16 * dpi.DpiScaleX);
        int x = _cfg.OverlayCorner.EndsWith("Left") ? wa.Left + m : wa.Right - pw - m;
        int y = _cfg.OverlayCorner.StartsWith("Top") ? wa.Top + m : wa.Bottom - ph - m;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | 0x4 /* SWP_NOZORDER */);
    }

    // ---------- Inhalt ----------
    sealed record Item(string Label, string Value, Brush Color);

    List<Item> Build()
    {
        var items = new List<Item>();
        foreach (var key in _cfg.OverlayMetrics)
        {
            switch (key)
            {
                case "cpu" when _m != null:
                    items.Add(new("CPU", $"{_m.Cpu:0}%", Level(_m.Cpu, _cfg.CpuWarn, _cfg.CpuCrit))); break;
                case "ram" when _m != null:
                    items.Add(new("RAM", $"{_m.MemUsedPct:0}%  {_m.MemUsedGb:0.0} GB", Level(_m.MemUsedPct, _cfg.MemWarn, _cfg.MemCrit))); break;
                case "disk" when _m != null:
                    items.Add(new("Disk", $"{_m.DiskBusy:0}%  {Fmt.Bytes(_m.DiskReadBps + _m.DiskWriteBps)}/s", Level(_m.DiskBusy, 80, 95))); break;
                case "net" when _m != null:
                    items.Add(new("Netz", $"↓{Fmt.Bytes(_m.NetRxBps)}/s  ↑{Fmt.Bytes(_m.NetTxBps)}/s", Palette.Info)); break;
                case "topproc":
                    var top = _p.Where(x => x.Pid != 0).OrderByDescending(x => x.Cpu).FirstOrDefault();
                    if (top != null) items.Add(new("Top", $"{top.Name}  {top.Cpu:0}%", Level(top.Cpu, 40, 80))); break;
            }
            if (_s == null) continue;
            switch (key)
            {
                case "health":
                    var h = HealthMonitor.Overall(_s.Checks);
                    items.Add(new("Health", $"{HealthMonitor.Score(_s.Checks)}  {Loc.T(h == Health.Ok ? "gesund" : h == Health.Warning ? "Warnung" : "Problem")}",
                        HealthBrushConverter.For(h))); break;
                case "free" when _s.Disks.Count > 0:
                    var d = _s.Disks.OrderBy(x => x.FreePct).First();
                    items.Add(new("Frei", $"{d.Name} {d.FreePct:0}%  {d.FreeGb:0} GB",
                        d.FreePct <= _cfg.DiskFreeCritPct ? Palette.Crit : d.FreePct <= _cfg.DiskFreeWarnPct ? Palette.Warn : Palette.Ok)); break;
                case "dbconn" when _s.Databases.Count > 0:
                    var run = _s.Databases.Where(x => x.State == "Running").ToList();
                    items.Add(new("DB", run.Count == 0 ? "gestoppt" : $"{run.Sum(x => x.Connections)} Verb.  CPU {run.Sum(x => x.Cpu):0}%",
                        run.Count < _s.Databases.Count ? Palette.Crit : Palette.Ok)); break;
                case "rdp": items.Add(new("RDP", $"{_s.RdpActive} aktiv / {_s.RdpTotal}", Palette.Info)); break;
                case "smb": items.Add(new("SMB", $"{_s.SmbSessions} Clients", Palette.Info)); break;
                case "inet": items.Add(new("Internet", $"{_s.Connections.Count(c => c.Scope == NetworkCollector.ScopeInternet)} Verb.", Palette.Info)); break;
                case "backup": items.Add(new("Backup", _s.BackupRunning ? "läuft" : "–", _s.BackupRunning ? Palette.Info : Palette.Muted)); break;
                case "dhcp" when _s.Dhcp != null:
                    int free = _s.Dhcp.Scopes.Sum(x => x.ReallyFree), conf = _s.Dhcp.Scopes.Sum(x => x.Conflicts);
                    items.Add(new("DHCP", $"{free} frei" + (conf > 0 ? $"  {conf} Konflikt" : ""), conf > 0 ? Palette.Warn : Palette.Ok)); break;
                case "ad" when _s.Ad != null:
                    items.Add(new("AD", $"{_s.Ad.UsersLocked} gesperrt", _s.Ad.UsersLocked > 0 ? Palette.Warn : Palette.Ok)); break;
            }
        }
        return items;
    }

    static Brush Level(double v, double warn, double crit) => v >= crit ? Palette.Crit : v >= warn ? Palette.Warn : Palette.Ok;

    void Render()
    {
        if (!IsVisible) return;
        var items = Build();
        string sig = (_cfg.OverlayHorizontal ? "H" : "V") + string.Join("|", items.Select(i => $"{i.Label}={i.Value}={(i.Color as SolidColorBrush)?.Color}"));
        if (sig == _sig) return;
        _sig = sig;

        Rows.Children.Clear();
        if (_cfg.OverlayHorizontal)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items)
            {
                sp.Children.Add(new TextBlock { Text = Loc.T(i.Label) + " ", Foreground = Palette.Muted, Margin = new Thickness(sp.Children.Count > 0 ? 16 : 0, 0, 0, 0) });
                sp.Children.Add(new TextBlock { Text = i.Value, Foreground = i.Color, FontWeight = FontWeights.SemiBold });
            }
            Rows.Children.Add(sp);
        }
        else
        {
            foreach (var i in items)
            {
                var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                g.ColumnDefinitions.Add(new ColumnDefinition { SharedSizeGroup = "L", Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = Loc.T(i.Label), Foreground = Palette.Muted, Margin = new Thickness(0, 0, 14, 0) });
                var v = new TextBlock { Text = i.Value, Foreground = i.Color, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Left };
                Grid.SetColumn(v, 1);
                g.Children.Add(v);
                Rows.Children.Add(g);
            }
        }
        if (items.Count == 0)
            Rows.Children.Add(new TextBlock { Text = Loc.T("Keine Kennzahlen gewählt"), Foreground = Palette.Muted });
    }

    /// <summary>Für Screenshots: das Overlay als Bild.</summary>
    public System.Windows.Media.Imaging.BitmapSource Capture()
    {
        Frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Frame.Arrange(new Rect(Frame.DesiredSize));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(Frame.DesiredSize.Width), (int)Math.Ceiling(Frame.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(Frame);
        return rtb;
    }
}
