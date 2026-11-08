using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Android.App;
using Android.Content;
using Android.Util;
using YoYoStudio.Core.Utils;

namespace GmIde;

// Native Android preview. The IDE keeps its normal CmdProcess, Output and RunComplete lifecycle.
public static class LocalGameRun
{
    static readonly System.Collections.Concurrent.ConcurrentQueue<string> Controls = new();
    public static void RequestControl(string action) => Controls.Enqueue(action);
    public static void RunPendingControls()
    {
        while (Controls.TryDequeue(out var action))
        {
            if (action == "preview-f5")
            {
                var debugger = Type.GetType("YoYoStudio.GMDebug.DebuggerManager, Debugger", false);
                if (debugger?.GetProperty("Connection")?.GetValue(null)?.ToString() == "Connected")
                {
                    // Same event as the debugger's Continue button, on the IDE thread.
                    var resume = debugger.GetField("OnContinue")!.GetValue(null)!;
                    resume.GetType().GetMethod("Throw")!.Invoke(resume, new object[] { Array.Empty<object>() });
                }
                else YoYoStudio.Plugins.Command.execute("run_program");
            }
            else if (action == "preview-stop") YoYoStudio.Plugins.Command.execute("stop_program");
            else if (action == "preview-show")
            {
                var manager = (ActivityManager)Application.Context.GetSystemService(Context.ActivityService)!;
                var task = manager.AppTasks?.FirstOrDefault(t => t.TaskInfo?.BaseIntent?.Component?.ClassName == "io.gmide.yyrunner.RunnerActivity");
                if (task != null) task.MoveToFront();
                else StudioConsole.WriteLine("[GMIDE] Nenhum preview em execucao. Use F5 ou F6 para iniciar.");
            }
        }
    }

    public static bool IsLocalTarget() => OperatingSystem.IsAndroid() &&
        YoYoStudio.User.TargetManager.Platform?.ToLowerInvariant() is "test" or "linux" or "android";

    public static bool CheckSettings(string runtime)
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(30)) throw new NotSupportedException("O preview embutido requer Android 11 ou superior. A exportacao de APK continua disponivel.");
            if (runtime != "VM") throw new NotSupportedException("O preview no aparelho usa Android VM. Selecione VM antes de executar.");
            var pi = Type.GetType("YoYoStudio.Resources.ProjectInfo, CoreResources", true)!;
            var project = pi.GetField("Current")!.GetValue(null) ?? throw new InvalidOperationException("Abra um projeto.");
            var options = AndroidGameOptions.Get(project, create: true)!;
            AndroidGameOptions.FillMissingBuildSettings(options, ApkExport.TemplateCompileSdk());
            if (!File.Exists(Path.Combine(MainApplication.Instance.ApplicationInfo!.NativeLibraryDir!, "libyoyo.so")))
                throw new FileNotFoundException("Runner Android nao esta incluido nesta versao da IDE.");
            BuildIntegration.EnsureBuildService();
            StudioConsole.WriteLine("[GMIDE] Preview Android local (VM), sem instalacao.");
            return true;
        }
        catch (Exception e) { StudioConsole.WriteLine("[GMIDE] " + e.Message); return false; }
    }

    static string GameDirectory
    {
        get
        {
            string key = Environment.GetEnvironmentVariable("GMIDE_ANDROID_RUN_KEY")
                ?? throw new InvalidOperationException("Projeto do preview nao identificado.");
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            return Path.Combine(Application.Context.FilesDir!.AbsolutePath, "game-run", hash);
        }
    }

    // Called in :build at the same packaging point used by APK export, without signing.
    public static void CreateDataZip(string module, string output)
    {
        string assets = Path.Combine(module, "src", "main", "assets");
        if (!File.Exists(Path.Combine(assets, "game.droid"))) throw new FileNotFoundException("O compilador nao gerou assets/game.droid.");
        foreach (string root in new[] { Path.Combine(module, "src", "main", "jniLibs"), Path.Combine(module, "elibs") })
            if (Directory.Exists(root) && Directory.EnumerateFiles(root, "*.so", SearchOption.AllDirectories).Any(f => Path.GetFileName(f) != "libyoyo.so"))
                throw new NotSupportedException("O preview embutido ainda nao carrega bibliotecas nativas de extensoes. Use o APK do jogo para este projeto.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using (var fileStream = new FileStream(output + ".tmp", FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fileStream, ZipArchiveMode.Create))
            foreach (string file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories).Order())
                zip.CreateEntryFromFile(file, "assets/" + Path.GetRelativePath(assets, file).Replace('\\', '/'), CompressionLevel.NoCompression);
        File.Move(output + ".tmp", output, overwrite: true);
        Directory.CreateDirectory(GameDirectory);
        File.Copy(output, Path.Combine(GameDirectory, "game.zip.tmp"), overwrite: true);
        File.Move(Path.Combine(GameDirectory, "game.zip.tmp"), Path.Combine(GameDirectory, "game.zip"), overwrite: true);
        Console.WriteLine("[GMIDE] Dados do preview Android prontos.");
    }

    static int GamePid()
    {
        var manager = (ActivityManager)Application.Context.GetSystemService(Context.ActivityService)!;
        return manager.RunningAppProcesses?.FirstOrDefault(p => p.ProcessName == Application.Context.PackageName + ":game")?.Pid ?? 0;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    static extern int ProbeProcess(int pid, int signal);
    // Android restricts cross-process /proc reads, even for processes of this app.
    // Signal zero only checks existence and permission; it does not stop the game.
    static bool ProcessExists(int pid) => pid > 0 &&
        (ProbeProcess(pid, 0) == 0 || System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 1);

    public static void Stop()
    {
        int pid = GamePid();
        if (pid > 0) global::Android.OS.Process.KillProcess(pid);
    }

    public static void StopBuild()
    {
        bool runningGame = GamePid() > 0;
        Stop();
        if (runningGame) return; // RunAndWait ends normally; keep the build service reusable.
        var common = Type.GetType("YoYoStudio.Plugins.Build.Common, Build", true)!;
        var process = common.GetProperty("BuildProcess", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
        process?.GetType().GetMethod("Terminate")?.Invoke(process, null);
    }

    public static int RunAndWait()
    {
        string directory = GameDirectory, zip = Path.Combine(directory, "game.zip");
        if (!File.Exists(zip)) throw new FileNotFoundException("Compile o projeto antes de repetir o preview.", zip);
        Stop();
        var stopping = Stopwatch.StartNew();
        while (GamePid() != 0 && stopping.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(100);
        if (GamePid() != 0) throw new TimeoutException("O preview anterior nao encerrou.");
        var ctx = Application.Context;
        string saves = Path.Combine(directory, "saves");
        Directory.CreateDirectory(saves);
        var intent = new Intent();
        intent.SetClassName(ctx.PackageName!, "io.gmide.yyrunner.RunnerActivity");
        intent.PutExtra("gmide.game_zip", zip);
        intent.PutExtra("gmide.save_dir", saves);
        intent.PutExtra("gmide.project_id", "preview." + Path.GetFileName(directory).ToLowerInvariant());
        intent.PutExtra("gmide.debug", Environment.GetEnvironmentVariable("GMIDE_ANDROID_DEBUG") == "1");
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
        ctx.StartActivity(intent);
        int pid = 0;
        var startup = Stopwatch.StartNew();
        while (startup.Elapsed < TimeSpan.FromSeconds(30) && (pid = GamePid()) == 0) Thread.Sleep(100);
        if (pid == 0) throw new TimeoutException("O runner Android nao iniciou em 30 segundos.");
        Console.WriteLine($"[GMIDE] Preview iniciado (processo {pid}). Use Stop na IDE para encerrar a execucao.");
        var start = new ProcessStartInfo("/system/bin/logcat") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "brief", "--pid=" + pid, "yoyo:V", "AndroidRuntime:E", "libc:E", "*:S" }) start.ArgumentList.Add(arg);
        using var log = System.Diagnostics.Process.Start(start) ?? throw new IOException("Nao foi possivel ler o log do preview.");
        int failed = 0, ready = 0, debuggerConnected = 0, debuggerShown = 0;
        var pump = Task.Run(() =>
        {
            string? line;
            while ((line = log.StandardOutput.ReadLine()) != null)
            {
                if (line.Contains("FATAL EXCEPTION") || line.Contains("Fatal signal")) Interlocked.Exchange(ref failed, 1);
                if (line.Contains("[GMIDE] Native preview ready")) Interlocked.Exchange(ref ready, 1);
                // F6 can intentionally stop in Startup before the first room/frame.
                if (line.Contains("Debugger connected")) Interlocked.Exchange(ref debuggerConnected, 1);
                Console.WriteLine(line);
                if (line.Contains("[DbgServer]") && Interlocked.Exchange(ref debuggerShown, 1) == 0)
                {
                    // The debugger finishes its handshake on the IDE's frame loop.
                    // Leaving the runner in front suspends that loop and can ANR Startup.
                    var pip = new Intent(ctx.PackageName + ".DEBUG_PIP");
                    pip.SetPackage(ctx.PackageName);
                    ctx.SendBroadcast(pip);
                }
            }
        });
        try
        {
            while (ProcessExists(pid))
            {
                if (ready == 0 && debuggerConnected == 0 && startup.Elapsed > TimeSpan.FromSeconds(30))
                { Stop(); throw new TimeoutException("O runner iniciou, mas o jogo nao inicializou em 30 segundos."); }
                Thread.Sleep(200);
            }
        }
        finally
        {
            Thread.Sleep(150);
            try { if (!log.HasExited) log.Kill(); } catch { }
            pump.Wait(TimeSpan.FromSeconds(2));
        }
        Console.WriteLine("[GMIDE] Preview encerrado.");
        if (ready == 0 && debuggerConnected == 0) { Console.Error.WriteLine("[GMIDE] O runner encerrou antes de inicializar o jogo."); return 1; }
        return failed;
    }
}
