using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Android.Util;

namespace GmIde;

// Filtro de eventos do SDL (SDL_SetEventFilter) que adapta toque para a IDE,
// antes do SDL_PollEvent dela (docs/input-analysis.md, secoes 3 e 5):
//   toque rapido           -> clique esquerdo (o "down" e segurado ate decidir)
//   arrastar 1 dedo        -> arrastar com o esquerdo
//   toque longo (~500 ms)  -> vibra e "arma": soltar = clique direito no ponto do toque;
//                             arrastar depois disso = arrastar com o esquerdo (arrastar e soltar)
//   arrastar 1 dedo numa area rolavel -> rolagem direta 1:1 com inercia (MobileScroll)
//   toque com 2 dedos      -> clique direito no ponto do 1o dedo
//   arrastar com 2 dedos   -> num canvas (room, sprite, tileset) ou no workspace: o conteudo acompanha
//                             os dedos 1:1 nos dois eixos (MobileWorkspace); em listas/Inspetor/codigo:
//                             SDL_MOUSEWHEEL (vertical e horizontal, rolagem natural)
//   pinca com 2 dedos      -> no workspace (inclusive sobre janelas de editores): zoom continuo entre os
//                             dedos (MobileWorkspace.SetPinch); em canvas e paineis: Ctrl + SDL_MOUSEWHEEL
//   modo Mover (barra)     -> um dedo arrasta o canvas sob ele (room...) ou o workspace
//   Voltar (AC_BACK)       -> Esc
// Barra de atalhos (ShortcutBar.java / ShortcutBridge.cs): tecla HELP = comandos;
// "proximo toque = clique direito"; Ctrl/Shift/Alt de um uso soltos depois do
// proximo clique/tecla; SDL_TEXTINPUT bloqueado com Ctrl/Alt presos.
// So mexe em eventos de mouse com which == SDL_TOUCH_MOUSEID (os sinteticos do
// toque). Mouse real passa intacto. SDL_MULTIGESTURE e descartado (ver Handle).
//
// Preferencias ("gmide", lidas no Install):
//   touch_wheel_step_dp  int (padrao 24): deslocamento dos dedos por "notch" da roda
//   touch_scroll_invert  bool (padrao false): inverte a rolagem natural
//   touch_pinch_invert   bool (padrao false): inverte o sentido do zoom da pinca
//
// Threads: o filtro roda na thread que gerou o evento (UI do Java para toque,
// thread do SDL para teclado) e o timer do toque longo roda no pool. Nunca
// chamamos SDL_PushEvent segurando _gate (o SDL segura um mutex proprio
// enquanto chama o filtro -> deadlock cruzado). Eventos injetados passam pelo
// filtro de novo (SDL_PushEvent chama o filtro na mesma thread) e sao
// reconhecidos pela flag [ThreadStatic] _injecting.
static unsafe class TouchInput
{
    const string Tag = "GmIde.Touch";
    const int LongPressMs = 500;
    const int TwoFingerTapMaxMs = 350;
    const int ProbeWaitMs = 150;   // espera maxima pela sondagem de rolagem antes de virar arrasto

    enum Mode { Idle, Pending, Dragging, LongPressed, LongArmed, TwoFinger, Panning, Scrolling }

    // Rolagem com um dedo (listas, arvores, paineis): no toque, um movimento sem botao leva o ponteiro
    // ao dedo e a IDE (MobileWindowFit.ProbeScroll, thread da IDE, no proximo quadro) diz se o gadget
    // sob o dedo esta numa area rolavel e em que eixo. Arrastar nesse eixo vira roda do mouse (como
    // os dois dedos); no outro eixo continua arrastando (ex.: asset da Asset Browser para a room).
    public static volatile int ProbeGen, ProbeX, ProbeY;
    static volatile int _probeDoneGen, _probeAxes;
    public static void SetProbe(int gen, bool vertical, bool horizontal)
    {
        _probeAxes = (vertical ? 1 : 0) | (horizontal ? 2 : 0);
        _probeDoneGen = gen;
    }
    static bool _scrollVertical;
    static bool _stopperTap;        // toque que parou uma inercia: nao vira clique

    // Amostras recentes do dedo no eixo da rolagem, para a velocidade do fling.
    const int Samples = 8;
    static readonly long[] _sampleT = new long[Samples];
    static readonly float[] _sampleP = new float[Samples];
    static int _sampleN;
    static void AddSample(float p)
    {
        long t = Environment.TickCount64;
        int i = _sampleN % Samples;
        _sampleT[i] = t; _sampleP[i] = p; _sampleN++;
    }
    // px/s nos ultimos ~100 ms (0 se o dedo parou antes de soltar).
    static float Velocity()
    {
        if (_sampleN < 2) return 0;
        int last = (_sampleN - 1) % Samples;
        long tEnd = _sampleT[last];
        if (Environment.TickCount64 - tEnd > 80) return 0;
        int first = last;
        for (int k = 1; k < Math.Min(_sampleN, Samples); k++)
        {
            int i = (_sampleN - 1 - k) % Samples;
            if (tEnd - _sampleT[i] > 100) break;
            first = i;
        }
        long dt = tEnd - _sampleT[first];
        return dt <= 0 ? 0 : (_sampleP[last] - _sampleP[first]) * 1000f / dt;
    }

    /// <summary>
    /// Modo "Mover" da barra de atalhos (ShortcutBridge "pan"): arrastar com um dedo move o workspace
    /// e a pinca da zoom nele (MobileWorkspace, aplicado na thread da IDE), mesmo em cima de janelas.
    /// Toque rapido continua sendo clique. Os movimentos passam para a IDE como mouse sem botao (so
    /// para ela processar o evento e o observador aplicar o deslocamento).
    /// </summary>
    public static volatile bool PanMode;
    enum TwoKind { Undecided, Pan, Pinch }

    // Dois dedos juntos fora do modo Mover: sobre um canvas (room, sprite, tileset) ou o workspace, o
    // conteudo acompanha os dedos 1:1 nos dois eixos (MobileWorkspace); sobre listas, Inspetor e codigo
    // vira roda do mouse. A IDE responde no quadro seguinte (MobileWindowFit.ProbeCanvas); enquanto isso
    // o deslocamento acumula, e sem resposta em CanvasProbeWaitMs vale a roda.
    enum TwoTarget { Unknown, Canvas, Wheel }
    static TwoTarget _twoTarget;
    static int _twoProbeGen;
    static long _twoProbeTicks;
    const int CanvasProbeWaitMs = 200;

    // Pinca: cada variacao de 15% na distancia entre os dedos = 1 notch de Ctrl+roda.
    const float PinchStepRatio = 1.15f;

    static readonly object _gate = new();
    static Mode _mode = Mode.Idle;
    static int _generation;
    static uint _windowId;
    static int _downX, _downY, _lastX, _lastY;
    static long _downTicks;
    static readonly HashSet<long> _fingers = new();
    static float _twoAccX, _twoAccY;
    static bool _twoMoved;
    static long _twoStartTicks;
    static int _winW, _winH;
    static Timer? _timer;

    // Posicao (normalizada) de cada dedo, para pinca e centroide.
    static readonly Dictionary<long, (float x, float y)> _fingerPos = new();
    static TwoKind _twoKind;
    static float _pinchStartDist, _pinchRefDist, _twoStartCx, _twoStartCy;
    static float _prevCx, _prevCy;   // centro dos dedos no ultimo movimento (pinca do workspace)
    static bool _pinchCtrlDown;   // empurramos um LCTRL sintetico
    static int _pinchSteps, _panSteps;

    static float _slopPx = 32, _wheelStepPx = 64;

    // Duplo toque: a IDE so aceita duplo clique com o 2o clique a <= 10 unidades "de 1680" do 1o
    // (Graphics Desktop.IsPositionValidMultipleClick: ~6 px nesta tela) e em < 350 ms (SlowClickMs).
    // Um dedo nunca acerta 6 px; o 2o toque rapido perto do 1o e entregue na mesma posicao.
    const int DoubleTapMs = 450;
    static float _doubleTapSnapPx = 120;
    static long _lastTapTicks;
    static int _lastTapX, _lastTapY;
    static bool _scrollInvert, _pinchInvert;
    static bool _installed;
    static IntPtr _previousFilter, _previousUserdata;

    [ThreadStatic] static bool _injecting;

    // ---- "solta" do esquerdo segurado ate a IDE ver o botao pressionado
    // Um toque rapido virava MOTION+DOWN+UP na mesma leitura da fila do SDL: a IDE le a fila inteira
    // por quadro (SystemPlatformSDL2.Update) e so chama OnMouseOver (onde a paleta de tiles, o lapis e
    // a borracha de tiles agem, RoomEditor_TileDisplay.TileDisplay_SelectTiles) com o estado do fim
    // do quadro, ja sem o botao. Regra: o UP sintetico do esquerdo so entra na fila depois de a IDE
    // rodar 2 quadros (WindowManager.OnProcess) desde o DOWN; e liberado da thread da IDE (OnIdeFrame)
    // ou, se a IDE nao rodar quadros (dialogo nativo), por um timer.
    public static volatile int IdeFrame;
    static int _leftDownFrame = int.MinValue / 2;
    static Sdl.Event _heldUp;
    static bool _hasHeldUp;
    static Timer? _upTimer;
    const int HeldUpFallbackMs = 250;

    // Marca o DOWN sintetico do esquerdo (chamar com _gate).
    static void MarkLeftDown() => _leftDownFrame = IdeFrame;

    // Segura o UP se a IDE ainda nao viu o DOWN num quadro inteiro (chamar com _gate). true = segurado.
    static bool HoldUpIfNeeded(Sdl.Event* e)
    {
        if (IdeFrame - _leftDownFrame >= 2) return false;
        _heldUp = *e;
        _hasHeldUp = true;
        _upTimer?.Change(HeldUpFallbackMs, Timeout.Infinite);
        return true;
    }

    // Tira o UP segurado (chamar com _gate); o chamador empurra fora do lock.
    static bool TakeHeldUp(out Sdl.Event up)
    {
        up = _heldUp;
        if (!_hasHeldUp) return false;
        _hasHeldUp = false;
        _upTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        return true;
    }

    /// <summary>Thread da IDE, a cada quadro (WindowManager.OnProcess, IdeSetup).</summary>
    public static void OnIdeFrame()
    {
        int frame = ++IdeFrame;
        Sdl.Event up = default;
        bool push;
        lock (_gate) push = _hasHeldUp && frame - _leftDownFrame >= 2 && TakeHeldUp(out up);
        if (push) PushHeldUp(up);
    }

    static void OnHeldUpTimer(object? _)
    {
        Sdl.Event up;
        bool push;
        lock (_gate) push = TakeHeldUp(out up);
        if (push)
        {
            Log.Info(Tag, "UP segurado liberado pelo timer (IDE sem quadros)");
            PushHeldUp(up);
        }
    }

    static void PushHeldUp(Sdl.Event up)
    {
        Push(new List<Sdl.Event> { up });
        // Ctrl/Shift/Alt "proxima tecla" da barra valem para este clique: so soltar depois do UP.
        ShortcutBridge.NotifyKeyUsed("clique");
    }

    // Diagnostico: loga as primeiras ocorrencias de cada tipo de evento.
    static readonly Dictionary<uint, int> _seen = new();
    const int SeenLimit = 4;

    public static void Install(float density)
    {
        int stepDp = 24;
        try
        {
            var p = MainApplication.Instance.GetSharedPreferences("gmide", global::Android.Content.FileCreationMode.Private)!;
            stepDp = Math.Clamp(p.GetInt("touch_wheel_step_dp", 24), 4, 200);
            _scrollInvert = p.GetBoolean("touch_scroll_invert", false);
            _pinchInvert = p.GetBoolean("touch_pinch_invert", false);
        }
        catch (Exception e) { Log.Warn(Tag, "Preferencias de toque: " + e.Message); }
        _slopPx = Math.Max(12f, 10f * density);           // ~10 dp
        _doubleTapSnapPx = Math.Max(24f, 40f * density);  // ~40 dp
        _wheelStepPx = Math.Max(16f, stepDp * density);   // dp por "notch" da roda
        if (_installed)
        {
            Log.Info(Tag, "Filtro ja instalado (SDL_main rodando de novo); reinstalando");
        }
        _installed = true;
        _timer ??= new Timer(OnLongPressTimer, null, Timeout.Infinite, Timeout.Infinite);
        _upTimer ??= new Timer(OnHeldUpTimer, null, Timeout.Infinite, Timeout.Infinite);

        if (Sdl.SDL_GetEventFilter(out var prev, out var prevUd) != 0)
        {
            delegate* unmanaged[Cdecl]<IntPtr, Sdl.Event*, int> mine = &Filter;
            if (prev != (IntPtr)mine)
            {
                _previousFilter = prev;
                _previousUserdata = prevUd;
                Log.Warn(Tag, $"Ja havia um SDL_EventFilter (0x{prev:x}); sera encadeado depois do nosso");
            }
        }
        Sdl.SDL_SetEventFilter(&Filter, IntPtr.Zero);
        Log.Info(Tag, $"Filtro de eventos instalado: slop={_slopPx:F0}px, passo da roda={_wheelStepPx:F0}px ({stepDp}dp), " +
                      $"rolagem {(_scrollInvert ? "invertida" : "natural")}, pinca {(_pinchInvert ? "invertida" : "normal")} " +
                      $"(passo {PinchStepRatio:F2}x), toque longo={LongPressMs}ms, touch devices={Sdl.SDL_GetNumTouchDevices()}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static int Filter(IntPtr userdata, Sdl.Event* e)
    {
        try
        {
            if (_injecting) return 1;
            int keep = Handle(e) ? 1 : 0;
            if (keep == 1 && _previousFilter != IntPtr.Zero)
                keep = ((delegate* unmanaged[Cdecl]<IntPtr, Sdl.Event*, int>)_previousFilter)(_previousUserdata, e);
            return keep;
        }
        catch (Exception ex)
        {
            // Nunca deixar excecao escapar para o C.
            Log.Error(Tag, "Excecao no filtro: " + ex);
            return 1;
        }
    }

    static void Diag(Sdl.Event* e)
    {
        int n;
        lock (_seen)
        {
            _seen.TryGetValue(e->type, out n);
            if (n >= SeenLimit) return;
            _seen[e->type] = n + 1;
        }
        string d = e->type switch
        {
            Sdl.MOUSEBUTTONDOWN or Sdl.MOUSEBUTTONUP =>
                $"win={e->windowID} which=0x{e->which:x} button={e->button} state={e->state} clicks={e->clicks} pos=({e->x},{e->y})",
            Sdl.MOUSEMOTION => $"win={e->windowID} which=0x{e->which:x} pos=({e->x},{e->y}) rel=({e->xrel},{e->yrel})",
            Sdl.MOUSEWHEEL => $"win={e->windowID} which=0x{e->which:x} x={e->wheelX} y={e->wheelY} precise=({e->wheelPreciseX},{e->wheelPreciseY})",
            Sdl.FINGERDOWN or Sdl.FINGERUP or Sdl.FINGERMOTION =>
                $"touch={e->touchId} finger={e->fingerId} pos=({e->fx:F3},{e->fy:F3}) d=({e->fdx:F3},{e->fdy:F3}) win={e->fingerWindowID}",
            Sdl.KEYDOWN or Sdl.KEYUP => $"win={e->windowID} scancode={e->scancode} sym=0x{e->sym:x} mod=0x{e->mod:x} repeat={e->keyRepeat}",
            Sdl.TEXTINPUT or Sdl.TEXTEDITING => "texto=\"" + Marshal.PtrToStringUTF8((IntPtr)((byte*)e + 12)) + "\"",
            Sdl.MULTIGESTURE => "(pinca/rotacao com 2+ dedos)",
            _ => ""
        };
        if (d.Length > 0 || n == 0)
            Log.Info(Tag, $"evento 0x{e->type:x} #{n + 1}: {d}");
    }

    // true = deixa o evento seguir; false = descarta.
    static bool Handle(Sdl.Event* e)
    {
        Diag(e);
        switch (e->type)
        {
            case Sdl.KEYDOWN:
            case Sdl.KEYUP:
                if (e->scancode == Sdl.SCANCODE_AC_BACK || e->sym == Sdl.SDLK_AC_BACK)
                {
                    if (e->type == Sdl.KEYDOWN) Log.Info(Tag, "Voltar -> Esc");
                    e->scancode = Sdl.SCANCODE_ESCAPE;
                    e->sym = Sdl.SDLK_ESCAPE;
                }
                if (e->scancode == Sdl.SCANCODE_HELP)
                {
                    // Canal da barra de atalhos. Deixa passar: o observador de teclado
                    // da IDE (IdeSetup, thread do SDL) aplica a escala pendente.
                    if (e->type == Sdl.KEYDOWN) ShortcutBridge.DrainCommands();
                    return true;
                }
                if (e->type == Sdl.KEYUP)
                {
                    if (Sdl.IsModifierScancode(e->scancode)) ShortcutBridge.NotifyModifierUp(e->scancode);
                    else ShortcutBridge.NotifyKeyUsed("tecla " + e->scancode);
                }
                return true;

            case Sdl.TEXTINPUT:
                if (ShortcutBridge.BlocksTextInput)
                {
                    if (_blockedText++ < 8)
                        Log.Info(Tag, "SDL_TEXTINPUT descartado (Ctrl/Alt presos pela barra): \"" +
                                      Marshal.PtrToStringUTF8((IntPtr)((byte*)e + 12)) + "\"");
                    return false;
                }
                return true;

            case Sdl.WINDOWEVENT:
                // SIZE_CHANGED (6) / RESIZED (5): guarda o tamanho para converter dedos normalizados em pixels.
                if (e->windowEvent is 5 or 6) { _winW = e->data1; _winH = e->data2; }
                return true;

            case Sdl.MOUSEBUTTONDOWN:
            case Sdl.MOUSEBUTTONUP:
            case Sdl.MOUSEMOTION:
                if (e->which != Sdl.TOUCH_MOUSEID) return HandleRealMouse(e);
                return HandleTouchMouse(e);

            case Sdl.MOUSEWHEEL:
                return e->which == Sdl.TOUCH_MOUSEID || HandleRealMouse(e);

            case Sdl.FINGERDOWN:
            case Sdl.FINGERUP:
            case Sdl.FINGERMOTION:
                HandleFinger(e);
                return true; // a IDE so usa FINGERDOWN/UP sem argumentos; deixa passar

            case Sdl.MULTIGESTURE:
                // Os dois dedos sao nossos (rolagem, arraste do canvas, pinca). O gesto nativo do SDL
                // chegava junto ao PanArea/WorkspaceArea (PanArea_Multigesture): qualquer variacao de
                // distancia > 0.03 entre os dedos virava zoom e WindowManager.SetZoomLockGadget, e com o
                // zoom travado DoMouseWheelEvent descarta toda roda ate o dedo sair. Arrastar a room
                // para os lados com dois dedos dava zoom tremido ou nada.
                return false;

            default:
                return true;
        }
    }

    static bool HandleTouchMouse(Sdl.Event* e)
    {
        List<Sdl.Event>? inject = null;
        bool keep;
        bool clickDone = false, rightClickUsed = false;
        lock (_gate)
        {
            switch (e->type)
            {
                case Sdl.MOUSEBUTTONDOWN when e->button == Sdl.BUTTON_LEFT:
                    if (TakeHeldUp(out var pendingUp))
                    {
                        (inject ??= new()).Add(pendingUp);   // UP do toque anterior antes deste gesto
                        clickDone = true;
                    }
                    // O SDL manda o down emulado ANTES do FINGERDOWN do 1o dedo: aqui nao
                    // deveria haver dedo nenhum. Se houver, perdemos um FINGERUP (pausa etc.).
                    if (_fingers.Count > 0)
                    {
                        Log.Warn(Tag, $"Limpando {_fingers.Count} dedo(s) presos de um gesto anterior");
                        _fingers.Clear();
                        _fingerPos.Clear();
                        if (_pinchCtrlDown)
                        {
                            _pinchCtrlDown = false;
                            (inject ??= new()).Add(CtrlKey(Sdl.KEYUP));
                        }
                    }
                    _generation++;
                    _windowId = e->windowID;
                    _downX = _lastX = e->x;
                    _downY = _lastY = e->y;
                    _downTicks = Environment.TickCount64;
                    if (ShortcutBridge.RightClickMode != ShortcutBridge.Off)
                    {
                        // Botao "Btn D" da barra: este toque vira clique direito imediato.
                        _mode = Mode.LongPressed;
                        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                        (inject ??= new()).AddRange(RightClick(_windowId, _downX, _downY));
                        rightClickUsed = ShortcutBridge.RightClickMode == ShortcutBridge.OneShot;
                        clickDone = true;
                        Log.Info(Tag, $"Toque com 'proximo toque = clique direito' -> clique direito em ({_downX},{_downY})" +
                                      (rightClickUsed ? "" : " (travado)"));
                        keep = false;
                        break;
                    }
                    _stopperTap = MobileScroll.Flinging;   // toque que so para a inercia
                    MobileScroll.Stop();
                    _mode = Mode.Pending;
                    if (!PanMode)
                    {
                        _timer?.Change(LongPressMs, Timeout.Infinite); // no modo Mover nao ha toque longo
                        ProbeX = _downX; ProbeY = _downY; ProbeGen++;
                        (inject ??= new()).Add(Motion(_windowId, _downX, _downY)); // ponteiro ao dedo, sem botao
                    }
                    keep = false; // segura o "down" ate saber se e toque, arrasto ou toque longo
                    break;

                case Sdl.MOUSEMOTION:
                {
                    int prevX = _lastX, prevY = _lastY;
                    _lastX = e->x; _lastY = e->y;
                    if (_mode == Mode.Scrolling)
                    {
                        // Rolagem direta: o conteudo acompanha o dedo (aplicado na thread da IDE).
                        float d = _scrollVertical ? e->y - prevY : e->x - prevX;
                        MobileScroll.Drag(_scrollInvert ? -d : d);
                        AddSample(_scrollVertical ? e->y : e->x);
                        keep = false; // o ponteiro fica onde o dedo encostou
                        break;
                    }
                    if (_mode == Mode.LongArmed)
                    {
                        // Segurou e depois arrastou: arrastar com o esquerdo desde o ponto do toque
                        // (arrastar e soltar recursos, selecionar texto no editor de codigo...).
                        float ax = e->x - _downX, ay = e->y - _downY;
                        if (ax * ax + ay * ay > _slopPx * _slopPx)
                        {
                            _mode = Mode.Dragging;
                            inject = new() { Motion(_windowId, _downX, _downY), Button(Sdl.MOUSEBUTTONDOWN, Sdl.BUTTON_LEFT, _windowId, _downX, _downY) };
                            MarkLeftDown();
                            keep = true;
                            Log.Info(Tag, $"Toque longo + arrasto -> arrastar com o esquerdo desde ({_downX},{_downY})");
                        }
                        else keep = false;
                        break;
                    }
                    if (_mode == Mode.Panning)
                    {
                        MobileWorkspace.AddPan(prevX - e->x, prevY - e->y);
                        e->motionState = 0; // mouse "sem botao" para a IDE
                        keep = true;
                        break;
                    }
                    if (_mode == Mode.Pending)
                    {
                        float dx = e->x - _downX, dy = e->y - _downY;
                        // A sondagem (o que esta sob o dedo e rolavel?) responde no quadro seguinte da IDE.
                        // Um gesto rapido passa do limiar antes disso: espera a resposta (ate ProbeWaitMs)
                        // em vez de decidir "arrastar" as cegas.
                        if (!PanMode && _probeDoneGen != ProbeGen &&
                            Environment.TickCount64 - _downTicks < ProbeWaitMs &&
                            dx * dx + dy * dy > _slopPx * _slopPx)
                        {
                            keep = false;
                            break;
                        }
                        int axes = _probeDoneGen == ProbeGen ? _probeAxes : 0;
                        bool vert = Math.Abs(dy) >= Math.Abs(dx);
                        if (!PanMode && axes != 0 && dx * dx + dy * dy > _slopPx * _slopPx &&
                            (vert ? (axes & 1) != 0 : axes == 2))
                        {
                            _mode = Mode.Scrolling;
                            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                            _scrollVertical = vert;
                            _sampleN = 0;
                            AddSample(vert ? _downY : _downX);
                            AddSample(vert ? e->y : e->x);
                            float first = vert ? dy : dx;
                            MobileScroll.Begin(ProbeGen, vert, _scrollInvert ? -first : first);
                            keep = false;
                            break;
                        }
                        if (PanMode && dx * dx + dy * dy > _slopPx * _slopPx)
                        {
                            _mode = Mode.Panning;
                            MobileWorkspace.BeginGesture();   // alvo: canvas sob o dedo (room...) ou workspace
                            MobileWorkspace.AddPan(_downX - e->x, _downY - e->y);
                            e->motionState = 0;
                            keep = true;
                        }
                        else if (dx * dx + dy * dy > _slopPx * _slopPx)
                        {
                            // Virou arrasto: entrega motion+down no ponto original antes deste motion.
                            _mode = Mode.Dragging;
                            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                            inject = new() { Motion(_windowId, _downX, _downY), Button(Sdl.MOUSEBUTTONDOWN, Sdl.BUTTON_LEFT, _windowId, _downX, _downY) };
                            MarkLeftDown();
                            keep = true;
                        }
                        else keep = false; // tremida do dedo: ignora
                    }
                    else keep = _mode is Mode.Idle or Mode.Dragging;
                    break;
                }

                case Sdl.MOUSEBUTTONUP when e->button == Sdl.BUTTON_LEFT:
                    switch (_mode)
                    {
                        case Mode.Pending when _stopperTap:
                            // Toque que so parou uma rolagem com inercia: nao clica no que estiver embaixo.
                            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                            keep = false;
                            break;
                        case Mode.Scrolling:
                        {
                            float v = Velocity();
                            MobileScroll.Release(_scrollInvert ? -v : v);
                            keep = false;
                            break;
                        }
                        case Mode.LongArmed:
                            // Segurou e soltou sem arrastar: menu de contexto (clique direito).
                            inject = RightClick(_windowId, _downX, _downY);
                            Log.Info(Tag, $"Toque longo solto -> clique direito em ({_downX},{_downY})");
                            keep = false;
                            clickDone = true;
                            break;
                        case Mode.Pending:
                        {
                            // Toque rapido: entrega o down segurado e depois este up.
                            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                            long now = Environment.TickCount64;
                            float ddx = _downX - _lastTapX, ddy = _downY - _lastTapY;
                            if (now - _lastTapTicks <= DoubleTapMs && ddx * ddx + ddy * ddy <= _doubleTapSnapPx * _doubleTapSnapPx)
                            {
                                _downX = _lastTapX; _downY = _lastTapY; // mesmo ponto do 1o toque: a IDE ve duplo clique
                                _lastTapTicks = 0;
                            }
                            else { _lastTapTicks = now; _lastTapX = _downX; _lastTapY = _downY; }
                            inject = new() { Motion(_windowId, _downX, _downY), Button(Sdl.MOUSEBUTTONDOWN, Sdl.BUTTON_LEFT, _windowId, _downX, _downY) };
                            MarkLeftDown();
                            e->x = _downX; e->y = _downY;
                            // O UP so entra depois de a IDE ver o DOWN num quadro (NotifyKeyUsed vai junto).
                            keep = !HoldUpIfNeeded(e);
                            clickDone = keep;
                            if (ShortcutBridge.AnyHeld)
                                Log.Info(Tag, $"Toque em ({_downX},{_downY}) com modificadores da barra: " +
                                              $"Ctrl={ShortcutBridge.Ctrl} Shift={ShortcutBridge.Shift} Alt={ShortcutBridge.Alt}");
                            break;
                        }
                        case Mode.Dragging:
                        case Mode.Idle:
                            keep = !HoldUpIfNeeded(e);   // arrasto curtissimo: o mesmo cuidado
                            clickDone = keep;
                            break;
                        default: // LongPressed / TwoFinger / Panning: nada a clicar
                            keep = false;
                            break;
                    }
                    if (_mode != Mode.TwoFinger) _mode = Mode.Idle;
                    break;

                default:
                    keep = true;
                    break;
            }
        }
        if (inject != null) Push(inject);
        if (rightClickUsed) ShortcutBridge.NotifyRightClickUsed();
        // Ctrl/Shift/Alt "proxima tecla" valem para este clique; a barra solta depois
        // (o Java posta o keyup, que chega a IDE depois deste evento).
        if (clickDone) ShortcutBridge.NotifyKeyUsed("clique");
        return keep;
    }

    // ---- mouse real (Bluetooth/USB) sem teclado
    // Na IDE desktop a vista de um canvas anda com o botao do meio, Espaco+esquerdo ou Shift+roda, e o
    // zoom e Ctrl+roda. No celular nao ha teclado e muitos mouses nao tem roda horizontal: a roda so
    // rolava a room para cima e para baixo. Entao, so sobre canvas (room, sprite, tileset) e workspace:
    //   arrastar com o direito -> a vista acompanha o mouse 1:1 (MobileWorkspace, como os dois dedos);
    //   direito segurado + roda -> zoom (Ctrl+roda);
    //   clique direito sem arrastar -> clique direito normal (menu de contexto).
    // Fora de canvas (listas, paineis) o direito segurado e entregue como antes. O "down" do direito fica
    // segurado ate decidir; a IDE diz o que ha sob o ponteiro (MobileWindowFit.ProbeCanvas).
    static bool _rmbHeld, _rmbPan, _rmbUsed, _rmbCtrl;
    static Sdl.Event _rmbDown;
    static int _rmbX, _rmbY, _rmbLastX, _rmbLastY, _rmbProbeGen;
    static long _rmbTicks;
    const int MouseProbeWaitMs = 400;   // a IDE responde num quadro; folga para quadros lentos

    static bool HandleRealMouse(Sdl.Event* e)
    {
        List<Sdl.Event>? inject = null;
        bool keep = true;
        lock (_gate)
        {
            switch (e->type)
            {
                case Sdl.MOUSEBUTTONDOWN when e->button == Sdl.BUTTON_RIGHT:
                    _rmbHeld = true; _rmbPan = _rmbUsed = _rmbCtrl = false;
                    _rmbDown = *e;
                    _windowId = e->windowID;
                    _rmbX = _rmbLastX = e->x; _rmbY = _rmbLastY = e->y;
                    _rmbTicks = Environment.TickCount64;
                    MobileWorkspace.CanvasProbeX = e->x;
                    MobileWorkspace.CanvasProbeY = e->y;
                    MobileWorkspace.CanvasProbeTolerance = int.MaxValue;
                    _rmbProbeGen = ++MobileWorkspace.CanvasProbeGen;
                    keep = false;   // segura ate saber se e clique, arraste da vista ou zoom
                    break;

                case Sdl.MOUSEMOTION when _rmbHeld:
                {
                    int px = _rmbLastX, py = _rmbLastY;
                    _rmbLastX = e->x; _rmbLastY = e->y;
                    if (_rmbPan)
                    {
                        MobileWorkspace.AddPan(px - e->x, py - e->y);
                        e->motionState = 0;   // para a IDE e so o ponteiro andando
                        break;
                    }
                    if (_rmbUsed) { e->motionState = 0; break; }
                    float dx = e->x - _rmbX, dy = e->y - _rmbY;
                    if (dx * dx + dy * dy <= _slopPx * _slopPx) { e->motionState = 0; break; }
                    bool answered = MobileWorkspace.TryGetCanvasProbe(_rmbProbeGen, out bool canvas, out _);
                    if (!answered && Environment.TickCount64 - _rmbTicks < MouseProbeWaitMs) { e->motionState = 0; break; }
                    if (answered && canvas)
                    {
                        _rmbPan = true;
                        MobileWorkspace.BeginGesture();
                        MobileWorkspace.AddPan(_rmbX - e->x, _rmbY - e->y);
                        e->motionState = 0;
                        Log.Info(Tag, $"Mouse: arrastar com o direito -> mover a vista desde ({_rmbX},{_rmbY})");
                    }
                    else
                    {
                        // Nao e canvas: o arraste com o direito segue como na IDE desktop.
                        _rmbHeld = false;
                        inject = new() { _rmbDown };
                        Log.Info(Tag, "Mouse: arrastar com o direito fora de canvas -> direito normal");
                    }
                    break;
                }

                case Sdl.MOUSEBUTTONUP when e->button == Sdl.BUTTON_RIGHT && _rmbHeld:
                    _rmbHeld = false;
                    if (_rmbCtrl) { (inject ??= new()).Add(CtrlKey(Sdl.KEYUP)); _rmbCtrl = false; }
                    if (_rmbPan || _rmbUsed) keep = false;
                    else
                    {
                        // Clique direito sem arrastar: entrega o "down" segurado e este "up".
                        (inject ??= new()).Add(_rmbDown);
                        e->x = _rmbX; e->y = _rmbY;
                    }
                    break;

                case Sdl.MOUSEWHEEL when _rmbHeld && !_rmbPan:
                    // Direito segurado + roda = zoom. O Ctrl fica preso ate soltar o direito.
                    if (!_rmbCtrl && ShortcutBridge.Ctrl == ShortcutBridge.Off)
                    {
                        (inject ??= new()).Add(CtrlKey(Sdl.KEYDOWN));
                        _rmbCtrl = true;
                    }
                    if (!_rmbUsed) Log.Info(Tag, "Mouse: direito segurado + roda -> zoom (Ctrl+roda)");
                    _rmbUsed = true;
                    break;
            }
        }
        if (inject != null) Push(inject);
        return keep;
    }

    static void HandleFinger(Sdl.Event* e)
    {
        List<Sdl.Event>? inject = null;
        bool clickDone = false;
        lock (_gate)
        {
            switch (e->type)
            {
                case Sdl.FINGERDOWN:
                    _fingers.Add(e->fingerId);
                    _fingerPos[e->fingerId] = (e->fx, e->fy);
                    if (_fingers.Count == 2 && _mode != Mode.TwoFinger)
                    {
                        var prev = _mode;
                        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                        if (TakeHeldUp(out var pendingUp)) { (inject ??= new()).Add(pendingUp); clickDone = true; }
                        if (prev == Mode.Dragging)
                            (inject ??= new()).Add(Button(Sdl.MOUSEBUTTONUP, Sdl.BUTTON_LEFT, _windowId, _lastX, _lastY));
                        if (prev == Mode.Idle)
                        {
                            // Segundo dedo sem o down sintetico do primeiro (ex.: o primeiro dedo
                            // comecou com o mouse emulado desligado): usa a posicao do dedo.
                            _windowId = e->fingerWindowID;
                        }
                        _mode = Mode.TwoFinger;
                        _twoKind = TwoKind.Undecided;
                        _twoAccX = _twoAccY = 0;
                        _twoMoved = false;
                        _pinchSteps = _panSteps = 0;
                        _twoStartTicks = Environment.TickCount64;
                        UpdateWindowSize(e->fingerWindowID);
                        var (cx, cy, dist) = TwoFingerGeometry();
                        _pinchStartDist = _pinchRefDist = dist;
                        _twoStartCx = cx; _twoStartCy = cy;
                        Log.Info(Tag, $"Dois dedos (antes: {prev}); janela {_winW}x{_winH}, distancia {dist:F0}px, centro ({cx:F0},{cy:F0})");
                    }
                    break;

                case Sdl.FINGERMOTION:
                {
                    _fingerPos[e->fingerId] = (e->fx, e->fy);
                    if (_mode != Mode.TwoFinger || _fingers.Count < 2) break;
                    // Media do movimento dos dedos: cada dedo contribui 1/N.
                    float n = _fingers.Count;
                    _twoAccX += e->fdx * _winW / n;
                    _twoAccY += e->fdy * _winH / n;
                    var (cx, cy, dist) = TwoFingerGeometry();

                    if (_twoKind == TwoKind.Undecided)
                    {
                        // Decide uma vez por gesto: abrir/fechar os dedos (pinca) ou mover juntos (rolagem).
                        float spread = Math.Abs(dist - _pinchStartDist);
                        float pan = MathF.Sqrt(_twoAccX * _twoAccX + _twoAccY * _twoAccY);
                        if (Math.Max(spread, pan) <= _slopPx) break;
                        _twoMoved = true;
                        _twoKind = spread > pan ? TwoKind.Pinch : TwoKind.Pan;
                        // Ponteiro no centro dos dedos: a IDE rola / da zoom onde o mouse esta.
                        _lastX = (int)_twoStartCx; _lastY = (int)_twoStartCy;
                        inject = new() { Motion(_windowId, _lastX, _lastY) };
                        if (PanMode)
                        {
                            if (_twoKind == TwoKind.Pan) MobileWorkspace.BeginGesture();
                        }
                        else
                        {
                            // O que ha sob o centro dos dedos decide arraste/pinca diretos ou roda.
                            _twoTarget = TwoTarget.Unknown;
                            _twoProbeTicks = Environment.TickCount64;
                            MobileWorkspace.CanvasProbeTolerance = 3;
                            MobileWorkspace.CanvasProbeX = _lastX;
                            MobileWorkspace.CanvasProbeY = _lastY;
                            _twoProbeGen = ++MobileWorkspace.CanvasProbeGen;
                        }
                        if (_twoKind == TwoKind.Pinch)
                        {
                            _pinchRefDist = _pinchStartDist;
                            _prevCx = cx; _prevCy = cy;
                        }
                        Log.Info(Tag, $"Dois dedos -> {(_twoKind == TwoKind.Pinch ? "pinca" : "arraste")}: " +
                                      $"abertura {spread:F0}px, deslocamento {pan:F0}px, ponteiro em ({_lastX},{_lastY})");
                    }

                    if (_twoKind == TwoKind.Pinch)
                    {
                        if (!PanMode)
                        {
                            if (!ResolveTwoTarget(pinch: true, ref inject)) break;   // acumula ate a resposta
                            if (_twoTarget == TwoTarget.Canvas)
                            {
                                // Pinca do workspace: zoom continuo (razao das distancias) no ponto entre os
                                // dedos, e o centro dos dedos tambem arrasta.
                                float r = dist > 1 && _pinchStartDist > 1 ? dist / _pinchStartDist : 1f;
                                if (_pinchInvert) r = 1f / r;
                                MobileWorkspace.SetPinch(r, cx, cy);
                                MobileWorkspace.AddPan(_prevCx - cx, _prevCy - cy);
                                _prevCx = cx; _prevCy = cy;
                                (inject ??= new()).Add(Motion(_windowId, _lastX, _lastY));   // acorda o observador da IDE
                                break;
                            }
                        }
                        if (dist > 1 && _pinchRefDist > 1)
                        {
                            int steps = (int)(MathF.Log(dist / _pinchRefDist) / MathF.Log(PinchStepRatio));
                            if (steps != 0)
                            {
                                _pinchRefDist *= MathF.Pow(PinchStepRatio, steps);
                                _pinchSteps += steps;
                                // Abrir os dedos = roda para cima (y>0) = aproximar.
                                int wy = _pinchInvert ? -steps : steps;
                                if (PanMode)
                                {
                                    MobileWorkspace.AddZoom(wy);
                                    (inject ??= new()).Add(Motion(_windowId, _lastX, _lastY)); // acorda o observador da IDE
                                }
                                else (inject ??= new()).Add(Wheel(_windowId, 0, wy));
                            }
                        }
                    }
                    else if (PanMode)
                    {
                        // Dois dedos juntos no modo Mover: tambem movem o workspace.
                        MobileWorkspace.AddPan(-_twoAccX, -_twoAccY);
                        _twoAccX = _twoAccY = 0;
                        (inject ??= new()).Add(Motion(_windowId, _lastX, _lastY));
                    }
                    else
                    {
                        if (!ResolveTwoTarget(pinch: false, ref inject)) break;   // acumula ate a resposta
                        if (_twoTarget == TwoTarget.Canvas)
                        {
                            // Conteudo acompanha os dedos (sem a inversao da roda: e manipulacao direta).
                            MobileWorkspace.AddPan(-_twoAccX, -_twoAccY);
                            _twoAccX = _twoAccY = 0;
                            (inject ??= new()).Add(Motion(_windowId, _lastX, _lastY));   // acorda o observador da IDE
                            break;
                        }
                        int stepsY = (int)(_twoAccY / _wheelStepPx);
                        int stepsX = (int)(_twoAccX / _wheelStepPx);
                        if (stepsY != 0 || stepsX != 0)
                        {
                            _twoAccY -= stepsY * _wheelStepPx;
                            _twoAccX -= stepsX * _wheelStepPx;
                            // Rolagem "natural" (como no celular): dedos para baixo -> conteudo desce ->
                            // roda para cima (y>0). Dedos para a esquerda -> ver o que esta a direita -> x>0.
                            int wx = -stepsX, wy = stepsY;
                            if (_scrollInvert) { wx = -wx; wy = -wy; }
                            _panSteps += Math.Abs(stepsX) + Math.Abs(stepsY);
                            (inject ??= new()).Add(Wheel(_windowId, wx, wy));
                        }
                    }
                    break;
                }

                case Sdl.FINGERUP:
                    _fingers.Remove(e->fingerId);
                    _fingerPos.Remove(e->fingerId);
                    if (_fingers.Count < 2 && _pinchCtrlDown)
                    {
                        _pinchCtrlDown = false;
                        inject = new() { CtrlKey(Sdl.KEYUP) };
                    }
                    if (_fingers.Count == 0)
                    {
                        if (_mode == Mode.TwoFinger)
                        {
                            long ms = Environment.TickCount64 - _twoStartTicks;
                            if (!_twoMoved && ms <= TwoFingerTapMaxMs)
                            {
                                Log.Info(Tag, $"Toque com dois dedos ({ms} ms) -> clique direito em ({_downX},{_downY})");
                                (inject ??= new()).AddRange(RightClick(_windowId, _downX, _downY));
                                clickDone = true;
                            }
                            else
                                Log.Info(Tag, $"Fim dos dois dedos ({ms} ms, {_twoKind}, notches de roda={_panSteps}, zoom={_pinchSteps})");
                        }
                        _mode = Mode.Idle;
                    }
                    break;
            }
        }
        if (inject != null) Push(inject);
        if (clickDone) ShortcutBridge.NotifyKeyUsed("toque com dois dedos");
    }

    /// <summary>
    /// Dois dedos fora do modo Mover (chamar com _gate): decide pela resposta da IDE se o gesto e direto
    /// (arraste: canvas/workspace; pinca: workspace) ou vira roda. false = ainda sem resposta. Pinca por
    /// roda precisa do Ctrl (Ctrl+roda = zoom); o Ctrl da barra, se preso, ja serve.
    /// </summary>
    static bool ResolveTwoTarget(bool pinch, ref List<Sdl.Event>? inject)
    {
        if (_twoTarget != TwoTarget.Unknown) return true;
        if (MobileWorkspace.TryGetCanvasProbe(_twoProbeGen, out bool canvas, out bool zoomWorkspace))
        {
            bool direct = pinch ? zoomWorkspace : canvas;
            _twoTarget = direct ? TwoTarget.Canvas : TwoTarget.Wheel;
            if (direct) MobileWorkspace.BeginGesture();
        }
        else if (Environment.TickCount64 - _twoProbeTicks > CanvasProbeWaitMs)
        {
            _twoTarget = TwoTarget.Wheel;
            Log.Info(Tag, "Dois dedos: IDE nao respondeu a sondagem; usando a roda");
        }
        else return false;
        if (pinch && _twoTarget == TwoTarget.Wheel && ShortcutBridge.Ctrl == ShortcutBridge.Off)
        {
            (inject ??= new()).Add(CtrlKey(Sdl.KEYDOWN));
            _pinchCtrlDown = true;
        }
        return true;
    }

    // Centro e distancia (px) entre os dois primeiros dedos na tela.
    static (float cx, float cy, float dist) TwoFingerGeometry()
    {
        float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        int k = 0;
        foreach (var id in _fingers)
        {
            if (!_fingerPos.TryGetValue(id, out var p)) continue;
            if (k == 0) { x0 = p.x * _winW; y0 = p.y * _winH; }
            else { x1 = p.x * _winW; y1 = p.y * _winH; }
            if (++k == 2) break;
        }
        if (k < 2) return (_lastX, _lastY, 0);
        float dx = x1 - x0, dy = y1 - y0;
        return ((x0 + x1) / 2, (y0 + y1) / 2, MathF.Sqrt(dx * dx + dy * dy));
    }

    // LCTRL sintetico para a pinca. Nao muda o estado interno do SDL (SDL_GetModState),
    // mas a IDE monta os modificadores a partir dos eventos (KeyboardManager).
    static Sdl.Event CtrlKey(uint type)
    {
        var ev = new Sdl.Event { type = type, windowID = _windowId };
        ev.keyState = type == Sdl.KEYDOWN ? Sdl.PRESSED : Sdl.RELEASED;
        ev.keyRepeat = 0;
        ev.scancode = Sdl.SCANCODE_LCTRL;
        ev.sym = Sdl.SDLK_LCTRL;
        ev.mod = type == Sdl.KEYDOWN ? Sdl.KMOD_LCTRL : (ushort)0;
        return ev;
    }

    // Toque longo: so arma (vibra). Soltar sem mover = clique direito; mover = arrastar (HandleTouchMouse).
    static void OnLongPressTimer(object? _)
    {
        int gen;
        lock (_gate)
        {
            gen = _generation;
            if (_mode != Mode.Pending || _stopperTap) return;
            _mode = Mode.LongArmed;
        }
        Log.Info(Tag, $"Toque longo armado em ({_downX},{_downY}) [gesto {gen}]");
        Haptic();
    }

    static IntPtr _actCls, _hapticId;
    static void Haptic()
    {
        try
        {
            if (_hapticId == IntPtr.Zero)
            {
                var local = global::Android.Runtime.JNIEnv.FindClass("io/gmide/GmIdeActivity");
                _actCls = global::Android.Runtime.JNIEnv.NewGlobalRef(local);
                _hapticId = global::Android.Runtime.JNIEnv.GetStaticMethodID(_actCls, "haptic", "()V");
            }
            global::Android.Runtime.JNIEnv.CallStaticVoidMethod(_actCls, _hapticId);
        }
        catch (Exception e) { Log.Warn(Tag, "Vibracao: " + e.Message); }
    }

    static void UpdateWindowSize(uint windowId)
    {
        try
        {
            var w = Sdl.SDL_GetWindowFromID(windowId != 0 ? windowId : _windowId);
            if (w != IntPtr.Zero)
            {
                Sdl.SDL_GetWindowSize(w, out var ww, out var hh);
                if (ww > 0 && hh > 0) { _winW = ww; _winH = hh; }
            }
        }
        catch (Exception ex) { Log.Warn(Tag, "SDL_GetWindowSize falhou: " + ex.Message); }
        if (_winW <= 0 || _winH <= 0)
        {
            var m = MainApplication.Instance.Resources!.DisplayMetrics!;
            _winW = m.WidthPixels; _winH = m.HeightPixels;
        }
    }

    static List<Sdl.Event> RightClick(uint win, int x, int y) => new()
    {
        Motion(win, x, y),
        Button(Sdl.MOUSEBUTTONDOWN, Sdl.BUTTON_RIGHT, win, x, y),
        Button(Sdl.MOUSEBUTTONUP, Sdl.BUTTON_RIGHT, win, x, y),
    };

    static Sdl.Event Motion(uint win, int x, int y)
    {
        var ev = new Sdl.Event { type = Sdl.MOUSEMOTION, windowID = win, which = Sdl.TOUCH_MOUSEID, x = x, y = y };
        return ev;
    }

    static Sdl.Event Button(uint type, byte button, uint win, int x, int y)
    {
        var ev = new Sdl.Event { type = type, windowID = win, which = Sdl.TOUCH_MOUSEID, x = x, y = y };
        ev.button = button;
        ev.state = type == Sdl.MOUSEBUTTONDOWN ? Sdl.PRESSED : Sdl.RELEASED;
        ev.clicks = 1;
        return ev;
    }

    static Sdl.Event Wheel(uint win, int x, int y)
    {
        var ev = new Sdl.Event { type = Sdl.MOUSEWHEEL, windowID = win, which = Sdl.TOUCH_MOUSEID };
        ev.wheelX = x; ev.wheelY = y; ev.wheelDirection = 0;
        ev.wheelPreciseX = x; ev.wheelPreciseY = y;
        ev.wheelMouseX = _lastX; ev.wheelMouseY = _lastY;
        return ev;
    }

    static int _pushFailures, _blockedText;

    static void Push(List<Sdl.Event> events)
    {
        bool prev = _injecting;
        _injecting = true;
        try
        {
            foreach (var ev in events)
            {
                var copy = ev;
                if (Sdl.SDL_PushEvent(&copy) < 0 && _pushFailures++ < 5)
                    Log.Warn(Tag, $"SDL_PushEvent(0x{copy.type:x}) falhou: {Sdl.Error}");
            }
        }
        finally { _injecting = prev; }
    }
}
