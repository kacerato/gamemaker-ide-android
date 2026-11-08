using System.Diagnostics;
using Android.Util;

namespace GmIde;

// Sem adb nao ha como ler o logcat. Este espelho roda "logcat" do proprio app
// (um app pode ler o log do proprio processo) e grava em
// Download/gmide-log.txt, que o usuario consegue abrir e enviar.
// O arquivo e recriado a cada abertura do app; o anterior vira gmide-log.prev.txt.
static class LogMirror
{
    const string Dir = "/storage/emulated/0/Download";
    static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        var thread = new Thread(Run) { IsBackground = true, Name = "gmide-logmirror" };
        thread.Start();
    }

    static void Run()
    {
        try
        {
            var path = Path.Combine(Dir, "gmide-log.txt");
            var prev = Path.Combine(Dir, "gmide-log.prev.txt");
            if (File.Exists(path)) File.Move(path, prev, true);

            var psi = new ProcessStartInfo("logcat", $"-v time --pid={Environment.ProcessId}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi)!;
            using var w = new StreamWriter(path, false) { AutoFlush = true };
            w.WriteLine($"# GameMaker IDE port - log do processo {Environment.ProcessId} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
                w.WriteLine(line);
        }
        catch (Exception e)
        {
            // Sem permissao "Todos os arquivos" ainda: tenta de novo no proximo boot.
            Log.Warn("GmIde", "LogMirror: " + e.Message);
            _started = false;
        }
    }
}
