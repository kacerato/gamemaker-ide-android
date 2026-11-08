using Android.Util;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GmIde;

// Ramos "Linux" da IDE que chamam programas do sistema ou caminhos que o Android nao tem
// (docs/auditoria-port.md, secoes 2.2-2.6). Cada metodo vai para o PlatformBridge.cs.
static partial class IdePatches
{
    // IDE.dll (composto no TryPatch do IDE.dll):
    //  - YoYoStudio.IDE.OpenBrowser(string, bool): prefixo -> PlatformBridge.OpenUrl (Intent ACTION_VIEW)
    //  - YoYoStudio.IDE.SpawnNewIDE(string[]): prefixo -> PlatformBridge.SpawnNewIde (RestartActivity)
    //  - YoYoStudio.IDE.ValidateOsSpecificFontFolders: "/usr/share/fonts" -> "/system/fonts" (Font Editor)
    //  - YoYoStudio.Resources.ResourceCreation.Font: fonte nova "Arial" (nao existe no Android) -> "Roboto"
    static bool PatchIdePlatform(ModuleDefinition module)
    {
        var ide = module.GetType("YoYoStudio.IDE");
        var creation = module.GetType("YoYoStudio.Resources.ResourceCreation");
        if (ide == null || creation == null) { Log.Error(Tag, "Patch IdePlatform: tipos nao encontrados"); return false; }

        var open = ide.Methods.SingleOrDefault(m => m.Name == "OpenBrowser" && m.Parameters.Count == 2 && m.HasBody);
        var spawn = ide.Methods.SingleOrDefault(m => m.Name == "SpawnNewIDE" && m.Parameters.Count == 1 && m.HasBody);
        var fonts = ide.Methods.SingleOrDefault(m => m.Name == "ValidateOsSpecificFontFolders" && m.HasBody);
        var font = creation.Methods.SingleOrDefault(m => m.Name == "Font" && m.Parameters.Count == 0 && m.HasBody);
        if (open == null || spawn == null || fonts == null || font == null)
        {
            Log.Error(Tag, $"Patch IdePlatform: OpenBrowser={open != null} SpawnNewIDE={spawn != null} " +
                           $"ValidateOsSpecificFontFolders={fonts != null} ResourceCreation.Font={font != null}");
            return false;
        }
        PrefixReturnIfTrue(open, module.ImportReference(typeof(PlatformBridge).GetMethod(nameof(PlatformBridge.OpenUrl))!));
        PrefixReturnIfTrue(spawn, module.ImportReference(typeof(PlatformBridge).GetMethod(nameof(PlatformBridge.SpawnNewIde))!));
        return ReplaceString(fonts, "/usr/share/fonts", "/system/fonts") && ReplaceString(font, "Arial", "Roboto");
    }

    // OSCore.dll: Core.CoreOS.FileAPI.FileExplorer_Linux.OpenLocation/OpenFile(string) fazem
    // Process.Start(UseShellExecute) sem try/catch (sem xdg-open -> Win32Exception -> a IDE fecha).
    // Corpo inteiro trocado por PlatformBridge.OpenLocation/OpenFile.
    static bool PatchFileExplorer(ModuleDefinition module)
    {
        var type = module.GetType("Core.CoreOS.FileAPI.FileExplorer_Linux");
        var loc = type?.Methods.SingleOrDefault(m => m.Name == "OpenLocation" && m.Parameters.Count == 1);
        var file = type?.Methods.SingleOrDefault(m => m.Name == "OpenFile" && m.Parameters.Count == 1);
        if (loc == null || file == null) { Log.Error(Tag, "Patch FileExplorer: FileExplorer_Linux.OpenLocation/OpenFile nao encontrados"); return false; }
        ReplaceBody(loc, module.ImportReference(typeof(PlatformBridge).GetMethod(nameof(PlatformBridge.OpenLocation))!));
        ReplaceBody(file, module.ImportReference(typeof(PlatformBridge).GetMethod(nameof(PlatformBridge.OpenFile))!));
        return true;
    }

    // Audio.dll: YoYoStudio.Plugins.Audio.Cache.Init grava ffMpeg = "/usr/bin//ffmpeg" no Linux; o
    // Play e a forma de onda do editor de som executam esse caminho. Antes de cada ret:
    //     ffMpeg = PlatformBridge.FfmpegPath(ffMpeg)
    static bool PatchAudioFfmpeg(ModuleDefinition module)
    {
        var cache = module.GetType("YoYoStudio.Plugins.Audio.Cache");
        var init = cache?.Methods.SingleOrDefault(m => m.Name == "Init" && m.Parameters.Count == 0 && m.HasBody);
        var field = cache?.Fields.SingleOrDefault(f => f.Name == "ffMpeg" && f.IsStatic);
        if (init == null || field == null) { Log.Error(Tag, "Patch AudioFfmpeg: Cache.Init/ffMpeg nao encontrados"); return false; }
        var hook = module.ImportReference(typeof(PlatformBridge).GetMethod(nameof(PlatformBridge.FfmpegPath))!);
        var il = init.Body.GetILProcessor();
        foreach (var ret in init.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            ret.OpCode = OpCodes.Ldsfld; ret.Operand = field; // desvios para o ret continuam validos
            var call = il.Create(OpCodes.Call, hook);
            var st = il.Create(OpCodes.Stsfld, field);
            il.InsertAfter(ret, call);
            il.InsertAfter(call, st);
            il.InsertAfter(st, il.Create(OpCodes.Ret));
        }
        return true;
    }

    // CodeDiagnostics.dll: DiagnosticsModule.CreateWindow faz
    //     dock.DockGadget(window, num, !ProjectLayout.RestoringDesktops)
    // e expande o dock de baixo ao abrir qualquer projeto. Depois do "ceq" que calcula o terceiro
    // argumento: call PlatformBridge.FeatherAutoExpand(bool) -> bool.
    static bool PatchFeatherDock(ModuleDefinition module)
    {
        var type = module.GetType("YoYoStudio.Plugins.CodeDiagnostics.DiagnosticsModule");
        var create = type?.Methods.SingleOrDefault(m => m.Name == "CreateWindow" && m.Parameters.Count == 1 && m.HasBody);
        if (create == null) { Log.Error(Tag, "Patch FeatherDock: DiagnosticsModule.CreateWindow nao encontrado"); return false; }
        var ins = create.Body.Instructions;
        int i = ins.ToList().FindIndex(x => x.OpCode == OpCodes.Call && x.Operand is MethodReference r && r.Name == "get_RestoringDesktops");
        if (i < 0 || i + 2 >= ins.Count || ins[i + 1].OpCode != OpCodes.Ldc_I4_0 || ins[i + 2].OpCode != OpCodes.Ceq)
        {
            Log.Error(Tag, "Patch FeatherDock: \"!ProjectLayout.RestoringDesktops\" nao encontrado em CreateWindow");
            return false;
        }
        var hook = module.ImportReference(typeof(PlatformBridge).GetMethod(nameof(PlatformBridge.FeatherAutoExpand))!);
        create.Body.GetILProcessor().InsertAfter(ins[i + 2], Instruction.Create(OpCodes.Call, hook));
        return true;
    }

    // SettingsEditor.dll: GameOptionsDialog.OpenWindow faz
    //     m_ide.WindowManager.AddChainRoot(gameOptions.settings_tree.m_window, tabItem)
    // (pilha: wm, window, tab). Troca o callvirt por MobileWindows.AddGameOptionsFloating(object,
    // object, object), que consome os mesmos tres valores e abre a janela flutuante.
    static bool PatchGameOptionsFloating(ModuleDefinition module)
    {
        var type = module.GetType("YoYoStudio.Plugins.SettingsEditor.GameOptionsDialog");
        var open = type?.Methods.SingleOrDefault(m => m.Name == "OpenWindow" && m.HasBody);
        if (open == null) { Log.Error(Tag, "Patch GameOptionsFloating: GameOptionsDialog.OpenWindow nao encontrado"); return false; }
        var hook = module.ImportReference(typeof(MobileWindows).GetMethod(nameof(MobileWindows.AddGameOptionsFloating))!);
        int n = 0;
        foreach (var ins in open.Body.Instructions)
            if ((ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call) && ins.Operand is MethodReference r &&
                r.Name == "AddChainRoot" && r.Parameters.Count == 2 &&
                r.DeclaringType.Name is "WindowManager" or "IWindowInterface")
            { ins.OpCode = OpCodes.Call; ins.Operand = hook; n++; }
        if (n != 1) { Log.Error(Tag, $"Patch GameOptionsFloating: {n} chamadas de AddChainRoot (esperado 1)"); return false; }
        return true;
    }

    // ---- utilitarios

    /// <summary>No inicio: if (hook(args...)) return; (metodo void; hook recebe os mesmos argumentos e devolve bool).</summary>
    static void PrefixReturnIfTrue(MethodDefinition m, MethodReference hook)
    {
        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions[0];
        int args = hook.Parameters.Count;
        for (int i = 0; i < args; i++) il.InsertBefore(first, il.Create(OpCodes.Ldarg, m.Parameters[i]));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
    }

    /// <summary>Corpo vira: hook(arg0); ret.</summary>
    static void ReplaceBody(MethodDefinition m, MethodReference hook)
    {
        var body = m.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, hook));
        il.Append(il.Create(OpCodes.Ret));
    }

    static bool ReplaceString(MethodDefinition m, string from, string to)
    {
        int n = 0;
        foreach (var ins in m.Body.Instructions)
            if (ins.OpCode == OpCodes.Ldstr && Equals(ins.Operand, from)) { ins.Operand = to; n++; }
        if (n == 0) Log.Error(Tag, $"Patch: \"{from}\" nao encontrado em {m.DeclaringType.Name}.{m.Name}");
        return n > 0;
    }
}
