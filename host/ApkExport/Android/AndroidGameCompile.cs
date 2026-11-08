using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GmIde;

// A configuracao e capturada na thread da IDE, antes de a activity pausar o SDL.
// O compilador roda no processo :build pelo mesmo stub usado pelo Build normal.
static class AndroidGameCompile
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    internal sealed record Input(string BuildFile, string[] IgorOptions);
    static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(15);

    internal static IDisposable Reserve()
    {
        var common = Type.GetType("YoYoStudio.Plugins.Build.Common, Build", true)!;
        var state = common.GetProperty("BuildActive", Static)!;
        var mutex = common.GetField("mBuildMutex", Static)!.GetValue(null)!;
        lock (mutex)
        {
            if (state.GetValue(null)?.ToString() != "Nothing")
                throw new InvalidOperationException("Ha uma compilacao em andamento. Aguarde antes de exportar o APK.");
            state.SetValue(null, Enum.Parse(state.PropertyType, "PrePackage"));
        }
        return new Reservation(state, mutex);
    }

    sealed class Reservation(PropertyInfo state, object mutex) : IDisposable
    {
        int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            lock (mutex)
                if (state.GetValue(null)?.ToString() == "PrePackage")
                    state.SetValue(null, Enum.Parse(state.PropertyType, "Nothing"));
        }
    }

    internal static Input Prepare(string platform = "android")
    {
        if (platform is not ("android" or "operagx")) throw new ArgumentException("Destino de exportacao invalido.");
        var common = Type.GetType("YoYoStudio.Plugins.Build.Common, Build", true)!;
        object? Call(string name, params object?[] args) => common.GetMethod(name, Static)!.Invoke(null, args);
        string Get(string name) => (string)common.GetProperty(name, Static)!.GetValue(null)!;
        Call("AssignMacros");
        Call("EnsureFontsGenerated");
        Call("WriteMacros");
        if (platform == "android") Type.GetType("YoYoStudio.Plugins.Build.Preferences, Build", true)!
            .GetMethod("WriteAndroidPreferences", Static)!.Invoke(null, null);
        var env = (Dictionary<string, string>)Call("CreateDefaultEnvironment", "VM", platform)!;
        if (!env.TryGetValue("config", out var config) || string.IsNullOrWhiteSpace(config)) env["config"] = "Default";
        string cache = Get("AssetCompileCache");
        Directory.CreateDirectory(cache);
        string targetOptions = Path.Combine(cache, platform + "-export-targetoptions.json");
        File.WriteAllText(targetOptions, "{\"runtime\":\"VM\"}");
        env["targetOptions"] = targetOptions;
        env["verbose"] = "True";
        env["compile_output_file_name"] = Path.Combine((string)Call("DestFolder", "VM")!, Get("ProjectName") + ".win");
        env["useShaders"] = "TRUE";
        env["package_launch"] = "False";
        // Mesmo que Common.WriteToBuildFile (Build.dll) acrescenta antes de chamar o Igor.
        var prefs = typeof(YoYoStudio.Core.Utils.Preferences.PreferencesManager);
        env["helpPort"] = PrefInt(prefs, "machine.General Settings.Help.WebServerPort", 51290).ToString();
        env["debuggerPort"] = PrefInt(prefs, "machine.General Settings.Debugging.DebuggerPort", 6502).ToString();
        env["projecttool"] = YoYoStudio.Resources.ProjectTool.Locator.FindTool() ?? "";
        // ProjectInfo.PrefabCentralPaths (CoreResources) e um campo; EnvironmentPackagesFolderPath
        // tambem (readonly). Sem ele o compilador nao acha os prefabs ("Missing Prefab") e falha.
        var info = Type.GetType("YoYoStudio.Resources.ProjectInfo, CoreResources", true)!;
        var paths = info.GetField("PrefabCentralPaths", Static)?.GetValue(null)
            ?? throw new InvalidOperationException("A IDE nao informou a pasta de prefabs (ProjectInfo.PrefabCentralPaths).");
        string prefabs = paths.GetType().GetField("EnvironmentPackagesFolderPath")?.GetValue(paths) as string ?? "";
        if (string.IsNullOrWhiteSpace(prefabs) || !Directory.Exists(prefabs))
            throw new DirectoryNotFoundException("Pasta de prefabs da IDE nao encontrada: '" + prefabs + "'.");
        env["prefabs"] = prefabs;
        if (info.GetProperty("SerialiserDeveloperLeniencyMode", Static)?.GetValue(null) is true ||
            info.GetField("SerialiserDeveloperLeniencyMode", Static)?.GetValue(null) is true)
            env["sdlm"] = "true";
        string bff = Path.Combine(cache, platform + "-export-build.bff");
        File.WriteAllText(bff, JsonSerializer.Serialize(env));
        // Common.WriteToBuildFile: -j=<MaxParallelJobs> e, fora do Windows, -ac=/cins no modo sem
        // diferenca de maiusculas (AssetCompilerOption).
        var options = new List<string> { "-j=" + PrefInt(prefs, "machine.General Settings.Compiling.MaxParallelJobs", 2) };
        if (PrefBool(prefs, "machine.General Settings.CaseInsensitiveMode")) options.Add("-ac=/cins");
        return new Input(bff, options.ToArray());
    }

    static T? Pref<T>(Type prefs, string method, string key)
    {
        // GetSetting<T>(string) e Get<T>(string, bool mayBeMissing = false)
        var get = prefs.GetMethods(Static).First(m => m.Name == method && m.IsGenericMethodDefinition &&
            m.GetParameters() is { Length: > 0 } p && p[0].ParameterType == typeof(string));
        var args = get.GetParameters().Select((p, i) => i == 0 ? key : p.DefaultValue).ToArray();
        return (T?)get.MakeGenericMethod(typeof(T)).Invoke(null, args);
    }

    static int PrefInt(Type prefs, string key, int fallback)
    {
        try { return Pref<int>(prefs, "GetSetting", key) is var v && v > 0 ? v : fallback; }
        catch { return fallback; }
    }

    static bool PrefBool(Type prefs, string key)
    {
        try { return Pref<bool>(prefs, "Get", key); }
        catch { return false; }
    }

    internal static void Run(Input input, string choicesJson, Action<string> progress)
    {
        var choices = JsonNode.Parse(choicesJson)!;
        string output = Path.GetFullPath(choices["output"]!.GetValue<string>());
        string job = Path.Combine(Path.GetDirectoryName(input.BuildFile)!, "apk-" + Guid.NewGuid().ToString("N"));
        string stagedApk = job + ".apk", buildFile = job + ".bff", optionsFile = job + ".json";
        string publishTemp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var env = JsonNode.Parse(File.ReadAllText(input.BuildFile))!;
        env["targetFile"] = stagedApk;
        File.WriteAllText(buildFile, env.ToJsonString());
        File.WriteAllText(optionsFile, choicesJson);
        var start = new ProcessStartInfo(Path.Combine(MainApplication.Instance.ApplicationInfo!.NativeLibraryDir!, BuildIntegration.StubName))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in input.IgorOptions.Concat(new[] { "-v", "-options=" + buildFile, "--", "Android", "Package" }))
            start.ArgumentList.Add(arg);
        start.Environment["GMIDE_LOCAL_ANDROID_EXPORT"] = "1";
        start.Environment["GMIDE_ANDROID_EXPORT_OPTIONS"] = optionsFile;
        start.Environment["GMIDE_ANDROID_EXPORT_BUILD"] = buildFile;
        using var process = new Process { StartInfo = start };
        var errors = new Queue<string>();
        var clock = Stopwatch.StartNew();
        long lastProgress = -500;
        void Line(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            global::Android.Util.Log.Info("GmIde.Apk", line);
            lock (errors)
            {
                errors.Enqueue(line); while (errors.Count > 12) errors.Dequeue();
                bool error = line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("FAILED", StringComparison.OrdinalIgnoreCase);
                bool stage = line.Contains("[GMIDE]", StringComparison.Ordinal) || line.StartsWith("Compil", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("DoSplash", StringComparison.Ordinal) || line.StartsWith("DoIcon", StringComparison.Ordinal);
                if ((error || stage) && clock.ElapsedMilliseconds - lastProgress >= 250)
                { lastProgress = clock.ElapsedMilliseconds; progress(line); }
            }
        }
        try
        {
        process.Start();
        Task Pump(StreamReader reader) => Task.Run(() => { string? line; while ((line = reader.ReadLine()) != null) Line(line); });
        var pumps = new[] { Pump(process.StandardOutput), Pump(process.StandardError) };
        if (!process.WaitForExit((int)CompileTimeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (PlatformNotSupportedException) { process.Kill(); }
            process.WaitForExit(5000);
            throw new TimeoutException("A compilacao excedeu 15 minutos e foi interrompida. Consulte o log do compilador.");
        }
        if (!Task.WaitAll(pumps, TimeSpan.FromSeconds(5))) throw new IOException("O compilador encerrou, mas a leitura do log nao terminou.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException("A compilacao Android falhou (codigo " + process.ExitCode + ").\n" + string.Join("\n", errors));
        if (!File.Exists(stagedApk)) throw new InvalidDataException("O compilador terminou sem gerar o APK.");
        progress("Salvando o APK em Downloads...");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.Copy(stagedApk, publishTemp);
        File.Move(publishTemp, output, overwrite: true);
        }
        finally
        {
            foreach (string file in new[] { stagedApk, buildFile, optionsFile, publishTemp })
                try { File.Delete(file); } catch { }
        }
    }
}
