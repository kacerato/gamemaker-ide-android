using Android.Content;
using Android.OS;
using Android.Util;

namespace GmIde;

// Funcoes "abrir no sistema" da IDE que no ramo Linux executam programas que o Android nao tem
// (xdg-open, /usr/bin/ffmpeg, <ide>/IDE). Chamadas pelos patches Cecil (IdePatches.PlatformPatches).
// Assinaturas so com tipos do BCL: os patches importam estes metodos antes de o IDE.dll/OSCore.dll
// poderem ser carregados (ver MobileWindows.cs).
public static class PlatformBridge
{
    const string Tag = "GmIde.Platform";

    /// <summary>Pedido de reinicio da IDE (SpawnNewIDE); o Launcher mata o processo quando o Execute volta.</summary>
    public static volatile bool RestartRequested;

    static Context Ctx => MainApplication.Instance;

    // ------------------------------------------------------------------ ffmpeg do editor de som

    /// <summary>
    /// Audio.dll Cache.Init: no Linux ffMpeg = "/usr/bin//ffmpeg". Devolve o ffmpeg do app
    /// (native/ffmpeg -> lib/arm64-v8a/libffmpeg.so, extraido na instalacao) quando existe.
    /// </summary>
    public static string FfmpegPath(string current)
    {
        try
        {
            string exe = Path.Combine(Ctx.ApplicationInfo?.NativeLibraryDir ?? "", "libffmpeg.so");
            if (File.Exists(exe)) { Log.Info(Tag, $"Editor de som: ffmpeg {current} -> {exe}"); return exe; }
            Log.Warn(Tag, $"Editor de som: {exe} ausente; mantendo {current}");
        }
        catch (Exception e) { Log.Warn(Tag, "FfmpegPath: " + e.Message); }
        return current;
    }

    // ------------------------------------------------------------------ links e pastas

    /// <summary>
    /// IDE.OpenBrowser: links (http/https/mailto) abrem no navegador por Intent.ACTION_VIEW. Caminhos
    /// locais (Help > Open project directory etc.) vao para OpenLocation. true = tratado aqui.
    /// </summary>
    public static bool OpenUrl(string url)
    {
        try
        {
            url = (url ?? "").Trim().Trim('"');
            if (url.Length == 0) return true;
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) url = Uri.UnescapeDataString(url.Substring(7));
            if (url.StartsWith("/") || File.Exists(url) || Directory.Exists(url)) { OpenLocation(url); return true; }
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(intent);
            Log.Info(Tag, "Abrindo link: " + url);
        }
        catch (Exception e)
        {
            Log.Warn(Tag, $"Abrir link {url}: {e.Message}");
            CopyAndToast(url, "Nenhum app abriu o link. Endereco copiado:\n" + url);
        }
        return true;
    }

    /// <summary>
    /// FileExplorer_Linux.OpenLocation (OSCore; "Show in Explorer", fim da exportacao YYZ...): o
    /// original faz Process.Start(pasta) sem try/catch e derruba a IDE. Pastas no armazenamento
    /// compartilhado abrem no app Arquivos (DocumentsContract); as privadas do app (Android/data,
    /// /data) nao sao visiveis para outros apps: o caminho e copiado e mostrado.
    /// </summary>
    public static void OpenLocation(string path)
    {
        string dir = path ?? "";
        try
        {
            while (dir.Length > 0 && !File.Exists(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir) ?? "";
            if (dir.Length > 0 && File.Exists(dir)) dir = Path.GetDirectoryName(dir) ?? dir;
            if (dir.Length == 0) dir = path ?? "";
            if (TryOpenInFilesApp(dir)) return;
        }
        catch (Exception e) { Log.Warn(Tag, $"Abrir pasta {dir}: {e.Message}"); }
        CopyAndToast(dir, "Pasta (caminho copiado):\n" + dir);
    }

    /// <summary>FileExplorer_Linux.OpenFile: mostra a pasta do arquivo (sem FileProvider para abrir em outro app).</summary>
    public static void OpenFile(string path)
    {
        Log.Info(Tag, "Abrir arquivo no app associado: " + path);
        OpenLocation(path);
    }

    const string SharedRoot = "/storage/emulated/0";

    static bool TryOpenInFilesApp(string dir)
    {
        string full = Path.GetFullPath(dir);
        if (!full.StartsWith(SharedRoot + "/", StringComparison.Ordinal) && full != SharedRoot) return false;
        string rel = full.Length > SharedRoot.Length ? full.Substring(SharedRoot.Length + 1) : "";
        if (rel.StartsWith("Android/data", StringComparison.Ordinal) || rel.StartsWith("Android/obb", StringComparison.Ordinal)) return false;
        var uri = global::Android.Provider.DocumentsContract.BuildDocumentUri("com.android.externalstorage.documents", "primary:" + rel);
        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, global::Android.Provider.DocumentsContract.Document.MimeTypeDir);
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);
        try
        {
            Ctx.StartActivity(intent);
            Log.Info(Tag, "Pasta aberta no app Arquivos: " + full);
            return true;
        }
        catch (ActivityNotFoundException) { return false; }
    }

    static void CopyAndToast(string copy, string message)
    {
        Log.Info(Tag, message.Replace('\n', ' '));
        new Handler(Looper.MainLooper!).Post(() =>
        {
            try
            {
                var cm = (ClipboardManager?)Ctx.GetSystemService(Context.ClipboardService);
                if (cm != null) cm.PrimaryClip = ClipData.NewPlainText("GameMaker IDE", copy);
                global::Android.Widget.Toast.MakeText(Ctx, message, global::Android.Widget.ToastLength.Long)!.Show();
            }
            catch (Exception e) { Log.Warn(Tag, "Toast: " + e.Message); }
        });
    }

    // ------------------------------------------------------------------ dock de baixo (Feather)

    static int _featherDocks;

    /// <summary>
    /// CodeDiagnostics DiagnosticsModule.CreateWindow: DockGadget(window, tab, autoExpand: !RestoringDesktops).
    /// A primeira criacao e automatica (OnPostLayout depois de abrir o projeto, quando a restauracao do
    /// layout ja acabou) e expandia o dock de baixo em todo projeto, ocupando metade da altura no celular.
    /// So essa primeira vez entra no dock sem expandir; "Show Diagnostics" depois expande normalmente.
    /// </summary>
    public static bool FeatherAutoExpand(bool original)
    {
        if (Interlocked.Increment(ref _featherDocks) == 1)
        {
            if (original) Log.Info(Tag, "Feather: janela criada no dock de baixo sem expandi-lo");
            return false;
        }
        return original;
    }

    // ------------------------------------------------------------------ reinicio da IDE

    /// <summary>
    /// IDE.SpawnNewIDE (fim do IDE.Run com m_isRestarting: troca de skin, DPI, runtime...): no Linux
    /// executa &lt;ide&gt;/IDE, que nao existe. Aqui: io.gmide.RestartActivity num processo proprio espera
    /// este processo terminar e abre a IDE de novo; o Launcher mata o processo quando o Execute volta.
    /// </summary>
    public static bool SpawnNewIde(string[]? args)
    {
        try
        {
            var intent = new Intent();
            intent.SetClassName(Ctx.PackageName!, "io.gmide.RestartActivity");
            intent.PutExtra("pid", Process.MyPid());
            intent.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(intent);
            RestartRequested = true;
            Log.Info(Tag, "Reinicio da IDE pedido (" + string.Join(" ", args ?? Array.Empty<string>()) + ")");
            return true;
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Reinicio da IDE: " + e);
            CopyAndToast("", "Feche e abra o app para aplicar a mudanca.");
            return true; // nunca deixar o original executar <ide>/IDE
        }
    }
}
