using Android.Runtime;
using Android.Util;

namespace GmIde;

// Lado C# da barra de atalhos (host/Java/io/gmide/ShortcutBar.java).
//
// Java -> C#: a barra enfileira um comando e manda a tecla HELP; o filtro de
// eventos (TouchInput) chama DrainCommands() ao ver SDL_SCANCODE_HELP.
// Comandos: "mods:CSA" (estado de Ctrl/Shift/Alt: 0 solto, 1 proxima tecla,
// 2 travado), "rclick:N" (proximo toque = clique direito), "kbd" (teclado
// virtual), "scale:N" (escala da UI, 0 = automatica).
// C# -> Java: onKeyUsed / onModifierKeyUp / onRightClickUsed (JNI, estaticos).
//
// As teclas Ctrl/Shift/Alt da barra sao pressionadas de verdade no SDL, entao a
// IDE ja as ve em cliques e na roda (KeyboardManager). Aqui so guardamos um
// espelho para decidir quando avisar a barra e para bloquear SDL_TEXTINPUT com
// Ctrl/Alt (senao Ctrl+C no teclado virtual tambem digitaria "c").
static class ShortcutBridge
{
    const string Tag = "GmIde.Bar";
    public const int Off = 0, OneShot = 1, Locked = 2;

    static volatile int _ctrl, _shift, _alt, _rightClick;
    public static int Ctrl => _ctrl;
    public static int Shift => _shift;
    public static int Alt => _alt;
    public static int RightClickMode => _rightClick;
    public static bool AnyOneShot => _ctrl == OneShot || _shift == OneShot || _alt == OneShot;
    public static bool AnyHeld => _ctrl != Off || _shift != Off || _alt != Off;
    public static bool BlocksTextInput => _ctrl != Off || _alt != Off;

    static IntPtr _cls, _take, _keyUsed, _modUp, _rcUsed, _post;
    static bool _failed;
    static readonly object _jniGate = new();

    static bool EnsureJni()
    {
        if (_cls != IntPtr.Zero) return true;
        if (_failed) return false;
        lock (_jniGate)
        {
            if (_cls != IntPtr.Zero) return true;
            try
            {
                var local = JNIEnv.FindClass("io/gmide/ShortcutBar");
                var cls = JNIEnv.NewGlobalRef(local);
                _take = JNIEnv.GetStaticMethodID(cls, "takeCommand", "()Ljava/lang/String;");
                _keyUsed = JNIEnv.GetStaticMethodID(cls, "onKeyUsed", "(Ljava/lang/String;)V");
                _modUp = JNIEnv.GetStaticMethodID(cls, "onModifierKeyUp", "(I)V");
                _rcUsed = JNIEnv.GetStaticMethodID(cls, "onRightClickUsed", "()V");
                _post = JNIEnv.GetStaticMethodID(cls, "postCommand", "(Ljava/lang/String;)V");
                _cls = cls;
                Log.Info(Tag, "Ponte JNI com io.gmide.ShortcutBar pronta");
                return true;
            }
            catch (Exception e)
            {
                _failed = true;
                Log.Error(Tag, "io.gmide.ShortcutBar indisponivel (barra sem integracao com o toque): " + e.Message);
                return false;
            }
        }
    }

    // Chamado pelo filtro ao ver HELP (thread do Java/UI, dentro do onNativeKeyDown da barra).
    public static void DrainCommands()
    {
        if (!EnsureJni()) return;
        for (int i = 0; i < 64; i++)
        {
            string? cmd;
            try
            {
                IntPtr r = JNIEnv.CallStaticObjectMethod(_cls, _take);
                cmd = JNIEnv.GetString(r, JniHandleOwnership.TransferLocalRef);
            }
            catch (Exception e) { Log.Error(Tag, "takeCommand: " + e.Message); return; }
            if (cmd == null) return;
            try { Apply(cmd); }
            catch (Exception e) { Log.Error(Tag, $"Comando '{cmd}': {e}"); }
        }
    }

    static void Apply(string cmd)
    {
        int colon = cmd.IndexOf(':');
        string name = colon < 0 ? cmd : cmd[..colon];
        string arg = colon < 0 ? "" : cmd[(colon + 1)..];
        switch (name)
        {
            case "mods" when arg.Length >= 3:
                _ctrl = arg[0] - '0'; _shift = arg[1] - '0'; _alt = arg[2] - '0';
                Log.Info(Tag, $"Modificadores da barra: Ctrl={Name(_ctrl)} Shift={Name(_shift)} Alt={Name(_alt)}");
                break;
            case "rclick":
                int.TryParse(arg, out int rc);
                _rightClick = rc;
                Log.Info(Tag, "Proximo toque = clique direito: " + Name(rc));
                break;
            case "kbd":
                ToggleKeyboard();
                break;
            case "pan":
                TouchInput.PanMode = arg == "1";
                Log.Info(Tag, "Modo Mover (arrastar/pinca movem o workspace): " + (TouchInput.PanMode ? "ligado" : "desligado"));
                break;
            case "scale":
                int.TryParse(arg, out int pct);
                IdeSetup.RequestScale(pct);
                break;
            case "apk":
            case "apk-go":
                ApkExport.Request(name, arg); // roda na thread da IDE (IdeSetup.OnInputObserved)
                break;
            case "grip":
                MobileWindowGrip.Enqueue(arg);   // alca de redimensionar janela flutuante (WindowGrip.java)
                break;
            case "shell":
                MobileShell.Enqueue(arg);   // casca mobile (MobileShell.java); aplicado na thread da IDE
                break;
            case "preview-stop":
            case "preview-show":
            case "preview-f5":
                LocalGameRun.RequestControl(name);
                break;
            default:
                Log.Warn(Tag, "Comando desconhecido da barra: " + cmd);
                break;
        }
    }

    static string Name(int s) => s switch { Off => "solto", OneShot => "proximo", Locked => "travado", _ => s.ToString() };

    static void ToggleKeyboard()
    {
        IntPtr win = Sdl.SDL_GetKeyboardFocus();
        bool shown = win != IntPtr.Zero && Sdl.SDL_IsScreenKeyboardShown(win) != 0;
        bool active = Sdl.SDL_IsTextInputActive() != 0;
        if (shown)
        {
            Sdl.SDL_StopTextInput();
            Log.Info(Tag, $"Teclado virtual: ocultar (estava visivel, textinput={active})");
        }
        else
        {
            Sdl.SDL_StartTextInput();
            Log.Info(Tag, $"Teclado virtual: mostrar (janela=0x{win:x}, textinput antes={active}, depois={Sdl.SDL_IsTextInputActive() != 0})");
        }
    }

    // ---- avisos para a barra (qualquer thread; o Java posta na thread de UI)

    public static void NotifyKeyUsed(string what)
    {
        if (!AnyOneShot || !EnsureJni()) return;
        try
        {
            IntPtr s = JNIEnv.NewString(what);
            try { JNIEnv.CallStaticVoidMethod(_cls, _keyUsed, new JValue(s)); }
            finally { JNIEnv.DeleteLocalRef(s); }
        }
        catch (Exception e) { Log.Warn(Tag, "onKeyUsed: " + e.Message); }
    }

    public static void NotifyModifierUp(int scancode)
    {
        if (!AnyHeld || !EnsureJni()) return;
        try { JNIEnv.CallStaticVoidMethod(_cls, _modUp, new JValue(scancode)); }
        catch (Exception e) { Log.Warn(Tag, "onModifierKeyUp: " + e.Message); }
    }

    /// <summary>C# -> fila da barra -> HELP -> de volta ao C# na thread da IDE (qualquer thread).</summary>
    public static void Post(string cmd)
    {
        if (!EnsureJni()) return;
        try
        {
            IntPtr s = JNIEnv.NewString(cmd);
            try { JNIEnv.CallStaticVoidMethod(_cls, _post, new JValue(s)); }
            finally { JNIEnv.DeleteLocalRef(s); }
        }
        catch (Exception e) { Log.Warn(Tag, "postCommand: " + e.Message); }
    }

    public static void NotifyRightClickUsed()
    {
        _rightClick = Off; // ja; a barra confirma com "rclick:0"
        if (!EnsureJni()) return;
        try { JNIEnv.CallStaticVoidMethod(_cls, _rcUsed); }
        catch (Exception e) { Log.Warn(Tag, "onRightClickUsed: " + e.Message); }
    }
}
