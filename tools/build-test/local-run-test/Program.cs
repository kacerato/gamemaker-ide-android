using Mono.Cecil;
using Mono.Cecil.Cil;
using GmIde;

string source = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(Path.GetDirectoryName(source)!);
using (var module = ModuleDefinition.ReadModule(source, new ReaderParameters { AssemblyResolver = resolver }))
{
    Check(IdePatches.PatchLocalAndroidRun(module), "o patch reconhece o Build.dll real");
    module.Write(output);
}
using (var module = ModuleDefinition.ReadModule(output))
{
    bool Calls(MethodDefinition m, string type, string name) => m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == type && r.Name == name);
    var common = module.GetType("YoYoStudio.Plugins.Build.Common");
    var verify = module.GetType("YoYoStudio.Plugins.Build.WinTrust").Methods.Single(m => m.Name == "VerifySecurity");
    Check(verify.Body.Instructions.Count == 4 && Calls(verify, "GmIde.BuildBridge", "VerifyBuildTools"), "VerifySecurity usa o verificador real do runtime/stub");
    foreach (string type in new[] { "RunProgram", "StopProgram" })
    {
        var t = module.GetType("YoYoStudio.Plugins.Build." + type);
        var method = t.Methods.Single(m => m.Name == (type == "RunProgram" ? "RunLinux" : "StopLinux"));
        Check(Calls(method, "GmIde.LocalGameRun", "IsLocalTarget") && Calls(method, t.FullName, type == "RunProgram" ? "RunAndroid" : "StopAndroid"), type + " adapta o alvo Test/Linux ao Android local");
    }
    var check = common.Methods.Single(m => m.Name == "CheckPlatformSettings");
    Check(Calls(module.GetType("YoYoStudio.Plugins.Build.StopProgram").Methods.Single(m => m.Name == "StopProcesses"),
        "GmIde.LocalGameRun", "StopBuild"), "Stop local encerra o job sem entrar na busca de processos de desktop");
    Check(check.Body.Instructions[0].OpCode == OpCodes.Ldarg_1 && check.Body.Instructions[1].OpCode == OpCodes.Brtrue &&
        Calls(check, "GmIde.LocalGameRun", "CheckSettings"), "validacao local so se aplica ao Run, preservando Package");
    IEnumerable<TypeDefinition> Types(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Types));
    var callback = Types(module.GetType("YoYoStudio.Plugins.Build.RunProgram")).SelectMany(t => t.Methods)
        .Single(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "Android Run")));
    Check(callback.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "CreateDefaultEnvironment" &&
        i.Previous.OpCode == OpCodes.Ldstr && Equals(i.Previous.Operand, "android")), "callback de Run cria o ambiente Android mesmo com Test na toolbar");
}
static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }

// Assinaturas de importacao apenas: este teste inspeciona IL, nao executa o runner Android.
namespace GmIde
{
 static partial class IdePatches
 {
    static partial void ApplyBuildPatches(string ideDir);
    static void TryPatch(string a, string b, string c, Func<ModuleDefinition, bool> p) => throw new NotSupportedException();
 }
 public static class LocalGameRun { public static bool IsLocalTarget() => throw new NotSupportedException(); public static bool CheckSettings(string s) => throw new NotSupportedException(); public static void Stop() => throw new NotSupportedException(); public static void StopBuild() => throw new NotSupportedException(); }
 public static class BuildBridge { public static bool VerifyBuildTools(string a, string[] b) => throw new NotSupportedException(); }
 public static class BuildTarget { public static bool HandleAndroidTargetSelection(object a, object b, int c) => throw new NotSupportedException(); }
}
