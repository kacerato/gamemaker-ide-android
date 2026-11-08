#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <shlobj.h>
#include <string>
#include <algorithm>
#include "WebView2.h"

// A precompiled desktop player. Android only supplies game.ini, icon and web files.
// No server, browser window, editor process or SDK is required alongside the game.
static HWND windowHandle;
static ICoreWebView2Controller* controller;
static ICoreWebView2* web;
static std::wstring directory, title;
static HMODULE loader;
static bool fullscreen;
static WINDOWPLACEMENT placement = { sizeof(WINDOWPLACEMENT) };
static DWORD windowStyle;

static IID Guid(const wchar_t* text) {
    IID id{}; std::wstring value = std::wstring(L"{") + text + L"}";
    CLSIDFromString(value.c_str(), &id); return id;
}
static std::wstring Option(const wchar_t* key, const wchar_t* fallback) {
    wchar_t value[1024];
    GetPrivateProfileStringW(L"game", key, fallback, value, 1024, (directory + L"\\game.ini").c_str());
    return value;
}
static void Fail(const wchar_t* message) {
    MessageBoxW(windowHandle, message, title.c_str(), MB_ICONERROR | MB_OK);
    if (IsWindow(windowHandle)) PostMessageW(windowHandle, WM_CLOSE, 0, 0);
}
static void ToggleFullscreen() {
    if (!fullscreen) {
        MONITORINFO mi = { sizeof(mi) };
        GetWindowPlacement(windowHandle, &placement);
        GetMonitorInfoW(MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST), &mi);
        windowStyle = GetWindowLongW(windowHandle, GWL_STYLE);
        SetWindowLongW(windowHandle, GWL_STYLE, windowStyle & ~WS_OVERLAPPEDWINDOW);
        SetWindowPos(windowHandle, HWND_TOP, mi.rcMonitor.left, mi.rcMonitor.top,
            mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top,
            SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
    } else {
        SetWindowLongW(windowHandle, GWL_STYLE, windowStyle);
        SetWindowPlacement(windowHandle, &placement);
        SetWindowPos(windowHandle, nullptr, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOOWNERZORDER | SWP_NOZORDER);
    }
    fullscreen = !fullscreen;
}
template<class T> class Handler : public T {
    LONG references = 1;
    IID iid;
public:
    Handler(const wchar_t* id) : iid(Guid(id)) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (IsEqualIID(id, IID_IUnknown) || IsEqualIID(id, iid)) {
            *result = static_cast<T*>(this); AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG n = InterlockedDecrement(&references); if (!n) delete this; return n;
    }
    virtual ~Handler() = default;
};
class Keys final : public Handler<ICoreWebView2AcceleratorKeyPressedEventHandler> {
public:
    Keys() : Handler(L"b29c7e28-fa79-41a8-8e44-65811c76dcb2") {}
    HRESULT STDMETHODCALLTYPE Invoke(ICoreWebView2Controller*, ICoreWebView2AcceleratorKeyPressedEventArgs* args) override {
        UINT key; COREWEBVIEW2_KEY_EVENT_KIND kind;
        args->get_VirtualKey(&key); args->get_KeyEventKind(&kind);
        if (kind == COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN && key == VK_F11) {
            args->put_Handled(TRUE); PostMessageW(windowHandle, WM_APP + 1, 0, 0);
        }
        return S_OK;
    }
};
class FullscreenChanged final : public Handler<ICoreWebView2ContainsFullScreenElementChangedEventHandler> {
public:
    FullscreenChanged() : Handler(L"e45d98b1-afef-45be-8baf-6c7728867f73") {}
    HRESULT STDMETHODCALLTYPE Invoke(ICoreWebView2* view, IUnknown*) override {
        BOOL requested = FALSE; view->get_ContainsFullScreenElement(&requested);
        if ((requested != FALSE) != fullscreen) PostMessageW(windowHandle, WM_APP + 1, 0, 0);
        return S_OK;
    }
};
class Messages final : public Handler<ICoreWebView2WebMessageReceivedEventHandler> {
public:
    Messages() : Handler(L"57213f19-00e6-49fa-8e07-898ea01ecbd2") {}
    HRESULT STDMETHODCALLTYPE Invoke(ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) override {
        LPWSTR source = nullptr, message = nullptr;
        args->get_Source(&source);
        if (source && std::wstring(source).find(L"https://game.local/") == 0 &&
            SUCCEEDED(args->TryGetWebMessageAsString(&message)) && message && std::wstring(message) == L"gmide-close")
            PostMessageW(windowHandle, WM_CLOSE, 0, 0);
        CoTaskMemFree(source); CoTaskMemFree(message); return S_OK;
    }
};
class ControllerCreated final : public Handler<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler> {
public:
    ControllerCreated() : Handler(L"6c4819f3-c9b7-4260-8127-c9f5bde7f68c") {}
    HRESULT STDMETHODCALLTYPE Invoke(HRESULT status, ICoreWebView2Controller* value) override {
        if (!IsWindow(windowHandle)) return S_OK;
        if (FAILED(status) || !value) { Fail(L"Nao foi possivel iniciar o jogador WebView2."); return S_OK; }
        controller = value; controller->AddRef(); controller->get_CoreWebView2(&web);
        ICoreWebView2_3* local = nullptr;
        HRESULT result = web->QueryInterface(Guid(L"A0D6DF20-3B92-416D-AA0C-437A9C727857"), reinterpret_cast<void**>(&local));
        if (FAILED(result) || !local) { Fail(L"Atualize o Microsoft Edge WebView2 Runtime."); return S_OK; }
        result = local->SetVirtualHostNameToFolderMapping(L"game.local", (directory + L"\\web").c_str(), COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS);
        local->Release();
        if (FAILED(result)) { Fail(L"Nao foi possivel carregar os arquivos do jogo."); return S_OK; }
        ICoreWebView2Settings* settings = nullptr;
        if (SUCCEEDED(web->get_Settings(&settings))) {
            settings->put_AreDefaultContextMenusEnabled(FALSE);
            settings->put_AreDevToolsEnabled(FALSE);
            settings->put_IsStatusBarEnabled(FALSE);
            settings->put_IsZoomControlEnabled(FALSE);
            settings->Release();
        }
        EventRegistrationToken token;
        auto keys = new Keys(); controller->add_AcceleratorKeyPressed(keys, &token); keys->Release();
        auto fs = new FullscreenChanged(); web->add_ContainsFullScreenElementChanged(fs, &token); fs->Release();
        auto messages = new Messages(); web->add_WebMessageReceived(messages, &token); messages->Release();
        RECT bounds; GetClientRect(windowHandle, &bounds); controller->put_Bounds(bounds);
        controller->put_IsVisible(TRUE);
        web->Navigate(L"https://game.local/index.html");
        controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
        return S_OK;
    }
};
class EnvironmentCreated final : public Handler<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler> {
public:
    EnvironmentCreated() : Handler(L"4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d") {}
    HRESULT STDMETHODCALLTYPE Invoke(HRESULT status, ICoreWebView2Environment* environment) override {
        if (!IsWindow(windowHandle)) return S_OK;
        if (FAILED(status) || !environment) { Fail(L"Nao foi possivel iniciar o ambiente WebView2. Atualize o runtime da Microsoft."); return S_OK; }
        auto callback = new ControllerCreated();
        HRESULT result = environment->CreateCoreWebView2Controller(windowHandle, callback);
        callback->Release();
        if (FAILED(result)) Fail(L"Nao foi possivel criar a janela do jogo.");
        return S_OK;
    }
};
static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    switch (message) {
    case WM_SIZE:
        if (controller) {
            controller->put_IsVisible(wparam != SIZE_MINIMIZED);
            if (wparam != SIZE_MINIMIZED) { RECT bounds; GetClientRect(hwnd, &bounds); controller->put_Bounds(bounds); }
        }
        return 0;
    case WM_SETFOCUS:
        if (controller) controller->MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC);
        return 0;
    case WM_APP + 1: ToggleFullscreen(); return 0;
    case WM_DESTROY:
        if (controller) { controller->Close(); controller->Release(); controller = nullptr; }
        if (web) { web->Release(); web = nullptr; }
        PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    SetProcessDPIAware();
    wchar_t executable[32768]; GetModuleFileNameW(nullptr, executable, 32768);
    directory = executable; directory.resize(directory.find_last_of(L"\\/"));
    title = Option(L"title", L"Game");
    std::wstring id = Option(L"id", L"game");
    if (id.empty() || id.find_first_not_of(L"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-") != std::wstring::npos || id == L"." || id == L"..") {
        Fail(L"Identificador de armazenamento invalido em game.ini."); return 1;
    }
    if (GetFileAttributesW((directory + L"\\web\\index.html").c_str()) == INVALID_FILE_ATTRIBUTES) {
        Fail(L"Extraia todo o ZIP antes de abrir o executavel. A pasta web esta ausente."); return 1;
    }
    HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(com)) return 1;
    loader = LoadLibraryExW((directory + L"\\WebView2Loader.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    using Create = HRESULT(STDAPICALLTYPE*)(PCWSTR, PCWSTR, ICoreWebView2EnvironmentOptions*, ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler*);
    auto create = loader ? reinterpret_cast<Create>(GetProcAddress(loader, "CreateCoreWebView2EnvironmentWithOptions")) : nullptr;
    if (!create) { Fail(L"WebView2Loader.dll ausente ou invalido. Extraia todo o pacote do jogo."); CoUninitialize(); return 1; }
    wchar_t local[32768];
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, local))) return 1;
    std::wstring profile = std::wstring(local) + L"\\GmGames\\" + id;
    SHCreateDirectoryExW(nullptr, profile.c_str(), nullptr);
    WNDCLASSW wc{};
    wc.hInstance = instance; wc.lpszClassName = L"GmGamePlayer"; wc.lpfnWndProc = WindowProc;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hIcon = static_cast<HICON>(LoadImageW(nullptr, (directory + L"\\game.ico").c_str(), IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE));
    wc.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    RegisterClassW(&wc);
    int width = std::clamp(_wtoi(Option(L"width", L"1280").c_str()), 320, 7680);
    int height = std::clamp(_wtoi(Option(L"height", L"720").c_str()), 240, 4320);
    RECT bounds{ 0, 0, width, height }; AdjustWindowRect(&bounds, WS_OVERLAPPEDWINDOW, FALSE);
    windowHandle = CreateWindowW(wc.lpszClassName, title.c_str(), WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, bounds.right - bounds.left, bounds.bottom - bounds.top,
        nullptr, nullptr, instance, nullptr);
    ShowWindow(windowHandle, show);
    auto callback = new EnvironmentCreated();
    HRESULT result = create(nullptr, profile.c_str(), nullptr, callback);
    callback->Release();
    if (FAILED(result)) {
        int choice = MessageBoxW(windowHandle, L"Este jogo precisa do Microsoft Edge WebView2 Runtime. Abrir o site oficial para instalar?", title.c_str(), MB_YESNO | MB_ICONINFORMATION);
        if (choice == IDYES) ShellExecuteW(nullptr, L"open", L"https://developer.microsoft.com/microsoft-edge/webview2/", nullptr, nullptr, SW_SHOWNORMAL);
        DestroyWindow(windowHandle);
    }
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
    CoUninitialize();
    // Keep loader mapped until COM callbacks and WebView2 have completed cleanup.
    return FAILED(result) ? 1 : 0;
}
