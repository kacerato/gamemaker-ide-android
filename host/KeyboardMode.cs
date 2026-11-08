using Android.Runtime;
using Log = Android.Util.Log;
using YoYoStudio.GUI;
using YoYoStudio.GUI.Gadgets;
using YoYoStudio.GUI.Gadgets.Containers;
using YoYoStudio.GUI.Gadgets.Documents;
using GuiWindow = YoYoStudio.GUI.Gadgets.Window;
using GridLayout = YoYoStudio.GUI.Gadgets.GridLayout;

namespace GmIde;

// Modo de edicao de codigo com o teclado virtual aberto (docs/mobile-ui.md).
//
// Num celular em paisagem o teclado ocupa ~55% da altura. Com o editor de codigo focado:
//  - o teclado vem no modo codigo: sem sugestao/autocorrecao/maiuscula automatica
//    (SDLActivity.gmSetImeCode; o IME trocava "else" por "eles");
//  - o Java encolhe a SDLSurface para a faixa acima do teclado e poe a barra de simbolos
//    (GmIdeActivity.setImeShrink / barra de simbolos);
//  - aqui deixamos a faixa para o codigo: menu e barra de ferramentas da IDE desligados
//    (RequestDisabled nos StackedGadgets da desktop: o StackPanel pula filhos desligados) e paineis
//    laterais recolhidos. As abas de eventos e a barra de status ficam (navegacao e linha/coluna).
//  - o editor fica numa aba de tela cheia. As preferencias da IDE ja abrem codigo assim
//    (MobileUiPatches: OpenObjectEventsFullScreen etc.); se o editor ainda for uma janela do
//    workspace (layout antigo), ele e promovido para a aba sem perder o foco/cursor.
// Teclado fechado ou foco fora do editor: desfaz na ordem inversa.
//
// Campos comuns (nomes, numeros, busca): barra de eco sobre o teclado e teclado "sem sugestoes".
// Thread do SDL/IDE: OnFocusChange (IdeSetup) e OnFrame (WindowManager.OnPostRender).
static class KeyboardMode
{
    const string Tag = "GmIde.Kbd";

    // ------------------------------------------------------------------ JNI

    static IntPtr _cls, _setShrink, _imeField, _sdlCls, _setImeCode;
    static bool _jniFailed;
    static bool? _shrinkSent, _imeCodeSent;

    static bool EnsureJni()
    {
        if (_cls != IntPtr.Zero) return true;
        if (_jniFailed) return false;
        try
        {
            var local = JNIEnv.FindClass("io/gmide/GmIdeActivity");
            var cls = JNIEnv.NewGlobalRef(local);
            _setShrink = JNIEnv.GetStaticMethodID(cls, "setImeShrink", "(Z)V");
            _imeField = JNIEnv.GetStaticFieldID(cls, "sImeBottom", "I");
            var sdl = JNIEnv.FindClass("org/libsdl/app/SDLActivity");
            _sdlCls = JNIEnv.NewGlobalRef(sdl);
            _setImeCode = JNIEnv.GetStaticMethodID(_sdlCls, "gmSetImeCode", "(Z)V");
            _cls = cls;
            Log.Info(Tag, "Ponte JNI com io.gmide.GmIdeActivity / SDLActivity (teclado) pronta");
            return true;
        }
        catch (Exception e)
        {
            _jniFailed = true;
            Log.Error(Tag, "GmIdeActivity/SDLActivity (teclado) indisponivel: " + e.Message);
            return false;
        }
    }

    static void SendShrink(bool shrink)
    {
        if (_shrinkSent == shrink || !EnsureJni()) return;
        try
        {
            JNIEnv.CallStaticVoidMethod(_cls, _setShrink, new JValue(shrink));
            _shrinkSent = shrink;
            Log.Info(Tag, shrink ? "Teclado: modo editor (encolhe a IDE)" : "Teclado: modo campo");
        }
        catch (Exception e) { Log.Error(Tag, "setImeShrink: " + e.Message); }
    }

    // Tipo de entrada do IME; precisa ir antes do SDL_StartTextInput (mesma fila da thread de UI).
    static void SendImeCode(bool code)
    {
        if (_imeCodeSent == code || !EnsureJni()) return;
        try
        {
            JNIEnv.CallStaticVoidMethod(_sdlCls, _setImeCode, new JValue(code));
            _imeCodeSent = code;
            Log.Info(Tag, code ? "IME: codigo (sem sugestoes, sem correcao)" : "IME: campo (sem sugestoes)");
        }
        catch (Exception e) { Log.Error(Tag, "gmSetImeCode: " + e.Message); }
    }

    static int ReadImeBottom()
    {
        if (!EnsureJni()) return 0;
        try { return JNIEnv.GetStaticIntField(_cls, _imeField); }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ barra de texto sobre o teclado

    static WeakReference<GUIBase>? _textGadget;
    static IntPtr _setEcho;
    static string? _echoSent;
    static bool _echoShown;

    static void SendEcho(string? text)
    {
        if (!_echoShown && text == null) return;
        if (_echoShown && text == _echoSent) return;
        if (!EnsureJni()) return;
        try
        {
            if (_setEcho == IntPtr.Zero) _setEcho = JNIEnv.GetStaticMethodID(_cls, "setImeEcho", "(Ljava/lang/String;)V");
            IntPtr s = text == null ? IntPtr.Zero : JNIEnv.NewString(text);
            try { JNIEnv.CallStaticVoidMethod(_cls, _setEcho, new JValue(s)); }
            finally { if (s != IntPtr.Zero) JNIEnv.DeleteLocalRef(s); }
            _echoSent = text;
            _echoShown = text != null;
        }
        catch (Exception e) { Log.Warn(Tag, "setImeEcho: " + e.Message); }
    }

    static void UpdateEcho(int ime)
    {
        GUIBase? g = null;
        _textGadget?.TryGetTarget(out g);
        if (g == null || g.Destroyed || ime <= 0) { SendEcho(null); return; }
        SendEcho(ReadText(g) ?? "");
    }

    // Texto do gadget focado ou do primeiro pai com uma propriedade publica string "Text" (TextBox,
    // NumericTextBox, SearchBar...).
    static string? ReadText(GUIBase g)
    {
        for (GUIBase? cur = g; cur != null; cur = cur.parent)
        {
            var p = cur.GetType().GetProperty("Text", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (p != null && p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            {
                try { return p.GetValue(cur) as string; } catch { return null; }
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ o que e editor de codigo

    const string CodeEditor2Panel = "YoYoStudio.Plugins.CodeEditor2.GUI.TextEditorPanel";

    /// <summary>
    /// Painel de codigo focado: o TextEditorPanel do IDE.dll (Code Editor 1: scripts, eventos,
    /// shaders, previa do Inspetor, DnD) ou o do Code Editor 2 (plugin, comparado pelo nome).
    /// So o gadget e dois pais: a barra de status do editor nao conta como campo de codigo.
    /// </summary>
    internal static GUIBase? CodePanelOf(GUIBase? g)
    {
        var cur = g;
        for (int i = 0; cur != null && i < 3; i++, cur = cur.parent)
        {
            if (cur is YoYoStudio.GUI.Gadgets.TextEditor.TextEditorPanel) return cur;
            if (cur.GetType().FullName == CodeEditor2Panel) return cur;
        }
        return null;
    }

    /// <summary>Editor de documento (aba de script/evento) e nao previa embutida: pode ocupar a tela.</summary>
    static bool IsDocumentEditor(GUIBase panel) =>
        panel is not YoYoStudio.GUI.Gadgets.TextEditor.TextEditorPanel tep || tep.AssociatedTab != null;

    static DocumentWindow? DocumentWindowOf(GUIBase g)
    {
        for (var cur = g.parent; cur != null; cur = cur.parent)
            if (cur is DocumentWindow dw) return dw;
        return null;
    }

    // ------------------------------------------------------------------ foco

    static WeakReference<GUIBase>? _codeGadget;   // painel de codigo focado (null = fora do codigo)
    static WeakReference<GUIBase>? _promoting;    // painel sendo levado para a aba de tela cheia

    /// <summary>
    /// Enquanto um editor do workspace vira aba, a IDE foca a janela nova (foco de gadget = null) e
    /// depois devolvemos o foco ao painel. IdeSetup ignora esse foco transitorio (nao fecha o teclado).
    /// </summary>
    internal static bool Promoting
    {
        get { GUIBase? p = null; _promoting?.TryGetTarget(out p); return p != null && !p.Destroyed; }
    }

    // Chamado por IdeSetup antes de SDL_StartTextInput / StopTextInput.
    public static void OnFocusChange(GUIBase? gadget, bool isText)
    {
        try
        {
            var panel = isText ? CodePanelOf(gadget) : null;
            _codeGadget = panel != null ? new WeakReference<GUIBase>(panel) : null;
            // Campo comum (busca, nome, numero...): barra de texto sobre o teclado (OnFrame -> SendEcho).
            _textGadget = isText && panel == null && gadget != null ? new WeakReference<GUIBase>(gadget) : null;
            if (_textGadget == null) SendEcho(null);
            if (!isText) return;

            SendImeCode(panel != null);
            bool fullscreen = panel != null && IsDocumentEditor(panel);
            SendShrink(fullscreen);

            // Editor de documento ainda numa janela do workspace: vira aba de tela cheia mantendo o foco.
            if (fullscreen && DocumentWindowOf(panel!) is DocumentWindow dw && !dw.IsFullscreen() && !Promoting)
                Promote(panel!, dw);
        }
        catch (Exception e) { Log.Error(Tag, "OnFocusChange: " + e); }
    }

    static void Promote(GUIBase panel, DocumentWindow dw)
    {
        _promoting = new WeakReference<GUIBase>(panel);
        WindowManager.OnPostInput.DoOnce(delegate
        {
            try
            {
                var click = typeof(GuiWindow).GetMethod("OnMaximizeClick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (click == null || dw.Destroyed || panel.Destroyed) { _promoting = null; return; }
                click.Invoke(dw, null);   // DocumentWindow: MenuFullScreenClick -> aba de tela cheia
                // TransferTab mantem o mesmo TextEditorPanel (e o cursor): so devolver o foco.
                WindowManager.OnPostInput.DoOnce(delegate
                {
                    try
                    {
                        if (!panel.Destroyed) YoYoStudio.IDE.WindowManager?.SetFocus(panel);
                        Log.Info(Tag, "Editor do workspace aberto em aba de tela cheia (foco mantido)");
                    }
                    catch (Exception e) { Log.Warn(Tag, "Devolver foco ao editor: " + e.Message); }
                    finally { _promoting = null; }
                });
            }
            catch (Exception e) { _promoting = null; Log.Warn(Tag, "Promover editor: " + e.Message); }
        });
    }

    static bool IsInside(GUIBase g, GUIBase ancestor)
    {
        for (var cur = g; cur != null; cur = cur.parent)
            if (cur == ancestor) return true;
        return false;
    }

    // ------------------------------------------------------------------ quadro

    static WindowManager.DesktopPostRenderEvent? _frameHandler; // ref forte (WeakEventStruct guarda fraco)

    public static void Install()
    {
        if (_frameHandler != null) return;
        try
        {
            _frameHandler = OnFrame;
            WindowManager.OnPostRender.DoEveryTime(_frameHandler);
            Log.Info(Tag, "WindowManager.OnPostRender assinado (modo codigo com teclado)");
        }
        catch (Exception e) { Log.Error(Tag, "Falha ao assinar OnPostRender: " + e); }
    }

    static bool _active;
    static int _offFrames;         // quadros seguidos com teclado fechado

    static void OnFrame(int desktopId, TimeSpan delta)
    {
        try
        {
            int ime = ReadImeBottom();
            UpdateEcho(ime);
            GUIBase? code = null;
            _codeGadget?.TryGetTarget(out code);
            if (code != null && code.Destroyed) code = null;

            if (!_active)
            {
                if (ime > 0 && code != null && _shrinkSent == true && !Promoting) Enter(code);
                return;
            }

            // Teclado fecha: espera alguns quadros (troca de campo fecha/reabre rapido).
            _offFrames = ime > 0 ? 0 : _offFrames + 1;
            bool focusGone = code == null && !Promoting && !FocusInsideWindow();
            if (_offFrames > 6 || focusGone || _win == null || _win.Destroyed)
                Exit(focusGone ? "foco saiu do editor" : "teclado fechado");
        }
        catch (Exception e)
        {
            Log.Error(Tag, "OnFrame: " + e);
            try { Exit("erro"); } catch { }
        }
    }

    static bool FocusInsideWindow()
    {
        var g = YoYoStudio.IDE.WindowManager?.ActiveGadget;
        return g != null && _win != null && IsInside(g, _win);
    }

    // ------------------------------------------------------------------ entrar / sair

    static GUIBase? _win;
    static readonly List<GUIBase> _hidden = new();
    static readonly List<DockContainer> _collapsed = new();
    static StackPanel? _stack;

    static void Enter(GUIBase code)
    {
        var wm = YoYoStudio.IDE.WindowManager;
        if (wm == null) return;
        GUIBase win = (GUIBase?)DocumentWindowOf(code) ?? code;
        var details = wm.GetDesktopDetails(wm.FindContainingDesktop(win));
        var stack = details?.stackPanel;
        if (details == null || stack == null) { Log.Info(Tag, "Desktop do editor sem StackPanel; nada a fazer"); return; }

        _win = win;
        _stack = stack;
        _active = true;
        _offFrames = 0;

        // 1. Menu e barra de ferramentas da IDE. O StackPanel move os filhos para um InternalStackPanel no
        //    layout; a lista real e StackedGadgets: [menu, barra de ferramentas, grade dos docks].
        foreach (var c in stack.StackedGadgets.ToArray())
            if (c != null && !c.RequestDisabled && !IsInside(win, c))
            {
                c.RequestDisabled = true;
                _hidden.Add(c);
            }

        // 2. Paineis laterais/inferior expandidos (menos o que contem o editor).
        try
        {
            foreach (var d in details.DockMan?.GetDocks() ?? Array.Empty<DockContainer>())
                if (d != null && d.DockState == eDockState.Expanded && !IsInside(win, d))
                {
                    d.Collapse();
                    _collapsed.Add(d);
                }
        }
        catch (Exception e) { Log.Error(Tag, "Recolher docks: " + e.Message); }

        // 3. Faixas de abas (abas da IDE e sub-abas de evento/script): so a linha do conteudo fica. Para
        //    trocar de evento basta fechar o teclado (as faixas voltam).
        HideTabStrip(details.Chrome?.tabControl);
        if (win is DocumentWindow dw)
            for (int i = 0; ; i++) { var tc = dw.GetTabControl(i); if (tc == null) break; HideTabStrip(tc); }

        stack.DirtyLayout();
        _lastCodeH = -1;
        Log.Info(Tag, $"Modo codigo: {win.GetType().Name}, {_hidden.Count} barra(s) escondida(s), {_collapsed.Count} painel(is) recolhido(s), " +
                      $"{_strips.Count} faixa(s) de abas escondida(s)");
    }

    static readonly List<(TabControl tc, GridLayout[] rows)> _strips = new();

    // TabControl e um Grid: a celula ClientPanelGridCell e o conteudo, as outras linhas sao a faixa.
    static void HideTabStrip(TabControl? tc)
    {
        if (tc == null || tc.Destroyed) return;
        try
        {
            var rows = tc.GridRowLayout;
            int client = tc.ClientPanelGridCell.y;
            if (rows == null || rows.Length < 2 || client < 0 || client >= rows.Length) return;
            var hidden = new GridLayout[rows.Length];
            for (int i = 0; i < rows.Length; i++)
                hidden[i] = i == client ? rows[i] : new GridLayout(0f, eGridLayoutMode.Pixel);
            _strips.Add((tc, rows));
            tc.GridRowLayout = hidden;
            tc.DirtyLayout();
        }
        catch (Exception e) { Log.Warn(Tag, "Esconder faixa de abas: " + e.Message); }
    }

    // A area do codigo muda de altura (teclado, barras): leva a vista ate o cursor.
    /// <summary>
    /// Thread da IDE, toda volta do loop (MobileFrame / WindowManager.OnProcess). OnPostRender so roda
    /// quando ha quadro a desenhar, e com a tela parada o ajuste do cursor nunca acontecia.
    /// </summary>
    internal static void OnProcessFrame()
    {
        if (!_active) return;
        GUIBase? code = null;
        _codeGadget?.TryGetTarget(out code);
        if (code != null && !code.Destroyed && ReadImeBottom() > 0) KeepCursorVisible(code);
    }

    // Espera a altura ficar 3 quadros igual (teclado anima, barra mede a si mesma) e so entao rola.
    static float _lastCodeH = -1;
    static int _stableFrames = -1;
    static void KeepCursorVisible(GUIBase code)
    {
        var r = code.AbsoluteClipRect;
        if (r == null || r.h <= 0) return;
        if (Math.Abs(r.h - _lastCodeH) >= 1) { _lastCodeH = r.h; _stableFrames = 0; return; }
        if (_stableFrames < 0 || ++_stableFrames < 3) return;
        _stableFrames = -1;
        if (code is YoYoStudio.GUI.Gadgets.TextEditor.TextEditorPanel tep)
            try { tep.ScrollToCursor(); tep.RequireRender = true; Log.Debug(Tag, $"Cursor visivel (area {r.h:F0}px)"); }
            catch (Exception e) { Log.Warn(Tag, "ScrollToCursor: " + e.Message); }
    }

    static void Exit(string why)
    {
        if (!_active) return;
        _active = false;

        foreach (var d in _collapsed)
            try { if (!d.Destroyed && d.DockState != eDockState.Expanded) d.Expand(); }
            catch (Exception e) { Log.Error(Tag, "Expandir dock: " + e.Message); }

        foreach (var c in _hidden)
            try { if (!c.Destroyed) c.RequestDisabled = false; }
            catch (Exception e) { Log.Error(Tag, "Reativar barra: " + e.Message); }

        foreach (var (tc, rows) in _strips)
            try { if (!tc.Destroyed) { tc.GridRowLayout = rows; tc.DirtyLayout(); } }
            catch (Exception e) { Log.Error(Tag, "Restaurar faixa de abas: " + e.Message); }
        _strips.Clear();

        try { if (_stack != null && !_stack.Destroyed) _stack.DirtyLayout(); } catch { }
        Log.Info(Tag, $"Layout normal restaurado ({why}): {_hidden.Count} barra(s), {_collapsed.Count} painel(is)");

        _hidden.Clear();
        _collapsed.Clear();
        _win = null; _stack = null;
    }
}
