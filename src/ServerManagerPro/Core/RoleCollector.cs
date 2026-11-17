using System.Management;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace ServerManagerPro.Core;

/// <summary>Zusatzrollen: Hyper-V, IIS, Druckserver, Dateiserver-Kontingente (FSRM), Exchange, DFS. Jede Rolle wird nur geprüft, wenn ihr Dienst vorhanden ist.</summary>
public static class RoleCollector
{
    static bool Has(List<ServiceRow> s, string name) => s.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    static bool Running(List<ServiceRow> s, string name) => s.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.IsRunning);

    public static (List<DetailSection> sections, List<CheckResult> checks) Collect(List<ServiceRow> services, AppSettings cfg)
    {
        var sections = new List<DetailSection>();
        var checks = new List<CheckResult>();
        void Try(string cat, Action a)
        {
            try { a(); }
            catch (Exception ex) { checks.Add(new CheckResult(cat, "Abfrage fehlgeschlagen", Health.Unknown, ex.GetBaseException().Message)); }
        }
        if (Running(services, "vmms")) Try("Hyper-V", () => HyperV(sections, checks));
        if (Running(services, "W3SVC")) Try("IIS", () => Iis(sections, checks));
        if (Running(services, "Spooler")) Try("Druckserver", () => Printers(sections, checks));
        if (Has(services, "SrmSvc")) Try("Dateiserver", () => Fsrm(sections, checks));
        if (services.Any(s => s.Name.StartsWith("MSExchange", StringComparison.OrdinalIgnoreCase))) Try("Exchange", () => Exchange(services, sections, checks));
        if (Has(services, "DFSR")) Try("DFS", () => Dfs(services, sections, checks));
        return (sections, checks);
    }

    // ---------- Hyper-V ----------
    const string HyperVScript = @"
$vms=@(Get-VM | ForEach-Object {
  $v=$_; $snap=@(Get-VMSnapshot -VMName $v.Name -ErrorAction SilentlyContinue); $old=''
  if($snap.Count -gt 0){ $old=($snap | Sort-Object CreationTime | Select-Object -First 1).CreationTime.ToString('s') }
  [pscustomobject]@{Name=$v.Name;State=[string]$v.State;Status=[string]$v.Status;UptimeH=[math]::Round($v.Uptime.TotalHours,1);Cpu=$v.CPUUsage;MemMB=[int]($v.MemoryAssigned/1MB);Repl=[string]$v.ReplicationHealth;Heartbeat=[string]$v.Heartbeat;Snaps=$snap.Count;OldSnap=$old}
})
ConvertTo-Json -InputObject @($vms) -Depth 3 -Compress";

    static void HyperV(List<DetailSection> sections, List<CheckResult> checks)
    {
        const string cat = "Hyper-V";
        using var doc = PsRunner.RunJson(HyperVScript);
        var sec = new DetailSection { Group = "Hyper-V", Title = "Virtuelle Maschinen", Headers = new[] { "VM", "Zustand", "Status", "CPU %", "RAM (MB)", "Laufzeit", "Replikation", "Checkpoints", "Heartbeat" } };
        var problems = new List<string>(); int running = 0, total = 0;
        foreach (var vm in PsRunner.Items(doc.RootElement))
        {
            total++;
            string name = PsRunner.S(vm, "Name"), state = PsRunner.S(vm, "State"), status = PsRunner.S(vm, "Status"), repl = PsRunner.S(vm, "Repl"), hb = PsRunner.S(vm, "Heartbeat");
            int snaps = (int)PsRunner.D(vm, "Snaps");
            bool on = state.Equals("Running", StringComparison.OrdinalIgnoreCase);
            if (on) running++;
            sec.Add(name, state, status, on ? PsRunner.S(vm, "Cpu") : "–", PsRunner.S(vm, "MemMB"), on ? $"{PsRunner.D(vm, "UptimeH"):0.#} h" : "–",
                repl is "" or "NotApplicable" ? "–" : repl, snaps.ToString(), hb is "" ? "–" : hb);
            if (on && status.Length > 0 && !status.Equals("Operating normally", StringComparison.OrdinalIgnoreCase)) problems.Add($"{name}: {status}");
            if (state is "Paused" or "Saved" or "Critical" or "Other") problems.Add($"{name}: {state}");
            if (repl is "Warning" or "Critical") problems.Add($"{name}: Replikation {repl}");
            if (on && (hb.Contains("Error", StringComparison.OrdinalIgnoreCase) || hb.Contains("Lost", StringComparison.OrdinalIgnoreCase) || hb.Contains("Heartbeat") && hb.Contains("No", StringComparison.OrdinalIgnoreCase))) problems.Add($"{name}: Heartbeat {hb}");
            var old = PsRunner.S(vm, "OldSnap");
            if (snaps >= 3) problems.Add($"{name}: {snaps} Checkpoints");
            else if (old.Length > 0 && DateTime.TryParse(old, out var od) && (DateTime.Now - od).TotalDays > 7) problems.Add($"{name}: Checkpoint älter als 7 Tage");
        }
        if (total == 0) return;
        sec.Status = problems.Count == 0 ? Health.Ok : Health.Warning;
        sections.Add(sec);
        checks.Add(new CheckResult(cat, "Virtuelle Maschinen", sec.Status,
            problems.Count == 0 ? $"{running} von {total} VMs laufen, keine Auffälligkeiten." : string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? " …" : "") + "."));
    }

    // ---------- IIS ----------
    const string IisScript = @"
Import-Module WebAdministration
$sites=@(Get-Website | ForEach-Object { [pscustomobject]@{Name=$_.Name;State=[string]$_.State;Path=[string]$_.PhysicalPath;Pool=[string]$_.ApplicationPool} })
$pools=@(Get-ChildItem IIS:\AppPools | ForEach-Object { [pscustomobject]@{Name=$_.Name;State=[string]$_.State;Runtime=[string]$_.managedRuntimeVersion} })
$bind=@(Get-WebBinding | Where-Object { $_.protocol -eq 'https' } | ForEach-Object { [pscustomobject]@{Binding=[string]$_.bindingInformation;Hash=[string]$_.certificateHash;Store=[string]$_.certificateStoreName} })
ConvertTo-Json -InputObject @{Sites=$sites;Pools=$pools;Bindings=$bind} -Depth 4 -Compress";

    static void Iis(List<DetailSection> sections, List<CheckResult> checks)
    {
        const string cat = "IIS";
        using var doc = PsRunner.RunJson(IisScript);
        var root = doc.RootElement;
        var problems = new List<string>();

        var sites = new DetailSection { Group = "IIS", Title = "Websites", Headers = new[] { "Website", "Status", "Anwendungspool", "Pfad" } };
        foreach (var s in PsRunner.Items(root.GetProperty("Sites")))
        {
            string st = PsRunner.S(s, "State");
            sites.Add(PsRunner.S(s, "Name"), st, PsRunner.S(s, "Pool"), PsRunner.S(s, "Path"));
            if (!st.Equals("Started", StringComparison.OrdinalIgnoreCase)) problems.Add($"Website {PsRunner.S(s, "Name")}: {st}");
        }
        var pools = new DetailSection { Group = "IIS", Title = "Anwendungspools", Headers = new[] { "Pool", "Status", ".NET-Version" } };
        foreach (var p in PsRunner.Items(root.GetProperty("Pools")))
        {
            string st = PsRunner.S(p, "State");
            pools.Add(PsRunner.S(p, "Name"), st, PsRunner.S(p, "Runtime"));
            if (!st.Equals("Started", StringComparison.OrdinalIgnoreCase)) problems.Add($"Pool {PsRunner.S(p, "Name")}: {st}");
        }
        var certs = new DetailSection { Group = "IIS", Title = "HTTPS-Zertifikate der Bindungen", Headers = new[] { "Bindung", "Zertifikat", "Gültig bis", "Verbleibend" } };
        foreach (var b in PsRunner.Items(root.GetProperty("Bindings")))
        {
            string hash = PsRunner.S(b, "Hash"), store = PsRunner.S(b, "Store");
            if (hash.Length == 0) continue;
            try
            {
                using var xs = new X509Store(store.Length > 0 ? store : "My", StoreLocation.LocalMachine);
                xs.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var c = xs.Certificates.Find(X509FindType.FindByThumbprint, hash, false).OfType<X509Certificate2>().FirstOrDefault();
                if (c == null) { certs.Add(PsRunner.S(b, "Binding"), "nicht gefunden", "–", "–"); problems.Add($"Bindung {PsRunner.S(b, "Binding")}: Zertifikat nicht gefunden"); continue; }
                double days = (c.NotAfter - DateTime.Now).TotalDays;
                certs.Add(PsRunner.S(b, "Binding"), c.GetNameInfo(X509NameType.SimpleName, false), c.NotAfter.ToString("d"), days < 0 ? "abgelaufen" : $"{days:0} Tage");
                if (days <= 30) problems.Add($"Bindung {PsRunner.S(b, "Binding")}: Zertifikat {(days < 0 ? "abgelaufen" : $"läuft in {days:0} Tagen ab")}");
            }
            catch { }
        }
        sites.Status = pools.Status = certs.Status = problems.Count == 0 ? Health.Ok : Health.Warning;
        if (sites.Rows.Count > 0) sections.Add(sites);
        if (pools.Rows.Count > 0) sections.Add(pools);
        if (certs.Rows.Count > 0) sections.Add(certs);
        checks.Add(new CheckResult(cat, "Webserver", problems.Count == 0 ? Health.Ok : Health.Warning,
            problems.Count == 0 ? $"{sites.Rows.Count} Website(s) und {pools.Rows.Count} Anwendungspool(s) laufen." : string.Join("; ", problems.Take(4)) + "."));
    }

    // ---------- Drucker ----------
    static readonly string[] VirtualPrinters = { "Microsoft Print to PDF", "Microsoft XPS Document Writer", "OneNote", "Fax", "Send To OneNote", "Adobe PDF", "PDF" };

    static void Printers(List<DetailSection> sections, List<CheckResult> checks)
    {
        const string cat = "Druckserver";
        var jobs = new Dictionary<string, List<(string doc, DateTime? at, string status)>>(StringComparer.OrdinalIgnoreCase);
        using (var jq = new ManagementObjectSearcher("SELECT Name, Document, TimeSubmitted, JobStatus FROM Win32_PrintJob"))
            foreach (ManagementObject o in jq.Get())
            {
                using (o)
                {
                    string n = o["Name"]?.ToString() ?? "";
                    string printer = n.Contains(',') ? n[..n.LastIndexOf(',')] : n;
                    DateTime? t = null;
                    try { t = ManagementDateTimeConverter.ToDateTime(o["TimeSubmitted"]?.ToString()); } catch { }
                    if (!jobs.TryGetValue(printer, out var l)) jobs[printer] = l = new();
                    l.Add((o["Document"]?.ToString() ?? "", t, o["JobStatus"]?.ToString() ?? ""));
                }
            }
        var sec = new DetailSection { Group = "Drucker", Title = "Druckwarteschlangen", Headers = new[] { "Drucker", "Freigegeben", "Zustand", "Aufträge", "Ältester Auftrag" } };
        var problems = new List<string>();
        using var q = new ManagementObjectSearcher("SELECT Name, PrinterStatus, WorkOffline, Shared FROM Win32_Printer");
        foreach (ManagementObject o in q.Get())
        {
            using (o)
            {
                string name = o["Name"]?.ToString() ?? "";
                if (VirtualPrinters.Any(v => name.StartsWith(v, StringComparison.OrdinalIgnoreCase))) continue;
                int ps = Convert.ToInt32(o["PrinterStatus"] ?? 2);
                bool offline = Convert.ToBoolean(o["WorkOffline"] ?? false) || ps == 7;
                bool shared = Convert.ToBoolean(o["Shared"] ?? false);
                jobs.TryGetValue(name, out var l);
                int n = l?.Count ?? 0;
                var oldest = l?.Where(j => j.at.HasValue).Select(j => j.at!.Value).DefaultIfEmpty().Min();
                string state = offline ? "offline" : ps switch { 3 => "bereit", 4 => "druckt", 5 => "Aufwärmen", 6 => "gestoppt", _ => "unbekannt" };
                sec.Add(name, shared ? "ja" : "nein", state, n.ToString(), oldest.HasValue && oldest.Value != default ? $"vor {BackupCollector.Age(DateTime.Now - oldest.Value)}" : "–");
                if (offline && shared) problems.Add($"{name}: offline");
                if (ps == 6) problems.Add($"{name}: gestoppt");
                if (l != null && l.Any(j => j.status.Contains("Error", StringComparison.OrdinalIgnoreCase) || j.status.Contains("Paused", StringComparison.OrdinalIgnoreCase))) problems.Add($"{name}: Auftrag mit Fehler/pausiert");
                else if (oldest.HasValue && oldest.Value != default && (DateTime.Now - oldest.Value).TotalMinutes > 30) problems.Add($"{name}: Auftrag hängt seit {BackupCollector.Age(DateTime.Now - oldest.Value)}");
            }
        }
        if (sec.Rows.Count == 0) return;
        sec.Status = problems.Count == 0 ? Health.Ok : Health.Warning;
        sections.Add(sec);
        checks.Add(new CheckResult(cat, "Druckwarteschlangen", sec.Status,
            problems.Count == 0 ? $"{sec.Rows.Count} Drucker, keine hängenden Aufträge." : string.Join("; ", problems.Take(4)) + "."));
    }

    // ---------- Kontingente (FSRM) ----------
    const string FsrmScript = @"
$q=@(Get-FsrmQuota | ForEach-Object { [pscustomobject]@{Path=[string]$_.Path;Size=[double]$_.Size;Usage=[double]$_.Usage;Soft=[bool]$_.SoftLimit;Disabled=[bool]$_.Disabled} })
ConvertTo-Json -InputObject @($q) -Depth 3 -Compress";

    static void Fsrm(List<DetailSection> sections, List<CheckResult> checks)
    {
        using var doc = PsRunner.RunJson(FsrmScript);
        var sec = new DetailSection { Group = "Dateiserver", Title = "Kontingente (FSRM)", Headers = new[] { "Pfad", "Limit", "Belegt", "Auslastung", "Art" } };
        var problems = new List<string>(); var st = Health.Ok;
        foreach (var e in PsRunner.Items(doc.RootElement).OrderByDescending(e => PsRunner.D(e, "Size") > 0 ? PsRunner.D(e, "Usage") / PsRunner.D(e, "Size") : 0).Take(60))
        {
            double size = PsRunner.D(e, "Size"), use = PsRunner.D(e, "Usage");
            if (size <= 0 || PsRunner.S(e, "Disabled") == "True") continue;
            double pct = use / size * 100;
            bool soft = PsRunner.S(e, "Soft") == "True";
            sec.Add(PsRunner.S(e, "Path"), Fmt.Bytes(size), Fmt.Bytes(use), $"{pct:0} %", soft ? "weich" : "hart");
            if (pct >= 95) { st = HealthExt.Worst(st, soft ? Health.Warning : Health.Critical); problems.Add($"{PsRunner.S(e, "Path")} {pct:0} %"); }
            else if (pct >= 85) { st = HealthExt.Worst(st, Health.Warning); problems.Add($"{PsRunner.S(e, "Path")} {pct:0} %"); }
        }
        if (sec.Rows.Count == 0) return;
        sec.Status = st;
        sections.Add(sec);
        checks.Add(new CheckResult("Dateiserver", "Kontingente", st,
            problems.Count == 0 ? $"{sec.Rows.Count} Kontingente, alle unter 85 % belegt." : "Fast voll: " + string.Join(", ", problems.Take(4)) + "."));
    }

    // ---------- Exchange ----------
    static readonly string[] ExchangeCore = { "MSExchangeIS", "MSExchangeTransport", "MSExchangeADTopology", "MSExchangeServiceHost", "MSExchangeRPC", "MSExchangeFrontEndTransport", "MSExchangeMailboxAssistants" };

    static void Exchange(List<ServiceRow> services, List<DetailSection> sections, List<CheckResult> checks)
    {
        const string cat = "Exchange";
        var ex = services.Where(s => s.Name.StartsWith("MSExchange", StringComparison.OrdinalIgnoreCase)).ToList();
        var sec = new DetailSection { Group = "Exchange", Title = "Exchange-Dienste", Headers = new[] { "Dienst", "Name", "Status", "Starttyp" } };
        var stopped = ex.Where(s => s.StartMode == "Auto" && !s.IsRunning).ToList();
        foreach (var s in ex.OrderBy(s => s.IsRunning).ThenBy(s => s.DisplayName).Take(80)) sec.Add(s.DisplayName, s.Name, s.State, s.StartMode);
        var core = stopped.Where(s => ExchangeCore.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        sec.Status = core.Count > 0 ? Health.Critical : stopped.Count > 0 ? Health.Warning : Health.Ok;
        sections.Add(sec);
        checks.Add(new CheckResult(cat, "Exchange-Dienste", sec.Status,
            stopped.Count == 0 ? $"{ex.Count} Exchange-Dienste, alle automatischen laufen." : $"{stopped.Count} Dienst(e) gestoppt: " + string.Join(", ", stopped.Take(4).Select(s => s.DisplayName))));
        try
        {
            using var c = new System.Diagnostics.PerformanceCounter("MSExchangeTransport Queues", "Aggregate Delivery Queue Length (All Queues)", "_total");
            double q = c.NextValue();
            checks.Add(new CheckResult(cat, "Mailwarteschlange", q > 500 ? Health.Critical : q > 100 ? Health.Warning : Health.Ok, $"{q:0} Nachrichten in den Zustellwarteschlangen."));
        }
        catch { }
    }

    // ---------- DFS ----------
    static void Dfs(List<ServiceRow> services, List<DetailSection> sections, List<CheckResult> checks)
    {
        var sec = new DetailSection { Group = "DFS", Title = "DFS-Replikation", Headers = new[] { "Replizierter Ordner", "Zustand" } };
        var bad = new List<string>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\MicrosoftDFS");
            scope.Connect();
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ReplicatedFolderName, State FROM DfsrReplicatedFolderInfo"));
            foreach (ManagementObject o in q.Get())
            {
                using (o)
                {
                    int st = Convert.ToInt32(o["State"] ?? 0);
                    string name = o["ReplicatedFolderName"]?.ToString() ?? "?";
                    string text = st switch { 0 => "nicht initialisiert", 1 => "initialisiert", 2 => "Erstsynchronisierung", 3 => "Autorecovery", 4 => "normal", 5 => "FEHLER", _ => st.ToString() };
                    sec.Add(name, text);
                    if (st is 3 or 5) bad.Add($"{name}: {text}");
                }
            }
        }
        catch { }
        if (sec.Rows.Count == 0) return;
        sec.Status = bad.Count == 0 ? Health.Ok : Health.Critical;
        sections.Add(sec);
        checks.Add(new CheckResult("DFS", "DFS-Replikation", sec.Status, bad.Count == 0 ? $"{sec.Rows.Count} replizierte Ordner, alle normal." : string.Join("; ", bad) + "."));
    }
}
