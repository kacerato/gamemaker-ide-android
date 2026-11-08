using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using GmIde;
using Mono.Cecil;

// Regressao: a descoberta de opcoes do compilador nao pode inspecionar os tipos do host.
string source = Path.GetFullPath(args[0]);
string work = Path.GetFullPath(args[1]);
Directory.CreateDirectory(work);
string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
var originals = new[] { "GMAssetCompiler.dll", "CoreResources.dll" }.ToDictionary(n => n, n => Hash(Path.Combine(source, n)));
string poisonPath = Path.Combine(work, "HostWithMissingDependency.dll");
using (var poison = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("HostWithMissingDependency", new Version(1, 0)), "HostWithMissingDependency", ModuleKind.Dll))
{
    var missing = new AssemblyNameReference("OSCore", new Version(2026, 0, 0, 16));
    poison.MainModule.AssemblyReferences.Add(missing);
    poison.MainModule.Types.Add(new TypeDefinition("Host", "RequiresOSCore", Mono.Cecil.TypeAttributes.Public,
        new TypeReference("Absent", "Base", poison.MainModule, missing)));
    poison.Write(poisonPath);
}
AssemblyLoadContext.Default.LoadFromAssemblyPath(poisonPath);
bool baselineFailed = false;
try { ReadOptions(source, source); }
catch (TargetInvocationException e) when (e.InnerException is ReflectionTypeLoadException missing &&
    missing.LoaderExceptions.Any(x => x?.Message.Contains("OSCore") == true))
{ baselineFailed = true; Console.WriteLine("BASELINE: reproduziu a falha OSCore ao varrer o host."); }
if (!baselineFailed) throw new Exception("A linha de base nao reproduziu a regressao esperada.");

// No Windows deste host nao ha privilegio de symlink; exercita os mesmos escritores de DLL
// usados pelo espelho Android, usando o diretorio original apenas para resolver dependencias.
string mirror = Path.Combine(work, "patched");
Directory.CreateDirectory(mirror);
typeof(IgorPatches).GetMethod("PatchConsoleSetters", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, new object[] { Path.Combine(source, "GMAssetCompiler.dll"), Path.Combine(mirror, "GMAssetCompiler.dll"), false, false });
int scans = IgorPatches.PatchCompilerResources(source, mirror);
Console.WriteLine("CoreResources: " + scans + " varreduras corrigidas pelo escritor de producao.");
foreach (string name in originals.Keys)
{
    string copy = Path.Combine(mirror, name);
    if (new FileInfo(copy).LinkTarget != null) throw new Exception(name + " deveria ser copia, nao symlink.");
    using var asm = AssemblyDefinition.ReadAssembly(copy);
    int remaining = asm.MainModule.GetTypes().Where(t => t.HasMethods).SelectMany(t => t.Methods).Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions).Count(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.AppDomain" && m.Name == "GetAssemblies");
    if (remaining != 0 || AssemblyScanPatches.Rewrite(asm.MainModule) != 0) throw new Exception(name + ": varredura global restante ou patch nao idempotente.");
    if (Hash(Path.Combine(source, name)) != originals[name]) throw new Exception("Original alterado: " + name);
    Console.WriteLine(name + ": copia corrigida; original preservado; reaplicacao sem alteracoes.");
}
var options = ReadOptions(mirror, source);
if (!options.Contains("Android")) throw new Exception("A descoberta corrigida perdeu as opcoes Android.");
Console.WriteLine("PATCHED: descoberta concluiu com " + options.Count + " plataformas, incluindo Android, apesar do host com OSCore ausente.");

static List<string> ReadOptions(string dir, string fallback)
{
    var context = new CompilerContext(dir, fallback);
    var asm = context.LoadFromAssemblyPath(Path.Combine(dir, "GMAssetCompiler.dll"));
    var type = asm.GetType("GMAssetCompiler.AllOptionsList", true)!;
    return ((IEnumerable<string>)type.GetMethod("GetAllOptions", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(Activator.CreateInstance(type), null)!).ToList();
}

sealed class CompilerContext(string dir, string fallback) : AssemblyLoadContext(isCollectible: false)
{
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "System" || name.Name == "netstandard" || name.Name!.StartsWith("System.") || name.Name.StartsWith("Microsoft."))
            try { return Default.LoadFromAssemblyName(name); } catch { }
        foreach (string root in new[] { dir, fallback })
        {
            string path = Path.Combine(root, name.Name + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
        }
        return null;
    }
}

namespace GmIde
{
    public static class ApkBridge
    {
        public static string ReadLocalKeystorePassword(bool aliasPassword) => throw new NotSupportedException("Nao executado neste teste de descoberta de tipos.");
        public static int RunGradle(string command, string args, bool failOnError, string cwd, object env,
            bool asAdmin, string timingKey, bool quiet, string logPid) => throw new NotSupportedException("Nao executado neste teste de descoberta de tipos.");
    }
}
