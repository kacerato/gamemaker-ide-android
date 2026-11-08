using System.Text;
using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;

namespace GmIde;

// Na abertura da IDE: por que cada processo do app terminou desde a ultima vez
// (ActivityManager.getHistoricalProcessExitReasons, API 30+). E o unico jeito de ver crash
// nativo, ANR e morte por falta de memoria, porque o processo morre antes de enviar algo.
// Tambem fecha a sessao anterior da IDE com o motivo certo. Abaixo da API 30 a sessao
// anterior fica "unknown" e so os crashes managed/Java (Telemetry.Crash) aparecem.
static class ExitReasons
{
    const string Tag = "GmIde.Telemetry";
    // ApplicationExitInfo.REASON_*
    const int ExitSelf = 1, Signaled = 2, LowMemory = 3, CrashJava = 4, CrashNative = 5, Anr = 6,
        InitFailure = 7, ExcessiveResource = 9, UserRequested = 10, UserStopped = 11;

    public static void Collect(Context ctx)
    {
        var prev = Telemetry.ReadSessionFile();
        bool prevClosed = prev == null || prev.Ended;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30)) prevClosed |= CollectExitInfo(ctx, prev);
        }
        catch (Exception e) { Log.Warn(Tag, "ExitReasons: " + e); }
        if (!prevClosed)
            Telemetry.Append(new JsonObject { ["type"] = "session_end", ["session_id"] = prev!.Id,
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["reason"] = "unknown" });
    }

    /// <returns>true se a sessao anterior foi fechada aqui.</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    static bool CollectExitInfo(Context ctx, Telemetry.SessionFile? prev)
    {
        var am = (ActivityManager)ctx.GetSystemService(Context.ActivityService)!;
        var infos = am.GetHistoricalProcessExitReasons(null, 0, 32);
        if (infos == null) return false;
        var lastFile = Path.Combine(Telemetry.Dir, "exit-last");
        long last = 0;
        try { if (File.Exists(lastFile)) long.TryParse(File.ReadAllText(lastFile), out last); } catch { }
        long newest = last;
        bool closed = false;
        var crashed = Telemetry.CrashedPids();
        string pkg = ctx.PackageName!;

        foreach (var info in infos)
        {
            long ts = info.Timestamp;
            if (ts <= last) continue;
            newest = Math.Max(newest, ts);
            string name = info.ProcessName ?? "";
            string process = name == pkg ? "ide" : name.EndsWith(":build") ? "build" : name.EndsWith(":game") ? "game" : "other";
            if (process == "other") continue;
            int reason = (int)info.Reason;
            bool prevIde = process == "ide" && prev != null && !prev.Ended && info.Pid == prev.Pid;

            if (prevIde)
            {
                Telemetry.Append(new JsonObject { ["type"] = "session_end", ["session_id"] = prev!.Id, ["ts"] = ts, ["reason"] = SessionReason(reason) });
                closed = true;
            }

            string? kind = reason switch
            {
                CrashJava => crashed.Contains(info.Pid) ? null : "java", // ja registrado pelo Telemetry.Crash
                CrashNative => "native",
                Anr => "anr",
                LowMemory => process == "build" ? null : "oom",       // :build ocioso morto pelo sistema e normal
                ExcessiveResource or InitFailure => "killed",
                _ => null,
            };
            if (kind == null) continue;

            string? stack = kind switch
            {
                "anr" => ReadTrace(info, 48 * 1024, text: true),
                "native" => NativeStack(info, process, prevIde),
                _ => null,
            };
            Telemetry.CrashRecord(process, kind, ReasonName(reason), info.Description, stack,
                process == "ide" && prevIde ? PreviousLogTail(info.Pid) : null,
                new JsonObject
                {
                    ["pid"] = info.Pid, ["status"] = info.Status, ["importance"] = (int)info.Importance,
                    ["pss_kb"] = info.Pss, ["rss_kb"] = info.Rss, ["source"] = "exit_info",
                },
                ts, prevIde ? prev!.Id : null);
        }
        if (newest > last) File.WriteAllText(lastFile, newest.ToString());
        return closed;
    }

    static string SessionReason(int r) => r switch
    {
        CrashJava or CrashNative => "crash",
        Anr => "anr",
        LowMemory => "oom",
        ExitSelf or UserRequested or UserStopped => "normal",
        _ => "killed",
    };

    static string ReasonName(int r) => r switch
    {
        CrashJava => "REASON_CRASH", CrashNative => "REASON_CRASH_NATIVE", Anr => "REASON_ANR",
        LowMemory => "REASON_LOW_MEMORY", ExcessiveResource => "REASON_EXCESSIVE_RESOURCE_USAGE",
        InitFailure => "REASON_INITIALIZATION_FAILURE", _ => "REASON_" + r,
    };

    // Crash nativo da IDE: o registrador do gmhost (tag gmhost-crash) ja escreveu a pilha no
    // logcat, e o LogMirror guardou esse log em Download/gmide-log.prev.txt. Nos outros
    // processos (ou sem o log), usa as cadeias legiveis do tombstone (protobuf, API 31+).
    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    static string? NativeStack(ApplicationExitInfo info, string process, bool prevIde)
    {
        if (process == "ide" && prevIde)
        {
            var log = ReadPreviousLog(info.Pid);
            if (log != null)
            {
                var lines = log.Split('\n').Where(l => l.Contains("gmhost-crash") || l.Contains("Fatal signal")).ToArray();
                if (lines.Length > 0) return string.Join("\n", lines.TakeLast(80));
            }
        }
        return OperatingSystem.IsAndroidVersionAtLeast(31) ? ReadTrace(info, 256 * 1024, text: false) : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    static string? ReadTrace(ApplicationExitInfo info, int max, bool text)
    {
        try
        {
            using var s = info.TraceInputStream;
            if (s == null) return null;
            var buf = new byte[max];
            int n = 0, r;
            while (n < max && (r = s.Read(buf, n, max - n)) > 0) n += r;
            return text ? Encoding.UTF8.GetString(buf, 0, n) : PrintableStrings(buf, n);
        }
        catch (Exception e) { return "(trace ilegivel: " + e.Message + ")"; }
    }

    // O tombstone e protobuf: as cadeias de texto (nomes de funcao, bibliotecas, mensagem
    // de abort) aparecem na ordem dos quadros, o que basta para agrupar e ler a pilha.
    static string PrintableStrings(byte[] b, int n)
    {
        var sb = new StringBuilder();
        int start = -1, count = 0;
        for (int i = 0; i <= n && count < 120; i++)
        {
            bool printable = i < n && b[i] >= 0x20 && b[i] < 0x7f;
            if (printable) { if (start < 0) start = i; continue; }
            if (start >= 0 && i - start >= 4)
            {
                var s = Encoding.ASCII.GetString(b, start, i - start);
                if (s.Contains(".so") || s.Contains('/') || s.Contains('(') || s.Contains("::") || s.Contains('_') || char.IsLetter(s[0]))
                { sb.Append(s).Append('\n'); count++; }
            }
            start = -1;
        }
        return sb.ToString();
    }

    const string LogDir = "/storage/emulated/0/Download";

    static string? ReadPreviousLog(int pid)
    {
        // O LogMirror da sessao nova pode ja ter girado o arquivo: confere os dois pelo pid do cabecalho.
        foreach (var name in new[] { "gmide-log.prev.txt", "gmide-log.txt" })
        {
            try
            {
                var path = Path.Combine(LogDir, name);
                if (!File.Exists(path)) continue;
                using var r = new StreamReader(path);
                var header = r.ReadLine() ?? "";
                if (!header.Contains($"log do processo {pid} ")) continue;
                var fs = r.BaseStream;
                if (fs.Length > 256 * 1024) { fs.Seek(-256 * 1024, SeekOrigin.End); r.DiscardBufferedData(); }
                return r.ReadToEnd();
            }
            catch { }
        }
        return null;
    }

    static string? PreviousLogTail(int pid)
    {
        var log = ReadPreviousLog(pid);
        return log == null ? null : Telemetry.ClipTail(log, 48 * 1024);
    }
}
