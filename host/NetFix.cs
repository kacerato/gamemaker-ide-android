using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Android.Util;

namespace GmIde;

// Rede/HTTPS da IDE no Android. Chamar NetFix.Apply() no Launcher.BootIde ANTES do IDE.Bootstrap.
//
// O que a IDE usa (analise do IL do LTS2026):
//  - Utils: HttpUtil.HappyEyeballsHttp.CreateHttpClient -> HttpClient(new SocketsHttpHandler{ConnectCallback})
//    (gmpm, feeds, marketplace). Nao usa AndroidMessageHandler: o TLS e o SslStream gerenciado,
//    que no Android roda sobre SSLEngine/Conscrypt (libSystem.Security.Cryptography.Native.Android.so).
//  - StartScreen: SSDownloadCache usa WebClient -> HttpWebRequest -> SocketsHttpHandler (mesmo SslStream).
//    HttpWebRequest copia ServicePointManager.SecurityProtocol para SslOptions.EnabledSslProtocols.
//  - IDE.cs: WebRequest.DefaultWebProxy = new GMSProxy.NetworkProxy() (proxy_settings.json; sem
//    proxy configurado fica "Proxy set: NONE" e IsBypassed=true). MQTT (MQTTnet) forca Tls12.
//  - Ninguem instala ServerCertificateValidationCallback; o lancador original (GameMaker-LTS2026.exe)
//    faz ServicePointManager.SecurityProtocol |= Tls11|Tls12, que este port NAO executa.
//
// Logs (run7..run14): HTTPS para hosts Cloudflare (gmpm-config.gamemaker.io, gms.yoyogames.com) funciona.
// A unica falha (run11) foi em 2 imagens da Start Page na CloudFront (dvovpqt3h0n7u.cloudfront.net):
// RST do servidor/rede durante o handshake TLS (ReceiveHandshakeFrame), com 5 downloads simultaneos.
// O "SSLPeerUnverifiedException: No peer certificates" do System.err aparece 1x por processo mesmo
// nos runs sem falha: e ruido do PAL Android do SslStream, nao a causa.
//
// O que este arquivo faz (sem desligar validacao de certificado):
//  1. Tira SSL3/TLS1.0/TLS1.1 de ServicePointManager.SecurityProtocol se alguem os ligar (o Conscrypt
//     recente rejeita esses nomes em setEnabledProtocols; servidores atuais nao aceitam).
//  2. Se um boot anterior detectou que IPv6 quebra o TLS e IPv4 funciona (arquivo net-disable-ipv6 em
//     FilesDir), liga System.Net.DisableIPv6 antes de qualquer socket (vale para WebClient tambem).
//     Para forcar manualmente: criar o arquivo; para desfazer: apagar (ou criar net-keep-ipv6).
//  3. Autoteste assincrono (tag GmIde.Net), 20 s depois do boot, com a configuracao final da IDE.
static class NetFix
{
    const string Tag = "GmIde.Net";
    const string FlagDisableV6 = "net-disable-ipv6";
    const string FlagKeepV6 = "net-keep-ipv6";

    static readonly string[] CloudFrontUrls =
    {
        "https://dvovpqt3h0n7u.cloudfront.net/1xome2fyebt3cawgn0p3ximdqujb",
        "https://dvovpqt3h0n7u.cloudfront.net/2mcf7rz9rw6js5xbrkbmjkiju6sj",
        "https://dvovpqt3h0n7u.cloudfront.net/7f336m10t4ba8aghmoo687c32972",
        "https://dvovpqt3h0n7u.cloudfront.net/nd7aerv80fm6mqmydljpzwzfy361",
        "https://dvovpqt3h0n7u.cloudfront.net/yfy9hvahwc3bcektsrv2eurgexj9",
    };
    const string CloudFrontHost = "dvovpqt3h0n7u.cloudfront.net";

    static bool _applied;
    static string? _filesDir;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;
        try
        {
            _filesDir = global::Android.App.Application.Context.FilesDir?.AbsolutePath;

            // (2) precisa vir antes de qualquer Socket: o switch e lido uma vez por processo.
            if (_filesDir != null && File.Exists(Path.Combine(_filesDir, FlagDisableV6))
                && !File.Exists(Path.Combine(_filesDir, FlagKeepV6)))
            {
                AppContext.SetSwitch("System.Net.DisableIPv6", true);
                Log.Warn(Tag, $"IPv6 desligado para sockets .NET (arquivo {FlagDisableV6} de um autoteste anterior)");
            }

            NormalizeProtocols("Apply");
            Log.Info(Tag, "Apply: " + Describe());
        }
        catch (Exception e) { Log.Warn(Tag, "Apply: " + e); }

        var t = new Thread(() =>
        {
            try
            {
                Thread.Sleep(20000);
                NormalizeProtocols("autoteste");
                SelfTest().GetAwaiter().GetResult();
            }
            catch (Exception e) { Log.Warn(Tag, "Autoteste abortado: " + e); }
        }) { IsBackground = true, Name = "gmide-nettest" };
        t.Start();
    }

    // Nao forca Tls12|Tls13: SystemDefault (0) deixa o Conscrypt escolher (TLS 1.2/1.3), que e o melhor.
    static void NormalizeProtocols(string when)
    {
#pragma warning disable CS0618, SYSLIB0014
        var cur = ServicePointManager.SecurityProtocol;
        const SecurityProtocolType legacy = SecurityProtocolType.Ssl3 | SecurityProtocolType.Tls | SecurityProtocolType.Tls11;
        if ((cur & legacy) == 0) return;
        var fixedP = cur & ~legacy;
        if (fixedP != SecurityProtocolType.SystemDefault)
            fixedP |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        ServicePointManager.SecurityProtocol = fixedP;
        Log.Warn(Tag, $"{when}: SecurityProtocol {cur} -> {fixedP}");
#pragma warning restore CS0618, SYSLIB0014
    }

    static string Describe()
    {
#pragma warning disable SYSLIB0014
        string proxy;
        try
        {
            var p = WebRequest.DefaultWebProxy;
            var dest = new Uri("https://" + CloudFrontHost + "/");
            proxy = p == null ? "null" : $"{p.GetType().FullName} -> {(p.IsBypassed(dest) ? "direto" : p.GetProxy(dest)?.ToString())}";
        }
        catch (Exception e) { proxy = "erro " + e.Message; }
        bool v6Off = AppContext.TryGetSwitch("System.Net.DisableIPv6", out var b) && b;
        return $"SecurityProtocol={ServicePointManager.SecurityProtocol}, DefaultWebProxy={proxy}, " +
               $"HttpClient.DefaultProxy={HttpClient.DefaultProxy?.GetType().Name}, " +
               $"OSSupportsIPv6={Socket.OSSupportsIPv6}, DisableIPv6={v6Off}, " +
               $"http_proxy={Environment.GetEnvironmentVariable("http_proxy") ?? Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? "-"}";
#pragma warning restore SYSLIB0014
    }

    // ---------------- autoteste ----------------

    static readonly List<string> _summary = new();

    static async Task SelfTest()
    {
        Log.Info(Tag, "Autoteste inicio: " + Describe());
        _summary.Clear();

        // 1) HttpClient padrao do .NET Android (AndroidMessageHandler, pilha Java).
        await Run("HttpClient(padrao) GET gamemaker.io", async () =>
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var r = await c.GetAsync("https://gamemaker.io/");
            return $"{(int)r.StatusCode} {r.StatusCode} handler={HandlerName(c)}";
        });

        // 2) O caminho que a IDE usa no HttpUtil: SocketsHttpHandler + SslStream gerenciado.
        await Run("HttpClient(SocketsHttpHandler) GET gamemaker.io", async () =>
        {
            using var c = new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(20) };
            using var r = await c.GetAsync("https://gamemaker.io/");
            return $"{(int)r.StatusCode} {r.StatusCode}";
        });
        await Run("HttpClient(SocketsHttpHandler) GET cloudfront", async () =>
        {
            using var c = new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(20) };
            var bytes = await c.GetByteArrayAsync(CloudFrontUrls[0]);
            return $"{bytes.Length} bytes";
        });

#pragma warning disable SYSLIB0014
        // 3) WebClient.DownloadData, como o SSDownloadCache da Start Page.
        await Run("WebClient.DownloadData cloudfront", () => Task.Run(() =>
        {
            using var wc = new WebClient();
            var bytes = wc.DownloadData(CloudFrontUrls[1]);
            return $"{bytes.Length} bytes";
        }));

        // 4) HttpWebRequest direto.
        await Run("HttpWebRequest GET cloudfront", () => Task.Run(() =>
        {
            var req = (HttpWebRequest)WebRequest.Create(CloudFrontUrls[2]);
            req.Timeout = 20000;
            using var resp = (HttpWebResponse)req.GetResponse();
            using var ms = new MemoryStream();
            resp.GetResponseStream().CopyTo(ms);
            return $"{(int)resp.StatusCode} {ms.Length} bytes";
        }));
        await Run("HttpWebRequest GET gms.yoyogames.com rss", () => Task.Run(() =>
        {
            var req = (HttpWebRequest)WebRequest.Create("https://gms.yoyogames.com/update-manual-lts-2026.rss");
            req.Timeout = 20000;
            using var resp = (HttpWebResponse)req.GetResponse();
            return $"{(int)resp.StatusCode}";
        }));

        // 5) Cenario do run11: 5 WebClient async ao mesmo tempo na CloudFront.
        await Run("WebClient x5 paralelo cloudfront", async () =>
        {
            var tasks = CloudFrontUrls.Select(async u =>
            {
                try
                {
                    using var wc = new WebClient();
                    var b = await wc.DownloadDataTaskAsync(new Uri(u));
                    return "ok";
                }
                catch (Exception e) { return "FALHA(" + Short(e) + ")"; }
            }).ToArray();
            var res = await Task.WhenAll(tasks);
            int ok = res.Count(r => r == "ok");
            if (ok != res.Length) throw new Exception($"{ok}/{res.Length} ok: " + string.Join("; ", res.Where(r => r != "ok")));
            return $"{ok}/{res.Length} ok";
        });
#pragma warning restore SYSLIB0014

        // 6) SslStream cru por familia de endereco e por versao de TLS (separa IPv6 x IPv4, TLS1.3 x 1.2).
        var v4 = await RunTls(CloudFrontHost, AddressFamily.InterNetwork, SslProtocols.None);
        var v6 = await RunTls(CloudFrontHost, AddressFamily.InterNetworkV6, SslProtocols.None);
        await RunTls(CloudFrontHost, AddressFamily.InterNetwork, SslProtocols.Tls12);
        await RunTls("gamemaker.io", AddressFamily.InterNetworkV6, SslProtocols.None);

        // Decisao adaptativa para o proximo boot (ver Apply).
        try
        {
            if (_filesDir != null)
            {
                var flag = Path.Combine(_filesDir, FlagDisableV6);
                bool v6Off = AppContext.TryGetSwitch("System.Net.DisableIPv6", out var b) && b;
                if (!v6Off && v4 == true && v6 == false && !File.Exists(flag))
                {
                    File.WriteAllText(flag, DateTime.Now.ToString("s") + " IPv6 TLS falhou, IPv4 ok\n");
                    Log.Warn(Tag, $"IPv6 falhou e IPv4 funcionou: proximo boot desliga IPv6 para .NET ({flag})");
                }
                else if (!v6Off && v6 == true && File.Exists(flag))
                {
                    File.Delete(flag);
                    Log.Info(Tag, "IPv6 voltou a funcionar: removido " + flag);
                }
            }
        }
        catch (Exception e) { Log.Warn(Tag, "flag IPv6: " + e.Message); }

        Log.Info(Tag, "RESUMO: " + string.Join(" | ", _summary));
    }

    static async Task Run(string name, Func<Task<string>> body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await body().WaitAsync(TimeSpan.FromSeconds(45));
            Log.Info(Tag, $"OK    {name}: {r} ({sw.ElapsedMilliseconds} ms)");
            _summary.Add(name + "=OK");
        }
        catch (Exception e)
        {
            Log.Warn(Tag, $"FALHA {name} ({sw.ElapsedMilliseconds} ms): {Chain(e)}");
            _summary.Add(name + "=FALHA");
        }
    }

    // null = sem endereco dessa familia; true/false = handshake ok/falhou.
    static async Task<bool?> RunTls(string host, AddressFamily fam, SslProtocols prot)
    {
        string fname = fam == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
        string name = $"SslStream {host} {fname} {(prot == SslProtocols.None ? "padrao" : prot.ToString())}";
        IPAddress? ip = null;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, fam);
            ip = addrs.FirstOrDefault();
        }
        catch (Exception e) { Log.Info(Tag, $"{name}: DNS {fam} sem resposta ({Short(e)})"); }
        if (ip == null) { Log.Info(Tag, $"{name}: sem endereco {fname}"); _summary.Add(name + "=sem-endereco"); return null; }

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var sock = new Socket(fam, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await sock.ConnectAsync(new IPEndPoint(ip, 443), cts.Token);
            long tcp = sw.ElapsedMilliseconds;
            using var ns = new NetworkStream(sock, ownsSocket: false);
            using var ssl = new SslStream(ns); // validacao padrao de certificado (nenhum callback)
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host, // SNI
                EnabledSslProtocols = prot,
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
            }, cts.Token);
            var cert = ssl.RemoteCertificate;
            var hdr = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
            await ssl.WriteAsync(hdr, cts.Token);
            var buf = new byte[64];
            int n = await ssl.ReadAsync(buf, cts.Token);
            var first = Encoding.ASCII.GetString(buf, 0, n).Split('\r')[0];
            Log.Info(Tag, $"OK    {name} [{ip}]: tcp {tcp} ms, tls {ssl.SslProtocol} {ssl.NegotiatedCipherSuite}, " +
                          $"cert '{cert?.Subject}', resposta '{first}' ({sw.ElapsedMilliseconds} ms)");
            _summary.Add(name + "=OK");
            return true;
        }
        catch (Exception e)
        {
            Log.Warn(Tag, $"FALHA {name} [{ip}] ({sw.ElapsedMilliseconds} ms): {Chain(e)}");
            _summary.Add(name + "=FALHA");
            return false;
        }
    }

    static string HandlerName(HttpMessageInvoker c)
    {
        try
        {
            var f = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var h = f?.GetValue(c);
            // HttpClientHandler no Android embrulha o handler nativo em _nativeHandler/_underlyingHandler
            var inner = h?.GetType().GetField("_nativeUnderlyingHandler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(h)
                     ?? h?.GetType().GetField("_underlyingHandler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(h);
            return h?.GetType().Name + (inner != null ? "/" + inner.GetType().Name : "");
        }
        catch { return "?"; }
    }

    static string Short(Exception e)
    {
        while (e.InnerException != null && (e is AggregateException || e is System.Reflection.TargetInvocationException)) e = e.InnerException;
        var root = e; while (root.InnerException != null) root = root.InnerException;
        return root == e ? $"{e.GetType().Name}: {e.Message}" : $"{e.GetType().Name} > {root.GetType().Name}: {root.Message}";
    }

    static string Chain(Exception e)
    {
        var sb = new StringBuilder();
        for (var x = e; x != null; x = x.InnerException)
        {
            if (sb.Length > 0) sb.Append(" ---> ");
            sb.Append(x.GetType().Name).Append(": ").Append(x.Message);
            if (x is SocketException se) sb.Append($" (SocketError={se.SocketErrorCode})");
            if (x is WebException we) sb.Append($" (Status={we.Status})");
            if (x is HttpRequestException he && he.HttpRequestError != HttpRequestError.Unknown) sb.Append($" (HttpRequestError={he.HttpRequestError})");
        }
        return sb.ToString();
    }
}
