using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Util;

namespace GmIde;

/// <summary>
/// Botao "Gerar APK" (docs/apk-export.md), lado da IDE (processo principal).
///
/// "apk" chega na thread da IDE (ShortcutBridge -> IdeSetup.OnInputObserved -> RunPending): ali
/// salvamos o projeto aberto, capturamos a configuracao Android VM e abrimos o popup.
/// Com o popup por cima, a IDE (SDL) fica
/// pausada, entao o resto nao passa mais pela thread dela: uma thread "sessao" fala com o popup
/// por JNI (ApkExportActivity.nextRequest / onStatus) e devolve progresso e resultado.
/// A sessao passa as escolhas ao Igor no processo :build, onde o APK e montado uma vez.
/// </summary>
static class ApkExport
{
    const string Tag = "GmIde.Apk";
    const string JavaClass = "io/gmide/ApkExportActivity";
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    static readonly object _lock = new();
    static readonly object _settingsLock = new();
    static readonly Queue<(string cmd, string arg)> _queue = new();
    static int _session;
    static int _popupOpen;

    sealed class PopupReservation(IDisposable buildReservation) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { buildReservation.Dispose(); }
            finally { Volatile.Write(ref _popupOpen, 0); }
        }
    }

    public static bool HasPending { get { lock (_lock) return _queue.Count > 0; } }

    /// <summary>Thread de UI (ShortcutBridge.Apply).</summary>
    public static void Request(string cmd, string arg)
    {
        lock (_lock) _queue.Enqueue((cmd, arg));
        Log.Info(Tag, $"Pedido \"{cmd}\" (executa no proximo evento da IDE)");
    }

    /// <summary>Thread da IDE.</summary>
    public static void RunPending()
    {
        while (true)
        {
            (string cmd, string arg) item;
            lock (_lock) { if (_queue.Count == 0) return; item = _queue.Dequeue(); }
            try
            {
                if (item.cmd == "apk") Open();
                else if (item.cmd == "apply-options") ApplyGameOptions(item.arg);
                else Log.Warn(Tag, $"Comando {item.cmd} ignorado (o popup fala direto com a sessao)");
            }
            catch (Exception e)
            {
                Log.Error(Tag, $"{item.cmd}: {e}");
                Toast("Gerar APK: " + e.Message);
            }
        }
    }

    /// <summary>O que a sessao precisa do projeto, lido na thread da IDE ao abrir o popup.</summary>
    sealed record Session(int Id, string ProjectName, string ProjectPath, string SigningPath, bool HasConfiguredKey, string? KeystoreError, AndroidGameCompile.Input Compile, AndroidGameCompile.Input WebCompile, IDisposable Reservation);

    // ------------------------------------------------------------------ popup

    static void Open()
    {
        // VM dispara dois callbacks de selecao no Build.dll; ambos devem consumir o clique,
        // mas somente o primeiro abre/salva uma sessao de exportacao.
        if (Interlocked.CompareExchange(ref _popupOpen, 1, 0) != 0) return;
        IDisposable reservation;
        try { reservation = new PopupReservation(AndroidGameCompile.Reserve()); }
        catch { Volatile.Write(ref _popupOpen, 0); throw; }
        try
        {
        var (project, _) = AndroidOptions();
        if (project == null) throw new InvalidOperationException("Abra um projeto antes de gerar o APK.");
        var androidOptions = AndroidGameOptions.Get(project, create: true)!;
        // Antes do SaveProjectIncremental abaixo: o compilador le a Game Options do disco.
        var filled = AndroidGameOptions.FillMissingBuildSettings(androidOptions, TemplateCompileSdk());
        if (filled.Count > 0) Log.Info(Tag, "Build Settings Android vazios preenchidos (modelo do runner): " + string.Join(", ", filled));
        string projectName = Prop<string>(project, "name") ?? "jogo";
        var pending = LoadLast(projectName)?["pending_game_options"];
        if (pending?["project_path"]?.GetValue<string>() != YoYoStudio.Core.Utils.MacroExpansion.Expand("${project_full_filename}")) pending = null;
        if (pending != null) ApplyGameOptions(pending.ToJsonString(), save: false);
        string legacyIcon = IconStorePath(projectName);
        if (File.Exists(legacyIcon)) ImportIcon(androidOptions, legacyIcon);
        YoYoStudio.IDE.SaveProjectIncremental((success, error) =>
        {
            if (!success) { reservation.Dispose(); Toast("Nao foi possivel salvar o projeto: " + error?.Message); return; }
            try
            {
                if (pending != null) ClearPendingOptions(projectName, pending.ToJsonString());
                if (File.Exists(legacyIcon)) File.Move(legacyIcon, legacyIcon + ".imported", overwrite: true);
                OpenSaved(reservation);
            }
            catch (Exception e) { reservation.Dispose(); Log.Error(Tag, e.ToString()); Toast("Gerar APK: " + e.Message); }
        });
        }
        catch { reservation.Dispose(); throw; }
    }

    static void OpenSaved(IDisposable reservation)
    {
        var (project, opts) = AndroidOptions();
        if (project == null) throw new InvalidOperationException("Abra um projeto antes de gerar o APK.");
        string projectName = Prop<string>(project, "name") ?? "jogo";
        var compile = AndroidGameCompile.Prepare();
        var webCompile = AndroidGameCompile.Prepare("operagx");
        string projectPath = YoYoStudio.Core.Utils.MacroExpansion.Expand("${project_full_filename}");
        // Falha ao ler as Preferencias nao impede o popup: ele abre, mostra o problema e o Gerar
        // devolve o erro (nunca cai na chave padrao, que nao atualizaria o jogo instalado).
        string? configuredKey = null, keystoreError = null;
        try { configuredKey = ApkKeystoreSelection.ReadConfiguredPath(compile.BuildFile); }
        catch (Exception e)
        {
            keystoreError = "Nao foi possivel identificar o keystore das Preferencias. " + e.Message;
            Log.Error(Tag, "Keystore das Preferencias: " + e);
        }
        string signingPath = keystoreError != null ? "" :
            string.IsNullOrWhiteSpace(configuredKey) ? AndroidSigning.DefaultKeystore
            : YoYoStudio.Core.Utils.MacroExpansion.Expand(configuredKey).Replace('\\', '/');

        // Nome, pacote e versao vem da Game Options Android (fonte unica; o popup grava de volta
        // em ApplyGameOptions). last.json guarda a saida e o diario temporario ate o projeto salvar.
        var last = LoadLast(projectName);
        string Last(string key, string fallback) =>
            last?[key] is JsonValue v && v.TryGetValue<string>(out var str) && str.Length > 0 ? str : fallback;
        string lastOutput = Last("output", Path.Combine(DownloadDir(), SafeFileName(projectName) + ".apk"));
        var game = ReadGameOptions(opts, projectName);
        string iconStore = Path.Combine(Path.GetDirectoryName(IconStorePath(projectName))!,
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(projectPath))) + ".img");
        string? projectIcon = Prop<string>(opts, "option_android_icon_xxxhdpi");
        if (!string.IsNullOrEmpty(projectIcon)) projectIcon = YoYoStudio.Core.Utils.MacroExpansion.Expand(projectIcon);

        int id = Interlocked.Increment(ref _session);
        var j = new JsonObject
        {
            ["session"] = id,
            ["project"] = projectName,
            ["display_name"] = game.DisplayName,
            ["package"] = game.Package,
            ["version"] = game.Version,
            ["output_dir"] = Path.GetDirectoryName(lastOutput) ?? DownloadDir(),
            ["file_name"] = Path.GetFileName(lastOutput),
            ["icon_store"] = iconStore,
            ["icon_path"] = File.Exists(projectIcon) ? projectIcon : null,
            ["keystore"] = keystoreError != null ? "nao identificado (erro nas Preferencias)" : signingPath,
            ["targets"] = new JsonObject
            {
                ["android"] = new JsonObject { ["display_name"] = game.DisplayName, ["version"] = game.Version, ["package"] = game.Package, ["file_name"] = Path.GetFileName(lastOutput), ["icon_path"] = projectIcon },
                ["web"] = ReadDesktopOptions(project, "web", projectName, game.Package, last),
                ["windows"] = ReadDesktopOptions(project, "windows", projectName, game.Package, last),
            },
        };

        var session = new Session(id, projectName, projectPath, signingPath, !string.IsNullOrWhiteSpace(configuredKey), keystoreError, compile, webCompile, reservation);

        var ctx = MainApplication.Instance;
        var intent = new Intent();
        intent.SetClassName(ctx.PackageName!, "io.gmide.ApkExportActivity");
        intent.PutExtra("json", j.ToJsonString());
        intent.AddFlags(ActivityFlags.NewTask);
        ctx.StartActivity(intent);
        new Thread(() => RunSession(session)) { IsBackground = true, Name = "gmide-apk-session" }.Start();
        Log.Info(Tag, "Popup Gerar APK aberto: " + j.ToJsonString());
    }

    /// <summary>Thread propria: espera pedidos do popup ate ele fechar (ou outro popup abrir).</summary>
    static void RunSession(Session s)
    {
        try
        {
            while (Volatile.Read(ref _session) == s.Id)
            {
                string? req = NextRequest(s.Id, 30000);
                if (req == null) continue;          // tempo esgotado: popup ainda aberto
                if (req == "close") break;
                if (req.StartsWith("build:", StringComparison.Ordinal)) Build(s, req.Substring(6));
            }
        }
        catch (Exception e) { Log.Error(Tag, "Sessao do popup: " + e); }
        finally { s.Reservation.Dispose(); }
        Log.Info(Tag, $"Sessao {s.Id} do popup encerrada");
    }

    // ------------------------------------------------------------------ build

    static void Build(Session s, string json)
    {
        string? telemetryTarget = null;
        var total = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var req = JsonNode.Parse(json)!.AsObject();
            string target = req["target"]?.GetValue<string>() ?? "android";
            if (target is not ("android" or "web" or "windows")) throw new ArgumentException("Destino de exportacao invalido.");
            telemetryTarget = target == "android" ? "apk" : target;
            string displayName = req["display_name"]!.GetValue<string>().Trim();
            string packageName = req["package"]!.GetValue<string>().Trim();
            string version = req["version"]!.GetValue<string>().Trim();
            string output = req["output"]!.GetValue<string>();
            string? icon = req["icon_path"]?.GetValue<string>();
            if (displayName.Length == 0) throw new ArgumentException("Informe o nome do app.");
            if (!ApkBuilder.IsValidPackage(packageName)) throw new ArgumentException("Nome do pacote invalido.");
            if (!string.IsNullOrEmpty(icon) && !File.Exists(icon)) throw new FileNotFoundException("A imagem do icone nao esta mais disponivel; escolha outra.");
            if (!string.IsNullOrEmpty(icon))
            {
                // O arquivo do picker pode ser substituido no proximo popup: o job e seu
                // registro pendente precisam de uma copia imutavel ate a IDE retomar.
                byte[] bytes = File.ReadAllBytes(icon);
                string snapshot = Path.Combine(Path.GetDirectoryName(IconStorePath(s.ProjectName))!,
                    "source-" + Convert.ToHexString(SHA256.HashData(bytes)) + ".img");
                File.WriteAllBytes(snapshot + ".tmp", bytes);
                File.Move(snapshot + ".tmp", snapshot, overwrite: true);
                icon = snapshot;
                req["icon_path"] = snapshot;
            }
            if (target == "android" && s.KeystoreError != null) throw new InvalidOperationException(s.KeystoreError);
            if (target == "android" && s.HasConfiguredKey) ApkKeystoreSelection.Resolve(s.SigningPath,
                () => throw new InvalidOperationException("A chave configurada nao pode ser substituida pela chave padrao."));

            // A Game Options so pode ser alterada na thread da IDE, que esta pausada com o popup
            // aberto: fica na fila e e aplicada (e salva) no proximo evento da IDE.
            var pending = new JsonObject
            {
                ["project"] = s.ProjectName, ["project_path"] = s.ProjectPath,
                ["display_name"] = displayName, ["package"] = packageName, ["version"] = version,
                ["icon_path"] = icon, ["target"] = target,
                ["window_width"] = req["window_width"]?.DeepClone(), ["window_height"] = req["window_height"]?.DeepClone(),
            };
            // Diario duravel: se o app fechar antes do proximo evento SDL, Open recupera e salva.
            var settings = LoadLast(s.ProjectName)?.AsObject() ?? new JsonObject();
            settings["project_path"] = s.ProjectPath;
            if (target == "android") settings["output"] = output;
            settings["targets"] ??= new JsonObject();
            settings["targets"]![target] = req.DeepClone();
            settings["pending_game_options"] = pending.DeepClone();
            SaveLast(s.ProjectName, settings);
            Request("apply-options", pending.ToJsonString());
            req["signing_keystore"] = s.SigningPath;

            Status("progress", target == "android" ? "Compilando o jogo para Android..." : "Compilando o jogo para WebAssembly...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? preview = null;
            if (target == "android") AndroidGameCompile.Run(s.Compile, req.ToJsonString(), line => Status("progress", line));
            else preview = WebGameCompile.Run(s.WebCompile, req.ToJsonString(), line => Status("progress", line));
            Log.Info(Tag, $"APK pronto em {sw.Elapsed.TotalSeconds:F1} s: {output}");
            Telemetry.Build(telemetryTarget, "ok", sw.ElapsedMilliseconds, 0);
            Telemetry.Event("export." + telemetryTarget, new() { ["ok"] = true, ["bytes"] = new FileInfo(output).Length, ["ms"] = sw.ElapsedMilliseconds });
            Status("done", output, new JsonObject
            {
                ["size"] = new FileInfo(output).Length,
                ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
                ["package"] = packageName,
                ["target"] = target, ["preview"] = preview,
            });
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Gerar APK: " + e);
            if (telemetryTarget != null)
            {
                // Erro de validacao do formulario nao e falha do exportador.
                bool userInput = e is ArgumentException or FileNotFoundException;
                Telemetry.Build(telemetryTarget, userInput ? "cancelled" : "failed", total.ElapsedMilliseconds, null,
                    e.GetType().FullName + ": " + e.Message + (userInput ? "" : "\n" + e.StackTrace));
            }
            Status("error", e.Message);
        }
    }

    // ------------------------------------------------------------------ JNI com o popup

    // JNIEnv.FindClass do .NET Android devolve referencia GLOBAL: guardada uma vez, nunca apagada
    // (DeleteLocalRef nela derruba o processo: "Attempt to delete global reference as local").
    static IntPtr _cls;
    static IntPtr Cls => _cls != IntPtr.Zero ? _cls : (_cls = JNIEnv.FindClass(JavaClass));

    static string? NextRequest(int session, long timeoutMs)
    {
        var m = JNIEnv.GetStaticMethodID(Cls, "nextRequest", "(IJ)Ljava/lang/String;");
        IntPtr r = JNIEnv.CallStaticObjectMethod(Cls, m, new JValue(session), new JValue(timeoutMs));
        return JNIEnv.GetString(r, JniHandleOwnership.TransferLocalRef);
    }

    static void Status(string state, string message, JsonObject? extra = null)
    {
        var j = extra ?? new JsonObject();
        j["state"] = state;
        j["message"] = message;
        try
        {
            var m = JNIEnv.GetStaticMethodID(Cls, "onStatus", "(Ljava/lang/String;)V");
            IntPtr str = JNIEnv.NewString(j.ToJsonString());
            try { JNIEnv.CallStaticVoidMethod(Cls, m, new JValue(str)); }
            finally { JNIEnv.DeleteLocalRef(str); }
        }
        catch (Exception e) { Log.Warn(Tag, "onStatus: " + e.Message); }
    }

    // ------------------------------------------------------------------ utilitarios

    static (object? project, object? opts) AndroidOptions()
    {
        var pi = Type.GetType("YoYoStudio.Resources.ProjectInfo, CoreResources");
        var project = pi?.GetField("Current", Any)?.GetValue(null);
        return (project, project == null ? null : AndroidGameOptions.Get(project));
    }

    sealed record GameOptionValues(string DisplayName, string Package, string Version);

    const string ProjectNameMacro = "${project_name}";

    static JsonObject ReadDesktopOptions(object project, string target, string projectName, string id, JsonNode? last)
    {
        var options = AndroidGameOptions.GetPlatform(project, target == "web" ? "GMOperaGXOptions" : "GMWindowsOptions");
        string name = Prop<string>(options, target == "web" ? "option_operagx_game_name" : "option_windows_display_name") ?? projectName;
        string version = Prop<string>(options, target == "web" ? "option_operagx_version" : "option_windows_version") ?? "1.0.0.0";
        var saved = last?["targets"]?[target];
        string projectFile = YoYoStudio.Core.Utils.MacroExpansion.Expand("${project_full_filename}");
        if (last?["project_path"]?.GetValue<string>() != projectFile) saved = null;
        var authored = ProjectExportSettings.Read(YoYoStudio.Core.Utils.MacroExpansion.Expand("${project_full_filename}"), target);
        string? nativeIcon = target == "windows" ? Prop<string>(options, "option_windows_icon") : null;
        if (!string.IsNullOrEmpty(nativeIcon)) nativeIcon = YoYoStudio.Core.Utils.MacroExpansion.Expand(nativeIcon);
        string? selectedIcon = File.Exists(nativeIcon) ? nativeIcon : authored["icon_path"]?.GetValue<string>() ?? saved?["icon_path"]?.GetValue<string>();
        if (File.Exists(selectedIcon))
        {
            string png = Path.Combine(Path.GetDirectoryName(IconStorePath(projectName))!,
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(YoYoStudio.Core.Utils.MacroExpansion.Expand("${project_full_filename}")))) + "-" + target + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            File.WriteAllBytes(png, ApkBuilder.RenderIconPng(selectedIcon!, 256));
            selectedIcon = png;
        }
        string? output = saved?["output"]?.GetValue<string>();
        return new JsonObject
        {
            ["display_name"] = string.IsNullOrWhiteSpace(name) ? projectName : name.Replace(ProjectNameMacro, projectName),
            ["version"] = string.IsNullOrWhiteSpace(version) ? "1.0.0.0" : version,
            ["package"] = authored["package"]?.GetValue<string>() ?? saved?["package"]?.GetValue<string>() ?? ProjectExportSettings.DefaultId(projectFile, projectName),
            ["file_name"] = output == null ? SafeFileName(projectName) + "-" + target + (target == "windows" ? ".exe" : ".zip")
                : Path.ChangeExtension(Path.GetFileName(output), target == "windows" ? ".exe" : ".zip"),
            ["icon_path"] = selectedIcon,
            ["window_width"] = authored["window_width"]?.GetValue<int>() ?? saved?["window_width"]?.GetValue<int>() ?? 1280,
            ["window_height"] = authored["window_height"]?.GetValue<int>() ?? saved?["window_height"]?.GetValue<int>() ?? 720,
        };
    }

    /// <summary>Valores Android da Game Options como o popup mostra (macro do nome resolvida).</summary>
    static GameOptionValues ReadGameOptions(object? opts, string projectName)
    {
        string Opt(string name, string fallback) => Prop<string>(opts, name) is { Length: > 0 } v ? v : fallback;
        return new GameOptionValues(
            Opt("option_android_display_name", ProjectNameMacro).Replace(ProjectNameMacro, projectName),
            string.Join(".",
                Opt("option_android_package_domain", "com"),
                Opt("option_android_package_company", "company"),
                Opt("option_android_package_product", SafePackagePart(projectName))),
            Opt("option_android_version", "1.0.0.0"));
    }

    /// <summary>
    /// Thread da IDE: grava na Game Options Android o que foi escolhido no popup e salva o projeto.
    /// So altera o que mudou; se o nome continua igual ao do projeto, a macro ${project_name} fica.
    /// </summary>
    static void ApplyGameOptions(string json, bool save = true)
    {
        var req = JsonNode.Parse(json)!.AsObject();
        var (project, opts) = AndroidOptions();
        string? projectName = Prop<string>(project, "name");
        string currentPath = YoYoStudio.Core.Utils.MacroExpansion.Expand("${project_full_filename}");
        if (project == null || projectName != req["project"]?.GetValue<string>() ||
            !string.Equals(currentPath, req["project_path"]?.GetValue<string>(), StringComparison.Ordinal))
        {
            Log.Warn(Tag, "Game Options Android nao atualizada: o projeto aberto mudou");
            return;
        }
        if (req["target"]?.GetValue<string>() is "web" or "windows")
        {
            string target = req["target"]!.GetValue<string>();
            var options = AndroidGameOptions.GetPlatform(project, target == "web" ? "GMOperaGXOptions" : "GMWindowsOptions", create: true)!;
            options.GetType().GetProperty(target == "web" ? "option_operagx_game_name" : "option_windows_display_name", Any)!
                .SetValue(options, req["display_name"]!.GetValue<string>());
            options.GetType().GetProperty(target == "web" ? "option_operagx_version" : "option_windows_version", Any)!
                .SetValue(options, req["version"]!.GetValue<string>());
            byte[]? png = req["icon_path"]?.GetValue<string>() is { Length: > 0 } source ? ApkBuilder.RenderIconPng(source, 256) : null;
            ProjectExportSettings.Save(currentPath, target, req, png);
            if (target == "windows" && png != null) options.GetType().GetProperty("option_windows_icon", Any)!
                .SetValue(options, "${project_dir}/options/gmide-export/windows-icon.ico");
            if (save) YoYoStudio.IDE.SaveProjectIncremental((success, error) =>
            {
                if (success) ClearPendingOptions(projectName!, json);
                else Toast("Nao foi possivel salvar Game Options: " + error?.Message);
            });
            return;
        }
        opts ??= AndroidGameOptions.Get(project, create: true)!;
        var current = ReadGameOptions(opts, projectName!);
        string name = req["display_name"]!.GetValue<string>();
        string package = req["package"]!.GetValue<string>();
        string version = req["version"]!.GetValue<string>();
        var changes = new List<(string prop, string value)>();
        if (name != current.DisplayName)
            changes.Add(("option_android_display_name", name == projectName ? ProjectNameMacro : name));
        if (version != current.Version) changes.Add(("option_android_version", version));
        if (package != current.Package)
        {
            // A Game Options divide o pacote em dominio.empresa.produto; partes extras ficam na empresa.
            var parts = package.Split('.');
            changes.Add(("option_android_package_domain", parts[0]));
            changes.Add(("option_android_package_company", string.Join(".", parts[1..^1])));
            changes.Add(("option_android_package_product", parts[^1]));
        }
        foreach (var (prop, value) in changes)
            opts.GetType().GetProperty(prop, Any)!.SetValue(opts, value);
        if (req["icon_path"]?.GetValue<string>() is { Length: > 0 } icon) ImportIcon(opts, icon);
        if (changes.Count > 0) Log.Info(Tag, "Game Options Android atualizada: " + string.Join(", ", changes.Select(c => $"{c.prop}={c.value}")));
        if (!save) return;
        YoYoStudio.IDE.SaveProjectIncremental((success, error) =>
        {
            if (!success) Toast("A Game Options foi alterada, mas o projeto nao foi salvo: " + error?.Message);
            else
                try { ClearPendingOptions(projectName!, json); }
                catch (Exception e) { Toast("Projeto salvo, mas o registro da exportacao nao foi atualizado: " + e.Message); }
        });
    }

    static void ClearPendingOptions(string projectName, string json)
    {
        lock (_settingsLock)
        {
            if (LoadLast(projectName) is not JsonObject values || values["pending_game_options"]?.ToJsonString() != json) return;
            values.Remove("pending_game_options");
            SaveLast(projectName, values);
        }
    }

    static void ImportIcon(object options, string source) => AndroidProjectIcons.Import(options, source,
        value => YoYoStudio.Core.Utils.MacroExpansion.Expand(value), ApkBuilder.RenderIcon);

    /// <summary>compileSdk do modelo do runner embutido (assets/apk-template/template.json).</summary>
    internal static int TemplateCompileSdk()
    {
        using var s = MainApplication.Instance.Assets!.Open("apk-template/template.json");
        using var r = new StreamReader(s);
        return JsonNode.Parse(r.ReadToEnd())?["compile_sdk"]?.GetValue<int>()
            ?? throw new InvalidDataException("compile_sdk ausente no template.json do modelo do runner.");
    }

    static T? Prop<T>(object? o, string name)
    {
        if (o == null) return default;
        var p = o.GetType().GetProperty(name, Any);
        var v = p?.GetValue(o);
        return v is T t ? t : default;
    }

    static string SafePackagePart(string value)
    {
        var part = new string(value.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
        return part.Length == 0 || !char.IsAsciiLetter(part[0]) ? "jogo" : part;
    }

    static string DownloadDir() =>
        global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDownloads)!.AbsolutePath;

    static string SafeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var r = new string(s.Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return r.Length == 0 ? "jogo" : r;
    }

    static string LastPath => Path.Combine(MainApplication.Instance.FilesDir!.AbsolutePath, "apk-export", "last.json");

    static JsonNode? LoadLast(string project)
    {
        try { return File.Exists(LastPath) ? JsonNode.Parse(File.ReadAllText(LastPath))?[project] : null; }
        catch { return null; }
    }

    static void SaveLast(string project, JsonObject values)
    {
        lock (_settingsLock)
        try
        {
            var all = File.Exists(LastPath) ? JsonNode.Parse(File.ReadAllText(LastPath)) as JsonObject ?? new() : new JsonObject();
            // LoadLast devolve o no ainda preso ao documento lido; um JsonNode so tem um pai.
            all[project] = values.Parent == null ? values : values.DeepClone();
            Directory.CreateDirectory(Path.GetDirectoryName(LastPath)!);
            File.WriteAllText(LastPath + ".tmp", all.ToJsonString());
            File.Move(LastPath + ".tmp", LastPath, overwrite: true);
        }
        catch (Exception e) { throw new IOException("Nao foi possivel salvar a personalizacao da exportacao.", e); }
    }

    /// <summary>Copia do icone escolhido para este projeto (o popup grava aqui).</summary>
    static string IconStorePath(string project) =>
        Path.Combine(MainApplication.Instance.FilesDir!.AbsolutePath, "apk-export", "icons", SafeFileName(project) + ".img");

    static void Toast(string msg)
    {
        Log.Info(Tag, msg);
        new Handler(Looper.MainLooper!).Post(() =>
        {
            try { global::Android.Widget.Toast.MakeText(MainApplication.Instance, msg, global::Android.Widget.ToastLength.Long)!.Show(); } catch { }
        });
    }
}
