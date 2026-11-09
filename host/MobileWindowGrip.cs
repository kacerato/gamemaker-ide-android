using System.Collections.Concurrent;
using Android.Runtime;
using Android.Util;
using YoYoStudio.GUI;
using YoYoStudio.GUI.Gadgets;
using YoYoStudio.GUI.Gadgets.Containers;
using GuiWindow = YoYoStudio.GUI.Gadgets.Window;
using Button = YoYoStudio.GUI.Gadgets.Button;

namespace GmIde;

// Alca de redimensionar janelas flutuantes pelo toque (docs/mobile-ui.md, "Janelas").
//
// As janelas flutuantes da IDE (Prefab Library, Preferencias, Game Options...) so mudam de tamanho
// arrastando a borda, com uma area de poucos pixels: com o dedo e impossivel. Com uma janela flutuante
// redimensionavel em foco, o Java (io.gmide.WindowGrip) mostra uma alca de 48dp no canto inferior
// direito dela; arrastar a alca manda o deslocamento em px de tela e aqui ele vira Width/Height em
// unidades da IDE (mesmo modo DpiPixel que a politica de janelas usa para caber na tela). Vale com e
// sem a casca mobile.
//
// Na casca mobile ha tambem um botao ao lado dos botoes da barra de titulo: "levar para o workspace"
// (WindowManager.AddChainRoot, o caminho de qualquer editor). A janela sai da desktop
// (WindowInterface.RemoveWindow), deixa de ser flutuante para a politica e entra numa corrente do
// workspace atual, onde fica junto dos editores e acompanha zoom e arraste do workspace.
//
// Thread: Publish/Apply na thread da IDE (MobileWindowPolicy.OnFrame); Enqueue de qualquer thread
// (ShortcutBridge, comando "grip:dx:dy").
static class MobileWindowGrip
{
    const string Tag = "GmIde.Grip";
    const float MinW = 220, MinH = 140;      // unidades da IDE

    static readonly ConcurrentQueue<(float dx, float dy)> _moves = new();
    static volatile bool _toWorkspace;
    static WeakReference<GuiWindow>? _target;
    static string? _sent;

    public static void Enqueue(string arg)
    {
        if (arg == "ws") { _toWorkspace = true; return; }
        var p = arg.Split(':');
        if (p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dx)
            && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dy))
            _moves.Enqueue((dx, dy));
    }

    /// <summary>Thread da IDE, a cada quadro: aplica arrastes pendentes e publica onde fica a alca.</summary>
    internal static void OnFrame(IEnumerable<GuiWindow> floating)
    {
        try
        {
            var target = Pick(floating);
            float dx = 0, dy = 0;
            while (_moves.TryDequeue(out var m)) { dx += m.dx; dy += m.dy; }
            if (target != null && (dx != 0 || dy != 0) && Resizable(target)) Resize(target, dx, dy);
            if (_toWorkspace)
            {
                _toWorkspace = false;
                if (target != null && CanDock(target)) { ToWorkspace(target); target = null; }
            }
            Publish(target);
        }
        catch (Exception e) { Log.Warn(Tag, "Alca: " + e.Message); }
    }

    // A janela que o usuario esta usando: a que contem o foco ou, se o toque nao moveu o foco (itens de
    // DataListBox como os do Prefab Library nao pegam foco), a que esta sob o ultimo toque. Primeiro as
    // flutuantes da desktop (FloatingDock, Preferencias...), depois a janela de editor no workspace.
    static GuiWindow? Pick(IEnumerable<GuiWindow> floating)
    {
        var wm = YoYoStudio.IDE.WindowManager;
        var touched = new[] { wm?.ActiveGadget, wm?.GetMouseOverGadget() };
        var list = floating.ToList();
        GuiWindow? held = null;
        _target?.TryGetTarget(out held);
        foreach (var g in touched)
        {
            if (g == null) continue;
            foreach (var w in list)
                if (Usable(w) && IsInside(g, w)) { _target = new WeakReference<GuiWindow>(w); return w; }
            if (WorkspaceWindowOf(g) is { } ws && Resizable(ws)) { _target = new WeakReference<GuiWindow>(ws); return ws; }
        }
        // Durante o arraste o foco pode ir para outro gadget da janela; continua na mesma.
        if (held != null && Usable(held) && !_moves.IsEmpty) return held;
        _target = null;
        return null;
    }

    static GuiWindow? WorkspaceWindowOf(GUIBase g)
    {
        GuiWindow? win = null;
        for (var cur = g; cur != null; cur = cur.parent)
        {
            if (cur is WorkspaceChain) return win;
            if (cur is GuiWindow w) win = w;
        }
        return null;
    }

    static bool InWorkspace(GuiWindow w)
    {
        for (var cur = w.parent; cur != null; cur = cur.parent) if (cur is WorkspaceChain) return true;
        return false;
    }

    static bool Usable(GuiWindow w) =>
        !w.Destroyed && !w.Closing && !w.Minimized && w.parent != null &&
        w is not ContextMenu && w is not YoYoStudio.GUI.Gadgets.PopupWindow && (Resizable(w) || CanDock(w));

    // Tamanho fixo em unidades (DpiPixel: dialogos, Preferencias) ou em px de tela (Pixel: FloatingDock,
    // um painel solto como janela, ex.: Prefab Library). Percentual nao: depende do pai.
    static bool Resizable(GuiWindow w) =>
        (w.WindowFlags & eWindowFlags.ResizeButton) != 0 && Fixed(w.Width) && Fixed(w.Height);

    static bool Fixed(GUIMeasurement? m) =>
        m != null && m.value > 0 && (m.mode == eGUIMeasurementMode.DpiPixel || m.mode == eGUIMeasurementMode.Pixel);

    // Para o workspace: so com a casca, janelas com barra de titulo que nao sejam dialogos (estes tem
    // dono e fecham com ele: MobileWindowPolicy.RedirectChildWindow).
    static bool CanDock(GuiWindow w) =>
        MobileShell.Active && w.WindowTitleBar != null && !InWorkspace(w) && w is not FloatingDock && w is not YoYoStudio.GUI.Gadgets.Documents.DocumentWindow &&
        !w.GetType().Name.EndsWith("Dialog", StringComparison.Ordinal);

    static void ToWorkspace(GuiWindow w)
    {
        var wm = YoYoStudio.IDE.WindowManager;
        if (wm == null) return;
        MobileWindowPolicy.Untrack(w);
        new WindowInterface(wm, null).RemoveWindow(w);
        w.Left = null; w.Top = null; w.Right = null; w.Bottom = null;   // a corrente posiciona
        wm.AddChainRoot(w, null);                                        // workspace atual
        WindowManager.OnPostLayout.DoOnce(delegate
        {
            try { if (!w.Destroyed) { wm.SetFocus(w); MobileWindowPolicy.ShowWindow(w); } }
            catch (Exception e) { Log.Warn(Tag, "Mostrar no workspace: " + e.Message); }
        });
        Log.Info(Tag, $"'{w.GetType().Name}' ({w.Title}) levada para o workspace");
    }

    static void Resize(GuiWindow w, float dxPx, float dyPx)
    {
        var r = w.AbsoluteContentRect;
        var desk = w.parent.AbsoluteClipRect;
        if (r == null || desk == null || r.w <= 0 || r.h <= 0) return;
        float ppuX = r.w / w.Width!.value, ppuY = r.h / w.Height!.value;     // px por unidade (1 em modo Pixel)
        // Flutuante: ate a borda da tela. No workspace nao ha borda (ele rola e da zoom).
        bool ws = InWorkspace(w);
        float maxW = ws ? 8000 : (desk.x + desk.w - r.x) / ppuX, maxH = ws ? 8000 : (desk.y + desk.h - r.y) / ppuY;
        // Minimo em unidades da IDE convertido para o modo da janela (em Pixel, px = unidade * escala).
        float unit = w.ScreenToDPIPixelConversion > 0 ? 1f / w.ScreenToDPIPixelConversion : 1f;   // px por unidade
        float minW = w.Width.mode == eGUIMeasurementMode.Pixel ? MinW * unit : MinW;
        float minH = w.Height.mode == eGUIMeasurementMode.Pixel ? MinH * unit : MinH;
        float nw = Math.Clamp(w.Width.value + dxPx / ppuX, Math.Min(minW, maxW), maxW);
        float nh = Math.Clamp(w.Height.value + dyPx / ppuY, Math.Min(minH, maxH), maxH);
        w.Width = new GUIMeasurement((float)Math.Floor(nw), w.Width.mode);    // mesmo modo de antes
        w.Height = new GUIMeasurement((float)Math.Floor(nh), w.Height.mode);
        w.DirtyLayout();
        Log.Debug(Tag, $"'{w.GetType().Name}' -> {nw:F0}x{nh:F0}");
    }

    // ------------------------------------------------------------------ JNI (io.gmide.WindowGrip)

    static IntPtr _cls, _set;
    static bool _failed;

    static void Publish(GuiWindow? w)
    {
        // "x,y,w,h,redimensiona,workspace,ax,ay": retangulo da janela, o que a alca oferece e onde vai o
        // botao do workspace (ax = borda direita do botao, a esquerda dos botoes da barra de titulo; ay =
        // centro vertical da barra), tudo em px da superficie do SDL.
        string msg = "";
        if (w?.AbsoluteContentRect is { } r && r.w > 0 && r.h > 0)
        {
            bool dock = CanDock(w);
            int ax = (int)(r.x + r.w), ay = (int)r.y;
            if (dock && w.WindowTitleBar?.AbsoluteContentRect is { } t && t.h > 0)
            {
                ay = (int)(t.y + t.h / 2);
                ax = (int)(t.x + t.w);
                foreach (var b in Buttons(w.WindowTitleBar))
                    if (b.AbsoluteContentRect is { } br && br.w > 0 && br.x > t.x + t.w / 2) ax = Math.Min(ax, (int)br.x);
            }
            msg = $"{(int)r.x},{(int)r.y},{(int)r.w},{(int)r.h},{(Resizable(w) ? 1 : 0)},{(dock ? 1 : 0)},{ax},{ay}";
        }
        if (msg == _sent) return;
        if (msg.Length > 0 && string.IsNullOrEmpty(_sent))
            Log.Info(Tag, $"Alca em '{w!.GetType().Name}' ({w.Title}): {msg} (largura {w.Width?.mode})");
        if (_cls == IntPtr.Zero && !_failed)
        {
            try
            {
                _cls = JNIEnv.NewGlobalRef(JNIEnv.FindClass("io/gmide/WindowGrip"));
                _set = JNIEnv.GetStaticMethodID(_cls, "setTarget", "(Ljava/lang/String;)V");
            }
            catch (Exception e) { _failed = true; _cls = IntPtr.Zero; Log.Error(Tag, "io.gmide.WindowGrip indisponivel: " + e.Message); }
        }
        if (_cls == IntPtr.Zero) return;
        IntPtr s = JNIEnv.NewString(msg);
        try
        {
            JNIEnv.CallStaticVoidMethod(_cls, _set, new JValue(s));
            _sent = msg;
        }
        catch (Exception e) { Log.Warn(Tag, "setTarget: " + e.Message); }
        finally { JNIEnv.DeleteLocalRef(s); }
    }

    static IEnumerable<Button> Buttons(GUIBase g)
    {
        foreach (var list in new[] { g.ChildGadgets, g.Gadgets })
        {
            if (list == null) continue;
            foreach (var c in list.ToArray())
            {
                if (c == null || c.RequestDisabled) continue;
                if (c is Button b) yield return b;
                foreach (var d in Buttons(c)) yield return d;
            }
        }
    }

    static bool IsInside(GUIBase g, GUIBase ancestor)
    {
        for (var cur = g; cur != null; cur = cur.parent) if (cur == ancestor) return true;
        return false;
    }
}
