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
        _pages = Nav.Items.Cast<ListBoxItem>().Select(i => (FrameworkElement)FindName((string)i.Tag)).ToArray();

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
        if (App.Args.Contains("--demo"))
        {
            _mon.Demo = true;
            NavAd.Visibility = NavDhcp.Visibility = Visibility.Visible;
        }
        _ready = true;
        _mon.Start();
        Closed += (_, _) => _mon.Dispose();

        HandleCommandLine();
    }

    bool _ready;
    List<ConnRow> _conns = new();
    DhcpInfo? _dhcp;

    // ---------- Hilfen ----------
    static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            var r = FindScroll(c);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>Setzt die Datenquelle und behält die Scrollposition bei.</summary>
    static void SetItems(DataGrid g, System.Collections.IEnumerable items)
    {
        double off = FindScroll(g)?.VerticalOffset ?? 0;
        g.ItemsSource = items;
        if (off > 0)
            g.Dispatcher.BeginInvoke(new Action(() => FindScroll(g)?.ScrollToVerticalOffset(off)), DispatcherPriority.Loaded);
    }

    // ---------- Netzwerk ----------
    void OnNetMode(object sender, SelectionChangedEventArgs e) => ApplyNet();
    void OnNetFilter(object sender, TextChangedEventArgs e) => ApplyNet();

    void ApplyNet()
    {
        if (!_ready) return;
        IEnumerable<ConnRow> q = _conns;
        int mode = NetMode.SelectedIndex;
        q = mode switch
        {
            1 => q.Where(c => c.Scope == NetworkCollector.ScopeLan),
            2 => q.Where(c => c.Scope == NetworkCollector.ScopeInternet),
            3 => q.Where(c => c.Scope == NetworkCollector.ScopeLoop),
            4 => q.Where(c => c.Scope == NetworkCollector.ScopeListen),
            _ => q,
        };
        string f = NetFilter.Text.Trim();
        if (f.Length > 0)
            q = q.Where(c => c.Process.Contains(f, StringComparison.OrdinalIgnoreCase) || c.Local.Contains(f)
                          || c.Remote.Contains(f) || c.Pid.ToString() == f);
        var view = new ListCollectionView(q.ToList());
        if (mode == 0) view.GroupDescriptions.Add(new PropertyGroupDescription("Scope"));
        SetItems(NetGrid, view);
    }

    // ---------- Active Directory ----------
    void FillAd(AdInfo? ad)
    {
        NavAd.Visibility = ad != null || App.Args.Contains("--demo") ? Visibility.Visible : Visibility.Collapsed;
        if (ad == null) return;
        AdDot.Fill = HealthBrushConverter.For(ad.Status);
        AdHead.Text = "Active Directory" + (ad.Domain.Length > 0 ? " – " + ad.Domain : "");
        AdDemo.Visibility = ad.Demo ? Visibility.Visible : Visibility.Collapsed;
        AdSub.Text = $"Domänencontroller {ad.DcName}" + (ad.Site.Length > 0 ? $" · Standort {ad.Site}" : "")
                     + (ad.Level.Length > 0 ? $" · {ad.Level}" : "") + (ad.Error.Length > 0 ? $"\nHinweis: {ad.Error}" : "");
        AdFacts.Children.Clear();
        AddFact(AdFacts, "Benutzer gesamt", ad.Users.ToString());
        AddFact(AdFacts, "Aktiv", ad.UsersEnabled.ToString());
        AddFact(AdFacts, "Deaktiviert", ad.UsersDisabled.ToString());
        AddFact(AdFacts, "Gesperrt", ad.UsersLocked.ToString());
        AddFact(AdFacts, "Inaktiv (90 Tage)", ad.Inactive90.ToString());
        AddFact(AdFacts, "Kennwort läuft nie ab", ad.PasswordNeverExpires.ToString());
        AddFact(AdFacts, "Computer", ad.Computers.ToString());
        AddFact(AdFacts, "Gruppen", ad.Groups.ToString());
        AddFact(AdFacts, "Domänen-Admins", ad.DomainAdmins.ToString());
        AddFact(AdFacts, "SYSVOL / NETLOGON", ad.SysvolOk && ad.NetlogonOk ? "erreichbar" : "Problem");
        SetItems(AdSvcGrid, ad.Services);
        SetItems(AdReplGrid, ad.Replication);
        SetItems(AdFsmoGrid, ad.Fsmo);
    }

    // ---------- DHCP ----------
    void FillDhcp(Snapshot s)
    {
        var d = s.Dhcp;
        _dhcp = d;
        NavDhcp.Visibility = d != null || App.Args.Contains("--demo") ? Visibility.Visible : Visibility.Collapsed;
        if (d == null) return;
        var h = s.Checks.Where(c => c.Category == "DHCP").Aggregate(Health.Ok, (a, c) => c.Status is Health.Warning or Health.Critical ? HealthExt.Worst(a, c.Status) : a);
        DhcpDot.Fill = HealthBrushConverter.For(h);
        DhcpDemo.Visibility = d.Demo ? Visibility.Visible : Visibility.Collapsed;
        int total = d.Scopes.Sum(x => x.Total), really = d.Scopes.Sum(x => x.ReallyFree), conf = d.Scopes.Sum(x => x.Conflicts);
        DhcpHead.Text = d.ServiceRunning ? $"DHCP-Server – {d.Scopes.Count} Bereich(e)" : "DHCP-Server ist gestoppt";
        DhcpSub.Text = d.Error.Length > 0 ? "DHCP-Daten nicht lesbar: " + d.Error
            : $"Wirklich vergebbar: {really} von {total} Adressen" + (conf > 0 ? $" · {conf} Konflikt(e): antworten, obwohl DHCP sie als frei führt" : "")
              + (d.ScanTime.HasValue ? $" · letzter Scan {d.ScanTime:t}" : " · Scan ausstehend")
              + ".\nDie Zahl „wirklich frei“ = laut DHCP frei und im Scan (Ping und ARP) ohne Antwort.";
        ScanInfo.Text = d.Scanning ? "  " + d.ScanProgress : "";
        ScanButton.IsEnabled = !d.Scanning && !d.Demo;

        string? sel = (ScopeGrid.SelectedItem as DhcpScopeRow)?.ScopeId;
        ScopeGrid.ItemsSource = d.Scopes;
        if (sel != null) ScopeGrid.SelectedItem = d.Scopes.FirstOrDefault(x => x.ScopeId == sel);
        ApplyIpFilter();
    }

    void OnScopeSelected(object sender, SelectionChangedEventArgs e) => ApplyIpFilter();
    void OnIpFilter(object sender, SelectionChangedEventArgs e) => ApplyIpFilter();
    void OnIpFilterText(object sender, TextChangedEventArgs e) => ApplyIpFilter();

    void ApplyIpFilter()
    {
        if (!_ready || _dhcp == null) return;
        IEnumerable<IpRow> q = _dhcp.Addresses;
        if (ScopeGrid.SelectedItem is DhcpScopeRow sc) q = q.Where(a => a.Scope == sc.ScopeId);
        q = IpMode.SelectedIndex switch
        {
            1 => q.Where(a => a.Status == "Frei"),
            2 => q.Where(a => a.Status is "Konflikt" or "Abgelehnt"),
            3 => q.Where(a => a.Status == "Vergeben"),
            4 => q.Where(a => a.Status is "Reserviert" or "Dieser Server"),
            _ => q,
        };
        string f = IpFilter.Text.Trim();
        if (f.Length > 0)
            q = q.Where(a => a.Ip.Contains(f) || a.Host.Contains(f, StringComparison.OrdinalIgnoreCase) || a.Mac.Contains(f, StringComparison.OrdinalIgnoreCase));
        SetItems(IpGrid, q.OrderBy(a => a.Sort).ToList());
    }

    void OnDhcpScan(object sender, RoutedEventArgs e)
    {
        _mon.ScanDhcpNow();
        ScanInfo.Text = "  Scan wird gestartet …";
        Task.Run(() => _mon.HealthOnce());
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
        AddFact(Facts, "davon ins Internet", s.Connections.Count(c => c.Scope == NetworkCollector.ScopeInternet).ToString());

        // Netzwerk
        _conns = s.Connections;
        NetFacts.Children.Clear();
        AddFact(NetFacts, "Lokales Netz (LAN)", _conns.Count(c => c.Scope == NetworkCollector.ScopeLan).ToString());
        AddFact(NetFacts, "Internet", _conns.Count(c => c.Scope == NetworkCollector.ScopeInternet).ToString());
        AddFact(NetFacts, "Gegenstellen im Internet", _conns.Where(c => c.Scope == NetworkCollector.ScopeInternet).Select(c => c.Remote[..c.Remote.LastIndexOf(':')]).Distinct().Count().ToString());
        AddFact(NetFacts, "Innerhalb dieses Rechners", _conns.Count(c => c.Scope == NetworkCollector.ScopeLoop).ToString());
        AddFact(NetFacts, "Lauschende Ports", _conns.Count(c => c.Scope == NetworkCollector.ScopeListen).ToString());
        ApplyNet();
        FillAd(s.Ad);
        FillDhcp(s);

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
