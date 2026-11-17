using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ServerManagerPro.Core;

public sealed record AlertMessage(string Event, Health Level, string Source, string Category, string Name, string Detail, DateTime Time, TimeSpan? Duration = null, List<AlertMessage>? Items = null);

public sealed class AlertLogRow
{
    public DateTime Time { get; init; }
    public string Source { get; init; } = "";
    public string Level { get; init; } = "";
    public string Event { get; init; } = "";
    public string Text { get; init; } = "";
    public string Result { get; set; } = "";
}

/// <summary>
/// Entscheidet, wann gemeldet wird: Ein Problem muss erst eine Weile bestehen (Verzögerung), wird bei Bedarf erneut
/// gemeldet (Erinnerung), meldet Verschärfungen sofort und kündigt die Entwarnung an. Viele gleichzeitige Probleme werden
/// zu einer Nachricht zusammengefasst; pro Stunde gibt es eine Obergrenze.
/// </summary>
public sealed class AlertEngine
{
    sealed class State { public int Level; public DateTime Since, LastSent; public bool Open; public string Detail = ""; }

    readonly AppSettings _cfg;
    readonly Dictionary<string, State> _state = new();
    readonly Queue<DateTime> _sent = new();
    readonly object _lock = new();
    bool _limitNotified;
    readonly List<AlertLogRow> _history = new();

    public AlertEngine(AppSettings cfg) { _cfg = cfg; }

    /// <summary>Lokale Benachrichtigung (Tray-Sprechblase): Titel, Text.</summary>
    public event Action<string, string>? Tray;
    public event Action? HistoryChanged;

    public List<AlertLogRow> History { get { lock (_lock) return _history.ToList(); } }

    int Level(Health h) => h == Health.Critical ? 2 : h == Health.Warning && !_cfg.AlertOnlyCritical ? 1 : 0;

    /// <summary>Wertet die Prüfungen einer Quelle (dieser Rechner oder ein entfernter Server) aus.</summary>
    public void Process(string source, IEnumerable<CheckResult> checks, DateTime? at = null)
    {
        if (!_cfg.AlertsEnabled) return;
        var now = at ?? DateTime.Now;
        var delay = TimeSpan.FromMinutes(Math.Max(0, _cfg.AlertDelayMinutes));
        var reminder = _cfg.AlertReminderHours > 0 ? TimeSpan.FromHours(_cfg.AlertReminderHours) : TimeSpan.Zero;
        var events = new List<AlertMessage>();
        var seen = new HashSet<string>();
        string prefix = source + "\u0001";

        lock (_lock)
        {
            foreach (var c in checks)
            {
                string key = prefix + c.Category + "\u0001" + c.Name;
                seen.Add(key);
                int lvl = Level(c.Status);
                if (lvl == 0)
                {
                    if (_state.Remove(key, out var gone) && gone.Open && _cfg.AlertSendRecovery)
                        events.Add(new AlertMessage("resolved", Health.Ok, source, c.Category, c.Name, c.Detail, now, now - gone.Since));
                    continue;
                }
                if (!_state.TryGetValue(key, out var st)) _state[key] = st = new State { Level = lvl, Since = now, Detail = c.Detail };
                else
                {
                    if (lvl > st.Level && st.Open)
                    {
                        st.LastSent = now;
                        events.Add(new AlertMessage("escalated", c.Status, source, c.Category, c.Name, c.Detail, now, now - st.Since));
                    }
                    st.Level = lvl; st.Detail = c.Detail;
                }
                if (!st.Open && now - st.Since >= delay)
                {
                    st.Open = true; st.LastSent = now;
                    events.Add(new AlertMessage("open", c.Status, source, c.Category, c.Name, c.Detail, now));
                }
                else if (st.Open && reminder > TimeSpan.Zero && now - st.LastSent >= reminder)
                {
                    st.LastSent = now;
                    events.Add(new AlertMessage("reminder", c.Status, source, c.Category, c.Name, c.Detail, now, now - st.Since));
                }
            }
            // Prüfungen, die es nicht mehr gibt (z. B. entfernte Rolle), still vergessen
            foreach (var k in _state.Keys.Where(k => k.StartsWith(prefix) && !seen.Contains(k)).ToList()) _state.Remove(k);
        }
        if (events.Count > 0) Dispatch(source, events, now);
    }

    void Dispatch(string source, List<AlertMessage> events, DateTime now)
    {
        List<AlertMessage> toSend;
        if (events.Count > 4)
            toSend = new() { new AlertMessage("multiple", events.Max(e => e.Level), source, "", "", "", now, null, events) };
        else toSend = events;

        lock (_lock)
        {
            while (_sent.Count > 0 && now - _sent.Peek() > TimeSpan.FromHours(1)) _sent.Dequeue();
            int max = Math.Max(1, _cfg.AlertMaxPerHour);
            if (_sent.Count + toSend.Count > max)
            {
                if (!_limitNotified)
                {
                    _limitNotified = true;
                    toSend = new() { new AlertMessage("limit", Health.Warning, source, "", "", $"{max}", now) };
                }
                else { AddLog(new AlertLogRow { Time = now, Source = source, Level = "–", Event = "unterdrückt", Text = $"{events.Count} Meldung(en) wegen des Stundenlimits nicht gesendet" }); return; }
            }
            else _limitNotified = false;
            foreach (var _ in toSend) _sent.Enqueue(now);
        }
        foreach (var m in toSend) _ = SendAsync(m);
    }

    /// <summary>Sendet eine Testnachricht an alle Ziele und liefert das Ergebnis je Ziel.</summary>
    public async Task<List<string>> SendTestAsync()
    {
        var msg = new AlertMessage("test", Health.Info, Environment.MachineName, "", "", "", DateTime.Now);
        return await SendAsync(msg);
    }

    async Task<List<string>> SendAsync(AlertMessage m)
    {
        var results = new List<string>();
        var row = new AlertLogRow { Time = m.Time, Source = m.Source, Level = AlertText.LevelLabel(m.Level), Event = AlertText.EventLabel(m.Event), Text = AlertText.Title(m) };
        foreach (var line in _cfg.AlertTargets.Where(l => !string.IsNullOrWhiteSpace(l)).ToList())
        {
            string type = line.Split('|')[0].Trim().ToLowerInvariant();
            try
            {
                if (type == "tray") { Tray?.Invoke(AlertText.Title(m), AlertText.Body(m)); results.Add("tray: ok"); continue; }
                string err = await AlertSenders.SendAsync(line, m);
                results.Add(err.Length == 0 ? $"{type}: ok" : $"{type}: {err}");
            }
            catch (Exception ex) { results.Add($"{type}: {ex.GetBaseException().Message}"); }
        }
        if (results.Count == 0) results.Add("keine Ziele eingetragen");
        row.Result = string.Join("; ", results);
        lock (_lock) AddLog(row);
        HistoryChanged?.Invoke();
        return results;
    }

    void AddLog(AlertLogRow row)
    {
        _history.Insert(0, row);
        if (_history.Count > 200) _history.RemoveRange(200, _history.Count - 200);
    }
}

/// <summary>Texte der Nachrichten (deutsch oder englisch, je nach Spracheinstellung).</summary>
public static class AlertText
{
    public static string LevelLabel(Health h) => Loc.T(h switch { Health.Critical => "Kritisch", Health.Warning => "Warnung", Health.Ok => "In Ordnung", Health.Info => "Hinweis", _ => "Unbekannt" });

    public static string EventLabel(string ev) => Loc.T(ev switch
    {
        "open" => "Problem", "reminder" => "Erinnerung", "resolved" => "Behoben", "escalated" => "Verschärft",
        "test" => "Test", "multiple" => "Mehrere Probleme", "limit" => "Limit erreicht", _ => ev,
    });

    static string Dur(TimeSpan? d) => d.HasValue ? BackupCollector.Age(d.Value).TrimEnd('.') : "";

    public static string Title(AlertMessage m)
    {
        switch (m.Event)
        {
            case "test": return Loc.T("[Test] ServerManagerPro auf {0}").Replace("{0}", m.Source);
            case "limit": return Loc.T("[Limit] {0}: zu viele Meldungen").Replace("{0}", m.Source);
            case "multiple": return $"[{LevelLabel(m.Level)}] {m.Source}: " + Loc.T("{0} Meldungen").Replace("{0}", (m.Items?.Count ?? 0).ToString());
            case "resolved": return $"[{Loc.T("Behoben")}] {m.Source}: {Loc.T(m.Name)}";
            default: return $"[{LevelLabel(m.Level)}] {m.Source}: {Loc.T(m.Name)}";
        }
    }

    public static string Body(AlertMessage m)
    {
        switch (m.Event)
        {
            case "test": return Loc.T("Dies ist eine Testnachricht. Wenn Sie sie lesen, funktioniert dieses Ziel.");
            case "limit": return Loc.T("Es wurden mehr als {0} Meldungen pro Stunde erzeugt. Weitere Meldungen werden in dieser Stunde unterdrückt.").Replace("{0}", m.Detail);
            case "multiple":
                return string.Join("\n", (m.Items ?? new()).Select(i => $"• [{(i.Event == "resolved" ? Loc.T("Behoben") : LevelLabel(i.Level))}] {Loc.T(i.Category)} – {Loc.T(i.Name)}: {Loc.T(i.Detail)}"));
            case "resolved":
                return $"{Loc.T(m.Category)}: {Loc.T(m.Detail)}" + (m.Duration.HasValue ? "\n" + Loc.T("Das Problem bestand {0}.").Replace("{0}", Dur(m.Duration)) : "");
            case "reminder":
            case "escalated":
                return $"{Loc.T(m.Category)}: {Loc.T(m.Detail)}\n" + Loc.T("Besteht seit {0}.").Replace("{0}", Dur(m.Duration));
            default: return $"{Loc.T(m.Category)}: {Loc.T(m.Detail)}";
        }
    }
}

/// <summary>Die einzelnen Kanäle. Es wird nur nach außen gesendet; es gibt keine offenen Ports.</summary>
public static class AlertSenders
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ServerManagerPro-Alert");
        return h;
    }

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    static string Err(HttpResponseMessage r) => r.IsSuccessStatusCode ? "" : $"{(int)r.StatusCode} {r.ReasonPhrase}";

    /// <summary>Liefert "" bei Erfolg, sonst die Fehlermeldung.</summary>
    public static async Task<string> SendAsync(string line, AlertMessage m)
    {
        var p = line.Split('|').Select(x => x.Trim()).ToArray();
        string type = p[0].ToLowerInvariant();
        string title = AlertText.Title(m), body = AlertText.Body(m);
        int sev = m.Event == "resolved" || m.Event == "test" ? 0 : m.Level == Health.Critical ? 2 : 1;
        if (type == "tray") return "";
        if (p.Length < 2 || p[1].Length == 0) return "Adresse fehlt";

        switch (type)
        {
            case "ntfy":
            {
                var uri = new Uri(p[1]);
                string topic = uri.AbsolutePath.Trim('/');
                if (topic.Length == 0) return "Thema fehlt (z. B. https://ntfy.sh/mein-thema)";
                using var req = new HttpRequestMessage(HttpMethod.Post, uri.GetLeftPart(UriPartial.Authority) + "/")
                {
                    Content = Json(new { topic, title, message = body, priority = sev == 2 ? 5 : sev == 1 ? 4 : 3, tags = new[] { sev == 2 ? "rotating_light" : sev == 1 ? "warning" : "white_check_mark" } }),
                };
                if (p.Length > 2 && p[2].Length > 0) req.Headers.Authorization = new("Bearer", p[2]);
                using var r = await Http.SendAsync(req);
                return Err(r);
            }
            case "teams":
            {
                // Teams-Workflow-Webhook: Adaptive Card
                var card = new
                {
                    type = "message",
                    attachments = new[]
                    {
                        new
                        {
                            contentType = "application/vnd.microsoft.card.adaptive",
                            content = new
                            {
                                type = "AdaptiveCard", version = "1.4",
                                body = new object[]
                                {
                                    new { type = "TextBlock", text = title, weight = "Bolder", size = "Medium", wrap = true, color = sev == 2 ? "Attention" : sev == 1 ? "Warning" : "Good" },
                                    new { type = "TextBlock", text = body.Replace("\n", "\n\n"), wrap = true },
                                },
                            },
                        },
                    },
                };
                using var r = await Http.PostAsync(p[1], Json(card));
                return Err(r);
            }
            case "slack":
            {
                using var r = await Http.PostAsync(p[1], Json(new { text = $"*{title}*\n{body}" }));
                return Err(r);
            }
            case "telegram":
            {
                if (p.Length < 3) return "Format: telegram|BotToken|ChatId";
                string baseUrl = p.Length > 3 && p[3].Length > 0 ? p[3].TrimEnd('/') : "https://api.telegram.org";
                using var r = await Http.PostAsync($"{baseUrl}/bot{p[1]}/sendMessage", Json(new { chat_id = p[2], text = $"{title}\n{body}", disable_web_page_preview = true }));
                return Err(r);
            }
            case "webhook":
            {
                using var r = await Http.PostAsync(p[1], Json(new
                {
                    host = Environment.MachineName, source = m.Source, @event = m.Event, level = m.Level.ToString().ToLowerInvariant(),
                    category = m.Category, name = m.Name, detail = m.Detail, time = m.Time.ToString("o"),
                    durationSeconds = m.Duration?.TotalSeconds, title, message = body,
                    items = m.Items?.Select(i => new { i.Event, level = i.Level.ToString().ToLowerInvariant(), i.Category, i.Name, i.Detail }),
                }));
                return Err(r);
            }
            default: return $"unbekannter Kanal \"{type}\"";
        }
    }
}
