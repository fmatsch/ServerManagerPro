using System.DirectoryServices;
using System.DirectoryServices.ActiveDirectory;

namespace ServerManagerPro.Core;

/// <summary>Zustand des Active-Directory-Domänendienstes (nur auf Domänencontrollern).</summary>
public static class AdCollector
{
    const string Cat = "Active Directory";
    static readonly string[] AdServices = { "NTDS", "Kdc", "Netlogon", "DNS", "DFSR", "ADWS", "IsmServ", "W32Time" };

    public static bool IsDomainController(List<ServiceRow> services) =>
        services.Any(s => s.Name.Equals("NTDS", StringComparison.OrdinalIgnoreCase));

    public static (AdInfo info, List<CheckResult> checks) Collect(List<ServiceRow> services, AppSettings cfg)
    {
        var info = new AdInfo { DcName = Environment.MachineName };
        var checks = new List<CheckResult>();

        // Dienste
        foreach (var n in AdServices)
        {
            var s = services.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (s == null) continue;
            info.Services.Add(s);
            bool critical = n is "NTDS" or "Kdc" or "Netlogon" or "DNS" or "DFSR";
            if (!s.IsRunning && s.StartMode != "Disabled")
                checks.Add(new CheckResult(Cat, $"Dienst {s.DisplayName}", critical ? Health.Critical : Health.Warning, $"{s.Name} ist {s.State}."));
        }
        var ntds = info.Services.FirstOrDefault(s => s.Name.Equals("NTDS", StringComparison.OrdinalIgnoreCase));
        if (ntds is { IsRunning: true })
            checks.Add(new CheckResult(Cat, "Domänendienst (NTDS)", Health.Ok, "Active Directory-Domänendienste laufen."));

        // SYSVOL/NETLOGON
        try
        {
            info.SysvolOk = Directory.Exists(@"\\localhost\SYSVOL");
            info.NetlogonOk = Directory.Exists(@"\\localhost\NETLOGON");
            checks.Add(new CheckResult(Cat, "SYSVOL / NETLOGON",
                info.SysvolOk && info.NetlogonOk ? Health.Ok : Health.Critical,
                info.SysvolOk && info.NetlogonOk ? "Beide Freigaben sind erreichbar." : "SYSVOL oder NETLOGON ist nicht freigegeben – Gruppenrichtlinien und Anmeldeskripte fallen aus."));
        }
        catch { }

        if (ntds is not { IsRunning: true })
        {
            info.Status = Health.Critical;
            return (info, checks);
        }

        try { Directory_(info, checks, cfg); }
        catch (Exception ex)
        {
            info.Error = ex.GetBaseException().Message;
            checks.Add(new CheckResult(Cat, "Verzeichnisabfrage", Health.Warning, "AD konnte nicht abgefragt werden: " + info.Error));
        }

        info.Status = checks.Aggregate(Health.Ok, (a, c) => c.Status is Health.Warning or Health.Critical ? HealthExt.Worst(a, c.Status) : a);
        return (info, checks);
    }

    static void Directory_(AdInfo info, List<CheckResult> checks, AppSettings cfg)
    {
        using var root = new DirectoryEntry("LDAP://RootDSE");
        string nc = root.Properties["defaultNamingContext"].Value?.ToString() ?? "";
        info.Domain = nc.Replace("DC=", "").Replace(",", ".");
        using var domainEntry = new DirectoryEntry("LDAP://" + nc);

        int Count(string filter)
        {
            using var s = new DirectorySearcher(domainEntry, filter, new[] { "distinguishedName" }) { PageSize = 1000, SizeLimit = 0 };
            using var res = s.FindAll();
            return res.Count;
        }

        const string user = "(&(objectCategory=person)(objectClass=user)";
        info.Users = Count(user + ")");
        info.UsersDisabled = Count(user + "(userAccountControl:1.2.840.113556.1.4.803:=2))");
        info.UsersEnabled = info.Users - info.UsersDisabled;
        info.PasswordNeverExpires = Count(user + "(userAccountControl:1.2.840.113556.1.4.803:=65536)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))");
        long cutoff = DateTime.UtcNow.AddDays(-90).ToFileTimeUtc();
        info.Inactive90 = Count(user + $"(!(userAccountControl:1.2.840.113556.1.4.803:=2))(lastLogonTimestamp<={cutoff}))");
        info.Computers = Count("(objectCategory=computer)");
        info.Groups = Count("(objectCategory=group)");

        // Wirklich gesperrte Konten: das berechnete Attribut msDS-User-Account-Control-Computed (Bit 0x10)
        using (var s = new DirectorySearcher(domainEntry, user + "(lockoutTime>=1))", new[] { "msDS-User-Account-Control-Computed" }) { PageSize = 1000 })
        using (var res = s.FindAll())
            foreach (SearchResult r in res)
                if (r.Properties["msDS-User-Account-Control-Computed"].Count > 0
                    && (Convert.ToInt32(r.Properties["msDS-User-Account-Control-Computed"][0]) & 0x10) != 0)
                    info.UsersLocked++;

        try
        {
            // Domänen-Admins über die bekannte RID 512 (sprachunabhängig)
            var domSid = new System.Security.Principal.SecurityIdentifier((byte[])domainEntry.Properties["objectSid"].Value!, 0);
            using var da = new DirectoryEntry($"LDAP://<SID={domSid}-512>");
            info.DomainAdmins = da.Properties["member"].Count;
        }
        catch { }

        checks.Add(new CheckResult(Cat, "Benutzerkonten", Health.Ok,
            $"{info.UsersEnabled} aktiv, {info.UsersDisabled} deaktiviert, {info.Computers} Computerkonten, {info.Groups} Gruppen."));
        checks.Add(new CheckResult(Cat, "Gesperrte Konten", info.UsersLocked > 0 ? Health.Warning : Health.Ok,
            info.UsersLocked > 0 ? $"{info.UsersLocked} Konto/Konten sind gerade gesperrt." : "Keine gesperrten Konten."));
        checks.Add(new CheckResult(Cat, "Domänen-Admins", info.DomainAdmins > 10 ? Health.Warning : Health.Ok,
            $"{info.DomainAdmins} Mitglied(er)" + (info.DomainAdmins > 10 ? " – das sind ungewöhnlich viele." : ".")));
        if (info.Inactive90 > 0)
            checks.Add(new CheckResult(Cat, "Inaktive Konten", Health.Info, $"{info.Inactive90} aktive Konten ohne Anmeldung in 90 Tagen."));
        if (info.PasswordNeverExpires > 0)
            checks.Add(new CheckResult(Cat, "Kennwörter ohne Ablauf", Health.Info, $"{info.PasswordNeverExpires} aktive Konten haben ein Kennwort, das nie abläuft."));

        // Domäne, Gesamtstruktur, Rollen
        try
        {
            using var domain = System.DirectoryServices.ActiveDirectory.Domain.GetCurrentDomain();
            using var forest = Forest.GetCurrentForest();
            info.Domain = domain.Name;
            info.Forest = forest.Name;
            info.Level = $"Domäne {domain.DomainMode}, Gesamtstruktur {forest.ForestMode}";
            info.Fsmo.Add(new("PDC-Emulator", domain.PdcRoleOwner.Name));
            info.Fsmo.Add(new("RID-Master", domain.RidRoleOwner.Name));
            info.Fsmo.Add(new("Infrastruktur-Master", domain.InfrastructureRoleOwner.Name));
            info.Fsmo.Add(new("Schema-Master", forest.SchemaRoleOwner.Name));
            info.Fsmo.Add(new("Domänennamen-Master", forest.NamingRoleOwner.Name));
            using var dc = DomainController.GetDomainController(new DirectoryContext(DirectoryContextType.DirectoryServer, System.Net.Dns.GetHostEntry("").HostName));
            info.DcName = dc.Name;
            info.Site = dc.SiteName;

            foreach (ReplicationNeighbor n in dc.GetAllReplicationNeighbors())
            {
                bool ok = n.LastSyncResult == 0;
                info.Replication.Add(new ReplRow
                {
                    Partner = n.SourceServer,
                    Partition = n.PartitionName,
                    LastSuccess = n.LastSuccessfulSync == DateTime.MinValue ? null : n.LastSuccessfulSync.ToLocalTime(),
                    Failures = n.ConsecutiveFailureCount,
                    Result = ok ? "Erfolgreich" : $"Fehler {n.LastSyncResult}",
                    Ok = ok,
                });
            }
            int bad = info.Replication.Count(r => !r.Ok);
            if (info.Replication.Count == 0)
                checks.Add(new CheckResult(Cat, "Replikation", Health.Info, "Keine Replikationspartner (einziger Domänencontroller)."));
            else
                checks.Add(new CheckResult(Cat, "Replikation", bad > 0 ? Health.Critical : Health.Ok,
                    bad > 0 ? $"{bad} von {info.Replication.Count} Replikationsverbindungen fehlerhaft." : $"{info.Replication.Count} Verbindungen, alle erfolgreich."));
        }
        catch (Exception ex)
        {
            checks.Add(new CheckResult(Cat, "Rollen & Replikation", Health.Unknown, ex.GetBaseException().Message));
        }
    }
}
