namespace ServerManagerPro.Core;

public sealed class ConnRow
{
    /// <summary>"Lokal (LAN)", "Internet" oder "Lauschend".</summary>
    public string Scope { get; set; } = "";
    public string Process { get; set; } = "";
    public int Pid { get; set; }
    public string Local { get; set; } = "";
    public string Remote { get; set; } = "";
    public string State { get; set; } = "";
    public string ScopeText => Loc.T(Scope);
    public string StateText => Loc.T(State);
}

public sealed class AdInfo
{
    public string Domain { get; set; } = "";
    public string Forest { get; set; } = "";
    public string DcName { get; set; } = "";
    public string Site { get; set; } = "";
    public string Level { get; set; } = "";
    public int Users, UsersEnabled, UsersDisabled, UsersLocked, PasswordNeverExpires, Inactive90, Computers, Groups, DomainAdmins;
    public bool SysvolOk, NetlogonOk;
    public string Error { get; set; } = "";
    public Health Status { get; set; }
    public List<KeyValuePair<string, string>> Fsmo { get; } = new();
    public List<ReplRow> Replication { get; } = new();
    public List<ServiceRow> Services { get; } = new();
    public bool Demo { get; set; }
}

public sealed class ReplRow
{
    public string Partner { get; set; } = "";
    public string Partition { get; set; } = "";
    public DateTime? LastSuccess { get; set; }
    public int Failures { get; set; }
    public string Result { get; set; } = "";
    public bool Ok { get; set; }
}

public sealed class DhcpScopeRow
{
    public string ScopeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Range { get; set; } = "";
    public string State { get; set; } = "";
    public int Total { get; set; }
    public int Leased { get; set; }
    public int Reserved { get; set; }
    public int Excluded { get; set; }
    public int Declined { get; set; }
    /// <summary>Laut DHCP-Server noch nicht vergeben.</summary>
    public int ServerFree { get; set; }
    /// <summary>Tatsächlich vergebbar: laut DHCP frei und im Scan ohne Antwort.</summary>
    public int ReallyFree { get; set; }
    public int Conflicts { get; set; }
    public bool Scanned { get; set; }
    public string Note { get; set; } = "";
    public double UsedPct => Total <= 0 ? 0 : (Total - ReallyFree) * 100.0 / Total;
}

public sealed class IpRow
{
    public string Ip { get; set; } = "";
    public uint Sort { get; set; }
    public string Scope { get; set; } = "";
    /// <summary>Frei, Vergeben, Reserviert, Ausgeschlossen, Konflikt, Abgelehnt, Unbekannt</summary>
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Host { get; set; } = "";
    public bool Alive { get; set; }
    public string StatusText => Loc.T(Status);
    public string DetailText => Loc.T(Detail);
}

/// <summary>Allgemeine Detailtabelle für die Seite „Sicherheit &amp; Rollen“.</summary>
public sealed class DetailSection
{
    public string Group { get; set; } = "";
    public string Title { get; set; } = "";
    public Health Status { get; set; } = Health.Ok;
    public string Note { get; set; } = "";
    public string[] Headers { get; set; } = Array.Empty<string>();
    public List<string[]> Rows { get; } = new();
    public DetailSection Add(params string[] cells) { Rows.Add(cells); return this; }
}

public sealed class DhcpInfo
{
    public bool ServiceRunning { get; set; }
    public string Error { get; set; } = "";
    public DateTime? ScanTime { get; set; }
    public bool Scanning { get; set; }
    public string ScanProgress { get; set; } = "";
    public List<DhcpScopeRow> Scopes { get; } = new();
    public List<IpRow> Addresses { get; } = new();
    public List<DetailSection> Sections { get; } = new();
    public bool Demo { get; set; }
}
