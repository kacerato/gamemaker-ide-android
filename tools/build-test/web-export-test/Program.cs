using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Runtime.Loader;
using GmIde;

// Same managed patch writers, with a test-only Android OS predicate. This is host simulation,
// not physical-device acceptance. Compiler errors can Environment.Exit: keep it in a worker.
if (args[0] == "compile")
{
    string runtimeDir = args[1], bundle = args[2], build = args[3], patched = args[4];
    Directory.CreateDirectory(patched);
    string ac = Path.Combine(runtimeDir, "bin", "assetcompiler", "windows", "x64");
    typeof(IgorPatches).GetMethod("PatchConsoleSetters", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { Path.Combine(ac, "GMAssetCompiler.dll"), Path.Combine(patched, "GMAssetCompiler.dll"), false, false });
    IgorPatches.PatchCompilerResources(ac, patched);
    string igor = Path.Combine(patched, "igor"); Directory.CreateDirectory(igor);
    File.Copy(Path.Combine(bundle, "Igor.dll"), Path.Combine(igor, "Igor.dll"));
    IgorPatches.Apply(igor, Console.WriteLine); IgorPatches.AcMirrorDir = patched;
    Environment.SetEnvironmentVariable("GMIDE_LOCAL_WEB_EXPORT", "1");
    var context = new ToolContext(new[] { igor, patched, bundle, ac });
    var assembly = context.LoadFromAssemblyPath(Path.Combine(igor, "Igor.dll"));
    var main = assembly.GetType("Igor.Program", true)!.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
    main.Invoke(null, new object[] { new[] { "-j=2", "-v", "-options=" + build, "--", "OperaGX", "Package" } });
    return;
}
string template = Path.GetFullPath(args[0]), work = Path.GetFullPath(args[1]), player = Path.GetFullPath(args[2]);
Directory.CreateDirectory(work);
string title = "Export Test", version = "1.2.3.4";
var first = WebExportPackage.Prepare(template, work, title, version);
var second = WebExportPackage.Prepare(template, work, title, version);
if (first.Root == second.Root) throw new Exception("Jobs share output/cache.");
var original = JsonNode.Parse(File.ReadAllText(template))!;
string runtime = original["runtimeLocation"]!.GetValue<string>();
var start = new ProcessStartInfo(Environment.ProcessPath!)
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (string arg in new[] { "compile", runtime, Path.GetFullPath(args[3]), first.BuildFile, Path.Combine(first.Root, "tools") }) start.ArgumentList.Add(arg);
using var process = Process.Start(start)!;
var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
if (!process.WaitForExit(180000)) { process.Kill(true); throw new TimeoutException("Compiler timed out."); }
Console.WriteLine(await stdout); Console.Error.WriteLine(await stderr);
if (process.ExitCode != 0) throw new Exception("Compiler exit " + process.ExitCode);
WebExportPackage.UnpackAndPersonalize(first, title, version, null);
if (!File.ReadAllText(Path.Combine(first.Preview, "index.html")).Contains("<title>Export Test</title>")) throw new Exception("Title disconnected.");
string page = File.ReadAllText(Path.Combine(first.Preview, "index.html"));
if (page.Contains("pauseMenu.hidden = false;") || page.Contains("} else if (isMultiplayer()) {\n          resume();") || !page.Contains("#output-container,#multiplayer-stats-container{display:none!important}"))
    throw new Exception("GX pause menu/debug console still present in the release flow.");
string webZip = Path.Combine(work, "export-web.zip");
WebExportPackage.Publish(first.Preview, webZip);
string windows = WindowsWebPackage.Create(first.Root, first.Preview, player, title, "com.gmide.exporttest", version, null, 960, 540);
string windowsZip = Path.Combine(work, "export-windows.zip");
WebExportPackage.Publish(windows, windowsZip);
using (var zip = ZipFile.OpenRead(windowsZip))
    foreach (string file in new[] { "Export Test.exe", "WebView2Loader.dll", "game.ini", "web/index.html", "web/runner.wasm", "web/game.unx" })
        if (zip.GetEntry(file) == null) throw new Exception("Windows package missing " + file);
if (!File.ReadAllText(Path.Combine(windows, "game.ini")).Contains("width=960")) throw new Exception("Window choice disconnected.");
var exeVersion = FileVersionInfo.GetVersionInfo(Path.Combine(windows, "Export Test.exe"));
if (exeVersion.ProductName != title || exeVersion.FileVersion != version) throw new Exception("EXE metadata disconnected.");
var icon = IcoImage.Read(Path.Combine(runtime, "BaseProject", "options", "windows", "icons", "icon.ico"));
if (icon == null || icon.Width < 1 || icon.Height < 1 || icon.Pixels == null && icon.Png == null) throw new Exception("Game Settings ICO unreadable.");
string authoringProject = Path.Combine(first.Root, "authoring", "test.yyp");
ProjectExportSettings.Save(authoringProject, "windows", new JsonObject { ["display_name"] = title, ["package"] = "com.gmide.saved", ["window_width"] = 960 }, icon.Png);
var restored = ProjectExportSettings.Read(authoringProject, "windows");
if (restored["package"]?.GetValue<string>() != "com.gmide.saved" || restored["window_width"]?.GetValue<int>() != 960)
    throw new Exception("Project export settings did not round-trip.");
// Verify malformed compiler output cannot replace a good package or escape its folder.
using (var zip = ZipFile.Open(second.Archive, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open())) writer.Write("invalid");
try { WebExportPackage.UnpackAndPersonalize(second, "test", version, null); throw new Exception("Traversal accepted."); }
catch (InvalidDataException) { }
Console.WriteLine("PASS: isolated jobs, real compilation, title, web ZIP, Windows package, window size, traversal rejection.");
Console.WriteLine("PLAYER=" + Path.Combine(windows, "Export Test.exe"));
Console.WriteLine("WEB=" + first.Preview);

sealed class ToolContext(string[] roots) : AssemblyLoadContext(isCollectible: false)
{
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "GmIde.Android") return typeof(IgorPatches).Assembly;
        if (name.Name == "System" || name.Name == "netstandard" || name.Name!.StartsWith("System.") || name.Name.StartsWith("Microsoft."))
            try { return Default.LoadFromAssemblyName(name); } catch { }
        foreach (string root in roots)
        {
            string path = Path.Combine(root, name.Name + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
        }
        return null;
    }
}

namespace GmIde
{
    // Only this test assembly emulates the Android admission branch. No OS emulation is
    // claimed for native libraries, audio executables, graphics or file permissions.
    static class OperatingSystem { public static bool IsAndroid() => true; }
    public static class ApkBridge
    {
        public static string ReadLocalKeystorePassword(bool aliasPassword) => throw new NotSupportedException();
        public static int RunGradle(string command, string args, bool failOnError, string cwd, object env,
            bool asAdmin, string timingKey, bool quiet, string logPid) => throw new NotSupportedException();
    }
}
