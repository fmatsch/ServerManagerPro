using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using ServerManagerPro.Core;

namespace ServerManagerPro;

public partial class App : Application
{
    public static string[] Args = Array.Empty<string>();
    public static AppSettings Settings = new();
    public static HealthMonitor Monitor = null!;
    public static OverlayWindow Overlay = null!;
    public static MainWindow MainWin = null!;
    public static AlertEngine Alerts = null!;
    public static RemoteMonitor Remotes = null!;
    public static string ServeError = "";
    static StatusServer? _serve;

    static System.Windows.Forms.NotifyIcon? _tray;
    static System.Windows.Forms.ToolStripMenuItem? _miOverlay;
    static bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        Args = e.Args;
        // Kopfloser Selbsttest: ServerManagerPro.exe --selftest <datei.txt>
        int i = Array.IndexOf(Args, "--selftest");
        if (i >= 0)
        {
            string path = i + 1 < Args.Length ? Args[i + 1] : "selftest.txt";
            SelfTest(path);
            Shutdown();
            return;
        }
        int sc = Array.IndexOf(Args, "--scantest");
        if (sc >= 0 && sc + 2 < Args.Length)
        {
            // --scantest 192.168.1.1-192.168.1.254 datei.txt
            var parts = Args[sc + 1].Split('-');
            var res = Task.Run(() => DhcpCollector.QuickScan(DhcpCollector.U(parts[0]), DhcpCollector.U(parts[1]))).GetAwaiter().GetResult();
            File.WriteAllLines(Args[sc + 2], res.Select(r => $"{r.ip}\t{(r.mac.Length > 0 ? r.mac : "(nur Ping)")}"));
            Shutdown();
            return;
        }

        int al = Array.IndexOf(Args, "--alerttest"), sv = Array.IndexOf(Args, "--servetest");
        if ((al >= 0 && al + 1 < Args.Length) || (sv >= 0 && sv + 1 < Args.Length))
        {
            string text;
            try { text = al >= 0 ? Task.Run(SelfChecks.AlertTest).GetAwaiter().GetResult() : Task.Run(SelfChecks.ServeTest).GetAwaiter().GetResult(); }
            catch (Exception ex) { text = "Fehler: " + ex; }
            File.WriteAllText(Args[(al >= 0 ? al : sv) + 1], text);
            Shutdown();
            return;
        }
        int ut = Array.IndexOf(Args, "--updatetest");
        if (ut >= 0 && ut + 1 < Args.Length)
        {
            string text;
            try
            {
                var latest = Task.Run(UpdateChecker.LatestAsync).GetAwaiter().GetResult();
                text = latest == null ? "keine Version gefunden"
                    : $"Neueste: {latest.Tag} | laufend: {UpdateChecker.Current} | Update: {(latest.Version > UpdateChecker.Current ? "ja" : "nein")} | Setup: {latest.SetupName ?? "–"} | Summen: {(latest.SumsUrl != null ? "ja" : "–")} | Installiert: {UpdateChecker.IsInstalledCopy}";
            }
            catch (Exception ex) { text = "Fehler: " + ex.GetBaseException().Message; }
            File.WriteAllText(Args[ut + 1], text);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Settings = AppSettings.Load();
        int li = Array.IndexOf(Args, "--lang");
        if (li >= 0 && li + 1 < Args.Length) Settings.Language = Args[li + 1];
        Loc.Init(Settings.Language);
        bool overlayOnly = Args.Contains("--overlay");
        if (Args.Contains("--with-overlay"))
            Settings.OverlayMetrics = new() { "health", "cpu", "ram", "disk", "net", "free", "topproc", "backup", "dhcp" };

        Monitor = new HealthMonitor(Settings) { Demo = Args.Contains("--demo") };
        Alerts = new AlertEngine(Settings);
        Alerts.Tray += (title, text) => Current.Dispatcher.BeginInvoke(() =>
            _tray?.ShowBalloonTip(10000, title, text, title.Contains("Kritisch") || title.Contains("Critical") ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Warning));
        Monitor.HealthUpdated += s => Alerts.Process(Environment.MachineName, s.Checks);
        Remotes = new RemoteMonitor(Settings, Alerts);
        Overlay = new OverlayWindow(Monitor, Settings);
        Overlay.ToggleRequested += ToggleOverlay;
        Overlay.MainRequested += ShowMain;
        MainWin = new MainWindow();
        MainWin.IsVisibleChanged += (_, _) =>
        {
            UpdateDemand();
            if (MainWin.IsVisible) { Monitor.RefreshNow(); Task.Run(() => Monitor.HealthOnce()); }
        };
        MainWin.Closing += OnMainClosing;
        CreateTray();

        if (!overlayOnly) MainWin.Show();
        if (overlayOnly || Settings.OverlayOnStart || Args.Contains("--with-overlay")) Overlay.ShowOverlay();
        UpdateDemand();
        ApplyServe();
        Monitor.Start();
        Remotes.Start();
    }

    /// <summary>Abmelden/Herunterfahren darf nie vom "Ins Tray schließen" blockiert werden.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _serve?.Dispose();
        Remotes?.Dispose();
        Monitor?.Dispose();
        base.OnExit(e);
    }

    // ---------- Bedarfssteuerung ----------
    /// <summary>Misst nur, was gerade angezeigt wird – bei geschlossenem Hauptfenster und einfachem Overlay bleibt fast nichts übrig.</summary>
    public static void UpdateDemand()
    {
        if (Monitor == null) return;
        bool main = MainWin != null && MainWin.IsVisible, ov = Overlay != null && Overlay.IsShown;
        var sel = OverlayWindow.Catalog.Where(c => Settings.OverlayMetrics.Contains(c.key)).ToList();
        // Alarme und Status-Zugriff brauchen die Gesundheitsprüfung auch ohne sichtbares Fenster
        Monitor.NeedHealth = main || (ov && sel.Any(c => c.needsHealth)) || Settings.AlertsEnabled || Settings.ServeEnabled;
        Monitor.NeedProcesses = main || (ov && sel.Any(c => c.needsProcs));
        Monitor.Relaxed = !main;
        Monitor.IncludeCores = main;
        // Im Hintergrund (nur Overlay) niemals der Arbeit Rechenzeit wegnehmen
        try { Process.GetCurrentProcess().PriorityClass = main ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal; } catch { }
    }

    // ---------- Overlay und Fenster ----------
    public static void ToggleOverlay()
    {
        if (Overlay.IsShown) Overlay.HideOverlay(); else Overlay.ShowOverlay();
        UpdateDemand();
        if (Overlay.IsShown) { Monitor.RefreshNow(); Task.Run(() => Monitor.HealthOnce()); }
    }

    public static void ShowMain()
    {
        MainWin.Show();
        if (MainWin.WindowState == WindowState.Minimized) MainWin.WindowState = WindowState.Normal;
        MainWin.Activate();
    }

    static void OnMainClosing(object? s, CancelEventArgs e)
    {
        if (_exiting) return;
        // Läuft das Overlay, bleibt die App im Hintergrund (Tray); sonst beendet das Schließen alles.
        if (Overlay.IsShown) { e.Cancel = true; MainWin.Hide(); }
        else Quit();
    }

    public static void Quit()
    {
        _exiting = true;
        try { Overlay.Close(); } catch { }
        Current.Shutdown();
    }

    // ---------- Status-Zugriff ----------
    public static string NewToken() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Startet oder stoppt den schreibgeschützten Status-Zugriff gemäß den Einstellungen.</summary>
    public static void ApplyServe()
    {
        _serve?.Dispose(); _serve = null; ServeError = "";
        if (!Settings.ServeEnabled) return;
        try
        {
            if (string.IsNullOrEmpty(Settings.ServeToken)) { Settings.ServeToken = NewToken(); Settings.Save(); }
            if (!System.Net.IPAddress.TryParse(Settings.ServeBind, out var ip)) ip = System.Net.IPAddress.Loopback;
            var cert = StatusCert.GetOrCreate();
            _serve = new StatusServer(ip, Settings.ServePort, Settings.ServeToken, cert,
                () => Monitor.Latest is { } s ? StatusPayload.From(s, Monitor.LastMetrics) : null);
            _serve.Start();
        }
        catch (Exception ex) { ServeError = ex.GetBaseException().Message; _serve = null; }
    }

    // ---------- Tray ----------
    static void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _miOverlay = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Overlay anzeigen   (Strg+Alt+O)"));
        _miOverlay.Click += (_, _) => ToggleOverlay();
        var miMove = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Overlay verschieben"));
        miMove.Click += (_, _) => Overlay.ToggleMove();
        var miMain = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Hauptfenster öffnen   (Strg+Alt+M)"));
        miMain.Click += (_, _) => ShowMain();
        var miQuit = new System.Windows.Forms.ToolStripMenuItem(Loc.T("Beenden"));
        miQuit.Click += (_, _) => Quit();
        menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { _miOverlay, miMove, miMain, new System.Windows.Forms.ToolStripSeparator(), miQuit });
        menu.Opening += (_, _) => _miOverlay!.Checked = Overlay.IsShown;

        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { icon = System.Drawing.SystemIcons.Application; }
        _tray = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "ServerManagerPro", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowMain();
        Monitor.HealthUpdated += s => Current.Dispatcher.BeginInvoke(() =>
        {
            if (_tray == null) return;
            string t = $"ServerManagerPro – Gesundheit {HealthMonitor.Score(s.Checks)}/100";
            _tray.Text = t.Length > 63 ? t[..63] : t;
        });
    }

    // ---------- Autostart (nur Overlay im Hintergrund) ----------
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Autostart
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("ServerManagerPro") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("ServerManagerPro", $"\"{Environment.ProcessPath}\" --overlay");
            else k.DeleteValue("ServerManagerPro", false);
        }
    }

    static void SelfTest(string path)
    {
        var sb = new StringBuilder();
        using var mon = new HealthMonitor(AppSettings.Load());
        var rt = new AppSettings { DbConnections = new() { "mysql||Server=x;Password=geheim" } };
        string json = System.Text.Json.JsonSerializer.Serialize(rt);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        sb.AppendLine($"Einstellungen verschlüsselt gespeichert: {(!json.Contains("geheim") && back.DbConnections.SequenceEqual(rt.DbConnections) ? "OK" : "FEHLER")}");
        var sw = Stopwatch.StartNew();
        mon.FastOnce();
        Thread.Sleep(1500);
        var m = mon.FastOnce();
        sb.AppendLine($"CPU {m.Cpu:0.0}% | RAM {m.MemUsedGb:0.0}/{m.MemTotalGb:0.0} GB | Disk {m.DiskBusy:0}% | Net ↓{Fmt.Bytes(m.NetRxBps)}/s ↑{Fmt.Bytes(m.NetTxBps)}/s | Kerne {m.Cores.Length} | Prozesse {m.Processes}");
        var sw2 = Stopwatch.StartNew();
        for (int k = 0; k < 10; k++) mon.FastOnce();
        sb.AppendLine($"Messzyklus (Leistung + Prozesse): {sw2.ElapsedMilliseconds / 10.0:0} ms");
        mon.NeedProcesses = false;
        var sw3 = Stopwatch.StartNew();
        for (int k = 0; k < 10; k++) mon.FastOnce();
        sb.AppendLine($"Messzyklus nur Leistung (Overlay): {sw3.ElapsedMilliseconds / 10.0:0} ms");
        mon.NeedProcesses = true;
        var s = mon.HealthOnce();
        for (int w = 0; w < 120 && !mon.ExtraReady; w++) Thread.Sleep(1000); // Zusatzprüfungen laufen im Hintergrund
        s = mon.HealthOnce();
        sb.AppendLine($"Admin: {s.IsAdmin} | Gesundheit: {HealthMonitor.Score(s.Checks)} ({HealthMonitor.Overall(s.Checks)}) | {sw.ElapsedMilliseconds} ms");
        foreach (var g in s.Checks.GroupBy(c => c.Category))
        {
            sb.AppendLine($"\n[{g.Key}]");
            foreach (var c in g) sb.AppendLine($"  {c.Status,-9} {c.Name}: {c.Detail}");
        }
        foreach (var sec in s.Sections)
        {
            sb.AppendLine($"\n<{sec.Group}> {sec.Title} [{sec.Status}]");
            sb.AppendLine("  " + string.Join(" | ", sec.Headers));
            foreach (var r in sec.Rows.Take(6)) sb.AppendLine("  " + string.Join(" | ", r));
        }
        sb.AppendLine($"\nDatenbanken: {s.Databases.Count} | Sicherungen: {s.Backups.Count} | Aufgaben: {s.Tasks.Count} | Sitzungen: {s.Sessions.Count} | Ereignisse: {s.Events.Count}");
        File.WriteAllText(path, sb.ToString());
    }
}
