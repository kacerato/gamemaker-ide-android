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

/// <summary>
/// Arraste 1:1 de canvas e do workspace. Chamado pelo TouchInput (thread de UI do Java); aplicado na
/// thread da IDE (MobileWindowFit.ApplyPending). Duas origens:
///   - modo Mover da barra: um dedo arrasta, a pinca da zoom;
///   - dois dedos juntos fora do modo Mover, quando sob eles ha um canvas (room, sprite, tileset: PanArea)
///     ou o workspace. Em listas, Inspetor e codigo os dois dedos continuam virando roda do mouse.
/// O alvo e decidido uma vez por gesto (BeginGesture), pelo que esta sob o ponteiro.
/// </summary>
public static class MobileWorkspace
{
    static readonly object _lock = new();
    static float _panX, _panY;     // px de tela a mover (positivo = ver o que esta a direita/abaixo)
    static int _zoomSteps;          // >0 aproxima
    static int _gesture;            // gesto atual: o alvo e resolvido de novo quando muda

    // Pinca continua no workspace: escala = escala do inicio do gesto * razao entre as distancias dos
    // dedos, ancorada no centro dos dedos (px de tela).
    static float _pinchRatio, _pinchCx, _pinchCy;
    static bool _hasPinch;

    public static void BeginGesture() { lock (_lock) { _gesture++; _panX = _panY = 0; _zoomSteps = 0; _hasPinch = false; } }
    public static void AddPan(float dx, float dy) { lock (_lock) { _panX += dx; _panY += dy; } }
    public static void AddZoom(int steps) { lock (_lock) _zoomSteps += steps; }
    public static void SetPinch(float ratio, float cx, float cy)
    {
        lock (_lock) { _pinchRatio = ratio; _pinchCx = cx; _pinchCy = cy; _hasPinch = true; }
    }

    internal readonly struct Pending
    {
        public readonly float Dx, Dy, PinchRatio, PinchCx, PinchCy;
        public readonly int Zoom, Gesture;
        public readonly bool HasPinch;
        public Pending(float dx, float dy, int zoom, int gesture, bool hasPinch, float ratio, float cx, float cy)
        { Dx = dx; Dy = dy; Zoom = zoom; Gesture = gesture; HasPinch = hasPinch; PinchRatio = ratio; PinchCx = cx; PinchCy = cy; }
    }

    internal static bool Take(out Pending p)
    {
        lock (_lock)
        {
            p = new Pending(_panX, _panY, _zoomSteps, _gesture, _hasPinch, _pinchRatio, _pinchCx, _pinchCy);
            _panX = _panY = 0; _zoomSteps = 0; _hasPinch = false;
        }
        return p.Dx != 0 || p.Dy != 0 || p.Zoom != 0 || p.HasPinch;
    }

    // ---- dois dedos: o que ha sob o centro dos dedos? (respondido pela IDE no proximo quadro)
    //   canvas: arrastar direto (PanArea do canvas ou workspace; uma area rolavel no caminho vence);
    //   zoomWorkspace: a pinca e do workspace (o primeiro entre PanArea e WorkspaceArea subindo do
    //   gadget e o workspace, mesmo com listas/paineis de uma janela no caminho).
    public static volatile int CanvasProbeGen, CanvasProbeX, CanvasProbeY;
    static volatile int _canvasDoneGen = -1;
    static volatile bool _canvasHit, _zoomWorkspace;

    public static bool TryGetCanvasProbe(int gen, out bool canvas, out bool zoomWorkspace)
    {
        canvas = _canvasHit;
        zoomWorkspace = _zoomWorkspace;
        return _canvasDoneGen == gen;
    }

    internal static void AnswerCanvasProbe(int gen, bool canvas, bool zoomWorkspace)
    {
        _canvasHit = canvas; _zoomWorkspace = zoomWorkspace; _canvasDoneGen = gen;
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

    // Alvo do arraste do gesto atual (PanArea do canvas ou WorkspaceArea).
    static int _panGesture = -1;
    static WeakReference<GUIBase>? _panTarget;
    static float _pinchStartScale = 1f;

    /// <summary>
    /// Pinca continua no workspace: a mesma conta do WorkspaceArea.WorkspaceZoom (protegido), com a
    /// escala do inicio do gesto vezes a razao das distancias dos dedos e o ponto entre eles parado.
    /// Antes a pinca virava Ctrl+roda, que a janela sob os dedos (lista do Prefabs, Inspetor de um
    /// editor...) consumia: ScrollBar.Parent_MouseWheel so deixa o Ctrl+roda subir em janelas de uma
    /// cadeia do workspace, e listas com zoom proprio de icones tambem o usam.
    /// </summary>
    static void PinchWorkspace(WorkspaceArea area, float ratio, float screenX, float screenY)
    {
        float min = area.MinScale, max = area.MaxScale;
        if (!(max > min)) { min = 0.1f; max = 2f; }   // limites nao configurados: faixa segura
        float scale = Math.Clamp(_pinchStartScale * ratio, min, max);
        if (Math.Abs(scale - area.Layout.ClipScale) < 0.001f) return;
        area.AbortPanAnimation();
        area.Layout.ConvertScreenPositionToRelative(screenX, screenY, out float rx, out float ry);
        area.Layout.ConvertRelativePositionToChildSpace(rx, ry, out float c1x, out float c1y);
        area.Layout.ClipScale = scale;
        area.Layout.ConvertRelativePositionToChildSpace(rx, ry, out float c2x, out float c2y);
        area.VerticalScrollValue -= c2y - c1y;
        area.HorizontalScrollValue -= c2x - c1x;
        area.RequireRender = true;
        area.DirtyLayout();
    }
    static int _canvasProbed = -1;

    /// <summary>
    /// O que arrastar sob o ponteiro: o primeiro canvas (PanArea que rola, como RoomEditor_Room, o
    /// canvas do sprite e o do tileset) ou o WorkspaceArea. Com <paramref name="stopAtScroll"/> (dois
    /// dedos), uma area rolavel no caminho (lista, Inspetor, codigo) vence: ali os dedos viram roda.
    /// </summary>
    static GUIBase? ResolvePanTarget(bool stopAtScroll)
    {
        for (GUIBase? cur = YoYoStudio.IDE.WindowManager?.GetMouseOverGadget(); cur != null; cur = cur.parent)
        {
            if (cur is YoYoStudio.GUI.Gadgets.PanArea pa)
            {
                // InputOnlyMode: o PanArea so repassa eventos (OnPanInput) e o dono decide; nao mexemos.
                if (pa.InputOnlyMode) return null;
                if (pa.HandleMouseEvents && (pa.AllowHorizontalScrolling || pa.AllowVerticalScrolling)) return pa;
            }
            if (cur is WorkspaceArea) return cur;
            if (stopAtScroll && IsScrollContainer(cur)) return null;
        }
        return null;
    }

    static bool IsScrollContainer(GUIBase cur)
    {
        string n = cur.GetType().Name;
        if (Array.IndexOf(CodeScrollTypes, n) >= 0) return true;
        if (cur is YoYoStudio.GUI.Gadgets.ScrollPanel sp)
            return (sp.ScrollPanelFlags & (YoYoStudio.GUI.Gadgets.eScrollPanelFlags.VerticalScroll |
                                           YoYoStudio.GUI.Gadgets.eScrollPanelFlags.HorizontalScroll)) != 0;
        return n is "ListView" or "DataGrid" or "ListBox" or "TreeView" or "DataTree" or "TableGadget";
    }

    /// <summary>Thread da IDE, a cada quadro: responde se ha canvas sob os dois dedos (TouchInput).</summary>
    internal static void ProbeCanvas()
    {
        try
        {
            int gen = MobileWorkspace.CanvasProbeGen;
            if (gen == _canvasProbed) return;
            // O movimento do ponteiro ate o centro dos dedos precisa ja ter chegado a IDE.
            if (Math.Abs(WindowManager.MouseX - MobileWorkspace.CanvasProbeX) > 3 ||
                Math.Abs(WindowManager.MouseY - MobileWorkspace.CanvasProbeY) > 3) return;
            _canvasProbed = gen;
            var t = ResolvePanTarget(stopAtScroll: true);
            bool zoomWs = ResolvePanTarget(stopAtScroll: false) is WorkspaceArea;
            Log.Info(Tag, $"Dois dedos #{gen} sobre {YoYoStudio.IDE.WindowManager?.GetMouseOverGadget()?.GetType().Name ?? "-"}: " +
                          (t != null ? $"arrastar {t.GetType().Name}" : "roda") + (zoomWs ? ", pinca no workspace" : ", pinca Ctrl+roda"));
            MobileWorkspace.AnswerCanvasProbe(gen, t != null, zoomWs);
        }
        catch (Exception e) { Log.Warn(Tag, "Sondagem de canvas: " + e.Message); }
    }

    /// <summary>Thread da IDE: aplica o arraste/pinca acumulado (modo Mover ou dois dedos num canvas).</summary>
    internal static void ApplyPending()
    {
        try
        {
            if (!MobileWorkspace.Take(out var p)) return;
            float dx = p.Dx, dy = p.Dy;
            int zoom = p.Zoom;
            GUIBase? target = null;
            if (p.Gesture == _panGesture) _panTarget?.TryGetTarget(out target);
            else
            {
                _panGesture = p.Gesture;
                target = ResolvePanTarget(stopAtScroll: false) ?? YoYoStudio.IDE.WindowManager?.GetCurrentWorkspaceArea();
                _panTarget = target != null ? new WeakReference<GUIBase>(target) : null;
                _pinchStartScale = target?.Layout.ClipScale ?? 1f;
                if (target is WorkspaceArea w0)
                    Log.Info(Tag, $"Arrastar: alvo WorkspaceArea (escala {w0.Layout.ClipScale:F2}, limites {w0.MinScale:F2}..{w0.MaxScale:F2})");
                else
                    Log.Info(Tag, $"Arrastar: alvo {target?.GetType().Name ?? "-"}");
            }
            if (target == null || target.Destroyed) return;
            if (p.HasPinch && target is WorkspaceArea wz) PinchWorkspace(wz, p.PinchRatio, p.PinchCx, p.PinchCy);

            float cx = 0, cy = 0;
            if (dx != 0 || dy != 0)
            {
                // Delta de pixels de tela para o espaco dos filhos do alvo: a mesma conta do arraste com
                // o botao do meio (PanArea_MouseMove / WorkspaceArea_MouseMove).
                target.Layout.ConvertScreenPositionToRelative(0, 0, out float x0, out float y0);
                target.Layout.ConvertScreenPositionToRelative(dx, dy, out float x1, out float y1);
                cx = target.Layout.ConvertRelativeValueToChildSpace(x1 - x0);
                cy = target.Layout.ConvertRelativeValueToChildSpace(y1 - y0);
            }
            if (target is YoYoStudio.GUI.Gadgets.PanArea pa)
            {
                if (cx != 0 && pa.AllowHorizontalScrolling) pa.Scroll(eGUIDirection.Horizontal, pa.HorizontalScrollValue + cx);
                if (cy != 0 && pa.AllowVerticalScrolling) pa.Scroll(eGUIDirection.Vertical, pa.VerticalScrollValue + cy);
                for (int i = 0; i < Math.Abs(zoom); i++) { if (zoom > 0) pa.ZoomIn(); else pa.ZoomOut(); }
                pa.RequireRender = true;
            }
            else if (target is WorkspaceArea area)
            {
                if (cx != 0 || cy != 0)
                {
                    area.Layout.ClipOffsetX += cx;
                    area.Layout.ClipOffsetY += cy;
                    area.RequireRender = true;
                    area.DirtyLayout();
                }
                for (int i = 0; i < Math.Abs(zoom); i++) area.CentreZoom(Math.Sign(zoom));
            }
        }
        catch (Exception e) { Log.Warn(Tag, "Arrastar canvas/workspace: " + e.Message); }
    }
}
