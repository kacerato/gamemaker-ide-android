using Android.Util;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GmIde;

// Patches de IL da camada de interface mobile (docs/mobile-ui.md). Todos no IDE.dll, compostos no
// TryPatch do IDE.dll (IdePatches.Apply).
static partial class IdePatches
{
    // GeneralSettings(): padroes de editor de codigo para tela de celular. So os PADROES mudam: quem
    // nunca mexeu ganha codigo em aba cheia; quem mudou nas Preferencias continua com a sua escolha.
    //   cloud.Text Editors.OpenObjectEventsFullScreen  false -> true  (eventos de objeto numa aba cheia)
    //   cloud.Text Editors.OpenScriptsFullScreen       false -> true
    //   cloud.Text Editors.OpenShadersFullScreen       false -> true
    //   cloud.Text Editors.FocusPan                    false -> true  (codigo encadeado: a vista vai ate ele)
    // No construtor cada uma e "ldarg.0; ldc.i4.0; call set_X": troca o ldc.i4.0 por ldc.i4.1.
    static readonly string[] MobileTrueDefaults =
    {
        "set_TextEditor_OpenObjectEventsFullScreen", "set_TextEditor_OpenScriptsFullScreen",
        "set_TextEditor_OpenShadersFullScreen", "set_TextEditor_PanOnFocus",
    };

    static bool PatchMobileCodePrefs(ModuleDefinition module)
    {
        var type = module.GetType("YoYoStudio.User.UserPrefs.GeneralSettings");
        var ctor = type?.Methods.SingleOrDefault(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0 && m.HasBody);
        if (ctor == null) { Log.Error(Tag, "Patch MobileCodePrefs: GeneralSettings() nao encontrado"); return false; }
        var ins = ctor.Body.Instructions;
        int n = 0;
        for (int i = 1; i < ins.Count; i++)
        {
            if (ins[i].OpCode != OpCodes.Call || ins[i].Operand is not MethodReference r ||
                Array.IndexOf(MobileTrueDefaults, r.Name) < 0) continue;
            if (ins[i - 1].OpCode != OpCodes.Ldc_I4_0)
            {
                Log.Error(Tag, $"Patch MobileCodePrefs: {r.Name} sem ldc.i4.0 antes");
                return false;
            }
            ins[i - 1].OpCode = OpCodes.Ldc_I4_1;
            n++;
        }
        if (n != MobileTrueDefaults.Length) { Log.Error(Tag, $"Patch MobileCodePrefs: {n} de {MobileTrueDefaults.Length} padroes"); return false; }
        return true;
    }

    // GUIBase.AddWindow(GUIBase): ponto unico por onde um gadget recebe uma janela filha (Window.AddWindow
    // -> ClientPanel.AddWindow cai aqui; o editor de imagem chama direto, pois seu EditorWindow e tipado
    // GUIBase). Dialogos de editor (StripImportDialog...) vao para a desktop.
    // No inicio: if (MobileWindows.RedirectChildWindow(this, _window)) return;
    static bool PatchDialogToDesktop(ModuleDefinition module)
    {
        var gb = module.GetType("YoYoStudio.GUI.GUIBase");
        var add = gb?.Methods.SingleOrDefault(m => m.Name == "AddWindow" && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "YoYoStudio.GUI.GUIBase" && m.HasBody && !m.IsStatic);
        if (add == null) { Log.Error(Tag, "Patch DialogToDesktop: GUIBase.AddWindow(GUIBase) nao encontrado"); return false; }
        var hook = module.ImportReference(typeof(MobileWindows).GetMethod(nameof(MobileWindows.RedirectChildWindow))!);
        var il = add.Body.GetILProcessor();
        var first = add.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
        il.InsertBefore(first, il.Create(OpCodes.Ret));
        return true;
    }

    // WindowManager.AddWindow(GUIBase, bool, bool, int): antes de cada ret,
    //     MobileWindows.OnWindowAdded(_window, _bAddToWorkspace)
    // para a politica manter a janela flutuante dentro da tela.
    static bool PatchWindowAdded(ModuleDefinition module)
    {
        var wm = module.GetType("YoYoStudio.GUI.WindowManager");
        var add = wm?.Methods.SingleOrDefault(m => m.Name == "AddWindow" && m.Parameters.Count == 4 &&
            m.Parameters[0].ParameterType.FullName == "YoYoStudio.GUI.GUIBase" && m.HasBody);
        if (add == null) { Log.Error(Tag, "Patch WindowAdded: WindowManager.AddWindow(GUIBase, bool, bool, int) nao encontrado"); return false; }
        var hook = module.ImportReference(typeof(MobileWindows).GetMethod(nameof(MobileWindows.OnWindowAdded))!);
        var il = add.Body.GetILProcessor();
        int n = 0;
        foreach (var ret in add.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList())
        {
            // O proprio ret vira ldarg.1: desvios que apontavam para ele continuam validos.
            ret.OpCode = OpCodes.Ldarg_1; ret.Operand = null;
            var a2 = il.Create(OpCodes.Ldarg_2);
            var call = il.Create(OpCodes.Call, hook);
            il.InsertAfter(ret, a2);
            il.InsertAfter(a2, call);
            il.InsertAfter(call, il.Create(OpCodes.Ret));
            n++;
        }
        if (n == 0) { Log.Error(Tag, "Patch WindowAdded: AddWindow sem ret"); return false; }
        return true;
    }
}
