using Android.Util;
using YoYoStudio.GUI;

namespace GmIde;

// Um ponto unico, na thread da IDE, para o que a camada mobile faz a cada volta do loop:
// WindowManager.OnProcess roda em toda iteracao de IDE.Run (WindowManager.Process), ao contrario de
// OnPostRender, que so roda quando ha quadro a desenhar. Ordem:
//  1. TouchInput.OnIdeFrame: conta quadros e libera o "solta" de toque segurado (paleta de tiles etc.);
//  2. sondagem do que esta sob o dedo (rolavel? qual ScrollBar?) e sob os dois dedos (canvas?);
//  3. rolagem direta / inercia e arraste 1:1 de canvas/workspace;
//  4. politica de janelas (editores ocupando a area, dialogos dentro da tela);
//  5. casca mobile-first (MobileShell: moldura nativa, docks como gavetas, menu ⋮).
static class MobileFrame
{
    const string Tag = "GmIde.Frame";
    static ProcessEvent? _handler;        // ref forte: o evento guarda referencia fraca
    static object? _registration;

    public static void Install()
    {
        if (_handler != null) return;
        try
        {
            _handler = OnProcess;
            _registration = WindowManager.OnProcess.DoEveryTime(_handler);
            Log.Info(Tag, "WindowManager.OnProcess assinado (toque, rolagem, janelas)");
        }
        catch (Exception e)
        {
            _handler = null;
            Log.Error(Tag, "Falha ao assinar WindowManager.OnProcess: " + e);
        }
    }

    static void OnProcess(TimeSpan delta)
    {
        try { TouchInput.OnIdeFrame(); }
        catch (Exception e) { Log.Warn(Tag, "Toque: " + e.Message); }
        MobileWindowFit.ProbeScroll();
        MobileWindowFit.ProbeCanvas();
        MobileScrollTarget.Apply();
        MobileWindowFit.ApplyPending();   // arraste de canvas/workspace (tambem chamado no mouse motion)
        MobileWindowPolicy.OnFrame();
        try { KeyboardMode.OnProcessFrame(); } catch (Exception e) { Log.Warn(Tag, "Teclado: " + e.Message); }
        MobileShell.OnFrame();
    }
}
