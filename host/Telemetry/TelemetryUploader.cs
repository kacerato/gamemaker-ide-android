using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;

namespace GmIde;

// Envio da fila (so no processo da IDE, so com consentimento "1"). Ciclo:
//  1. cada q-*.jsonl e renomeado para s-<guid>.jsonl (quem grava abre/fecha por item, entao
//     o proximo item cai num q- novo);
//  2. os s- viram lotes b-<batch_id>.json ja montados (ate 400 itens/~384 KB cada);
//  3. cada b- e enviado com gzip; 2xx ou 4xx definitivo apaga, 429/5xx/rede fica para o
//     proximo ciclo com o MESMO batch_id (o servidor ignora o repetido).
// Teto local de 2 MB: os arquivos mais antigos saem primeiro.
static class TelemetryUploader
{
    const string Tag = "GmIde.Telemetry";
    public const string Endpoint = "https://gmide-telemetry.vercel.app";
    const int MaxItems = 400, MaxBatchBytes = 384 * 1024;
    const long MaxQueueBytes = 2 * 1024 * 1024;

    static readonly object _flushLock = new();
    static JsonObject? _app, _device;
    static int _intervalS = 600;
    static bool _serverEnabled = true;
    static int _failures;

    public static void Start(Context ctx)
    {
        _app = AppInfo(ctx);
        _device = DeviceInfo(ctx);
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(25));
            while (true)
            {
                Flush();
                // Falhas seguidas espacam o ciclo (ate 1 h).
                int wait = Math.Min(3600, _intervalS * (1 << Math.Min(_failures, 3)));
                Thread.Sleep(TimeSpan.FromSeconds(wait));
            }
        }) { IsBackground = true, Name = "gmide-telemetry-up" }.Start();
    }

    /// <summary>Envia o que estiver na fila. Seguro chamar de qualquer thread.</summary>
    public static void Flush()
    {
        if (!Monitor.TryEnter(_flushLock)) return;
        try
        {
            int consent = Telemetry.Consent;
            if (consent == 0) { Telemetry.Purge(); return; }
            Trim();
            if (consent != 1) return;   // sem resposta ainda: guarda, nao envia
            using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
            { Timeout = TimeSpan.FromSeconds(30) };
            FetchConfig(http);
            if (!_serverEnabled) return;
            Compose();
            foreach (var b in Directory.GetFiles(Telemetry.Dir, "b-*.json").OrderBy(File.GetLastWriteTimeUtc))
                if (!Send(http, b)) { _failures++; return; }
            _failures = 0;
        }
        catch (Exception e) { _failures++; Log.Warn(Tag, "Flush: " + e.Message); }
        finally { Monitor.Exit(_flushLock); }
    }

    static void FetchConfig(HttpClient http)
    {
        try
        {
            var j = JsonNode.Parse(http.GetStringAsync(Endpoint + "/api/v1/config").GetAwaiter().GetResult());
            _serverEnabled = j?["enabled"]?.GetValue<bool>() ?? true;
            _intervalS = Math.Clamp(j?["flush_interval_s"]?.GetValue<int>() ?? 600, 60, 86400);
        }
        catch (Exception e) { Log.Warn(Tag, "config: " + e.Message); }
    }

    static void Compose()
    {
        foreach (var q in Directory.GetFiles(Telemetry.Dir, "q-*.jsonl"))
        {
            try { File.Move(q, Path.Combine(Telemetry.Dir, $"s-{Guid.NewGuid():N}.jsonl")); } catch { }
        }
        var sources = Directory.GetFiles(Telemetry.Dir, "s-*.jsonl").OrderBy(File.GetLastWriteTimeUtc).ToList();
        if (sources.Count == 0) return;
        Thread.Sleep(200); // um Append que abriu o arquivo antes do Move termina de escrever

        var items = new List<string>();
        int bytes = 0;
        foreach (var s in sources)
        {
            foreach (var line in File.ReadLines(s))
            {
                if (line.Length == 0 || line.Length > MaxBatchBytes) continue;
                try { using var _ = JsonDocument.Parse(line); } catch { continue; } // linha cortada
                if (items.Count >= MaxItems || bytes + line.Length > MaxBatchBytes) { WriteBatch(items); items.Clear(); bytes = 0; }
                items.Add(line);
                bytes += line.Length + 1;
            }
        }
        if (items.Count > 0) WriteBatch(items);
        foreach (var s in sources) { try { File.Delete(s); } catch { } }
    }

    static void WriteBatch(List<string> items)
    {
        var id = Guid.NewGuid().ToString();
        var sb = new StringBuilder();
        sb.Append("{\"schema\":\"").Append(Telemetry.Schema).Append("\",\"batch_id\":\"").Append(id)
          .Append("\",\"install_id\":\"").Append(Telemetry.InstallId).Append("\",\"app\":")
          .Append((_app ?? new JsonObject()).ToJsonString()).Append(",\"device\":")
          .Append((_device ?? new JsonObject()).ToJsonString()).Append(",\"items\":[")
          .Append(string.Join(",", items)).Append("]}");
        var tmp = Path.Combine(Telemetry.Dir, $"b-{id}.tmp");
        File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
        File.Move(tmp, Path.ChangeExtension(tmp, ".json"));
    }

    static bool Send(HttpClient http, string file)
    {
        var raw = File.ReadAllBytes(file);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(raw);
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        try
        {
            using var resp = http.PostAsync(Endpoint + "/api/v1/ingest", content).GetAwaiter().GetResult();
            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode || (code >= 400 && code < 500 && code != 429 && code != 408))
            {
                if (!resp.IsSuccessStatusCode)
                    Log.Warn(Tag, $"Lote recusado ({code}): {resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
                File.Delete(file);
                return true;
            }
            Log.Warn(Tag, $"Envio adiado ({code})");
            return false;
        }
        catch (Exception e) { Log.Warn(Tag, "Envio: " + e.Message); return false; }
    }

    static void Trim()
    {
        var files = new DirectoryInfo(Telemetry.Dir).GetFiles()
            .Where(f => f.Name.StartsWith("q-") || f.Name.StartsWith("s-") || f.Name.StartsWith("b-"))
            .OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= MaxQueueBytes) break;
            if (f.Name.StartsWith("q-") && f.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-1)) continue; // em uso
            total -= f.Length;
            try { f.Delete(); } catch { }
        }
    }

    static JsonObject AppInfo(Context ctx)
    {
        var j = new JsonObject { ["runtime"] = Telemetry.RuntimeVersion };
        try
        {
            var pi = ctx.PackageManager!.GetPackageInfo(ctx.PackageName!, 0)!;
            j["version"] = pi.VersionName;
            #pragma warning disable CA1422 // VersionCode so abaixo da API 28
            j["code"] = OperatingSystem.IsAndroidVersionAtLeast(28) ? pi.LongVersionCode : pi.VersionCode;
#pragma warning restore CA1422
        }
        catch { }
        return j;
    }

    static JsonObject DeviceInfo(Context ctx)
    {
        var j = new JsonObject
        {
            ["model"] = Build.Model, ["manufacturer"] = Build.Manufacturer, ["api"] = (int)Build.VERSION.SdkInt,
            ["abi"] = Build.SupportedAbis?.FirstOrDefault(), ["locale"] = Java.Util.Locale.Default.ToLanguageTag(),
        };
        try
        {
            j["gpu"] = OperatingSystem.IsAndroidVersionAtLeast(31) ? $"{Build.SocManufacturer} {Build.SocModel}" : Build.Hardware;
            var am = (ActivityManager)ctx.GetSystemService(Context.ActivityService)!;
            var mi = new ActivityManager.MemoryInfo();
            am.GetMemoryInfo(mi);
            j["ram_mb"] = mi.TotalMem / (1024 * 1024);
        }
        catch { }
        return j;
    }

    /// <summary>A versao do runtime so e conhecida depois do BuildIntegration.Configure.</summary>
    public static void RefreshApp(Context ctx) => _app = AppInfo(ctx);
}
