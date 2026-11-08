using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using GmIde;

string source = Path.GetFullPath(args[0]), work = Path.GetFullPath(args[1]);
Directory.CreateDirectory(work);
string originalDir = Path.GetDirectoryName(source)!;
var baseline = new ToolContext(originalDir).LoadFromAssemblyPath(source);
var android = baseline.GetType("Igor.AndroidUtils", true)!;
// PlatformSettings precisa existir para chegar ao acesso nulo da licenca no metodo original.
var env = baseline.GetType("Igor.BuildEnv", true)!;
var dictionary = baseline.GetType("Igor.YYDictionary`2", true)!.MakeGenericType(typeof(string), typeof(string));
env.GetField("PlatformSettings")!.SetValue(null, Activator.CreateInstance(dictionary));
try { android.GetMethod("GetKeystorePassword")!.Invoke(null, new object[] { "android" }); throw new Exception("Nao reproduziu a falha."); }
catch (TargetInvocationException e) when (e.InnerException is NullReferenceException)
{ Console.WriteLine("PASS: Igor original reproduziu NullReferenceException com BuildEnv.License nulo."); }

string patchedDir = Path.Combine(work, "patched");
Directory.CreateDirectory(patchedDir);
string patched = Path.Combine(patchedDir, "Igor.dll");
File.Copy(source, patched, overwrite: true);
// Um marcador antigo nao pode impedir a instalacao do patch novo.
File.WriteAllText(Path.Combine(patchedDir, ".gmide-igor-patch-4"), "old");
IgorPatches.Apply(patchedDir, Console.WriteLine);
if (!File.Exists(Path.Combine(patchedDir, ".gmide-igor-patch-7"))) throw new Exception("Patch nao aplicado.");
var after = new ToolContext(patchedDir, originalDir).LoadFromAssemblyPath(patched);
var afterEnv = after.GetType("Igor.BuildEnv", true)!;
if (afterEnv.GetField("License")!.GetValue(null) != null) throw new Exception("O teste exige licenca ausente.");
var type = after.GetType("Igor.AndroidUtils", true)!;
string Get(string method) => (string)type.GetMethod(method)!.Invoke(null, new object[] { "android" })!;
void Require(bool success, string message) { if (!success) throw new Exception(message); Console.WriteLine("PASS: " + message); }
Environment.SetEnvironmentVariable("GMIDE_LOCAL_ANDROID_EXPORT", "1");
string defaultKey = Path.Combine(work, "gmide-default.p12"), customKey = Path.Combine(work, "custom.p12");
string bff = Path.Combine(work, "build.bff"), options = Path.Combine(work, "options.json"), preferences = Path.Combine(work, "preferences.json");
ApkBridge.DefaultKey = defaultKey;
Environment.SetEnvironmentVariable("GMIDE_ANDROID_EXPORT_BUILD", bff);
Environment.SetEnvironmentVariable("GMIDE_ANDROID_EXPORT_OPTIONS", options);
File.WriteAllText(options, new JsonObject { ["signing_keystore"] = defaultKey }.ToJsonString());
File.Delete(bff); // chave padrao nao depende nem de snapshot, nem de email/licenca
Require(Get("GetKeystorePassword") == "" && Get("GetKeystoreAliasPassword") == "", "chave padrao passa nos dois getters sem licenca ou snapshot.");
File.WriteAllText(bff, new JsonObject { ["preferences"] = preferences }.ToJsonString());
File.WriteAllText(options, new JsonObject { ["signing_keystore"] = customKey }.ToJsonString());
void WriteSnapshot(string filename, string aliasPassword) => File.WriteAllText(preferences,
    new JsonObject { ["keystore"] = new JsonObject { ["filename"] = filename, ["password"] = "store$test", ["alias_password"] = aliasPassword } }.ToJsonString());
WriteSnapshot(customKey, "alias$test");
Require(Get("GetKeystorePassword") == "store\\$test" && Get("GetKeystoreAliasPassword") == "alias\\$test", "chave personalizada usa credenciais do snapshot e preserva o escape do Igor.");
WriteSnapshot(customKey, "");
Require(Get("GetKeystoreAliasPassword") == "store\\$test", "senha de alias vazia reutiliza a senha do keystore.");
WriteSnapshot(defaultKey, "");
try { Get("GetKeystorePassword"); throw new Exception("Aceitou credenciais de outra chave."); }
catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
{ Console.WriteLine("PASS: credenciais de outra chave recusadas sem fallback."); }
Require(afterEnv.GetField("License")!.GetValue(null) == null, "nenhuma licenca ou email foi fabricado.");
Require(!IgorPatches.UseLocalKeystoreCredentials("amazonfire"), "demais alvos preservam o caminho original.");
Environment.SetEnvironmentVariable("GMIDE_LOCAL_ANDROID_RUN", "1");
Require(Convert.ToInt32(type.GetMethod("GetConnectedDeviceArchitecture")!.Invoke(null, null)) == 16 &&
        (bool)type.GetMethod("IsAndroidDeviceConnected")!.Invoke(null, null)! &&
        (string)type.GetMethod("DeviceGetIpAddress")!.Invoke(null, null)! == "127.0.0.1",
        "Igor real resolve ARM64 e loopback no preview local sem chamar adb.");
Environment.SetEnvironmentVariable("GMIDE_LOCAL_ANDROID_RUN", null);

sealed class ToolContext(params string[] dirs) : AssemblyLoadContext(isCollectible: false)
{
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name!.StartsWith("System.") || name.Name is "System" or "netstandard")
            try { return Default.LoadFromAssemblyName(name); } catch { }
        foreach (string dir in dirs)
        {
            string path = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
        }
        return null;
    }
}

namespace GmIde
{
    // Substitui apenas o acesso Android ao caminho da chave; a leitura das credenciais e o IL
    // dos dois getters sao os mesmos de producao, sem executar assinatura Java no host.
    public static class ApkBridge
    {
        public static string DefaultKey = "";
        public static string ReadLocalKeystorePassword(bool aliasPassword)
        {
            string selected = JsonNode.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("GMIDE_ANDROID_EXPORT_OPTIONS")!))!["signing_keystore"]!.GetValue<string>();
            return ApkKeystoreCredentials.ReadPassword(Environment.GetEnvironmentVariable("GMIDE_ANDROID_EXPORT_BUILD")!, selected, DefaultKey, aliasPassword).Replace("$", "\\$");
        }
        public static int RunGradle(string command, string args, bool failOnError, string cwd, object env,
            bool asAdmin, string timingKey, bool quiet, string logPid) => throw new NotSupportedException("Assinatura Android fora do escopo deste teste de credenciais.");
    }
}
