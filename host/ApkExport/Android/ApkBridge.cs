using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Android.Runtime;
using Android.Util;

namespace GmIde;

/// <summary>
/// Lado do processo :build do "Gerar APK" (docs/apk-export.md).
///
/// O Igor ("Android Package") compila o jogo (GMAssetCompiler /m=android -> game.droid), expande
/// o projeto Gradle do runner em ${project_cache}/android/{config}/{pacote} e chama
///     Command.ExecuteCommandSync(".../gradle/gradlew", " build assembleRelease -x lint", ...)
/// O patch do Igor (IgorPatches.PatchAndroid) troca essa chamada por RunGradle: em vez do Gradle,
/// o ApkBuilder monta o APK a partir do modelo pre-compilado e grava onde o Gradle gravaria
/// ({modulo}/build/outputs/apk/release/{pacote}-release.apk); o Igor copia de la para o arquivo
/// escolhido. Na exportacao local, as escolhas do popup entram neste unico empacotamento.
/// Mensagens vao para o Console do Igor (janela Output da IDE).
/// </summary>
public static class ApkBridge
{
    const string Tag = "GmIde.Apk";
    const string TemplateAsset = "apk-template/template.apk";
    const string TemplateJsonAsset = "apk-template/template.json";

    public static string ReadLocalKeystorePassword(bool aliasPassword)
    {
        if (IgorPatches.IsLocalAndroidRun()) return "";
        string optionsFile = Environment.GetEnvironmentVariable("GMIDE_ANDROID_EXPORT_OPTIONS")
            ?? throw new InvalidOperationException("Configuracao da exportacao local ausente.");
        string buildFile = Environment.GetEnvironmentVariable("GMIDE_ANDROID_EXPORT_BUILD")
            ?? throw new InvalidOperationException("Snapshot do build local ausente.");
        string selected = JsonNode.Parse(File.ReadAllText(optionsFile))?["signing_keystore"]?.GetValue<string>()
            ?? throw new InvalidDataException("Keystore selecionado ausente na exportacao.");
        return ApkKeystoreCredentials.ReadPassword(buildFile, selected, AndroidSigning.DefaultKeystore, aliasPassword).Replace("$", "\\$");
    }

    // Mesma assinatura do Igor.Command.ExecuteCommandSync; o YYDictionary chega como object.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static int RunGradle(string command, string args, bool failOnError, string cwd, object env,
        bool asAdmin, string timingKey, bool quiet, string logPid)
    {
        args ??= "";
        if (args.Contains("--stop")) return 0;
        void Out(string s) { Console.WriteLine(s); Log.Info(Tag, s); }
        void Err(string s) { Console.Error.WriteLine(s); Log.Error(Tag, s); }
        try
        {
            if (args.Contains("bundleRelease"))
            {
                Err("[GMIDE] Android App Bundle (.aab) nao e suportado no aparelho; escolha um arquivo .apk.");
                return 1;
            }
            var root = string.IsNullOrEmpty(cwd) ? Path.GetDirectoryName(Path.GetDirectoryName(command))! : cwd;
            var module = FindModule(root) ?? throw new DirectoryNotFoundException($"modulo Gradle nao encontrado em {root}");
            var moduleName = Path.GetFileName(module);
            if (IgorPatches.IsLocalAndroidRun())
            {
                var previewOutDir = Path.Combine(module, "build", "outputs", "apk", "release");
                Directory.CreateDirectory(previewOutDir);
                string output = Path.Combine(previewOutDir, moduleName + "-release.apk");
                LocalGameRun.CreateDataZip(module, output);
                return 0;
            }
            if (Directory.Exists(Path.Combine(module, "src", "main", "cpp")) || File.Exists(Path.Combine(module, "CMakeLists.txt")))
            {
                Err("[GMIDE] YYC no Android precisa do NDK/CMake e nao funciona no aparelho; use o runtime VM.");
                return 1;
            }
            var ctx = global::Android.App.Application.Context;
            var (tplApk, tpl) = EnsureTemplate(ctx);
            var rt = new DirectoryInfo(BuildBridge.RuntimeDir).Name;
            if (!string.Equals(rt, tpl.Runtime, StringComparison.OrdinalIgnoreCase))
                Out($"[GMIDE] Aviso: modelo do runner feito com {tpl.Runtime}, runtime ativo {rt}. Se o jogo nao abrir, gere o modelo de novo (tools/apk-template/make-template.py).");

            JsonNode? choices = null;
            bool localExport = Environment.GetEnvironmentVariable("GMIDE_LOCAL_ANDROID_EXPORT") == "1";
            if (localExport)
            {
                string options = Environment.GetEnvironmentVariable("GMIDE_ANDROID_EXPORT_OPTIONS")
                    ?? throw new InvalidOperationException("Configuracao da exportacao local ausente.");
                choices = JsonNode.Parse(File.ReadAllText(options)) ?? throw new InvalidDataException("Configuracao da exportacao invalida.");
            }
            // Keystore das Preferencias (Platform Settings > Android > Keystore) quando configurado;
            // somente a ausencia de configuracao permite criar a chave padrao.
            var ks = ApkKeystoreSelection.Resolve(Environment.GetEnvironmentVariable("YYAndroidKeyStoreFile"), AndroidSigning.EnsureDefault);
            if (localExport && choices?["signing_keystore"]?.GetValue<string>() is { Length: > 0 } shown &&
                !ApkKeystoreSelection.SamePath(ks, shown))
                throw new InvalidOperationException("O keystore das Preferencias mudou desde a abertura da exportacao. Reabra a janela para confirmar a chave usada.");
            string? Unesc(string name) => Environment.GetEnvironmentVariable(name)?.Replace("\\$", "$");
            IApkSigningKey key;
            try
            {
                key = AndroidSigning.Load(ks, Unesc("YYAndroidKeyStoreFilePassword"), Environment.GetEnvironmentVariable("YYAndroidKeyAlias"),
                    Unesc("YYAndroidKeyPassword"));
            }
            catch (Exception e) when (!AndroidSigning.IsDefault(ks))
            {
                throw new InvalidOperationException($"Nao foi possivel abrir o keystore das Preferencias ({Path.GetFileName(ks)}): {e.Message}. " +
                    "Confira alias e senhas em Preferencias > Platform Settings > Android > Keystore.", e);
            }

            var outDir = Path.Combine(module, "build", "outputs", "apk", "release");
            Directory.CreateDirectory(outDir);
            var outApk = Path.Combine(outDir, moduleName + "-release.apk");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Out($"[GMIDE] Montando o APK sem Gradle (modelo {tpl.Runtime}, keystore {ks})");
            ApkBuilder.Build(new ApkBuildRequest
            {
                ModuleDir = module, TemplateApk = tplApk, Template = tpl, OutApk = outApk, Key = key,
                Overrides = choices == null ? null : new ApkOverrides
                {
                    DisplayName = choices["display_name"]!.GetValue<string>(),
                    PackageName = choices["package"]!.GetValue<string>(),
                    VersionName = choices["version"]!.GetValue<string>(),
                },
                IconFile = choices?["icon_path"]?.GetValue<string>(),
                Log = s => Out("[GMIDE] " + s),
            });
            // O YYC/CreateTargetExe procura o nome "universal"; deixa os dois.
            File.Copy(outApk, Path.Combine(outDir, moduleName + "-universal-release.apk"), overwrite: true);
            Out($"[GMIDE] APK pronto em {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }
        catch (Exception e)
        {
            Err("[GMIDE] Gerar APK falhou: " + e.Message);
            Log.Error(Tag, e.ToString());
            return 1;
        }
    }

    internal static string? FindModule(string root)
    {
        var settings = Path.Combine(root, "settings.gradle");
        if (File.Exists(settings))
        {
            var m = Regex.Match(File.ReadAllText(settings), @"include\s+'\:([^']+)'");
            if (m.Success && Directory.Exists(Path.Combine(root, m.Groups[1].Value))) return Path.Combine(root, m.Groups[1].Value);
        }
        return Directory.EnumerateDirectories(root)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "src", "main", "AndroidManifest.xml")));
    }

    /// <summary>Extrai o modelo dos assets do APK da IDE para files/apk-template (uma vez por versao).</summary>
    internal static (string apk, ApkTemplateInfo info) EnsureTemplate(global::Android.Content.Context ctx)
    {
        var dir = Path.Combine(ctx.FilesDir!.AbsolutePath, "apk-template");
        Directory.CreateDirectory(dir);
        string json;
        using (var s = ctx.Assets!.Open(TemplateJsonAsset)) using (var r = new StreamReader(s)) json = r.ReadToEnd();
        var jsonPath = Path.Combine(dir, "template.json");
        var apkPath = Path.Combine(dir, "template.apk");
        if (!File.Exists(apkPath) || !File.Exists(jsonPath) || File.ReadAllText(jsonPath) != json)
        {
            var tmp = apkPath + ".tmp";
            using (var s = ctx.Assets!.Open(TemplateAsset)) using (var f = File.Create(tmp)) s.CopyTo(f);
            File.Move(tmp, apkPath, overwrite: true);
            File.WriteAllText(jsonPath, json);
            Log.Info(Tag, $"Modelo do runner extraido para {dir}");
        }
        return (apkPath, JsonSerializer.Deserialize<ApkTemplateInfo>(json)!);
    }

    internal static string? CallInstaller(global::Android.Content.Context ctx, string apk)
    {
        // JNIEnv.FindClass devolve referencia GLOBAL (DeleteLocalRef nela aborta o processo).
        IntPtr cls = JNIEnv.FindClass("io/gmide/ApkInstaller");
        try
        {
            var m = JNIEnv.GetStaticMethodID(cls, "install", "(Landroid/content/Context;Ljava/lang/String;)Ljava/lang/String;");
            IntPtr s = JNIEnv.NewString(apk);
            try
            {
                IntPtr r = JNIEnv.CallStaticObjectMethod(cls, m, new JValue(ctx), new JValue(s));
                return JNIEnv.GetString(r, JniHandleOwnership.TransferLocalRef);
            }
            finally { JNIEnv.DeleteLocalRef(s); }
        }
        finally { JNIEnv.DeleteGlobalRef(cls); }
    }
}
