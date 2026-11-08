using System.Runtime.CompilerServices;
using Android.Util;
using YoYoStudio.GUI;
using YoYoStudio.GUI.Gadgets;
using YoYoStudio.GUI.Gadgets.Containers;
using YoYoStudio.GUI.Gadgets.Documents;
using GuiWindow = YoYoStudio.GUI.Gadgets.Window;

namespace GmIde;

// Politica de janelas da IDE para tela de celular (docs/mobile-ui.md). Tres regras:
//
// 1. Editores no workspace abrem em 100% e a vista vai ate eles. Antes o workspace era afastado
//    (zoom) ate a janela caber inteira, o que deixava tudo minusculo, e as janelas encadeadas
//    (codigo de eventos) abriam fora da tela. Agora: ao abrir (AddChainRoot) os paineis laterais
//    expandidos recolhem (o workspace ganha a largura da tela; tocar na aba do painel o abre de
//    novo) e, a cada troca de janela focada, se ela nao estiver visivel a vista anda ate ela, sem
//    zoom. O codigo vai para uma aba cheia pelas preferencias da propria IDE (padroes mudados em
//    MobileUiPatches: OpenObjectEventsFullScreen/OpenScriptsFullScreen/OpenShadersFullScreen/FocusPan).
//
// 2. Dialogos de editor (StripImportDialog "Converter para quadros", confirmacoes, redimensionar...)
//    eram filhos da janela do editor (Window.AddWindow): centralizados no painel do editor, que e
//    menor que eles, ficavam com posicao negativa e cortados, e herdavam o zoom do workspace. Sobem
//    para a desktop (WindowManager.AddWindow, como ConfirmDialog e Preferencias) e fecham junto com
//    o dono.
//
// 3. Toda janela flutuante da desktop fica dentro da tela. Janelas redimensionaveis encolhem (o
//    conteudo reflui); dialogos de tamanho fixo tem o conteudo reduzido proporcionalmente
//    (ClientPanel.Layout.ClipScale, o mesmo mecanismo do zoom do workspace: layout, clique e
//    rolagem continuam coerentes) so o necessario para caber. Depois de ajustada, a janela pode
//    ser arrastada; volta para dentro se sair da tela.
//
// Tudo na thread da IDE (MobileFrame / ganchos de IL chamados pela propria IDE).
static class MobileWindowPolicy
{
    internal const string Tag = "GmIde.Windows";

    sealed class FitState
    {
        public float LastX = float.NaN, LastY, LastW, LastH;
        public bool Scaled;
        public OnCloseDelegate? OwnerClose;      // ref forte do handler no dono (evento fraco)
        public WeakReference<GUIBase>? Owner;
    }

    static readonly ConditionalWeakTable<GUIBase, FitState> _states = new();
    static readonly List<WeakReference<GuiWindow>> _floating = new();

    // ------------------------------------------------------------------ ganchos de IL

    [ThreadStatic] static bool _redirecting;

    /// <summary>
    /// Prefixo de GUIBase.AddWindow(GUIBase): dialogos de editor vao para a desktop.
    /// true = ja tratado (o metodo original retorna).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool RedirectChildWindow(object owner, object child)
    {
        if (_redirecting || owner is not GUIBase ownerG || child is not GuiWindow dlg) return false;
        if (dlg is DocumentWindow || !dlg.GetType().Name.EndsWith("Dialog", StringComparison.Ordinal)) return false;
        if (ownerG.parent == null) return false;   // raiz da desktop: ja e o lugar certo
        var wm = YoYoStudio.IDE.WindowManager;
        if (wm == null) return false;
        int desk = wm.FindContainingDesktop(ownerG);
        _redirecting = true;
        try { wm.AddWindow(dlg, false, true, desk); }   // no topo da desktop do dono
        finally { _redirecting = false; }
        // Janela que fecha o dialogo junto: a mais proxima do dono (ou o proprio dono).
        GuiWindow? ownerWin = null;
        for (var cur = ownerG; cur != null && ownerWin == null; cur = cur.parent) ownerWin = cur as GuiWindow;
        var st = _states.GetOrCreateValue(dlg);
        st.Owner = new WeakReference<GUIBase>(ownerG);
        if (ownerWin != null)
        {
            var dlgRef = new WeakReference<GuiWindow>(dlg);
            st.OwnerClose = () =>
            {
                // O dono fechou: o dialogo era filho dele e morria junto; agora fecha explicitamente.
                if (dlgRef.TryGetTarget(out var d) && !d.Destroyed && !d.Closing) d.Close();
            };
            ownerWin.OnCloseBegin.DoEveryTime(st.OwnerClose);
        }
        Track(dlg);
        Log.Info(Tag, $"Dialogo '{dlg.GetType().Name}' de '{ownerG.GetType().Name}' aberto na desktop");
        return true;
    }

    /// <summary>Fim de WindowManager.AddWindow: janelas flutuantes da desktop entram no ajuste a tela.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void OnWindowAdded(object window, bool addToWorkspace)
    {
        if (addToWorkspace || window is not GuiWindow w || w is DocumentWindow) return;
        Track(w);
    }

    static void Track(GuiWindow w)
    {
        foreach (var r in _floating) if (r.TryGetTarget(out var x) && x == w) return;
        _floating.Add(new WeakReference<GuiWindow>(w));
    }

    /// <summary>AddChainRoot terminou (gancho em IdePatches.PatchChainRootFit).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void OnChainRootAdded(object root)
    {
        if (root is not GUIBase g) return;
        CollapseSideDocks(g);
        WindowManager.OnPostLayout.DoOnce(delegate { ShowWindow(g); });
    }

    // ------------------------------------------------------------------ quadro

    static WeakReference<GUIBase>? _lastFocusWin;

    internal static void OnFrame()
    {
        try { FollowFocus(); } catch (Exception e) { Log.Warn(Tag, "Foco: " + e.Message); }
        try { FitFloating(); } catch (Exception e) { Log.Warn(Tag, "Janelas flutuantes: " + e.Message); }
    }

    // Janela do workspace que contem o gadget focado: a mais externa antes do WorkspaceChain.
    static GuiWindow? ChainWindowOf(GUIBase? g, out WorkspaceArea? area)
    {
        area = null;
        GuiWindow? win = null;
        bool inChain = false;
        for (var cur = g; cur != null; cur = cur.parent)
        {
            if (cur is WorkspaceArea a) { area = a; break; }
            if (cur is WorkspaceChain) { inChain = true; continue; }
            if (!inChain && cur is GuiWindow w) win = w;
        }
        return inChain && area != null ? win : null;
    }

    static void FollowFocus()
    {
        var wm = YoYoStudio.IDE.WindowManager;
        var win = ChainWindowOf(wm?.ActiveGadget, out var area);
        if (win == null || area == null) return;
        GUIBase? last = null;
        _lastFocusWin?.TryGetTarget(out last);
        if (last == win) return;
        _lastFocusWin = new WeakReference<GUIBase>(win);
        if (!IsVisibleIn(win, area)) ShowWindow(win);
    }

    static bool IsVisibleIn(GUIBase win, WorkspaceArea area)
    {
        var v = area.AbsoluteClipRect; var r = win.AbsoluteContentRect;
        if (v == null || r == null || r.w <= 0 || r.h <= 0) return true;
        // Visivel o bastante: o canto superior esquerdo e ao menos 60% da largura que cabe.
        float visW = Math.Min(r.x + r.w, v.x + v.w) - Math.Max(r.x, v.x);
        return r.x >= v.x - 2 && r.y >= v.y - 2 && r.y < v.y + v.h * 0.5f && visW >= Math.Min(r.w, v.w) * 0.6f;
    }

    /// <summary>Zoom 100% e vista ate a janela (canto superior esquerdo se ela for maior que a area).</summary>
    static void ShowWindow(GUIBase win)
    {
        try
        {
            if (win.Destroyed) return;
            WorkspaceArea? area = null;
            for (var p = win.parent; p != null; p = p.parent) if (p is WorkspaceArea a) { area = a; break; }
            if (area == null) return;
            area.AbortPanAnimation();
            if (area.Layout.ClipScale < 1f) { area.Layout.ClipScale = 1f; area.DirtyLayout(); }
            WindowManager.OnPostLayout.DoOnce(delegate
            {
                try { if (!win.Destroyed) area.PanToGadget(win, false); }
                catch (Exception e) { Log.Warn(Tag, "PanToGadget: " + e.Message); }
            });
        }
        catch (Exception e) { Log.Warn(Tag, "Mostrar janela: " + e.Message); }
    }

    /// <summary>
    /// Paineis laterais como gavetas no espaco de trabalho: um toque concluido no workspace (janelas
    /// de editores: sprite, objeto...) recolhe o Inspetor/Recursos abertos; tocar na aba do painel abre
    /// de novo. So no WorkspaceArea: no editor de sala e no de imagem os paineis sao as ferramentas
    /// (camadas, paleta de tiles) e recolher a cada toque no canvas atrapalharia; no codigo o modo
    /// teclado ja recolhe. Thread da IDE (observador de mouseup do IdeSetup).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void OnTapUp()
    {
        try
        {
            var wm = YoYoStudio.IDE.WindowManager;
            var g = wm?.GetMouseOverGadget();
            if (wm == null || g == null) return;
            bool inWorkspace = false;
            for (var cur = g; cur != null; cur = cur.parent)
            {
                if (cur is DockContainer) return;
                if (cur is WorkspaceArea) { inWorkspace = true; break; }
            }
            if (!inWorkspace) return;
            CollapseSideDocks(g);
        }
        catch (Exception e) { Log.Warn(Tag, "Gaveta: " + e.Message); }
    }

    /// <summary>Recolhe os paineis laterais expandidos da desktop da janela (como "recolher docks").</summary>
    internal static void CollapseSideDocks(GUIBase near)
    {
        try
        {
            var wm = YoYoStudio.IDE.WindowManager;
            var details = wm?.GetDesktopDetails(wm.FindContainingDesktop(near));
            foreach (var d in details?.DockMan?.GetDocks() ?? Array.Empty<DockContainer>())
            {
                if (d == null || d.Destroyed || d.DockState != eDockState.Expanded) continue;
                if (d.DockType is not (eDockContainerType.eLeft or eDockContainerType.eRight)) continue;
                if (IsInside(near, d)) continue;
                d.Collapse();
                Log.Info(Tag, $"Painel {d.DockType} recolhido (gaveta)");
            }
        }
        catch (Exception e) { Log.Warn(Tag, "Recolher paineis: " + e.Message); }
    }

    static bool IsInside(GUIBase g, GUIBase ancestor)
    {
        for (var cur = g; cur != null; cur = cur.parent) if (cur == ancestor) return true;
        return false;
    }

    // ------------------------------------------------------------------ janelas flutuantes

    static void FitFloating()
    {
        for (int i = _floating.Count - 1; i >= 0; i--)
        {
            if (!_floating[i].TryGetTarget(out var w) || w.Destroyed) { _floating.RemoveAt(i); continue; }
            if (w.Closing || w.parent == null || w.Minimized) continue;
            // Dialogo levado para a desktop cujo dono sumiu (aba do editor fechada): fecha junto.
            if (_states.TryGetValue(w, out var s0) && s0.Owner != null &&
                (!s0.Owner.TryGetTarget(out var owner) || owner.Destroyed))
            {
                w.Close();
                continue;
            }
            FitOne(w);
        }
    }

    static void FitOne(GuiWindow w)
    {
        var desk = w.parent.AbsoluteClipRect;
        var r = w.AbsoluteContentRect;
        if (desk == null || r == null || desk.w <= 0 || desk.h <= 0 || r.w <= 0 || r.h <= 0) return;
        var st = _states.GetOrCreateValue(w);
        if (r.x == st.LastX && r.y == st.LastY && r.w == st.LastW && r.h == st.LastH) return;   // nada mudou

        GUIMeasurement? mw = w.Width, mh = w.Height;
        bool dpi = mw != null && mh != null && mw.mode == eGUIMeasurementMode.DpiPixel && mh.mode == eGUIMeasurementMode.DpiPixel;
        if (!dpi) { Remember(st, r); return; }      // janelas de preenchimento (chrome etc.): nao mexe
        float ppuX = r.w / mw!.value, ppuY = r.h / mh!.value;   // px por unidade
        float deskW = desk.w / ppuX, deskH = desk.h / ppuY;      // desktop em unidades
        float winW = mw.value, winH = mh.value;
        const float MaxW = 0.97f, MaxH = 0.95f;
        bool changed = false;

        if (winW > deskW * MaxW || winH > deskH * MaxH)
        {
            bool resizable = (w.WindowFlags & eWindowFlags.ResizeButton) != 0;
            var client = w.ClientPanel;
            if (!resizable && client != null && !st.Scaled)
            {
                // Tamanho fixo: reduz o conteudo (a moldura/titulo fica). Area do cliente em unidades:
                var c = client.AbsoluteContentRect;
                float chromeW = c != null && c.w > 0 ? winW - c.w / ppuX : 0;
                float chromeH = c != null && c.h > 0 ? winH - c.h / ppuY : 0;
                float cw = winW - chromeW, ch = winH - chromeH;
                float s = Math.Min(1f, Math.Min((deskW * MaxW - chromeW) / cw, (deskH * MaxH - chromeH) / ch));
                s = Math.Max(0.5f, s);
                client.Layout.ClipScale = s;
                winW = chromeW + cw * s; winH = chromeH + ch * s;
                st.Scaled = true;
                Log.Info(Tag, $"'{w.GetType().Name}' {mw.value:F0}x{mh.value:F0} nao cabe em {deskW:F0}x{deskH:F0}: conteudo a {s:P0}");
            }
            else
            {
                winW = Math.Min(winW, deskW * MaxW);
                winH = Math.Min(winH, deskH * MaxH);
                Log.Info(Tag, $"'{w.GetType().Name}' {mw.value:F0}x{mh.value:F0} -> {winW:F0}x{winH:F0} (desktop {deskW:F0}x{deskH:F0})");
            }
            w.Width = GUIMeasurement.DpiPixels((float)Math.Floor(winW));
            w.Height = GUIMeasurement.DpiPixels((float)Math.Floor(winH));
            changed = true;
        }

        // Posicao dentro da desktop (em unidades relativas a ela).
        float x = (r.x - desk.x) / ppuX, y = (r.y - desk.y) / ppuY;
        float nx = Math.Clamp(x, 0, Math.Max(0, deskW - winW));
        float ny = Math.Clamp(y, 0, Math.Max(0, deskH - winH));
        if (changed || Math.Abs(nx - x) > 0.5f || Math.Abs(ny - y) > 0.5f)
        {
            if (changed && float.IsNaN(st.LastX))
            {
                // Primeira vez: centraliza.
                nx = Math.Max(0, (deskW - winW) / 2); ny = Math.Max(0, (deskH - winH) / 2);
            }
            w.Right = null; w.Bottom = null;
            w.Left = GUIMeasurement.DpiPixels((float)Math.Floor(nx));
            w.Top = GUIMeasurement.DpiPixels((float)Math.Floor(ny));
            changed = true;
        }
        if (changed)
        {
            w.DirtyLayout();
            st.LastX = -1e9f;   // ja ajustada uma vez (nao recentraliza); remede depois do layout
            return;
        }
        Remember(st, r);
    }

    static void Remember(FitState st, YoYoStudio.Core.Utils.rect r) { st.LastX = r.x; st.LastY = r.y; st.LastW = r.w; st.LastH = r.h; }
}
