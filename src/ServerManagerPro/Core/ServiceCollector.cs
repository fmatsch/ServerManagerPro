using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace ServerManagerPro.Core;

public static class ServiceCollector
{
    public static List<ServiceRow> Collect()
    {
        var list = new List<ServiceRow>();
        using var s = new ManagementObjectSearcher("SELECT * FROM Win32_Service");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                var row = new ServiceRow
                {
                    Name = Str(o, "Name"),
                    DisplayName = Str(o, "DisplayName"),
                    State = Str(o, "State"),
                    StartMode = Str(o, "StartMode"),
                    Account = Str(o, "StartName"),
                    Pid = (int)Num(o, "ProcessId"),
                    ExitCode = (uint)Num(o, "ExitCode"),
                    Delayed = o["DelayedAutoStart"] is bool b && b,
                };
                row.Info = row.State != "Running" && row.StartMode == "Auto" && row.ExitCode != 0 ? $"Fehlercode {row.ExitCode}" : "";
                list.Add(row);
            }
        }
        return list.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string Str(ManagementBaseObject o, string p) { try { return o[p]?.ToString() ?? ""; } catch { return ""; } }
    static double Num(ManagementBaseObject o, string p) { try { return o[p] == null ? 0 : Convert.ToDouble(o[p]); } catch { return 0; } }

    public static string Control(string name, string action)
    {
        try
        {
            using var sc = new ServiceController(name);
            var to = TimeSpan.FromSeconds(30);
            switch (action)
            {
                case "start":
                    if (sc.Status != ServiceControllerStatus.Running) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, to); }
                    break;
                case "stop":
                    if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, to); }
                    break;
                case "restart":
                    if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, to); }
                    sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, to);
                    break;
            }
            return "";
        }
        catch (Exception ex) { return ex.GetBaseException().Message; }
    }

    // ---- Gesundheitsprüfung der Dienste ----
    static readonly (string svc, string role, bool critical)[] Roles =
    {
        ("NTDS", "Active Directory", true), ("DNS", "DNS-Server", true), ("DHCPServer", "DHCP-Server", true),
        ("Kdc", "Kerberos KDC", true), ("Netlogon", "Netlogon", true), ("DFSR", "DFS-Replikation", true),
        ("W3SVC", "IIS Webserver", true), ("TermService", "Remotedesktop", true), ("LanmanServer", "Dateiserver (SMB)", true),
        ("Spooler", "Druckwarteschlange", false), ("W32Time", "Zeitdienst", false), ("Schedule", "Aufgabenplanung", true),
        ("EventLog", "Ereignisprotokoll", true), ("Winmgmt", "WMI", true), ("RpcSs", "RPC", true), ("Dnscache", "DNS-Client", false),
        ("MpsSvc", "Windows-Firewall", false), ("WinDefend", "Microsoft Defender", false), ("vmms", "Hyper-V", true),
    };

    public static List<CheckResult> Check(List<ServiceRow> services, AppSettings cfg)
    {
        var res = new List<CheckResult>();
        const string cat = "Dienste";
        var byName = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        var okRoles = new List<string>();
        foreach (var (svc, role, critical) in Roles)
        {
            if (!byName.TryGetValue(svc, out var s)) continue;
            if (s.IsRunning) { okRoles.Add(role); continue; }
            if (s.StartMode == "Auto")
                res.Add(new CheckResult(cat, role, critical ? Health.Critical : Health.Warning,
                    $"Dienst \"{s.DisplayName}\" ({s.Name}) ist gestoppt, obwohl er automatisch startet."));
        }
        if (okRoles.Count > 0)
            res.Add(new CheckResult(cat, "Server-Rollen & Basisdienste", Health.Ok, string.Join(", ", okRoles) + " – laufen."));

        foreach (var w in cfg.WatchedServices.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var s = services.FirstOrDefault(x => x.Name.Equals(w, StringComparison.OrdinalIgnoreCase)
                                              || x.DisplayName.Equals(w, StringComparison.OrdinalIgnoreCase));
            if (s == null) res.Add(new CheckResult(cat, $"Überwacht: {w}", Health.Warning, "Dienst nicht gefunden."));
            else if (s.IsRunning) res.Add(new CheckResult(cat, $"Überwacht: {s.DisplayName}", Health.Ok, "Läuft."));
            else res.Add(new CheckResult(cat, $"Überwacht: {s.DisplayName}", Health.Critical, $"Dienst ist {s.State}."));
        }

        var failed = services.Where(s => s.StartMode == "Auto" && !s.IsRunning && s.ExitCode != 0
                                         && s.ExitCode != 1077 /* nie gestartet */ && s.ExitCode != 1056).ToList();
        if (failed.Count > 0)
            res.Add(new CheckResult(cat, "Fehlgeschlagene Autostart-Dienste", Health.Warning,
                $"{failed.Count} Dienst(e) mit Fehlercode beendet: " + string.Join(", ", failed.Take(5).Select(f => f.DisplayName)) + (failed.Count > 5 ? " …" : "")));
        else
            res.Add(new CheckResult(cat, "Autostart-Dienste", Health.Ok,
                $"{services.Count(s => s.IsRunning)} von {services.Count} Diensten laufen, keiner ist mit Fehler beendet."));
        return res;
    }
}
