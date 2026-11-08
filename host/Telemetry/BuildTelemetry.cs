using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GmIde;

// Um build do Igor no processo :build (BuildBridge.HandleClient). Os builds sao exclusivos
// (_buildLock), entao ha no maximo um em andamento. Termina por um destes caminhos:
//  - Dispatch voltou (End);
//  - Environment.Exit do Igor/GMAssetCompiler, o caminho dos erros de GML (OnProcessExit);
//  - Stop na IDE: o stub desconecta e o processo e morto em seguida (Cancel).
// Em "Run", a duracao e a da compilacao: termina quando o jogo abre (CompileDone).
static class BuildTelemetry
{
    sealed class Current
    {
        public required string Target;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public long? CompileMs;
        public bool ToolCrash, SawGml, Done;
        public readonly Queue<string> Errors = new();
    }

    static Current? _cur;
    static readonly Regex ErrorLine = new(@"error|exception|failed|fatal", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Comeca o registro, exceto nas compilacoes do "Gerar APK"/exportacoes (registradas pelo ApkExport).</summary>
    public static void Begin(string[] argv, IDictionary<string, string> env)
    {
        _cur = null;
        if (env.TryGetValue("GMIDE_LOCAL_ANDROID_EXPORT", out var a) && a == "1") return;
        if (env.TryGetValue("GMIDE_LOCAL_WEB_EXPORT", out var w) && w == "1") return;
        _cur = new Current { Target = TargetOf(argv) };
    }

    public static string TargetOf(string[] argv)
    {
        int i = Array.IndexOf(argv, "--");
        if (i < 0 || i + 2 >= argv.Length) return "other";
        string platform = argv[i + 1].ToLowerInvariant(), action = argv[i + 2].ToLowerInvariant();
        if (action is "run" or "debug")
            return platform switch { "operagx" => "run_gx", "android" => "run_android", _ => "other" };
        return platform switch
        {
            "android" => "apk",
            "html5" or "operagx" => "web",
            "windows" => "windows",
            _ => "other",
        };
    }

    public static void Line(string line)
    {
        var c = _cur;
        if (c == null || c.Done) return;
        if (line.Contains("gml_", StringComparison.Ordinal)) c.SawGml = true;
        if (!ErrorLine.IsMatch(line)) return;
        lock (c.Errors)
        {
            c.Errors.Enqueue(line.Length > 400 ? line[..400] : line);
            while (c.Errors.Count > 12) c.Errors.Dequeue();
        }
    }

    /// <summary>O jogo abriu (runner GX no WebView ou RunnerActivity): fim da compilacao.</summary>
    public static void CompileDone()
    {
        var c = _cur;
        if (c != null && c.CompileMs == null) c.CompileMs = c.Clock.ElapsedMilliseconds;
    }

    /// <summary>Excecao que escapou do Igor/GMAssetCompiler/webserver: bug a corrigir, vira crash tambem.</summary>
    public static void ToolException(Exception e)
    {
        if (_cur != null) _cur.ToolCrash = true;
        Telemetry.CrashRecord("build", "managed", e.GetType().FullName, e.Message, e.ToString(), null,
            new JsonObject { ["fatal"] = false, ["target"] = _cur?.Target, ["source"] = "tool" });
    }

    public static void End(int code, bool viaExit = false)
    {
        var c = _cur;
        if (c == null || c.Done) return;
        c.Done = true;
        string result = code == 0 ? "ok" : c.ToolCrash ? "tool_crash" : c.SawGml ? "gml_error" : "failed";
        string? error;
        lock (c.Errors) error = c.Errors.Count == 0 ? null : string.Join("\n", c.Errors);
        Telemetry.Build(c.Target, result, c.CompileMs ?? c.Clock.ElapsedMilliseconds, code, code == 0 ? null : error);
        if (viaExit) Telemetry.Event("build.process_exit", new() { ["code"] = code, ["target"] = c.Target });
    }

    public static void Cancel()
    {
        var c = _cur;
        if (c == null || c.Done) return;
        c.Done = true;
        // Stop depois do jogo abrir e o fim normal de um Run, nao um cancelamento.
        Telemetry.Build(c.Target, c.CompileMs != null ? "ok" : "cancelled", c.CompileMs ?? c.Clock.ElapsedMilliseconds);
    }
}
