#pragma once
#include <string>
// Empty means an unpackaged development player. A damaged bundle throws.
std::wstring OpenGameBundle(const std::wstring& executable);
