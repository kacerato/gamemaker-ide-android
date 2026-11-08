using Android.Util;
using System.Reflection;

namespace GmIde;

// Seletor de alvo da toolbar (Plataforma / Runtime / Dispositivo) no Android.
//
// Sintoma (logs/run14-all.txt): ao abrir o seletor, TargetSelectButton.TargetSelect_UpdatePopUp
// -> UpdateDevices(TargetManager.Platform) -> DeviceManager.GetPlatformDeviceManager(null)
// -> ArgumentNullException em string.ToLower(). TargetManager.Platform ficou nulo.
//
// Causa: o unico ponto que define o alvo ao abrir um projeto e
// ProjectLayout.OnProjectNodeLoad -> TargetManager.TargetManager_LoadTargetInfo -> SetTarget.
// Sem layout salvo do projeto, ProjectLayout.Load le
//     Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "defaultProjectLayout.xml")
// e no app o BaseDirectory e files/ (o host), nao files/ide/ onde a IDE foi importada.
// O arquivo nao e achado, Load retorna sem erro visivel ("Loading project layout from:
// .../files/defaultProjectLayout.xml" e nada mais) e SetTarget nunca roda. O mesmo vale para
// convertProjectLayout.xml (projetos de formato antigo). Sao os dois unicos usos de
// BaseDirectory no IDE.dll/Build.dll.
//
// Correcao (sem patch): links em BaseDirectory para os dois XML de files/ide/, e uma rede de
// seguranca em IDE.OnProjectLoaded que, se o alvo continuar nulo, chama
// TargetManager.SetTarget(null, "", "") - o mesmo caminho padrao da IDE
// (preferencia "Default Build Target", senao TestPlatform_Actual, senao "Test").
// No aparelho Test conserva sua identidade na UI; o Run e adaptado para Android VM em BuildPatches.
public static class BuildTarget
{
    const string Tag = "GmIde.Build";
    static readonly string[] LayoutFiles = { "defaultProjectLayout.xml", "convertProjectLayout.xml" };

    /// <summary>Antes de YoYoStudio.IDE.Bootstrap (chamado por BuildIntegration.Configure).</summary>
    public static void Configure(string ideDir)
    {
        LinkLayouts(ideDir, AppDomain.CurrentDomain.BaseDirectory);
        PatchStartScreenLayouts(ideDir);
        try { YoYoStudio.IDE.OnProjectLoaded.DoEveryTime(OnProjectLoaded); }
        catch (Exception e) { Log.Warn(Tag, "OnProjectLoaded: " + e.Message); }
    }

    /// <summary>
    /// Layout inicial de projeto para celular: copia do defaultProjectLayout.xml da IDE com o dock de
    /// baixo (Output, Search Results, Compile Errors...) em eDockState.Collapsed em vez de Expanded:
    /// vira uma faixa que abre com um toque e libera a altura para o editor. So vale para projetos
    /// sem layout salvo; o que o usuario arrumar depois fica salvo pela propria IDE.
    /// </summary>
    static void WriteMobileLayout(string src, string dst)
    {
        var doc = System.Xml.Linq.XDocument.Load(src, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        foreach (var dock in doc.Descendants("Dock").Where(d => (string?)d.Attribute("type") == "eBottom"))
            dock.SetAttributeValue("state", "Collapsed");
        // Docks laterais (Inspector, Asset Browser) um pouco mais estreitos que os 305 do desktop:
        // com a escala de toque eles comeriam o workspace.
        foreach (var dock in doc.Descendants("Dock").Where(d => (string?)d.Attribute("type") is "eLeft" or "eRight"))
            dock.SetAttributeValue("restore_size", "260dpi_px");
        string text = doc.Declaration + Environment.NewLine + doc.Root;
        var info = new FileInfo(dst);
        if (info.LinkTarget != null) File.Delete(dst);
        else if (info.Exists && File.ReadAllText(dst) == text) return;
        File.WriteAllText(dst, text);
        Log.Info(Tag, $"Layout: {dst} (copia de {src} com o dock inferior recolhido)");
    }

    /// <summary>
    /// Start Page no celular (StartScreenGUI/Skins/*/Layouts): com a escala de toque, as linhas de altura
    /// fixa (banner 124, botoes 96/120, titulo do painel New 60) deixavam ~30 unidades para a lista de
    /// templates. Banner, faixa dos botoes e titulos ficam mais baixos; a lista ganha ~100 unidades.
    /// Sempre parte do .original (a IDE so le os XML ao montar a tela).
    /// </summary>
    internal static void PatchStartScreenLayouts(string ideDir)
    {
        var edits = new (string file, string from, string to)[]
        {
            ("startscreen_layout.xml", "grid_rowlayout=\"['124dpi_px', '48dpi_px', '120dpi_px', '1*']\"",
                                       "grid_rowlayout=\"['56dpi_px', '48dpi_px', '80dpi_px', '1*']\""),
            ("startscreen_layout.xml", "grid_rowlayout=\"['124dpi_px', '42dpi_px', '96dpi_px', '1*']\"",
                                       "grid_rowlayout=\"['56dpi_px', '34dpi_px', '66dpi_px', '1*']\""),
            // Linha dos cards de tipo (Game, Live Wallpaper...): cards de 104 (StylePatches abaixo).
            ("startscreen_newproj_layout.xml", "grid_rowlayout=\"['150dpi_px', '26dpi_px', '1*']\"",
                                               "grid_rowlayout=\"['112dpi_px', '26dpi_px', '1*']\""),
            ("startscreen_newproj_layout.xml", "grid_rowlayout=\"['60dpi_px', '1*']\"", "grid_rowlayout=\"['42dpi_px', '1*']\""),
            ("startscreen_newproj_layout.xml", "grid_rowlayout=\"['60dpi_px', '1*', '62dpi_px']\"",
                                               "grid_rowlayout=\"['42dpi_px', '1*', '62dpi_px']\""),
        };
        try
        {
            var skins = Path.Combine(ideDir, "StartScreenGUI", "Skins");
            if (!Directory.Exists(skins)) return;
            foreach (var layouts in Directory.EnumerateDirectories(skins).Select(d => Path.Combine(d, "Layouts")).Where(Directory.Exists))
                foreach (var file in edits.Select(e => e.file).Distinct())
                {
                    var path = Path.Combine(layouts, file);
                    if (!File.Exists(path)) continue;
                    var orig = path + ".original";
                    if (!File.Exists(orig)) File.Copy(path, orig);
                    var text = File.ReadAllText(orig);
                    int n = 0;
                    foreach (var e in edits.Where(e => e.file == file))
                        if (text.Contains(e.from)) { text = text.Replace(e.from, e.to); n++; }
                    if (File.ReadAllText(path) != text) { File.WriteAllText(path, text); Log.Info(Tag, $"Start Page (celular): {path} ({n} ajustes)"); }
                }
            foreach (var styles in Directory.EnumerateDirectories(skins).Select(d => Path.Combine(d, "Styles", "startscreen_styles.json")).Where(File.Exists))
                PatchStartScreenStyles(styles);
        }
        catch (Exception e) { Log.Warn(Tag, "Layout da Start Page: " + e.Message); }
    }

    // Cards de tipo e de template (ss_template_button, 135x135 + margem 20) menores, com o icone e o
    // nome reposicionados para caber no card de 104 de altura.
    static readonly (string style, string key, string value)[] StyleEdits =
    {
        ("ss_template_button", "height", "104dpi_px"),
        ("ss_template_button", "width", "128dpi_px"),
        ("ss_template_button", "margin_right", "12dpi_px"),
        ("ss_template_button", "margin_bottom", "12dpi_px"),
        ("ss_template_button_image", "height", "96dpi_px"),
        ("ss_template_button_image", "width", "96dpi_px"),
        ("ss_blanktemplate_icon_large", "top", "16dpi_px"),
        ("ss_label_blanktemplate", "top", "62dpi_px"),
    };

    static void PatchStartScreenStyles(string path)
    {
        var orig = path + ".original";
        if (!File.Exists(orig)) File.Copy(path, orig);
        var text = File.ReadAllText(orig);
        int n = 0;
        foreach (var (style, key, value) in StyleEdits)
        {
            int start = text.IndexOf("\"" + style + "\":", StringComparison.Ordinal);
            if (start < 0) continue;
            int open = text.IndexOf('{', start), close = text.IndexOf('}', open);
            if (open < 0 || close < 0) continue;
            var block = text.Substring(open, close - open);
            var re = new System.Text.RegularExpressions.Regex("(\"" + key + "\"\\s*:\\s*\")[^\"]*(\")");
            if (!re.IsMatch(block)) continue;
            text = text.Substring(0, open) + re.Replace(block, "${1}" + value + "${2}", 1) + text.Substring(close);
            n++;
        }
        if (File.ReadAllText(path) != text) { File.WriteAllText(path, text); Log.Info(Tag, $"Start Page (celular): {path} ({n} estilos)"); }
    }

    internal static void LinkLayouts(string ideDir, string baseDir)
    {
        if (string.IsNullOrEmpty(baseDir)) return;
        if (Path.GetFullPath(baseDir).TrimEnd('/') == Path.GetFullPath(ideDir).TrimEnd('/')) return;
        foreach (var name in LayoutFiles)
        {
            var src = Path.Combine(ideDir, name);
            var dst = Path.Combine(baseDir, name);
            try
            {
                if (!File.Exists(src)) { Log.Warn(Tag, $"{src} nao existe (IDE importada sem o layout?)"); continue; }
                if (name == "defaultProjectLayout.xml") { WriteMobileLayout(src, dst); continue; }
                var info = new FileInfo(dst);
                if (info.LinkTarget == src) continue;
                if (info.Exists || info.LinkTarget != null) File.Delete(dst);
                try { File.CreateSymbolicLink(dst, src); }
                catch { File.Copy(src, dst, true); }
                Log.Info(Tag, $"Layout: {dst} -> {src}");
            }
            catch (Exception e) { Log.Warn(Tag, $"Layout {name}: {e.Message}"); }
        }
    }

    static void OnProjectLoaded(YoYoStudio.IDE.ProjectLoadedMessage _)
    {
        try
        {
            if (YoYoStudio.User.TargetManager.Platform == null)
            {
                Log.Warn(Tag, "Projeto aberto sem alvo (layout nao carregou); usando o alvo padrao da IDE");
                YoYoStudio.User.TargetManager.SetTarget(null, "", "");
            }
            Log.Info(Tag, $"Alvo: {YoYoStudio.User.TargetManager.Platform} / {YoYoStudio.User.TargetManager.Runtime} / " +
                          $"{YoYoStudio.User.TargetManager.Device} (Test = {YoYoStudio.User.TargetManager.TestPlatform_Actual})");
        }
        catch (Exception e) { Log.Error(Tag, "Alvo padrao: " + e); }
        CollapseBottomDockSoon(3);
    }

    // Celular: o dock de baixo (Output, Feather, Compile Errors...) sempre comeca recolhido ao abrir um
    // projeto, mesmo que o layout salvo do projeto esteja expandido (ocupa metade da altura). Depois de
    // alguns layouts, para vir depois da restauracao do layout e da janela do Feather.
    static void CollapseBottomDockSoon(int layouts)
    {
        try
        {
            YoYoStudio.GUI.WindowManager.OnPostLayout.DoOnce(delegate
            {
                if (layouts > 1) { CollapseBottomDockSoon(layouts - 1); return; }
                try
                {
                    var dock = YoYoStudio.IDE.WindowManager?.GetDock(YoYoStudio.GUI.Gadgets.eDockContainerType.eBottom);
                    if (dock != null && dock.DockState == YoYoStudio.GUI.Gadgets.eDockState.Expanded)
                    {
                        dock.Collapse();
                        Log.Info(Tag, "Dock de baixo recolhido ao abrir o projeto");
                    }
                }
                catch (Exception e) { Log.Warn(Tag, "Recolher dock de baixo: " + e.Message); }
            });
        }
        catch (Exception e) { Log.Warn(Tag, "Recolher dock de baixo: " + e.Message); }
    }

    /// <summary>
    /// Chamado pelo TargetSelectButton antes de aplicar um toque em Android. Abre a exportacao
    /// local e consome o evento, para nao deixar um toque na lista cair no fluxo de Opera GX.
    /// </summary>
    public static bool HandleAndroidTargetSelection(object selector, object? gadget, int index)
    {
        try
        {
            if (gadget == null || index < 0) return false;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var lists = selector.GetType().GetField("m_lists", flags)?.GetValue(selector) as System.Collections.IList;
            var getSelection = selector.GetType().GetMethod("GetSelection", flags);
            string? Selected(int column) => lists != null && lists.Count > column && getSelection != null
                ? AndroidTargetSelection.Tag(getSelection.Invoke(selector, new[] { lists[column] })) : null;
            var route = AndroidTargetSelection.Route(AndroidTargetSelection.Tag(gadget), Selected(0), Selected(1), Selected(2));
            if (route == LocalTargetAction.Original) return false;
            Log.Info(Tag, $"Selecao local: clique={AndroidTargetSelection.Tag(gadget)}, plataforma={Selected(0)}, runtime={Selected(1)}, dispositivo={Selected(2)}, acao={route}");
            var popup = selector.GetType().GetField("m_popupWindow", flags)?.GetValue(selector);
            var deactivate = popup?.GetType().GetMethods(flags).FirstOrDefault(m => m.Name == "DeactivatePopup" && m.GetParameters().Length == 0);
            deactivate?.Invoke(popup, null);
            if (route == LocalTargetAction.Unsupported)
            {
                new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!).Post(() =>
                    global::Android.Widget.Toast.MakeText(MainApplication.Instance, "A exportacao local usa Android VM e o dispositivo Default. YYC e dispositivos remotos nao sao suportados neste fluxo.", global::Android.Widget.ToastLength.Long)!.Show());
                return true;
            }
            ApkExport.Request("apk", "");
            ApkExport.RunPending();
            return true;
        }
        catch (Exception e)
        {
            Log.Error(Tag, "Intercept Android target: " + e);
            return false;
        }
    }
}
