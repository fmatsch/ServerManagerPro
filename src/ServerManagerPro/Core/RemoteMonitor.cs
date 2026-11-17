using System.Net.Http;
using System.Text.Json;

namespace ServerManagerPro.Core;

public sealed class RemoteState
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public DateTime? Fetched { get; set; }
    public int Failures { get; set; }
    public StatusPayload? Payload { get; set; }

    public Health Status => !Ok ? Health.Critical : Enum.TryParse<Health>(Payload?.Overall, out var h) ? h : Health.Unknown;
    public string StatusText => Loc.T(!Ok ? "Nicht erreichbar" : Status.Label());
    public string ScoreText => Ok && Payload != null ? $"{Payload.Score}/100" : "–";
    public string FetchedText => Fetched?.ToString("T") ?? "–";
    public string Problems
    {
        get
        {
            if (!Ok) return Loc.T(Error);
            var bad = Payload?.Checks.Where(c => c.Status is "Warning" or "Critical").ToList() ?? new();
            return bad.Count == 0 ? "–" : string.Join("; ", bad.Take(3).Select(c => Loc.T(c.Name))) + (bad.Count > 3 ? $" … (+{bad.Count - 3})" : "");
        }
    }
}

/// <summary>Fragt entfernte Server (die ihren schreibgeschützten Status-Zugriff eingeschaltet haben) regelmäßig ab.</summary>
public sealed class RemoteMonitor : IDisposable
{
    readonly AppSettings _cfg;
    readonly AlertEngine _alerts;
    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<string, int> _failures = new();
    public event Action<List<RemoteState>>? Updated;
    public List<RemoteState> Last { get; private set; } = new();

    public RemoteMonitor(AppSettings cfg, AlertEngine alerts) { _cfg = cfg; _alerts = alerts; }

    public void Start() => _ = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await PollAll(); } catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); } catch { return; }
        }
    });

    public async Task PollAll()
    {
        var entries = _cfg.RemoteServers.Select(Parse).Where(e => e != null).Select(e => e!).ToList();
        var states = await Task.WhenAll(entries.Select(Poll));
        Last = states.ToList();
        Updated?.Invoke(Last);
        foreach (var st in Last)
        {
            var checks = new List<CheckResult>
            {
                new("Erreichbarkeit", "Server nicht erreichbar", st.Failures >= 2 ? Health.Critical : Health.Ok,
                    st.Failures >= 2 ? $"{st.Name} antwortet nicht: {st.Error}" : "Erreichbar."),
            };
            if (st.Ok && st.Payload != null)
                checks.AddRange(st.Payload.Checks.Select(c => new CheckResult(c.Category, c.Name, Enum.TryParse<Health>(c.Status, out var h) ? h : Health.Unknown, c.Detail)));
            _alerts.Process(st.Name, checks);
        }
    }

    sealed record Entry(string Name, string Url, string Token, string Pin);

    static Entry? Parse(string line)
    {
        var p = line.Split('|').Select(x => x.Trim()).ToArray();
        if (p.Length < 3 || p[1].Length == 0 || p[2].Length == 0) return null;
        return new Entry(p[0].Length > 0 ? p[0] : p[1], p[1].TrimEnd('/'), p[2], p.Length > 3 ? p[3].Replace(" ", "").Replace(":", "") : "");
    }

    async Task<RemoteState> Poll(Entry e)
    {
        var st = new RemoteState { Name = e.Name, Url = e.Url };
        try
        {
            using var handler = new HttpClientHandler
            {
                // Mit Fingerabdruck: genau dieses Zertifikat akzeptieren (auch selbstsigniert). Ohne: normale Prüfung.
                ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                    e.Pin.Length > 0 ? cert != null && cert.GetCertHashString().Equals(e.Pin, StringComparison.OrdinalIgnoreCase)
                                     : errors == System.Net.Security.SslPolicyErrors.None,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var req = new HttpRequestMessage(HttpMethod.Get, e.Url + "/api/status");
            req.Headers.Authorization = new("Bearer", e.Token);
            using var r = await http.SendAsync(req);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{(int)r.StatusCode} {r.ReasonPhrase}");
            var p = JsonSerializer.Deserialize<StatusPayload>(await r.Content.ReadAsStringAsync());
            st.Payload = p; st.Ok = p != null; st.Fetched = DateTime.Now;
            if (p == null) st.Error = "leere Antwort";
        }
        catch (Exception ex)
        {
            var b = ex.GetBaseException();
            st.Error = b is System.Security.Authentication.AuthenticationException || b.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || b.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                ? "Zertifikat nicht vertraut – Fingerabdruck des Servers eintragen" : b.Message;
        }
        lock (_failures)
        {
            _failures[e.Name] = st.Ok ? 0 : _failures.GetValueOrDefault(e.Name) + 1;
            st.Failures = _failures[e.Name];
        }
        return st;
    }

    public void Dispose() => _cts.Cancel();
}
