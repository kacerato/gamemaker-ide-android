using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Util;

namespace GmIde;

// Telemetria opcional (docs/telemetria.md): uso, builds e crashes da IDE e dos processos
// :build e :game. Cada processo grava itens JSONL em files/telemetry/q-<processo>-<pid>.jsonl
// (abre, acrescenta e fecha a cada item: o envio pode renomear o arquivo a qualquer momento).
// So o processo da IDE envia (TelemetryUploader), e so com o consentimento "1".
//
// Consentimento (arquivo telemetry/consent, gravado pelo dialogo Java TelemetryConsent):
//   ausente -> grava, mas nao envia (o primeiro boot conta se o usuario aceitar);
//   "0"     -> nao grava e apaga a fila;  "1" -> grava e envia.
public static class Telemetry
{
    const string Tag = "GmIde.Telemetry";
    public const string Schema = "gmide.telemetry/1";

    static string _dir = "";
    static string _process = "ide";
    static string? _sessionId;
    static readonly object _writeLock = new();
    static Exception? _lastCrash;
    static int _consent = -2;            // -2 nao lido; -1 sem resposta; 0 nao; 1 sim
    static long _consentReadAt;

    public static string Dir => _dir;
    public static string ProcessKind => _process;
    public static string? SessionId => _sessionId;
    /// <summary>Versao do runtime (runtime-2026.0.0.23 -> 2026.0.0.23); definida pelo BuildIntegration/BuildService.</summary>
    public static string? RuntimeVersion { get; private set; }

    public static void SetRuntimeDir(string? runtimeDir)
    {
        if (string.IsNullOrEmpty(runtimeDir)) return;
        var name = Path.GetFileName(runtimeDir.TrimEnd('/'));
        RuntimeVersion = name.StartsWith("runtime-", StringComparison.Ordinal) ? name[8..] : name;
    }

    /// <summary>MainApplication.OnCreate, em todos os processos.</summary>
    public static void Init(Context ctx, string processName)
    {
        try
        {
            _process = processName.EndsWith(":build", StringComparison.Ordinal) ? "build"
                : processName.EndsWith(":game", StringComparison.Ordinal) ? "game"
                : processName.Contains(':') ? "other" : "ide";
            if (_process == "other") return;
            _dir = Path.Combine(ctx.FilesDir!.AbsolutePath, "telemetry");
            Directory.CreateDirectory(_dir);

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) Crash(ex, e.IsTerminating);
            };
            AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Crash(e.Exception, fatal: true);
            TaskScheduler.UnobservedTaskException += (_, e) =>
                Event("unobserved_task", new() { ["type"] = e.Exception.InnerException?.GetType().FullName ?? e.Exception.GetType().FullName });

            if (_process == "ide")
            {
                if (Consent == 0) { Purge(); return; }
                ExitReasons.Collect(ctx);       // fecha a sessao anterior e registra quedas de qualquer processo
                StartSession();
                TelemetryUploader.Start(ctx);
            }
            else
            {
                _sessionId = ReadSessionFile()?.Id; // :build/:game se ligam a sessao da IDE
            }
        }
        catch (Exception e) { Log.Warn(Tag, "Init: " + e); }
    }

    // ------------------------------------------------------------------ consentimento

    public static int Consent
    {
        get
        {
            long now = System.Environment.TickCount64;
            if (_consent != -2 && now - _consentReadAt < 10000) return _consent;
            _consentReadAt = now;
            try
            {
                var f = Path.Combine(_dir, "consent");
                _consent = !File.Exists(f) ? -1 : File.ReadAllText(f).Trim() == "1" ? 1 : 0;
            }
            catch { _consent = -1; }
            return _consent;
        }
    }

    static bool Recording => _dir.Length > 0 && Consent != 0;

    /// <summary>Recusa: apaga a fila e os ids (a resposta fica no arquivo consent).</summary>
    public static void Purge()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir))
                if (Path.GetFileName(f) != "consent") File.Delete(f);
        }
        catch (Exception e) { Log.Warn(Tag, "Purge: " + e.Message); }
    }

    public static string InstallId
    {
        get
        {
            var f = Path.Combine(_dir, "install_id");
            try { if (File.Exists(f)) { var s = File.ReadAllText(f).Trim(); if (Guid.TryParse(s, out _)) return s; } } catch { }
            var id = Guid.NewGuid().ToString();
            File.WriteAllText(f, id);
            // Saidas de processo anteriores a instalacao da telemetria nao sao enviadas.
            File.WriteAllText(Path.Combine(_dir, "exit-last"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            return id;
        }
    }

    // ------------------------------------------------------------------ sessao

    public sealed record SessionFile(string Id, int Pid, long StartMs, bool Ended);

    internal static SessionFile? ReadSessionFile()
    {
        try
        {
            var p = File.ReadAllText(Path.Combine(_dir, "session")).Split(' ');
            return new SessionFile(p[0], int.Parse(p[1]), long.Parse(p[2]), p.Length > 3 && p[3] == "ended");
        }
        catch { return null; }
    }

    static void WriteSessionFile(SessionFile s) =>
        File.WriteAllText(Path.Combine(_dir, "session"), $"{s.Id} {s.Pid} {s.StartMs}{(s.Ended ? " ended" : "")}");

    static void StartSession()
    {
        _ = InstallId;
        _sessionId = Guid.NewGuid().ToString();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteSessionFile(new SessionFile(_sessionId, global::Android.OS.Process.MyPid(), now, false));
        Append(new JsonObject { ["type"] = "session_start", ["session_id"] = _sessionId, ["ts"] = now });
        new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(TimeSpan.FromMinutes(5));
                if (!Recording) continue;
                Append(new JsonObject { ["type"] = "session_heartbeat", ["session_id"] = _sessionId, ["ts"] = Now });
            }
        }) { IsBackground = true, Name = "gmide-telemetry-hb" }.Start();
    }

    /// <summary>Tempo da abertura do processo ate o loop principal da IDE.</summary>
    public static void Booted()
    {
        if (_sessionId == null) return;
        long ms = SystemClock.ElapsedRealtime() - global::Android.OS.Process.StartElapsedRealtime;
        var s = ReadSessionFile();
        Append(new JsonObject { ["type"] = "session_start", ["session_id"] = _sessionId, ["ts"] = s?.StartMs ?? Now, ["boot_ms"] = ms });
    }

    /// <summary>Fim conhecido da sessao (IDE.Execute voltou, reinicio pedido pela IDE).</summary>
    public static void EndSession(string reason)
    {
        if (_sessionId == null || !Recording) return;
        Append(new JsonObject { ["type"] = "session_end", ["session_id"] = _sessionId, ["ts"] = Now, ["reason"] = reason });
        var s = ReadSessionFile();
        if (s != null && s.Id == _sessionId) WriteSessionFile(s with { Ended = true });
    }

    // ------------------------------------------------------------------ itens

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static void Event(string name, JsonObject? props = null)
    {
        if (!Recording) return;
        Append(new JsonObject { ["type"] = "event", ["session_id"] = _sessionId, ["ts"] = Now, ["name"] = name, ["props"] = props ?? new JsonObject() });
    }

    /// <summary>target: run_gx | run_android | apk | web | windows | other.
    /// result: ok | failed | gml_error | tool_crash | killed | cancelled.</summary>
    public static void Build(string target, string result, long durationMs, int? exitCode = null, string? error = null)
    {
        if (!Recording) return;
        Append(new JsonObject
        {
            ["type"] = "build", ["session_id"] = _sessionId, ["ts"] = Now, ["target"] = target, ["result"] = result,
            ["duration_ms"] = durationMs, ["exit_code"] = exitCode, ["runtime"] = RuntimeVersion,
            ["error"] = error == null ? null : Scrub(Clip(error, 4000)),
        });
    }

    /// <summary>Excecao nao tratada (managed ou Java) deste processo.</summary>
    public static void Crash(Exception ex, bool fatal)
    {
        if (!Recording || ReferenceEquals(ex, _lastCrash)) return;
        _lastCrash = ex;
        try
        {
            var root = ex is Java.Lang.Throwable ? ex : ex.GetBaseException();
            CrashRecord(_process, ex is Java.Lang.Throwable ? "java" : "managed", root.GetType().FullName,
                root.Message, ex.ToString(), LogTail(48 * 1024),
                new JsonObject { ["fatal"] = fatal, ["pid"] = global::Android.OS.Process.MyPid(), ["thread"] = Thread.CurrentThread.Name });
            if (fatal) RememberCrashedPid(global::Android.OS.Process.MyPid());
        }
        catch (Exception e) { Log.Warn(Tag, "Crash: " + e.Message); }
    }

    /// <summary>process: ide | build | game | runner. kind: managed | java | native | anr | oom | runner_js | killed.</summary>
    public static void CrashRecord(string process, string kind, string? type, string? message, string? stack,
        string? logTail, JsonObject? meta = null, long? ts = null, string? sessionId = null)
    {
        if (!Recording) return;
        Append(new JsonObject
        {
            ["type"] = "crash", ["session_id"] = sessionId ?? _sessionId, ["ts"] = ts ?? Now,
            ["process"] = process, ["kind"] = kind, ["exception_type"] = type,
            ["message"] = message == null ? null : Scrub(Clip(message, 2000)),
            ["stack"] = stack == null ? null : Scrub(Clip(stack, 60 * 1024)),
            ["log_tail"] = logTail == null ? null : Scrub(ClipTail(logTail, 60 * 1024)),
            ["meta"] = meta ?? new JsonObject(),
        });
    }

    internal static void Append(JsonObject item)
    {
        if (_dir.Length == 0) return;
        try
        {
            var line = item.ToJsonString() + "\n";
            int pid = global::Android.OS.Process.MyPid();
            var file = Path.Combine(_dir, $"q-{_process}-{pid}.jsonl");
            lock (_writeLock)
                File.AppendAllText(file, line, Encoding.UTF8);
        }
        catch (Exception e) { Log.Warn(Tag, "Append: " + e.Message); }
    }

    static void RememberCrashedPid(int pid)
    {
        try { File.AppendAllText(Path.Combine(_dir, "crash-pids"), pid + "\n"); } catch { }
    }

    internal static HashSet<int> CrashedPids()
    {
        var set = new HashSet<int>();
        try
        {
            var f = Path.Combine(_dir, "crash-pids");
            if (!File.Exists(f)) return set;
            var lines = File.ReadAllLines(f);
            foreach (var l in lines) if (int.TryParse(l, out var p)) set.Add(p);
            if (lines.Length > 64) File.WriteAllLines(f, lines[^32..]);
        }
        catch { }
        return set;
    }

    // ------------------------------------------------------------------ texto

    /// <summary>Ultimas linhas do logcat deste processo (um app le o proprio log).</summary>
    public static string? LogTail(int maxBytes)
    {
        try
        {
            var psi = new ProcessStartInfo("logcat") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-d", "-v", "time", "-t", "400", "--pid=" + global::Android.OS.Process.MyPid() }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!read.Wait(1500)) { try { p.Kill(); } catch { } return null; }
            return ClipTail(read.Result, maxBytes);
        }
        catch { return null; }
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    internal static string ClipTail(string s, int max) => s.Length <= max ? s : s[^max..];

    static readonly Regex SdcardPath = new(@"/(storage/emulated/\d+|sdcard)/[^\s:'""()]*?([^/\s:'""()]+)(?=[\s:'""()]|$)", RegexOptions.Compiled);
    static readonly Regex AppPath = new(@"/data/(user/\d+|data)/io\.gmide\.android", RegexOptions.Compiled);
    static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.Compiled);

    /// <summary>Tira caminhos do armazenamento do usuario (fica so o nome do arquivo) e e-mails.</summary>
    public static string Scrub(string s)
    {
        s = SdcardPath.Replace(s, "<sdcard>/…/$2");
        s = AppPath.Replace(s, "<app>");
        return Email.Replace(s, "<email>");
    }
}
