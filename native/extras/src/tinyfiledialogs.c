/*
 * libtinyfiledialogs.so - Android arm64 replacement for tinyfiledialogs
 * (OSCore.dll: [DllImport("tinyfiledialogs")] class Core.CoreOS.System.tinyfd).
 *
 * The real library shells out to zenity/kdialog/osascript, none of which exist on
 * Android. This implementation:
 *   - tinyfd_messageBox[W]: shown with SDL_ShowMessageBox (libSDL2.so is resolved at
 *     runtime with dlopen, so there is no link dependency). If SDL is unavailable or
 *     the box fails, it returns the safe answer: 1 for "ok", 0 (cancel/no) otherwise.
 *   - tinyfd_notifyPopup[W]: written to logcat, returns 1.
 *   - file/folder/color/input dialogs: return NULL (= user cancelled). File dialogs on
 *     Android are implemented in managed code (host/AndroidFileDialogs.cs, SAF).
 *   - tinyfd_beep: no-op.
 *   - globals: tinyfd_getGlobalChar/getGlobalInt/setGlobalInt with the documented names.
 * OSCore declares the W variants with the same managed signatures as the narrow
 * ones (string, marshalled as UTF-8 on Unix), so they forward to the narrow versions.
 * Signatures follow tinyfiledialogs 3.x and the managed declarations in OSCore.dll.
 */
#include <dlfcn.h>
#include <string.h>
#include <stdint.h>
#include <android/log.h>

#define API __attribute__((visibility("default")))
#define TAG "tinyfd"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)

/* --- minimal SDL2 (2.0.x ABI) message box declarations --- */
typedef struct { uint32_t flags; int buttonid; const char *text; } SDLButton;
typedef struct {
    uint32_t flags; void *window; const char *title; const char *message;
    int numbuttons; const SDLButton *buttons; const void *colorScheme;
} SDLMsgBox;
typedef int (*PFN_ShowMessageBox)(const SDLMsgBox *, int *);
#define SDL_MB_ERROR 0x10
#define SDL_MB_WARNING 0x20
#define SDL_MB_INFORMATION 0x40
#define SDL_BTN_RETURN 1
#define SDL_BTN_ESCAPE 2

static PFN_ShowMessageBox get_sdl_msgbox(void)
{
    static PFN_ShowMessageBox fn;
    static int tried;
    if (!tried) {
        tried = 1;
        void *h = dlopen("libSDL2.so", RTLD_NOW | RTLD_NOLOAD);
        if (!h) h = dlopen("libSDL2.so", RTLD_NOW);
        if (h) fn = (PFN_ShowMessageBox)dlsym(h, "SDL_ShowMessageBox");
        if (!fn) LOGI("SDL_ShowMessageBox not available; message boxes will auto-answer");
    }
    return fn;
}

static char g_response[1024] = "";
static char g_version[] = "3.18.2-android-stub";
static int g_verbose, g_silent = 1, g_allowCurses, g_forceConsole, g_assumeGraphicDisplay, g_winUtf8 = 1;

static int is_query(const char *t) { return t && strcmp(t, "tinyfd_query") == 0; }
static const char *nz(const char *s) { return s ? s : ""; }

API void tinyfd_beep(void) { }

API int tinyfd_notifyPopup(const char *title, const char *message, const char *iconType)
{
    if (is_query(title)) { strcpy(g_response, "android-logcat"); return 1; }
    LOGI("notifyPopup [%s] %s: %s", nz(iconType), nz(title), nz(message));
    return 1;
}

API int tinyfd_messageBox(const char *title, const char *message, const char *dialogType,
                          const char *iconType, int defaultButton)
{
    PFN_ShowMessageBox show = get_sdl_msgbox();
    const char *type = dialogType ? dialogType : "ok";
    int fallback = strcmp(type, "ok") == 0 ? 1 : 0;
    if (is_query(title)) { strcpy(g_response, show ? "android-sdl" : "none"); return show ? 1 : 0; }
    LOGI("messageBox [%s/%s] %s: %s", type, nz(iconType), nz(title), nz(message));
    if (!show) return fallback;

    /* buttonid == value tinyfd returns for that button */
    SDLButton b[3];
    int n = 0;
    if (strcmp(type, "okcancel") == 0) {
        b[n++] = (SDLButton){0, 1, "OK"};
        b[n++] = (SDLButton){0, 0, "Cancel"};
    } else if (strcmp(type, "yesno") == 0) {
        b[n++] = (SDLButton){0, 1, "Yes"};
        b[n++] = (SDLButton){0, 0, "No"};
    } else if (strcmp(type, "yesnocancel") == 0) {
        b[n++] = (SDLButton){0, 1, "Yes"};
        b[n++] = (SDLButton){0, 2, "No"};
        b[n++] = (SDLButton){0, 0, "Cancel"};
    } else {
        b[n++] = (SDLButton){0, 1, "OK"};
    }
    for (int i = 0; i < n; i++) {
        if (b[i].buttonid == defaultButton) b[i].flags |= SDL_BTN_RETURN;
        if (b[i].buttonid == 0 || n == 1) b[i].flags |= SDL_BTN_ESCAPE;
    }
    uint32_t flags = SDL_MB_INFORMATION;
    if (iconType && strcmp(iconType, "error") == 0) flags = SDL_MB_ERROR;
    else if (iconType && strcmp(iconType, "warning") == 0) flags = SDL_MB_WARNING;
    SDLMsgBox d = { flags, NULL, nz(title), nz(message), n, b, NULL };
    int id = -1;
    if (show(&d, &id) != 0 || id < 0) return fallback; /* error or dismissed */
    return id;
}

API char *tinyfd_inputBox(const char *title, const char *message, const char *defaultInput)
{
    (void)defaultInput;
    if (is_query(title)) { strcpy(g_response, "none"); return NULL; }
    LOGI("inputBox not supported, returning cancel: %s: %s", nz(title), nz(message));
    return NULL;
}

API char *tinyfd_saveFileDialog(const char *title, const char *defaultPathAndFile, int numPatterns,
                                const char *const *patterns, const char *filterDesc)
{
    (void)defaultPathAndFile; (void)numPatterns; (void)patterns; (void)filterDesc;
    LOGI("saveFileDialog not supported (SAF is used instead), returning cancel: %s", nz(title));
    return NULL;
}

API char *tinyfd_openFileDialog(const char *title, const char *defaultPathAndFile, int numPatterns,
                                const char *const *patterns, const char *filterDesc, int allowMultiple)
{
    (void)defaultPathAndFile; (void)numPatterns; (void)patterns; (void)filterDesc; (void)allowMultiple;
    LOGI("openFileDialog not supported (SAF is used instead), returning cancel: %s", nz(title));
    return NULL;
}

API char *tinyfd_selectFolderDialog(const char *title, const char *defaultPath)
{
    (void)defaultPath;
    LOGI("selectFolderDialog not supported (SAF is used instead), returning cancel: %s", nz(title));
    return NULL;
}

API char *tinyfd_colorChooser(const char *title, const char *defaultHexRGB,
                              const unsigned char defaultRGB[3], unsigned char resultRGB[3])
{
    (void)defaultHexRGB; (void)defaultRGB; (void)resultRGB;
    LOGI("colorChooser not supported, returning cancel: %s", nz(title));
    return NULL;
}

/* W variants: same managed signatures (string -> UTF-8 char*), forward. */
API int tinyfd_notifyPopupW(const char *t, const char *m, const char *i) { return tinyfd_notifyPopup(t, m, i); }
API int tinyfd_messageBoxW(const char *t, const char *m, const char *d, const char *i, int b) { return tinyfd_messageBox(t, m, d, i, b); }
API char *tinyfd_inputBoxW(const char *t, const char *m, const char *d) { return tinyfd_inputBox(t, m, d); }
API char *tinyfd_saveFileDialogW(const char *t, const char *p, int n, const char *const *f, const char *d) { return tinyfd_saveFileDialog(t, p, n, f, d); }
API char *tinyfd_openFileDialogW(const char *t, const char *p, int n, const char *const *f, const char *d, int a) { return tinyfd_openFileDialog(t, p, n, f, d, a); }
API char *tinyfd_selectFolderDialogW(const char *t, const char *p) { return tinyfd_selectFolderDialog(t, p); }
API char *tinyfd_colorChooserW(const char *t, const char *h, const unsigned char *d, unsigned char *r) { return tinyfd_colorChooser(t, h, d, r); }

API char *tinyfd_getGlobalChar(const char *name)
{
    if (!name) return NULL;
    if (!strcmp(name, "tinyfd_version")) return g_version;
    if (!strcmp(name, "tinyfd_response")) return g_response;
    if (!strcmp(name, "tinyfd_needs")) return g_response;
    return NULL;
}

static int *int_var(const char *name)
{
    if (!name) return NULL;
    if (!strcmp(name, "tinyfd_verbose")) return &g_verbose;
    if (!strcmp(name, "tinyfd_silent")) return &g_silent;
    if (!strcmp(name, "tinyfd_allowCursesDialogs")) return &g_allowCurses;
    if (!strcmp(name, "tinyfd_forceConsole")) return &g_forceConsole;
    if (!strcmp(name, "tinyfd_assumeGraphicDisplay")) return &g_assumeGraphicDisplay;
    if (!strcmp(name, "tinyfd_winUtf8")) return &g_winUtf8;
    return NULL;
}

API int tinyfd_getGlobalInt(const char *name) { int *p = int_var(name); return p ? *p : -1; }
API int tinyfd_setGlobalInt(const char *name, int value) { int *p = int_var(name); if (!p) return -1; *p = value; return 0; }
