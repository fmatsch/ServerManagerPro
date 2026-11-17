using System.Management;
using Microsoft.Win32;

namespace ServerManagerPro.Core;

/// <summary>Hardwarezustand über Windows-Schnittstellen: Datenträger (SMART/Verschleiß/Temperatur), Speicherplätze, Temperaturen, USV/Batterie.</summary>
public static class HardwareCollector
{
    const string Cat = "Hardware", Group = "Hardware";

    public static bool IsServerOs()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return !string.Equals(k?.GetValue("InstallationType")?.ToString(), "Client", StringComparison.OrdinalIgnoreCase);
    }

    static double? Num(ManagementBaseObject o, string p)
    {
        try { return o[p] == null ? null : Convert.ToDouble(o[p]); } catch { return null; }
    }

    public static (List<DetailSection> sections, List<CheckResult> checks) Collect(AppSettings cfg)
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
        Try(() => Disks(sections, checks), "Datenträger");
        Try(() => Pools(sections, checks), "Speicherplätze");
        Try(() => Thermal(sections, checks), "Temperaturen");
        Try(() => Battery(sections, checks), "USV/Batterie");
        if (needAdmin.Count > 0)
            checks.Add(new CheckResult(Cat, "Nur mit Administratorrechten", Health.Info, "Nicht lesbar ohne Administratorrechte: " + string.Join(", ", needAdmin) + "."));
        return (sections, checks);
    }

    static void Disks(List<DetailSection> sections, List<CheckResult> checks)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        scope.Connect();
        var rel = new Dictionary<string, ManagementBaseObject>();
        try
        {
            using var rq = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_StorageReliabilityCounter"));
            foreach (ManagementObject o in rq.Get()) rel[o["DeviceId"]?.ToString() ?? ""] = o;
        }
        catch { }

        var sec = new DetailSection { Group = Group, Title = "Datenträger (Zustand, Temperatur, Verschleiß)",
            Headers = new[] { "Datenträger", "Typ", "Größe", "Zustand", "Temperatur", "Verschleiß", "Lesefehler", "Betriebsstunden" } };
        var problems = new List<string>();
        var st = Health.Ok;
        using var q = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
        foreach (ManagementObject d in q.Get())
        {
            using (d)
            {
                string id = d["DeviceId"]?.ToString() ?? "", name = d["FriendlyName"]?.ToString() ?? id;
                int bus = Convert.ToInt32(d["BusType"] ?? 0), media = Convert.ToInt32(d["MediaType"] ?? 0);
                string type = bus == 17 ? "NVMe" : media == 4 ? "SSD" : media == 3 ? "HDD" : "–";
                int health = Convert.ToInt32(d["HealthStatus"] ?? 0);
                string hs = health == 0 ? "gesund" : health == 1 ? "Warnung" : "defekt";
                double size = (Num(d, "Size") ?? 0) / 1073741824.0;
                double? temp = null, wear = null, readErr = null, hours = null;
                if (rel.TryGetValue(id, out var r))
                {
                    temp = Num(r, "Temperature"); wear = Num(r, "Wear"); readErr = Num(r, "ReadErrorsUncorrected"); hours = Num(r, "PowerOnHours");
                }
                if (temp is 0) temp = null;
                sec.Add(name, type, $"{size:0} GB", hs, temp.HasValue ? $"{temp:0} °C" : "–", wear.HasValue ? $"{wear:0} %" : "–",
                    readErr.HasValue ? $"{readErr:0}" : "–", hours.HasValue ? $"{hours:#,0} h" : "–");
                if (health == 2) { st = HealthExt.Worst(st, Health.Critical); problems.Add($"{name}: defekt"); }
                else if (health == 1) { st = HealthExt.Worst(st, Health.Warning); problems.Add($"{name}: Warnung"); }
                double limitWarn = type == "HDD" ? 50 : 65, limitCrit = type == "HDD" ? 60 : 75;
                if (temp >= limitCrit) { st = HealthExt.Worst(st, Health.Critical); problems.Add($"{name}: {temp:0} °C"); }
                else if (temp >= limitWarn) { st = HealthExt.Worst(st, Health.Warning); problems.Add($"{name}: {temp:0} °C"); }
                if (wear >= 90) { st = HealthExt.Worst(st, Health.Critical); problems.Add($"{name}: Verschleiß {wear:0} %"); }
                else if (wear >= 80) { st = HealthExt.Worst(st, Health.Warning); problems.Add($"{name}: Verschleiß {wear:0} %"); }
                if (readErr > 0) { st = HealthExt.Worst(st, Health.Warning); problems.Add($"{name}: {readErr:0} unkorrigierte Lesefehler"); }
            }
        }
        if (sec.Rows.Count == 0) return;

        // SMART-Ausfallvorhersage (Administratorrechte nötig)
        try
        {
            using var f = new ManagementObjectSearcher(@"root\wmi", "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            foreach (ManagementObject o in f.Get())
                if (Convert.ToBoolean(o["PredictFailure"] ?? false)) { st = Health.Critical; problems.Add("SMART meldet einen drohenden Ausfall"); }
        }
        catch { }

        sec.Status = st;
        sections.Add(sec);
        checks.Add(new CheckResult(Cat, "Datenträger-Gesundheit", st,
            problems.Count == 0 ? $"{sec.Rows.Count} Datenträger ohne Auffälligkeiten (Zustand, Temperatur, Verschleiß, Lesefehler)." : string.Join("; ", problems.Distinct().Take(4)) + "."));
    }

    static void Pools(List<DetailSection> sections, List<CheckResult> checks)
    {
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        scope.Connect();
        var sec = new DetailSection { Group = Group, Title = "Speicherplätze (Storage Spaces)", Headers = new[] { "Name", "Art", "Größe", "Zustand", "Status" } };
        var bad = new List<string>();
        foreach (var (cls, art) in new[] { ("MSFT_StoragePool", "Pool"), ("MSFT_VirtualDisk", "Virtueller Datenträger") })
        {
            using var q = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {cls}"));
            foreach (ManagementObject o in q.Get())
            {
                using (o)
                {
                    if (cls == "MSFT_StoragePool" && Convert.ToBoolean(o["IsPrimordial"] ?? false)) continue;
                    int h = Convert.ToInt32(o["HealthStatus"] ?? 0);
                    string name = o["FriendlyName"]?.ToString() ?? "?";
                    string op = "";
                    try { op = string.Join(", ", ((ushort[])o["OperationalStatus"]).Select(x => x switch { 2 => "OK", 3 => "eingeschränkt", 5 => "Fehler", 10 => "Fehler", 0xD009 => "Mehr Platz nötig", 0xD00B => "Wird repariert", _ => x.ToString() })); } catch { }
                    sec.Add(name, art, $"{(Num(o, "Size") ?? 0) / 1073741824.0:0} GB", h == 0 ? "gesund" : h == 1 ? "Warnung" : "defekt", op);
                    if (h != 0) bad.Add($"{art} {name}: {(h == 1 ? "Warnung" : "defekt")}");
                }
            }
        }
        if (sec.Rows.Count == 0) return;
        sec.Status = bad.Count == 0 ? Health.Ok : bad.Any(b => b.EndsWith("defekt")) ? Health.Critical : Health.Warning;
        sections.Add(sec);
        checks.Add(new CheckResult(Cat, "Speicherplätze", sec.Status, bad.Count == 0 ? $"{sec.Rows.Count} Pools/Datenträger gesund." : string.Join("; ", bad) + "."));
    }

    static void Thermal(List<DetailSection> sections, List<CheckResult> checks)
    {
        using var q = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
        var sec = new DetailSection { Group = Group, Title = "Temperaturen (ACPI-Zonen)", Headers = new[] { "Zone", "Temperatur" } };
        double max = 0;
        foreach (ManagementObject o in q.Get())
        {
            using (o)
            {
                double c = Convert.ToDouble(o["CurrentTemperature"] ?? 0) / 10.0 - 273.15;
                if (c < -50 || c > 150) continue;
                sec.Add(o["InstanceName"]?.ToString() ?? "?", $"{c:0.#} °C");
                max = Math.Max(max, c);
            }
        }
        if (sec.Rows.Count == 0) return;
        sec.Status = max >= 90 ? Health.Critical : max >= 80 ? Health.Warning : Health.Ok;
        sections.Add(sec);
        checks.Add(new CheckResult(Cat, "Temperatur", sec.Status, $"Höchste gemeldete Temperatur {max:0} °C."));
    }

    static void Battery(List<DetailSection> sections, List<CheckResult> checks)
    {
        using var q = new ManagementObjectSearcher("SELECT * FROM Win32_Battery");
        var sec = new DetailSection { Group = Group, Title = "USV / Batterie", Headers = new[] { "Name", "Ladung", "Restlaufzeit", "Status" } };
        bool server = IsServerOs();
        var st = Health.Ok;
        foreach (ManagementObject o in q.Get())
        {
            using (o)
            {
                int status = Convert.ToInt32(o["BatteryStatus"] ?? 0);
                double charge = Num(o, "EstimatedChargeRemaining") ?? 0, minutes = Num(o, "EstimatedRunTime") ?? 0;
                bool onBattery = status == 1;
                sec.Add(o["Name"]?.ToString() ?? "Batterie", $"{charge:0} %", minutes is > 0 and < 71582788 ? $"{minutes:0} Min." : "–", onBattery ? "läuft auf Batterie" : "Netzbetrieb");
                if (onBattery) st = HealthExt.Worst(st, server ? Health.Critical : Health.Info);
                if (charge < 20 && onBattery) st = HealthExt.Worst(st, server ? Health.Critical : Health.Warning);
            }
        }
        if (sec.Rows.Count == 0) return;
        sec.Status = st;
        sections.Add(sec);
        checks.Add(new CheckResult(Cat, "USV / Batterie", st, st == Health.Ok ? "Netzbetrieb, Batterie geladen." : "Läuft auf Batterie – Stromausfall oder USV im Akkubetrieb."));
    }
}
