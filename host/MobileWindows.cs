using System.Runtime.CompilerServices;
using Android.Util;
using Core.CoreOS.System;
using YoYoStudio.GUI;
using YoYoStudio.GUI.Gadgets.Containers;

namespace GmIde;

// Fachada da camada de janelas mobile chamada pelos ganchos de IL (IdePatches/MobileUiPatches) e pelo
// TouchInput. Nao cita tipos do IDE.dll em assinaturas: esses chamadores sao carregados antes de o
// IDE.dll poder ser resolvido. A politica em si (editores em 100% com a vista acompanhando o foco,
// dialogos na desktop e dentro da tela) fica em MobileWindowPolicy (docs/mobile-ui.md).
//
// Modo "Mover" da barra de atalhos (TouchInput): um dedo arrasta o workspace e a pinca da zoom,
// mesmo com o dedo em cima de uma janela. Continua disponivel, mas a politica de foco torna o uso raro.
public static class MobileWindows
{
    /// <summary>Fim de WindowManager.AddChainRoot(GUIBase, TabItem).</summary>
    public static void OnChainRootAdded(object root)
    {
        try { MobileWindowPolicy.OnChainRootAdded(root); }
        catch (Exception e) { Log.Warn(MobileWindowPolicy.Tag, "OnChainRootAdded: " + e.Message); }
    }

    /// <summary>Prefixo de Window.AddWindow(GUIBase, bool): true = dialogo aberto na desktop.</summary>
    public static bool RedirectChildWindow(object owner, object child)
    {
        try { return MobileWindowPolicy.RedirectChildWindow(owner, child); }
        catch (Exception e) { Log.Warn(MobileWindowPolicy.Tag, "Dialogo na desktop: " + e.Message); return false; }
    }

    /// <summary>Fim de WindowManager.AddWindow(GUIBase, bool, bool, int).</summary>
    public static void OnWindowAdded(object window, bool addToWorkspace)
    {
        try { MobileWindowPolicy.OnWindowAdded(window, addToWorkspace); }
        catch (Exception e) { Log.Warn(MobileWindowPolicy.Tag, "Janela adicionada: " + e.Message); }
    }

    /// <summary>
    /// No lugar de WindowManager.AddChainRoot(window, tab) em GameOptionsDialog.OpenWindow (patch em
    /// IdePatches.PatchGameOptionsFloating): a Game Options abre como janela flutuante, como as
    /// Preferencias (AddWindow + SetFocus); o ajuste a tela vem da politica (OnWindowAdded).
    /// </summary>
    public static void AddGameOptionsFloating(object windowManager, object window, object workspaceTab)
    {
        try { MobileWindowFit.AddFloating(windowManager, window); }
        catch (Exception e)
        {
            Log.Warn(MobileWindowPolicy.Tag, "Game Options flutuante: " + e.Message + "; abrindo no workspace");
            MobileWindowFit.AddChainRootFallback(windowManager, window, workspaceTab);
        }
    }
}

/// <summary>Arraste/pinca do modo Mover. Chamado pelo TouchInput (thread de UI do Java).</summary>
public static class MobileWorkspace
{
    static readonly object _lock = new();
    static float _panX, _panY;     // px de tela a mover (positivo = ver o que esta a direita/abaixo)
    static int _zoomSteps;          // >0 aproxima

    public static void AddPan(float dx, float dy) { lock (_lock) { _panX += dx; _panY += dy; } }
    public static void AddZoom(int steps) { lock (_lock) _zoomSteps += steps; }

    internal static bool Take(out float dx, out float dy, out int zoom)
    {
        lock (_lock)
        {
            dx = _panX; dy = _panY; zoom = _zoomSteps;
            _panX = _panY = 0; _zoomSteps = 0;
        }
        return dx != 0 || dy != 0 || zoom != 0;
    }

    static SystemPlatform.OnMouseMotionDelegate? _observer;

    /// <summary>Thread da IDE, junto dos outros observadores (IdeSetup.InstallTextInput).</summary>
    public static void Install()
    {
        _observer ??= (win, x, y) => MobileWindowFit.ApplyPending();
        SystemPlatform.OnMouseMotion -= _observer;
        SystemPlatform.OnMouseMotion += _observer;
    }
}

static class MobileWindowFit
{
    internal const string Tag = "GmIde.Windows";

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AddFloating(object windowManager, object window)
    {
        var wm = (IWindowInterface)windowManager;
        var win = (GUIBase)window;
        wm.AddWindow(win);   // gancho OnWindowAdded: entra no ajuste a tela da politica
        wm.SetFocus(win);
        Log.Info(Tag, $"'{win.GetType().Name}' aberta como janela flutuante");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void AddChainRootFallback(object windowManager, object window, object workspaceTab) =>
        ((IWindowInterface)windowManager).AddChainRoot((GUIBase)window, workspaceTab as YoYoStudio.GUI.Gadgets.TabItem);

    // ---- rolagem com um dedo: o que esta sob o dedo (TouchInput.ProbeGen), uma vez por toque

    static int _probed;
    // Editor de codigo: um dedo rola o texto (como nos editores de celular); selecionar texto e
    // segurar e arrastar (toque longo armado, TouchInput). Ele tem barras proprias
    // (TextEditorPanelVerticalScrollBar/HorizontalScrollBar).
    static readonly string[] CodeScrollTypes = { "TextEditorPanel", "TextEditorComponent", "DocumentScrollPanel" };

    /// <summary>
    /// Thread da IDE, a cada quadro (MobileFrame). Uma vez por toque: o gadget sob o dedo esta numa
    /// area rolavel? Paineis com rolagem (ScrollPanel com flag de rolagem, listas, arvores, tabelas)
    /// e o editor de codigo sim; canvas (room, sprite, tileset) e o workspace nao, porque neles
    /// arrastar desenha/move. O ScrollBar encontrado e o alvo da rolagem direta (MobileScrollTarget).
    /// </summary>
    internal static void ProbeScroll()
    {
        try
        {
            int gen = TouchInput.ProbeGen;
            if (gen == _probed) return;
            // O movimento "sem botao" do toque precisa ja ter chegado a IDE.
            if (Math.Abs(WindowManager.MouseX - TouchInput.ProbeX) > 3 || Math.Abs(WindowManager.MouseY - TouchInput.ProbeY) > 3) return;
            _probed = gen;
            GUIBase? container = null;
            for (GUIBase? cur = YoYoStudio.IDE.WindowManager?.GetMouseOverGadget(); cur != null; cur = cur.parent)
            {
                if (cur is WorkspaceArea) break;
                string n = cur.GetType().Name;
                if (Array.IndexOf(CodeScrollTypes, n) >= 0) { container = cur; break; }
                if (cur is YoYoStudio.GUI.Gadgets.ScrollPanel sp)
                {
                    var f = sp.ScrollPanelFlags;
                    if ((f & (YoYoStudio.GUI.Gadgets.eScrollPanelFlags.VerticalScroll | YoYoStudio.GUI.Gadgets.eScrollPanelFlags.HorizontalScroll)) != 0)
                    { container = cur; break; }
                }
                else if (n is "ListView" or "DataGrid" or "ListBox" or "TreeView" or "DataTree" or "TableGadget")
                { container = cur; break; }
            }
            YoYoStudio.GUI.Gadgets.ScrollBar? vBar = null, hBar = null;
            if (container != null)
            {
                // As barras ficam no proprio gadget ou num filho dele (ex.: ScrollPanel interno da lista).
                MobileScrollTarget.Find(container, out vBar, out hBar);
                if (vBar == null && hBar == null) FindBelow(container, ref vBar, ref hBar, 3);
            }
            MobileScrollTarget.SetTarget(gen, vBar, hBar);
            Log.Debug(Tag, $"Sondagem #{gen}: {container?.GetType().Name ?? "-"} vertical={vBar != null} horizontal={hBar != null}");
            TouchInput.SetProbe(gen, vBar != null, hBar != null);
        }
        catch (Exception e) { Log.Warn(Tag, "Sondagem de rolagem: " + e.Message); }
    }

    static void FindBelow(GUIBase g, ref YoYoStudio.GUI.Gadgets.ScrollBar? v, ref YoYoStudio.GUI.Gadgets.ScrollBar? h, int depth)
    {
        if (depth <= 0) return;
        foreach (var list in new[] { g.Gadgets, g.ChildGadgets })
        {
            if (list == null) continue;
            foreach (var c in list)
            {
                if (c == null) continue;
                MobileScrollTarget.BarsOf(c, ref v, ref h);
                if (v != null || h != null) return;
            }
        }
        foreach (var list in new[] { g.Gadgets, g.ChildGadgets })
        {
            if (list == null) continue;
            foreach (var c in list) { if (c != null) FindBelow(c, ref v, ref h, depth - 1); if (v != null || h != null) return; }
        }
    }

    /// <summary>Thread da IDE: aplica o arraste/pinca acumulado do modo Mover.</summary>
    internal static void ApplyPending()
    {
        try
        {
            if (!MobileWorkspace.Take(out float dx, out float dy, out int zoom)) return;
            var area = YoYoStudio.IDE.WindowManager?.GetCurrentWorkspaceArea();
            if (area == null) return;
            if (dx != 0 || dy != 0)
            {
                // Mesma conta do arraste com o botao do meio (WorkspaceArea_MouseMove), com o delta
                // convertido de pixels de tela para o espaco relativo da area.
                area.Layout.ConvertScreenPositionToRelative(0, 0, out float x0, out float y0);
                area.Layout.ConvertScreenPositionToRelative(dx, dy, out float x1, out float y1);
                area.Layout.ClipOffsetX += area.Layout.ConvertRelativeValueToChildSpace(x1 - x0);
                area.Layout.ClipOffsetY += area.Layout.ConvertRelativeValueToChildSpace(y1 - y0);
                area.RequireRender = true;
                area.DirtyLayout();
            }
            for (int i = 0; i < Math.Abs(zoom); i++) area.CentreZoom(Math.Sign(zoom));
        }
        catch (Exception e) { Log.Warn(Tag, "Modo Mover: " + e.Message); }
    }
}
