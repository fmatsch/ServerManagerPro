using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ServerManagerPro.Controls;
using ServerManagerPro.Core;

namespace ServerManagerPro;

public sealed class CoreVm : Observable
{
    double _v;
    public string Label { get; init; } = "";
    public double Value { get => _v; set => Set(ref _v, value); }
}

public partial class MainWindow : Window
{
    readonly AppSettings _cfg = AppSettings.Load();
    readonly HealthMonitor _mon;
    readonly ObservableCollection<ProcessRow> _procs = new();
    readonly ObservableCollection<ServiceRow> _services = new();
    readonly ObservableCollection<CoreVm> _cores = new();
    readonly ListCollectionView _procView, _svcView;
    readonly FrameworkElement[] _pages;
    Snapshot? _last;

    public MainWindow()
    {
        InitializeComponent();
        _pages = new FrameworkElement[] { PageOverview, PagePerf, PageProcs, PageServices, PageDb, PageBackup, PageClients, PageEvents, PageSettings };

        _procView = new ListCollectionView(_procs) { IsLiveSorting = true };
        foreach (var p in new[] { "Cpu", "MemMb", "Name", "Pid", "Threads", "Session" }) _procView.LiveSortingProperties.Add(p);
        _procView.SortDescriptions.Add(new SortDescription("Cpu", ListSortDirection.Descending));
        ProcGrid.ItemsSource = _procView;

        _svcView = new ListCollectionView(_services);
        SvcGrid.ItemsSource = _svcView;
        CoreList.ItemsSource = _cores;

        HostText.Text = Environment.MachineName;
        OsText.Text = OsDescription();
        if (!Native.IsAdmin()) AdminButton.Visibility = Visibility.Visible;
        LoadSettingsUi();

        _mon = new HealthMonitor(_cfg);
        _mon.MetricsUpdated += m => Dispatcher.BeginInvoke(() => OnMetrics(m));
        _mon.ProcessesUpdated += p => Dispatcher.BeginInvoke(() => OnProcesses(p));
        _mon.ServicesUpdated += s => Dispatcher.BeginInvoke(() => OnServices(s));
        _mon.HealthUpdated += s => Dispatcher.BeginInvoke(() => OnHealth(s));
        _mon.Start();
        Closed += (_, _) => _mon.Dispose();

        HandleCommandLine();
    }

    static string OsDescription()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string name = k?.GetValue("ProductName")?.ToString() ?? "Windows";
            string build = k?.GetValue("CurrentBuildNumber")?.ToString() ?? "";
            string ver = k?.GetValue("DisplayVersion")?.ToString() ?? k?.GetValue("ReleaseId")?.ToString() ?? "";
            if (int.TryParse(build, out int b) && b >= 22000 && name.Contains("Windows 10")) name = name.Replace("Windows 10", "Windows 11");
            return $"{name} {ver} (Build {build}) · {Environment.ProcessorCount} logische Prozessoren · {(Native.IsAdmin() ? "Administrator" : "Standardbenutzer")}";
        }
        catch { return Environment.OSVersion.ToString(); }
    }

    // ---------- Kommandozeile für Screenshots ----------
    void HandleCommandLine()
    {
        var a = App.Args;
        int pi = Array.IndexOf(a, "--page");
        if (pi >= 0 && pi + 1 < a.Length && int.TryParse(a[pi + 1], out int page)) Nav.SelectedIndex = Math.Clamp(page, 0, _pages.Length - 1);
        int si = Array.IndexOf(a, "--screenshot");
        if (si >= 0 && si + 1 < a.Length)
        {
            string path = a[si + 1];
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(14) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var root = (FrameworkElement)Content;
                var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
                Application.Current.Shutdown();
            };
            t.Start();
        }
    }

    // ---------- Navigation ----------
    void OnNav(object sender, SelectionChangedEventArgs e)
    {
        if (_pages == null) return;
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnRefresh(object sender, RoutedEventArgs e)
    {
        _mon.RefreshNow();
        Task.Run(() => _mon.HealthOnce());
    }

    void OnElevate(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { Verb = "runas", UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { /* UAC abgebrochen */ }
    }

    // ---------- Leistung ----------
    static string Rate(double bps) => Fmt.Bytes(bps) + "/s";

    void OnMetrics(MetricsSnapshot m)
    {
        TileCpu.Text = $"{m.Cpu:0}%";
        TileMem.Text = $"{m.MemUsedPct:0}%  ·  {m.MemUsedGb:0.0} GB";
        TileDisk.Text = $"{m.DiskBusy:0}%  ·  {Rate(m.DiskReadBps + m.DiskWriteBps)}";
        TileNet.Text = $"↓ {Rate(m.NetRxBps)}";
        TileCpuGraph.Push(m.Cpu); TileMemGraph.Push(m.MemUsedPct); TileDiskGraph.Push(m.DiskBusy); TileNetGraph.Push(m.NetRxBps + m.NetTxBps);

        PerfCpuGraph.Push(m.Cpu); PerfMemGraph.Push(m.MemUsedPct); PerfDiskGraph.Push(m.DiskBusy); PerfNetGraph.Push(m.NetRxBps + m.NetTxBps);
        PerfCpuText.Text = $"{m.Cpu:0.0}%";
        PerfCpuSub.Text = $"{m.Processes} Prozesse · {m.Threads} Threads · {m.Cores.Length} logische Prozessoren";
        PerfMemText.Text = $"{m.MemUsedPct:0}%";
        PerfMemSub.Text = $"{m.MemUsedGb:0.0} GB von {m.MemTotalGb:0.0} GB belegt";
        PerfDiskText.Text = $"{m.DiskBusy:0}%";
        PerfDiskSub.Text = $"Lesen {Rate(m.DiskReadBps)} · Schreiben {Rate(m.DiskWriteBps)}";
        PerfNetText.Text = Rate(m.NetRxBps + m.NetTxBps);
        PerfNetSub.Text = $"Empfangen {Rate(m.NetRxBps)} · Senden {Rate(m.NetTxBps)}";

        while (_cores.Count < m.Cores.Length) _cores.Add(new CoreVm { Label = $"CPU {_cores.Count}" });
        for (int i = 0; i < m.Cores.Length; i++) _cores[i].Value = m.Cores[i];
    }

    // ---------- Prozesse ----------
    void OnProcesses(List<ProcRecord> list)
    {
        var byPid = _procs.ToDictionary(p => p.Pid);
        var alive = new HashSet<int>();
        foreach (var r in list)
        {
            alive.Add(r.Pid);
            if (!byPid.TryGetValue(r.Pid, out var row))
            {
                _procs.Add(new ProcessRow { Pid = r.Pid, Name = r.Name, Cpu = r.Cpu, MemMb = r.MemMb, Threads = r.Threads, Session = r.Session });
            }
            else { row.Cpu = r.Cpu; row.MemMb = r.MemMb; row.Threads = r.Threads; }
        }
        for (int i = _procs.Count - 1; i >= 0; i--) if (!alive.Contains(_procs[i].Pid)) _procs.RemoveAt(i);
        ProcInfo.Text = $"{_procs.Count} Prozesse";
    }

    void OnProcFilter(object sender, TextChangedEventArgs e)
    {
        string f = ProcFilter.Text.Trim();
        _procView.Filter = string.IsNullOrEmpty(f) ? null
            : o => o is ProcessRow p && (p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Pid.ToString() == f);
    }

    void OnKillProcess(object sender, RoutedEventArgs e)
    {
        if (ProcGrid.SelectedItem is not ProcessRow p) return;
        if (MessageBox.Show(this, $"Prozess „{p.Name}“ (PID {p.Pid}) beenden?\nNicht gespeicherte Daten gehen verloren.", "Prozess beenden",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { using var pr = Process.GetProcessById(p.Pid); pr.Kill(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Beenden nicht möglich", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ---------- Dienste ----------
    void OnServices(List<ServiceRow> list)
    {
        var byName = _services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in list)
        {
            alive.Add(r.Name);
            if (!byName.TryGetValue(r.Name, out var row)) _services.Add(r);
            else { row.State = r.State; row.StartMode = r.StartMode; row.Pid = r.Pid; row.Info = r.Info; row.DisplayName = r.DisplayName; }
        }
        for (int i = _services.Count - 1; i >= 0; i--) if (!alive.Contains(_services[i].Name)) _services.RemoveAt(i);
        SvcInfo.Text = $"{_services.Count(s => s.IsRunning)} von {_services.Count} laufen";
    }

    void OnSvcFilter(object sender, TextChangedEventArgs e)
    {
        string f = SvcFilter.Text.Trim();
        _svcView.Filter = string.IsNullOrEmpty(f) ? null
            : o => o is ServiceRow s && (s.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || s.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    void OnSvcStart(object sender, RoutedEventArgs e) => ControlService("start");
    void OnSvcStop(object sender, RoutedEventArgs e) => ControlService("stop");
    void OnSvcRestart(object sender, RoutedEventArgs e) => ControlService("restart");

    async void ControlService(string action)
    {
        if (SvcGrid.SelectedItem is not ServiceRow s) return;
        if (action != "start" && MessageBox.Show(this, $"Dienst „{s.DisplayName}“ {(action == "stop" ? "beenden" : "neu starten")}?", "Dienst steuern",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SvcInfo.Text = "Bitte warten …";
        string err = await Task.Run(() => ServiceCollector.Control(s.Name, action));
        if (err.Length > 0) MessageBox.Show(this, err + (Native.IsAdmin() ? "" : "\n\nTipp: Starten Sie die Anwendung als Administrator."), "Dienst steuern", MessageBoxButton.OK, MessageBoxImage.Warning);
        _ = Task.Run(() => _mon.HealthOnce());
    }

    // ---------- Gesundheit ----------
    static readonly string[] Order = { "Leistung", "System", "Dienste", "Datenbanken", "Sicherung", "Clients", "Ereignisse" };

    void OnHealth(Snapshot s)
    {
        _last = s;
        var overall = HealthMonitor.Overall(s.Checks);
        int score = HealthMonitor.Score(s.Checks);
        OverallDot.Fill = HealthBrushConverter.For(overall == Health.Ok ? Health.Ok : overall);
        OverallText.Text = overall switch { Health.Ok => "Server gesund", Health.Warning => "Warnungen", _ => "Probleme erkannt" };
        int bad = s.Checks.Count(c => c.Status is Health.Warning or Health.Critical);
        ScoreText.Text = $"Gesundheit {score}/100 · {(bad == 0 ? "alles in Ordnung" : bad + " Auffälligkeit(en)")}";

        Groups.ItemsSource = s.Checks.GroupBy(c => c.Category)
            .OrderBy(g => { int i = Array.IndexOf(Order, g.Key); return i < 0 ? 99 : i; })
            .Select(g => new CategoryGroup
            {
                Title = g.Key,
                Status = g.Aggregate(Health.Ok, (a, c) => c.Status is Health.Warning or Health.Critical ? HealthExt.Worst(a, c.Status) : a),
                Items = g.OrderByDescending(c => c.Status is Health.Critical ? 3 : c.Status is Health.Warning ? 2 : 0).ToList(),
            }).ToList();

        Facts.Children.Clear();
        var up = TimeSpan.FromMilliseconds(Native.GetTickCount64());
        AddFact(Facts, "Laufzeit", $"{(int)up.TotalDays} T {up.Hours} Std.");
        AddFact(Facts, "Remote-Sitzungen", $"{s.RdpActive} aktiv / {s.RdpTotal}");
        AddFact(Facts, "Dateiserver-Clients", s.SmbSessions.ToString());
        AddFact(Facts, "Datenbankverbindungen", s.Databases.Sum(d => d.Connections).ToString());
        AddFact(Facts, "Datenbankdienste", $"{s.Databases.Count(d => d.State == "Running")} von {s.Databases.Count} laufen");
        AddFact(Facts, "Sicherung", s.BackupRunning ? "läuft gerade" : "läuft nicht");
        AddFact(Facts, "TCP-Verbindungen", s.TcpEstablished.ToString());

        // Datenbanken
        DbList.ItemsSource = s.Databases;
        DbEmpty.Visibility = s.Databases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Sicherung
        BackupGrid.ItemsSource = s.Backups;
        TaskGrid.ItemsSource = s.Tasks;
        var bchecks = s.Checks.Where(c => c.Category == "Sicherung").ToList();
        var bh = bchecks.Aggregate(Health.Ok, (a, c) => c.Status is Health.Warning or Health.Critical ? HealthExt.Worst(a, c.Status) : a);
        BackupDot.Fill = HealthBrushConverter.For(s.BackupRunning ? Health.Info : bh);
        BackupHead.Text = s.BackupRunning ? "Eine Sicherung läuft gerade" : bh == Health.Ok ? "Sicherung in Ordnung" : "Sicherung prüfen";
        BackupSub.Text = string.Join("  ·  ", bchecks.Where(c => c.Name != "Sicherung aktiv").Select(c => c.Detail).Take(2));

        // Clients
        ClientFacts.Children.Clear();
        AddFact(ClientFacts, "Remote-Sitzungen aktiv", s.RdpActive.ToString());
        AddFact(ClientFacts, "Getrennt", (s.RdpTotal - s.RdpActive).ToString());
        AddFact(ClientFacts, "SMB-Clients", s.SmbSessions.ToString());
        AddFact(ClientFacts, "TCP-Verbindungen", s.TcpEstablished.ToString());
        ClientGrid.ItemsSource = s.Sessions;

        EventGrid.ItemsSource = s.Events;
        DiskList.ItemsSource = s.Disks;
        if (!s.IsAdmin) SettingsInfo.Text = "Hinweis: Ohne Administratorrechte sind einige Messwerte nicht verfügbar.";
    }

    static void AddFact(Panel panel, string label, string value)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 36, 4) };
        sp.Children.Add(new TextBlock { Text = label, Foreground = (Brush)Application.Current.FindResource("MutedBrush"), FontSize = 11 });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(sp);
    }

    // ---------- Einstellungen ----------
    void LoadSettingsUi()
    {
        SCpuW.Text = _cfg.CpuWarn.ToString("0"); SCpuC.Text = _cfg.CpuCrit.ToString("0");
        SMemW.Text = _cfg.MemWarn.ToString("0"); SMemC.Text = _cfg.MemCrit.ToString("0");
        SDiskW.Text = _cfg.DiskFreeWarnPct.ToString("0"); SDiskC.Text = _cfg.DiskFreeCritPct.ToString("0");
        SBackup.Text = _cfg.BackupMaxAgeHours.ToString();
        SEvW.Text = _cfg.EventWarnCount.ToString(); SEvC.Text = _cfg.EventCritCount.ToString();
        SRefresh.Text = _cfg.RefreshSeconds.ToString();
        SWatched.Text = string.Join(Environment.NewLine, _cfg.WatchedServices);
        SDbExtra.Text = string.Join(Environment.NewLine, _cfg.ExtraDatabaseServices);
        SBackupWords.Text = string.Join(Environment.NewLine, _cfg.ExtraBackupKeywords);
    }

    static List<string> Lines(string t) => t.Split('\n', '\r').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    static double D(string t, double def) => double.TryParse(t, out var v) ? v : def;
    static int I(string t, int def) => int.TryParse(t, out var v) ? v : def;

    void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        _cfg.CpuWarn = D(SCpuW.Text, 85); _cfg.CpuCrit = D(SCpuC.Text, 95);
        _cfg.MemWarn = D(SMemW.Text, 85); _cfg.MemCrit = D(SMemC.Text, 95);
        _cfg.DiskFreeWarnPct = D(SDiskW.Text, 15); _cfg.DiskFreeCritPct = D(SDiskC.Text, 5);
        _cfg.BackupMaxAgeHours = Math.Max(1, I(SBackup.Text, 36));
        _cfg.EventWarnCount = I(SEvW.Text, 10); _cfg.EventCritCount = I(SEvC.Text, 100);
        _cfg.RefreshSeconds = Math.Clamp(I(SRefresh.Text, 2), 1, 60);
        _cfg.WatchedServices = Lines(SWatched.Text);
        _cfg.ExtraDatabaseServices = Lines(SDbExtra.Text);
        _cfg.ExtraBackupKeywords = Lines(SBackupWords.Text);
        _cfg.Save();
        SettingsInfo.Text = "Gespeichert in " + AppSettings.FilePath;
        OnRefresh(sender, e);
    }

    void OnExport(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        var dlg = new SaveFileDialog { Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt", FileName = $"Serverbericht-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.md" };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder();
        sb.AppendLine($"# Serverbericht {Environment.MachineName}");
        sb.AppendLine($"_{_last.Time:g} · {OsText.Text}_\n");
        sb.AppendLine($"**Gesundheit: {HealthMonitor.Score(_last.Checks)}/100 – {HealthMonitor.Overall(_last.Checks).Label()}**\n");
        foreach (var g in _last.Checks.GroupBy(c => c.Category))
        {
            sb.AppendLine($"## {g.Key}");
            foreach (var c in g) sb.AppendLine($"- **{c.Status.Label()}** – {c.Name}: {c.Detail}");
            sb.AppendLine();
        }
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        SettingsInfo.Text = "Bericht gespeichert: " + dlg.FileName;
    }
}
