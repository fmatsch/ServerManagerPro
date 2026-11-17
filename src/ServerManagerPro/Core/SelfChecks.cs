using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace ServerManagerPro.Core;

/// <summary>Kopflose Selbsttests für Alarme und Status-Zugriff (--alerttest, --servetest). Sie benutzen nur lokale Testserver und senden nichts nach außen.</summary>
public static class SelfChecks
{
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static async Task<string> AlertTest()
    {
        var sb = new StringBuilder();
        int port = FreePort();
        var captured = new List<string>();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                try
                {
                    var ctx = await http.GetContextAsync();
                    using var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    string body = await rd.ReadToEndAsync();
                    lock (captured) captured.Add($"{ctx.Request.HttpMethod} {ctx.Request.Url!.AbsolutePath} auth={(ctx.Request.Headers["Authorization"] ?? "-")} ct={ctx.Request.ContentType}\n    {body}");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close(Encoding.UTF8.GetBytes("ok"), true);
                }
                catch { }
            }
        });

        var cfg = new AppSettings
        {
            AlertsEnabled = true, AlertDelayMinutes = 3, AlertReminderHours = 12, AlertSendRecovery = true, AlertMaxPerHour = 100,
            AlertTargets = new()
            {
                $"ntfy|http://127.0.0.1:{port}/mein-thema|geheimtoken",
                $"slack|http://127.0.0.1:{port}/slack",
                $"teams|http://127.0.0.1:{port}/teams",
                $"telegram|BOTTOKEN|4711|http://127.0.0.1:{port}",
                $"webhook|http://127.0.0.1:{port}/hook",
            },
        };
        var engine = new AlertEngine(cfg);
        var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
        CheckResult Bad(string n, Health h = Health.Critical) => new("Dienste", n, h, $"{n} ist gestoppt, größer als 5 % – Ünïcode ✓");
        CheckResult Good(string n) => new("Dienste", n, Health.Ok, "läuft");

        async Task Step(string name, string src, IEnumerable<CheckResult> checks, TimeSpan at)
        {
            int before; lock (captured) before = captured.Count;
            engine.Process(src, checks, t0 + at);
            await Task.Delay(700);
            int after; lock (captured) after = captured.Count;
            sb.AppendLine($"{name}: {(after - before)} Anfrage(n) ({(after - before) / 5.0:0.#} Meldung(en) an 5 Kanäle)");
        }

        await Step("1  Problem erscheint (0 min, Verzögerung 3 min) → keine Meldung erwartet", "SRV1", new[] { Bad("SQL-Dienst") }, TimeSpan.Zero);
        await Step("2  nach 1 min → keine Meldung erwartet", "SRV1", new[] { Bad("SQL-Dienst") }, TimeSpan.FromMinutes(1));
        await Step("3  nach 4 min → 1 Meldung (Problem) erwartet", "SRV1", new[] { Bad("SQL-Dienst") }, TimeSpan.FromMinutes(4));
        await Step("4  nach 5 min → keine doppelte Meldung erwartet", "SRV1", new[] { Bad("SQL-Dienst") }, TimeSpan.FromMinutes(5));
        await Step("5  nach 13 h → 1 Erinnerung erwartet", "SRV1", new[] { Bad("SQL-Dienst") }, TimeSpan.FromHours(13));
        await Step("6  behoben → 1 Entwarnung erwartet", "SRV1", new[] { Good("SQL-Dienst") }, TimeSpan.FromHours(14));
        await Step("7  kurzer Ausschlag (2 min), dann behoben → keine Meldung erwartet", "SRV1", new[] { Bad("Kurz") }, TimeSpan.FromHours(15));
        await Step("7b behoben nach 2 min", "SRV1", new[] { Good("Kurz") }, TimeSpan.FromHours(15) + TimeSpan.FromMinutes(2));

        var six = Enumerable.Range(1, 6).Select(i => Bad("Dienst" + i)).ToList();
        await Step("8  6 Probleme beginnen", "SRV2", six, TimeSpan.FromHours(20));
        await Step("8b nach 4 min → 1 zusammengefasste Meldung erwartet", "SRV2", six, TimeSpan.FromHours(20) + TimeSpan.FromMinutes(4));

        await Step("9  Warnung beginnt", "SRV3", new[] { Bad("Platte", Health.Warning) }, TimeSpan.FromHours(30));
        await Step("9b Warnung nach 4 min → 1 Meldung", "SRV3", new[] { Bad("Platte", Health.Warning) }, TimeSpan.FromHours(30) + TimeSpan.FromMinutes(4));
        await Step("9c wird kritisch → sofort 1 Meldung (verschärft) erwartet", "SRV3", new[] { Bad("Platte") }, TimeSpan.FromHours(30) + TimeSpan.FromMinutes(5));

        cfg.AlertMaxPerHour = 2;
        var many = new[] { Bad("A"), Bad("B"), Bad("C") };
        await Step("10 Limit 2/Stunde: 3 Probleme beginnen", "SRV4", many, TimeSpan.FromHours(40));
        await Step("10b nach 4 min → 1 Limit-Meldung erwartet (nicht 3)", "SRV4", many, TimeSpan.FromHours(40) + TimeSpan.FromMinutes(4));

        var res = await engine.SendTestAsync();
        sb.AppendLine("11 Testnachricht: " + string.Join("; ", res));

        sb.AppendLine("\n--- Angekommene Anfragen ---");
        lock (captured) foreach (var c in captured.Take(12)) sb.AppendLine(c);
        sb.AppendLine($"… gesamt {captured.Count} Anfragen");
        sb.AppendLine("\n--- Verlauf (neueste zuerst) ---");
        foreach (var h in engine.History.Take(8)) sb.AppendLine($"{h.Event,-18} {h.Level,-9} {h.Text} => {h.Result}");
        http.Stop();
        return sb.ToString();
    }

    public static async Task<string> ServeTest()
    {
        var sb = new StringBuilder();
        int port = FreePort();
        var cert = StatusCert.Create();
        string pin = cert.GetCertHashString();
        var snap = new Snapshot { IsAdmin = true };
        snap.Checks.Add(new("System", "Laufwerk C:", Health.Warning, "12 GB frei von 500 GB"));
        snap.Checks.Add(new("Dienste", "Autostart-Dienste", Health.Ok, "alle laufen"));
        using var server = new StatusServer(IPAddress.Loopback, port, "T0K-3n", cert, () => StatusPayload.From(snap, new MetricsSnapshot { Cpu = 12, MemUsedPct = 40, MemUsedGb = 8, MemTotalGb = 16 }));
        server.Start();

        async Task<string> Get(string method, string path, string? bearer, string? pinOverride = null)
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c != null && c.GetCertHashString().Equals(pinOverride ?? pin, StringComparison.OrdinalIgnoreCase) };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(new HttpMethod(method), $"https://127.0.0.1:{port}{path}");
            if (bearer != null) req.Headers.Authorization = new("Bearer", bearer);
            try
            {
                using var r = await http.SendAsync(req);
                string body = await r.Content.ReadAsStringAsync();
                return $"{(int)r.StatusCode} ({body.Length} Zeichen){(r.StatusCode == HttpStatusCode.OK && path.StartsWith("/api/status") ? " " + body[..Math.Min(160, body.Length)] : "")}";
            }
            catch (Exception ex) { return "Fehler: " + ex.GetBaseException().Message; }
        }

        sb.AppendLine("GET /api/status mit Token (Bearer):   " + await Get("GET", "/api/status", "T0K-3n"));
        sb.AppendLine("GET /api/status?token=… (Abfrage):    " + await Get("GET", "/api/status?token=T0K-3n", null));
        sb.AppendLine("GET /api/status ohne Token:             " + await Get("GET", "/api/status", null));
        sb.AppendLine("GET /api/status falsches Token:         " + await Get("GET", "/api/status", "falsch"));
        sb.AppendLine("POST /api/status mit Token:             " + await Get("POST", "/api/status", "T0K-3n"));
        sb.AppendLine("DELETE /api/status mit Token:           " + await Get("DELETE", "/api/status", "T0K-3n"));
        sb.AppendLine("GET / (Webseite) mit Token:             " + await Get("GET", "/?token=T0K-3n", null));
        sb.AppendLine("GET /unbekannt mit Token:               " + await Get("GET", "/unbekannt", "T0K-3n"));
        sb.AppendLine("falscher Fingerabdruck wird abgelehnt:  " + await Get("GET", "/api/status", "T0K-3n", "0000000000000000000000000000000000000000"));

        // Abfrage wie von einer anderen Installation
        var cfg = new AppSettings { RemoteServers = new() { $"Testserver|https://127.0.0.1:{port}|T0K-3n|{pin}", $"Falscher-Pin|https://127.0.0.1:{port}|T0K-3n|AAAA", $"Tot|https://127.0.0.1:{FreePort()}|x|{pin}" } };
        using var mon = new RemoteMonitor(cfg, new AlertEngine(cfg));
        await mon.PollAll();
        foreach (var s in mon.Last)
            sb.AppendLine($"RemoteMonitor {s.Name,-13} ok={s.Ok} status={s.Status} score={s.ScoreText} fehler='{s.Error}' probleme='{s.Problems}'");
        return sb.ToString();
    }
}
