using System.Security.Cryptography;
using Android.Util;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GmIde;


static partial class IdePatches
{
    const string Tag = "GmIde";

    public static void Apply(string ideDir)
    {
        TryPatch(ideDir, "CorePlugins.dll", "plugin-default-alc-v1", PatchPluginLoadContext);
        // TryPatch sempre parte do .original e o marcador guarda um so id: patches do
        // mesmo DLL precisam ir juntos num id so (senao um apaga o outro a cada boot).
        TryPatch(ideDir, "IDE.dll", "processhelper-sh-v1+unsafe-location-v1+local-android-options-v1+chainroot-fit-v2+platform-v1" +
                                    "+mobile-code-prefs-v1+dialog-desktop-v2+window-added-v1",
            m => PatchProcessHelperShell(m) && PatchUnsafeLocalAppData(m) && PatchLocalAndroidOptions(m) && PatchChainRootFit(m) &&
                 PatchIdePlatform(m) && PatchMobileCodePrefs(m) && PatchDialogToDesktop(m) && PatchWindowAdded(m));
        TryPatch(ideDir, "CoreResources.dll", "local-android-options-load-v1", PatchLocalAndroidOptionLoading);
        // PlatformPatches.cs: "Show in Explorer"/abrir arquivo e ffmpeg do editor de som.
        TryPatch(ideDir, "OSCore.dll", "file-explorer-v1", PatchFileExplorer);
        TryPatch(ideDir, "Audio.dll", "ffmpeg-path-v1", PatchAudioFfmpeg);
        TryPatch(ideDir, "CodeDiagnostics.dll", "feather-dock-v1", PatchFeatherDock);
        TryPatch(ideDir, "SettingsEditor.dll", "gameoptions-floating-v2", PatchGameOptionsFloating);
        ApplyBuildPatches(ideDir); // implementado em outro arquivo (Build/Run)
    }

    // Implementacao opcional em outro arquivo (ex.: BuildPatches.cs).
    static partial void ApplyBuildPatches(string ideDir);

    // B1 (docs/pinvoke-audit.md 4.3): PluginLoadContext cria um
    // AssemblyDependencyResolver, que no Mono android-arm64 lanca
    // PlatformNotSupportedException; com isso ~96 plugins (IDE, OSCore...) nao
    // carregam. Em CorePlugins.PluginLoading.LoadRequiredAssemblies troca
    //     newobj PluginLoadContext::.ctor(string)
    // por
    //     pop ; call AssemblyLoadContext::get_Default()
    // A variavel destino e tipada AssemblyLoadContext e o resto do metodo ja
    // trata Default (nao chama Unload). Efeito colateral: plugins nao sao
    // descarregaveis (so importa ao atualizar plugins com a IDE aberta).
    static bool PatchPluginLoadContext(ModuleDefinition module)
    {
        var type = module.GetType("CorePlugins.PluginLoading");
        var method = type?.Methods.FirstOrDefault(m => m.Name == "LoadRequiredAssemblies");
        if (method?.Body == null) { Log.Error(Tag, "Patch CorePlugins: metodo nao encontrado"); return false; }

        var ins = method.Body.Instructions;
        var getDefault = ins.Select(i => i.Operand).OfType<MethodReference>()
            .FirstOrDefault(m => m.Name == "get_Default" && m.DeclaringType.Name == "AssemblyLoadContext");
        var ctor = ins.FirstOrDefault(i => i.OpCode == OpCodes.Newobj &&
            i.Operand is MethodReference m && m.DeclaringType.Name == "PluginLoadContext");
        if (getDefault == null || ctor == null) { Log.Error(Tag, "Patch CorePlugins: IL inesperado"); return false; }

        var il = method.Body.GetILProcessor();
        // Reaproveita a mesma instrucao (mantem alvos de branch/try) virando pop.
        ctor.OpCode = OpCodes.Pop;
        ctor.Operand = null;
        il.InsertAfter(ctor, il.Create(OpCodes.Call, getDefault));
        return true;
    }

    // YoYoStudio.Utils.ProcessHelper.GetProcessCount (usado em IDE.Run para contar
    // instancias) roda "/bin/bash -c 'ps -A | grep ...'" fora do Windows. O Android
    // nao tem /bin/bash e a excecao derruba o IDE.Run. Troca so o caminho do shell
    // por /system/bin/sh (o toybox do Android tem ps e grep).
    static bool PatchProcessHelperShell(ModuleDefinition module)
    {
        var type = module.GetType("YoYoStudio.Utils.ProcessHelper");
        var method = type?.Methods.FirstOrDefault(m => m.Name == "GetProcessCount");
        var ins = method?.Body?.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "/bin/bash");
        if (ins == null) { Log.Error(Tag, "Patch ProcessHelper: ldstr \"/bin/bash\" nao encontrado"); return false; }
        ins.Operand = "/system/bin/sh";
        return true;
    }

    // Aviso "The location you have chosen is unsafe" ao criar qualquer projeto.
    // YoYoStudio.Utils.ProjectPathHelper.CheckProjectSaveLocation pergunta se o caminho
    // completo do .yyp comeca (StartsWith) com algum item de GetBadProjectPaths(): pasta
    // do exe, TEMP, Windows, ApplicationData, CommonApplicationData, LocalApplicationData,
    // InternetCache, Recent e as macros system/cache/temp. No Mono android-arm64
    // (System.Private.CoreLib 8.0, Environment.GetFolderPathCore), LocalApplicationData
    // devolve o proprio $HOME (ignora XDG_DATA_HOME), e $HOME e .../files/home, onde fica
    // GameMakerProjects. Todo projeto em $HOME vira "inseguro".
    // No Linux de desktop LocalApplicationData = $XDG_DATA_HOME; o Launcher define
    // XDG_DATA_HOME = $HOME/.local/share. So nesse item troca
    //     ldc.i4.s 28 (LocalApplicationData) ; call Environment::GetFolderPath(SpecialFolder)
    // por
    //     ldstr "XDG_DATA_HOME"              ; call Environment::GetEnvironmentVariable(string)
    // O resto da lista (system_directory, caches, temp, pasta da IDE, .config) continua
    // valendo, e o aviso de OneDrive/Dropbox tambem.
    static bool PatchUnsafeLocalAppData(ModuleDefinition module)
    {
        var type = module.GetType("YoYoStudio.Utils.ProjectPathHelper");
        var method = type?.Methods.FirstOrDefault(m => m.Name == "GetBadProjectPaths");
        if (method?.Body == null) { Log.Error(Tag, "Patch UnsafeLocation: GetBadProjectPaths nao encontrado"); return false; }

        const int LocalApplicationData = 28; // Environment.SpecialFolder.LocalApplicationData
        var ins = method.Body.Instructions;
        for (int i = 0; i + 1 < ins.Count; i++)
        {
            if (ins[i + 1].OpCode != OpCodes.Call || ins[i + 1].Operand is not MethodReference gfp ||
                gfp.Name != "GetFolderPath" || gfp.DeclaringType.FullName != "System.Environment" ||
                gfp.Parameters.Count != 1)
                continue;
            var ld = ins[i];
            int? value = ld.OpCode.Code switch
            {
                Code.Ldc_I4_S => (sbyte)ld.Operand,
                Code.Ldc_I4 => (int)ld.Operand,
                _ => null,
            };
            if (value != LocalApplicationData) continue;

            // Mesmo assembly de referencia que o GetFolderPath original (System.Runtime).
            var getEnv = new MethodReference("GetEnvironmentVariable", module.TypeSystem.String, gfp.DeclaringType)
            {
                HasThis = false,
            };
            getEnv.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

            ld.OpCode = OpCodes.Ldstr;
            ld.Operand = "XDG_DATA_HOME";
            ins[i + 1].Operand = getEnv;
            return true;
        }
        Log.Error(Tag, "Patch UnsafeLocation: GetFolderPath(LocalApplicationData) nao encontrado");
        return false;
    }

    // Janelas do workspace com tamanho de desktop (MobileWindows.cs): no fim de
    // WindowManager.AddChainRoot(GUIBase, TabItem), antes de cada ret, chama
    // MobileWindows.OnChainRootAdded(_root).
    static bool PatchChainRootFit(ModuleDefinition module)
    {
        var wm = module.GetType("YoYoStudio.GUI.WindowManager");
        var add = wm?.Methods.SingleOrDefault(m => m.Name == "AddChainRoot" && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "YoYoStudio.GUI.GUIBase" && m.HasBody);
        if (add == null) { Log.Error(Tag, "Patch ChainRootFit: WindowManager.AddChainRoot(GUIBase, TabItem) nao encontrado"); return false; }
        var hook = module.ImportReference(typeof(MobileWindows).GetMethod(nameof(MobileWindows.OnChainRootAdded))!);
        var il = add.Body.GetILProcessor();
        int n = 0;
        foreach (var ret in add.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            // O proprio ret vira ldarg.1: desvios que apontavam para ele continuam validos.
            ret.OpCode = OpCodes.Ldarg_1;
            ret.Operand = null;
            var call = il.Create(OpCodes.Call, hook);
            il.InsertAfter(ret, call);
            il.InsertAfter(call, il.Create(OpCodes.Ret));
            n++;
        }
        if (n == 0) { Log.Error(Tag, "Patch ChainRootFit: AddChainRoot sem ret"); return false; }
        return true;
    }

    static void TryPatch(string ideDir, string dllName, string patchId, Func<ModuleDefinition, bool> patch)
    {
        var path = Path.Combine(ideDir, dllName);
        var marker = path + ".gmide-patch";
        try
        {
            if (!File.Exists(path)) { Log.Warn(Tag, $"Patch {patchId}: {dllName} nao existe"); return; }
            if (File.Exists(marker) && File.ReadAllText(marker).StartsWith(patchId + " "))
            {
                Log.Info(Tag, $"Patch {patchId}: ja aplicado");
                return;
            }
            var original = path + ".original";
            if (!File.Exists(original)) File.Copy(path, original);
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(ideDir);
            using (var asm = AssemblyDefinition.ReadAssembly(original,
                       new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate }))
            {
                if (!patch(asm.MainModule)) return;
                var tmp = path + ".tmp";
                asm.Write(tmp);
                File.Move(tmp, path, true);
            }
            File.WriteAllText(marker, $"{patchId} original-sha256={sha}\n");
            Log.Info(Tag, $"Patch {patchId} aplicado em {dllName}");
        }
        catch (Exception e)
        {
            Log.Error(Tag, $"Patch {patchId} em {dllName} falhou: {e}");
        }
    }
}
