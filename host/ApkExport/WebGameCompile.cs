using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GmIde;

internal static class WebGameCompile
{
    internal static string Run(AndroidGameCompile.Input input, string choicesJson, Action<string> progress)
    {
        var choices = JsonNode.Parse(choicesJson)!;
        string target = choices["target"]!.GetValue<string>();
        if (target is not ("web" or "windows")) throw new ArgumentException("Destino invalido.");
        string output = Path.GetFullPath(choices["output"]!.GetValue<string>());
        string title = choices["display_name"]!.GetValue<string>();
        string version = choices["version"]!.GetValue<string>();
        string id = choices["package"]!.GetValue<string>();
        var ctx = MainApplication.Instance;
        string jobs = Path.Combine(ctx.CacheDir!.AbsolutePath, "game-exports");
        var job = WebExportPackage.Prepare(input.BuildFile, jobs, title, version);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(ctx.ApplicationInfo!.NativeLibraryDir!, BuildIntegration.StubName))
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in input.IgorOptions.Concat(new[] { "-v", "-options=" + job.BuildFile, "--", "OperaGX", "Package" }))
                start.ArgumentList.Add(arg);
            start.Environment["GMIDE_LOCAL_WEB_EXPORT"] = "1";
            start.Environment.Remove("GMIDE_LOCAL_ANDROID_EXPORT");
            var errors = new Queue<string>();
            var clock = Stopwatch.StartNew(); long last = -500;
            void Line(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                global::Android.Util.Log.Info("GmIde.Export", line);
                lock (errors)
                {
                    errors.Enqueue(line); while (errors.Count > 16) errors.Dequeue();
                    if (clock.ElapsedMilliseconds - last >= 300 && (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("compil", StringComparison.OrdinalIgnoreCase) || line.Contains("[GMIDE]")))
                    { last = clock.ElapsedMilliseconds; progress(line); }
                }
            }
            using var process = new Process { StartInfo = start };
            process.Start();
            Task Pump(StreamReader reader) => Task.Run(() => { string? line; while ((line = reader.ReadLine()) != null) Line(line); });
            var pumps = new[] { Pump(process.StandardOutput), Pump(process.StandardError) };
            if (!process.WaitForExit(15 * 60 * 1000))
            {
                try { process.Kill(true); } catch (PlatformNotSupportedException) { process.Kill(); }
                process.WaitForExit(5000);
                throw new TimeoutException("A compilacao excedeu 15 minutos e foi interrompida.");
            }
            if (!Task.WaitAll(pumps, TimeSpan.FromSeconds(5))) throw new IOException("A leitura do log do compilador nao terminou.");
            if (process.ExitCode != 0) throw new InvalidOperationException("A compilacao web falhou (codigo " + process.ExitCode + ").\n" + string.Join("\n", errors));
            byte[]? png = null;
            if (choices["icon_path"]?.GetValue<string>() is { Length: > 0 } icon) png = ApkBuilder.RenderIconPng(icon, 256);
            progress("Preparando os arquivos do jogo...");
            WebExportPackage.UnpackAndPersonalize(job, title, version, png);
            string folder = job.Preview;
            if (target == "windows")
            {
                progress("Montando o pacote Windows x64...");
                string player = Path.Combine(job.Root, "player"); Directory.CreateDirectory(player);
                foreach (string file in new[] { "GmGame.exe", "WebView2Loader.dll", "WebView2-LICENSE.txt" })
                {
                    using var source = ctx.Assets!.Open("windows-player/" + file);
                    using var dest = File.Create(Path.Combine(player, file)); source.CopyTo(dest);
                }
                folder = WindowsWebPackage.Create(job.Root, job.Preview, player, title, id, version, png,
                    choices["window_width"]?.GetValue<int>() ?? 1280, choices["window_height"]?.GetValue<int>() ?? 720);
            }
            progress("Salvando em Downloads...");
            WebExportPackage.Publish(folder, output);
            // Preview uses the exact, personalized files in the exported archive, never a stale Run cache.
            // One stable folder per storage identifier; repeated exports replace the previous preview.
            string preview = Path.Combine(ctx.FilesDir!.AbsolutePath, "export-previews", Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id))));
            Directory.CreateDirectory(Path.GetDirectoryName(preview)!);
            if (Directory.Exists(preview)) Directory.Delete(preview, true);
            Directory.Move(job.Preview, preview);
            return preview;
        }
        finally { if (Directory.Exists(job.Root)) Directory.Delete(job.Root, true); }
    }
}
