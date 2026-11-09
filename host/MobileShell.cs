using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Android.Runtime;
using Android.Util;
using YoYoStudio.GUI;
using YoYoStudio.GUI.Gadgets;
using YoYoStudio.GUI.Gadgets.Containers;
using YoYoStudio.Inspector;
using GuiWindow = YoYoStudio.GUI.Gadgets.Window;
using Button = YoYoStudio.GUI.Gadgets.Button;
using GridLayout = YoYoStudio.GUI.Gadgets.GridLayout;

namespace GmIde;

// Casca mobile-first da IDE (docs/mobile-ui.md, secao "Casca").
//
// A IDE de desktop mostra o tempo todo menu, barra de ferramentas, faixas de abas e tres docks. Num
// celular em paisagem isso come metade da tela. A casca troca essa moldura por views Android
// (io.gmide.MobileShell): barra superior (projeto/editor, Executar, Depurar, Parar, menu ⋮), trilho de
// paineis a esquerda e folhas (menu completo, seletor de editores). Aqui fica o lado da IDE:
//  - esconde menu e barra de ferramentas (RequestDisabled nos StackedGadgets da desktop, como o
//    KeyboardMode), as faixas de abas do workspace e dos docks (linhas do TabControl em 0 px) e os docks
//    (DockContainer.Hide: some ate a tirinha "<<" do dock recolhido);
//  - um painel abre como gaveta: DockContainer.Expand + ActivateTab; outro painel ou toque fora fecha;
//  - publica o estado (abas, janelas do workspace, paineis, Executar/Parar, progresso) em JSON para o
//    Java, so quando muda;
//  - serve o menu original nivel a nivel (MenuBar -> ContextMenu -> SubMenuEntry, inclusive os
//    preguicosos) e a barra de ferramentas; um toque no menu nativo chama Button.ClickButton(), o mesmo
//    caminho do Enter no menu da IDE. Nenhuma funcao some: tudo o que estava na moldura esta no ⋮;
//  - selecionar algo num editor (instancia na sala) abre a gaveta do Inspetor.
// "Interface classica" (prefs gmide/mobile_shell = false, ou pelo ⋮) devolve a moldura original.
//
// Thread: tudo na thread da IDE (MobileFrame -> OnFrame). Os comandos chegam do Java pela fila da barra
// de atalhos (ShortcutBridge, comando "shell:...") e sao aplicados aqui.
static class MobileShell
{
    const string Tag = "GmIde.Shell";
    const string PrefsName = "gmide", PrefShell = "mobile_shell";
    const string InspectorPanelType = "YoYoStudio.Plugins.Inspector.InspectorScrollPanel";

    static readonly ConcurrentQueue<string> _commands = new();
    static bool _wanted, _prefRead, _active;
    static int _frame;

    /// <summary>Qualquer thread (ShortcutBridge.Apply, thread de UI do Java).</summary>
    public static void Enqueue(string cmd) => _commands.Enqueue(cmd);

    public static bool Active => _active;

    // ------------------------------------------------------------------ JNI (Java io.gmide.MobileShell)

    static IntPtr _cls, _onState, _onMenu;
    static bool _jniFailed;

    static bool EnsureJni()
    {
        if (_cls != IntPtr.Zero) return true;
        if (_jniFailed) return false;
        try
        {
            var local = JNIEnv.FindClass("io/gmide/MobileShell");
            var cls = JNIEnv.NewGlobalRef(local);
            _onState = JNIEnv.GetStaticMethodID(cls, "onState", "(Ljava/lang/String;)V");
            _onMenu = JNIEnv.GetStaticMethodID(cls, "onMenu", "(Ljava/lang/String;Ljava/lang/String;)V");
            _cls = cls;
            return true;
        }
        catch (Exception e)
        {
            _jniFailed = true;
            Log.Error(Tag, "io.gmide.MobileShell indisponivel (casca desligada): " + e.Message);
            return false;
        }
    }

    static bool CallJava(IntPtr method, params string[] args)
    {
        if (!EnsureJni()) return false;
        var refs = new IntPtr[args.Length];
        try
        {
            var vals = new JValue[args.Length];
            for (int i = 0; i < args.Length; i++) { refs[i] = JNIEnv.NewString(args[i]); vals[i] = new JValue(refs[i]); }
            JNIEnv.CallStaticVoidMethod(_cls, method, vals);
            return true;
        }
        catch (Exception e) { Log.Warn(Tag, "Chamada ao Java: " + e.Message); return false; }
        finally { foreach (var r in refs) if (r != IntPtr.Zero) JNIEnv.DeleteLocalRef(r); }
    }

    // ------------------------------------------------------------------ quadro

    /// <summary>Thread da IDE, toda volta do loop (MobileFrame).</summary>
    internal static void OnFrame()
    {
        try
        {
            _frame++;
            if (!_prefRead) ReadPref();
            var wm = YoYoStudio.IDE.WindowManager;
            if (wm == null) return;
            // A desktop da casca e a primeira com moldura e docks; outra desktop em foco por um instante
            // (janela flutuante, tela inicial) nao desfaz nada.
            bool project = YoYoStudio.IDE.IsProjectLoadingComplete;
            var details = _details != null && _details.Chrome?.tabControl is { Destroyed: false } ? _details
                        : wm.GetDesktopDetails(WindowManager.CurrentDesktopID);
            bool ready = project && details?.Chrome?.tabControl != null && details.DockMan != null && details.stackPanel != null;

            while (_commands.TryDequeue(out var cmd))
                try { Run(cmd, details); }
                catch (Exception e) { Log.Error(Tag, $"Comando '{cmd}': {e}"); }

            if (ready && _wanted && !_active) Apply(details!);
            else if (_active && (!_wanted || !project)) Restore(_wanted ? "projeto fechado" : "interface classica");

            if (_active && ready && _frame % 30 == 0) Enforce(details!);
            if (_active && ready) WatchEditorTab(details!);
            if (_frame % 8 == 0) PublishState(details, ready);
        }
        catch (Exception e) { Log.Error(Tag, "OnFrame: " + e); }
    }

    static void ReadPref()
    {
        _prefRead = true;
        try
        {
            var p = MainApplication.Instance.GetSharedPreferences(PrefsName, global::Android.Content.FileCreationMode.Private)!;
            _wanted = p.GetBoolean(PrefShell, false);   // padrao: interface desktop; a casca e opcional
            Log.Info(Tag, "Casca mobile: " + (_wanted ? "ligada" : "desligada (interface classica)"));
        }
        catch (Exception e) { Log.Warn(Tag, "Preferencia da casca: " + e.Message); }
    }

    // ------------------------------------------------------------------ aplicar / restaurar

    static readonly List<WeakReference<GUIBase>> _rows = new();
    static readonly ConditionalWeakTable<TabControl, GridLayout[]> _strips = new();
    static readonly List<WeakReference<TabControl>> _stripList = new();
    static StackPanel? _stack;
    static DesktopDetails? _details;
    static InspectorSelected? _inspectHandler;   // ref forte (evento guarda fraco)

    static void Apply(DesktopDetails details)
    {
        _active = true;
        _details = details;
        var stack = details.stackPanel!;
        _stack = stack;
        var tabs = details.Chrome!.tabControl;

        // 1. Menu e barra de ferramentas: tudo da pilha da desktop que nao contem a grade dos docks.
        var names = new List<string>();
        foreach (var c in stack.StackedGadgets.ToArray())
        {
            if (c == null || IsInside(tabs, c)) continue;
            names.Add($"{c.GetType().Name}({c.LayoutID?.Name ?? "-"}{(c.RequestDisabled ? ", ja desligado" : "")})");
            if (c.RequestDisabled) continue;
            c.RequestDisabled = true;
            _rows.Add(new WeakReference<GUIBase>(c));
        }

        // 2. Faixas de abas: workspace/editores e os tres docks.
        HideStrip(tabs);
        var docks = details.DockMan!.GetDocks() ?? Array.Empty<DockContainer>();
        foreach (var d in docks) if (d != null) HideStrip(d);

        // 3. Docks fechados (nem a tirinha de recolhido fica); abrem como gaveta pelo trilho.
        foreach (var d in docks)
            if (d != null && !d.Destroyed && d.GetTabCount() > 0) Shut(d);

        stack.DirtyLayout();
        _applyClock.Restart();
        _userPanels = false;
        HookInspector();
        _lastState = null;
        Log.Info(Tag, $"Casca aplicada: moldura escondida [{string.Join(", ", names)}], {_stripList.Count} faixa(s) de abas, " +
                      $"{docks.Count(d => d != null)} dock(s) como gaveta");
        LogChrome(details);
    }

    static void Restore(string why)
    {
        _active = false;
        foreach (var r in _rows)
            if (r.TryGetTarget(out var c) && !c.Destroyed) c.RequestDisabled = false;
        _rows.Clear();
        foreach (var r in _stripList)
            if (r.TryGetTarget(out var tc) && !tc.Destroyed && _strips.TryGetValue(tc, out var rows))
            { tc.GridRowLayout = rows; tc.DirtyLayout(); _strips.Remove(tc); _zeroed.Remove(tc); }
        _stripList.Clear();
        try
        {
            var wm = YoYoStudio.IDE.WindowManager;
            var details = _details ?? wm?.GetDesktopDetails(WindowManager.CurrentDesktopID);
            // Docks com abas voltam ao estado classico: recolhidos, com a tirinha de expandir (o Expand
            // devolve a coluna/linha da grade; o Collapse poe a largura da tirinha).
            foreach (var d in details?.DockMan?.GetDocks() ?? Array.Empty<DockContainer>())
                if (d != null && !d.Destroyed && d.GetTabCount() > 0 && d.DockState != eDockState.Expanded)
                { d.Expand(); d.Collapse(_bStoreSize: false); }
        }
        catch (Exception e) { Log.Warn(Tag, "Restaurar docks: " + e.Message); }
        try { if (_stack != null && !_stack.Destroyed) _stack.DirtyLayout(); } catch { }
        _stack = null;
        _details = null;
        _lastState = null;
        _lastTab = null;
        Log.Info(Tag, "Moldura original restaurada (" + why + ")");
    }

    // Ha caminhos da IDE que refazem layouts (troca de escala, layouts salvos): a casca se reimpoe.
    static void Enforce(DesktopDetails details)
    {
        foreach (var c in details.stackPanel!.StackedGadgets.ToArray())
            if (c != null && !c.RequestDisabled && !IsInside(details.Chrome!.tabControl, c))
            {
                c.RequestDisabled = true;
                _rows.Add(new WeakReference<GUIBase>(c));
                Log.Info(Tag, $"Moldura reapareceu ({c.GetType().Name}); escondida de novo");
            }
        HideStrip(details.Chrome!.tabControl);
        // Dock recolhido mostra a tirinha "<<"; na casca o fechado nao tem tirinha (Shut). Logo depois de
        // abrir o projeto a IDE ainda restaura o layout salvo (docks expandidos): ate o usuario mexer num
        // painel, ou por alguns segundos, esses tambem voltam a ficar fechados.
        bool settling = !_userPanels && _applyClock.ElapsedMilliseconds < 15000;
        foreach (var d in details.DockMan!.GetDocks() ?? Array.Empty<DockContainer>())
        {
            if (d == null || d.Destroyed) continue;
            HideStrip(d);
            if (d.GetTabCount() == 0) continue;
            if ((d.DockState == eDockState.Collapsed && SlotSize(d) > 0) || d.DockState == eDockState.Hidden ||
                (settling && d.DockState == eDockState.Expanded))
            {
                Log.Info(Tag, $"Dock {d.DockType} {d.DockState} pela IDE; fechado (gaveta)");
                Shut(d);
            }
        }
    }

    // Abrir um editor (sala, sprite, objeto) muda a aba ativa do workspace. Quem abriu foi a gaveta de
    // Recursos (dock da direita): ela fecha sozinha para o editor ganhar a largura. Os paineis que o
    // proprio editor abre (Camadas/Inspetor da sala, a esquerda) ficam.
    static WeakReference<TabItem>? _lastTab;

    static void WatchEditorTab(DesktopDetails details)
    {
        var tc = details.Chrome!.tabControl;
        var active = tc.ActiveTab;
        TabItem? last = null;
        _lastTab?.TryGetTarget(out last);
        if (active == null || active == last) return;
        bool first = _lastTab == null;
        _lastTab = new WeakReference<TabItem>(active);
        if (first) return;
        var right = details.DockMan?.GetDock(eDockContainerType.eRight);
        if (right != null && right.DockState == eDockState.Expanded)
        {
            CloseDock(right);
            Log.Info(Tag, $"Editor '{Clean(active.Label)}' aberto: gaveta de Recursos fechada");
        }
    }

    static readonly System.Diagnostics.Stopwatch _applyClock = new();
    static bool _userPanels;

    // TabControl e um Grid: a celula ClientPanelGridCell e o conteudo; as outras linhas sao a faixa.
    // Guarda as linhas originais (para a interface classica) e o vetor zerado que pusemos: se a IDE
    // trocar o vetor (layout refeito), a faixa volta e e escondida de novo.
    static readonly ConditionalWeakTable<TabControl, GridLayout[]> _zeroed = new();

    static void HideStrip(TabControl? tc)
    {
        if (tc == null || tc.Destroyed) return;
        try
        {
            var rows = tc.GridRowLayout;
            if (_zeroed.TryGetValue(tc, out var mine) && ReferenceEquals(rows, mine)) return;
            int client = tc.ClientPanelGridCell.y;
            if (rows == null || rows.Length < 2 || client < 0 || client >= rows.Length) return;
            if (!_strips.TryGetValue(tc, out _))
            {
                _strips.Add(tc, rows);
                _stripList.Add(new WeakReference<TabControl>(tc));
            }
            var zeroed = new GridLayout[rows.Length];
            for (int i = 0; i < rows.Length; i++) zeroed[i] = i == client ? rows[i] : Zero;
            _zeroed.Remove(tc);
            _zeroed.Add(tc, zeroed);
            tc.GridRowLayout = zeroed;
            tc.DirtyLayout();
        }
        catch (Exception e) { Log.Warn(Tag, "Esconder faixa de abas: " + e.Message); }
    }

    static readonly GridLayout Zero = new GridLayout(0f, eGridLayoutMode.Pixel);

    // ------------------------------------------------------------------ docks como gavetas

    /// <summary>Fecha um painel: com a casca, recolhido e sem a tirinha; sem ela, recolhido com a tirinha.</summary>
    internal static void CloseDock(DockContainer d)
    {
        if (d.Destroyed) return;
        if (_active) Shut(d); else d.Collapse();
    }

    // Fechado na casca = Collapse (o caminho da IDE: desliga os paineis do dock e o divisor) + a coluna ou
    // linha do dock na grade em 0 px (a tirinha "<<" some). DockContainer.Hide() nao serve: no dock de
    // baixo o conteudo (ex.: Prefab Library) continuava desenhado por cima do workspace, pegando os toques.
    // Expand() devolve o tamanho salvo da gaveta (RestoreSize), entao abrir de novo funciona igual.
    static void Shut(DockContainer d)
    {
        if (d.DockState == eDockState.Hidden) d.Expand();   // Unhide: volta ao caminho normal
        if (d.DockState == eDockState.Expanded) d.Collapse();
        SetSlot(d, Zero);
    }

    static float SlotSize(DockContainer d)
    {
        var g = d.ParentGrid;
        if (g == null) return 0;
        var slot = d.DockType switch
        {
            eDockContainerType.eLeft => g.GridColumnLayout?[0],
            eDockContainerType.eRight => g.GridColumnLayout?[2],
            eDockContainerType.eBottom => g.GridRowLayout?[1],
            _ => null,
        };
        return slot == null ? 0 : slot.ToPixels(d);
    }

    static void SetSlot(DockContainer d, GridLayout size)
    {
        var g = d.ParentGrid;
        if (g == null) return;
        switch (d.DockType)
        {
            case eDockContainerType.eLeft: if (g.GridColumnLayout?.Length > 0) g.GridColumnLayout[0] = size; break;
            case eDockContainerType.eRight: if (g.GridColumnLayout?.Length > 2) g.GridColumnLayout[2] = size; break;
            case eDockContainerType.eBottom: if (g.GridRowLayout?.Length > 1) g.GridRowLayout[1] = size; break;
            default: return;
        }
        g.DirtyLayout();
    }

    static void TogglePanel(DesktopDetails details, int dockIndex, int tab)
    {
        _userPanels = true;
        var docks = details.DockMan?.GetDocks();
        if (docks == null || dockIndex < 0 || dockIndex >= docks.Length || docks[dockIndex] == null) return;
        var dock = docks[dockIndex];
        if (tab < 0 || tab >= dock.GetTabCount()) return;
        if (dock.DockState == eDockState.Expanded && dock.ActiveTabIndex == tab)
        {
            CloseDock(dock);
            Log.Info(Tag, $"Gaveta {dock.DockType} fechada");
            return;
        }
        OpenPanel(docks, dock, tab);
    }

    static void OpenPanel(DockContainer[] docks, DockContainer dock, int tab)
    {
        _userPanels = true;
        foreach (var other in docks)
            if (other != null && other != dock && other.DockState == eDockState.Expanded) CloseDock(other);
        CapDrawer(dock);
        dock.Expand();
        dock.ActivateTab(tab);
        HideStrip(dock);
        Log.Info(Tag, $"Gaveta {dock.DockType} aberta na aba {tab} ('{dock.GetTab(tab)?.Label}')");
    }

    // Gaveta com no maximo 45% da largura (laterais) ou da altura (a de baixo): sempre sobra editor ou
    // workspace a vista para tocar fora e fechar. O tamanho salvo do layout podia cobrir a tela inteira.
    static void CapDrawer(DockContainer dock)
    {
        try
        {
            var gr = dock.ParentGrid?.AbsoluteContentRect;
            if (gr == null || gr.w <= 0 || gr.h <= 0) return;
            float maxPx = (dock.DockType == eDockContainerType.eBottom ? gr.h : gr.w) * 0.45f;
            float cur = dock.RestoreSize.ToPixels(dock);
            if (cur <= maxPx) return;
            dock.SetRestoreSize(GUIMeasurement.DpiPixels((float)Math.Floor(maxPx * dock.ScreenToDPIPixelConversion)));
            Log.Info(Tag, $"Gaveta {dock.DockType}: {cur:F0}px -> {maxPx:F0}px (45% da tela)");
        }
        catch (Exception e) { Log.Warn(Tag, "Limitar gaveta: " + e.Message); }
    }

    static void ClosePanels(DesktopDetails details)
    {
        foreach (var d in details.DockMan?.GetDocks() ?? Array.Empty<DockContainer>())
            if (d != null && d.DockState == eDockState.Expanded) CloseDock(d);
    }

    // Selecionar algo num editor (instancia, camada, recurso na sala...) abre a gaveta do Inspetor.
    // InspectorManager.InspectMultiple -> OnInspectorSelected. Selecoes vindas de um dock (Recursos,
    // Camadas) nao trocam de gaveta; abrir a sala (que inspeciona o proprio GMRoom) tambem nao.
    static void HookInspector()
    {
        if (_inspectHandler != null) return;
        try
        {
            _inspectHandler = OnInspectorSelected;
            YoYoStudio.Inspector.InspectorManager.OnInspectorSelected.DoEveryTime(_inspectHandler);
            Log.Info(Tag, "InspectorManager.OnInspectorSelected assinado (Inspetor segue a selecao)");
        }
        catch (Exception e) { _inspectHandler = null; Log.Warn(Tag, "Inspetor: " + e.Message); }
    }

    static void OnInspectorSelected(IList<object> elements)
    {
        try
        {
            if (!_active || elements == null || elements.Count == 0 || KeyboardMode.IsActive) return;
            if (elements.All(e => e?.GetType().Name == "GMRoom")) return;
            // Selecao feita dentro de um painel (Recursos, Camadas, Prefab Library no dock...): o foco ou o
            // gadget sob o dedo estao num DockContainer.
            var wm = YoYoStudio.IDE.WindowManager;
            foreach (var g in new[] { wm?.ActiveGadget, wm?.GetMouseOverGadget() })
                for (var cur = g; cur != null; cur = cur.parent)
                    if (cur is DockContainer) return;
            string what = elements[0]?.GetType().Name ?? "?";
            WindowManager.OnPostInput.DoOnce(delegate
            {
                try { ShowInspector(what, elements.Count); }
                catch (Exception e) { Log.Warn(Tag, "Abrir Inspetor: " + e.Message); }
            });
        }
        catch (Exception e) { Log.Warn(Tag, "Selecao do Inspetor: " + e.Message); }
    }

    static void ShowInspector(string what, int count)
    {
        var wm = YoYoStudio.IDE.WindowManager;
        var details = wm?.GetDesktopDetails(WindowManager.CurrentDesktopID);
        var docks = details?.DockMan?.GetDocks();
        if (docks == null) return;
        foreach (var d in docks)
        {
            if (d == null) continue;
            for (int i = 0; i < d.GetTabCount(); i++)
            {
                var panel = d.GetTab(i)?.TabPanel;
                if (panel == null || !ContainsType(panel, InspectorPanelType, 8)) continue;
                if (d.DockState == eDockState.Expanded && d.ActiveTabIndex == i) return;
                OpenPanel(docks, d, i);
                Log.Info(Tag, $"Selecao no editor ({count}x {what}): gaveta do Inspetor aberta");
                return;
            }
        }
        Log.Info(Tag, $"Selecao no editor ({what}), mas nenhum dock contem o Inspetor");
    }

    static bool ContainsType(GUIBase g, string fullName, int depth)
    {
        if (g.GetType().FullName == fullName) return true;
        if (depth <= 0) return false;
        foreach (var list in new[] { g.ChildGadgets, g.Gadgets })
        {
            if (list == null) continue;
            foreach (var c in list) if (c != null && ContainsType(c, fullName, depth - 1)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ comandos do Java

    static void Run(string cmd, DesktopDetails? details)
    {
        int colon = cmd.IndexOf(':');
        string name = colon < 0 ? cmd : cmd[..colon];
        string arg = colon < 0 ? "" : cmd[(colon + 1)..];
        if (name == "mode") { SetMode(arg == "1"); return; }
        if (name == "menu") { SendMenu(details, arg); return; }
        if (details?.Chrome == null) return;
        switch (name)
        {
            case "panel":
            {
                var p = arg.Split(':');
                if (p.Length == 2 && int.TryParse(p[0], out int d) && int.TryParse(p[1], out int t)) TogglePanel(details, d, t);
                break;
            }
            case "closepanels": ClosePanels(details); break;
            case "tab": WithTab(details, arg, t => { details.Chrome.tabControl.ActivateTab(t); Log.Info(Tag, "Aba ativa: " + t.Label); }); break;
            case "closetab": WithTab(details, arg, t => { details.Chrome.tabControl.CloseTab(t); Log.Info(Tag, "Aba fechada: " + t.Label); }); break;
            case "newtab": NewWorkspace(details.Chrome.tabControl); break;
            case "win": FocusWindow(arg); break;
            case "run": ClickToolbar(details, "ide_toolbar_play"); break;
            case "stop": ClickToolbar(details, "ide_toolbar_stop"); break;
            case "debug": ClickToolbar(details, "ide_toolbar_debug"); break;
            case "tool": ClickToolbar(details, arg); break;   // botao da moldura pelo LayoutID (Game Options...)
            case "click": ClickMenu(details, arg); break;
            default: Log.Warn(Tag, "Comando desconhecido: " + cmd); break;
        }
        _lastState = null;   // responde rapido ao que mudou
    }

    static void SetMode(bool on)
    {
        _wanted = on;
        try
        {
            var p = MainApplication.Instance.GetSharedPreferences(PrefsName, global::Android.Content.FileCreationMode.Private)!;
            p.Edit()!.PutBoolean(PrefShell, on)!.Apply();
        }
        catch (Exception e) { Log.Warn(Tag, "Gravar preferencia da casca: " + e.Message); }
        Log.Info(Tag, on ? "Casca mobile ligada pelo usuario" : "Interface classica escolhida pelo usuario");
    }

    static void WithTab(DesktopDetails details, string arg, Action<TabItem> act)
    {
        if (!int.TryParse(arg, out int i)) return;
        var tc = details.Chrome!.tabControl;
        var tab = i >= 0 && i < tc.TabCount ? tc.GetTab(i) : null;
        if (tab != null && !tab.Destroyed) act(tab);
    }

    static void NewWorkspace(TabControl tc)
    {
        // O "+" da faixa de abas (TabControl.TabControl_AddButtonClicked, protegido): cria a aba e avisa
        // os ouvintes (OnUserTabAdded), como o clique original.
        var m = typeof(TabControl).GetMethod("TabControl_AddButtonClicked",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (m == null) { Log.Warn(Tag, "TabControl_AddButtonClicked nao encontrado"); return; }
        m.Invoke(tc, null);
        Log.Info(Tag, "Novo workspace");
    }

    static List<GuiWindow> _wins = new();

    static void FocusWindow(string arg)
    {
        if (!int.TryParse(arg, out int i) || i < 0 || i >= _wins.Count) return;
        var w = _wins[i];
        if (w.Destroyed) return;
        YoYoStudio.IDE.WindowManager?.SetFocus(w);
        MobileWindowPolicy.ShowWindow(w);
        Log.Info(Tag, "Janela do workspace em foco: " + w.Title);
    }

    static void ClickToolbar(DesktopDetails details, string id)
    {
        if (details.stackPanel?.FindGadget(id) is not Button b) { Log.Warn(Tag, id + " nao encontrado"); return; }
        if (b.Deactivated) { Log.Info(Tag, id + " desativado agora"); return; }
        b.ClickButton();
        Log.Info(Tag, "Barra: " + id);
    }

    // ------------------------------------------------------------------ menu original, nivel a nivel
    //
    // Caminho: "" = raiz; "i" = item i da raiz; "i.j.k" = submenus. Raiz = entradas do menu (MenuBar
    // mestre e o do modulo ativo, se separado) + "Barra de ferramentas" (todos os botoes visiveis da barra,
    // inclusive os do editor ativo). Separadores contam no indice (o Java so nao os toca).

    sealed class Node
    {
        public string Label = "";
        public string Kind = "item";    // item | sub | sep | check
        public bool Enabled = true, Checked;
        public string Hotkey = "";
        public string Icon = "";        // LayoutID do botao (o Java escolhe o icone)
        public GUIBase? Gadget;         // MenuEntry / Button a clicar
        public Func<List<Node>>? Children;
    }

    static List<Node> Root(DesktopDetails details)
    {
        var list = new List<Node>();
        var chrome = details.Chrome!;
        var bars = new List<MenuBar>();
        if (chrome.MasterMenuBar != null) bars.Add(chrome.MasterMenuBar);
        if (chrome.Menu_ModuleMenuBar != null && chrome.Menu_ModuleMenuBar != chrome.MasterMenuBar) bars.Add(chrome.Menu_ModuleMenuBar);
        foreach (var bar in bars)
            foreach (var g in bar.StackedGadgets)
            {
                if (g is not MenuBarEntry e || e.RequestDisabled) continue;
                var entry = e;
                list.Add(new Node
                {
                    Label = Clean(e.Label), Kind = "sub", Enabled = !e.Deactivated,
                    Children = () => MenuChildren(entry.LazyMenu != null ? Lazy(entry, entry.LazyMenu) : entry.Menu),
                });
            }
        // Acoes rapidas (tudo o que era botao na moldura: barra de ferramentas, alvo, conta) primeiro e
        // com icone; depois os menus. Executar/Depurar/Parar ja estao na barra superior.
        var quick = ToolbarNodes(details).Where(n => n.Kind != "sep" && !InTopBar(n.Icon)).ToList();
        quick = quick.OrderBy(n => Rank(n.Icon)).ToList();   // estavel: o resto mantem a ordem da barra
        var root = new List<Node> { new Node { Kind = "hdr", Label = "Ações rápidas" } };
        root.AddRange(quick);
        root.Add(new Node { Kind = "hdr", Label = "Menus" });
        root.AddRange(list);
        return root;
    }

    static bool InTopBar(string? id) => id is "ide_toolbar_play" or "ide_toolbar_stop" or "ide_toolbar_debug";

    // Ordem das acoes rapidas: o que se usa mais no celular vem antes.
    static readonly string[] QuickOrder =
    {
        "ide_toolbar_gamesettings", "target_select_button", "ide_toolbar_save", "ide_toolbar_clean",
    };
    // O "Create Executable" da propria IDE pede login YoYo e nao gera nada no aparelho: fica no fim. O
    // caminho que funciona e o "Exportar" do host (primeira linha das acoes rapidas).
    static readonly string[] QuickLast = { "ide_toolbar_package_run", "ide_toolbar_package" };

    static int Rank(string id)
    {
        int i = Array.IndexOf(QuickOrder, id);
        if (i >= 0) return i;
        return Array.IndexOf(QuickLast, id) >= 0 ? QuickOrder.Length + 1 : QuickOrder.Length;
    }

    static List<Node> MenuChildren(ContextMenu? menu)
    {
        var list = new List<Node>();
        if (menu == null) return list;
        // O que a IDE faz ao abrir o menu: ouvintes atualizam rotulos/estado (ex.: "Previa de efeitos").
        try { if (menu.OnActiveStateChange.HasListeners) { menu.OnActiveStateChange.Throw(true); menu.OnActiveStateChange.Throw(false); } }
        catch (Exception e) { Log.Warn(Tag, "Atualizar menu: " + e.Message); }
        try { menu.UpdateLayoutAndRetrieveAllLeafEntries(new List<MenuEntry>()); }   // poda itens de recursos desligados, como ao abrir
        catch (Exception e) { Log.Warn(Tag, "Podar menu: " + e.Message); }
        foreach (var g in menu.Entries.ToArray())
        {
            if (g == null || g.RequestDisabled) continue;
            if (g is SubMenuEntry s)
            {
                var sub = s;
                list.Add(new Node
                {
                    Label = Clean(s.Label), Kind = "sub", Enabled = !s.Deactivated, Gadget = s,
                    Children = () => MenuChildren(sub.LazySubMenu != null ? Lazy(sub, sub.LazySubMenu) : sub.SubMenu),
                });
            }
            else if (g is MenuEntry m)
            {
                list.Add(new Node
                {
                    Label = Clean(m.Label), Kind = m.HasCheckBox ? "check" : "item", Enabled = !m.Deactivated,
                    Checked = m.Checked, Hotkey = m.HotkeyLabel?.Caption ?? "", Gadget = m,
                });
            }
            else if (g is Separator) list.Add(new Node { Kind = "sep" });
        }
        // Separadores no inicio/fim ou repetidos (itens escondidos entre eles) somem.
        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i].Kind == "sep" && (i == 0 || i == list.Count - 1 || list[i - 1].Kind == "sep")) list.RemoveAt(i);
        return list;
    }

    // Todos os botoes das linhas da moldura (menu e barra de ferramentas): os da barra (mestre, do editor
    // ativo, extra), o seletor de alvo (target_select_button, irmao da barra) e o da conta (linha do menu).
    static List<Node> ToolbarNodes(DesktopDetails details)
    {
        var list = new List<Node>();
        var seen = new HashSet<GUIBase>();
        var chrome = details.Chrome!;
        // Pontos de partida conhecidos primeiro (ordem da barra), depois o resto das linhas da moldura.
        var seeds = new List<GUIBase?>
        {
            chrome.MasterToolbar, chrome.Toolbar_ModuleToolbar, chrome.ExtraToolbar,
            details.stackPanel!.FindGadget("target_select_button"), details.stackPanel.FindGadget("account_button"),
        };
        seeds.AddRange(FrameRows(details));
        foreach (var root in seeds)
        {
            if (root == null || root.Destroyed) continue;
            if (list.Count > 0 && list[^1].Kind != "sep") list.Add(new Node { Kind = "sep" });
            if (root is Button) CollectButtons(root, list, seen, 0);
            else { seen.Add(root); foreach (var c in Kids(root)) CollectButtons(c, list, seen, 8); }
        }
        if (list.Count > 0 && list[^1].Kind == "sep") list.RemoveAt(list.Count - 1);
        return list;
    }

    static IEnumerable<GUIBase> FrameRows(DesktopDetails details)
    {
        var tabs = details.Chrome!.tabControl;
        foreach (var c in details.stackPanel!.StackedGadgets.ToArray())
            if (c != null && !c.Destroyed && !IsInside(tabs, c)) yield return c;
    }

    // Filhos de um gadget em qualquer das listas: StackPanel guarda em StackedGadgets (e move para um painel
    // interno no layout), Grid e outros em ChildGadgets ou Gadgets. Duplicatas saem pelo "seen" do chamador.
    static IEnumerable<GUIBase> Kids(GUIBase g)
    {
        var all = new List<GUIBase>();
        if (g is StackPanel sp && sp.StackedGadgets != null) all.AddRange(sp.StackedGadgets);
        if (g.ChildGadgets != null) all.AddRange(g.ChildGadgets);
        if (g.Gadgets != null) all.AddRange(g.Gadgets);
        return all.Where(c => c != null);
    }

    static void CollectButtons(GUIBase g, List<Node> list, HashSet<GUIBase> seen, int depth)
    {
        if (g.RequestDisabled || g is MenuBar || !seen.Add(g)) return;
        if (g is Button b && g is not MenuBarEntry)
        {
            string id = b.LayoutID?.Name ?? "";
            string label = id == "target_select_button" ? TargetLabel(b) : Clean(b.ToolTipCaption);
            if (string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(b.ToolTipLocalisationCaption))
                label = Clean(YoYoStudio.Core.Utils.Language.GetString(b.ToolTipLocalisationCaption));
            if (string.IsNullOrEmpty(label)) label = Clean(b.Label);
            if (string.IsNullOrEmpty(label) || label == id)
                label = id == "account_button" ? "Conta YoYo" : id.Length > 0 ? id : b.GetType().Name;
            list.Add(new Node { Label = label, Enabled = !b.Deactivated, Gadget = b, Icon = id });
            return;
        }
        if (depth <= 0) return;
        foreach (var c in Kids(g)) CollectButtons(c, list, seen, depth - 1);
    }

    // "Alvo: Test | GMS2 VM | Default | Default" (os rotulos target_select_label_0..4 ao lado do botao).
    static string TargetLabel(Button b)
    {
        var parts = new List<string>();
        for (int i = 0; i <= 4; i++)
            if (b.parent?.FindGadget("target_select_label_" + i) is Label l && !l.RequestDisabled && Clean(l.Caption).Length > 0)
                parts.Add(Clean(l.Caption));
        return parts.Count > 0 ? "Alvo: " + string.Join(" | ", parts) : "Alvo de compilação";
    }

    // Menus preguicosos (projetos recentes, layouts...): criados uma vez por abertura do ⋮ e reaproveitados
    // ate o clique (o caminho e resolvido de novo no clique e precisa achar os mesmos itens).
    static readonly Dictionary<GUIBase, ContextMenu> _lazy = new();

    static ContextMenu? Lazy(GUIBase owner, Func<ContextMenu> make)
    {
        if (_lazy.TryGetValue(owner, out var m) && !m.Destroyed) return m;
        m = make();
        if (m != null) _lazy[owner] = m;
        return m;
    }

    static void DropLazy()
    {
        foreach (var m in _lazy.Values)
            try { if (!m.Destroyed && m.parent == null) m.Destroy(); } catch { }
        _lazy.Clear();
    }

    static string Clean(string? s) => (s ?? "").Replace("&", "").Trim();

    static List<Node>? Resolve(DesktopDetails details, string path, out Node? node)
    {
        node = null;
        var level = Root(details);
        if (path.Length == 0) return level;
        foreach (var part in path.Split('.'))
        {
            if (!int.TryParse(part, out int i) || i < 0 || i >= level.Count) return null;
            node = level[i];
            if (node.Children == null) return null;
            level = node.Children();
        }
        return level;
    }

    static Node? ResolveNode(DesktopDetails details, string path)
    {
        int dot = path.LastIndexOf('.');
        string parent = dot < 0 ? "" : path[..dot];
        if (!int.TryParse(dot < 0 ? path : path[(dot + 1)..], out int i)) return null;
        var level = Resolve(details, parent, out _);
        return level != null && i >= 0 && i < level.Count ? level[i] : null;
    }

    static void SendMenu(DesktopDetails? details, string path)
    {
        if (details?.Chrome == null) { CallJava(_onMenu, path, "[]"); return; }
        if (path.Length == 0) DropLazy();   // ⋮ aberto de novo: menus preguicosos refeitos
        var level = Resolve(details, path, out _) ?? new List<Node>();
        var sb = new StringBuilder("[");
        for (int i = 0; i < level.Count; i++)
        {
            var n = level[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"k\":").Append(J(n.Kind)).Append(",\"t\":").Append(J(n.Label))
              .Append(",\"e\":").Append(n.Enabled ? 1 : 0).Append(",\"c\":").Append(n.Checked ? 1 : 0)
              .Append(",\"h\":").Append(J(n.Hotkey)).Append(",\"ic\":").Append(J(n.Icon)).Append('}');
        }
        sb.Append(']');
        CallJava(_onMenu, path, sb.ToString());
    }

    static void ClickMenu(DesktopDetails details, string path)
    {
        var n = ResolveNode(details, path);
        if (n?.Gadget == null || !n.Enabled) { Log.Warn(Tag, $"Item de menu {path} indisponivel"); return; }
        if (n.Gadget is MenuEntry m && m.HasCheckBox && m.CheckBox is { } cb)
        {
            // MenuEntry com caixa: a IDE alterna no OnMouseUp (CheckBox.MouseUpEvent); OnUpdated avisa.
            if (!cb.ReadOnly) cb.Checked = !cb.Checked;
            Log.Info(Tag, $"Menu: '{n.Label}' -> {cb.Checked}");
            return;
        }
        if (n.Gadget is Button b) { b.ClickButton(); Log.Info(Tag, $"Menu: '{n.Label}'"); }
    }

    // ------------------------------------------------------------------ estado para o Java

    static string? _lastState;

    static void PublishState(DesktopDetails? details, bool ready)
    {
        string json = ready && _active && details != null ? BuildState(details) : "{\"on\":0}";
        if (json == _lastState) return;
        if (CallJava(_onState, json)) _lastState = json;   // falhou: tenta de novo no proximo ciclo
    }

    static string BuildState(DesktopDetails details)
    {
        var chrome = details.Chrome!;
        var sb = new StringBuilder("{\"on\":1");
        string proj = "";
        try { proj = ProjectName(); } catch { }
        sb.Append(",\"proj\":").Append(J(proj));

        var tc = chrome.tabControl;
        sb.Append(",\"tabs\":[");
        string active = "";
        bool workspaceActive = false;
        for (int i = 0; i < tc.TabCount; i++)
        {
            var t = tc.GetTab(i);
            if (t == null) continue;
            bool a = t.IsActive;
            if (a) { active = Clean(t.Label); workspaceActive = ContainsWorkspace(t.TabPanel); }
            if (i > 0) sb.Append(',');
            sb.Append("{\"t\":").Append(J(Clean(t.Label))).Append(",\"a\":").Append(a ? 1 : 0)
              .Append(",\"c\":").Append(t.CloseButtonDisabled ? 0 : 1).Append('}');
        }
        sb.Append("],\"ed\":").Append(J(active));

        _wins = workspaceActive ? WorkspaceWindows() : new List<GuiWindow>();
        var focused = YoYoStudio.IDE.WindowManager?.ActiveGadget;
        sb.Append(",\"wins\":[");
        for (int i = 0; i < _wins.Count; i++)
        {
            if (i > 0) sb.Append(',');
            string title = Clean(_wins[i].Title);
            if (title.Length == 0) title = _wins[i].GetType().Name;
            sb.Append("{\"t\":").Append(J(title)).Append(",\"a\":").Append(focused != null && IsInside(focused, _wins[i]) ? 1 : 0).Append('}');
        }
        sb.Append(']');

        sb.Append(",\"panels\":[");
        bool first = true;
        var docks = details.DockMan!.GetDocks() ?? Array.Empty<DockContainer>();
        for (int d = 0; d < docks.Length; d++)
        {
            var dock = docks[d];
            if (dock == null || dock.Destroyed) continue;
            for (int i = 0; i < dock.GetTabCount(); i++)
            {
                var t = dock.GetTab(i);
                if (t == null) continue;
                if (!first) sb.Append(',');
                first = false;
                bool open = dock.DockState == eDockState.Expanded && dock.ActiveTabIndex == i;
                bool insp = t.TabPanel != null && ContainsType(t.TabPanel, InspectorPanelType, 8);
                sb.Append("{\"d\":").Append(d).Append(",\"i\":").Append(i)
                  .Append(",\"t\":").Append(J(Clean(t.Label))).Append(",\"id\":").Append(J(t.LayoutID?.Name ?? ""))
                  .Append(",\"insp\":").Append(insp ? 1 : 0).Append(",\"o\":").Append(open ? 1 : 0).Append('}');
            }
        }
        sb.Append(']');

        var bar = chrome.MasterToolbar;
        sb.Append(",\"play\":").Append(Enabled(bar?.FindGadget("ide_toolbar_play")))
          .Append(",\"stop\":").Append(Enabled(bar?.FindGadget("ide_toolbar_stop")))
          .Append(",\"debug\":").Append(Enabled(bar?.FindGadget("ide_toolbar_debug")))
          .Append(",\"go\":").Append(Enabled(bar?.FindGadget("ide_toolbar_gamesettings")));

        string prog = "";
        try
        {
            if (chrome.ProgressPanel != null && !chrome.ProgressPanel.RequestDisabled && chrome.ProgressPanel.AllowRender)
                prog = Clean(chrome.ProgressLabel?.Caption);
        }
        catch { }
        sb.Append(",\"prog\":").Append(J(prog));
        sb.Append(",\"kbd\":").Append(KeyboardMode.IsActive ? 1 : 0);
        sb.Append('}');
        return sb.ToString();
    }

    // ProjectManager.Current e um GMProject (CoreResources.dll, sem referencia de compilacao no host):
    // le Current.name por reflexao, como a IDE mostra no titulo.
    static System.Reflection.PropertyInfo? _currentProp;
    static string ProjectName()
    {
        _currentProp ??= typeof(YoYoStudio.Resources.ProjectManager).GetProperty("Current",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var proj = _currentProp?.GetValue(null);
        if (proj == null) return "";
        var t = proj.GetType();
        object? name = t.GetField("name")?.GetValue(proj) ?? t.GetProperty("name")?.GetValue(proj);
        return name as string ?? "";
    }

    static int Enabled(GUIBase? g) => g != null && !g.Deactivated && !g.Destroyed ? 1 : 0;

    static bool ContainsWorkspace(GUIBase? g)
    {
        if (g == null) return false;
        if (g is WorkspaceArea) return true;
        foreach (var list in new[] { g.ChildGadgets, g.Gadgets })
        {
            if (list == null) continue;
            foreach (var c in list) if (c is WorkspaceArea) return true;
        }
        return false;
    }

    // Janelas dos editores no workspace atual (a mais externa de cada encadeamento).
    static List<GuiWindow> WorkspaceWindows()
    {
        var list = new List<GuiWindow>();
        var area = YoYoStudio.IDE.WindowManager?.GetCurrentWorkspaceArea();
        if (area != null) CollectWindows(area, list, 6);
        return list;
    }

    static void CollectWindows(GUIBase g, List<GuiWindow> list, int depth)
    {
        foreach (var src in new[] { g.ChildGadgets, g.Gadgets })
        {
            if (src == null) continue;
            foreach (var c in src)
            {
                if (c == null || c.Destroyed) continue;
                if (c is GuiWindow w) { if (!list.Contains(w)) list.Add(w); continue; }
                if (depth > 0) CollectWindows(c, list, depth - 1);
            }
        }
    }

    static string J(string? s)
    {
        if (s == null) return "\"\"";
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append(' '); break;
                default:
                    if (ch < 0x20) sb.Append(' '); else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    static bool IsInside(GUIBase g, GUIBase ancestor)
    {
        for (var cur = g; cur != null; cur = cur.parent) if (cur == ancestor) return true;
        return false;
    }

    // Uma vez: o que a moldura tinha (confere no log os nomes reais da barra e do menu).
    static void LogChrome(DesktopDetails details)
    {
        try
        {
            var root = Root(details);
            Log.Info(Tag, "Menu (raiz): " + string.Join(" | ", root.Select(n => n.Label)));
            var tb = ToolbarNodes(details);
            Log.Info(Tag, "Barra de ferramentas: " + string.Join(" | ", tb.Where(n => n.Kind != "sep")
                .Select(n => $"{n.Label}[{n.Gadget?.LayoutID?.Name ?? n.Gadget?.GetType().Name}]")));
        }
        catch (Exception e) { Log.Warn(Tag, "Inventario da moldura: " + e.Message); }
    }
}
