using System.Runtime.CompilerServices;
using Android.Util;
using YoYoStudio.GUI;
using YoYoStudio.GUI.Gadgets;

namespace GmIde;

// Rolagem com um dedo no estilo de celular: o conteudo acompanha o dedo 1:1 e, ao soltar com
// velocidade, continua com inercia (fling) ate parar ou bater no fim.
//
// Antes o arraste virava eventos de roda (um "notch" a cada 24 dp), e cada notch anda
// ScrollBar.WheelIncrement unidades, que varia por gadget: no Inspetor um gesto inteiro rolava
// poucos pixels. Agora o alvo e o proprio ScrollBar do gadget sob o dedo (todos os paineis rolaveis
// da IDE usam ScrollBar: ScrollPanel, ListBox, TreeView, DataTree, TableGadget, Window e o editor de
// codigo, TextEditorPanelVerticalScrollBar). Mudar ScrollBar.Slider.SliderValue e o mesmo caminho do
// arraste do "polegar" da barra: o slider dispara OnSliderValueChanged -> parent.Scroll(direcao, valor).
//
// Threads: TouchInput (thread de UI do Java) so acumula deslocamento/velocidade sob _lock. A IDE
// (thread do SDL, KeyboardMode.OnFrame -> Apply) resolve o alvo e aplica.
public static class MobileScroll
{
    const string Tag = "GmIde.Scroll";
    static readonly object _lock = new();
    static float _pendingPx;        // deslocamento do dedo ainda nao aplicado (eixo do gesto)
    static float _velocity;         // px/s do fling (0 = parado)
    static int _gen;                // gesto atual (ProbeGen do TouchInput)
    static bool _vertical;          // eixo do gesto

    /// <summary>Novo toque: para qualquer inercia em andamento (como no Android).</summary>
    public static void Stop()
    {
        lock (_lock) { _pendingPx = 0; _velocity = 0; }
    }

    public static bool Flinging { get { lock (_lock) return _velocity != 0; } }

    public static void Begin(int gen, bool vertical, float initialPx)
    {
        lock (_lock) { _gen = gen; _vertical = vertical; _pendingPx = initialPx; _velocity = 0; }
    }

    /// <summary>Dedo andou <paramref name="px"/> no eixo do gesto (positivo = para baixo/direita).</summary>
    public static void Drag(float px)
    {
        lock (_lock) _pendingPx += px;
    }

    /// <summary>Dedo soltou com velocidade (px/s, mesmo sinal de Drag).</summary>
    public static void Release(float velocityPx)
    {
        lock (_lock) _velocity = Math.Abs(velocityPx) >= MinFlingPx ? velocityPx : 0;
    }

    internal static bool Take(out float px, out float velocity, out int gen, out bool vertical)
    {
        lock (_lock)
        {
            px = _pendingPx; _pendingPx = 0;
            velocity = _velocity; gen = _gen; vertical = _vertical;
        }
        return px != 0 || velocity != 0;
    }

    internal static void SetVelocity(float v)
    {
        lock (_lock) _velocity = v;
    }

    internal const float MinFlingPx = 400f;      // abaixo disso nao ha inercia
    internal const float StopFlingPx = 60f;      // velocidade em que a inercia termina
    internal const float FlingDecayPerSec = 3.2f; // v *= e^(-k*dt): ~1 s de inercia num gesto rapido
}

static class MobileScrollTarget
{
    const string Tag = "GmIde.Scroll";
    static WeakReference<ScrollBar>? _vBar, _hBar;
    static int _barGen = -1;
    static float _remainder;          // fracao de unidade que o slider arredondou
    static long _lastTicks;

    /// <summary>Thread da IDE: guarda o ScrollBar escolhido pela sondagem deste gesto.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void SetTarget(int gen, ScrollBar? vertical, ScrollBar? horizontal)
    {
        _vBar = vertical == null ? null : new WeakReference<ScrollBar>(vertical);
        _hBar = horizontal == null ? null : new WeakReference<ScrollBar>(horizontal);
        _barGen = gen;
        _remainder = 0;
    }

    /// <summary>
    /// Procura, do gadget sob o dedo para cima, o primeiro ScrollBar ativo e com algo a rolar em cada
    /// eixo. Para em gadgets que tratam o arraste por conta propria (workspace, canvas dos editores).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Find(GUIBase? start, out ScrollBar? vertical, out ScrollBar? horizontal)
    {
        vertical = horizontal = null;
        for (GUIBase? cur = start; cur != null; cur = cur.parent)
        {
            if (cur is YoYoStudio.GUI.Gadgets.Containers.WorkspaceArea) break;
            BarsOf(cur, ref vertical, ref horizontal);
            if (vertical != null || horizontal != null) return;
        }
    }

    /// <summary>
    /// Barras de rolagem do proprio gadget: ScrollPanel.ScrollBars, ou ScrollBar entre os filhos. As
    /// barras ficam em Gadgets (moldura, nao rola com o conteudo), nao em ChildGadgets.
    /// </summary>
    internal static void BarsOf(GUIBase g, ref ScrollBar? vertical, ref ScrollBar? horizontal)
    {
        if (g is ScrollPanel sp && sp.ScrollBars != null)
            foreach (var sb in sp.ScrollBars) Consider(sb, ref vertical, ref horizontal);
        if (g.Gadgets != null) foreach (var k in g.Gadgets) Consider(k as ScrollBar, ref vertical, ref horizontal);
        if (g.ChildGadgets != null) foreach (var k in g.ChildGadgets) Consider(k as ScrollBar, ref vertical, ref horizontal);
    }

    static void Consider(ScrollBar? sb, ref ScrollBar? vertical, ref ScrollBar? horizontal)
    {
        if (sb == null || sb.Destroyed || sb.RequestDisabled || sb.Slider == null) return;
        if (sb.Slider.MaxValue - sb.Slider.MinValue <= 0.5f) return;   // nada a rolar
        if (sb.Slider.SliderDirection == eGUIDirection.Vertical) vertical ??= sb;
        else horizontal ??= sb;
    }

    /// <summary>Thread da IDE, a cada quadro.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Apply()
    {
        long now = Environment.TickCount64;
        float dt = _lastTicks == 0 ? 0.016f : Math.Clamp((now - _lastTicks) / 1000f, 0.001f, 0.1f);
        _lastTicks = now;
        if (!MobileScroll.Take(out float px, out float v, out int gen, out bool vertical)) return;
        try
        {
            ScrollBar? sb = null;
            if (gen == _barGen) (vertical ? _vBar : _hBar)?.TryGetTarget(out sb);
            if (sb == null || sb.Destroyed || sb.parent == null || sb.Slider == null)
            {
                MobileScroll.SetVelocity(0);
                return;
            }
            if (v != 0)
            {
                px += v * dt;
                v *= MathF.Exp(-MobileScroll.FlingDecayPerSec * dt);
                MobileScroll.SetVelocity(Math.Abs(v) < MobileScroll.StopFlingPx ? 0 : v);
            }
            var owner = sb.parent;
            bool vert = sb.Slider.SliderDirection == eGUIDirection.Vertical;
            float page = vert ? owner.VerticalPageSize : owner.HorizontalPageSize;
            var clip = owner.AbsoluteClipRect;
            float visPx = clip == null ? 0 : (vert ? clip.h : clip.w);
            float unitsPerPx = page > 0 && visPx > 0 ? page / visPx : 1f;

            // Dedo para baixo = conteudo desce = valor de rolagem diminui.
            float want = sb.Slider.SliderValue - px * unitsPerPx + _remainder;
            float clamped = Math.Clamp(want, sb.Slider.MinValue, sb.Slider.MaxValue);
            sb.Slider.SliderValue = clamped;
            _remainder = clamped - sb.Slider.SliderValue;   // o que o slider arredondou
            if (clamped != want)
            {
                _remainder = 0;
                MobileScroll.SetVelocity(0);                // bateu no fim: a inercia acaba
            }
            sb.parent.RequireRender = true;
        }
        catch (Exception e)
        {
            MobileScroll.SetVelocity(0);
            Log.Warn(Tag, "Rolagem: " + e.Message);
        }
    }
}
