using System.Runtime.InteropServices;

namespace GmIde;

// P/Invoke direto na libSDL2.so (a mesma instancia que o SDLActivity e a IDE usam).
// Nao depende das assinaturas do SDL2-CS.dll.
static unsafe class Sdl
{
    const string Lib = "SDL2";

    public const int HintDefault = 0, HintNormal = 1, HintOverride = 2;

    // Tipos de evento (SDL_events.h, SDL 2.28)
    public const uint WINDOWEVENT = 0x200;
    public const uint KEYDOWN = 0x300, KEYUP = 0x301, TEXTEDITING = 0x302, TEXTINPUT = 0x303;
    public const uint MOUSEMOTION = 0x400, MOUSEBUTTONDOWN = 0x401, MOUSEBUTTONUP = 0x402, MOUSEWHEEL = 0x403;
    public const uint FINGERDOWN = 0x700, FINGERUP = 0x701, FINGERMOTION = 0x702;
    public const uint MULTIGESTURE = 0x802;

    public const uint TOUCH_MOUSEID = 0xFFFFFFFF; // SDL_TOUCH_MOUSEID
    public const byte BUTTON_LEFT = 1, BUTTON_RIGHT = 3;
    public const byte PRESSED = 1, RELEASED = 0;

    public const int SCANCODE_ESCAPE = 41, SCANCODE_AC_BACK = 270;
    public const int SDLK_ESCAPE = 27, SDLK_AC_BACK = 0x4000010E;
    // HELP: canal de comandos da barra de atalhos (io.gmide.ShortcutBar manda AKEYCODE_HELP).
    public const int SCANCODE_HELP = 117;
    // Modificadores: LCTRL..RGUI = 224..231
    public const int SCANCODE_LCTRL = 224, SCANCODE_LSHIFT = 225, SCANCODE_LALT = 226, SCANCODE_RGUI = 231;
    public const int SDLK_LCTRL = 0x400000E0;
    public const ushort KMOD_LCTRL = 0x0040;
    public static bool IsModifierScancode(int sc) => sc >= SCANCODE_LCTRL && sc <= SCANCODE_RGUI;

    // SDL_GLattr
    public const int GL_CONTEXT_MAJOR_VERSION = 17, GL_CONTEXT_MINOR_VERSION = 18, GL_CONTEXT_PROFILE_MASK = 21;
    public const int GL_CONTEXT_PROFILE_ES = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int x, y, w, h; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Version { public byte major, minor, patch; }

    // SDL_Event (56 bytes). Offsets conferidos com SDL_events.h do SDL 2.28.
    [StructLayout(LayoutKind.Explicit, Size = 56)]
    public struct Event
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(4)] public uint timestamp;
        [FieldOffset(8)] public uint windowID;      // mouse/teclado/janela

        // SDL_MouseButtonEvent / SDL_MouseMotionEvent / SDL_MouseWheelEvent
        [FieldOffset(12)] public uint which;
        [FieldOffset(16)] public byte button;        // botao
        [FieldOffset(17)] public byte state;
        [FieldOffset(18)] public byte clicks;
        [FieldOffset(16)] public uint motionState;   // motion
        [FieldOffset(20)] public int x;              // botao/motion
        [FieldOffset(24)] public int y;
        [FieldOffset(28)] public int xrel;
        [FieldOffset(32)] public int yrel;
        [FieldOffset(16)] public int wheelX;         // wheel
        [FieldOffset(20)] public int wheelY;
        [FieldOffset(24)] public uint wheelDirection;
        [FieldOffset(28)] public float wheelPreciseX;
        [FieldOffset(32)] public float wheelPreciseY;
        [FieldOffset(36)] public int wheelMouseX;
        [FieldOffset(40)] public int wheelMouseY;

        // SDL_KeyboardEvent
        [FieldOffset(12)] public byte keyState;
        [FieldOffset(13)] public byte keyRepeat;
        [FieldOffset(16)] public int scancode;
        [FieldOffset(20)] public int sym;
        [FieldOffset(24)] public ushort mod;

        // SDL_TouchFingerEvent
        [FieldOffset(8)] public long touchId;
        [FieldOffset(16)] public long fingerId;
        [FieldOffset(24)] public float fx;
        [FieldOffset(28)] public float fy;
        [FieldOffset(32)] public float fdx;
        [FieldOffset(36)] public float fdy;
        [FieldOffset(40)] public float pressure;
        [FieldOffset(44)] public uint fingerWindowID;

        // SDL_WindowEvent
        [FieldOffset(12)] public byte windowEvent;
        [FieldOffset(16)] public int data1;
        [FieldOffset(20)] public int data2;
    }

    [DllImport(Lib)] public static extern int SDL_SetHintWithPriority(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int priority);
    [DllImport(Lib)] static extern IntPtr SDL_GetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    public static string? GetHint(string name) => Marshal.PtrToStringUTF8(SDL_GetHint(name));

    [DllImport(Lib)] static extern IntPtr SDL_GetPlatform();
    public static string Platform => Marshal.PtrToStringUTF8(SDL_GetPlatform()) ?? "?";
    [DllImport(Lib)] public static extern void SDL_GetVersion(out Version v);
    [DllImport(Lib)] static extern IntPtr SDL_GetError();
    public static string Error => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";
    [DllImport(Lib)] public static extern uint SDL_WasInit(uint flags);

    [DllImport(Lib)] public static extern int SDL_GL_SetAttribute(int attr, int value);
    [DllImport(Lib)] public static extern int SDL_GL_GetAttribute(int attr, out int value);
    [DllImport(Lib)] public static extern IntPtr SDL_GL_GetCurrentContext();

    [DllImport(Lib)] public static extern int SDL_GetNumVideoDisplays();
    [DllImport(Lib)] public static extern int SDL_GetDisplayDPI(int displayIndex, out float ddpi, out float hdpi, out float vdpi);

    [DllImport(Lib)] public static extern void SDL_StartTextInput();
    [DllImport(Lib)] public static extern void SDL_StopTextInput();
    [DllImport(Lib)] public static extern int SDL_IsTextInputActive();
    [DllImport(Lib)] public static extern void SDL_SetTextInputRect(ref Rect rect);
    [DllImport(Lib)] public static extern int SDL_HasScreenKeyboardSupport();
    [DllImport(Lib)] public static extern int SDL_IsScreenKeyboardShown(IntPtr window);
    [DllImport(Lib)] public static extern IntPtr SDL_GetKeyboardFocus();

    [DllImport(Lib)] public static extern IntPtr SDL_GetWindowFromID(uint id);
    [DllImport(Lib)] public static extern void SDL_GetWindowSize(IntPtr window, out int w, out int h);
    [DllImport(Lib)] public static extern int SDL_GetNumTouchDevices();

    [DllImport(Lib)] public static extern void SDL_SetEventFilter(delegate* unmanaged[Cdecl]<IntPtr, Event*, int> filter, IntPtr userdata);
    [DllImport(Lib)] public static extern int SDL_GetEventFilter(out IntPtr filter, out IntPtr userdata);
    [DllImport(Lib)] public static extern int SDL_PushEvent(Event* e);

    // setenv nativo: Environment.SetEnvironmentVariable nao chega ao getenv() do C no Linux/Android.
    [DllImport("libc", EntryPoint = "setenv")]
    static extern int libc_setenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);
    public static int SetEnvNative(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        return libc_setenv(name, value, 1);
    }
}
