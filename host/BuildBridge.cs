// Ponte para o botao Run/Build da IDE dentro do APK (docs/build-pipeline.md).
//
// Fluxo real da IDE (Build.dll, Common.WriteToBuildFile):
//   build.bff (JSON) -> new CmdProcess("${igor_path}", "-j=N [-ac=/cins] -options=\"build.bff\" -q|-v -- <Plataforma> <Comando>")
//   -> System.Diagnostics.Process.Start -> stdout/stderr linha a linha -> StudioConsole (canal AssetCompiler)
//   -> a barra de progresso termina quando aparece a linha "Igor complete.".
// O Igor (managed, net8.0) carrega o GMAssetCompiler.dll NO MESMO processo por reflexao
// (Assembly.LoadFrom + GMAssetCompiler.Program.Main/Reentry), e para GX/HTML5 lanca o
// GMWebServer (tambem managed) via Process e abre o navegador com Process.Start(url).
//
// No Android nao existe Igor nativo (bin/igor/linux/arm64 nao vem no runtime) e os .exe
// do runtime nao executam. Esta ponte:
//   1. Faz a IDE apontar ${igor_path} (e o webserver do Igor) para o executavel ELF minimo
//      do APK (lib/arm64-v8a/libgmexec.so, native/gmexec) - assim CmdProcess continua
//      usando um Process de verdade (HasExited/Kill/stdout funcionam sem patch).
//   2. O stub conecta neste servidor (socket unix abstrato) e repassa argv/cwd/env.
//   3. O servidor (no servico android:process=":build", BuildService.cs) roda Igor.dll
//      num AssemblyLoadContext proprio, com os assemblies extraidos do bundle single-file
//      Igor.exe, e devolve stdout/stderr/exit. Antes de carregar Igor, GMAssetCompiler e
//      GMWebServer, confere o SHA-256 deles contra o manifesto verificado no PC
//      (RuntimeIntegrity.cs).
//
// Sem APIs Android (exceto o log em #if ANDROID): o mesmo fonte roda no PC
// (tools/build-test/bridge-harness e tools/build-test/stub-test).

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GmIde;

public static partial class BuildBridge
{
    const string Tag = "GmIde.Build";

    /// <summary>Nome do socket unix abstrato ("\0" + nome) que o stub nativo usa por padrao.</summary>
    public const string SocketName = "io.gmide.android.exec";

    /// <summary>
    /// Endpoint do servidor: "@nome" = abstrato (Android); outro valor = caminho de arquivo
    /// (teste no PC; o stub recebe o mesmo valor em $GMEXEC_SOCKET).
    /// </summary>
    public static string SocketEndpoint = "@" + SocketName;

    /// <summary>Linha que a IDE espera para encerrar a barra de progresso (BuildProgressModule.DONE_PHRASE).</summary>
    public const string IgorDonePhrase = "Igor complete.";

    // Chaves de "platform settings" que o Igor le de <userDir>/local_settings.json
    // (Program.SetMacroWithCheckForDefault -> BuildEnv.GetPlatformSetting).
    const string KeyAssetCompiler = "machine.General Settings.Paths.Debug.PathToAssetCompiler";
    const string KeyWebServer = "machine.General Settings.Paths.Debug.PathToWebServer";

    public static string RuntimeDir { get; private set; } = "";
    /// <summary>Pasta do GMAssetCompiler usada no build (espelho com patch, ou a original).</summary>
    static string AcDir = "";
    // Debugger.VMCompiler loads the same compiler in the IDE's context to evaluate
    // expressions. Its private dependencies are not in ide/ on Android.
    public static string? AssetCompilerDependency(string name)
    {
        if (RuntimeDir.Length == 0 || name != Path.GetFileName(name)) return null;
        string directory = AcDir.Length > 0 ? AcDir : Path.GetDirectoryName(Path.Combine(RuntimeDir, RuntimeIntegrity.AssetCompilerRel))!;
        string path = Path.Combine(directory, name + ".dll");
        return File.Exists(path) ? path : null;
    }
    public static string ExecStubPath { get; private set; } = "";
    /// <summary>
    /// ${webserver_path}: a IDE exige que o arquivo se chame GMWebServer(.exe), entao e um
    /// link "GMWebServer" -> libgmexec.so (BuildIntegration). Vazio = usa o stub direto.
    /// </summary>
    public static string WebServerPath { get; set; } = "";
    /// <summary>${igor_path}: a IDE exige o nome Igor(.exe); link "Igor" -> libgmexec.so.</summary>
    public static string IgorPath { get; set; } = "";
    static string EffectiveIgorPath => string.IsNullOrEmpty(IgorPath) ? ExecStubPath : IgorPath;
    static string EffectiveWebServerPath => string.IsNullOrEmpty(WebServerPath) ? ExecStubPath : WebServerPath;
    public static string CacheDir { get; private set; } = "";
    /// <summary>Pasta com os assemblies extraidos de bin/igor/windows/x64/Igor.exe.</summary>
    public static string IgorLibDir { get; private set; } = "";

    /// <summary>
    /// Lado da IDE: devolve ${runtimeLocation} atual (o runtime ativo pode mudar nas preferencias).
    /// Definido por BuildIntegration; null = usa <see cref="RuntimeDir"/>.
    /// </summary>
    public static Func<string?>? ActiveRuntimeDir;

    /// <summary>
    /// Disparado quando o Igor imprime "Attempting to open browser - ... with URL "..."" (GX/HTML5 Run).
    /// No Android o servico :build abre o GameRunnerActivity (WebView) nessa URL.
    /// </summary>
    public static event Action<string>? OpenUrlRequested;

    /// <summary>
    /// O stub de um Igor saiu antes do fim do build (Stop da IDE -> CmdProcess.Terminate -> kill).
    /// O servico :build encerra o processo inteiro (nao da para abortar o Igor dentro dele).
    /// </summary>
    public static event Action? IgorClientAborted;

    /// <summary>Pasta "-root" do ultimo GMWebServer iniciado (runner do GX), para o WebView servir direto do disco.</summary>
    public static string? LastWebRoot { get; private set; }

    public static Action<string> Logger = s =>
    {
#if ANDROID
        global::Android.Util.Log.Info(Tag, s);
#else
        Console.Error.WriteLine("[" + Tag + "] " + s);
#endif
    };

    // ------------------------------------------------------------------ configuracao

    /// <summary>
    /// Lado da IDE: so registra os caminhos (sem extrair nada). <paramref name="execStubPath"/> =
    /// ApplicationInfo.NativeLibraryDir + "/libgmexec.so".
    /// </summary>
    public static void ConfigureClient(string runtimeDir, string execStubPath)
    {
        RuntimeDir = runtimeDir;
        ExecStubPath = execStubPath;
    }

    /// <summary>
    /// Lado do servidor: <paramref name="runtimeDir"/> = runtime-2026.0.0.23,
    /// <paramref name="execStubPath"/> = stub (vira o webserver do Igor),
    /// <paramref name="cacheDir"/> = pasta gravavel para extrair o bundle do Igor.
    /// Confere o Igor.exe contra o manifesto e extrai o bundle (uma vez por hash).
    /// </summary>
    public static void Configure(string runtimeDir, string execStubPath, string cacheDir)
    {
        ExecStubPath = execStubPath;
        CacheDir = cacheDir;
        UseRuntime(runtimeDir);
    }

    static readonly object _configLock = new();

    /// <summary>Seleciona o runtime (o build.bff traz o runtimeLocation de cada build) e prepara o Igor dele.</summary>
    static void UseRuntime(string runtimeDir)
    {
        lock (_configLock)
        {
            if (runtimeDir == RuntimeDir && IgorLibDir.Length > 0 && File.Exists(Path.Combine(IgorLibDir, "Igor.dll")))
                return;
            if (!RuntimeIntegrity.VerifyFile(runtimeDir, RuntimeIntegrity.IgorRel, null, out var err))
                throw new InvalidOperationException("Igor nao verificado: " + err);
            var igorExe = Path.Combine(runtimeDir, RuntimeIntegrity.IgorRel);
            var hash = RuntimeIntegrity.Hash(new FileInfo(igorExe));
            var libDir = Path.Combine(CacheDir, "igor-" + hash.Substring(0, 16).ToLowerInvariant());
            if (!File.Exists(Path.Combine(libDir, "Igor.dll")))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var n = ExtractSingleFileBundle(igorExe, libDir);
                Logger($"Bundle do Igor extraido: {n} arquivos em {libDir} ({sw.ElapsedMilliseconds} ms)");
            }
            IgorPatches.Apply(libDir, Logger);
            RuntimeDir = runtimeDir;
            IgorLibDir = libDir;
        }
    }

    /// <summary>
    /// Faz a IDE usar o stub como ${igor_path}. Chamar depois que o Utils.dll carregou e antes
    /// de RuntimeEntry.UpdateDefaultRuntimeToolPaths (fim da splash): como o stub existe, a IDE
    /// mantem DefaultIgorPath. Usa so membros publicos de YoYoStudio.Core.Utils.DefaultMacros.
    /// </summary>
    public static bool PointIdeAtStub()
    {
        var t = FindType("YoYoStudio.Core.Utils.DefaultMacros");
        if (t == null) { Logger("DefaultMacros nao encontrado"); return false; }
        t.GetField("DefaultIgorPath", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, EffectiveIgorPath);
        t.GetField("DefaultWebServerPath", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, EffectiveWebServerPath);
        // UpdateIgorPath("${igor_path}") recria a macro igor_path a partir de DefaultIgorPath.
        // Antes de RegisterDefaultMacros a macro ainda nao existe; o Remove/Add e inofensivo.
        try
        {
            t.GetMethod("UpdateIgorPath", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { "${igor_path}" });
            t.GetMethod("UpdateWebServerPath", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { "${webserver_path}" });
        }
        catch (TargetInvocationException e) { Logger("UpdateIgorPath: " + e.InnerException?.Message); }
        Logger("igor_path -> " + ExecStubPath);
        return true;
    }

    /// <summary>
    /// Substituto da verificacao Authenticode da IDE (YoYoStudio.Plugins.Build.WinTrust.VerifySecurity,
    /// alvo do patch em BuildPatches.cs). Aceita <paramref name="fileName"/> so se for o nosso stub
    /// (vem do APK, assinado pelo Android) E o Igor.exe do runtime ativo conferir com o SHA-256
    /// gravado no PC depois da verificacao Authenticode (RuntimeIntegrity, make-runtime-manifest.ps1),
    /// assinado por uma de <paramref name="orgs"/>. O Igor que roda (BuildBridge.UseRuntime) sai
    /// desse mesmo Igor.exe, conferido de novo no processo :build.
    /// </summary>
    public static bool VerifyBuildTools(string fileName, string[] orgs)
    {
        try
        {
            var full = Path.GetFullPath(fileName);
            bool isStub = string.Equals(full, Path.GetFullPath(ExecStubPath), StringComparison.Ordinal);
            bool isWebLink = !string.IsNullOrEmpty(WebServerPath) &&
                string.Equals(full, Path.GetFullPath(WebServerPath), StringComparison.Ordinal);
            bool isIgorLink = !string.IsNullOrEmpty(IgorPath) &&
                string.Equals(full, Path.GetFullPath(IgorPath), StringComparison.Ordinal);
            if (string.IsNullOrEmpty(ExecStubPath) || !File.Exists(ExecStubPath) || !(isStub || isWebLink || isIgorLink))
            {
                Logger($"VerifyBuildTools: {fileName} nao e o stub do APK ({ExecStubPath})");
                return false;
            }
            var rt = ActiveRuntimeDir?.Invoke();
            if (string.IsNullOrEmpty(rt)) rt = RuntimeDir;
            if (!RuntimeIntegrity.VerifyFile(rt, RuntimeIntegrity.IgorRel, orgs, out var err))
            {
                Logger("VerifyBuildTools: " + err);
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Logger("VerifyBuildTools: " + e);
            return false;
        }
    }

    // ------------------------------------------------------------------ servidor do stub

    static Thread? _server;
    static readonly ManualResetEventSlim _ready = new(true);
    static string? _configError;

    /// <summary>
    /// Servidor: configura em segundo plano (extrair o Igor leva alguns segundos e o OnCreate do
    /// servico nao pode bloquear) e ja aceita conexoes; os pedidos esperam a configuracao.
    /// </summary>
    public static void ConfigureInBackground(string runtimeDir, string execStubPath, string cacheDir)
    {
        _ready.Reset();
        new Thread(() =>
        {
            try { Configure(runtimeDir, execStubPath, cacheDir); _configError = null; }
            catch (Exception e) { _configError = e.Message; Logger("Configure: " + e); }
            finally { _ready.Set(); }
        }) { IsBackground = true, Name = "BuildBridge.configure" }.Start();
    }

    /// <summary>Inicia o servidor no socket. Um build por vez (como a IDE).</summary>
    public static void StartServer()
    {
        if (_server != null) return;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        _server = new Thread(ServerLoop) { IsBackground = true, Name = "BuildBridge" };
        _server.Start();
    }

    static EndPoint MakeEndPoint()
    {
        if (SocketEndpoint.StartsWith('@')) return new UnixDomainSocketEndPoint("\0" + SocketEndpoint.Substring(1));
        try { File.Delete(SocketEndpoint); } catch { }
        return new UnixDomainSocketEndPoint(SocketEndpoint);
    }

    static void ServerLoop()
    {
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(MakeEndPoint());
            listener.Listen(8);
            Logger("BuildBridge ouvindo em " + SocketEndpoint);
            while (true)
            {
                var client = listener.Accept();
                new Thread(() => HandleClient(client)) { IsBackground = true, Name = "BuildBridge.client" }.Start();
            }
        }
        catch (Exception e)
        {
            // Ex.: outro processo ja ouve no mesmo nome (servico duplicado).
            Logger("BuildBridge: servidor parou: " + e);
            _server = null;
        }
    }

    sealed class Client
    {
        public required FrameWriter Writer;
        public bool IsIgor;
        public volatile bool Finished;
    }

    static readonly ConcurrentDictionary<Client, byte> _clients = new();

    // Protocolo (stub -> ponte): "GMX1", int32 argc, argc x str, str cwd, int32 envc, envc x (str k, str v)
    //            (ponte -> stub): frames { byte tipo ('O' stdout | 'E' stderr | 'X' exit), int32 len, bytes }
    //            str = int32 len + UTF-8. Para 'X' o payload e int32 (exit code).
    // Se o stub morrer (CmdProcess.Terminate -> kill), o socket fecha e IgorClientAborted dispara.
    static void HandleClient(Socket client)
    {
        using var stream = new NetworkStream(client, ownsSocket: true);
        using var r = new BinaryReader(stream, Encoding.UTF8);
        var c = new Client { Writer = new FrameWriter(stream) };
        int code = -1;
        try
        {
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "GMX1") return;
            var argv = new string[r.ReadInt32()];
            for (int i = 0; i < argv.Length; i++) argv[i] = ReadStr(r);
            var cwd = ReadStr(r);
            var env = new Dictionary<string, string>();
            for (int i = 0, n = r.ReadInt32(); i < n; i++) env[ReadStr(r)] = ReadStr(r);
            bool tool = false; // ProjectTool / Language Server (ToolBridge.cs)
            TryRunTool(argv, cwd, env, r, c.Writer, ref tool, ref code);
            if (tool) return;
            c.IsIgor = !IsWebServerArgs(argv);
            if (c.IsIgor) BuildTelemetry.Begin(argv, env);
            _clients[c] = 0;
            WatchDisconnect(client, c);
            code = c.IsIgor
                ? Dispatch(argv, cwd, env, line => { c.Writer.Write('O', line); BuildTelemetry.Line(line); },
                    line => { c.Writer.Write('E', line); BuildTelemetry.Line(line); })
                : Dispatch(argv, cwd, env, line => c.Writer.Write('O', line), line => c.Writer.Write('E', line));
        }
        catch (Exception e)
        {
            Logger("BuildBridge: " + e);
            try { c.Writer.Write('E', "[BuildBridge] " + e.Message); } catch { }
            if (code == -1) code = 1;
            if (c.IsIgor) BuildTelemetry.ToolException(e);
        }
        finally
        {
            if (c.IsIgor) BuildTelemetry.End(code);
            c.Finished = true;
            _clients.TryRemove(c, out _);
            try { c.Writer.WriteExit(code); } catch { }
        }
    }

    /// <summary>Implementado em ToolBridge.cs: roda ProjectTool/Language Server e poe <paramref name="handled"/> = true.</summary>
    static partial void TryRunTool(string[] argv, string cwd, Dictionary<string, string> env, BinaryReader input,
        FrameWriter output, ref bool handled, ref int code);

    // O stub so manda o stdin ('I', descartado aqui) depois do pedido: recv() volta 0 quando ele fecha o socket.
    static void WatchDisconnect(Socket s, Client c)
    {
        new Thread(() =>
        {
            try { var b = new byte[64]; while (s.Receive(b) > 0) { } } catch { }
            if (c.Finished) return;
            Logger(c.IsIgor ? "Stub do Igor desconectou no meio do build (Stop?)" : "Stub do webserver desconectou");
            if (c.IsIgor) BuildTelemetry.Cancel();
            if (c.IsIgor) IgorClientAborted?.Invoke();
        }) { IsBackground = true, Name = "BuildBridge.watch" }.Start();
    }

    /// <summary>Encerra todos os pedidos em andamento com <paramref name="code"/> (ex.: o jogo foi fechado no WebView).</summary>
    public static void AbortAll(int code, string message)
    {
        foreach (var c in _clients.Keys)
        {
            c.Finished = true;
            try { if (message.Length > 0) c.Writer.Write('E', message); c.Writer.WriteExit(code); } catch { }
        }
    }

    // Environment.Exit do Igor/GMAssetCompiler (qualquer erro de compilacao GML) derruba o
    // processo :build. Antes disso a IDE recebe o codigo de saida certo, como no PC.
    static void OnProcessExit(object? sender, EventArgs e)
    {
        if (_clients.IsEmpty) return;
        Logger($"Processo de build encerrando (Environment.Exit {Environment.ExitCode})");
        BuildTelemetry.End(Environment.ExitCode == 0 ? 1 : Environment.ExitCode, viaExit: true);
        foreach (var c in _clients.Keys)
        {
            c.Finished = true;
            try { c.Writer.Flush(); c.Writer.WriteExit(Environment.ExitCode == 0 ? 1 : Environment.ExitCode, wait: 500); } catch { }
        }
    }

    static string ReadStr(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32()));

    sealed class FrameWriter
    {
        readonly Stream _s; readonly object _lock = new();
        public FrameWriter(Stream s) => _s = s;
        public void Write(char type, string line)
        {
            var b = Encoding.UTF8.GetBytes(line);
            lock (_lock) { _s.WriteByte((byte)type); _s.Write(BitConverter.GetBytes(b.Length)); _s.Write(b); _s.Flush(); }
        }
        /// <summary>Bytes crus ('o' stdout / 'e' stderr): o stub nao acrescenta '\n'.</summary>
        public void WriteRaw(char type, byte[] b, int off, int len)
        {
            lock (_lock) { _s.WriteByte((byte)type); _s.Write(BitConverter.GetBytes(len)); _s.Write(b, off, len); _s.Flush(); }
        }
        public void Flush() { }
        public void WriteExit(int code, int wait = Timeout.Infinite)
        {
            if (!Monitor.TryEnter(_lock, wait)) return;
            try { _s.WriteByte((byte)'X'); _s.Write(BitConverter.GetBytes(4)); _s.Write(BitConverter.GetBytes(code)); _s.Flush(); }
            finally { Monitor.Exit(_lock); }
        }
    }

    static bool IsWebServerArgs(string[] argv) => Array.IndexOf(argv, "-root") >= 0 && Array.IndexOf(argv, "-port") >= 0;

    /// <summary>
    /// Decide o papel pela forma dos argumentos: o mesmo stub serve de ${igor_path} (IDE) e de
    /// ${webserver_path} (Igor: "-root X -port N -cidr C").
    /// </summary>
    public static int Dispatch(string[] argv, string cwd, IDictionary<string, string> env, Action<string> stdout, Action<string> stderr)
    {
        if (!_ready.Wait(TimeSpan.FromMinutes(3))) { stderr("[BuildBridge] configuracao nao terminou"); return 1; }
        if (_configError != null && RuntimeDir.Length == 0) { stderr("[BuildBridge] " + _configError); return 1; }

        if (IsWebServerArgs(argv))
        {
            if (!RuntimeIntegrity.VerifyFile(RuntimeDir, RuntimeIntegrity.WebServerRel, null, out var werr))
            {
                stderr("[BuildBridge] GMWebServer recusado: " + werr);
                return 1;
            }
            int ri = Array.IndexOf(argv, "-root");
            if (ri + 1 < argv.Length) LastWebRoot = argv[ri + 1].Trim('"');
            return RunManagedTool(Path.Combine(RuntimeDir, RuntimeIntegrity.WebServerRel),
                "GMWebServer.Program", argv, null, stdout, stderr);
        }

        // O runtime, espelhos, preferencias e ambiente tambem pertencem ao build exclusivo.
        lock (_buildLock)
        {
        LastWebRoot = null; // novo build: o -root do webserver anterior nao vale mais
        var bff = ReadBuildFile(argv);
        var rt = bff?["runtimeLocation"]?.GetValue<string>();
        try
        {
            if (!string.IsNullOrEmpty(rt) && Directory.Exists(rt)) UseRuntime(rt);
            else if (IgorLibDir.Length == 0) UseRuntime(RuntimeDir);
        }
        catch (Exception e)
        {
            stderr("[BuildBridge] " + e.Message);
            return 1;
        }
        if (!RuntimeIntegrity.VerifyFile(RuntimeDir, RuntimeIntegrity.AssetCompilerRel, null, out var aerr))
        {
            stderr("[BuildBridge] GMAssetCompiler recusado: " + aerr);
            return 1;
        }
        // Espelho do GMAssetCompiler com o patch do Console (IgorPatches); o Igor o carrega por
        // Assembly.LoadFrom(PathToAssetCompiler), entao a configuracao aponta para o espelho.
        var acDll = Path.Combine(RuntimeDir, RuntimeIntegrity.AssetCompilerRel);
        AcDir = IgorPatches.MirrorAssetCompiler(Path.GetDirectoryName(acDll)!, CacheDir,
            RuntimeIntegrity.Hash(new FileInfo(acDll)), Logger);
        IgorPatches.AcMirrorDir = AcDir;
        PrepareIgorUserSettings(bff);
        ApplyEnvironment(env);
        bool localRun = OperatingSystem.IsAndroid() && Array.IndexOf(argv, "Android") is int androidIndex &&
            androidIndex >= 0 && androidIndex + 1 < argv.Length && (argv[androidIndex + 1] is "Run" or "Stop");
        Environment.SetEnvironmentVariable("GMIDE_LOCAL_ANDROID_RUN", localRun ? "1" : null);
        Environment.SetEnvironmentVariable("GMIDE_ANDROID_DEBUG", localRun &&
            string.Equals(bff?["debug"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase) ? "1" : null);
        Environment.SetEnvironmentVariable("GMIDE_ANDROID_RUN_KEY", localRun
            ? bff?["projectPath"]?.GetValue<string>() + "|" + bff?["config"]?.GetValue<string>() : null);
        if (localRun) Environment.SetEnvironmentVariable("GMIDE_LOCAL_ANDROID_EXPORT", "1");
        var hint = TargetHint(argv);
        if (hint != null) stdout(hint);
        return RunIgor(argv, cwd, stdout, stderr);
        }
    }

    /// <summary>
    /// "-- Windows Run" no aparelho: o Igor compila o data.win e depois tenta Process.Start do
    /// Runner.exe (Windows), que nao executa no Android; o Igor imprime a excecao e sai com 1
    /// ("FAILED: Run Program Complete"). O comportamento fica igual; so avisamos antes, no Output.
    /// </summary>
    static string? TargetHint(string[] argv)
    {
        for (int i = 0; i + 2 < argv.Length; i++)
        {
            if (argv[i] == "--" && argv[i + 1].Equals("Windows", StringComparison.OrdinalIgnoreCase) &&
                argv[i + 2].Equals("Run", StringComparison.OrdinalIgnoreCase))
                return "[GmIde] Este comando executa Windows Run: ele gera data.win, mas nao abre o Runner.exe no Android.";
        }
        return null;
    }


    // A IDE define GMAssetLogLoc (Environment.SetEnvironmentVariable) e o Launcher define
    // HOME/TMPDIR/XDG_*; o filho (stub) herda e manda. O processo :build nao passou pelo
    // Launcher, entao copiamos so essas (o resto do ambiente Android do servico fica).
    static void ApplyEnvironment(IDictionary<string, string> env)
    {
        Environment.SetEnvironmentVariable("GMIDE_LOCAL_WEB_EXPORT",
            env.TryGetValue("GMIDE_LOCAL_WEB_EXPORT", out var webExport) ? webExport : null);
        Environment.SetEnvironmentVariable("GMIDE_LOCAL_ANDROID_EXPORT",
            env.TryGetValue("GMIDE_LOCAL_ANDROID_EXPORT", out var localExport) ? localExport : null);
        Environment.SetEnvironmentVariable("GMIDE_ANDROID_EXPORT_OPTIONS",
            env.TryGetValue("GMIDE_ANDROID_EXPORT_OPTIONS", out var exportOptions) ? exportOptions : null);
        Environment.SetEnvironmentVariable("GMIDE_ANDROID_EXPORT_BUILD",
            env.TryGetValue("GMIDE_ANDROID_EXPORT_BUILD", out var exportBuild) ? exportBuild : null);
        foreach (var (k, v) in env)
        {
            if (k.StartsWith("GM", StringComparison.Ordinal) || k is "HOME" or "TMPDIR" or "XDG_CONFIG_HOME" or "XDG_DATA_HOME" or "XDG_CACHE_HOME")
                Environment.SetEnvironmentVariable(k, v);
        }
    }

    // ------------------------------------------------------------------ Igor em processo

    static readonly object _buildLock = new();
    static int _alcCounter;

    /// <summary>
    /// Roda Igor.Program.Main(argv) num AssemblyLoadContext novo (estado estatico limpo a cada build).
    /// CurrentDirectory e global do processo, e os caminhos de erro do Igor/GMAssetCompiler chamam
    /// Environment.Exit - por isso esta chamada roda no processo Android separado (":build").
    /// </summary>
    public static int RunIgor(string[] argv, string cwd, Action<string> stdout, Action<string> stderr)
    {
        var igorDll = Path.Combine(IgorLibDir, "Igor.dll");
        var acDir = AcDir.Length > 0 ? AcDir
            : Path.GetDirectoryName(Path.Combine(RuntimeDir, RuntimeIntegrity.AssetCompilerRel))!;
        return RunManagedTool(igorDll, "Igor.Program", argv, cwd, stdout, stderr, acDir);
    }

    static int RunManagedTool(string dll, string typeName, string[] argv, string? cwd,
        Action<string> stdout, Action<string> stderr, params string[] extraProbeDirs)
    {
        // So o Igor e exclusivo: durante "OperaGX Run" o Igor espera o webserver (outro Dispatch).
        bool exclusive = cwd != null;
        if (exclusive) Monitor.Enter(_buildLock);
        var dirs = new List<string> { Path.GetDirectoryName(dll)! };
        dirs.AddRange(extraProbeDirs);
        var alc = new ToolLoadContext("tool-" + Interlocked.Increment(ref _alcCounter), dirs);
        // Cada linha tambem vai ao logcat (tag GmIde.Igor; o LogMirror copia para Download/gmide-log.txt).
        var outW = new LineWriter(line => { stdout(line); WatchLine(line); global::Android.Util.Log.Info("GmIde.Igor", line); });
        var errW = new LineWriter(line => { stderr(line); global::Android.Util.Log.Warn("GmIde.Igor", line); });
        InstallConsoleRouter();
        var prevOut = _outSink.Value; var prevErr = _errSink.Value;
        var oldCwd = Environment.CurrentDirectory;
        try
        {
            // AsyncLocal: threads/tasks criadas pela ferramenta herdam o destino da saida.
            _outSink.Value = outW; _errSink.Value = errW;
            if (exclusive && Directory.Exists(cwd) && cwd != oldCwd) Environment.CurrentDirectory = cwd!;
            var asm = alc.LoadFromAssemblyPath(dll);
            var main = asm.GetType(typeName, throwOnError: true)!
                .GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            var ret = main.Invoke(null, main.GetParameters().Length == 0 ? null : new object[] { argv });
            return ret is int i ? i : 0;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            BuildTelemetry.ToolException(e.InnerException);
            stderr(e.InnerException.ToString());
            global::Android.Util.Log.Error("GmIde.Igor", e.InnerException.ToString());
            return 1;
        }
        finally
        {
            outW.Flush(); errW.Flush();
            _outSink.Value = prevOut; _errSink.Value = prevErr;
            if (exclusive)
            {
                try { if (Environment.CurrentDirectory != oldCwd) Environment.CurrentDirectory = oldCwd; } catch { }
                Monitor.Exit(_buildLock);
            }
        }
    }

    [ThreadStatic] static bool _inSink;
    static readonly AsyncLocal<TextWriter?> _outSink = new();
    static readonly AsyncLocal<TextWriter?> _errSink = new();
    static int _routerInstalled;

    /// <summary>Troca Console.Out/Error (uma vez) por roteadores que mandam cada escrita ao destino da invocacao atual.</summary>
    static void InstallConsoleRouter()
    {
        if (Interlocked.Exchange(ref _routerInstalled, 1) == 1) return;
        Console.SetOut(new RoutingWriter(Console.Out, () => _outSink.Value));
        Console.SetError(new RoutingWriter(Console.Error, () => _errSink.Value));
    }

    sealed class RoutingWriter : TextWriter
    {
        readonly TextWriter _fallback; readonly Func<TextWriter?> _current;
        public RoutingWriter(TextWriter fallback, Func<TextWriter?> current) { _fallback = fallback; _current = current; }
        public override Encoding Encoding => Encoding.UTF8;
        // Dentro de um sink (ex.: log que usa Console) vai direto ao console original: evita recursao.
        TextWriter Target => (_inSink ? null : _current()) ?? _fallback;
        public override void Write(char value) => Target.Write(value);
        public override void Write(string? value) => Target.Write(value);
        public override void WriteLine(string? value) { var t = Target; t.Write(value); t.Write('\n'); }
        public override void Flush() => Target.Flush();
    }

    static void WatchLine(string line)
    {
        // HTML5Builder.StartWebServerAndBrowser: Console.WriteLine("Attempting to open browser - \"{0}\" with URL \"{1}\" ", ...)
        const string marker = "with URL \"";
        if (!line.StartsWith("Attempting to open browser", StringComparison.Ordinal)) return;
        int a = line.IndexOf(marker, StringComparison.Ordinal);
        if (a < 0) return;
        a += marker.Length;
        int b = line.IndexOf('"', a);
        if (b > a) OpenUrlRequested?.Invoke(line.Substring(a, b - a));
    }

    static JsonNode? ReadBuildFile(string[] argv)
    {
        var bff = argv.Select(a => a.StartsWith("-options=") ? a.Substring(9).Trim('"') : null).FirstOrDefault(a => a != null);
        if (bff == null || !File.Exists(bff)) return null;
        try { return JsonNode.Parse(File.ReadAllText(bff)); }
        catch (Exception e) { Logger("build.bff invalido: " + e.Message); return null; }
    }

    /// <summary>
    /// O Igor rodando "como Linux arm64" procuraria bin/assetcompiler/linux/arm64/GMAssetCompiler.dll
    /// e bin/webserver/linux/arm64/GMWebServer. Gravamos em &lt;userDir&gt;/local_settings.json (lido pelo
    /// Igor como platform setting) os caminhos certos: o GMAssetCompiler.dll managed de windows/x64
    /// e o stub para o webserver.
    /// </summary>
    /// <summary>
    /// Na abertura da IDE: corrige PathToWebServer nos local_settings.json de todos os usuarios.
    /// Builds antigos gravavam ali o caminho do libgmexec.so, que muda a cada reinstalacao do APK
    /// e nao se chama GMWebServer: a IDE avisava "Path to Web Server is wrong".
    /// </summary>
    public static void FixUserSettings(string webLink)
    {
        try
        {
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(cfg) || !File.Exists(webLink)) return;
            var root = Path.Combine(cfg, "GameMakerStudio2-LTS2026");
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFiles(root, "local_settings.json", SearchOption.AllDirectories))
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj) continue;
                var cur = obj[KeyWebServer]?.GetValue<string>();
                if (cur == null || cur == webLink) continue;
                obj[KeyWebServer] = webLink;
                File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                Logger($"PathToWebServer corrigido em {path}: {cur} -> {webLink}");
            }
        }
        catch (Exception e) { Logger("FixUserSettings: " + e.Message); }
    }

    static void PrepareIgorUserSettings(JsonNode? bff)
    {
        var userDir = bff?["userDir"]?.GetValue<string>();
        if (string.IsNullOrEmpty(userDir)) return;
        var path = Path.Combine(userDir, "local_settings.json");
        JsonObject obj;
        try { obj = File.Exists(path) ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new()) : new(); }
        catch { obj = new(); }
        obj[KeyAssetCompiler] = AcDir.Length > 0 ? Path.Combine(AcDir, "GMAssetCompiler.dll")
            : Path.Combine(RuntimeDir, RuntimeIntegrity.AssetCompilerRel);
        // A IDE tambem le este arquivo: tem de ser o link files/bin/GMWebServer (nome aceito pela
        // IDE e caminho fixo). O caminho do stub muda a cada reinstalacao do APK.
        var webLink = Path.Combine(global::Android.App.Application.Context.FilesDir!.AbsolutePath, "bin", "GMWebServer");
        if (File.Exists(webLink)) obj[KeyWebServer] = webLink;
        else if (!string.IsNullOrEmpty(ExecStubPath)) obj[KeyWebServer] = ExecStubPath;
        Directory.CreateDirectory(userDir);
        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    sealed class LineWriter : TextWriter
    {
        readonly Action<string> _sink; readonly StringBuilder _sb = new(); readonly object _lock = new();
        public LineWriter(Action<string> sink) => _sink = sink;
        public override Encoding Encoding => Encoding.UTF8;
        /// <summary>Hora (TickCount64) da ultima escrita, mesmo sem fim de linha ("Loading....").</summary>
        public long LastWriteTick;
        /// <summary>Linha ainda sem '\n'.</summary>
        public string Pending { get { lock (_lock) return _sb.ToString(); } }
        public override void Write(char value)
        {
            lock (_lock)
            {
                LastWriteTick = Environment.TickCount64;
                if (value == '\n') { var s = _sb.ToString().TrimEnd('\r'); _sb.Clear(); Emit(s); }
                else _sb.Append(value);
            }
        }
        public override void Write(string? value) { if (value != null) foreach (var c in value) Write(c); }
        public override void Flush() { lock (_lock) if (_sb.Length > 0) { var s = _sb.ToString(); _sb.Clear(); Emit(s); } }
        void Emit(string s) { var was = _inSink; _inSink = true; try { _sink(s); } finally { _inSink = was; } }
    }

    /// <summary>
    /// Carrega Igor/GMAssetCompiler e dependencias (MQTTnet, Renci.SshNet, Newtonsoft, SharpZipLib...)
    /// das pastas do runtime. Assemblies do framework ficam com o contexto padrao (BCL do Mono no Android).
    /// </summary>
    sealed class ToolLoadContext : AssemblyLoadContext
    {
        readonly List<string> _dirs;
        public ToolLoadContext(string name, List<string> dirs) : base(name, isCollectible: false) => _dirs = dirs;

        /// <summary>
        /// Nativos pedidos pelas DLLs do runtime (ex.: Magick.NET do GMAssetCompiler pede
        /// "Magick.Native-Q8-arm64.dll"): mesmo mapeamento da IDE (NativeResolver). Sem isto o
        /// contexto tenta os .dll Windows da pasta do compilador, o NativeMagickSettings nao
        /// inicializa e toda imagem do jogo vira um quadrado 16x16 em branco.
        /// </summary>
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var h = NativeResolver.TryMap(unmanagedDllName, null);
            return h != IntPtr.Zero ? h : base.LoadUnmanagedDll(unmanagedDllName);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            var n = name.Name ?? "";
            if (IsFramework(n))
            {
                try { return Default.LoadFromAssemblyName(name); } catch { /* ex.: System.Management: tenta o bundle */ }
            }
            foreach (var d in _dirs)
            {
                var p = Path.Combine(d, n + ".dll");
                if (File.Exists(p)) return LoadFromAssemblyPath(p);
            }
            return null;
        }

        static bool IsFramework(string n) =>
            n is "mscorlib" or "netstandard" or "System" || n.StartsWith("System.") || n.StartsWith("Microsoft.Win32.") ||
            n is "Microsoft.CSharp" or "Microsoft.VisualBasic" or "Microsoft.VisualBasic.Core";
    }

    // ------------------------------------------------------------------ utilitarios

    static Type? FindType(string fullName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(fullName, throwOnError: false);
            if (t != null) return t;
        }
        return null;
    }

    static readonly byte[] BundleSignature =
    {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
    };

    /// <summary>
    /// Extrai os arquivos de um bundle single-file do .NET (formato v6 do .NET 6+; entradas podem
    /// estar comprimidas com deflate). Igor.exe e um bundle com Igor.dll + dependencias; o Igor.dll
    /// solto ao lado dele nao traz as dependencias. Retorna o numero de arquivos extraidos.
    /// </summary>
    public static int ExtractSingleFileBundle(string bundlePath, string outDir)
    {
        var data = File.ReadAllBytes(bundlePath);
        int sig = data.AsSpan().IndexOf(BundleSignature);
        if (sig < 8) throw new InvalidDataException("Assinatura de bundle nao encontrada em " + bundlePath);
        long p = BitConverter.ToInt64(data, sig - 8);
        uint major = BitConverter.ToUInt32(data, (int)p); p += 8; // major, minor
        int count = BitConverter.ToInt32(data, (int)p); p += 4;
        ReadBundleString(data, ref p); // bundle id
        if (major >= 2) p += 8 * 4 + 8; // deps.json/runtimeconfig.json (offset,size) + flags
        var tmp = outDir + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        for (int i = 0; i < count; i++)
        {
            long off = BitConverter.ToInt64(data, (int)p); p += 8;
            long size = BitConverter.ToInt64(data, (int)p); p += 8;
            long csize = 0;
            if (major >= 6) { csize = BitConverter.ToInt64(data, (int)p); p += 8; }
            p += 1; // tipo
            var rel = ReadBundleString(data, ref p).Replace('\\', '/');
            var dest = Path.GetFullPath(Path.Combine(tmp, rel));
            if (!dest.StartsWith(Path.GetFullPath(tmp), StringComparison.Ordinal)) continue; // caminho fora da pasta
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var fs = File.Create(dest);
            if (csize == 0) fs.Write(data, (int)off, (int)size);
            else
            {
                using var ds = new System.IO.Compression.DeflateStream(new MemoryStream(data, (int)off, (int)csize),
                    System.IO.Compression.CompressionMode.Decompress);
                ds.CopyTo(fs);
            }
        }
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.Move(tmp, outDir);
        return count;
    }

    static string ReadBundleString(byte[] d, ref long p)
    {
        int len = 0, shift = 0;
        while (true) { byte b = d[p++]; len |= (b & 0x7f) << shift; shift += 7; if (b < 0x80) break; }
        var s = Encoding.UTF8.GetString(d, (int)p, len); p += len; return s;
    }
}
