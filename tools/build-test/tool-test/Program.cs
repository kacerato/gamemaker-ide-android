// tool-test: o caminho Android das ferramentas externas da IDE (docs/external-tools.md), no PC.
//
//   tool-test server <ideDir> <cache> <socket> <manifest>
//       Processo "build" (papel do servico :build): BuildBridge.StartServer + ToolBridge.Configure.
//   tool-test pt <exe> <script> [socket]
//       Faz o papel do ProjectTool.Run da IDE (CmdProcess): <exe> "SCRIPT PATH=\"<script>\"".
//       <exe> = ProjectTool.exe original (linha de base) ou gmexec.exe (stub -> ponte).
//   tool-test lsp <exe> <args> <projectDir> <gmlFile> <runtimeDir> [socket] [kill]
//       Faz o papel do ProcessLanguageServer: stdin/stdout redirecionados, JSON-RPC com
//       Content-Length: initialize, initialized, didOpen, completion, hover, shutdown, exit.
//       "kill": em vez de shutdown/exit, mata o stub (ProcessLanguageServer.Shutdown -> Kill).
//   tool-test e2e <ideDir> <cache> <manifest> <stub> <ptTestDir> <runtimeDir>
//       Sobe o servidor em outro processo e roda: ProjectTool (conversao, export YYZ, import YYZ,
//       FILE CHANGEVERSION) pelo stub, sessao LSP pelo stub, LSP morto no meio, e a recusa
//       com manifesto adulterado.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GmIde;

return args.Length == 0 ? Usage() : args[0] switch
{
    "server" => Server(args[1], args[2], args[3], args[4]),
    "pt" => Pt(args[1], args[2], args.Length > 3 ? args[3] : null),
    "lsp" => Lsp(args[1], args[2], args[3], args[4], args[5], args.Length > 6 ? args[6] : null, args.Length > 7 && args[7] == "kill"),
    "e2e" => E2E(args[1], args[2], args[3], args[4], args[5], args[6]),
    "restypes" => ResTypes(args[1], args[2]),
    _ => Usage(),
};

static int Usage() { Console.Error.WriteLine("uso: tool-test server|pt|lsp|e2e|restypes ..."); return 2; }

static int Server(string ideDir, string cache, string socket, string manifest)
{
    BuildBridge.SocketEndpoint = socket;
    // stderr original: o Console.Error passa a ser roteado para a ferramenta em execucao (como no :build,
    // onde o log vai para o logcat).
    var log = Console.Error;
    BuildBridge.Logger = s => { lock (log) log.WriteLine("[server " + Environment.ProcessId + "] " + s); };
    ToolBridge.Logger = s => { lock (log) log.WriteLine("[server " + Environment.ProcessId + "] [tools] " + s); };
    ToolBridge.Configure(ideDir, cache);
    ToolBridge.ManifestOverride = manifest;
    BuildBridge.StartServer();
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

// ---------------------------------------------------------------- ProjectTool (CmdProcess)

static int Pt(string exe, string script, string? socket)
{
    var psi = new ProcessStartInfo(exe, $"SCRIPT PATH=\"{script}\"")
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
    };
    if (socket != null) psi.Environment["GMEXEC_SOCKET"] = socket;
    var sw = Stopwatch.StartNew();
    using var p = Process.Start(psi)!;
    p.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("  | " + e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("  ! " + e.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine();
    p.WaitForExit();
    Console.WriteLine($"[ide] ProjectTool exit {p.ExitCode} em {sw.ElapsedMilliseconds} ms");
    return p.ExitCode;
}

// O arquivo RESOURCETYPESPATH que a IDE gera (ProjectTool.EnsureResourceTypesListFileCreated ->
// ResourceBase.ResourceVersionsAsJsonFileImage do CoreResources.dll da IDE).
static int ResTypes(string ideDir, string outFile)
{
    var alc = new System.Runtime.Loader.AssemblyLoadContext("ide-coreres");
    alc.Resolving += (c, n) => { var p = Path.Combine(ideDir, n.Name + ".dll"); return File.Exists(p) ? c.LoadFromAssemblyPath(p) : null; };
    var asm = alc.LoadFromAssemblyPath(Path.Combine(ideDir, "CoreResources.dll"));
    var t = asm.GetTypes().First(t => t.Name == "ResourceBase" && t.GetMethod("ResourceVersionsAsJsonFileImage") != null);
    var data = (byte[])t.GetMethod("ResourceVersionsAsJsonFileImage")!.Invoke(null, null)!;
    File.WriteAllBytes(outFile, data);
    Console.WriteLine($"[ide] RESOURCETYPESPATH: {outFile} ({data.Length} bytes)");
    return 0;
}

// ---------------------------------------------------------------- Language Server

static int Lsp(string exe, string lspArgs, string projectDir, string gmlFile, string runtimeDir, string? socket, bool kill)
{
    // Igual ao ProcessLanguageServer.Initialize (LogLevel None: stderr nao redirecionado).
    var psi = new ProcessStartInfo(exe, lspArgs)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true,
    };
    if (socket != null) psi.Environment["GMEXEC_SOCKET"] = socket;
    var sw = Stopwatch.StartNew();
    using var p = Process.Start(psi)!;
    var c = new LspClient(p.StandardInput.BaseStream, p.StandardOutput.BaseStream);
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"[ide] {(ok ? "ok  " : "FALHOU")} {what}"); if (!ok) fails++; }
    try
    {
        var root = new Uri(Path.GetFullPath(projectDir) + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var init = c.Request("initialize", new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = root,
            ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = root, ["name"] = Path.GetFileName(projectDir) }),
            ["capabilities"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["completion"] = new JsonObject { ["completionItem"] = new JsonObject { ["snippetSupport"] = true } },
                    ["hover"] = new JsonObject(),
                    // Como o LspClient do CodeEditor2 (o SemanticTokensHandler exige).
                    ["semanticTokens"] = new JsonObject
                    {
                        ["dynamicRegistration"] = false,
                        ["formats"] = new JsonArray("relative"),
                        ["multilineTokenSupport"] = true,
                        ["overlappingTokenSupport"] = false,
                        ["tokenModifiers"] = new JsonArray("declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary", "global", "unset"),
                        ["tokenTypes"] = new JsonArray("namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator", "constant"),
                        ["requests"] = new JsonObject { ["full"] = new JsonObject { ["delta"] = true }, ["range"] = new JsonObject() },
                    },
                    ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true, ["labelSupport"] = true },
                    ["foldingRange"] = new JsonObject { ["lineFoldingOnly"] = true },
                    ["definition"] = new JsonObject { ["linkSupport"] = true },
                    ["rename"] = new JsonObject(),
                    ["codeAction"] = new JsonObject(),
                    ["publishDiagnostics"] = new JsonObject(),
                    ["synchronization"] = new JsonObject { ["didSave"] = true },
                },
                ["workspace"] = new JsonObject { ["workspaceFolders"] = true, ["configuration"] = true },
            },
            ["initializationOptions"] = new JsonObject
            {
                ["RuntimeDirectory"] = runtimeDir,
                ["RuntimeVersion"] = Path.GetFileName(runtimeDir).Replace("runtime-", ""),
                ["Platforms"] = new JsonArray("windows", "operagx", "html5"),
                ["Modules"] = new JsonArray(),
                ["Locale"] = "GB",
                ["PrefabLibraryPath"] = @"C:\ProgramData\GameMakerStudio2-LTS2026\Prefabs",
                ["Language"] = "english",
            },
        }, TimeSpan.FromSeconds(90));
        var caps = init?["result"]?["capabilities"];
        Check(caps != null, $"initialize ({sw.ElapsedMilliseconds} ms): capabilities {Short(caps)}");
        c.Notify("initialized", new JsonObject());

        var uri = new Uri(Path.GetFullPath(gmlFile)).AbsoluteUri;
        var text = File.ReadAllText(gmlFile);
        c.Notify("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "gml", ["version"] = 1, ["text"] = text },
        });
        // Posicao: depois de "draw_" na primeira linha que tiver (ou fim da linha 0).
        var lines = text.Split('\n');
        int line = 0, ch = lines[0].TrimEnd('\r').Length;
        for (int i = 0; i < lines.Length; i++)
        {
            int k = lines[i].IndexOf("draw_", StringComparison.Ordinal);
            if (k >= 0) { line = i; ch = k + 5; break; }
        }
        var comp = c.Request("textDocument/completion", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = ch },
        }, TimeSpan.FromSeconds(90));
        var items = comp?["result"] is JsonArray a ? a : comp?["result"]?["items"] as JsonArray;
        Check(items != null && items.Count > 0,
            $"completion em {Path.GetFileName(gmlFile)}:{line + 1}:{ch + 1}: {items?.Count ?? 0} itens, ex.: " +
            string.Join(", ", items?.Take(5).Select(i => i?["label"]?.ToString()) ?? Array.Empty<string>()));

        var hover = c.Request("textDocument/hover", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = Math.Max(0, ch - 1) },
        }, TimeSpan.FromSeconds(30));
        Check(hover != null && hover["error"] == null, $"hover: {Short(hover?["result"])}");
        Console.WriteLine($"[ide] notificacoes recebidas: {string.Join(", ", c.NotificationCounts.Select(kv => kv.Key + " x" + kv.Value))}");

        if (kill)
        {
            Console.WriteLine("[ide] matando o Language Server (ProcessLanguageServer.Shutdown -> Kill)");
            p.Kill();
            p.WaitForExit();
        }
        else
        {
            var sd = c.Request("shutdown", null, TimeSpan.FromSeconds(30));
            Check(sd != null && sd["error"] == null, "shutdown");
            c.Notify("exit", null);
            bool exited = p.WaitForExit(15000);
            Check(exited, $"exit: processo saiu ({(exited ? p.ExitCode.ToString() : "nao saiu")})");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine("[ide] FALHOU: " + e.Message);
        fails++;
        try { p.Kill(); } catch { }
    }
    Console.WriteLine($"[ide] LSP: {fails} falhas, {sw.ElapsedMilliseconds} ms");
    return fails == 0 ? 0 : 1;
}

static string Short(JsonNode? n)
{
    var s = n?.ToJsonString() ?? "null";
    return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
}

// ---------------------------------------------------------------- e2e

static int E2E(string ideDir, string cache, string manifest, string stub, string ptDir, string runtimeDir)
{
    var socket = Path.Combine(Path.GetTempPath(), "gmexec-tools-" + Environment.ProcessId + ".sock");
    var self = Environment.ProcessPath!;
    Process StartServer(string man)
    {
        var si = new ProcessStartInfo(self) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "server", ideDir, cache, socket, man }) si.ArgumentList.Add(a);
        var s = Process.Start(si)!;
        s.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("    " + e.Data); };
        s.BeginErrorReadLine();
        for (int i = 0; i < 50 && !File.Exists(socket); i++) Thread.Sleep(100);
        return s;
    }

    var results = new List<(string, int, int)>();
    void Run(string name, int expected, Func<int> f)
    {
        Console.WriteLine($"\n=== {name}");
        int rc;
        try { rc = f(); } catch (Exception e) { Console.WriteLine(e); rc = -999; }
        results.Add((name, rc, expected));
    }

    var server = StartServer(manifest);
    try
    {
        var w = Path.GetFullPath(ptDir);
        var outDir = Path.Combine(w, "stub");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        var src = Path.Combine(w, "src", "Idle Game Template.yyp");
        var pf = @"PREFABSFOLDER=""C:\ProgramData\GameMakerStudio2-LTS2026\Prefabs""";
        var conv = $@"CONVROOT=""{Path.Combine(outDir, "convroot")}""";
        var resTypes = Path.Combine(outDir, "resourcetypes.json");
        ResTypes(ideDir, resTypes);
        var rt = $@"RESOURCETYPESPATH=""{resTypes}""";
        string Script(string n, string body) { var f = Path.Combine(outDir, n); File.WriteAllText(f, body + $" {rt} {pf} {conv}"); return f; }

        // Como a IDE: ProjectTool.Convert -> "PROJECT SAVE SOURCE=... DESTINATION=..."
        Run("ProjectTool: PROJECT SAVE (conversao/copia do projeto)", 0, () =>
            Pt(stub, Script("s1.txt", $@"PROJECT SAVE SOURCE=""{src}"" DESTINATION=""{Path.Combine(outDir, "conv", "Idle Game Template.yyp")}"""), socket));
        Run("ProjectTool: EXPORT para YYZ", 0, () =>
            Pt(stub, Script("s2.txt", $@"EXPORT SOURCE=""{src}"" DESTINATION=""{Path.Combine(outDir, "proj.yyz")}"" PACKAGETYPE=Project"), socket));
        Run("ProjectTool: importar YYZ (PROJECT SAVE SOURCE=.yyz)", 0, () =>
            Pt(stub, Script("s3.txt", $@"PROJECT SAVE SOURCE=""{Path.Combine(outDir, "proj.yyz")}"" DESTINATION=""{Path.Combine(outDir, "imp", "Idle Game Template.yyp")}"""), socket));
        var yy = Directory.GetFiles(Path.Combine(w, "src", "objects"), "*.yy", SearchOption.AllDirectories).OrderBy(x => x).First();
        Run("ProjectTool: QUIET FILE CHANGEVERSION", 0, () =>
        {
            var f = Script("s4.txt", $@"FILE CHANGEVERSION SOURCE=""{yy}"" DESTINATION=""{Path.Combine(outDir, "cv.yy")}""");
            var psi = new ProcessStartInfo(stub, $"QUIET SCRIPT PATH=\"{f}\"") { UseShellExecute = false, RedirectStandardOutput = true };
            psi.Environment["GMEXEC_SOCKET"] = socket;
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Console.Write(string.Join("", o.Split('\n').Select(l => "  | " + l + "\n")));
            Console.WriteLine($"[ide] exit {p.ExitCode}");
            return p.ExitCode;
        });

        var gml = Directory.GetFiles(Path.Combine(w, "src"), "*.gml", SearchOption.AllDirectories)
            .OrderByDescending(f => File.ReadAllText(f).Contains("draw_")).ThenBy(f => f).First();
        var lspArgs = ToolBridge.ToolArgPrefix + ToolBridge.LspToolName + " --log-level=None ";
        Run("LSP: initialize/didOpen/completion/hover/shutdown/exit pelo stub", 0, () =>
            Lsp(stub, lspArgs, Path.Combine(w, "src"), gml, runtimeDir, socket, kill: false));
        Run("LSP: stub morto no meio (Shutdown -> Kill); o servidor continua vivo", 0, () =>
        {
            int rc = Lsp(stub, lspArgs, Path.Combine(w, "src"), gml, runtimeDir, socket, kill: true);
            Thread.Sleep(ToolBridge.LspExitGraceSeconds * 1000 + 3000);
            return rc != 0 ? rc : server.HasExited ? 3 : 0;
        });
        Run("LSP de novo no mesmo servidor (RebootClient)", 0, () =>
            Lsp(stub, lspArgs, Path.Combine(w, "src"), gml, runtimeDir, socket, kill: false));
    }
    finally
    {
        try { server.Kill(); server.WaitForExit(); } catch { }
    }

    // Manifesto adulterado: hash do ProjectTool.exe e do GameMakerLanguageServer.dll trocados.
    var bad = Path.Combine(Path.GetTempPath(), "gmide-tools-bad-" + Environment.ProcessId + ".json");
    var j = JsonNode.Parse(File.ReadAllText(manifest))!;
    j["files"]![ToolBridge.ProjectToolExeRel]!["sha256"] = new string('0', 64);
    j["files"]![ToolBridge.LspDllRel]!["sha256"] = new string('0', 64);
    File.WriteAllText(bad, j.ToJsonString());
    var badCache = Path.Combine(cache, "..", Path.GetFileName(cache) + "-bad");
    if (Directory.Exists(badCache)) Directory.Delete(badCache, true);
    server = StartServer(bad);
    try
    {
        var f = Path.Combine(Path.GetFullPath(ptDir), "stub", "s1.txt");
        Run("Recusa: ProjectTool.exe com hash diferente do manifesto", 1, () => Pt(stub, f, socket));
        Run("Recusa: GameMakerLanguageServer.dll com hash diferente", 1, () =>
        {
            var psi = new ProcessStartInfo(stub, ToolBridge.ToolArgPrefix + ToolBridge.LspToolName + " --log-level=None")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.Environment["GMEXEC_SOCKET"] = socket;
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Console.WriteLine("  ! " + err.Trim());
            Console.WriteLine($"[ide] exit {p.ExitCode}");
            return p.ExitCode;
        });
    }
    finally
    {
        try { server.Kill(); server.WaitForExit(); } catch { }
        File.Delete(bad);
    }

    Console.WriteLine("\n=== Resumo");
    int bads = 0;
    foreach (var (n, rc, exp) in results)
    {
        bool ok = rc == exp;
        if (!ok) bads++;
        Console.WriteLine($"{(ok ? "ok    " : "FALHOU")} {n} (exit {rc}, esperado {exp})");
    }
    return bads == 0 ? 0 : 1;
}

/// <summary>Cliente JSON-RPC minimo (Content-Length) com leitura em segundo plano.</summary>
sealed class LspClient
{
    readonly Stream _in, _out;
    int _id;
    readonly Dictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    public readonly Dictionary<string, int> NotificationCounts = new();
    readonly object _wlock = new();

    public LspClient(Stream toServer, Stream fromServer)
    {
        _in = toServer; _out = fromServer;
        new Thread(ReadLoop) { IsBackground = true }.Start();
    }

    void Send(JsonObject msg)
    {
        msg["jsonrpc"] = "2.0";
        var body = Encoding.UTF8.GetBytes(msg.ToJsonString());
        var head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_wlock) { _in.Write(head); _in.Write(body); _in.Flush(); }
    }

    public void Notify(string method, JsonNode? p)
    {
        var m = new JsonObject { ["method"] = method };
        if (p != null) m["params"] = p;
        Send(m);
    }

    public JsonNode? Request(string method, JsonNode? p, TimeSpan timeout)
    {
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonNode>();
        lock (_pending) _pending[id] = tcs;
        var m = new JsonObject { ["id"] = id, ["method"] = method };
        if (p != null) m["params"] = p;
        Send(m);
        if (!tcs.Task.Wait(timeout)) throw new TimeoutException($"{method}: sem resposta em {timeout.TotalSeconds} s");
        var r = tcs.Task.Result;
        if (r["error"] != null) Console.WriteLine($"[ide] {method}: erro {r["error"]!.ToJsonString()}");
        return r;
    }

    void ReadLoop()
    {
        try
        {
            while (true)
            {
                int len = -1;
                var sb = new StringBuilder();
                while (true)
                {
                    int b = _out.ReadByte();
                    if (b < 0) throw new EndOfStreamException();
                    sb.Append((char)b);
                    if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
                }
                foreach (var h in sb.ToString().Split("\r\n"))
                    if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = int.Parse(h.Substring(15).Trim());
                var buf = new byte[len];
                int got = 0;
                while (got < len) { int k = _out.Read(buf, got, len - got); if (k <= 0) throw new EndOfStreamException(); got += k; }
                var msg = JsonNode.Parse(buf)!.AsObject();
                if (msg["method"] is JsonNode mn)
                {
                    var method = mn.ToString();
                    lock (NotificationCounts) NotificationCounts[method] = NotificationCounts.GetValueOrDefault(method) + 1;
                    if (msg["id"] is JsonNode rid)
                    {
                        // Pedido do servidor (workspace/configuration, client/registerCapability...): responde vazio.
                        JsonNode? result = null;
                        if (method == "workspace/configuration")
                            result = new JsonArray((msg["params"]?["items"] as JsonArray)?.Select(_ => (JsonNode?)null).ToArray() ?? Array.Empty<JsonNode?>());
                        Send(new JsonObject { ["id"] = rid.DeepClone(), ["result"] = result });
                    }
                    continue;
                }
                if (msg["id"] is JsonNode idn && idn.GetValueKind() == JsonValueKind.Number)
                {
                    TaskCompletionSource<JsonNode>? t;
                    lock (_pending) { _pending.Remove(idn.GetValue<int>(), out t); }
                    t?.TrySetResult(msg);
                }
            }
        }
        catch (Exception)
        {
            lock (_pending) foreach (var t in _pending.Values) t.TrySetException(new EndOfStreamException("stdout do servidor fechou"));
        }
    }
}
