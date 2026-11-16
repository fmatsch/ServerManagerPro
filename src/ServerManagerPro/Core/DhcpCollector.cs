using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ServerManagerPro.Core;

/// <summary>
/// Liest Bereiche, Leases und Reservierungen vom DHCP-Server und gleicht sie mit einem echten IP-Scan (Ping + ARP) ab,
/// damit sichtbar wird, welche Adressen wirklich vergeben werden können.
/// </summary>
public sealed class DhcpCollector
{
    const string Cat = "DHCP";
    const int MaxScanPerScope = 4096;
    static readonly TimeSpan FetchEvery = TimeSpan.FromSeconds(45), ScanEvery = TimeSpan.FromMinutes(10);

    [DllImport("iphlpapi.dll")]
    static extern int SendARP(uint dest, uint src, byte[] mac, ref int len);

    // ---- Zustand zwischen den Läufen ----
    RawScope[] _raw = Array.Empty<RawScope>();
    DateTime _fetchAt = DateTime.MinValue, _scanAt = DateTime.MinValue;
    string _error = "";
    volatile bool _scanning, _force;
    int _scanDone, _scanTotal;
    ConcurrentDictionary<uint, string> _alive = new();   // IP → MAC ("" = nur Ping)
    ConcurrentDictionary<uint, string> _hosts = new();
    bool _scanned;
    readonly object _gate = new();

    public void RequestScan() => _force = true;

    sealed record RawScope(string ScopeId, string Name, uint Start, uint End, string Mask, string State,
        List<(uint s, uint e)> Excl, List<(uint ip, string mac, string host, string state, string exp)> Leases,
        List<(uint ip, string mac, string name)> Res);

    // ---------- öffentlicher Einstieg ----------
    public (DhcpInfo info, List<CheckResult> checks) Collect(bool serviceRunning)
    {
        var info = new DhcpInfo { ServiceRunning = serviceRunning };
        var checks = new List<CheckResult>();

        if (!serviceRunning)
        {
            checks.Add(new CheckResult(Cat, "DHCP-Server", Health.Critical, "Der DHCP-Serverdienst ist gestoppt – Clients erhalten keine Adressen."));
            return (info, checks);
        }
        checks.Add(new CheckResult(Cat, "DHCP-Server", Health.Ok, "Dienst läuft."));

        if (DateTime.Now - _fetchAt > FetchEvery || _force)
        {
            try { _raw = Fetch(); _error = ""; }
            catch (Exception ex) { _error = ex.GetBaseException().Message; }
            _fetchAt = DateTime.Now;
        }

        bool due = _raw.Length > 0 && !_scanning && (_force || DateTime.Now - _scanAt > ScanEvery);
        if (due)
        {
            _force = false;
            _scanning = true;
            var raw = _raw;
            _ = Task.Run(() => { try { Scan(raw); } catch { } finally { _scanning = false; } });
        }

        info.Error = _error;
        info.Scanning = _scanning;
        info.ScanProgress = _scanning ? $"{_scanDone} von {_scanTotal} Adressen geprüft" : "";
        info.ScanTime = _scanned ? _scanAt : null;
        if (_error.Length > 0)
        {
            checks.Add(new CheckResult(Cat, "Bereiche", Health.Warning, "DHCP-Daten nicht lesbar: " + _error));
            return (info, checks);
        }
        Build(info);

        foreach (var s in info.Scopes)
        {
            if (!s.State.Equals("Active", StringComparison.OrdinalIgnoreCase))
            { checks.Add(new CheckResult(Cat, $"Bereich {s.ScopeId}", Health.Warning, $"Bereich „{s.Name}“ ist {s.State}.")); continue; }
            double free = s.Total <= 0 ? 100 : s.ReallyFree * 100.0 / s.Total;
            var st = free < 2 ? Health.Critical : free < 10 ? Health.Warning : Health.Ok;
            checks.Add(new CheckResult(Cat, $"Bereich {s.ScopeId}", st,
                $"{s.Name}: {s.ReallyFree} von {s.Total} Adressen wirklich frei ({free:0}%)" + (s.Scanned ? "" : " – laut DHCP, Scan ausstehend") + "."));
            if (s.Conflicts > 0)
                checks.Add(new CheckResult(Cat, $"Konflikte {s.ScopeId}", Health.Warning,
                    $"{s.Conflicts} Adresse(n) antworten, obwohl DHCP sie als frei führt (statische IP oder fremder DHCP-Server?)."));
            if (s.Declined > 0)
                checks.Add(new CheckResult(Cat, $"Abgelehnte Adressen {s.ScopeId}", Health.Warning, $"{s.Declined} Adresse(n) wurden von Clients wegen Konflikt abgelehnt."));
        }
        if (info.Scopes.Count == 0)
            checks.Add(new CheckResult(Cat, "Bereiche", Health.Info, "Der DHCP-Server hat keine IPv4-Bereiche."));
        return (info, checks);
    }

    // ---------- DHCP-Daten per PowerShell ----------
    const string Script = @"
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[System.Text.Encoding]::UTF8
$out=@()
foreach($sc in @(Get-DhcpServerv4Scope)){
  $id=$sc.ScopeId
  $ex=@(Get-DhcpServerv4ExclusionRange -ScopeId $id -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{S=$_.StartRange.ToString();E=$_.EndRange.ToString()} })
  $le=@(Get-DhcpServerv4Lease -ScopeId $id -AllLeases -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{Ip=$_.IPAddress.ToString();Mac=[string]$_.ClientId;Host=[string]$_.HostName;State=[string]$_.AddressState;Exp=$(if($_.LeaseExpiryTime){$_.LeaseExpiryTime.ToString('s')}else{''})} })
  $re=@(Get-DhcpServerv4Reservation -ScopeId $id -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{Ip=$_.IPAddress.ToString();Mac=[string]$_.ClientId;Name=[string]$_.Name} })
  $out+=[pscustomobject]@{ScopeId=$id.ToString();Name=[string]$sc.Name;Start=$sc.StartRange.ToString();End=$sc.EndRange.ToString();Mask=$sc.SubnetMask.ToString();State=[string]$sc.State;Excl=$ex;Leases=$le;Res=$re}
}
ConvertTo-Json -InputObject @($out) -Depth 5 -Compress
";

    static RawScope[] Fetch()
    {
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(90_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("Abfrage des DHCP-Servers dauerte zu lange."); }
        string json = outp.Result.Trim();
        if (p.ExitCode != 0 || json.Length == 0)
        {
            string e = err.Result.Trim();
            throw new InvalidOperationException(e.Length > 0 ? e.Split('\n')[0].Trim() : "DHCP-Cmdlets nicht verfügbar (Rolle/RSAT-Modul DhcpServer fehlt oder keine Berechtigung).");
        }
        using var doc = JsonDocument.Parse(json);
        var res = new List<RawScope>();
        foreach (var sc in Items(doc.RootElement))
        {
            res.Add(new RawScope(S(sc, "ScopeId"), S(sc, "Name"), U(S(sc, "Start")), U(S(sc, "End")), S(sc, "Mask"), S(sc, "State"),
                Items(sc.GetProperty("Excl")).Select(x => (U(S(x, "S")), U(S(x, "E")))).ToList(),
                Items(sc.GetProperty("Leases")).Select(x => (U(S(x, "Ip")), S(x, "Mac"), S(x, "Host"), S(x, "State"), S(x, "Exp"))).ToList(),
                Items(sc.GetProperty("Res")).Select(x => (U(S(x, "Ip")), S(x, "Mac"), S(x, "Name"))).ToList()));
        }
        return res.ToArray();
    }

    static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList()
        : e.ValueKind == JsonValueKind.Object ? new List<JsonElement> { e } : new List<JsonElement>();
    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

    public static uint U(string ip) => IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork ? ToU(a) : 0;
    public static uint ToU(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    public static string Str(uint u) => $"{u >> 24}.{(u >> 16) & 255}.{(u >> 8) & 255}.{u & 255}";

    // ---------- Scan (Ping + ARP) ----------
    void Scan(RawScope[] scopes)
    {
        var targets = new HashSet<uint>();
        foreach (var sc in scopes)
        {
            uint n = 0;
            for (uint ip = sc.Start; ip <= sc.End && n < MaxScanPerScope; ip++, n++)
                if (!sc.Excl.Any(x => ip >= x.s && ip <= x.e)) targets.Add(ip);
        }
        _scanDone = 0; _scanTotal = targets.Count;
        ThreadPool.GetMinThreads(out int w, out int io);
        if (w < 96) ThreadPool.SetMinThreads(96, io);

        var local = LocalSubnets();
        var alive = new ConcurrentDictionary<uint, string>();
        using var gate = new SemaphoreSlim(64);
        var tasks = targets.Select(async ip =>
        {
            await gate.WaitAsync();
            try
            {
                string? mac = await Probe(ip, local);
                if (mac != null) alive[ip] = mac;
            }
            catch { }
            finally { gate.Release(); Interlocked.Increment(ref _scanDone); }
        }).ToArray();
        Task.WaitAll(tasks);

        // Namen für Adressen, die sich ohne DHCP-Eintrag melden
        var leased = scopes.SelectMany(s => s.Leases.Select(l => l.ip)).Concat(scopes.SelectMany(s => s.Res.Select(r => r.ip))).ToHashSet();
        var hosts = new ConcurrentDictionary<uint, string>();
        var unknown = alive.Keys.Where(k => !leased.Contains(k)).Take(60).ToList();
        Task.WaitAll(unknown.Select(async ip =>
        {
            try
            {
                var t = Dns.GetHostEntryAsync(Str(ip));
                if (await Task.WhenAny(t, Task.Delay(1500)) == t) hosts[ip] = t.Result.HostName;
            }
            catch { }
        }).ToArray());

        lock (_gate) { _alive = alive; _hosts = hosts; _scanAt = DateTime.Now; _scanned = true; }
    }

    /// <summary>Diagnose: scannt einen Adressbereich (Ping + ARP) und liefert die antwortenden Adressen.</summary>
    public static async Task<List<(string ip, string mac)>> QuickScan(uint start, uint end)
    {
        ThreadPool.SetMinThreads(96, 96);
        var local = LocalSubnets();
        var found = new ConcurrentDictionary<uint, string>();
        using var gate = new SemaphoreSlim(64);
        var tasks = Enumerable.Range(0, (int)(end - start + 1)).Select(i => start + (uint)i).Select(async ip =>
        {
            await gate.WaitAsync();
            try { var m = await Probe(ip, local); if (m != null) found[ip] = m; } catch { } finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return found.OrderBy(k => k.Key).Select(k => (Str(k.Key), k.Value)).ToList();
    }

    static List<(uint net, uint mask)> LocalSubnets()
    {
        var res = new List<(uint, uint)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var ua in ni.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                uint mask = U(ua.IPv4Mask?.ToString() ?? "0.0.0.0");
                if (mask != 0) res.Add((ToU(ua.Address) & mask, mask));
            }
        return res;
    }

    static HashSet<uint> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => ToU(u.Address)).ToHashSet();

    /// <summary>Antwortet die Adresse? Rückgabe: MAC (oder "" bei reinem Ping) bzw. null.</summary>
    static async Task<string?> Probe(uint ip, List<(uint net, uint mask)> local)
    {
        var addr = IPAddress.Parse(Str(ip));
        bool pinged = false;
        try
        {
            using var ping = new Ping();
            var r = await ping.SendPingAsync(addr, 500);
            pinged = r.Status == IPStatus.Success;
        }
        catch { }

        bool onLink = local.Any(l => (ip & l.mask) == l.net);
        if (onLink)
        {
            // ARP findet auch Geräte, deren Firewall Ping blockiert (blockiert bis ~3 s, wenn niemand antwortet)
            var mac = await Task.Run(() => Arp(ip));
            if (mac != null) return mac;
        }
        return pinged ? "" : null;
    }

    static string? Arp(uint ip)
    {
        var b = new byte[6]; int len = 6;
        var addr = IPAddress.Parse(Str(ip)).GetAddressBytes();
        int rc = SendARP(BitConverter.ToUInt32(addr, 0), 0, b, ref len);
        return rc == 0 && len >= 6 ? string.Join("-", b.Take(6).Select(x => x.ToString("X2"))) : null;
    }

    // ---------- Abgleich ----------
    void Build(DhcpInfo info)
    {
        ConcurrentDictionary<uint, string> alive; ConcurrentDictionary<uint, string> hosts; bool scanned;
        lock (_gate) { alive = _alive; hosts = _hosts; scanned = _scanned; }
        var own = LocalAddresses();

        foreach (var sc in _raw)
        {
            var row = new DhcpScopeRow
            {
                ScopeId = sc.ScopeId, Name = sc.Name, State = sc.State, Scanned = scanned,
                Range = $"{Str(sc.Start)} – {Str(sc.End)}",
            };
            var leases = new Dictionary<uint, (string mac, string host, string state, string exp)>();
            foreach (var l in sc.Leases) leases[l.ip] = (l.mac, l.host, l.state, l.exp);
            var res = sc.Res.ToDictionary(r => r.ip, r => r);
            uint n = 0;
            for (uint ip = sc.Start; ip <= sc.End && n < MaxScanPerScope; ip++, n++)
            {
                var r = new IpRow { Ip = Str(ip), Sort = ip, Scope = sc.ScopeId };
                bool isAlive = alive.TryGetValue(ip, out var amac);
                r.Alive = isAlive;
                if (isAlive) r.Mac = amac ?? "";
                if (hosts.TryGetValue(ip, out var hn)) r.Host = hn;

                if (sc.Excl.Any(x => ip >= x.s && ip <= x.e)) { r.Status = "Ausgeschlossen"; r.Detail = "Vom Vergeben ausgenommen"; row.Excluded++; }
                else
                {
                    row.Total++;
                    leases.TryGetValue(ip, out var le);
                    bool hasLease = leases.ContainsKey(ip);
                    string ls = le.state ?? "";
                    if (own.Contains(ip)) { r.Status = "Dieser Server"; r.Detail = "Eigene Adresse"; r.Alive = true; row.Reserved++; }
                    else if (hasLease && (ls.Contains("Declined", StringComparison.OrdinalIgnoreCase) || ls.Contains("Abandoned", StringComparison.OrdinalIgnoreCase) || ls.Contains("Bad", StringComparison.OrdinalIgnoreCase)))
                    { r.Status = "Abgelehnt"; r.Detail = "Ein Client hat die Adresse wegen Konflikt abgelehnt"; row.Declined++; }
                    else if (res.TryGetValue(ip, out var rv))
                    {
                        r.Status = "Reserviert"; r.Mac = rv.mac; r.Host = rv.name;
                        r.Detail = scanned ? (isAlive ? "Reservierung, Gerät antwortet" : "Reservierung, Gerät offline") : "Reservierung";
                        row.Reserved++;
                    }
                    else if (hasLease && ls.StartsWith("Active", StringComparison.OrdinalIgnoreCase))
                    {
                        r.Status = "Vergeben"; r.Mac = le.mac; r.Host = le.host;
                        r.Detail = (scanned ? (isAlive ? "Gerät antwortet" : "Gerät offline") : "") + (le.exp.Length > 0 ? (scanned ? ", " : "") + "Lease bis " + le.exp.Replace('T', ' ')[..16] : "");
                        row.Leased++;
                    }
                    else if (scanned && isAlive)
                    {
                        r.Status = "Konflikt";
                        r.Detail = "Antwortet, steht aber nicht im DHCP – statische IP oder fremdes Gerät";
                        row.Conflicts++;
                    }
                    else
                    {
                        r.Status = "Frei";
                        r.Detail = scanned ? "Laut DHCP frei und im Scan ohne Antwort" : "Laut DHCP frei (Scan läuft noch)";
                    }
                }
                info.Addresses.Add(r);
            }
            row.ServerFree = row.Total - row.Leased - row.Reserved - row.Declined;
            row.ReallyFree = Math.Max(0, row.ServerFree - row.Conflicts);
            if (sc.End - sc.Start + 1 > MaxScanPerScope) row.Note = $"Nur die ersten {MaxScanPerScope} Adressen werden betrachtet.";
            info.Scopes.Add(row);
        }
    }
}
