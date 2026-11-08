using System.Reflection;
using System.Runtime.CompilerServices;
using Log = Android.Util.Log;
using Core.CoreOS.System;
using YoYoStudio.Core.Utils;
using YoYoStudio.GUI;

namespace GmIde;

// Ajustes do lancador para Android que dependem da IDE carregada
// (chamados de Launcher.BootIde, na thread do SDL_main).
static class IdeSetup
{
    const string Tag = "GmIde.Setup";

    // Permite forcar a escala sem recompilar: um numero (25..400) neste arquivo.
    const string DpiOverrideUserFile = "/storage/emulated/0/Download/gmide-dpi.txt";

    // ---------------------------------------------------------------- hints

    // Antes de IDE.BootstrapSDL2() (que faz o SDL_Init). A IDE seta varios hints com
    // SDL_SetHint (prioridade normal); OVERRIDE ganha deles e de variaveis de ambiente.
    public static void ApplyEarlyHints()
    {
        try
        {
            Sdl.SDL_GetVersion(out var v);
            Log.Info(Tag, $"SDL {v.major}.{v.minor}.{v.patch}, plataforma '{Sdl.Platform}', SDL_WasInit=0x{Sdl.SDL_WasInit(0):x}");
        }
        catch (Exception e) { Log.Error(Tag, "libSDL2 nao respondeu: " + e); return; }

        var hints = new (string name, string value, string why)[]
        {
            // OpenGL ES 3 (FNA3D_Driver_OpenGL.c: OPENGL_PrepareWindowAttributes). No Android o
            // FNA3D ja forca ES3 por SDL_GetPlatform()=="Android"; o hint garante mesmo assim.
            ("FNA3D_FORCE_DRIVER", "OpenGL", "FNA3D so tem o driver OpenGL neste build; a IDE pode pedir Vulkan/D3D11"),
            ("FNA3D_OPENGL_FORCE_ES3", "1", "contexto GLES 3.0"),
            // Entrada (docs/input-analysis.md, lista priorizada item 1)
            ("SDL_TOUCH_MOUSE_EVENTS", "1", "toque gera mouse esquerdo (o filtro adapta)"),
            ("SDL_MOUSE_TOUCH_EVENTS", "0", "mouse real nao gera dedos falsos (a IDE seta 1)"),
            ("SDL_ANDROID_TRAP_BACK_BUTTON", "1", "Voltar vira tecla AC_BACK em vez de fechar a Activity"),
            ("SDL_RETURN_KEY_HIDES_IME", "0", "Enter nao fecha o teclado virtual"),
        };
        foreach (var (name, value, why) in hints)
        {
            int ok = Sdl.SDL_SetHintWithPriority(name, value, Sdl.HintOverride);
            int env = -1;
            try { env = Sdl.SetEnvNative(name, value); } catch (Exception e) { Log.Warn(Tag, $"setenv({name}) falhou: {e.Message}"); }
            Log.Info(Tag, $"hint {name}={value} (override={(ok != 0 ? "ok" : "FALHOU")}, setenv={env}) agora='{Sdl.GetHint(name)}' - {why}");
        }

        // A IDE le esta propriedade em CreateSdlWindow e repassa ao FNA3D_FORCE_DRIVER.
        try
        {
            SystemPlatform.ForceRenderer = "OpenGL";
            Log.Info(Tag, "SystemPlatform.ForceRenderer = OpenGL");
        }
        catch (Exception e) { Log.Warn(Tag, "SystemPlatform.ForceRenderer: " + e.Message); }
    }

    // Depois de BootstrapSDL2 (SDL_Init feito): atributos GL para ES 3.0. O FNA3D seta os
    // mesmos valores de novo em PrepareWindowAttributes; aqui e so redundancia + log.
    public static void ApplyGlAttributes()
    {
        Log.Info(Tag, $"Depois do SDL_Init: SDL_WasInit=0x{Sdl.SDL_WasInit(0):x}, plataforma '{Sdl.Platform}'");
        int a = Sdl.SDL_GL_SetAttribute(Sdl.GL_CONTEXT_PROFILE_MASK, Sdl.GL_CONTEXT_PROFILE_ES);
        int b = Sdl.SDL_GL_SetAttribute(Sdl.GL_CONTEXT_MAJOR_VERSION, 3);
        int c = Sdl.SDL_GL_SetAttribute(Sdl.GL_CONTEXT_MINOR_VERSION, 0);
        Log.Info(Tag, $"SDL_GL_SetAttribute ES 3.0: profile={a} major={b} minor={c}" + (a < 0 || b < 0 || c < 0 ? " erro: " + Sdl.Error : ""));
    }

    // ---------------------------------------------------------------- DPI

    // Cadeia da IDE: dpiEfetivo = hdpi(SDL_GetDisplayDPI) * Master%/100; fator = dpiEfetivo/96
    // (1 "unidade" de layout da IDE = fator px). A IDE le ${system_directory}/dpi_override.json em
    // IDE.Initialise -> ParseDPIOverride (depois de RegisterDefaultMacros + defaults/default_macros.json),
    // e so sobrescreve Desktop.MasterDPIOverridePercentageInt se o arquivo existir.
    //
    // Aqui (entre BootstrapSDL2 e Bootstrap) ${system_directory} ainda nao existe: o caminho e montado
    // com o mesmo modelo do defaults/default_macros.json ("${CommonApplicationData}/${program_dir_name}")
    // e conferido de novo depois do Initialise (VerifyDpiPath). O arquivo e gravado ja com a escala
    // escolhida, entao o ParseDPIOverride da IDE le o mesmo valor.
    //
    // Escolha da escala (docs/touch-ui.md):
    //   1. SharedPreferences "gmide"/"ui_scale_percent" > 0 (barra de atalhos: menu Escala +/-)
    //   2. /storage/emulated/0/Download/gmide-dpi.txt
    //   3. dpi_override.json salvo pelas Preferencias da IDE (sem a marca "gmide_source")
    //   4. automatico: fator = min(caber o espaco util minimo IdeMinW x IdeMinH em 95% da area, 0,16 mm por unidade)
    const string IdeProgramDirName = "GameMakerStudio2-LTS2026"; // DefaultMacros.RegisterDefaultMacros
    const string PrefsName = "gmide", PrefScale = "ui_scale_percent", PrefScaleEffective = "ui_scale_effective";
    const float TargetMmPerUnit = 0.16f;   // desktop a 96 dpi = 0,26 mm; celular fica ~2x mais perto do olho
    const float FitFraction = 0.95f;       // a "rede de seguranca" da IDE usa 0,82 (janela no desktop)
    // Espaco util minimo em unidades da IDE (antes era 748x528, o tamanho da tela de abertura,
    // SplashScreen.kSplash*DPI, que nao limita o layout e deixava a UI pequena demais para toque):
    // largura para os dois docks (~260 cada no layout de celular) e um workspace usavel; altura para
    // menu + toolbar (~70) e ~370 de workspace com o dock de baixo recolhido.
    const float IdeMinW = 960f, IdeMinH = 440f;

    static string? _dpiPath;
    static int _autoPct = 100, _pendingScale = -1;
    static float _hdpi = 96;
    static bool _dpiVerified;

    public static void ApplyDpi()
    {
        try
        {
            int displays = Sdl.SDL_GetNumVideoDisplays();
            float ddpi = 0, hdpi = 0, vdpi = 0;
            int r = Sdl.SDL_GetDisplayDPI(0, out ddpi, out hdpi, out vdpi);
            var m = MainApplication.Instance.Resources!.DisplayMetrics!;
            Log.Info(Tag, $"SDL_GetDisplayDPI(0)={r} ddpi={ddpi} hdpi={hdpi} vdpi={vdpi} (displays={displays}); " +
                          $"Android: {m.WidthPixels}x{m.HeightPixels} density={m.Density} densityDpi={m.DensityDpi} xdpi={m.Xdpi} ydpi={m.Ydpi}");
            if (r != 0 || hdpi <= 0)
            {
                Log.Warn(Tag, "SDL_GetDisplayDPI falhou (" + Sdl.Error + "); a IDE vai usar 96. Usando xdpi do Android para a conta.");
                hdpi = m.Xdpi > 0 ? m.Xdpi : 96;
            }
            _hdpi = hdpi;
            _autoPct = ComputeAutoPct(m, hdpi);

            _dpiPath = ResolveDpiPath();
            RemoveStaleRelativeFile();

            int pct;
            string source;
            bool write = true;
            int prefPct = ReadPrefScale();
            var existing = ReadDpiFile(_dpiPath);
            if (prefPct > 0)
            {
                pct = prefPct;
                source = "prefs";
                Log.Info(Tag, $"Escala forcada por SharedPreferences {PrefsName}/{PrefScale}: {pct}%");
            }
            else if (TryReadUserPct(out int userPct))
            {
                pct = userPct;
                source = "file";
            }
            else if (existing is { } ex && ex.source == null)
            {
                // Salvo pelas Preferencias da IDE (DPIOverrideGadget): respeita.
                pct = ex.enabled ? ex.pct : 100;
                source = "ide";
                write = false;
                Log.Info(Tag, $"dpi_override.json das Preferencias da IDE mantido: {ex.raw.Trim()} " +
                              $"(automatico seria {_autoPct}%; escolha 'Escala da UI: automatica' na barra para voltar)");
                if (!ex.enabled)
                    Log.Warn(Tag, "dpi_override.json com is_enabled=false: a IDE vai usar 100% (UI enorme no celular)");
            }
            else
            {
                pct = _autoPct;
                source = "auto";
            }
            pct = Math.Clamp(pct, 25, 400);
            YoYoStudio.Graphics.Desktop.MasterDPIOverridePercentageInt = pct;
            if (write) WriteDpiFile(_dpiPath, pct, source, existing?.vsync);
            SaveEffectiveScale(pct);
            Log.Info(Tag, $"Escala da UI: {pct}% (fonte: {source}; automatico={_autoPct}%) -> fator {Factor(pct):F2}, " +
                          $"dpi efetivo ~{hdpi * pct / 100f:F0}; Desktop.MasterDPIOverridePercentageInt = " +
                          YoYoStudio.Graphics.Desktop.MasterDPIOverridePercentageInt);
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Falha ao ajustar o DPI (a UI pode ficar 4-6x maior): " + e);
        }
    }

    static float Factor(int pct) => _hdpi * pct / 100f / 96f;

    /// <summary>
    /// Idiomas da IDE. No PC eles chegam como pacotes do gmpm (Node, que nao roda no aparelho) e a IDE
    /// so achava o ingles. Language.DiscoverLanguages (Utils.dll), chamado no SplashScreen, le todo
    /// *.csv de ${system_directory}/Languages; os pacotes ide/packages/localisation_plugins/&lt;idioma&gt;/
    /// trazem os .csv no mesmo formato. Copia os que mudaram (o idioma e escolhido em Preferences >
    /// General Settings > IDE language, com reinicio).
    /// </summary>
    public static void InstallLanguages(string ideDir)
    {
        try
        {
            if (_dpiPath == null) return;
            string dest = Path.Combine(Path.GetDirectoryName(_dpiPath)!, "Languages");
            string src = Path.Combine(ideDir, "packages", "localisation_plugins");
            if (!Directory.Exists(src)) { Log.Warn(Tag, "Idiomas: " + src + " nao existe"); return; }
            Directory.CreateDirectory(dest);
            int copied = 0, total = 0;
            foreach (var csv in Directory.EnumerateFiles(src, "*.csv", SearchOption.AllDirectories))
            {
                total++;
                var target = Path.Combine(dest, Path.GetFileName(csv));
                var fi = new FileInfo(csv);
                var ti = new FileInfo(target);
                if (ti.Exists && ti.Length == fi.Length) continue;
                File.Copy(csv, target, true);
                copied++;
            }
            Log.Info(Tag, $"Idiomas: {total} arquivos em {dest} ({copied} copiados agora)");
        }
        catch (Exception e) { Log.Warn(Tag, "Idiomas: " + e.Message); }
    }

    // Escala automatica. Area util = janela do app (sem a faixa do recorte) menos a barra de
    // atalhos quando ela reserva espaco (ShortcutBar: rodape 40dp, lateral 52dp).
    static int ComputeAutoPct(global::Android.Util.DisplayMetrics m, float hdpi)
    {
        float w = Math.Max(m.WidthPixels, m.HeightPixels);   // a IDE roda em paisagem
        float h = Math.Min(m.WidthPixels, m.HeightPixels);
        string bar = "sem barra";
        try
        {
            var p = Prefs();
            string pos = p.GetString("shortcut_bar_position", "bottom") ?? "bottom";
            bool collapsed = p.GetBoolean("shortcut_bar_collapsed", false);
            bool overlay = p.GetBoolean("shortcut_bar_overlay", false);
            if (!collapsed && !overlay && pos == "side") { w -= 52 * m.Density; bar = "barra lateral 52dp"; }
            else if (!collapsed && !overlay && pos == "bottom") { h -= 40 * m.Density; bar = "rodape 40dp"; }
            else bar = $"barra {pos} (recolhida={collapsed}, overlay={overlay})";
        }
        catch (Exception e) { Log.Warn(Tag, "Preferencias da barra: " + e.Message); }

        float xdpi = m.Xdpi > 0 ? m.Xdpi : hdpi;
        float fit = Math.Min(w * FitFraction / IdeMinW, h * FitFraction / IdeMinH);
        float phys = xdpi / 25.4f * TargetMmPerUnit;
        float f = Math.Clamp(Math.Min(fit, phys), 1f, 8f);
        int pct = Math.Clamp((int)Math.Round(100f * 96f * f / hdpi), 25, 400);
        float pxPerMm = xdpi / 25.4f;
        float realF = hdpi * pct / 100f / 96f;
        Log.Info(Tag, $"Escala automatica: area {w:F0}x{h:F0}px ({bar}), xdpi={xdpi:F0}; fator p/ caber={fit:F2}, " +
                      $"fator p/ {TargetMmPerUnit}mm/unidade={phys:F2} -> {pct}% (fator {realF:F2}). " +
                      $"Linha de lista/botao (~22 un.) = {22 * realF / pxPerMm:F1} mm, texto (~13 un.) = {13 * realF / pxPerMm:F1} mm; " +
                      $"UI minima ocupa {IdeMinW * realF:F0}x{IdeMinH * realF:F0}px");
        return pct;
    }

    static global::Android.Content.ISharedPreferences Prefs() =>
        MainApplication.Instance.GetSharedPreferences(PrefsName, global::Android.Content.FileCreationMode.Private)!;

    static int ReadPrefScale()
    {
        try { return Prefs().GetInt(PrefScale, 0); }
        catch (Exception e) { Log.Warn(Tag, $"{PrefsName}/{PrefScale} ilegivel: {e.Message}"); return 0; }
    }

    static void SaveEffectiveScale(int pct)
    {
        try { Prefs().Edit()!.PutInt(PrefScaleEffective, pct)!.Apply(); }
        catch (Exception e) { Log.Warn(Tag, "Nao gravei ui_scale_effective: " + e.Message); }
    }

    // Caminho real de ${system_directory}/dpi_override.json, mesmo antes da IDE registrar a macro.
    static string ResolveDpiPath()
    {
        string sys = MacroExpansion.Expand("${system_directory}");
        string how = "macro ${system_directory}";
        if (!Path.IsPathRooted(sys) || sys.Contains("${"))
        {
            // Mesmo modelo da IDE: defaults/default_macros.json (relativo ao diretorio atual, como
            // em IDE.Initialise -> LoadDefaultSystemSettings("defaults/default_macros.json")).
            string tpl = "${CommonApplicationData}/${program_dir_name}";
            try
            {
                const string defaults = "defaults/default_macros.json";
                if (File.Exists(defaults))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(defaults));
                    if (doc.RootElement.TryGetProperty("system_directory", out var v) && v.GetString() is { Length: > 0 } s)
                        tpl = s;
                }
                else Log.Warn(Tag, $"{Path.GetFullPath(defaults)} nao existe; usando o modelo padrao {tpl}");
            }
            catch (Exception e) { Log.Warn(Tag, "Lendo defaults/default_macros.json: " + e.Message); }
            string prog = MacroExpansion.Expand("${program_dir_name}");
            if (string.IsNullOrEmpty(prog) || prog == "program_dir_name" || prog.Contains('$')) prog = IdeProgramDirName;
            sys = MacroExpansion.Expand(tpl.Replace("${program_dir_name}", prog));
            how = $"modelo '{tpl}' (program_dir_name={prog})";
        }
        string path = Path.Combine(sys, "dpi_override.json");
        Log.Info(Tag, $"dpi_override.json em '{path}' (via {how})");
        if (!Path.IsPathRooted(path))
            Log.Warn(Tag, "Caminho do dpi_override.json ainda relativo: a IDE pode nao achar o arquivo");
        return path;
    }

    // Versoes anteriores gravavam em "system_directory/dpi_override.json" relativo ao diretorio atual.
    static void RemoveStaleRelativeFile()
    {
        try
        {
            string stale = Path.GetFullPath(Path.Combine("system_directory", "dpi_override.json"));
            if (stale != Path.GetFullPath(_dpiPath!) && File.Exists(stale))
            {
                File.Delete(stale);
                Log.Info(Tag, $"Removido dpi_override.json antigo no caminho errado: {stale}");
                var dir = Path.GetDirectoryName(stale)!;
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
        }
        catch (Exception e) { Log.Warn(Tag, "Limpando dpi_override.json antigo: " + e.Message); }
    }

    record DpiFile(int pct, bool enabled, bool? vsync, string? source, string raw);

    static DpiFile? ReadDpiFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            bool enabled = !root.TryGetProperty("is_enabled", out var en) || en.ValueKind != System.Text.Json.JsonValueKind.False;
            int pct = root.TryGetProperty("percentage", out var p) && p.TryGetInt32(out int v) ? v : 100;
            bool? vsync = root.TryGetProperty("vsync_enabled", out var vs) && vs.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? vs.GetBoolean() : null;
            string? src = root.TryGetProperty("gmide_source", out var s) ? s.GetString() : null;
            return new DpiFile(pct, enabled, vsync, src, text);
        }
        catch (Exception e)
        {
            Log.Warn(Tag, $"Nao entendi {path}: {e.Message}");
            return null;
        }
    }

    // Mesmo formato do DPIOverrideFile.JsonFileContent (+ "gmide_source", ignorado pela IDE).
    // Numeros como inteiro: DPIOverrideFile.LoadFrom faz (int)obj.
    static void WriteDpiFile(string path, int pct, string source, bool? vsync)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = "{\"vsync_enabled\":" + ((vsync ?? SystemPlatform.VSync) ? "true" : "false") +
                       ",\"is_enabled\":true,\"percentage\":" + pct +
                       ",\"d3d11bitblt_enabled\":false,\"gmide_source\":\"" + source + "\"}";
            File.WriteAllText(path, json);
            Log.Info(Tag, $"Gravado {path}: {json}");
        }
        catch (Exception e) { Log.Error(Tag, $"Nao gravei {path}: {e.Message}"); }
    }

    // Barra de atalhos (thread do Java): pede nova escala; 0 = automatica.
    // Aplicada na thread do SDL pelo proximo evento de entrada que a IDE processar
    // (a propria tecla HELP do comando passa pela IDE e dispara o observador).
    public static void RequestScale(int pct)
    {
        Volatile.Write(ref _pendingScale, Math.Max(0, pct));
        Log.Info(Tag, $"Escala pedida pela barra: {(pct > 0 ? pct + "%" : "automatica")} (aplica no proximo evento da IDE)");
    }

    static void ApplyPendingScale()
    {
        int req = Interlocked.Exchange(ref _pendingScale, -1);
        if (req < 0) return;
        var wm = YoYoStudio.IDE.WindowManager;
        if (wm == null)
        {
            Interlocked.CompareExchange(ref _pendingScale, req, -1);
            return;
        }
        int pct = Math.Clamp(req > 0 ? req : _autoPct, 25, 400);
        int before = YoYoStudio.Graphics.Desktop.MasterDPIOverridePercentageInt;
        try
        {
            if (pct != before) wm.LayoutAllDesktopsWithDpiOverrideChange(pct);
            string path = CurrentDpiPath();
            WriteDpiFile(path, pct, req > 0 ? "prefs" : "auto", ReadDpiFile(path)?.vsync);
            SaveEffectiveScale(pct);
            Log.Info(Tag, $"Escala aplicada ao vivo: {before}% -> {pct}% (fator {Factor(pct):F2})");
        }
        catch (Exception e) { Log.Error(Tag, "LayoutAllDesktopsWithDpiOverrideChange: " + e); }
    }

    static string CurrentDpiPath()
    {
        try
        {
            var p = YoYoStudio.GUI.Gadgets.Settings.DPIOverrideFile.StandardPath;
            if (Path.IsPathRooted(p)) return p;
        }
        catch { }
        return _dpiPath ?? Path.Combine(MacroExpansion.Expand("${system_directory}"), "dpi_override.json");
    }

    // Depois do IDE.Initialise: confere que o caminho previsto e o mesmo que a IDE usa.
    static void VerifyDpiPath()
    {
        if (_dpiVerified) return;
        _dpiVerified = true;
        try
        {
            string real = YoYoStudio.GUI.Gadgets.Settings.DPIOverrideFile.StandardPath;
            int now = YoYoStudio.Graphics.Desktop.MasterDPIOverridePercentageInt;
            if (_dpiPath != null && Path.GetFullPath(real) == Path.GetFullPath(_dpiPath))
                Log.Info(Tag, $"dpi_override.json confere com a IDE: {real}; escala em uso {now}%");
            else
            {
                Log.Warn(Tag, $"A IDE usa '{real}', mas o boot gravou em '{_dpiPath}'. Gravando la ({now}%) para o proximo boot.");
                _dpiPath = real;
                var ex = ReadDpiFile(real);
                if (ex == null || ex.source != null) WriteDpiFile(real, now, ReadPrefScale() > 0 ? "prefs" : "auto", ex?.vsync);
            }
        }
        catch (Exception e) { Log.Warn(Tag, "VerifyDpiPath: " + e.Message); }
    }

    static bool TryReadUserPct(out int pct)
    {
        pct = 0;
        try
        {
            if (!File.Exists(DpiOverrideUserFile)) return false;
            var s = File.ReadAllText(DpiOverrideUserFile).Trim().TrimEnd('%');
            if (int.TryParse(s, out pct) && pct > 0)
            {
                Log.Info(Tag, $"Escala forcada por {DpiOverrideUserFile}: {pct}%");
                return true;
            }
            Log.Warn(Tag, $"{DpiOverrideUserFile} nao contem um numero: '{s}'");
        }
        catch (Exception e) { Log.Warn(Tag, $"Nao li {DpiOverrideUserFile}: {e.Message}"); }
        return false;
    }

    // ---------------------------------------------------------------- diagnostico

    public static void LogEnvironment(string when)
    {
        void L(string what, Func<object?> f)
        {
            try { Log.Info(Tag, $"[{when}] {what} = {f()}"); }
            catch (Exception e) { Log.Warn(Tag, $"[{when}] {what}: {e.GetType().Name}: {e.Message}"); }
        }
        L("${system_directory}", () => MacroExpansion.Expand("${system_directory}"));
        L("${user_directory}", () => MacroExpansion.Expand("${user_directory}"));
        L("${local_directory}", () => MacroExpansion.Expand("${local_directory}"));
        L("Platform.OSType (0=Win 1=Mac 2=Linux)", () =>
        {
            var t = typeof(SystemPlatform).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "Platform" && t.GetProperty("OSType", BindingFlags.Public | BindingFlags.Static) != null
                                     || t.Name == "Platform" && t.GetField("OSType", BindingFlags.Public | BindingFlags.Static) != null);
            if (t == null) return "(tipo Platform nao encontrado)";
            return t.GetProperty("OSType", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                   ?? t.GetField("OSType", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        });
        L("SDL_IsTextInputActive", () => Sdl.SDL_IsTextInputActive() != 0);
        L("SDL_HasScreenKeyboardSupport", () => Sdl.SDL_HasScreenKeyboardSupport() != 0);
        L("Desktop.MasterDPIOverridePercentageInt", () => YoYoStudio.Graphics.Desktop.MasterDPIOverridePercentageInt);
        foreach (var h in new[] { "FNA3D_FORCE_DRIVER", "FNA3D_OPENGL_FORCE_ES3", "SDL_TOUCH_MOUSE_EVENTS",
                                  "SDL_MOUSE_TOUCH_EVENTS", "SDL_ANDROID_TRAP_BACK_BUTTON", "SDL_RETURN_KEY_HIDES_IME" })
            L("hint " + h, () => Sdl.GetHint(h));
    }

    // Uma vez, ja com a janela/contexto GL criados (chamado do primeiro evento de entrada).
    static void LogGlContext()
    {
        try
        {
            var ctx = Sdl.SDL_GL_GetCurrentContext();
            Sdl.SDL_GL_GetAttribute(Sdl.GL_CONTEXT_PROFILE_MASK, out int prof);
            Sdl.SDL_GL_GetAttribute(Sdl.GL_CONTEXT_MAJOR_VERSION, out int maj);
            Sdl.SDL_GL_GetAttribute(Sdl.GL_CONTEXT_MINOR_VERSION, out int min);
            Log.Info(Tag, $"Contexto GL atual=0x{ctx:x} profile=0x{prof:x} (4=ES) versao {maj}.{min}; " +
                          $"SystemPlatform.Renderer='{SystemPlatform.Renderer}'");
        }
        catch (Exception e) { Log.Warn(Tag, "Nao consegui ler o contexto GL: " + e.Message); }
    }

    // ---------------------------------------------------------------- CrashReporter

    // IDE.Execute captura a excecao, loga "IDE Exception: ..." e chama
    // CrashReporter.WriteStackDumpFile(ex). Esse metodo faz
    //   (ex.InnerException ?? ex).TargetSite.DeclaringType  (sem checar null)
    // e no Mono TargetSite e null quando a excecao nasce num stub de P/Invoke
    // (DllNotFoundException, EntryPointNotFoundException...) -> NRE que substitui o erro.
    // Tambem retorna cedo se m_dumpDirectory for null (so e preenchido em OnIDELoaded).
    // Aqui: preenchemos m_dumpDirectory e guardamos as ultimas excecoes (first-chance)
    // para o Launcher logar o erro original quando o NRE aparecer.
    public static void PrepareCrashReporter()
    {
        try
        {
            var dumps = Path.Combine(MacroExpansion.Expand("${system_directory}"), "Dumps");
            Directory.CreateDirectory(dumps);
            var f = typeof(YoYoStudio.Utils.CrashReporter).GetField("m_dumpDirectory", BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null) Log.Warn(Tag, "CrashReporter.m_dumpDirectory nao existe nesta versao");
            else if (f.GetValue(null) == null)
            {
                f.SetValue(null, dumps);
                Log.Info(Tag, "CrashReporter.m_dumpDirectory = " + dumps);
            }
        }
        catch (Exception e) { Log.Warn(Tag, "PrepareCrashReporter: " + e); }

        if (!_firstChanceHooked)
        {
            _firstChanceHooked = true;
            AppDomain.CurrentDomain.FirstChanceException += (_, a) =>
            {
                var ex = a.Exception;
                if (ex is NullReferenceException && ex.StackTrace?.Contains("CrashReporter") == true) return;
                lock (_recent)
                {
                    _recent.Enqueue(ex);
                    while (_recent.Count > 8) _recent.Dequeue();
                }
            };
        }
    }

    static bool _firstChanceHooked;
    static readonly Queue<Exception> _recent = new();

    public static bool IsCrashReporterNre(Exception e) =>
        e is NullReferenceException && e.StackTrace?.Contains("CrashReporter.WriteStackDumpFile") == true;

    public static void LogRecentExceptions(string header)
    {
        Exception[] list;
        lock (_recent) list = _recent.ToArray();
        Log.Error("GmIde", $"{header} ({list.Length} excecoes recentes, a mais nova por ultimo):");
        foreach (var ex in list)
        {
            string site;
            try { site = ex.TargetSite == null ? "TargetSite=null" : "TargetSite=" + ex.TargetSite.DeclaringType?.Name + "." + ex.TargetSite.Name; }
            catch { site = "TargetSite=?"; }
            Log.Error("GmIde", $"  [{site}] {ex}");
        }
    }

    // ---------------------------------------------------------------- texto (teclado)

    // Item 3 da lista: a IDE nunca chama SDL_StartTextInput, e no Android o SDL comeca
    // com a entrada de texto desligada (sem SDL_TEXTINPUT nem teclado virtual).
    // Versao com foco: observadores encadeados em SystemPlatform.OnMouseButtonUp /
    // OnKeyboardDown (rodam na thread do SDL) assinam WindowManager.OnFocusChange na
    // primeira vez em que IDE.WindowManager existe. No foco de um gadget de texto ->
    // SDL_StartTextInput (+ SDL_SetTextInputRect); fora dele -> SDL_StopTextInput.
    // Se o hook falhar, cai na versao minima (SDL_StartTextInput fixo).

    static SystemPlatform.OnMouseButtonUpDelegate? _mouseUpObserver;
    static SystemPlatform.OnKeyboardDownDelegate? _keyDownObserver;
    static FocusChangeEvent? _focusHandler; // referencia forte: o WeakEventStruct guarda fraco
    static bool _focusHooked, _focusHookFailed, _glLogged;
    static bool _textWanted;
    static int _observerCalls;

    static readonly string[] TextTypeNames =
    {
        "TextBox", "NumericTextBox", "SearchBar", "TextEditorPanel", "TextEditorComponent",
        "DocumentView", "DnDCodeInput2", "DocumentScrollPanel", "CodeEditor",
    };

    public static void InstallTextInput()
    {
        try
        {
            _mouseUpObserver ??= (win, x, y, button) => { OnTapForPendingText(x, y); OnInputObserved("mouseup", win); };
            _keyDownObserver ??= (win, mod, sc, sym, rep, uni) => OnInputObserved("keydown", win);
            SystemPlatform.OnMouseButtonUp -= _mouseUpObserver;
            SystemPlatform.OnMouseButtonUp += _mouseUpObserver;
            SystemPlatform.OnKeyboardDown -= _keyDownObserver;
            SystemPlatform.OnKeyboardDown += _keyDownObserver;
            MobileWorkspace.Install(); // modo Mover da barra (MobileWindows.cs)
            Log.Info(Tag, "Observadores de entrada encadeados (OnMouseButtonUp/OnKeyboardDown); " +
                          "o hook de foco sera ligado no primeiro clique/tecla depois do IDE.Initialise");
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Nao consegui encadear observadores; usando SDL_StartTextInput fixo: " + e);
            StartTextInputFallback();
        }
    }

    static void StartTextInputFallback()
    {
        Sdl.SDL_StartTextInput();
        _textWanted = true;
        Log.Info(Tag, $"SDL_StartTextInput (fixo). Ativo={Sdl.SDL_IsTextInputActive() != 0}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void OnInputObserved(string what, int window)
    {
        try
        {
            _observerCalls++;
            if (!_glLogged) { _glLogged = true; LogGlContext(); LogEnvironment("primeira entrada"); }
            if (!_dpiVerified && YoYoStudio.IDE.WindowManager != null) VerifyDpiPath();
            if (Volatile.Read(ref _pendingScale) >= 0) ApplyPendingScale();
            if (ApkExport.HasPending) ApkExport.RunPending();
            LocalGameRun.RunPendingControls();

            if (!_focusHooked && !_focusHookFailed)
                TryHookFocus();
            if (_focusHooked && what == "mouseup") MobileWindowPolicy.OnTapUp(); // paineis como gavetas

            // Teclado virtual fechado pelo usuario (botao do proprio teclado) com o
            // gadget de texto ainda focado: um novo toque reabre.
            if (_textWanted && what == "mouseup")
            {
                var w = Sdl.SDL_GetWindowFromID((uint)window);
                if (w != IntPtr.Zero && Sdl.SDL_IsScreenKeyboardShown(w) == 0)
                {
                    Log.Info(Tag, "Teclado virtual fechado com campo de texto focado; reabrindo");
                    Sdl.SDL_StartTextInput();
                }
            }
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Observador de entrada: " + e);
        }
    }

    static void TryHookFocus()
    {
        var wm = YoYoStudio.IDE.WindowManager;
        if (wm == null)
        {
            if (_observerCalls is 1 or 50 or 500)
                Log.Info(Tag, $"IDE.WindowManager ainda null ({_observerCalls} eventos)");
            return;
        }
        try
        {
            _focusHandler ??= OnFocusChange;
            WindowManager.OnFocusChange.DoEveryTime(_focusHandler);
            _focusHooked = true;
            KeyboardMode.Install();
            MobileFrame.Install();
            Log.Info(Tag, "WindowManager.OnFocusChange assinado; entrada de texto segue o foco");
            OnFocusChange(wm.ActiveGadget, null!);
        }
        catch (Exception e)
        {
            _focusHookFailed = true;
            Log.Error(Tag, "Falha ao assinar OnFocusChange; usando SDL_StartTextInput fixo: " + e);
            StartTextInputFallback();
        }
    }

    static void OnFocusChange(GUIBase activeGadget, GUIBase activeWindow)
    {
        try
        {
            var (isText, chain) = Classify(activeGadget);
            Log.Debug(Tag, $"Foco: {chain} -> texto={isText}");
            // Editor de codigo virando aba de tela cheia: a IDE foca a janela nova (gadget null) antes de
            // devolvermos o foco ao painel. Nao fechar o teclado nesse intervalo.
            if (!isText && KeyboardMode.Promoting)
            {
                Log.Debug(Tag, "Foco transitorio durante a promocao do editor; teclado mantido");
                return;
            }
            WatchSingleLineCommit(isText ? activeGadget : null);
            _pendingText = null;
            // Alguns campos recebem o foco no mouseup, depois do nosso observador: se o foco chegou logo
            // depois de um toque dentro do campo, e um toque do usuario -> abre o teclado na hora.
            if (isText && activeGadget != null && Environment.TickCount64 - _lastUpTicks <= 600 &&
                Inside(activeGadget, _lastUpX, _lastUpY))
            {
                Log.Info(Tag, $"Foco por toque no campo ({chain}): abrindo o teclado");
                OpenOrCloseForFocus(activeGadget, true, chain);
                return;
            }
            if (isText && !KeyboardAlreadyUp() && !HasHardwareKeyboard())
            {
                // Foco automatico (busca do New, seletor de sprite, nome de asset recem-criado): nao abre o
                // teclado virtual, que cobre metade da tela. Abre quando o usuario tocar dentro do campo
                // (OnTapForPendingText, no mouseup que segue o toque).
                _pendingText = activeGadget;
                _pendingChain = chain;
                KeyboardMode.OnFocusChange(activeGadget, false);
                if (_textWanted) { Sdl.SDL_StopTextInput(); _textWanted = false; }
                var fr = activeGadget!.AbsoluteClipRect;
                Log.Info(Tag, $"Foco em texto sem teclado aberto ({chain}): teclado so com um toque no campo " +
                              $"[campo {fr.x:F0},{fr.y:F0} {fr.w:F0}x{fr.h:F0}; ultimo toque ({_lastUpX},{_lastUpY}) ha " +
                              $"{Environment.TickCount64 - _lastUpTicks} ms]");
                return;
            }
            OpenOrCloseForFocus(activeGadget, isText, chain);
        }
        catch (Exception e)
        {
            Log.Error(Tag, "OnFocusChange: " + e);
        }
    }

    static GUIBase? _pendingText;
    static string _pendingChain = "";

    // ---- Enter num campo de uma linha fecha o teclado
    // TextBox.KeyDownEvent confirma o valor no Enter (OnTextUpdated + OnInputComplete) mas nao tira o
    // foco, entao o teclado ficava aberto cobrindo metade da tela. OnInputComplete com o campo ainda
    // focado = Enter/Esc (perder o foco tambem dispara, mas ai o foco ja mudou). Multilinha nao usa o
    // KeyDownEvent do TextBox; editores de codigo nao sao TextBox. Um toque no campo reabre o teclado.
    static YoYoStudio.GUI.Gadgets.TextBox? _commitBox;
    static InputValueChanged? _commitHandler;   // ref forte: o evento guarda referencia fraca

    static void WatchSingleLineCommit(GUIBase? g)
    {
        try
        {
            var box = g as YoYoStudio.GUI.Gadgets.TextBox;
            if (box is YoYoStudio.GUI.Gadgets.MultiLineTextBox) box = null;
            if (box == _commitBox) return;
            if (_commitBox != null && _commitHandler != null && !_commitBox.Destroyed)
                _commitBox.OnInputComplete.Remove(_commitHandler);
            _commitBox = box;
            if (box == null) return;
            _commitHandler ??= OnSingleLineCommit;
            box.OnInputComplete.DoEveryTime(_commitHandler);
        }
        catch (Exception e) { Log.Warn(Tag, "Observar Enter do campo: " + e.Message); }
    }

    static void OnSingleLineCommit(string value)
    {
        try
        {
            var box = _commitBox;
            if (box == null || box.Destroyed || YoYoStudio.IDE.WindowManager?.ActiveGadget != box) return;
            if (!_textWanted) return;
            Sdl.SDL_StopTextInput();
            _textWanted = false;
            _pendingText = box;                       // toque no campo reabre (OnTapForPendingText)
            _pendingChain = box.GetType().Name;
            KeyboardMode.OnFocusChange(box, false);   // some a barra de eco
            Log.Info(Tag, "Enter confirmou o campo: teclado fechado (toque no campo reabre)");
        }
        catch (Exception e) { Log.Warn(Tag, "Fechar teclado no Enter: " + e.Message); }
    }

    static bool KeyboardAlreadyUp()
    {
        IntPtr w = Sdl.SDL_GetKeyboardFocus();
        return w != IntPtr.Zero && Sdl.SDL_IsScreenKeyboardShown(w) != 0;
    }

    static bool HasHardwareKeyboard()
    {
        var c = MainApplication.Instance.Resources?.Configuration;
        return c != null && c.Keyboard != global::Android.Content.Res.KeyboardType.Nokeys &&
               c.HardKeyboardHidden == global::Android.Content.Res.HardKeyboardHidden.No;
    }

    /// <summary>Thread do SDL (mouseup): toque dentro do campo de texto focado sem teclado -> abre.</summary>
    static long _lastUpTicks;
    static int _lastUpX, _lastUpY;

    static bool Inside(GUIBase g, int x, int y)
    {
        var r = g.AbsoluteClipRect;
        const float slack = 12;
        return x >= r.x - slack && x <= r.x + r.w + slack && y >= r.y - slack && y <= r.y + r.h + slack;
    }

    static void OnTapForPendingText(int x, int y)
    {
        _lastUpTicks = Environment.TickCount64; _lastUpX = x; _lastUpY = y;
        try
        {
            var g = _pendingText;
            if (g == null) return;
            if (g.Destroyed) { _pendingText = null; return; }
            if (!Inside(g, x, y))
            {
                var r = g.AbsoluteClipRect;
                Log.Debug(Tag, $"Toque ({x},{y}) fora do campo focado ({r.x:F0},{r.y:F0} {r.w:F0}x{r.h:F0})");
                return;
            }
            _pendingText = null;
            Log.Info(Tag, $"Toque no campo de texto ({_pendingChain}): abrindo o teclado");
            OpenOrCloseForFocus(g, true, _pendingChain);
        }
        catch (Exception e) { Log.Warn(Tag, "Toque em campo de texto: " + e.Message); }
    }

    static void OpenOrCloseForFocus(GUIBase activeGadget, bool isText, string chain)
    {
        try
        {
            KeyboardMode.OnFocusChange(activeGadget, isText); // antes do teclado abrir: encolher ou panar
            if (isText)
            {
                SetTextRect(activeGadget);
                bool was = Sdl.SDL_IsTextInputActive() != 0;
                Sdl.SDL_StartTextInput();
                if (!_textWanted || !was)
                    Log.Info(Tag, $"SDL_StartTextInput (foco em {chain}); ativo={Sdl.SDL_IsTextInputActive() != 0}");
                _textWanted = true;
            }
            else if (_textWanted)
            {
                Sdl.SDL_StopTextInput();
                _textWanted = false;
                Log.Info(Tag, $"SDL_StopTextInput (foco em {chain})");
            }
        }
        catch (Exception e)
        {
            Log.Error(Tag, "OnFocusChange: " + e);
        }
    }

    // Olha o gadget e ate 4 pais; em cada um, a cadeia de tipos base.
    static (bool, string) Classify(GUIBase? g)
    {
        if (g == null) return (false, "(nenhum)");
        var names = new List<string>();
        bool isText = false;
        var cur = g;
        for (int depth = 0; cur != null && depth < 5; depth++, cur = cur.parent)
        {
            var t = cur.GetType();
            names.Add(t.Name);
            for (var bt = t; bt != null && bt != typeof(object); bt = bt.BaseType)
                if (Array.IndexOf(TextTypeNames, bt.Name) >= 0) { isText = true; break; }
            if (isText) break;
        }
        return (isText, string.Join(" < ", names));
    }

    static void SetTextRect(GUIBase g)
    {
        try
        {
            var r = g.AbsoluteContentRect;
            var rect = new Sdl.Rect { x = (int)r.x, y = (int)r.y, w = Math.Max(1, (int)r.w), h = Math.Max(1, (int)r.h) };
            Sdl.SDL_SetTextInputRect(ref rect);
            Log.Debug(Tag, $"SDL_SetTextInputRect({rect.x},{rect.y},{rect.w},{rect.h})");
        }
        catch (Exception e) { Log.Debug(Tag, "SetTextRect: " + e.Message); }
    }
}
