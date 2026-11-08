using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Reflection;

namespace GmIde;

static partial class IdePatches
{
    static partial void ApplyBuildPatches(string ideDir)
    {
        TryPatch(ideDir, "Build.dll", "android-apk-target-v4+local-run-v3",
            m => PatchAndroidFirstTarget(m) && PatchLocalAndroidRun(m));
    }

    internal static bool PatchLocalAndroidRun(ModuleDefinition module)
    {
        var run = module.GetType("YoYoStudio.Plugins.Build.RunProgram");
        var stop = module.GetType("YoYoStudio.Plugins.Build.StopProgram");
        var common = module.GetType("YoYoStudio.Plugins.Build.Common");
        var verify = module.GetType("YoYoStudio.Plugins.Build.WinTrust")?.Methods.SingleOrDefault(m => m.Name == "VerifySecurity");
        var check = common?.Methods.SingleOrDefault(m => m.Name == "CheckPlatformSettings");
        if (verify?.Body == null || check?.Body == null || run == null || stop == null) return false;
        verify.Body.Instructions.Clear(); verify.Body.ExceptionHandlers.Clear(); verify.Body.Variables.Clear();
        var v = verify.Body.GetILProcessor();
        v.Emit(OpCodes.Ldarg_0); v.Emit(OpCodes.Ldarg_1);
        v.Emit(OpCodes.Call, module.ImportReference(typeof(BuildBridge).GetMethod(nameof(BuildBridge.VerifyBuildTools))!)); v.Emit(OpCodes.Ret);
        var local = module.ImportReference(typeof(LocalGameRun).GetMethod(nameof(LocalGameRun.IsLocalTarget))!);
        var ci = check.Body.GetILProcessor(); var original = check.Body.Instructions[0];
        foreach (var i in new[] { ci.Create(OpCodes.Ldarg_1), ci.Create(OpCodes.Brtrue, original),
            ci.Create(OpCodes.Call, local), ci.Create(OpCodes.Brfalse, original), ci.Create(OpCodes.Ldarg_0),
            ci.Create(OpCodes.Call, module.ImportReference(typeof(LocalGameRun).GetMethod(nameof(LocalGameRun.CheckSettings))!)), ci.Create(OpCodes.Ret) }) ci.InsertBefore(original, i);
        foreach (var type in new[] { run, stop })
        {
            var linux = type.Methods.SingleOrDefault(m => m.Name == (type == run ? "RunLinux" : "StopLinux"));
            var android = type.Methods.SingleOrDefault(m => m.Name == (type == run ? "RunAndroid" : "StopAndroid"));
            if (linux?.Body == null || android == null) return false;
            var il = linux.Body.GetILProcessor(); var first = linux.Body.Instructions[0];
            foreach (var i in new[] { il.Create(OpCodes.Call, local), il.Create(OpCodes.Brfalse, first),
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Call, android), il.Create(OpCodes.Ret) }) il.InsertBefore(first, i);
        }
        var stopProcesses = stop.Methods.Single(m => m.Name == "StopProcesses");
        var si = stopProcesses.Body.GetILProcessor(); var stopOriginal = stopProcesses.Body.Instructions[0];
        foreach (var i in new[] { si.Create(OpCodes.Call, local), si.Create(OpCodes.Brfalse, stopOriginal),
            si.Create(OpCodes.Call, module.ImportReference(typeof(LocalGameRun).GetMethod(nameof(LocalGameRun.StopBuild))!)), si.Create(OpCodes.Ret) }) si.InsertBefore(stopOriginal, i);
        // RunAndroid's async save callback must request Android's target mask even when the
        // toolbar still says Test. Other build commands retain their original platform.
        IEnumerable<TypeDefinition> Descendants(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Descendants));
        int changed = 0;
        foreach (var m in Descendants(run).SelectMany(t => t.Methods).Where(m => m.HasBody &&
            m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "Android Run"))))
        foreach (var i in m.Body.Instructions)
        {
            if (i.Operand is MethodReference call && call.Name == "CreateDefaultEnvironment" && i.Previous.OpCode == OpCodes.Ldnull)
            { i.Previous.OpCode = OpCodes.Ldstr; i.Previous.Operand = "android"; changed++; }
            // The callback also formats an otherwise unused target-mask string. Test
            // is an alias, not a key in Targets.TargetMask, so it must resolve here too.
            if (i.Operand is MethodReference platform && platform.Name == "get_Platform" && platform.DeclaringType.Name == "TargetManager")
            { i.OpCode = OpCodes.Ldstr; i.Operand = "android"; changed++; }
        }
        return changed == 2;
    }

    // The mobile IDE is Android-first. Keep Opera GX out of its target popup so an imprecise
    // touch cannot take the user into GX's account flow instead of selecting Android.
    static bool PatchAndroidFirstTarget(ModuleDefinition module)
    {
        var type = module.GetType("YoYoStudio.GUI.Gadgets.TargetSelectButton");
        if (type == null) return false;
        var method = type.Methods.FirstOrDefault(m => m.Name == "InitPlatforms");
        if (method?.Body == null) return false;
        var ins = method.Body.Instructions;
        var gx = ins.Where(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "operagx")).ToList();
        var android = ins.Where(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "android")).ToList();
        if (gx.Count == 0 || gx.Count != android.Count) return false;
        var setAvailable = ins.FirstOrDefault(i => i.OpCode == OpCodes.Call &&
            i.Operand is MethodReference called && called.Name == "SetAvailablePlatforms");
        var platforms = type.Fields.FirstOrDefault(f => f.Name == "availablePlatforms");
        var remove = typeof(List<string>).GetMethod(nameof(List<string>.Remove), new[] { typeof(string) });
        if (setAvailable == null || platforms == null || remove == null) return false;
        var il = method.Body.GetILProcessor();
        var anchor = setAvailable;
        foreach (var add in new[]
        {
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldfld, platforms),
            il.Create(OpCodes.Ldstr, "operagx"),
            il.Create(OpCodes.Callvirt, module.ImportReference(remove)),
            il.Create(OpCodes.Pop),
        })
        {
            il.InsertAfter(anchor, add);
            anchor = add;
        }

        foreach (var i in gx) i.Operand = "android";
        foreach (var i in android) i.Operand = "operagx";

        var update = type.Methods.FirstOrDefault(m => m.Name == "TargetSelect_UpdateTargetManager" && m.Parameters.Count == 2);
        if (update?.Body == null) return false;
        var helper = typeof(BuildTarget).GetMethod(nameof(BuildTarget.HandleAndroidTargetSelection), BindingFlags.Static | BindingFlags.Public);
        if (helper == null) return false;
        var body = update.Body.GetILProcessor();
        var first = update.Body.Instructions[0];
        body.InsertBefore(first, body.Create(OpCodes.Ldarg_0));
        body.InsertBefore(first, body.Create(OpCodes.Ldarg_1));
        body.InsertBefore(first, body.Create(OpCodes.Ldarg_2));
        body.InsertBefore(first, body.Create(OpCodes.Call, module.ImportReference(helper)));
        body.InsertBefore(first, body.Create(OpCodes.Brfalse, first));
        body.InsertBefore(first, body.Create(OpCodes.Ret));
        return true;
    }

}
