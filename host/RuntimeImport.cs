using System.Text.Json;
using System.Text.Json.Nodes;
using Android.Util;
using MacroExpansion = YoYoStudio.Core.Utils.MacroExpansion;

namespace GmIde;

// Importa o runtime do GameMaker (runtime-2026.0.0.23, ~400 MB) e os Prefabs para o lugar
// em que a IDE os procura no aparelho, e o registra em runtime.json.
//
// Onde a IDE procura (Utils.dll/IDE.dll + defaults/default_macros.json da instalacao):
//   system_directory       = ${CommonApplicationData}/${program_dir_name}   (default_macros.json)
//   CommonApplicationData  = ${LocalApplicationData} no Linux (MacroExpansion.Initialise, OSType 2)
//   system_cache_directory = ${system_directory}/Cache
//   runtimeBaseLocation    = ${system_cache_directory}/runtimes             (DefaultMacros)
//   runtime ativo          = ${system_directory}/runtime.json: {"active": ver, ver: "<pasta>&<feed>"}
//                            (RuntimeEntry.MatchRuntimeJSON; instalado = pasta + receipt.json)
//   prefabs                = ${CommonApplicationData}/${program_dir_name}/Prefabs (IDE.cs, ProjectInfo.Init)
// No app, LocalApplicationData = $XDG_DATA_HOME (Launcher.PrepareFolders), entao o destino e
// <files externos>/home/.local/share/GameMakerStudio2-LTS2026/Cache/runtimes/runtime-2026.0.0.23.
//
// Mesmo padrao da IDE (Launcher.ImportIde): o adb nao consegue gravar arquivos legiveis pelo
// app em Android/data, entao o deploy.sh empurra para a pasta publica de passagem
//   /sdcard/Download/gmide-runtime/{runtime-2026.0.0.23,Prefabs}
// e grava runtime-2026.0.0.23/.gmide-complete por ultimo. A copia acontece uma vez.
static class RuntimeImport
{
    const string Tag = "GmIde.Build";
    public const string RuntimeVersion = "2026.0.0.23";
    public const string RuntimeFolder = "runtime-" + RuntimeVersion;
    public const string StagingRoot = "/storage/emulated/0/Download/gmide-runtime";
    public const string CompleteMarker = ".gmide-complete";
    const string ProgramDirName = "GameMakerStudio2-LTS2026";
    const string FeedUri = "https://gms.yoyogames.com/Zeus-Runtime-LTS2026.rss";

    /// <summary>${system_directory} como a IDE vai calcular. Requer MacroExpansion.Initialise (Launcher.BootIde).</summary>
    public static string SystemDirectory()
    {
        try
        {
            if (MacroExpansion.IsReady())
            {
                if (MacroExpansion.IsDefined("system_directory"))
                {
                    var s = MacroExpansion.Expand("${system_directory}");
                    if (!string.IsNullOrEmpty(s) && !s.Contains("${")) return s;
                }
                var c = MacroExpansion.Expand("${CommonApplicationData}");
                if (!string.IsNullOrEmpty(c) && !c.Contains("${")) return Path.Combine(c, ProgramDirName);
            }
        }
        catch (Exception e) { Log.Warn(Tag, "MacroExpansion: " + e.Message); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProgramDirName);
    }

    public static string DefaultRuntimeDir() => Path.Combine(SystemDirectory(), "Cache", "runtimes", RuntimeFolder);

    /// <summary>Importa (se faltar) e registra o runtime. Devolve a pasta do runtime, ou null se nao ha runtime.</summary>
    public static string? Run()
    {
        var sys = SystemDirectory();
        var dest = Path.Combine(sys, "Cache", "runtimes", RuntimeFolder);
        var staged = Path.Combine(StagingRoot, RuntimeFolder);
        bool stagedOk = File.Exists(Path.Combine(staged, CompleteMarker));
        Log.Info(Tag, $"Runtime: {dest} (passagem {(stagedOk ? "completa" : "ausente/incompleta")})");
        try
        {
            if (!File.Exists(Path.Combine(dest, "receipt.json")))
            {
                if (!stagedOk)
                {
                    Log.Error(Tag, $"Runtime nao instalado e {staged}/{CompleteMarker} nao existe " +
                                   "(ou falta a permissao 'Todos os arquivos'). Rode: bash deploy.sh. Run/Build ficam indisponiveis.");
                    return null;
                }
                CopyTree(staged, dest);
            }

            var stagedPrefabs = Path.Combine(StagingRoot, "Prefabs");
            if (stagedOk && Directory.Exists(stagedPrefabs))
                SyncMissing(stagedPrefabs, Path.Combine(sys, "Prefabs"));

            // O manifesto de assinaturas pode ser atualizado pelo deploy sem reimportar o runtime.
            var manifest = Path.Combine(staged, RuntimeIntegrity.ManifestName);
            if (File.Exists(manifest))
                File.Copy(manifest, Path.Combine(dest, RuntimeIntegrity.ManifestName), true);

            EnsureAndroidModule(dest);
            RegisterRuntime(sys, dest);
            return dest;
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Importacao do runtime falhou: " + e);
            return Directory.Exists(dest) ? dest : null;
        }
    }

    const string AndroidModuleAsset = "runtime-modules/android.zip";
    const string AndroidModuleMarker = ".gmide-module";
    const string BaseOptionsPrefix = "BaseProject/options/android/";

    /// <summary>
    /// "Gerar APK" (docs/apk-export.md) precisa do modulo android do runtime (runner, libyoyo.so,
    /// projeto Gradle). O runtime importado no aparelho pode ser anterior a ele, e baixar o modulo
    /// pela IDE exige login. O APK traz o modulo (tools/apk-template/make-template.py ->
    /// runtime-android.zip): extrai android/ e acrescenta "android" ao receipt.json, que e o que a
    /// IDE le para saber os modulos instalados. Refaz se o asset mudar.
    /// </summary>
    static void EnsureAndroidModule(string dest)
    {
        try
        {
            byte[] zip;
            try
            {
                using var s = MainApplication.Instance.Assets!.Open(AndroidModuleAsset);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                zip = ms.ToArray();
            }
            catch (Exception) { return; } // APK sem o modulo
            var stamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip));
            var androidDir = Path.Combine(dest, "android");
            var marker = Path.Combine(androidDir, AndroidModuleMarker);
            if (File.Exists(marker) && File.ReadAllText(marker) == stamp) return;

            var tmp = androidDir + ".importando";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            JsonObject? receiptEntry = null;
            int n = 0;
            using (var za = new System.IO.Compression.ZipArchive(new MemoryStream(zip)))
            {
                foreach (var e in za.Entries)
                {
                    if (e.FullName == "receipt-android.json")
                    {
                        using var r = new StreamReader(e.Open());
                        receiptEntry = JsonNode.Parse(r.ReadToEnd())?["android"]?.DeepClone() as JsonObject;
                        continue;
                    }
                    if (e.FullName.EndsWith('/')) continue;
                    if (e.FullName.StartsWith(BaseOptionsPrefix, StringComparison.Ordinal))
                    {
                        // Splash/icones padrao do modulo: vao direto para <runtime>/BaseProject/options/android.
                        var baseTarget = Path.GetFullPath(Path.Combine(dest, e.FullName));
                        var baseRoot = Path.GetFullPath(Path.Combine(dest, BaseOptionsPrefix));
                        if (!baseTarget.StartsWith(baseRoot, StringComparison.Ordinal)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(baseTarget)!);
                        using (var src = e.Open()) using (var f = File.Create(baseTarget)) src.CopyTo(f);
                        n++;
                        continue;
                    }
                    if (!e.FullName.StartsWith("android/", StringComparison.Ordinal)) continue;
                    var rel = e.FullName.Substring("android/".Length);
                    var target = Path.GetFullPath(Path.Combine(tmp, rel));
                    if (!target.StartsWith(Path.GetFullPath(tmp) + Path.DirectorySeparatorChar)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var src = e.Open()) using (var f = File.Create(target)) src.CopyTo(f);
                    n++;
                }
            }
            File.WriteAllText(Path.Combine(tmp, AndroidModuleMarker), stamp);
            if (Directory.Exists(androidDir)) Directory.Delete(androidDir, true);
            Directory.Move(tmp, androidDir);

            if (receiptEntry != null)
            {
                var receiptPath = Path.Combine(dest, "receipt.json");
                var receipt = File.Exists(receiptPath) ? JsonNode.Parse(File.ReadAllText(receiptPath)) as JsonObject ?? new() : new JsonObject();
                receipt["android"] = receiptEntry;
                File.WriteAllText(receiptPath, receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            Log.Info(Tag, $"Modulo android do runtime instalado do APK: {n} arquivos em {androidDir}");
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Modulo android do runtime: " + e);
        }
    }

    static void CopyTree(string src, string dest)
    {
        Log.Info(Tag, $"Importando o runtime de {src} para {dest} ...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tmp = dest + ".importando";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        long bytes = 0; int count = 0;
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            if (rel == CompleteMarker) continue;
            var dst = Path.Combine(tmp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
            bytes += new FileInfo(dst).Length;
            if (++count % 2000 == 0) Log.Info(Tag, $"  ... {count} arquivos ({bytes / 1048576} MB)");
        }
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Directory.Move(tmp, dest);
        Log.Info(Tag, $"Runtime importado: {count} arquivos ({bytes / 1048576} MB) em {sw.Elapsed.TotalSeconds:F1}s");
    }

    static void SyncMissing(string src, string dest)
    {
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(dest, Path.GetRelativePath(src, f));
            var di = new FileInfo(dst);
            if (di.Exists && di.Length == new FileInfo(f).Length) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
            n++;
        }
        if (n > 0) Log.Info(Tag, $"Prefabs: {n} arquivos copiados para {dest}");
    }

    // Mesmo formato que RuntimeEntry.SaveFeedData grava no PC:
    //   { "active": "2026.0.0.23", "2026.0.0.23": "<pasta>&https://gms.yoyogames.com/Zeus-Runtime-LTS2026.rss" }
    static void RegisterRuntime(string sys, string dest)
    {
        var path = Path.Combine(sys, "runtime.json");
        JsonObject obj;
        try { obj = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new(); }
        catch { obj = new(); }
        var entry = dest.Replace('\\', '/') + "&" + FeedUri;
        bool changed = obj[RuntimeVersion]?.GetValue<string>() != entry;
        obj[RuntimeVersion] = entry;
        var active = obj["active"]?.GetValue<string>();
        var activeLoc = active == null ? null : obj[active]?.GetValue<string>()?.Split('&')[0];
        if (active == null || activeLoc == null || !Directory.Exists(activeLoc))
        {
            obj["active"] = RuntimeVersion;
            changed = true;
        }
        if (!changed) return;
        Directory.CreateDirectory(sys);
        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log.Info(Tag, $"runtime.json: {RuntimeVersion} -> {dest} (ativo: {obj["active"]})");
    }
}
