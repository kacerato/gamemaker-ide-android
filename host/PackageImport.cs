using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
#if ANDROID
using System.Runtime.InteropServices;
using Android.Content;
using Android.OS;
using Android.Util;
using Android.Widget;
using Microsoft.Win32.SafeHandles;
using AUri = Android.Net.Uri;
#endif

namespace GmIde;

// Instalacao SEM adb a partir de um pacote unico (tools/make-package.sh):
//
//   gmide-package.zip
//     ide/...                                      -> FilesDir/ide (com ide/ide-integrity.json)
//     runtime/runtime-2026.0.0.23/...              -> <system_directory>/Cache/runtimes/runtime-2026.0.0.23
//                                                     (com gmide-runtime-integrity.json e receipt.json)
//     runtime/Prefabs/...                          -> <system_directory>/Prefabs
//     package.json                                 versoes + SHA-256 de cada arquivo
//
// <system_directory> e o mesmo que RuntimeImport.SystemDirectory() calcula depois do
// MacroExpansion. No Mono/Android observado, CommonApplicationData resolve para
// <GetExternalFilesDir>/home; XDG_DATA_HOME nao altera esse valor. A instalacao nova
// ja chega com receipt.json; RuntimeImport registra o runtime e instala seu modulo Android.
//
// Fluxo (PackageImport.TryInstall, chamado pelo Launcher antes do ImportIde do adb):
//  1. abre bootstrap/gmide-package.zip do proprio APK, sem permissao ou copia;
//  2. apenas em pacotes legados sem asset, pede "Todos os arquivos" e procura
//     /storage/emulated/0/Download/gmide-package.zip; se nao achar (ou nao conseguir
//     ler), abre o seletor do sistema (ACTION_OPEN_DOCUMENT, zip);
//  3. extrai direto para as pastas finais, cada uma via "<pasta>.importando" + rename
//     (atomico: uma extracao interrompida nao deixa IDE.dll no destino e e refeita no
//     proximo boot). A IDE e renomeada por ultimo: IDE.dll presente = instalacao completa;
//  4. confere o SHA-256 de cada arquivo contra o package.json enquanto extrai.
// Progresso: logcat (tag GmIde.Pkg) + Toasts postados na thread de UI. A extracao roda na
// thread do SDL (SDL_main), entao a thread de UI segue livre.
//
// PackageExtractor nao depende de Android: tools/pkg-test compila este mesmo arquivo
// (sem o simbolo ANDROID) para testar a extracao no PC.

public sealed class PackageInfo
{
    public string? IdeVersion, RuntimeVersion, RuntimeFolder;
    public long TotalBytes;
    public Dictionary<string, (string Sha256, long Size)> Files = new(StringComparer.Ordinal);
}

// AssetFileDescriptor aponta para um intervalo dentro do APK. ZipArchive precisa
// de offsets relativos a esse intervalo, inclusive Seek(End), sem enxergar o resto
// do APK nem copiar o pacote inteiro para a memoria/cache.
public sealed class PackageAssetStream : Stream
{
    readonly Stream source;
    readonly long start, length;
    long position;
    bool disposed;

    // Apos construcao bem-sucedida, este stream possui source.
    public PackageAssetStream(Stream source, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("O pacote embutido exige arquivo legivel com seek.", nameof(source));
        if (start < 0 || length < 0 || start > source.Length || length > source.Length - start)
            throw new InvalidDataException("Intervalo do pacote fora do APK.");
        this.source = source;
        this.start = start;
        this.length = length;
        source.Position = start;
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length { get { ObjectDisposedException.ThrowIf(disposed, this); return length; } }
    public override long Position { get => Seek(0, SeekOrigin.Current); set => Seek(value, SeekOrigin.Begin); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (next < 0 || next > length) throw new IOException("Seek fora do pacote embutido.");
        source.Position = start + next;
        return position = next;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int count = (int)Math.Min(buffer.Length, length - position);
        if (count == 0) return 0;
        int read = source.Read(buffer[..count]);
        if (read == 0) throw new EndOfStreamException("Pacote embutido truncado.");
        position += read;
        return read;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) { disposed = true; source.Dispose(); }
        base.Dispose(disposing);
    }
}

public sealed class PackageExtractor
{
    public static string SystemDirectoryFor(string externalFiles) =>
        Path.Combine(externalFiles, "home", "GameMakerStudio2-LTS2026");
    public const string ManifestEntry = "package.json";
    public const string IdePrefix = "ide/";
    public const string RuntimePrefix = "runtime/";
    public const string PrefabsFolder = "Prefabs";
    const string TmpSuffix = ".importando";

    public required string IdeDir;
    public required string RuntimeDir;      // .../Cache/runtimes/runtime-X
    public required string PrefabsDir;      // .../GameMakerStudio2-LTS2026/Prefabs
    public required string RuntimeFolder;   // "runtime-2026.0.0.23"
    /// Runtime ja instalado (receipt.json): nao reextrai, so atualiza o manifesto de integridade.
    public bool SkipInstalledRuntime = true;
    public Action<string> Log = _ => { };
    /// Percentual 0..100 (chamado so quando muda).
    public Action<int>? Progress;

    public static PackageInfo? ReadInfo(ZipArchive zip)
    {
        var e = zip.GetEntry(ManifestEntry);
        if (e == null) return null;
        using var s = e.Open();
        using var doc = JsonDocument.Parse(s);
        var r = doc.RootElement;
        var info = new PackageInfo
        {
            IdeVersion = Str(r, "ideVersion"),
            RuntimeVersion = Str(r, "runtimeVersion"),
            RuntimeFolder = Str(r, "runtimeFolder"),
            TotalBytes = r.TryGetProperty("totalBytes", out var tb) && tb.ValueKind == JsonValueKind.Number ? tb.GetInt64() : 0,
        };
        if (r.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
            foreach (var f in files.EnumerateObject())
                info.Files[f.Name] = (f.Value.GetProperty("sha256").GetString()!.ToUpperInvariant(),
                                      f.Value.GetProperty("size").GetInt64());
        return info;

        static string? Str(JsonElement r, string n) =>
            r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    enum Target { Ide, Runtime, RuntimeManifestOnly, Prefabs }

    /// Bytes que a extracao vai gravar (para conferir espaco livre antes).
    public long BytesNeeded(ZipArchive zip) => Plan(zip).Sum(p => p.Entry.Length);

    List<(ZipArchiveEntry Entry, Target Target, string Rel)> Plan(ZipArchive zip)
    {
        bool runtimeInstalled = SkipInstalledRuntime && File.Exists(Path.Combine(RuntimeDir, "receipt.json"));
        var rtPrefix = RuntimePrefix + RuntimeFolder + "/";
        var pfPrefix = RuntimePrefix + PrefabsFolder + "/";
        var plan = new List<(ZipArchiveEntry, Target, string)>();
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            if (name.EndsWith('/') || name == ManifestEntry) continue;
            if (name.StartsWith('/') || name.Split('/').Any(s => s is ".." or "."))
                throw new InvalidDataException("Caminho invalido no pacote: " + name);
            if (name.StartsWith(IdePrefix, StringComparison.Ordinal))
                plan.Add((e, Target.Ide, name[IdePrefix.Length..]));
            else if (name.StartsWith(rtPrefix, StringComparison.Ordinal))
            {
                var rel = name[rtPrefix.Length..];
                if (!runtimeInstalled) plan.Add((e, Target.Runtime, rel));
                else if (rel == RuntimeIntegrityManifest) plan.Add((e, Target.RuntimeManifestOnly, rel));
            }
            else if (name.StartsWith(pfPrefix, StringComparison.Ordinal))
                plan.Add((e, Target.Prefabs, name[pfPrefix.Length..]));
            else
                Log($"Ignorado (fora do layout esperado): {name}");
        }
        return plan;
    }

    // Mesmo nome que RuntimeIntegrity.ManifestName (aqui como literal para o teste no PC).
    const string RuntimeIntegrityManifest = "gmide-runtime-integrity.json";

    public void Extract(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var info = ReadInfo(zip);
        if (info == null)
            Log("package.json ausente: extraindo sem conferir SHA-256");
        else
        {
            Log($"Pacote: IDE {info.IdeVersion}, runtime {info.RuntimeVersion}, {info.Files.Count} arquivos, {info.TotalBytes / 1048576} MB");
            if (info.RuntimeFolder != null && info.RuntimeFolder != RuntimeFolder)
                Log($"AVISO: o pacote traz {info.RuntimeFolder}, o app espera {RuntimeFolder}; o runtime sera ignorado");
            foreach (var name in info.Files.Keys)
                if (zip.GetEntry(name) == null)
                    throw new InvalidDataException("Pacote incompleto: falta " + name);
        }
        if (zip.GetEntry(IdePrefix + "IDE.dll") == null)
            throw new InvalidDataException("O pacote nao contem ide/IDE.dll");

        var plan = Plan(zip);
        long total = Math.Max(1, plan.Sum(p => p.Entry.Length)), done = 0;
        int lastPct = -1, lastLogged = -1;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool hasRuntime = plan.Any(p => p.Target == Target.Runtime);
        bool hasPrefabs = plan.Any(p => p.Target == Target.Prefabs);

        string ideTmp = IdeDir + TmpSuffix, rtTmp = RuntimeDir + TmpSuffix, pfTmp = PrefabsDir + TmpSuffix;
        foreach (var t in new[] { ideTmp, rtTmp, pfTmp })
            if (Directory.Exists(t)) Directory.Delete(t, true);

        var buf = new byte[1 << 20];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (entry, target, rel) in plan)
        {
            var root = target switch
            {
                Target.Ide => ideTmp,
                Target.Runtime => rtTmp,
                Target.Prefabs => pfTmp,
                _ => RuntimeDir,
            };
            var dst = Path.GetFullPath(Path.Combine(root, rel));
            if (!dst.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Caminho fora do destino: " + entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            // Manifesto do runtime ja instalado: grava ao lado e troca no fim (sem meio-arquivo).
            var write = target == Target.RuntimeManifestOnly ? dst + TmpSuffix : dst;

            using (var input = entry.Open())
            using (var output = new FileStream(write, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    output.Write(buf, 0, n);
                    hasher.AppendData(buf, 0, n);
                    done += n;
                    int pct = (int)(done * 100 / total);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        Progress?.Invoke(pct);
                        if (pct / 5 != lastLogged)
                        {
                            lastLogged = pct / 5;
                            Log($"  {pct}% ({done / 1048576}/{total / 1048576} MB, {sw.Elapsed.TotalSeconds:F0}s)");
                        }
                    }
                }
            }
            var hash = Convert.ToHexString(hasher.GetHashAndReset());
            if (info != null && info.Files.TryGetValue(entry.FullName, out var exp) && exp.Sha256 != hash)
                throw new InvalidDataException($"SHA-256 diferente em {entry.FullName} (pacote corrompido? copie de novo)");
            try { File.SetLastWriteTime(write, entry.LastWriteTime.DateTime); } catch { }
            if (target == Target.RuntimeManifestOnly) File.Move(write, dst, true);
        }

        if (!File.Exists(Path.Combine(ideTmp, "IDE.dll")))
            throw new InvalidDataException("Extracao sem IDE.dll");

        // Renomeia: runtime, Prefabs e por ultimo a IDE (marcador de instalacao completa).
        if (hasRuntime) Replace(rtTmp, RuntimeDir);
        if (hasPrefabs) MergeInto(pfTmp, PrefabsDir);
        Replace(ideTmp, IdeDir);
        Log($"Pacote extraido: {plan.Count} arquivos ({done / 1048576} MB) em {sw.Elapsed.TotalSeconds:F1}s" +
            (hasRuntime ? "" : " (runtime ja instalado: so o manifesto foi atualizado)"));
    }

    static void Replace(string tmp, string dest)
    {
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Directory.Move(tmp, dest);
    }

    // Prefabs pode ter arquivos do usuario/de outro runtime: sobrepoe sem apagar o resto.
    static void MergeInto(string tmp, string dest)
    {
        if (!Directory.Exists(dest)) { Replace(tmp, dest); return; }
        foreach (var f in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories))
        {
            var d = Path.Combine(dest, Path.GetRelativePath(tmp, f));
            Directory.CreateDirectory(Path.GetDirectoryName(d)!);
            File.Move(f, d, true);
        }
        Directory.Delete(tmp, true);
    }
}

#if ANDROID
public static class PackageImport
{
    const string Tag = "GmIde.Pkg";
    public const string FileName = "gmide-package.zip";
    public const string BundledAsset = "bootstrap/" + FileName;
    public static bool HasBundledPackage =>
        (MainApplication.Instance.Assets!.List("bootstrap") ?? Array.Empty<string>()).Contains(FileName);
    public const string DefaultPath = "/storage/emulated/0/Download/" + FileName;
    const string AdbStagingIde = "/storage/emulated/0/Download/gmide-ide/IDE.dll";
    // Mesmo valor de RuntimeImport.ProgramDirName (${program_dir_name}).
    const string ProgramDirName = "GameMakerStudio2-LTS2026";
    const string Title = "GM IDE Android";

    /// ${system_directory} antes do MacroExpansion (ver comentario no topo).
    public static string SystemDirectory =>
        PackageExtractor.SystemDirectoryFor(MainApplication.Instance.GetExternalFilesDir(null)!.AbsolutePath);

    /// Instala a IDE (e o runtime) do gmide-package.zip se ideDir/IDE.dll faltar.
    /// true = IDE pronta em ideDir. false = nada instalado (o Launcher segue para o ImportIde do adb).
    public static bool TryInstall(string ideDir)
    {
        if (File.Exists(Path.Combine(ideDir, "IDE.dll"))) return true;
        string? cacheCopy = null;
        bool bundled = false;
        try
        {
            bundled = HasBundledPackage;
            Stream? src = bundled ? OpenBundled() : null;
            if (!bundled && !Saf.HasAllFilesAccess && OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                MessageBox(Info, "Primeira execucao: o app vai instalar a IDE a partir de\n" +
                                 "Download/" + FileName + ".\n\n" +
                                 "Na proxima tela, ative \"Permitir acesso para gerenciar todos os arquivos\" " +
                                 "e volte ao app.");
                Saf.EnsureAllFilesAccess();
            }

            src ??= OpenDefault();
            if (src == null)
            {
                if (File.Exists(AdbStagingIde))
                {
                    Log.Info(Tag, "Sem pacote, mas a pasta do adb existe: o ImportIde cuida disso");
                    return false;
                }
                MessageBox(Info, $"Nao encontrei Download/{FileName}.\n\n" +
                                 "Escolha o arquivo gmide-package.zip no seletor a seguir " +
                                 "(copie-o para o celular pelo cabo USB ou pela nuvem).");
                src = Pick(out cacheCopy);
                if (src == null)
                {
                    MessageBox(Error, "Nenhum pacote escolhido. Copie gmide-package.zip para a pasta " +
                                      "Download do celular e abra o app de novo.");
                    return false;
                }
            }

            using (src)
            {
                var ex = new PackageExtractor
                {
                    IdeDir = ideDir,
                    RuntimeDir = Path.Combine(SystemDirectory, "Cache", "runtimes", RuntimeImport.RuntimeFolder),
                    PrefabsDir = Path.Combine(SystemDirectory, "Prefabs"),
                    RuntimeFolder = RuntimeImport.RuntimeFolder,
                    Log = m => Log.Info(Tag, m),
                };
                Log.Info(Tag, $"IDE -> {ex.IdeDir}; runtime -> {ex.RuntimeDir}; Prefabs -> {ex.PrefabsDir}");

                using (var probe = new ZipArchive(src, ZipArchiveMode.Read, leaveOpen: true))
                {
                    long need = ex.BytesNeeded(probe) + (64L << 20);
                    long free = Math.Min(FreeBytes(Path.GetDirectoryName(ideDir)!),
                                         FreeBytes(MainApplication.Instance.GetExternalFilesDir(null)!.AbsolutePath));
                    Log.Info(Tag, $"Espaco: precisa ~{need / 1048576} MB, livre {free / 1048576} MB");
                    if (free < need)
                    {
                        MessageBox(Error, $"Espaco insuficiente: a instalacao precisa de ~{need / 1048576} MB " +
                                          $"e ha {free / 1048576} MB livres.");
                        return false;
                    }
                }
                src.Position = 0;

                ShowToast("Instalando a IDE do GameMaker... (0%)", true);
                int lastToast = 0;
                ex.Progress = pct =>
                {
                    if (pct >= lastToast + 10) { lastToast = pct - pct % 10; ShowToast($"Instalando a IDE... {lastToast}%", true); }
                };
                ex.Extract(src);
            }
            ShowToast("IDE instalada. Iniciando...", false);
            Log.Info(Tag, bundled ? "IDE/runtime instalados do APK; nenhum ZIP externo necessario."
                                  : "Pacote instalado; o zip pode ser apagado: " + DefaultPath);
            return true;
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Instalacao pelo pacote falhou: " + e);
            MessageBox(Error, "Falha ao instalar a IDE pelo pacote:\n" + e.Message +
                              (bundled ? "\n\nLibere espaco se necessario e reabra o app para tentar novamente. " +
                                         "Se a falha persistir, reinstale o APK por cima, sem apagar os dados."
                                       : "\n\nCopie o gmide-package.zip de novo e reabra o app."));
            return false;
        }
        finally
        {
            if (cacheCopy != null) try { File.Delete(cacheCopy); } catch { }
        }
    }

    static Stream OpenBundled()
    {
        var assets = MainApplication.Instance.Assets!;
        // Asset ausente permite o fluxo legado. Asset presente mas invalido deve
        // produzir erro explicito, sem mandar o usuario procurar um ZIP externo.
        using var asset = assets.OpenFd(BundledAsset);
        using var duplicate = asset.ParcelFileDescriptor!.Dup()
            ?? throw new IOException("Nao foi possivel abrir o descritor do pacote embutido.");
        var file = new FileStream(new SafeFileHandle((IntPtr)duplicate.DetachFd(), true), FileAccess.Read, 1 << 16);
        try
        {
            var stream = new PackageAssetStream(file, asset.StartOffset, asset.Length);
            Log.Info(Tag, $"Pacote embutido no APK: {asset.Length / 1048576} MB; leitura direta sem copia.");
            return stream;
        }
        catch { file.Dispose(); throw; }
    }

    static Stream? OpenDefault()
    {
        try
        {
            if (!File.Exists(DefaultPath))
            {
                Log.Info(Tag, $"{DefaultPath} nao encontrado (ou sem permissao 'Todos os arquivos')");
                return null;
            }
            Log.Info(Tag, "Pacote: " + DefaultPath);
            return new FileStream(DefaultPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        }
        catch (Exception e)
        {
            Log.Warn(Tag, $"Nao foi possivel abrir {DefaultPath}: {e.Message}");
            return null;
        }
    }

    // Seletor do sistema. ZipArchive precisa de Stream com Seek: caminho real (com a
    // permissao), descritor do provedor (arquivo local) ou, no ultimo caso (ex.: Google
    // Drive, que devolve pipe), copia para o cache do app.
    static Stream? Pick(out string? cacheCopy)
    {
        cacheCopy = null;
        var i = new Intent(Intent.ActionOpenDocument);
        i.AddCategory(Intent.CategoryOpenable);
        i.SetType("*/*");
        i.PutExtra(Intent.ExtraMimeTypes, new[] { "application/zip", "application/x-zip-compressed",
                                                   "application/x-zip", "application/octet-stream" });
        i.AddFlags(ActivityFlags.GrantReadUriPermission);
        Saf.PutInitialUri(i, "/storage/emulated/0/Download");
        var r = Saf.Run(i);
        var uri = r?.Data;
        if (uri == null) { Log.Info(Tag, "Seletor cancelado"); return null; }
        Log.Info(Tag, "Escolhido: " + uri);

        var path = Saf.UsablePath(uri, false);
        if (path != null)
        {
            Log.Info(Tag, "Pacote (caminho real): " + path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        }

        var resolver = MainApplication.Instance.ContentResolver!;
        try
        {
            var pfd = resolver.OpenFileDescriptor(uri, "r");
            if (pfd != null)
            {
                var fs = new FileStream(new SafeFileHandle((IntPtr)pfd.DetachFd(), true), FileAccess.Read, 1 << 16);
                pfd.Dispose();
                if (fs.CanSeek) { Log.Info(Tag, "Pacote via descritor do provedor"); return fs; }
                fs.Dispose();
            }
        }
        catch (Exception e) { Log.Warn(Tag, "Descritor do provedor: " + e.Message); }

        cacheCopy = Path.Combine(MainApplication.Instance.CacheDir!.AbsolutePath, FileName);
        Log.Info(Tag, "Copiando o pacote para " + cacheCopy);
        ShowToast("Copiando o pacote...", true);
        Saf.CopyUriToFile(uri, cacheCopy);
        return new FileStream(cacheCopy, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
    }

    static long FreeBytes(string dir)
    {
        try { return new StatFs(dir).AvailableBytes; }
        catch (Exception e) { Log.Warn(Tag, $"StatFs {dir}: {e.Message}"); return long.MaxValue; }
    }

    // ---- Avisos na tela -----------------------------------------------------

    static Toast? _toast;
    static Handler? _ui;

    /// Toast postado na thread de UI (a extracao roda na thread do SDL).
    static void ShowToast(string text, bool longDuration)
    {
        try
        {
            _ui ??= new Handler(Looper.MainLooper!);
            _ui.Post(() =>
            {
                try
                {
                    _toast?.Cancel();
                    _toast = Toast.MakeText(MainApplication.Instance, text,
                                            longDuration ? ToastLength.Long : ToastLength.Short);
                    _toast!.Show();
                }
                catch (Exception e) { Log.Warn(Tag, "Toast: " + e.Message); }
            });
        }
        catch (Exception e) { Log.Warn(Tag, "Toast: " + e.Message); }
    }

    const uint Error = 0x10, Info = 0x40;   // SDL_MESSAGEBOX_ERROR / _INFORMATION

    [DllImport("SDL2", EntryPoint = "SDL_ShowSimpleMessageBox")]
    static extern int SDL_ShowSimpleMessageBox(uint flags,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message, IntPtr window);

    /// Caixa modal do SDL (funciona antes do SDL_Init no Android; bloqueia so a thread do SDL
    /// ate o OK). Sem SDL, cai para Toast.
    static void MessageBox(uint flags, string message)
    {
        Log.Info(Tag, "Aviso: " + message.Replace('\n', ' '));
        try
        {
            if (SDL_ShowSimpleMessageBox(flags, Title, message, IntPtr.Zero) == 0) return;
        }
        catch (Exception e) { Log.Warn(Tag, "SDL_ShowSimpleMessageBox: " + e.Message); }
        ShowToast(message, true);
    }
}
#endif
