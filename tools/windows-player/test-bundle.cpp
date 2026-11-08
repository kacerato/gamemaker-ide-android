#include <windows.h>
#include <iostream>
#include "bundle.h"
// Integration harness: exercise the production reader without opening a game window.
int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    try {
        auto root = OpenGameBundle(argv[1]); if (root.empty()) return 2;
        int n = WideCharToMultiByte(CP_UTF8, 0, root.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string text(n, 0); WideCharToMultiByte(CP_UTF8, 0, root.c_str(), -1, text.data(), n, nullptr, nullptr);
        std::cout << text.c_str() << std::endl; return 0;
    } catch (const std::exception& e) { std::cerr << e.what() << std::endl; return 1; }
}
