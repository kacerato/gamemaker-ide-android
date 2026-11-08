// Ferramentas externas da IDE dentro do APK: ProjectTool e Language Server de GML
// (docs/external-tools.md). Reaproveita a ponte do Build/Run (BuildBridge.cs): a IDE lanca o
// stub ELF libgmexec.so como se fosse a ferramenta, o stub repassa argv/cwd/env/stdin pelo
// socket unix e o servico :build roda a ferramenta .NET num AssemblyLoadContext proprio.
//
//   ProjectTool  (YoYoStudio.Resources.ProjectTool.Run, IDE.dll)
//     CmdProcess(<ProjectToolLocator.FindTool()>, "[QUIET ]SCRIPT PATH=\"<tmp>\""), stdout linha a
//     linha, exit code. O caminho vem do pacote "@gm-tools/project-tool-<RID>" que
//     ToolIntegration registra em ide/packages (gm.tool.path absoluto = stub).
//     ProjectTool.exe e um bundle single-file self-contained .NET 8 (como o Igor): extraimos
//     ProjectTool.dll + dependencias para o cache, depois de conferir o hash.
//   GameMakerLanguageServer  (CodeEditor2.ProcessLanguageServer)
//     Process.Start(path, args) com stdin/stdout redirecionados: JSON-RPC (LSP) com
//     Content-Length, nos dois sentidos, enquanto o editor estiver aberto. O path vem de
//     ide/CodeEditor2/Servers/GML.lspConfig, que ToolIntegration reescreve para o stub com o
//     primeiro argumento "--gmexec-tool=GameMakerLanguageServer". O Main original nao serve
//     em processo (Console.SetOut(TextWriter.Null) e Console.OpenStandardInput/Output sao do
//     processo inteiro); ToolBridge refaz o Main com os streams do socket.
//
// Integridade: gmide-tools-integrity.json (tools/make-tools-manifest.ps1, Authenticode no PC).
// Cada DLL que o Language Server carrega da pasta da IDE precisa estar no manifesto e conferir
// o SHA-256; o ProjectTool.exe (nao assinado no original) confere o hash antes de extrair.
//
// Sem APIs Android: o mesmo fonte roda no PC (tools/build-test/tool-test).

using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace GmIde;

public enum ExternalTool { None, ProjectTool, LanguageServer }

public static class ToolBridge
{
    public const string ManifestName = "gmide-tools-integrity.json";
    public const string ProjectToolDirRel = "packages/gm-tools/project-tool-win-x64";
    public const string ProjectToolExeRel = ProjectToolDirRel + "/ProjectTool.exe";
    public const string LspDllRel = "GameMakerLanguageServer.dll";
    public const string LspConfigRel = "CodeEditor2/Servers/GML.lspConfig";
    /// <summary>Primeiro argumento que o GML.lspConfig reescrito passa ao stub.</summary>
    public const string ToolArgPrefix = "--gmexec-tool=";
    public const string LspToolName = "GameMakerLanguageServer";
    public const string ProjectToolName = "ProjectTool";

    /// <summary>Pasta da copia da IDE (ide/ no app; a instalacao no teste do PC).</summary>
    public static string IdeDir { get; private set; } = "";
    /// <summary>Pasta gravavel para extrair o ProjectTool.exe.</summary>
    public static string CacheDir { get; private set; } = "";
    /// <summary>Teste no PC: manifesto fora da pasta da IDE.</summary>
    public static string? ManifestOverride;
    /// <summary>Segundos que o Language Server tem para sair sozinho depois do EOF do stdin.</summary>
    public static int LspExitGraceSeconds = 5;

    public static Action<string> Logger = s => BuildBridge.Logger("[tools] " + s);

    public static void Configure(string ideDir, string cacheDir)
    {
        IdeDir = ideDir;
        CacheDir = cacheDir;
    }

    public static string ManifestPath => ManifestOverride ?? Path.Combine(IdeDir, ManifestName);

    // ------------------------------------------------------------------ classificacao

    /// <summary>
    /// Decide se o pedido do stub e uma ferramenta externa (e qual) ou o Igor/webserver de sempre.
    /// <paramref name="argv"/> perde o "--gmexec-tool=..." quando ele vem.
    /// </summary>
    public static ExternalTool Classify(ref string[] argv, IDictionary<string, string> env)
    {
        if (argv.Length > 0 && argv[0].StartsWith(ToolArgPrefix, StringComparison.Ordinal))
        {
            var name = argv[0].Substring(ToolArgPrefix.Length);
            argv = argv[1..];
            return FromName(name);
        }
        // Stub chamado por um link com o nome da ferramenta (argv[0]).
        if (env.TryGetValue("GMEXEC_ARGV0", out var a0) && a0.Length > 0)
        {
            var t = FromName(Path.GetFileNameWithoutExtension(a0.Replace('\\', '/').Split('/')[^1]));
            if (t != ExternalTool.None) return t;
        }
        // ProjectTool.Run: "[QUIET ]SCRIPT PATH=\"...\"" (prefixos do GMTool: ECHO, ATTACH, QUIET).
        int i = 0;
        while (i < argv.Length && argv[i] is "ECHO" or "ATTACH" or "QUIET") i++;
        if (i < argv.Length && argv[i].Equals("SCRIPT", StringComparison.OrdinalIgnoreCase) &&
            i + 1 < argv.Length && argv[i + 1].StartsWith("PATH=", StringComparison.OrdinalIgnoreCase))
            return ExternalTool.ProjectTool;
        return ExternalTool.None;
    }

    static ExternalTool FromName(string name) =>
        name.Equals(ProjectToolName, StringComparison.OrdinalIgnoreCase) ? ExternalTool.ProjectTool :
        name.Equals(LspToolName, StringComparison.OrdinalIgnoreCase) ? ExternalTool.LanguageServer :
        ExternalTool.None;

    // ------------------------------------------------------------------ integridade

    static JsonElement? _manifest;
    static DateTime _manifestMtime;
    static readonly object _manifestLock = new();

    static JsonElement? LoadManifest(out string error)
    {
        error = "";
        var path = ManifestPath;
        lock (_manifestLock)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                error = $"{path} ausente: a verificacao das ferramentas no PC nao foi feita (tools/make-tools-manifest.ps1)";
                return null;
            }
            if (_manifest != null && fi.LastWriteTimeUtc == _manifestMtime) return _manifest;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                _manifest = doc.RootElement.Clone();
                _manifestMtime = fi.LastWriteTimeUtc;
                return _manifest;
            }
            catch (Exception e)
            {
                error = $"{ManifestName} invalido: {e.Message}";
                return null;
            }
        }
    }

    /// <summary>
    /// Confere <paramref name="rel"/> (relativo a <see cref="IdeDir"/>, com '/') contra o manifesto.
    /// <paramref name="requireSigned"/>: exige que o PC tenha visto assinatura Authenticode valida.
    /// Um arquivo que os patches de IL do app (IdePatches) alteraram e aceito se o
    /// "&lt;arquivo&gt;.original" guardado por eles conferir e o marcador ".gmide-patch" existir.
    /// </summary>
    public static bool VerifyFile(string rel, bool requireSigned, out string error)
    {
        error = "";
        var m = LoadManifest(out error);
        if (m == null) return false;
        if (!m.Value.TryGetProperty("files", out var files) || !files.TryGetProperty(rel, out var entry))
        {
            error = $"{rel} fora do manifesto {ManifestName}";
            return false;
        }
        var want = entry.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
        long size = entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : -1;
        bool signed = entry.TryGetProperty("signed", out var sg) && sg.ValueKind == JsonValueKind.True;
        if (want.Length != 64) { error = $"{rel}: hash ausente no manifesto"; return false; }
        if (requireSigned && !signed) { error = $"{rel}: sem assinatura verificada no PC"; return false; }

        var path = Path.Combine(IdeDir, rel);
        if (Matches(path, size, want)) return true;
        if (File.Exists(path + ".gmide-patch") && Matches(path + ".original", size, want))
        {
            Logger($"{rel}: copia alterada pelos patches do app; o original confere");
            return true;
        }
        error = File.Exists(path) ? $"{rel} difere da copia verificada no PC (esperado {want})" : $"{path} nao existe";
        return false;
    }

    static bool Matches(string path, long size, string want)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || (size >= 0 && fi.Length != size)) return false;
        return RuntimeIntegrity.Hash(fi).Equals(want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Caminho relativo a IdeDir (com '/') se <paramref name="fullPath"/> estiver dentro dela.</summary>
    internal static string? RelToIde(string fullPath)
    {
        var root = Path.GetFullPath(IdeDir).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        return full.Substring(root.Length).Replace('\\', '/');
    }

    // ------------------------------------------------------------------ ProjectTool

    static readonly object _ptLock = new();

    /// <summary>
    /// Confere o ProjectTool.exe e extrai o bundle (uma vez por hash) junto com Formats/ e o
    /// package.json (o ProjectTool procura os dois ao lado do executavel). Retorna a pasta.
    /// </summary>
    public static string PrepareProjectTool()
    {
        lock (_ptLock)
        {
            if (!VerifyFile(ProjectToolExeRel, requireSigned: false, out var err))
                throw new InvalidOperationException("ProjectTool recusado: " + err);
            var exe = new FileInfo(Path.Combine(IdeDir, ProjectToolExeRel));
            var hash = RuntimeIntegrity.Hash(exe);
            var libDir = Path.Combine(CacheDir, "projecttool-" + hash.Substring(0, 16).ToLowerInvariant());
            if (File.Exists(Path.Combine(libDir, ".gmide-complete")))
            {
                PatchAssemblyScans(libDir); // extracoes antigas no cache ainda sem o patch
                return libDir;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = BuildBridge.ExtractSingleFileBundle(exe.FullName, libDir);
            // Dados ao lado do .exe (Formats/, *package.json), tambem conferidos com o manifesto.
            var srcDir = Path.Combine(IdeDir, ProjectToolDirRel);
            foreach (var src in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcDir, src).Replace('\\', '/');
                if (rel == "ProjectTool.exe") continue;
                if (!VerifyFile(ProjectToolDirRel + "/" + rel, requireSigned: false, out var derr))
                    throw new InvalidOperationException("ProjectTool: " + derr);
                var dst = Path.Combine(libDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
            }
            File.WriteAllText(Path.Combine(libDir, ".gmide-complete"), hash);
            Logger($"ProjectTool extraido: {n} arquivos em {libDir} ({sw.ElapsedMilliseconds} ms)");
            PatchAssemblyScans(libDir);
            return libDir;
        }
    }

    const string AssemblyScanPatchId = "alc-assembly-scan-v1";
    static readonly string[] AssemblyScanPatchTargets = { "ProjectTool.dll", "CoreResources.dll" };

    /// <summary>
    /// Restringe as varreduras de tipos do ProjectTool aos assemblies do proprio ALC.
    ///
    /// AllOptionsList.GetAllOptions / ResourceInfo (todas as versoes de formato) e CoreResources
    /// fazem AppDomain.CurrentDomain.GetAssemblies() e GetTypes() em cada um. No ProjectTool.exe
    /// (processo proprio) isso so ve os assemblies dele. No :build ve tambem o host
    /// (GmIde.Android.dll referencia OSCore, que o ALC padrao desse processo nao resolve), o
    /// Igor e o Language Server: GetTypes lanca FileNotFoundException(OSCore). Em
    /// ResourcesVERS0.GMProject.LoadUpdate essa excecao sai da Task de leitura (AddOptions fica
    /// fora do try/catch), bReadComplete nunca vira true e a thread do loader gira para sempre:
    /// era o travamento ao criar projeto a partir de template da Start Page.
    ///
    /// Troca cada  callvirt AppDomain::GetAssemblies()  por
    ///   pop; ldtoken &lt;tipo do metodo&gt;; Type.GetTypeFromHandle; get_Assembly;
    ///   AssemblyLoadContext.GetLoadContext; get_Assemblies; Enumerable.ToArray&lt;Assembly&gt;
    /// nas copias extraidas no cache (os arquivos da IDE nao mudam). Idempotente (marcador).
    /// </summary>
    internal static void PatchAssemblyScans(string libDir)
    {
        var marker = Path.Combine(libDir, ".gmide-patch-" + AssemblyScanPatchId);
        if (File.Exists(marker)) return;
        try { PatchAssemblyScansCore(libDir, marker); }
        catch (Exception e) { Logger($"Patch {AssemblyScanPatchId} falhou (ProjectTool segue sem ele): {e}"); }
    }

    static void PatchAssemblyScansCore(string libDir, string marker)
    {
        var sb = new StringBuilder();
        foreach (var name in AssemblyScanPatchTargets)
        {
            var path = Path.Combine(libDir, name);
            if (!File.Exists(path)) { sb.Append(name).Append(": ausente; "); continue; }
            var original = path + ".original";
            if (!File.Exists(original)) File.Copy(path, original);
            int count;
            var resolver = new Mono.Cecil.DefaultAssemblyResolver();
            resolver.AddSearchDirectory(libDir);
            using (var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(original,
                       new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver, ReadingMode = Mono.Cecil.ReadingMode.Immediate }))
            {
                count = AssemblyScanPatches.Rewrite(asm.MainModule);
                var tmp = path + ".tmp";
                asm.Write(tmp);
                File.Move(tmp, path, true);
            }
            sb.Append(name).Append(": ").Append(count).Append(" chamadas; ");
        }
        File.WriteAllText(marker, sb.ToString());
        Logger($"Patch {AssemblyScanPatchId}: {sb}");
    }


}

// Parte do BuildBridge (acesso ao roteamento do Console, LineWriter, FrameWriter, ToolLoadContext).
public static partial class BuildBridge
{
    static int _toolCounter;

    static partial void TryRunTool(string[] argv, string cwd, Dictionary<string, string> env, BinaryReader input,
        FrameWriter output, ref bool handled, ref int code)
    {
        var kind = ToolBridge.Classify(ref argv, env);
        if (kind == ExternalTool.None) return;
        handled = true;
        var stdin = new FrameInputStream(input);
        if (ToolBridge.IdeDir.Length == 0)
        {
            stdin.DrainInBackground();
            output.Write('E', "[ToolBridge] ferramentas externas nao configuradas (ToolIntegration)");
            code = 1;
            return;
        }
        Logger($"{kind}: {string.Join(" ", argv)}");
        // O ProjectTool nao le o stdin (modo SCRIPT). Os frames 'I' do stub precisam ser lidos
        // mesmo assim: fechar o socket com dados nao lidos manda RST, e o stub perde o 'X'.
        if (kind == ExternalTool.ProjectTool) stdin.DrainInBackground();
        try
        {
            code = kind == ExternalTool.ProjectTool
                ? RunProjectTool(argv, stdin, output)
                : RunLanguageServer(argv, stdin, output);
        }
        catch (Exception e)
        {
            Logger($"{kind}: {e}");
            try { output.Write('E', $"[ToolBridge] {kind}: {e.Message}"); } catch { }
            code = 1;
        }
        Logger($"{kind} terminou com {code}");
    }

    // ------------------------------------------------------------------ ProjectTool em processo

    static int RunProjectTool(string[] argv, FrameInputStream stdin, FrameWriter output)
    {
        var libDir = ToolBridge.PrepareProjectTool();

        // Um ALC por execucao: estado estatico limpo, como um processo novo. (O Mono nao descarrega
        // ALCs; a IDE so chama o ProjectTool em importacoes/conversoes e "FILE CHANGEVERSION" quando
        // um recurso precisa de downgrade.)
        // O bundle contem o ProjectTool e as dependencias do pacote, mas alguns comandos de
        // importacao tambem dependem das DLLs da instalacao da IDE (por exemplo OSCore.dll).
        // O ProjectTool.exe original as resolve ao lado da IDE; no ALC isolado, reproduzimos
        // esse caminho de procura sem alterar o carregamento do Igor nem do Language Server.
        var alc = new ToolLoadContext("projecttool-" + Interlocked.Increment(ref _toolCounter),
            new List<string> { libDir, ToolBridge.IdeDir });
        // Cada linha vai para a IDE (janela Output) e tambem para o log, com a hora da ultima:
        // se o ProjectTool parar no meio, o log mostra onde.
        string lastLine = "(nenhuma saida)";
        long lastTick = Environment.TickCount64;
        Action<string> Mirror(char type)
        {
            var toIde = SafeLine(output, type);
            return line =>
            {
                toIde(line);
                lastLine = line; Volatile.Write(ref lastTick, Environment.TickCount64);
                ToolBridge.Logger($"[PT {type}] {line}");
            };
        }
        var outW = new LineWriter(Mirror('O'));
        var errW = new LineWriter(Mirror('E'));
        InstallConsoleRouter();

        // Excecoes engolidas dentro do ProjectTool tambem aparecem no log (so as desta execucao).
        var ptThreadId = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> firstChance = (_, e) =>
        {
            if (_inFirstChance) return;
            _inFirstChance = true;
            try
            {
                // Qualquer thread (inclusive workers do ThreadPool que rodam Tasks do ProjectTool):
                // vale se a excecao ou algum quadro da pilha pertence ao ALC desta execucao.
                var st = new System.Diagnostics.StackTrace(e.Exception, false);
                bool ours = Environment.CurrentManagedThreadId == Volatile.Read(ref ptThreadId) ||
                            AssemblyLoadContext.GetLoadContext(e.Exception.GetType().Assembly) == alc;
                for (int i = 0; !ours && i < st.FrameCount; i++)
                {
                    var a = st.GetFrame(i)?.GetMethod()?.DeclaringType?.Assembly;
                    if (a != null && AssemblyLoadContext.GetLoadContext(a) == alc) ours = true;
                }
                if (ours)
                {
                    var trace = st.ToString();
                    if (trace.Length > 1500) trace = trace[..1500] + "...";
                    ToolBridge.Logger($"[PT excecao] thread {Environment.CurrentManagedThreadId} ({Thread.CurrentThread.Name ?? "?"}): {e.Exception.GetType().FullName}: {e.Exception.Message} | {trace.Replace('\n', '|')}");
                }
            }
            catch { }
            finally { _inFirstChance = false; }
        };

        EnsureThreadPoolWorkers();

        int code = 1;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            Volatile.Write(ref ptThreadId, Environment.CurrentManagedThreadId);
            var prevOut = _outSink.Value; var prevErr = _errSink.Value;
            try
            {
                _outSink.Value = outW; _errSink.Value = errW;
                var asm = alc.LoadFromAssemblyPath(Path.Combine(libDir, "ProjectTool.dll"));
                code = RunProjectToolMain(asm, alc, argv, libDir);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                errW.WriteLine(e.InnerException.ToString());
                code = 2;
            }
            catch (Exception e) { failure = e; }
            finally
            {
                outW.Flush(); errW.Flush();
                _outSink.Value = prevOut; _errSink.Value = prevErr;
            }
        }, 16 * 1024 * 1024) { IsBackground = true, Name = "gmide-projecttool" };

        AppDomain.CurrentDomain.FirstChanceException += firstChance;
        try
        {
            worker.Start();
            // Sem limite total: projetos grandes demoram. O limite e de silencio: se o ProjectTool
            // ficar sem escrever nada por ProjectToolSilenceLimit, desistimos e avisamos a IDE em
            // vez de deixa-la esperando para sempre (a thread presa fica para tras).
            while (!worker.Join(TimeSpan.FromSeconds(10)))
            {
                long lastAny = Math.Max(Volatile.Read(ref lastTick), Math.Max(Volatile.Read(ref outW.LastWriteTick), Volatile.Read(ref errW.LastWriteTick)));
                var quiet = TimeSpan.FromMilliseconds(Environment.TickCount64 - lastAny);
                var pending = outW.Pending;
                ToolBridge.Logger($"ProjectTool ainda rodando; {quiet.TotalSeconds:F0} s sem saida; ThreadPool: {ThreadPool.ThreadCount} threads, " +
                                  $"{ThreadPool.PendingWorkItemCount} pendentes, {ThreadPool.CompletedWorkItemCount} concluidos; ultima linha: {lastLine}" +
                                  (pending.Length > 0 ? $"; linha em curso ({pending.Length} car.): {(pending.Length > 120 ? "..." + pending[^120..] : pending)}" : ""));
                if (quiet > ProjectToolSilenceLimit)
                {
                    var msg = $"[ToolBridge] ProjectTool parou de responder ({quiet.TotalSeconds:F0} s sem saida). Ultima linha: {lastLine}";
                    ToolBridge.Logger(msg);
                    try { output.Write('E', msg); } catch { }
                    return 3;
                }
            }
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= firstChance; }
        if (failure != null) throw failure;
        return code;
    }

    // Rede de seguranca: se o ProjectTool parar de escrever (ex.: um laco de espera do loader que
    // nunca termina), a IDE recebe erro em vez de esperar para sempre.
    // Generoso porque "Loading...." escreve pontos sem quebrar linha em projetos grandes.
    static readonly TimeSpan ProjectToolSilenceLimit = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Folga no ThreadPool do :build: o loader VERS0 (GMProject.LoadUpdate) ocupa um worker em
    /// espera ativa enquanto outra Task le os arquivos. Nao era a causa do travamento dos
    /// templates (o log mostrou 0 itens pendentes; a causa era a excecao corrigida por
    /// PatchAssemblyScans), mas evita depender de o pool injetar threads com o cpuset reduzido.
    /// </summary>
    static void EnsureThreadPoolWorkers()
    {
        const int Min = 16;
        ThreadPool.GetMinThreads(out int w, out int io);
        if (w < Min) ThreadPool.SetMinThreads(Min, Math.Max(io, Min));
        ThreadPool.GetMinThreads(out int w2, out _);
        ToolBridge.Logger($"ThreadPool: {Environment.ProcessorCount} CPUs visiveis, minimo de workers {w} -> {w2}");
    }
    [ThreadStatic] static bool _inFirstChance;


    /// <summary>
    /// ProjectTool.Program.Main, passo a passo, com uma diferenca: GMTool.ProgramPath (pasta de
    /// Formats/) aponta para a pasta extraida, nao para AppDomain.BaseDirectory (a do app).
    /// Se a forma do ProjectTool mudar, cai no Main original.
    /// </summary>
    static int RunProjectToolMain(Assembly asm, AssemblyLoadContext alc, string[] argv, string libDir)
    {
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var prog = asm.GetType("ProjectTool.Program", throwOnError: true)!;
        Type? gm = null;
        try { gm = alc.LoadFromAssemblyName(new AssemblyName("GMTool")).GetType("GMTool.GMTool"); } catch { }
        var nested = prog.GetNestedType("ExecProjectToolWithinProjectTool", BindingFlags.NonPublic);
        var mgrInit = nested?.GetInterfaces().FirstOrDefault()?.DeclaringType?.GetMethod("Init", S);
        var initialise = gm?.GetMethod("Initialise", S);
        var parse = gm?.GetMethod("Parse", S);
        var primary = prog.GetField("PrimaryCommands", S);
        var globals = prog.GetField("mGlobalArgs", S);
        var programPath = gm?.GetField("ProgramPath", S);
        if (gm == null || nested == null || mgrInit == null || initialise == null || parse == null ||
            primary == null || globals == null || programPath == null)
        {
            Logger("ProjectTool: forma do Main mudou; usando o Main original (Formats/ pode faltar)");
            var main = prog.GetMethod("Main", S)!;
            return AwaitInt(main.Invoke(null, new object[] { argv }));
        }

        SetStatic(gm, "Name", "ProjectTool");
        var pkg = Directory.GetFiles(libDir, "*package.json").FirstOrDefault();
        var parseVer = asm.GetType("ProjectTool.BuildProperties")?.GetMethod("ParseVersionFromPackageFile", S);
        if (pkg != null && parseVer != null && parseVer.Invoke(null, new object[] { pkg }) is string ver)
            SetStatic(gm, "Version", ver);
        mgrInit.Invoke(null, new[] { Activator.CreateInstance(nested, nonPublic: true) });
        var args = (string[])initialise.Invoke(null, new[] { primary.GetValue(null), globals.GetValue(null), argv })!;
        programPath.SetValue(null, libDir.TrimEnd('/', '\\') + Path.DirectorySeparatorChar);
        if (GetStatic(gm, "Quiet") is false)
        {
            var name = GetStatic(gm, "Name");
            var version = GetStatic(gm, "Version");
            alc.LoadFromAssemblyName(new AssemblyName("GMTool")).GetType("GMTool.Library.ConsoleWriter")
                ?.GetMethod("WriteUnderlined", S, new[] { typeof(string) })
                ?.Invoke(null, new object[] { $"{name} - Version {version}" });
        }
        return AwaitInt(parse.Invoke(null, new object[] { args }));
    }

    // GMTool mistura campos (Name, Quiet, ProgramPath) e propriedades (Version, GitSha).
    static object? GetStatic(Type t, string member)
    {
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        return t.GetField(member, S) is { } f ? f.GetValue(null) : t.GetProperty(member, S)?.GetValue(null);
    }

    static void SetStatic(Type t, string member, object? value)
    {
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        if (t.GetField(member, S) is { } f) f.SetValue(null, value);
        else t.GetProperty(member, S)?.SetValue(null, value);
    }

    static int AwaitInt(object? ret) => ret switch
    {
        Task<int> t => t.GetAwaiter().GetResult(),
        Task t => Done(t),
        int i => i,
        _ => 0,
    };

    static int Done(Task t) { t.GetAwaiter().GetResult(); return 0; }

    // ------------------------------------------------------------------ Language Server em processo

    static int RunLanguageServer(string[] argv, FrameInputStream stdin, FrameWriter output)
    {
        if (!ToolBridge.VerifyFile(ToolBridge.LspDllRel, requireSigned: true, out var err))
        {
            stdin.DrainInBackground();
            output.Write('E', "[ToolBridge] Language Server recusado: " + err);
            return 1;
        }
        var ideDir = ToolBridge.IdeDir;
        var alc = new VerifiedIdeLoadContext("gml-lsp-" + Interlocked.Increment(ref _toolCounter), ideDir);
        var stdout = new FrameOutputStream(output, 'o');
        var errW = new LineWriter(SafeLine(output, 'E'));
        InstallConsoleRouter();
        var prevOut = _outSink.Value; var prevErr = _errSink.Value;
        object? server = null;
        try
        {
            // Program.Main: Console.SetOut(TextWriter.Null) -> aqui, so para este Language Server
            // (AsyncLocal: vale tambem para as threads/tasks que ele criar).
            _outSink.Value = TextWriter.Null; _errSink.Value = errW;
            var asm = alc.LoadFromAssemblyPath(Path.Combine(ideDir, ToolBridge.LspDllRel));
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            // Main: CultureInfo invariante (padrao do processo, como o servidor original).
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

            // Main: Config = RuntimeConfig.FromCommandLine(args)
            var prog = asm.GetType("GameMakerLanguageServer.Program", throwOnError: true)!;
            var cfgType = asm.GetType("GameMakerLanguageServer.RuntimeConfig", throwOnError: true)!;
            var cfg = cfgType.GetMethod("FromCommandLine", S)!.Invoke(null, new object[] { argv })!;
            prog.GetProperty("Config", S)!.GetSetMethod(nonPublic: true)!.Invoke(null, new[] { cfg });
            var level = cfgType.GetProperty("LogLevel")!.GetValue(cfg)!;
            // ParentProcessMonitor.Initialize(): omitido. No PC ele encerra o servidor quando a IDE
            // morre; aqui o servidor termina quando o stub (filho da IDE) fecha o socket.

            // Main: EchoStream.FromConfigLevel(stdin/stdout, level).UsePipeReader()/UsePipeWriter()
            var echo = asm.GetType("GameMakerLanguageServer.Utility.EchoStream", throwOnError: true)!.GetMethod("FromConfigLevel", S)!;
            var inS = (Stream)echo.Invoke(null, new[] { stdin, level })!;
            var outS = (Stream)echo.Invoke(null, new[] { (object)stdout, level })!;
            var nb = alc.LoadFromAssemblyName(new AssemblyName("Nerdbank.Streams"));
            var reader = InvokeExtension(nb, "UsePipeReader", inS);
            var writer = InvokeExtension(nb, "UsePipeWriter", outS);

            var serverType = asm.GetType("GameMakerLanguageServer.GmlLanguageServer", throwOnError: true)!;
            server = Activator.CreateInstance(serverType, reader, writer)!;
            var wait = (Task)serverType.GetMethod("WaitForExitAsync")!.Invoke(server, null)!;
            Logger("Language Server rodando");

            // Termina quando o servidor sai (notificacao "exit") ou, depois do EOF do stdin (stub
            // morto: ProcessLanguageServer.Shutdown -> Kill), com ForcefulShutdown.
            while (!wait.Wait(500))
            {
                if (!stdin.AtEof) continue;
                if (wait.Wait(TimeSpan.FromSeconds(ToolBridge.LspExitGraceSeconds))) break;
                Logger("Language Server nao saiu depois do EOF; ForcefulShutdown");
                try { serverType.GetMethod("ForcefulShutdown")?.Invoke(server, null); } catch (Exception e) { Logger("ForcefulShutdown: " + e.Message); }
                wait.Wait(TimeSpan.FromSeconds(2));
                break;
            }
            if (wait.IsFaulted) errW.WriteLine(wait.Exception!.ToString());
            return wait.IsCompletedSuccessfully ? 0 : 1;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            errW.WriteLine(e.InnerException.ToString());
            return 1;
        }
        finally
        {
            errW.Flush();
            _outSink.Value = prevOut; _errSink.Value = prevErr;
        }
    }

    // Uma linha de log da ferramenta depois que o stub morreu nao pode derrubar a execucao.
    static Action<string> SafeLine(FrameWriter w, char type) => line => { try { w.Write(type, line); } catch { } };

    static object InvokeExtension(Assembly asm, string name, Stream stream)
    {
        var m = asm.GetTypes().Where(t => t.IsAbstract && t.IsSealed)
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public))
            .First(m => m.Name == name && m.GetParameters() is { Length: > 0 } p && p[0].ParameterType == typeof(Stream));
        var ps = m.GetParameters();
        var args = new object?[ps.Length];
        args[0] = stream;
        for (int i = 1; i < ps.Length; i++)
            args[i] = ps[i].HasDefaultValue && ps[i].DefaultValue != null ? ps[i].DefaultValue
                : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
        return m.Invoke(null, args)!;
    }

    // ------------------------------------------------------------------ streams do stub

    /// <summary>stdin da ferramenta: frames 'I' do stub (len 0 = EOF). Socket fechado = EOF.</summary>
    sealed class FrameInputStream : Stream
    {
        readonly BinaryReader _r;
        byte[] _buf = Array.Empty<byte>();
        int _pos;
        volatile bool _eof;
        readonly object _lock = new();
        public FrameInputStream(BinaryReader r) => _r = r;
        public bool AtEof => _eof;

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                while (_pos >= _buf.Length)
                {
                    if (_eof || !NextFrame()) { _eof = true; return 0; }
                }
                int n = Math.Min(count, _buf.Length - _pos);
                Buffer.BlockCopy(_buf, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }
        }

        bool NextFrame()
        {
            try
            {
                while (true)
                {
                    byte t = _r.ReadByte();
                    int len = _r.ReadInt32();
                    if (len < 0) return false;
                    var data = len == 0 ? Array.Empty<byte>() : _r.ReadBytes(len);
                    if (data.Length != len) return false;
                    if (t != (byte)'I') continue; // tipo desconhecido: ignora
                    if (len == 0) return false;   // EOF do stdin do stub
                    _buf = data; _pos = 0;
                    return true;
                }
            }
            catch (Exception) { return false; } // socket fechado (stub morto)
        }

        /// <summary>Le e descarta ate o EOF/desconexao, numa thread propria.</summary>
        public void DrainInBackground() => new Thread(() =>
        {
            var b = new byte[16384];
            try { while (Read(b, 0, b.Length) > 0) { } } catch { }
        }) { IsBackground = true, Name = "ToolBridge.stdin-drain" }.Start();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>stdout cru da ferramenta: cada Write vira um frame 'o' (ou 'e').</summary>
    sealed class FrameOutputStream : Stream
    {
        readonly FrameWriter _w; readonly char _type;
        public FrameOutputStream(FrameWriter w, char type) { _w = w; _type = type; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > 0) _w.WriteRaw(_type, buffer, offset, count);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// ALC do Language Server: assemblies da pasta da IDE (OmniSharp, Feather.Logic, Utils...), cada
    /// um conferido com gmide-tools-integrity.json antes de carregar. Framework fica com o contexto
    /// padrao (BCL do Mono); um System.* que o Mono nao tenha (ex.: System.IO.Pipelines) vem da
    /// pasta, tambem conferido.
    /// </summary>
    sealed class VerifiedIdeLoadContext : AssemblyLoadContext
    {
        readonly string _dir;
        public VerifiedIdeLoadContext(string name, string dir) : base(name, isCollectible: false) => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            var n = name.Name ?? "";
            if (IsFrameworkName(n))
            {
                try { return Default.LoadFromAssemblyName(name); } catch { }
            }
            var p = Path.Combine(_dir, n + ".dll");
            if (!File.Exists(p)) return null;
            var rel = ToolBridge.RelToIde(p) ?? n + ".dll";
            if (!ToolBridge.VerifyFile(rel, requireSigned: true, out var err))
                throw new FileLoadException("[ToolBridge] " + err, n);
            return LoadFromAssemblyPath(p);
        }

        static bool IsFrameworkName(string n) =>
            n is "mscorlib" or "netstandard" or "System" || n.StartsWith("System.") || n.StartsWith("Microsoft.Win32.") ||
            n is "Microsoft.CSharp" or "Microsoft.VisualBasic" or "Microsoft.VisualBasic.Core";
    }
}
