using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Android.Util;

namespace GmIde;

// Equivalente ao YoYoStudio.Program.Main do lancador original, sem as partes
// exclusivas de Windows/macOS (relaunch, minidump, TestInstall, menus Cocoa).
// Os assemblies da IDE NAO vao no APK: sao lidos da pasta "ide" no
// armazenamento do app (copiada da instalacao Windows com adb push).
public static class Launcher
{
    const string Tag = "GmIde";
    static string _ideDir = "";
    static string[] _ideArgs = Array.Empty<string>();
    static bool _resolverInstalled;
    static int _runCount;

    [UnmanagedCallersOnly]
    public static int SdlMain(int argc, IntPtr argv)
    {
        try
        {
            // argv[0] e o nome do processo; o resto vem de GmIdeActivity.getArguments().
            var args = new List<string>();
            for (int i = 1; i < argc; i++)
            {
                var a = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
                if (!string.IsNullOrEmpty(a)) args.Add(a);
            }
            _ideArgs = args.ToArray();
            if (_ideArgs.Length > 0) Log.Info(Tag, "Argumentos para a IDE: " + string.Join(" | ", _ideArgs));
            return Run();
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Falha fatal: " + e);
            try { LogRecentIdeExceptions(e); }
            catch (Exception e2) { Log.Error(Tag, "Falha ao listar excecoes recentes: " + e2.Message); }
            return 1;
        }
    }

    // Separado e sem inline: IdeSetup tem campos de tipos do OSCore. Se o SdlMain
    // citasse IdeSetup direto, o Mono carregaria essa classe ao compilar o SdlMain,
    // antes do resolver de assemblies existir, e o app morreria com TypeLoadException.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void LogRecentIdeExceptions(Exception e)
    {
        if (IdeSetup.IsCrashReporterNre(e))
            IdeSetup.LogRecentExceptions(
                "O NRE acima e do CrashReporter da IDE (TargetSite null no Mono); o erro original e um destes");
        else
            IdeSetup.LogRecentExceptions("Excecoes recentes antes da falha");
    }

    static int Run()
    {
        _runCount++;
        if (_runCount > 1)
            Log.Warn(Tag, $"SDL_main chamado de novo no mesmo processo (vez {_runCount}); estado estatico da IDE ja existe");
        var files = MainApplication.Instance.GetExternalFilesDir(null)!.AbsolutePath;
        // A IDE roda do armazenamento interno do app (/data/user/0/.../files/ide):
        // arquivos com o dono certo e sem a camada FUSE do /sdcard.
        // Caminho canonico (/data/data/...): o Mono reporta Assembly.Location assim, e o
        // CorePlugins compara Location com GetFullPath para nao carregar duas vezes.
        _ideDir = Path.Combine(new Java.IO.File(MainApplication.Instance.FilesDir!.AbsolutePath).CanonicalPath, "ide");
        Log.Info(Tag, "Pasta da IDE: " + _ideDir);

        // Log em Download/gmide-log.txt (sem adb). Precisa de "Todos os arquivos";
        // se ainda nao houver, tenta de novo depois do importador.
        LogMirror.Start();

        // Instalacao sem adb: Download/gmide-package.zip ou seletor do sistema (PackageImport.cs).
        if (!File.Exists(Path.Combine(_ideDir, "IDE.dll")))
            PackageImport.TryInstall(_ideDir);
        LogMirror.Start();

        // Fluxo com adb (deploy.sh): pasta de passagem Download/gmide-ide.
        if (!File.Exists(Path.Combine(_ideDir, "IDE.dll")) && !ImportIde())
            return 2;

        // Os manifestos de integridade podem ser atualizados pelo deploy sem reimportar a IDE.
        foreach (var manifest in new[] { IntegrityCheck.ManifestName, ToolBridge.ManifestName })
        {
            var staged = Path.Combine(StagingDir, manifest);
            try
            {
                if (File.Exists(staged))
                    File.Copy(staged, Path.Combine(_ideDir, manifest), true);
            }
            catch (Exception e) { Log.Warn(Tag, $"Manifesto {manifest}: " + e.Message); }
        }

        // Patches minimos de IL na copia da IDE (antes de carregar qualquer assembly dela).
        IdePatches.Apply(_ideDir);

        // Antes de carregar qualquer assembly da IDE: mapeia nomes de libs nativas
        // Windows/Linux para os .so do APK/sistema e loga o que nao resolver.
        NativeResolver.Install(_ideDir);

        if (!_resolverInstalled)
        {
            _resolverInstalled = true;
            AssemblyLoadContext.Default.Resolving += ResolveFromIdeDir;
        }
        Environment.CurrentDirectory = _ideDir;
        PrepareFolders(files);

        // IDE.GetPathToExecutable() usa Assembly.GetEntryAssembly().Location.
        // Marcamos o IDE.dll (carregado da pasta da IDE) como assembly principal.
        var ideAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(_ideDir, "IDE.dll"));

        SeedFnaDllMapper();

        // DLLs com patch (IdePatches): o checksum delas no .gmplugin nao bate mais. O
        // CorePlugins pula a checagem de checksum de assembly ja carregado do mesmo
        // caminho, entao pre-carregamos cada uma daqui (a assinatura do bundle continua
        // valendo, e o original foi conferido contra o manifesto do PC).
        foreach (var marker in Directory.GetFiles(_ideDir, "*.dll.gmide-patch"))
        {
            var dll = marker[..^".gmide-patch".Length];
            try
            {
                AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                Log.Info(Tag, "Pre-carregado (com patch): " + Path.GetFileName(dll));
            }
            catch (Exception e) { Log.Warn(Tag, $"Pre-carga de {Path.GetFileName(dll)}: {e.Message}"); }
        }
        MonoHacks.SetEntryAssembly(ideAsm);
        Log.Info(Tag, "Entry assembly: " + Assembly.GetEntryAssembly()?.Location);

        return BootIde();
    }

    // Separado e sem inline: so e compilado depois que o resolver ja existe.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int BootIde()
    {
        // Hints do SDL/FNA3D (GLES3, toque, Voltar, IME) antes do SDL_Init.
        IdeSetup.ApplyEarlyHints();
        Log.Info(Tag, "IDE.BootstrapSDL2 ...");
        YoYoStudio.IDE.BootstrapSDL2();
        IdeSetup.ApplyGlAttributes();
        // Toque longo/dois dedos -> clique direito, dois dedos -> roda, Voltar -> Esc.
        TouchInput.Install(MainApplication.Instance.Resources?.DisplayMetrics?.Density ?? 1f);

        Core.CoreOS.Graphics.Windowing.GetScreenMousePosition(out int mx, out int my);
        var mouse = new YoYoStudio.Core.Utils.ipoint(mx, my);
        YoYoStudio.Core.Utils.MacroExpansion.Initialise();
        // No Linux a IDE fixa TempPath = "/tmp", que nao existe no Android (Build, BugReporter).
        YoYoStudio.Core.Utils.MacroExpansion.Add("TempPath", MainApplication.Instance.CacheDir!.AbsolutePath);
        IdeSetup.LogEnvironment("depois do MacroExpansion");

        // Escala da UI (MasterDPIOverridePercentageInt + dpi_override.json) e CrashReporter.
        IdeSetup.ApplyDpi();
        IdeSetup.InstallLanguages(_ideDir);
        IdeSetup.PrepareCrashReporter();

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        Core.CoreOS.FileAPI.NativeFileAPI.OpenFileDialog = new AndroidOpenFileDialog();
        Core.CoreOS.FileAPI.NativeFileAPI.SaveFileDialog = new AndroidSaveFileDialog();

        NetFix.Apply();        // TLS/IPv6 + autoteste de rede (tag GmIde.Net)
        BuildIntegration.Configure(_ideDir);   // Build/Run: runtime, ${igor_path} -> libgmexec.so, servico :build

        YoYoStudio.IDE.Bootstrap();
        Log.Info(Tag, "IDE.Bootstrap concluido");

        // Entrada de texto que segue o foco (liga no primeiro clique/tecla apos IDE.Initialise).
        IdeSetup.InstallTextInput();
        IdeSetup.LogEnvironment("antes do Execute");

        // Assinaturas: conferidas no PC pelo deploy.sh, hashes conferidos aqui.
        IntegrityCheck.ReplaceVerifyDlls(_ideDir);

        // Registra no logcat (tag gmhost-crash) a pilha de qualquer crash nativo, depois
        // repassa ao Mono. O Mono sozinho nao deixa nada util quando a falha e nativa.
        try { gmhost_install_crash_logger(); }
        catch (Exception e) { Log.Warn(Tag, "Registrador de crash: " + e.Message); }

        SelfTestLayoutXml();

        Log.Info(Tag, "Entrando no loop principal (IDE.Execute)");
        bool ok = YoYoStudio.IDE.Execute(_ideArgs, mouse);
        Log.Info(Tag, "IDE.Execute terminou: " + ok);
        if (PlatformBridge.RestartRequested)
        {
            // A IDE pediu para reiniciar (SpawnNewIDE): o estado estatico dela nao se recria no mesmo
            // processo. io.gmide.RestartActivity (processo :restart) reabre a activity quando este morrer.
            Log.Info(Tag, "Reiniciando a IDE: encerrando o processo");
            global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
        }
        return ok ? 0 : 3;
    }

    [DllImport("gmhost")]
    static extern int gmhost_install_crash_logger();

    // Alguns plugins (ex.: CodeEditor2/OnigInterop) pedem o handle da lib ao
    // FNADllMapper da YoYo (Utils.dll) para usar NativeLibrary.GetExport. Esse mapeador
    // so conhece o que carregou pelos .dll.config (sem entradas Android), entao o
    // dicionario estatico NativeLibraries fica vazio e GetExport recebe handle nulo.
    // Preenchemos com as libs do APK, pelos nomes que o codigo da IDE usa.
    static void SeedFnaDllMapper()
    {
        var map = new Dictionary<string, string>
        {
            ["onig.dll"] = "libonig.so", ["onig"] = "libonig.so",
            ["SDL2"] = "libSDL2.so", ["SDL2.dll"] = "libSDL2.so",
            ["FNA3D"] = "libFNA3D.so", ["FNA3D.dll"] = "libFNA3D.so",
            ["freetype.dll"] = "libfreetype.so", ["yoga"] = "libyoga.so", ["yoga.dll"] = "libyoga.so",
            ["yyal.dll"] = "libyyal.so", ["tinyfiledialogs"] = "libtinyfiledialogs.so",
        };
        try
        {
            var utils = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(_ideDir, "Utils.dll"));
            var field = utils.GetType("FNADllMapper")?.GetField("NativeLibraries", BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is not System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> dict)
            {
                Log.Warn(Tag, "FNADllMapper.NativeLibraries nao encontrado");
                return;
            }
            var nativeDir = MainApplication.Instance.ApplicationInfo!.NativeLibraryDir!;
            int n = 0;
            foreach (var (name, file) in map)
                if (NativeLibrary.TryLoad(Path.Combine(nativeDir, file), out var h)) { dict[name] = h; n++; }
            Log.Info(Tag, $"FNADllMapper: {n} libs registradas");
        }
        catch (Exception e) { Log.Warn(Tag, "FNADllMapper: " + e.Message); }
    }

    // Le um layout do mesmo jeito que LayoutFileDirectory.LoadFrom (XmlDocument + XPath).
    // Se o linker tiver cortado algo do System.Private.Xml, a excecao aparece aqui no
    // log em vez de ser engolida pela IDE (que so mostra o erro depois de abrir).
    static void SelfTestLayoutXml()
    {
        var path = Path.Combine(_ideDir, "GUI", "Skins", "Dark", "Layouts", "ide_menu.xml");
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(path);
            var n = doc.DocumentElement!.SelectNodes("Layout")!.Count;
            Log.Info(Tag, $"Autoteste XML: {Path.GetFileName(path)} tem {n} layouts");
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Autoteste XML falhou: " + e);
        }
    }

    // O adb nao consegue escrever arquivos que o app leia dentro de Android/data
    // (ficam com dono "shell" e pasta 2770). Entao o deploy copia a IDE para uma
    // pasta publica de passagem e o app importa para o armazenamento interno.
    // Ler a pasta publica exige "Todos os arquivos" (deploy.sh concede via appops).
    const string StagingDir = "/storage/emulated/0/Download/gmide-ide";

    static bool ImportIde()
    {
        if (!File.Exists(Path.Combine(StagingDir, "IDE.dll")))
        {
            Log.Error(Tag, $"IDE nao encontrada em {StagingDir} (ou sem permissao 'Todos os arquivos'). " +
                           "Rode: bash deploy.sh");
            return false;
        }
        Log.Info(Tag, $"Importando a IDE de {StagingDir} para {_ideDir} ...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tmp = _ideDir + ".importando";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        long bytes = 0; int count = 0;
        foreach (var src in Directory.EnumerateFiles(StagingDir, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(tmp, Path.GetRelativePath(StagingDir, src));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, true);
            bytes += new FileInfo(dst).Length; count++;
        }
        if (Directory.Exists(_ideDir)) Directory.Delete(_ideDir, true);
        Directory.Move(tmp, _ideDir);
        Log.Info(Tag, $"Importados {count} arquivos ({bytes / 1048576} MB) em {sw.Elapsed.TotalSeconds:F1}s");
        return true;
    }

    static Assembly? ResolveFromIdeDir(AssemblyLoadContext ctx, AssemblyName name)
    {
        var path = Path.Combine(_ideDir, name.Name + ".dll");
        if (!File.Exists(path)) path = BuildBridge.AssetCompilerDependency(name.Name ?? "") ?? path;
        if (!File.Exists(path)) return null;
        // Este evento so dispara para o que o runtime Android NAO tem. Assim, pacotes
        // gerenciados como Microsoft.Extensions.*, System.Reactive e System.IO.Pipelines
        // vem de ide/ (docs/pinvoke-audit.md 3.1). WinForms/WPF tambem vem de ide/: alguns
        // plugins tem tipos que os citam, e a IDE enumera esses tipos ao registrar gadgets.
        // So o metadado e lido; nenhum codigo de WinForms/WPF roda no Android.
        // Recusamos so o nucleo da BCL, que o runtime ja tem.
        if (name.Name is "mscorlib" or "netstandard" or "System.Private.CoreLib")
            return null;
        Log.Debug(Tag, "Carregando " + path);
        return ctx.LoadFromAssemblyPath(path);
    }

    // No Linux/Android o .NET mapeia CommonApplicationData para /usr/share,
    // que nao existe aqui. Apontamos as pastas da IDE para o armazenamento do app.
    static void PrepareFolders(string files)
    {
        var home = Path.Combine(files, "home");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HOME", home);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(home, ".local", "share"));
        Environment.SetEnvironmentVariable("TMPDIR", MainApplication.Instance.CacheDir!.AbsolutePath);
        foreach (var d in new[] { ".config", ".local/share" })
            Directory.CreateDirectory(Path.Combine(home, d));
        InstallFonts(home);
    }

    // A IDE procura fontes em ${exe_path}/Fonts, $HOME/.fonts e /usr/share/fonts; as do
    // Android ficam em /system/fonts. O editor de codigo usa "Noto Sans Mono" no Linux.
    static void InstallFonts(string home)
    {
        var dst = Path.Combine(home, ".fonts");
        Directory.CreateDirectory(dst);
        foreach (var name in new[] { "NotoSansMono-Regular.ttf", "DroidSansMono.ttf", "CutiveMono.ttf",
                                     "Roboto-Regular.ttf", "NotoSans-Regular.ttf" })
        {
            var src = Path.Combine("/system/fonts", name);
            var target = Path.Combine(dst, name);
            try
            {
                if (File.Exists(src) && !File.Exists(target)) File.Copy(src, target);
            }
            catch (Exception e) { Log.Warn(Tag, $"Fonte {name}: {e.Message}"); }
        }
        Log.Info(Tag, "Fontes em $HOME/.fonts: " + string.Join(", ", Directory.GetFiles(dst).Select(Path.GetFileName)));
    }
}
