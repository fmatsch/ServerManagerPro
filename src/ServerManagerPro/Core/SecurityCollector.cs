using System.Management;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace ServerManagerPro.Core;

/// <summary>Sicherheitsprüfungen: Updates, Virenschutz, Firewall, BitLocker, Zertifikate, offene Ports. Alles nur lesend.</summary>
public static class SecurityCollector
{
    const string Cat = "Sicherheit", Group = "Sicherheit";

    public static (List<DetailSection> sections, List<CheckResult> checks) Collect(
        List<ServiceRow> services, List<Native.TcpConn> tcp, List<ProcRecord> procs, AppSettings cfg)
    {
        var sections = new List<DetailSection>();
        var checks = new List<CheckResult>();
        var needAdmin = new List<string>();
        void Try(Action a, string name)
        {
            try { a(); }
            catch (Exception ex) when (Native.IsAccessDenied(ex)) { needAdmin.Add(name); }
            catch (Exception ex) { checks.Add(new CheckResult(Cat, name, Health.Unknown, ex.GetBaseException().Message)); }
        }
        Try(() => Updates(sections, checks), "Windows-Updates");
        Try(() => Defender(sections, checks), "Virenschutz");
        Try(() => Firewall(sections, checks), "Firewall");
        Try(() => BitLocker(sections), "BitLocker");
        Try(() => Certificates(sections, checks, cfg), "Zertifikate");
        Try(() => Ports(sections, checks, tcp, procs), "Offene Ports");
        if (needAdmin.Count > 0)
            checks.Add(new CheckResult(Cat, "Nur mit Administratorrechten", Health.Info, "Nicht lesbar ohne Administratorrechte: " + string.Join(", ", needAdmin) + "."));
        return (sections, checks);
    }

    // ---------- Windows Update ----------
    static (int count, int important, List<string[]> list, string error, DateTime at)? _wu;
    static DateTime? _wuLastInstall;
    static bool _wuRunning;

    static void StartUpdateSearch()
    {
        if (_wuRunning) return;
        _wuRunning = true;
        var th = new Thread(() =>
        {
            try
            {
                dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.Online = false; // nur Daten des letzten Scans – kein Netzwerkzugriff
                try
                {
                    int total = searcher.GetTotalHistoryCount();
                    dynamic hist = searcher.QueryHistory(0, Math.Min(total, 80));
                    for (int i = 0; i < hist.Count; i++)
                    {
                        dynamic h = hist.Item(i);
                        if ((int)h.Operation == 1 && (int)h.ResultCode == 2) { _wuLastInstall = ((DateTime)h.Date).ToLocalTime(); break; }
                    }
                }
                catch { }
                dynamic res = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
                int n = res.Updates.Count, important = 0;
                var list = new List<string[]>();
                for (int i = 0; i < n; i++)
                {
                    dynamic u = res.Updates.Item(i);
                    string sev = "";
                    try { sev = (string)u.MsrcSeverity; } catch { }
                    if (sev is "Critical" or "Important") important++;
                    if (list.Count < 40) list.Add(new[] { (string)u.Title, string.IsNullOrEmpty(sev) ? "–" : sev });
                }
                _wu = (n, important, list, "", DateTime.Now);
            }
            catch (Exception ex) { _wu = (0, 0, new(), ex.GetBaseException().Message, DateTime.Now); }
            finally { _wuRunning = false; }
        }) { IsBackground = true };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
    }

    static DateTime? RegTime(string sub, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(sub);
        var v = k?.GetValue(name)?.ToString();
        return v != null && DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime() : null;
    }

    static void Updates(List<DetailSection> sections, List<CheckResult> checks)
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\";
        var detect = RegTime(root + "Detect", "LastSuccessTime");
        var install = RegTime(root + "Install", "LastSuccessTime") ?? _wuLastInstall;
        if (_wu == null || (DateTime.Now - _wu.Value.at).TotalHours > 6) StartUpdateSearch();

        var sec = new DetailSection { Group = Group, Title = "Windows-Updates", Headers = new[] { "Eigenschaft", "Wert" } };
        sec.Add("Letzte Suche nach Updates", detect?.ToString("g") ?? "unbekannt");
        sec.Add("Letzte erfolgreiche Installation", install?.ToString("g") ?? "unbekannt");
        var st = Health.Ok;
        if (install.HasValue)
        {
            var age = (DateTime.Now - install.Value).TotalDays;
            var h = age > 90 ? Health.Critical : age > 45 ? Health.Warning : Health.Ok;
            checks.Add(new CheckResult(Cat, "Letzte Update-Installation", h, $"{install:d} (vor {age:0} Tagen)."));
            st = HealthExt.Worst(st, h);
        }
        if (detect.HasValue && (DateTime.Now - detect.Value).TotalDays > 14)
        {
            checks.Add(new CheckResult(Cat, "Update-Suche", Health.Warning, $"Die letzte erfolgreiche Suche war am {detect:d}."));
            st = HealthExt.Worst(st, Health.Warning);
        }
        if (_wu is { } w)
        {
            if (w.error.Length > 0) sec.Add("Ausstehende Updates", "nicht lesbar: " + w.error);
            else
            {
                sec.Add("Ausstehende Updates (laut letztem Scan)", $"{w.count}" + (w.important > 0 ? $", davon {w.important} wichtig/kritisch" : ""));
                if (w.important > 0)
                {
                    checks.Add(new CheckResult(Cat, "Ausstehende Updates", Health.Warning, $"{w.count} Update(s) warten auf die Installation, {w.important} davon wichtig oder kritisch."));
                    st = HealthExt.Worst(st, Health.Warning);
                }
                else checks.Add(new CheckResult(Cat, "Ausstehende Updates", w.count > 0 ? Health.Info : Health.Ok,
                    w.count > 0 ? $"{w.count} Update(s) warten auf die Installation." : "Keine ausstehenden Updates laut letztem Scan."));
            }
        }
        else sec.Add("Ausstehende Updates", "wird ermittelt …");
        sec.Status = st;
        if (_wu is { error.Length: 0 } w2 && w2.list.Count > 0)
        {
            var list = new DetailSection { Group = Group, Title = "Ausstehende Updates", Headers = new[] { "Update", "Schweregrad" }, Status = Health.Info };
            foreach (var r in w2.list) list.Rows.Add(r);
            sections.Add(sec); sections.Add(list);
        }
        else sections.Add(sec);
    }

    // ---------- Defender ----------
    static void Defender(List<DetailSection> sections, List<CheckResult> checks)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Defender");
        try { scope.Connect(); }
        catch
        {
            checks.Add(new CheckResult(Cat, "Virenschutz", Health.Info, "Microsoft Defender ist nicht vorhanden oder nicht abfragbar (anderer Virenschutz?)."));
            return;
        }
        using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_MpComputerStatus"));
        foreach (ManagementObject o in q.Get())
        {
            using (o)
            {
                bool rt = Convert.ToBoolean(o["RealTimeProtectionEnabled"] ?? false);
                bool av = Convert.ToBoolean(o["AntivirusEnabled"] ?? false);
                double sig = Convert.ToDouble(o["AntivirusSignatureAge"] ?? 0);
                string Age(string p) { var v = Convert.ToDouble(o[p] ?? 0); return v >= 1_000_000 ? "nie" : $"vor {v:0} Tag(en)"; }
                var sec = new DetailSection { Group = Group, Title = "Microsoft Defender", Headers = new[] { "Eigenschaft", "Wert" } };
                sec.Add("Virenschutz aktiv", av ? "ja" : "nein");
                sec.Add("Echtzeitschutz", rt ? "an" : "AUS");
                sec.Add("Signaturen", $"{sig:0} Tag(e) alt");
                sec.Add("Letzter Schnellscan", Age("QuickScanAge"));
                sec.Add("Letzter Vollscan", Age("FullScanAge"));
                var st = !av || !rt ? Health.Critical : sig > 7 ? Health.Critical : sig > 3 ? Health.Warning : Health.Ok;
                sec.Status = st;
                sections.Add(sec);
                checks.Add(new CheckResult(Cat, "Virenschutz (Defender)", st,
                    !av || !rt ? "Echtzeitschutz ist ausgeschaltet." : $"Aktiv, Signaturen {sig:0} Tag(e) alt."));
                return;
            }
        }
    }

    // ---------- Firewall ----------
    static void Firewall(List<DetailSection> sections, List<CheckResult> checks)
    {
        var sec = new DetailSection { Group = Group, Title = "Windows-Firewall", Headers = new[] { "Profil", "Zustand", "Quelle" } };
        int off = 0, known = 0;
        foreach (var (name, pol, loc) in new[] { ("Domäne", "DomainProfile", "DomainProfile"), ("Privat", "PrivateProfile", "StandardProfile"), ("Öffentlich", "PublicProfile", "PublicProfile") })
        {
            object? v = null; string src = "lokal";
            using (var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{pol}")) { v = k?.GetValue("EnableFirewall"); if (v != null) src = "Gruppenrichtlinie"; }
            if (v == null) { using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{loc}"); v = k?.GetValue("EnableFirewall"); }
            if (v == null) { sec.Add(name, "unbekannt", "–"); continue; }
            known++;
            bool on = Convert.ToInt32(v) != 0;
            if (!on) off++;
            sec.Add(name, on ? "an" : "AUS", src);
        }
        if (known == 0) return;
        sec.Status = off == 0 ? Health.Ok : off == known ? Health.Critical : Health.Warning;
        sections.Add(sec);
        checks.Add(new CheckResult(Cat, "Windows-Firewall", sec.Status,
            off == 0 ? "Alle Profile sind eingeschaltet." : $"{off} von {known} Profilen sind ausgeschaltet."));
    }

    // ---------- BitLocker ----------
    static void BitLocker(List<DetailSection> sections)
    {
        var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption", new ConnectionOptions { Impersonation = ImpersonationLevel.Impersonate, EnablePrivileges = true });
        scope.Connect();
        using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume"));
        var sec = new DetailSection { Group = Group, Title = "BitLocker", Headers = new[] { "Laufwerk", "Schutz" }, Status = Health.Info };
        foreach (ManagementObject o in q.Get())
        {
            using (o)
            {
                int ps = Convert.ToInt32(o["ProtectionStatus"] ?? 2);
                sec.Add(o["DriveLetter"]?.ToString() ?? "?", ps == 1 ? "geschützt" : ps == 0 ? "nicht geschützt" : "unbekannt");
            }
        }
        if (sec.Rows.Count > 0) sections.Add(sec);
    }

    // ---------- Zertifikate ----------
    static void Certificates(List<DetailSection> sections, List<CheckResult> checks, AppSettings cfg)
    {
        var rows = new List<(string subject, string store, DateTime until, double days, string issuer, string thumb)>();
        foreach (var storeName in new[] { "My", "WebHosting", "Remote Desktop" })
        {
            try
            {
                using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var c in store.Certificates)
                {
                    string cn = c.GetNameInfo(X509NameType.SimpleName, false);
                    string iss = c.GetNameInfo(X509NameType.SimpleName, true);
                    rows.Add((cn, storeName, c.NotAfter, (c.NotAfter - DateTime.Now).TotalDays, iss, c.Thumbprint[..8]));
                }
            }
            catch { }
        }
        if (rows.Count == 0) return;
        rows = rows.OrderBy(r => r.days).ToList();
        var relevant = rows.Where(r => r.days > -30).ToList();           // lange abgelaufene Altlasten nicht melden
        var sec = new DetailSection { Group = Group, Title = "Zertifikate (lokaler Computer)", Headers = new[] { "Name", "Speicher", "Gültig bis", "Verbleibend", "Aussteller", "ID" } };
        foreach (var r in rows.Take(60))
            sec.Add(r.subject, r.store, r.until.ToString("d"), r.days < 0 ? $"abgelaufen ({-r.days:0} T)" : $"{r.days:0} Tage", r.issuer, r.thumb);
        var worst = relevant.Count == 0 ? Health.Ok : relevant[0].days <= 7 ? Health.Critical : relevant[0].days <= 30 ? Health.Warning : Health.Ok;
        sec.Status = worst;
        sections.Add(sec);
        var expiring = relevant.Where(r => r.days <= 30).ToList();
        checks.Add(new CheckResult(Cat, "Zertifikate", worst,
            expiring.Count == 0 ? $"{rows.Count} Zertifikat(e), keines läuft in den nächsten 30 Tagen ab."
            : $"{expiring.Count} Zertifikat(e) laufen bald ab oder sind kürzlich abgelaufen: " + string.Join(", ", expiring.Take(3).Select(r => $"{r.subject} ({(r.days < 0 ? "abgelaufen" : r.days.ToString("0") + " T")})"))));
    }

    // ---------- Offene Ports ----------
    static readonly Dictionary<int, (string name, Health rating)> Risky = new()
    {
        [21] = ("FTP (unverschlüsselt)", Health.Info), [23] = ("Telnet (unverschlüsselt)", Health.Warning),
        [445] = ("SMB / Dateifreigabe", Health.Info), [1433] = ("SQL Server", Health.Info), [1521] = ("Oracle", Health.Info),
        [3306] = ("MySQL/MariaDB", Health.Info), [3389] = ("Remotedesktop", Health.Info), [5432] = ("PostgreSQL", Health.Info),
        [5900] = ("VNC", Health.Warning), [5985] = ("WinRM (HTTP)", Health.Info), [5986] = ("WinRM (HTTPS)", Health.Info),
        [6379] = ("Redis (oft ohne Kennwort)", Health.Warning), [9200] = ("Elasticsearch", Health.Warning), [27017] = ("MongoDB", Health.Warning),
    };

    static void Ports(List<DetailSection> sections, List<CheckResult> checks, List<Native.TcpConn> tcp, List<ProcRecord> procs)
    {
        var names = procs.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name);
        var open = tcp.Where(t => t.State == 2 && (t.Local.Equals(System.Net.IPAddress.Any) || t.Local.Equals(System.Net.IPAddress.IPv6Any))
                                  && Risky.ContainsKey(t.LocalPort))
                      .GroupBy(t => t.LocalPort).OrderBy(g => g.Key).ToList();
        if (open.Count == 0) { checks.Add(new CheckResult(Cat, "Offene Ports", Health.Ok, "Keiner der geprüften Ports lauscht auf allen Netzwerkkarten.")); return; }
        var sec = new DetailSection
        {
            Group = Group, Title = "Ports, die auf allen Netzwerkkarten lauschen", Headers = new[] { "Port", "Bedeutung", "Prozess", "Bewertung" },
            Note = "Ob ein Port aus dem Internet erreichbar ist, hängt von Firewall und Router ab – das wird hier nicht geprüft.",
        };
        var warn = new List<string>();
        foreach (var g in open)
        {
            var (name, rating) = Risky[g.Key];
            int pid = g.First().Pid;
            sec.Add(g.Key.ToString(), name, names.TryGetValue(pid, out var n) ? n : pid == 4 ? "System" : "?", rating == Health.Warning ? "prüfen" : "üblich");
            if (rating == Health.Warning) warn.Add($"{g.Key} {name}");
            sec.Status = HealthExt.Worst(sec.Status == Health.Ok ? Health.Info : sec.Status, rating);
        }
        sections.Add(sec);
        checks.Add(new CheckResult(Cat, "Offene Ports", warn.Count > 0 ? Health.Warning : Health.Info,
            warn.Count > 0 ? "Auf allen Netzwerkkarten erreichbar und meist ungeschützt: " + string.Join(", ", warn) + "."
                           : $"{open.Count} Dienst(e) lauschen auf allen Netzwerkkarten (üblich: " + string.Join(", ", open.Select(g => g.Key)) + ")."));
    }
}
