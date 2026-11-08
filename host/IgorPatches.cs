using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GmIde;

// Ajustes nas ferramentas de build (Igor e GMAssetCompiler) sem tocar nos arquivos do runtime:
// o Igor.exe e o GMAssetCompiler.dll originais ja foram conferidos pelo RuntimeIntegrity.
//
// No Android varios setters de System.Console lancam PlatformNotSupportedException (nao ha
// console). Igor e GMAssetCompiler fazem "Console.OutputEncoding = UTF8" logo no Main e morrem
// antes de compilar. Cada chamada a esses setters vira "pop" (descarta o valor): mesmo efeito
// de um console que ignora a configuracao.
//  - Igor: o Igor.dll ja e uma copia extraida do bundle (cache/igor-<hash>/): patch no lugar.
//  - GMAssetCompiler: usa Assembly.Location para achar os plugins vizinhos, entao montamos um
//    espelho (cache/ac-<hash>/) com symlinks para todos os arquivos originais, exceto o
//    GMAssetCompiler.dll, que e a copia com o patch.
public static class IgorPatches
{
    const string Marker = ".gmide-assetcompiler-patch-4";
    const string IgorMarker = ".gmide-igor-patch-7";

    /// <summary>Pasta do espelho do GMAssetCompiler para o build atual (BuildBridge.Dispatch).</summary>
    public static string AcMirrorDir = "";

    /// <summary>
    /// Chamado pelo Igor patchado no lugar de Assembly.LoadFrom(asset_compiler_path): o Igor pega o
    /// caminho do macros.json da IDE (runtime original), entao trocamos pelo espelho com o patch.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static System.Reflection.Assembly LoadAssetCompiler(string path)
    {
        if (AcMirrorDir.Length > 0 &&
            Path.GetFileName(path).Equals("GMAssetCompiler.dll", StringComparison.OrdinalIgnoreCase))
        {
            var mirrored = Path.Combine(AcMirrorDir, "GMAssetCompiler.dll");
            if (File.Exists(mirrored)) path = mirrored;
        }
        // Carrega no mesmo contexto do Igor (ToolLoadContext), que resolve as dependencias do AC
        // (FileIO, CoreResources...) pela pasta dele. O LoadFrom do Mono no Android nao as acha.
        var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(System.Reflection.Assembly.GetCallingAssembly());
        return alc != null && alc != System.Runtime.Loader.AssemblyLoadContext.Default
            ? alc.LoadFromAssemblyPath(path)
            : System.Reflection.Assembly.LoadFrom(path);
    }

    static readonly HashSet<string> ConsoleSetters = new()
    {
        "set_OutputEncoding", "set_InputEncoding", "set_Title", "set_CursorVisible",
        "set_BufferWidth", "set_BufferHeight", "set_WindowWidth", "set_WindowHeight",
        "set_TreatControlCAsInput", "set_CursorSize",
    };

    /// <summary>Patch no lugar do Igor.dll extraido (guarda Igor.dll.original).</summary>
    public static void Apply(string libDir, Action<string> log)
    {
        var dll = Path.Combine(libDir, "Igor.dll");
        if (!File.Exists(dll) || File.Exists(Path.Combine(libDir, IgorMarker))) return;
        try
        {
            var orig = dll + ".original";
            if (!File.Exists(orig)) File.Copy(dll, orig);
            var tmp = dll + ".tmp";
            int n = PatchConsoleSetters(orig, tmp, redirectLoadFrom: true, android: true);
            File.Move(tmp, dll, overwrite: true);
            File.WriteAllText(Path.Combine(libDir, IgorMarker), n.ToString());
            log($"IgorPatches: Igor.dll com {n} trocas (setters do Console, LoadFrom do AssetCompiler, Gradle/adb do Android)");
        }
        catch (Exception e)
        {
            log("IgorPatches (Igor) falhou: " + e.Message);
        }
    }

    /// <summary>
    /// Devolve o espelho gravavel do compilador com os patches. Os originais do runtime
    /// permanecem intactos; uma falha na preparacao e reportada antes de executar o compilador.
    /// </summary>
    public static string MirrorAssetCompiler(string acDir, string cacheDir, string hash, Action<string> log)
    {
        var mirror = Path.Combine(cacheDir, "ac-" + hash.Substring(0, 16).ToLowerInvariant() + "-v3");
        if (File.Exists(Path.Combine(mirror, Marker))) return mirror;
        try
        {
            if (Directory.Exists(mirror)) Directory.Delete(mirror, recursive: true);
            Directory.CreateDirectory(mirror);
            foreach (var entry in Directory.EnumerateFileSystemEntries(acDir))
            {
                var name = Path.GetFileName(entry);
                if (name.Equals("GMAssetCompiler.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("CoreResources.dll", StringComparison.OrdinalIgnoreCase)) continue;
                var link = Path.Combine(mirror, name);
                if (Directory.Exists(entry)) Directory.CreateSymbolicLink(link, entry);
                else File.CreateSymbolicLink(link, entry);
            }
            int n = PatchConsoleSetters(Path.Combine(acDir, "GMAssetCompiler.dll"), Path.Combine(mirror, "GMAssetCompiler.dll"));
            int scans = PatchCompilerResources(acDir, mirror);
            File.WriteAllText(Path.Combine(mirror, Marker), n.ToString());
            log($"IgorPatches: espelho do GMAssetCompiler em {mirror} ({n} ajustes no compilador; CoreResources: {scans} varreduras isoladas no ALC)");
            return mirror;
        }
        catch (Exception e)
        {
            log("IgorPatches (GMAssetCompiler) falhou: " + e.Message);
            throw new InvalidOperationException("Nao foi possivel preparar o compilador Android: " + e.Message, e);
        }
    }

    internal static int PatchCompilerResources(string acDir, string mirror)
    {
        string resources = Path.Combine(acDir, "CoreResources.dll");
        if (!File.Exists(resources)) throw new FileNotFoundException("CoreResources do compilador ausente.", resources);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(acDir);
        using var asm = AssemblyDefinition.ReadAssembly(resources, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
        int scans = AssemblyScanPatches.Rewrite(asm.MainModule);
        asm.Write(Path.Combine(mirror, "CoreResources.dll"));
        return scans;
    }

    // Audio (GMSound.DoFFMPEG / DoFFProbe): no Linux o compilador executa "ffmpeg"/"ffprobe" do PATH,
    // que nao existe no Android; a falha vira "sucesso" (Win32Exception -> 0) e o .wav/.ogg nunca e
    // gravado. O new ProcessStartInfo(path, args) desses dois metodos passa por AudioToolStartInfo,
    // que aponta para libffmpeg.so / libffprobe.so (native/ffmpeg, executaveis na pasta de libs do app).
    static int PatchAudioTools(ModuleDefinition module)
    {
        var sound = module.GetType("GMAssetCompiler.GMSound")
            ?? throw new InvalidDataException("Formato do compilador nao reconhecido: GMSound.");
        var helper = module.ImportReference(typeof(IgorPatches).GetMethod(nameof(AudioToolStartInfo))!);
        int n = 0;
        foreach (var m in sound.Methods.Where(m => m.Name is "DoFFMPEG" or "DoFFProbe" && m.HasBody))
            foreach (var ins in m.Body.Instructions)
                if (ins.OpCode == OpCodes.Newobj && ins.Operand is MethodReference ctor &&
                    ctor.DeclaringType.FullName == "System.Diagnostics.ProcessStartInfo" && ctor.Parameters.Count == 2)
                { ins.OpCode = OpCodes.Call; ins.Operand = helper; n++; }
        if (n != 2) throw new InvalidDataException($"Formato do compilador nao reconhecido: {n} chamadas de ffmpeg/ffprobe (esperado 2).");
        return n;
    }

    /// <summary>Chamado pelo GMAssetCompiler patchado no lugar de new ProcessStartInfo(ffmpeg|ffprobe, args).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static System.Diagnostics.ProcessStartInfo AudioToolStartInfo(string fileName, string arguments)
    {
#if ANDROID
        string tool = Path.GetFileNameWithoutExtension(fileName);
        if (OperatingSystem.IsAndroid() && tool is "ffmpeg" or "ffprobe")
        {
            string dir = global::Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir ?? "";
            string exe = Path.Combine(dir, "lib" + tool + ".so");
            // Win32Exception e o que o compilador trata (e registra) quando o programa nao existe.
            if (!File.Exists(exe))
                throw new System.ComponentModel.Win32Exception(2, $"{tool} nao foi incluido neste build do app ({exe}); o audio nao sera convertido");
            fileName = exe;
        }
#endif
        return new System.Diagnostics.ProcessStartInfo(fileName, arguments);
    }

    static int PatchConsoleSetters(string src, string dst, bool redirectLoadFrom = false, bool android = false)
    {
        int n = 0;
        using var asm = AssemblyDefinition.ReadAssembly(src, new ReaderParameters { InMemory = true });
        if (asm.Name.Name == "GMAssetCompiler")
        {
            n += PatchLocalAndroidCompiler(asm.MainModule);
            n += AssemblyScanPatches.Rewrite(asm.MainModule);
            n += PatchAudioTools(asm.MainModule);
        }
        if (android) n += PatchAndroid(asm.MainModule);
        MethodReference? loader = redirectLoadFrom
            ? asm.MainModule.ImportReference(typeof(IgorPatches).GetMethod(nameof(LoadAssetCompiler)))
            : null;
        foreach (var type in asm.MainModule.GetTypes())
        foreach (var m in type.Methods)
        {
            if (!m.HasBody) continue;
            foreach (var ins in m.Body.Instructions)
            {
                if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                    ins.Operand is MethodReference mr &&
                    mr.DeclaringType.FullName == "System.Console" &&
                    ConsoleSetters.Contains(mr.Name))
                {
                    ins.OpCode = OpCodes.Pop;
                    ins.Operand = null;
                    n++;
                }
                else if (loader != null && m.Name == "InitialiseAssetCompiler" &&
                    ins.OpCode == OpCodes.Call && ins.Operand is MethodReference lf &&
                    lf.DeclaringType.FullName == "System.Reflection.Assembly" && lf.Name == "LoadFrom" &&
                    lf.Parameters.Count == 1)
                {
                    ins.Operand = loader;
                    n++;
                }
            }
        }
        asm.Write(dst);
        return n;
    }

    public static bool IsLocalAndroidExport(object? machine)
    {
        if (!OperatingSystem.IsAndroid() || Environment.GetEnvironmentVariable("GMIDE_LOCAL_ANDROID_EXPORT") != "1" ||
            machine?.GetType().FullName != "GMAssetCompiler.Machines.Android") return false;
        var program = machine.GetType().Assembly.GetType("GMAssetCompiler.Program", true)!;
        foreach (string name in new[] { "FeatureFlagsEnable", "FeatureFlagsDisable" })
        {
            var property = program.GetProperty(name)!;
            if (property.GetValue(null) == null) property.SetValue(null, new Dictionary<string, string>());
        }
        return true;
    }

    public static void CheckAndroidToolPath(string path, string error, bool isFile)
    {
        // O perfil VM local usa o runner-modelo e o empacotador gerenciado, portanto nao
        // executa ferramentas do SDK/JDK/NDK. Outros comandos continuam validando seus caminhos.
        if (OperatingSystem.IsAndroid() && Environment.GetEnvironmentVariable("GMIDE_LOCAL_ANDROID_EXPORT") == "1") return;
        if (!(isFile ? File.Exists(path) : Directory.Exists(path))) throw new DirectoryNotFoundException(error + ": " + path);
    }

    static int PatchLocalAndroidCompiler(ModuleDefinition module)
    {
        var program = module.GetType("GMAssetCompiler.Program");
        var check = program?.Methods.SingleOrDefault(m => m.Name == "CheckMakerInvokedMe" && m.Parameters.Count == 0);
        if (check == null || !check.HasBody) throw new InvalidDataException("Formato do compilador nao reconhecido: CheckMakerInvokedMe.");
        var il = check.Body.GetILProcessor();
        var original = check.Body.Instructions[0];
        var helper = module.ImportReference(typeof(IgorPatches).GetMethod(nameof(IsLocalAndroidExport))!);
        var code = new[] {
            il.Create(OpCodes.Call, program!.Methods.Single(m => m.Name == "get_MachineType")),
            il.Create(OpCodes.Call, helper), il.Create(OpCodes.Brfalse, original),
            il.Create(OpCodes.Call, program.Methods.Single(m => m.Name == "SetDefaultLimits")),
            il.Create(OpCodes.Pop), il.Create(OpCodes.Ldc_I4_1),
            il.Create(OpCodes.Call, program.Methods.Single(m => m.Name == "set_LicenseValidForBuild")),
            il.Create(OpCodes.Ldc_I4_1), il.Create(OpCodes.Ret),
        };
        foreach (var instruction in code) il.InsertBefore(original, instruction);
        return 1;
    }

    // "Gerar APK" (docs/apk-export.md): no aparelho nao ha Gradle nem adb.
    //  - AndroidUtils.CreateAPK / AndroidBuilder.CreateTargetExe: Command.ExecuteCommandSync(gradlew ...)
    //    -> GmIde.ApkBridge.RunGradle (mesmos argumentos; o YYDictionary vai como object).
    //  - StopGradle, StopADB, InstallAPKOnDevice, SetAndroidTargetDevice (adb devices/connect): nada.
    //  - IsAndroidDeviceConnected: habilitado somente no perfil de preview local.
    static readonly string[] AndroidNoOps = { "StopGradle", "StopADB", "InstallAPKOnDevice", "SetAndroidTargetDevice" };

    public static bool UseLocalKeystoreCredentials(string target) =>
        target == "android" && Environment.GetEnvironmentVariable("GMIDE_LOCAL_ANDROID_EXPORT") == "1";

    public static bool IsLocalAndroidRun() => Environment.GetEnvironmentVariable("GMIDE_LOCAL_ANDROID_RUN") == "1";

    public static void RunAndroidGame()
    {
#if ANDROID
        if (LocalGameRun.RunAndWait() != 0) throw new InvalidOperationException("O preview Android encerrou com erro. Consulte o Output.");
#else
        throw new PlatformNotSupportedException();
#endif
    }

    public static void StopAndroidGame()
    {
#if ANDROID
        LocalGameRun.Stop();
#endif
        Console.WriteLine("Igor complete.");
    }

    static int PatchAndroid(ModuleDefinition module)
    {
        int n = 0;
        var utils = module.GetType("Igor.AndroidUtils");
        var builder = module.GetType("Igor.AndroidBuilder");
        if (utils == null || builder == null) return 0;
        var gradle = module.ImportReference(typeof(ApkBridge).GetMethod(nameof(ApkBridge.RunGradle)));
        var checkPath = module.ImportReference(typeof(IgorPatches).GetMethod(nameof(CheckAndroidToolPath))!);
        var localSigning = module.ImportReference(typeof(IgorPatches).GetMethod(nameof(UseLocalKeystoreCredentials))!);
        var password = module.ImportReference(typeof(ApkBridge).GetMethod(nameof(ApkBridge.ReadLocalKeystorePassword))!);
        var localRun = module.ImportReference(typeof(IgorPatches).GetMethod(nameof(IsLocalAndroidRun))!);
        foreach (var m in utils.Methods.Concat(builder.Methods))
        {
            if (!m.HasBody) continue;
            if ((m.DeclaringType == builder && (m.Name is "AndroidRun_Command" or "Stop")) ||
                (m.DeclaringType == utils && (m.Name is "DeviceGetIpAddress" or "GetConnectedDeviceArchitecture")))
            {
                var il = m.Body.GetILProcessor(); var original = m.Body.Instructions[0];
                il.InsertBefore(original, il.Create(OpCodes.Call, localRun));
                il.InsertBefore(original, il.Create(OpCodes.Brfalse, original));
                if (m.Name == "DeviceGetIpAddress") il.InsertBefore(original, il.Create(OpCodes.Ldstr, "127.0.0.1"));
                else if (m.Name == "GetConnectedDeviceArchitecture")
                    il.InsertBefore(original, il.Create(OpCodes.Ldc_I4, Convert.ToInt32(utils.NestedTypes.Single(t => t.Name == "Architecture").Fields.Single(f => f.Name == "ARM64").Constant)));
                else il.InsertBefore(original, il.Create(OpCodes.Call, module.ImportReference(typeof(IgorPatches).GetMethod(
                    m.Name == "Stop" ? nameof(StopAndroidGame) : nameof(RunAndroidGame))!)));
                il.InsertBefore(original, il.Create(OpCodes.Ret)); n++;
                continue;
            }
            if (m.DeclaringType == utils && (m.Name is "GetKeystorePassword" or "GetKeystoreAliasPassword") &&
                m.Parameters.Count == 1 && m.ReturnType.FullName == "System.String")
            {
                // Preserva o metodo original para os demais fluxos. O perfil local nao deve
                // acessar BuildEnv.License["email"], nem mesmo quando a senha e vazia.
                var il = m.Body.GetILProcessor();
                var original = m.Body.Instructions[0];
                foreach (var instruction in new[] {
                    il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, localSigning),
                    il.Create(OpCodes.Brfalse, original),
                    il.Create(m.Name == "GetKeystoreAliasPassword" ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0),
                    il.Create(OpCodes.Call, password), il.Create(OpCodes.Ret),
                }) il.InsertBefore(original, instruction);
                n++;
                continue;
            }
            if (m.DeclaringType == utils && m.Name == "SetAndroidPlatformOptions")
                foreach (var instruction in m.Body.Instructions)
                    if (instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference called &&
                        called.DeclaringType.FullName == "Igor.Program" && called.Name == "CheckPath" && called.Parameters.Count == 3)
                    { instruction.Operand = checkPath; n++; }
            if (m.DeclaringType == utils && AndroidNoOps.Contains(m.Name) && m.ReturnType.FullName == "System.Void")
            {
                ReplaceBody(m, il => il.Emit(OpCodes.Ret));
                n++;
                continue;
            }
            if (m.DeclaringType == utils && m.Name == "IsAndroidDeviceConnected" && m.ReturnType.FullName == "System.Boolean")
            {
                ReplaceBody(m, il => { il.Emit(OpCodes.Call, localRun); il.Emit(OpCodes.Ret); });
                n++;
                continue;
            }
            if (m.Name is not ("CreateAPK" or "CreateTargetExe")) continue;
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr &&
                    mr.DeclaringType.FullName == "Igor.Command" && mr.Name == "ExecuteCommandSync" &&
                    mr.Parameters.Count == 9 && mr.ReturnType.FullName == "System.Int32")
                {
                    ins.Operand = gradle;
                    n++;
                }
            }
        }
        return n;
    }

    static void ReplaceBody(MethodDefinition m, Action<ILProcessor> emit)
    {
        var body = m.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        body.InitLocals = false;
        emit(body.GetILProcessor());
    }
}
