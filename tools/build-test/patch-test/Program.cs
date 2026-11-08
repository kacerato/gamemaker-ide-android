// Uso: patch-test <Build.dll original> <pasta de trabalho> <runtime> <manifesto> <stub>
using System.Reflection;
using Mono.Cecil;
using GmIde;

var (buildDll, work, runtime, manifest, stub) = (args[0], Path.GetFullPath(args[1]), args[2], args[3], Path.GetFullPath(args[4]));
Directory.CreateDirectory(work);
var copy = Path.Combine(work, "Build.dll");
var ideSrc = Path.GetDirectoryName(Path.GetFullPath(buildDll))!;
// Dependencias para o resolver do Cecil (no aparelho estao todas em files/ide/).
foreach (var dep in Directory.GetFiles(ideSrc, "*.dll"))
{
    var to = Path.Combine(work, Path.GetFileName(dep));
    if (!File.Exists(to)) File.Copy(dep, to);
}
foreach (var dll in new[] { "Build.dll", "IDE.dll", "CorePlugins.dll" })
{
    File.Copy(Path.Combine(ideSrc, dll), Path.Combine(work, dll), true);
    foreach (var f in new[] { ".gmide-patch", ".original" }) File.Delete(Path.Combine(work, dll + f));
}
// Cadeia real de host/IdePatches.cs (Apply -> processhelper no IDE.dll -> ApplyBuildPatches).
IdePatches.Apply(work);
var ideDll = Path.Combine(work, "IDE.dll");
var stamp = File.GetLastWriteTimeUtc(ideDll);
Console.WriteLine("marcador Build.dll: " + File.ReadAllText(copy + ".gmide-patch").Trim());
Console.WriteLine("marcador IDE.dll:   " + File.ReadAllText(ideDll + ".gmide-patch").Trim());
// Segundo boot: nada deve ser regravado.
IdePatches.Apply(work);
bool stable = File.GetLastWriteTimeUtc(ideDll) == stamp;
Console.WriteLine($"segundo Apply regravou o IDE.dll? {!stable} (esperado False)");
bool ideOk;
using (var asm = AssemblyDefinition.ReadAssembly(ideDll))
{
    var g = asm.MainModule.GetType("YoYoStudio.User.TargetManager").Properties.First(p => p.Name == "TestPlatform_Actual").GetMethod;
    var lits = g.Body.Instructions.Where(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr).Select(i => (string)i.Operand).ToList();
    Console.WriteLine("  TestPlatform_Actual literais: " + string.Join(", ", lits));
    var ph = asm.MainModule.GetType("YoYoStudio.Utils.ProcessHelper").Methods.First(m => m.Name == "GetProcessCount");
    bool sh = ph.Body.Instructions.Any(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr && (string)i.Operand == "/system/bin/sh");
    Console.WriteLine($"  ProcessHelper usa /system/bin/sh: {sh}");
    ideOk = sh && !lits.Contains("Linux") && lits.Count(l => l == "Windows") == 2 && lits.Contains("Mac");
}

// IL resultante
using (var asm = AssemblyDefinition.ReadAssembly(copy))
{
    var m = asm.MainModule.GetType("YoYoStudio.Plugins.Build.WinTrust").Methods.First(x => x.Name == "VerifySecurity");
    foreach (var i in m.Body.Instructions) Console.WriteLine("  IL: " + i);
}

// Carrega o Build.dll corrigido (sem dependencias da IDE: WinTrust so usa BCL) e chama o metodo.
RuntimeIntegrity.ManifestOverride = manifest;
BuildBridge.ConfigureClient(runtime, stub);
BuildBridge.Logger = s => Console.WriteLine("    log: " + s);
var build = Assembly.LoadFrom(copy);
var vs = build.GetType("YoYoStudio.Plugins.Build.WinTrust", true)!.GetMethod("VerifySecurity", BindingFlags.Public | BindingFlags.Static)!;
var orgs = new[] { "YoYo Games Ltd", "YOYO GAMES LIMITED" };
bool a = (bool)vs.Invoke(null, new object[] { stub, orgs })!;
bool b = (bool)vs.Invoke(null, new object[] { Path.Combine(runtime, RuntimeIntegrity.IgorRel), orgs })!;
Console.WriteLine($"WinTrust.VerifySecurity(stub) = {a} (esperado True)");
Console.WriteLine($"WinTrust.VerifySecurity(Igor.exe original) = {b} (esperado False: so o stub e aceito)");
return a && !b && ideOk && stable ? 0 : 1;

namespace Android.Util
{
    static class Log
    {
        public static void Info(string t, string m) => Console.WriteLine($"[{t}] {m}");
        public static void Warn(string t, string m) => Console.WriteLine($"[{t}] W {m}");
        public static void Error(string t, string m) => Console.WriteLine($"[{t}] E {m}");
    }
}
