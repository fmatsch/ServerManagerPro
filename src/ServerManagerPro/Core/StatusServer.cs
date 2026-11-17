using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ServerManagerPro.Core;

public sealed record StatusCheck(string Category, string Name, string Status, string Detail);

/// <summary>Was der Status-Zugriff liefert: nur lesbare Zusammenfassung, keine Einstellungen, keine Zugangsdaten.</summary>
public sealed class StatusPayload
{
    public string Host { get; set; } = "";
    public string Time { get; set; } = "";
    public string Version { get; set; } = "";
    public int Score { get; set; }
    public string Overall { get; set; } = "Ok";
    public bool Admin { get; set; }
    public double Cpu { get; set; }
    public double MemUsedPct { get; set; }
    public double MemUsedGb { get; set; }
    public double MemTotalGb { get; set; }
    public List<StatusCheck> Checks { get; set; } = new();

    public static StatusPayload From(Snapshot s, MetricsSnapshot? m) => new()
    {
        Host = Environment.MachineName,
        Time = s.Time.ToString("o"),
        Version = UpdateChecker.Current.ToString(),
        Score = HealthMonitor.Score(s.Checks),
        Overall = HealthMonitor.Overall(s.Checks).ToString(),
        Admin = s.IsAdmin,
        Cpu = m?.Cpu ?? 0, MemUsedPct = m?.MemUsedPct ?? 0, MemUsedGb = m?.MemUsedGb ?? 0, MemTotalGb = m?.MemTotalGb ?? 0,
        Checks = s.Checks.Select(c => new StatusCheck(c.Category, c.Name, c.Status.ToString(), c.Detail)).ToList(),
    };
}

/// <summary>Selbstsigniertes Zertifikat für den Status-Zugriff; der Fingerabdruck wird in der Oberfläche angezeigt und von Gegenstellen angeheftet.</summary>
public static class StatusCert
{
    static string FilePath => Path.Combine(AppSettings.Directory, "status-cert.bin");

    public static X509Certificate2 GetOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var pfx = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.LocalMachine);
                var c = new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
                if (c.NotAfter > DateTime.Now.AddDays(30)) return c;
            }
        }
        catch { }
        var created = Create();
        byte[] bytes = created.Export(X509ContentType.Pfx);
        try { File.WriteAllBytes(FilePath, ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine)); } catch { }
        return created;
    }

    /// <summary>Erzeugt ein neues selbstsigniertes Zertifikat (ohne es zu speichern).</summary>
    public static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        string host = Environment.MachineName;
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var self = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        byte[] bytes = self.Export(X509ContentType.Pfx);
        return new X509Certificate2(bytes, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    public static string Thumbprint() { using var c = GetOrCreate(); return c.Thumbprint; }
}

/// <summary>
/// Kleiner, nur lesender HTTPS-Server. Antwortet ausschließlich auf GET mit gültigem Token, kennt keine Schreib- oder
/// Steuerbefehle und gibt nur die Zusammenfassung aus <see cref="StatusPayload"/> aus.
/// </summary>
public sealed class StatusServer : IDisposable
{
    readonly TcpListener _listener;
    readonly X509Certificate2 _cert;
    readonly byte[] _token;
    readonly Func<StatusPayload?> _provider;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _slots = new(16);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public StatusServer(IPAddress bind, int port, string token, X509Certificate2 cert, Func<StatusPayload?> provider)
    {
        _listener = new TcpListener(bind, port);
        _cert = cert;
        _token = Encoding.UTF8.GetBytes(token);
        _provider = provider;
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            if (!await _slots.WaitAsync(0)) { c.Dispose(); continue; }   // zu viele gleichzeitige Verbindungen: abweisen
            _ = Task.Run(async () => { try { await Handle(c); } finally { _slots.Release(); } });
        }
    }

    bool TokenOk(string? given)
    {
        if (string.IsNullOrEmpty(given) || _token.Length == 0) return false;
        var g = Encoding.UTF8.GetBytes(given);
        return g.Length == _token.Length && CryptographicOperations.FixedTimeEquals(g, _token);
    }

    async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = client.SendTimeout = 8000;
                using var ssl = new SslStream(client.GetStream(), false);
                using var cts = new CancellationTokenSource(8000);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, cts.Token);

                // Anfrage lesen (höchstens 8 KB bis zum Ende der Kopfzeilen)
                var buf = new byte[8192]; int len = 0;
                while (len < buf.Length)
                {
                    int n = await ssl.ReadAsync(buf.AsMemory(len), cts.Token);
                    if (n == 0) break;
                    len += n;
                    if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
                }
                var lines = Encoding.ASCII.GetString(buf, 0, len).Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 2) { await Respond(ssl, 400, "text/plain", "Bad request"); return; }
                if (first[0] != "GET") { await Respond(ssl, 405, "text/plain", "Nur GET erlaubt", "Allow: GET"); return; }

                string target = first[1];
                string path = target.Split('?')[0];
                string query = target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "";
                string? token = null;
                foreach (var l in lines.Skip(1))
                    if (l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) && l.Contains("Bearer ", StringComparison.OrdinalIgnoreCase))
                        token = l[(l.IndexOf("Bearer ", StringComparison.OrdinalIgnoreCase) + 7)..].Trim();
                if (token == null)
                    foreach (var kv in query.Split('&'))
                        if (kv.StartsWith("token=")) token = Uri.UnescapeDataString(kv[6..]);

                if (!TokenOk(token))
                {
                    await Task.Delay(500);   // bremst Raten
                    await Respond(ssl, 401, "text/plain", "Token fehlt oder falsch", "WWW-Authenticate: Bearer");
                    return;
                }
                switch (path)
                {
                    case "/api/ping": await Respond(ssl, 200, "text/plain; charset=utf-8", "ok"); break;
                    case "/api/status":
                    {
                        var p = _provider();
                        if (p == null) await Respond(ssl, 503, "text/plain", "Noch keine Messwerte", "Retry-After: 10");
                        else await Respond(ssl, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(p));
                        break;
                    }
                    case "/":
                    case "/index.html": await Respond(ssl, 200, "text/html; charset=utf-8", Html); break;
                    default: await Respond(ssl, 404, "text/plain", "Nicht gefunden"); break;
                }
            }
            catch { /* abgebrochene oder fehlerhafte Verbindung: ignorieren */ }
        }
    }

    static async Task Respond(Stream s, int code, string type, string body, string? extraHeader = null)
    {
        string reason = code switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", 503 => "Service Unavailable", _ => "Error" };
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                   "Referrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'\r\n" +
                   (extraHeader != null ? extraHeader + "\r\n" : "") + "Connection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(bytes);
        await s.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    const string Html = """
<!doctype html><html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>ServerManagerPro</title>
<style>
body{margin:0;background:#0e1620;color:#e6edf3;font:15px/1.5 Segoe UI,system-ui,sans-serif;padding:16px;max-width:900px;margin:auto}
h1{font-size:20px;margin:0 0 4px}.m{color:#8b9bab}.card{background:#16212d;border:1px solid #27374a;border-radius:12px;padding:12px 16px;margin:12px 0}
.row{display:grid;grid-template-columns:14px 200px 1fr;gap:10px;padding:4px 0}.dot{width:10px;height:10px;border-radius:50%;margin-top:7px}
.Ok{background:#22c55e}.Warning{background:#f59e0b}.Critical{background:#f87171}.Info{background:#60a5fa}.Unknown{background:#8b9bab}
b{font-weight:600}h2{font-size:15px;margin:0 0 6px}
</style></head><body>
<h1 id="h">ServerManagerPro</h1><div class="m" id="s">lädt …</div><div id="c"></div>
<script>
const t=new URLSearchParams(location.search).get('token')||'';
function el(tag,cls,txt){const e=document.createElement(tag);if(cls)e.className=cls;if(txt!==undefined)e.textContent=txt;return e}
async function load(){
 try{
  const r=await fetch('/api/status?token='+encodeURIComponent(t),{cache:'no-store'});
  if(!r.ok){document.getElementById('s').textContent='Fehler '+r.status;return}
  const d=await r.json();
  document.getElementById('h').textContent=d.Host+' – Gesundheit '+d.Score+'/100 ('+d.Overall+')';
  document.getElementById('s').textContent='Stand '+new Date(d.Time).toLocaleString()+' · CPU '+Math.round(d.Cpu)+' % · RAM '+Math.round(d.MemUsedPct)+' % · Version '+d.Version;
  const c=document.getElementById('c');c.textContent='';
  const groups={};d.Checks.forEach(x=>{(groups[x.Category]=groups[x.Category]||[]).push(x)});
  Object.keys(groups).forEach(g=>{const card=el('div','card');card.appendChild(el('h2','',g));
   groups[g].forEach(x=>{const row=el('div','row');row.appendChild(el('span','dot '+x.Status));row.appendChild(el('b','',x.Name));row.appendChild(el('span','m',x.Detail));card.appendChild(row)});c.appendChild(card)});
 }catch(e){document.getElementById('s').textContent='Nicht erreichbar'}
}
load();setInterval(load,10000);
</script></body></html>
""";
}
