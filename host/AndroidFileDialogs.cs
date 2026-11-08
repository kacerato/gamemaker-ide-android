using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Util;
using Android.Webkit;
using Core.CoreOS.FileAPI;
using AEnv = Android.OS.Environment;
using AUri = Android.Net.Uri;

namespace GmIde;

// Dialogos de arquivo da IDE usando o Storage Access Framework (SAF).
//
// PROBLEMA: a IDE so trabalha com caminhos comuns (System.IO), e o SAF devolve
// URIs content://. Escolha feita:
//
//  1. Caminho real (preferido). O app declara MANAGE_EXTERNAL_STORAGE
//     ("Acesso a todos os arquivos", Android 11+). Na primeira vez que um
//     dialogo e aberto sem essa permissao, abrimos a tela do sistema para o
//     usuario conceder (uma vez por sessao). Com a permissao, o URI escolhido no
//     seletor e convertido para caminho real: o documentId do provedor
//     com.android.externalstorage.documents tem a forma "primary:Pasta/arq"
//     -> /storage/emulated/0/Pasta/arq, ou "XXXX-XXXX:..." -> /storage/XXXX-XXXX/...
//     (cartao SD). Downloads com id "raw:/caminho" tambem e aceito. A IDE le e
//     escreve direto no lugar escolhido.
//     Caminhos dentro da pasta do proprio app (Android/data/io.gmide.android)
//     sao usados direto mesmo sem a permissao.
//
//  2. Copia (fallback: sem permissao, Android < 11, ou provedor sem caminho real,
//     ex.: Google Drive):
//     - abrir arquivo(s): copia para files/projects/_imported/<nome>;
//     - abrir pasta: copia a arvore inteira para files/projects/<nome da pasta>.
//       E uma importacao de mao unica: o que a IDE salvar fica na copia;
//     - salvar: o documento e criado pelo SAF, a IDE recebe um caminho em
//       files/projects/_exports/<nome> e um FileSystemWatcher copia o arquivo para
//       o URI sempre que ele muda (com debounce).
//
// THREADS: ShowDialog e chamado na thread do SDL (a thread da IDE). O Java
// (io.gmide.FilePicker) agenda startActivityForResult na thread de UI e bloqueia
// a thread do SDL ate GmIdeActivity.onActivityResult entregar o resultado. O
// callback e chamado na propria thread do SDL antes de ShowDialog retornar,
// igual ao OpenFileDialog_Linux original (sincrono). Enquanto o seletor esta
// aberto o loop da IDE fica parado, o que e o mesmo que acontece com o SDL
// pausado em segundo plano.
//
// CONTRATO (igual ao *_Linux da YoYo, OpenFileDialog_Linux.OpenFiles): sucesso ->
// complete(List<string>, null) para pasta ou selecao multipla, complete(string, null)
// para abrir UM arquivo e para salvar; cancelamento ou erro ->
// error(FileError.Cancelled, mensagem, null).
static class DialogPaths
{
    public static string Projects
    {
        get
        {
            var p = Path.Combine(MainApplication.Instance.GetExternalFilesDir(null)!.AbsolutePath, "projects");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    public static string Sub(string name)
    {
        var p = Path.Combine(Projects, name);
        Directory.CreateDirectory(p);
        return p;
    }
}

static class Saf
{
    const string Tag = "GmIde";
    const string ExternalStorageAuthority = "com.android.externalstorage.documents";
    const string DownloadsAuthority = "com.android.providers.downloads.documents";

    static IntPtr _pickerClass;
    static IntPtr _startAndWait;
    static bool _askedAllFiles;

    static ContentResolver Resolver => MainApplication.Instance.ContentResolver!;

    // ---- Ponte com io.gmide.FilePicker -------------------------------------

    /// Abre a Activity e bloqueia ate o resultado. null = cancelado/erro.
    public static Intent? Run(Intent request, bool acceptAnyResult = false)
    {
        if (_startAndWait == IntPtr.Zero)
        {
            var local = JNIEnv.FindClass("io/gmide/FilePicker");
            _pickerClass = JNIEnv.NewGlobalRef(local);
            _startAndWait = JNIEnv.GetStaticMethodID(_pickerClass, "startAndWait",
                "(Landroid/content/Intent;Z)Landroid/content/Intent;");
        }
        IntPtr r = JNIEnv.CallStaticObjectMethod(_pickerClass, _startAndWait,
            new JValue(request), new JValue(acceptAnyResult));
        return Java.Lang.Object.GetObject<Intent>(r, JniHandleOwnership.TransferLocalRef);
    }

    // ---- Permissao "Todos os arquivos" --------------------------------------

    public static bool HasAllFilesAccess =>
        OperatingSystem.IsAndroidVersionAtLeast(30) && AEnv.IsExternalStorageManager;

    /// Pede MANAGE_EXTERNAL_STORAGE uma vez por sessao. Retorna se a permissao existe.
    public static bool EnsureAllFilesAccess()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return false;
        if (AEnv.IsExternalStorageManager) return true;
        if (_askedAllFiles) return false;
        _askedAllFiles = true;

        Log.Info(Tag, "Pedindo permissao 'Todos os arquivos'");
        var pkg = AUri.Parse("package:" + MainApplication.Instance.PackageName);
        var i = new Intent(Settings.ActionManageAppAllFilesAccessPermission, pkg);
        if (Run(i, acceptAnyResult: true) == null)
            Run(new Intent(Settings.ActionManageAllFilesAccessPermission), acceptAnyResult: true);
        bool ok = AEnv.IsExternalStorageManager;
        Log.Info(Tag, "Permissao 'Todos os arquivos': " + ok);
        return ok;
    }

    // ---- URI <-> caminho ----------------------------------------------------

    static string AppStorageRoot =>
        Directory.GetParent(MainApplication.Instance.GetExternalFilesDir(null)!.AbsolutePath)!.FullName;

    /// Converte um URI do SAF para caminho real, se o provedor permitir.
    public static string? UriToPath(AUri uri, bool isTree)
    {
        try
        {
            if (uri.Scheme == "file") return uri.Path;
            string? docId = isTree && DocumentsContract.IsTreeUri(uri)
                ? DocumentsContract.GetTreeDocumentId(uri)
                : DocumentsContract.GetDocumentId(uri);
            if (docId == null) return null;

            if (uri.Authority == ExternalStorageAuthority)
            {
                int c = docId.IndexOf(':');
                string volume = c < 0 ? docId : docId[..c];
                string rest = c < 0 ? "" : docId[(c + 1)..];
                string root = volume switch
                {
                    "primary" => AEnv.ExternalStorageDirectory!.AbsolutePath,
                    "home" => Path.Combine(AEnv.ExternalStorageDirectory!.AbsolutePath, "Documents"),
                    _ => "/storage/" + volume,
                };
                return rest.Length == 0 ? root : Path.Combine(root, rest);
            }
            if (uri.Authority == DownloadsAuthority && docId.StartsWith("raw:"))
                return docId[4..];
        }
        catch (Exception e)
        {
            Log.Warn(Tag, $"UriToPath({uri}): {e.Message}");
        }
        return null;
    }

    /// Caminho real utilizavel com System.IO, ou null (cair para copia).
    public static string? UsablePath(AUri uri, bool isTree)
    {
        var p = UriToPath(uri, isTree);
        if (p == null) return null;
        bool inAppStorage = p.StartsWith(AppStorageRoot + "/", StringComparison.Ordinal) || p == AppStorageRoot;
        if (!inAppStorage && !HasAllFilesAccess) return null;
        if (!(File.Exists(p) || Directory.Exists(p))) return null;
        return p;
    }

    /// URI inicial (EXTRA_INITIAL_URI) a partir de um caminho em /storage/emulated/0.
    public static AUri? InitialUriFor(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        var root = AEnv.ExternalStorageDirectory!.AbsolutePath;
        string full;
        try { full = Path.GetFullPath(dir); } catch { return null; }
        if (!full.StartsWith(root, StringComparison.Ordinal)) return null;
        var rel = full[root.Length..].Trim('/');
        return DocumentsContract.BuildDocumentUri(ExternalStorageAuthority, "primary:" + rel);
    }

    public static void PutInitialUri(Intent i, string? dir)
    {
        var u = InitialUriFor(dir);
        if (u != null) i.PutExtra(DocumentsContract.ExtraInitialUri, u);
    }

    public static void TakePersistable(AUri uri)
    {
        try
        {
            Resolver.TakePersistableUriPermission(uri,
                ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
        }
        catch (Exception e)
        {
            Log.Debug(Tag, $"TakePersistableUriPermission({uri}): {e.Message}");
        }
    }

    // ---- Copia (fallback) ---------------------------------------------------

    public static string SafeName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Replace('/', '_');
    }

    public static string? DisplayName(AUri uri)
    {
        try
        {
            using ICursor? c = Resolver.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
            if (c != null && c.MoveToFirst()) return c.GetString(0);
        }
        catch (Exception e)
        {
            Log.Debug(Tag, $"DisplayName({uri}): {e.Message}");
        }
        return uri.LastPathSegment;
    }

    public static void CopyUriToFile(AUri uri, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var input = Resolver.OpenInputStream(uri) ?? throw new IOException("Nao foi possivel abrir " + uri);
        using var output = File.Create(dest);
        input.CopyTo(output);
    }

    public static void CopyFileToUri(string src, AUri uri)
    {
        using var input = File.OpenRead(src);
        using var output = Resolver.OpenOutputStream(uri, "wt") ?? throw new IOException("Nao foi possivel escrever " + uri);
        input.CopyTo(output);
    }

    /// Copia uma arvore do SAF para um diretorio local (recursivo).
    public static void CopyTree(AUri treeUri, string docId, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, docId)!;
        var cols = new[]
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
        };
        var entries = new List<(string id, string name, string mime)>();
        using (ICursor? c = Resolver.Query(children, cols, null, null, null))
        {
            while (c != null && c.MoveToNext())
                entries.Add((c.GetString(0)!, c.GetString(1) ?? "", c.GetString(2) ?? ""));
        }
        foreach (var (id, name, mime) in entries)
        {
            var target = Path.Combine(destDir, SafeName(name, "item"));
            if (mime == DocumentsContract.Document.MimeTypeDir)
                CopyTree(treeUri, id, target);
            else
                CopyUriToFile(DocumentsContract.BuildDocumentUriUsingTree(treeUri, id)!, target);
        }
    }

    // ---- Filtros -> MIME ----------------------------------------------------

    static readonly Regex ExtRx = new(@"\*\.([A-Za-z0-9_\-\*]+)", RegexOptions.Compiled);

    /// Tipos MIME para EXTRA_MIME_TYPES, ou null para "*/*". So filtra se TODAS
    /// as extensoes tiverem MIME conhecido (senao arquivos como .yyp sumiriam).
    public static string[]? MimeTypesFor(List<Tuple<string, string>>? filters)
    {
        if (filters == null || filters.Count == 0) return null;
        var mimes = new HashSet<string>();
        foreach (var f in filters)
        {
            var text = (f.Item1 ?? "") + ";" + (f.Item2 ?? "");
            var matches = ExtRx.Matches(text);
            if (matches.Count == 0 && text.Contains('*')) return null;
            foreach (Match m in matches)
            {
                var ext = m.Groups[1].Value.ToLowerInvariant();
                if (ext == "*") return null;
                var mime = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext);
                if (string.IsNullOrEmpty(mime)) return null;
                mimes.Add(mime);
            }
        }
        return mimes.Count == 0 ? null : mimes.ToArray();
    }

    public static string MimeForFile(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var mime = ext.Length > 0 ? MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext) : null;
        return string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
    }
}

/// Espelha um arquivo local num documento do SAF quando ele muda (fallback do salvar).
sealed class ExportMirror
{
    static readonly List<ExportMirror> Active = new();

    readonly string _path;
    readonly AUri _uri;
    readonly FileSystemWatcher _watcher;
    readonly Timer _debounce;

    ExportMirror(string path, AUri uri)
    {
        _path = path;
        _uri = uri;
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += (_, _) => Kick();
        _watcher.Created += (_, _) => Kick();
        _watcher.Renamed += (_, _) => Kick();
        _watcher.EnableRaisingEvents = true;
    }

    public static void Start(string path, AUri uri)
    {
        lock (Active)
        {
            foreach (var m in Active.Where(m => m._path == path).ToList())
            {
                m._watcher.Dispose();
                m._debounce.Dispose();
                Active.Remove(m);
            }
            Active.Add(new ExportMirror(path, uri));
        }
    }

    void Kick() => _debounce.Change(1500, Timeout.Infinite);

    void Flush()
    {
        try
        {
            if (!File.Exists(_path)) return;
            Saf.CopyFileToUri(_path, _uri);
            Log.Info("GmIde", $"Exportado {_path} -> {_uri}");
        }
        catch (Exception e)
        {
            Log.Warn("GmIde", $"Falha ao exportar {_path}: {e.Message}");
            Kick(); // arquivo ainda em uso: tenta de novo
        }
    }
}

public class AndroidOpenFileDialog : IOpenFileDialog
{
    public string InitialDirectory { get; set; } = "";
    public bool MultiSelect { get; set; }
    public bool SelectFolder { get; set; }
    public string Title { get; set; } = "";
    public bool AppsAsFolder { get; set; }
    public bool ShowNewFolder { get; set; }
    public Func<string> FindInitialDirectoryStrategy { get; set; } = null!;
    public List<Tuple<string, string>> Filters { get; set; } = new();

    public void ShowDialog(FileOpCompleteCallback complete, FileOpErrorCallback error)
    {
        Log.Info("GmIde", $"OpenFileDialog '{Title}' folder={SelectFolder} multi={MultiSelect}");
        List<string>? result;
        try
        {
            Saf.EnsureAllFilesAccess();
            result = SelectFolder ? PickFolder() : PickFiles();
        }
        catch (Exception e)
        {
            Log.Error("GmIde", "OpenFileDialog falhou: " + e);
            error(FileError.Cancelled, e.Message, null!);
            return;
        }
        if (result == null || result.Count == 0)
        {
            error(FileError.Cancelled, "Cancelado", null!);
            return;
        }
        Log.Info("GmIde", "OpenFileDialog -> " + string.Join(", ", result));
        // Mesmo tipo do OpenFileDialog_Linux.OpenFiles: lista para pasta ou selecao multipla, string
        // para um arquivo so. Os chamadores testam o tipo (Import Strip: "_result is string"; Abrir
        // Projeto: "(string)_result"); com a lista eles nao faziam nada ou lancavam InvalidCast.
        if (MultiSelect || SelectFolder) complete(result, null!);
        else complete(result[0], null!);
    }

    string? StartDir()
    {
        if (!string.IsNullOrEmpty(InitialDirectory)) return InitialDirectory;
        try { return FindInitialDirectoryStrategy?.Invoke(); } catch { return null; }
    }

    List<string>? PickFolder()
    {
        var i = new Intent(Intent.ActionOpenDocumentTree);
        i.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission |
                   ActivityFlags.GrantPersistableUriPermission);
        Saf.PutInitialUri(i, StartDir());
        var data = Saf.Run(i);
        var tree = data?.Data;
        if (tree == null) return null;
        Saf.TakePersistable(tree);

        var path = Saf.UsablePath(tree, isTree: true);
        if (path != null) return new List<string> { path };

        // Fallback: importa a pasta para files/projects/<nome>.
        var docId = DocumentsContract.GetTreeDocumentId(tree)!;
        var name = Saf.DisplayName(DocumentsContract.BuildDocumentUriUsingTree(tree, docId)!);
        var dest = Path.Combine(DialogPaths.Projects, Saf.SafeName(name, "pasta"));
        Log.Info("GmIde", $"Sem caminho real para {tree}; copiando para {dest}");
        Saf.CopyTree(tree, docId, dest);
        return new List<string> { dest };
    }

    List<string>? PickFiles()
    {
        var i = new Intent(Intent.ActionOpenDocument);
        i.AddCategory(Intent.CategoryOpenable);
        i.SetType("*/*");
        var mimes = Saf.MimeTypesFor(Filters);
        if (mimes != null) i.PutExtra(Intent.ExtraMimeTypes, mimes);
        if (MultiSelect) i.PutExtra(Intent.ExtraAllowMultiple, true);
        i.AddFlags(ActivityFlags.GrantReadUriPermission);
        Saf.PutInitialUri(i, StartDir());

        var data = Saf.Run(i);
        if (data == null) return null;
        var uris = new List<AUri>();
        var clip = data.ClipData;
        if (clip != null)
            for (int k = 0; k < clip.ItemCount; k++)
            {
                var u = clip.GetItemAt(k)?.Uri;
                if (u != null) uris.Add(u);
            }
        else if (data.Data != null)
            uris.Add(data.Data);

        var paths = new List<string>();
        foreach (var u in uris)
        {
            var path = Saf.UsablePath(u, isTree: false);
            if (path == null)
            {
                path = Path.Combine(DialogPaths.Sub("_imported"), Saf.SafeName(Saf.DisplayName(u), "arquivo"));
                Log.Info("GmIde", $"Sem caminho real para {u}; copiando para {path}");
                Saf.CopyUriToFile(u, path);
            }
            paths.Add(path);
        }
        return paths;
    }
}

public class AndroidSaveFileDialog : ISaveFileDialog
{
    public string InitialDirectory { get; set; } = "";
    public string FileName { get; set; } = "";
    public List<Tuple<string, string>> Filters { get; set; } = new();
    public string MessageString { get; set; } = "";
    public Func<string> FindInitialDirectoryStrategy { get; set; } = null!;

    /// <summary>
    /// Caminho ja escolhido fora da IDE (tela "Gerar APK", ApkExport.cs): o proximo dialogo de
    /// salvar devolve este caminho sem abrir o seletor. Vale uma vez.
    /// </summary>
    public static string? PresetPath;

    public void ShowDialog(FileOpCompleteCallback complete, FileOpErrorCallback error)
    {
        string? path;
        var preset = Interlocked.Exchange(ref PresetPath, null);
        if (preset != null)
        {
            Log.Info("GmIde", "SaveFileDialog -> " + preset + " (escolhido na tela Gerar APK)");
            complete(preset, null!);
            return;
        }
        try
        {
            path = Pick();
        }
        catch (Exception e)
        {
            Log.Error("GmIde", "SaveFileDialog falhou: " + e);
            error(FileError.Cancelled, e.Message, null!);
            return;
        }
        if (path == null)
        {
            error(FileError.Cancelled, "Cancelado", null!);
            return;
        }
        Log.Info("GmIde", "SaveFileDialog -> " + path);
        complete(path, null!); // SaveFileDialog_Linux passa o caminho como string
    }

    string? Pick()
    {
        Saf.EnsureAllFilesAccess();
        var name = string.IsNullOrEmpty(FileName) ? "untitled" : Path.GetFileName(FileName);
        var startDir = !string.IsNullOrEmpty(InitialDirectory) ? InitialDirectory
            : !string.IsNullOrEmpty(FileName) && Path.IsPathRooted(FileName) ? Path.GetDirectoryName(FileName)
            : SafeInvoke(FindInitialDirectoryStrategy);

        var i = new Intent(Intent.ActionCreateDocument);
        i.AddCategory(Intent.CategoryOpenable);
        i.SetType(Saf.MimeForFile(name));
        i.PutExtra(Intent.ExtraTitle, name);
        i.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission |
                   ActivityFlags.GrantPersistableUriPermission);
        Saf.PutInitialUri(i, startDir);

        var uri = Saf.Run(i)?.Data;
        if (uri == null) return null;
        Saf.TakePersistable(uri);

        // O SAF ja criou o arquivo (vazio); com caminho real a IDE sobrescreve.
        var real = Saf.UsablePath(uri, isTree: false);
        if (real != null) return real;

        // Fallback: a IDE grava numa copia local, espelhada no documento escolhido.
        var local = Path.Combine(DialogPaths.Sub("_exports"), Saf.SafeName(Saf.DisplayName(uri) ?? name, name));
        Log.Info("GmIde", $"Sem caminho real para {uri}; IDE grava em {local} (espelhado)");
        ExportMirror.Start(local, uri);
        return local;
    }

    static string? SafeInvoke(Func<string>? f)
    {
        try { return f?.Invoke(); } catch { return null; }
    }
}
