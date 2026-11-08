#include <windows.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <filesystem>
#include <vector>
#include <array>
#include <set>
#include <stdexcept>
#include <algorithm>
#include <cstring>
#include "bundle.h"
#include "vendor/miniz/miniz_tinfl.h"

namespace {
using Bytes = std::array<unsigned char, 32>;
struct Handle {
    HANDLE h;
    explicit Handle(HANDLE value) : h(value) {}
    ~Handle() { if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h); }
    Handle(const Handle&) = delete;
};
struct Hash {
    BCRYPT_ALG_HANDLE alg{}; BCRYPT_HASH_HANDLE hash{};
    Hash() {
        if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
            throw std::runtime_error("SHA-256 unavailable.");
        if (BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) < 0) {
            BCryptCloseAlgorithmProvider(alg, 0); throw std::runtime_error("SHA-256 initialization failed.");
        }
    }
    ~Hash() { if (hash) BCryptDestroyHash(hash); if (alg) BCryptCloseAlgorithmProvider(alg, 0); }
    void Add(const void* data, size_t size) {
        auto p = static_cast<const unsigned char*>(data);
        while (size) {
            ULONG n = static_cast<ULONG>(std::min<size_t>(size, 65536));
            if (BCryptHashData(hash, const_cast<PUCHAR>(p), n, 0) < 0) throw std::runtime_error("SHA-256 failed.");
            p += n; size -= n;
        }
    }
    Bytes Finish() {
        Bytes b{}; if (BCryptFinishHash(hash, b.data(), 32, 0) < 0) throw std::runtime_error("SHA-256 failed."); return b;
    }
};
std::wstring Hex(const Bytes& b) {
    std::wstring s; for (auto c : b) { s += L"0123456789abcdef"[c >> 4]; s += L"0123456789abcdef"[c & 15]; } return s;
}
template<class T> T Number(const unsigned char*& p, const unsigned char* end) {
    if (size_t(end - p) < sizeof(T)) throw std::runtime_error("Truncated package.");
    T n; std::memcpy(&n, p, sizeof(n)); p += sizeof(n); return n;
}
Bytes Digest(const unsigned char*& p, const unsigned char* end) {
    Bytes b{}; if (end - p < 32) throw std::runtime_error("Truncated digest.");
    std::memcpy(b.data(), p, 32); p += 32; return b;
}
std::wstring Name(const unsigned char* p, uint32_t size) {
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(p), size, nullptr, 0);
    if (!count) throw std::runtime_error("Invalid filename encoding.");
    std::wstring name(count, 0);
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, reinterpret_cast<const char*>(p), size, name.data(), count);
    size_t start = 0;
    while (start <= name.size()) {
        size_t end = name.find(L'/', start); if (end == std::wstring::npos) end = name.size();
        auto part = name.substr(start, end - start);
        if (part.empty() || part == L"." || part == L".." || part.back() == L'.' || part.back() == L' ' ||
            part.find_first_of(L"<>:\"\\|?*") != std::wstring::npos ||
            std::any_of(part.begin(), part.end(), [](wchar_t c) { return c < 32; }))
            throw std::runtime_error("Unsafe package filename.");
        auto stem = part.substr(0, part.find(L'.')); CharUpperBuffW(stem.data(), static_cast<DWORD>(stem.size()));
        if (stem == L"CON" || stem == L"PRN" || stem == L"AUX" || stem == L"NUL" ||
            (stem.size() == 4 && (stem.substr(0, 3) == L"COM" || stem.substr(0, 3) == L"LPT") && stem[3] >= L'1' && stem[3] <= L'9'))
            throw std::runtime_error("Reserved package filename.");
        if (end == name.size()) break; start = end + 1;
    }
    std::replace(name.begin(), name.end(), L'/', L'\\'); return name;
}
struct Entry { std::wstring name; uint64_t size, packed; Bytes digest; const unsigned char* data; };
bool Matches(const std::filesystem::path& file, const Entry& e) {
    DWORD attributes = GetFileAttributesW(file.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY))) return false;
    Handle h(CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
    LARGE_INTEGER size{};
    if (h.h == INVALID_HANDLE_VALUE || !GetFileSizeEx(h.h, &size) || uint64_t(size.QuadPart) != e.size) return false;
    Hash hash; unsigned char buffer[65536]; DWORD n;
    do {
        if (!ReadFile(h.h, buffer, sizeof(buffer), &n, nullptr)) return false;
        hash.Add(buffer, n);
    } while (n);
    return hash.Finish() == e.digest;
}
struct Output { HANDLE file; Hash hash; uint64_t size = 0, limit; bool failed = false; };
int Write(const void* buffer, int length, void* context) {
    auto& o = *static_cast<Output*>(context);
    if (length < 0 || uint64_t(length) > o.limit - o.size) { o.failed = true; return 0; }
    DWORD written;
    if (!WriteFile(o.file, buffer, length, &written, nullptr) || written != DWORD(length)) { o.failed = true; return 0; }
    try { o.hash.Add(buffer, length); } catch (...) { o.failed = true; return 0; }
    o.size += length; return 1;
}
void Extract(const Entry& e, const std::filesystem::path& root) {
    auto dest = root / e.name; std::filesystem::create_directories(dest.parent_path());
    Handle file(CreateFileW(dest.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (file.h == INVALID_HANDLE_VALUE) throw std::runtime_error("Cannot create game file. Check free space and permissions.");
    Output out{file.h, {}, 0, e.size}; size_t consumed = static_cast<size_t>(e.packed);
    int ok = !e.size && !e.packed ? 1 : tinfl_decompress_mem_to_callback(e.data, &consumed, Write, &out, 0);
    if (!ok || out.failed || consumed != e.packed || out.size != e.size || out.hash.Finish() != e.digest)
        throw std::runtime_error("Invalid compressed game file.");
    if (!FlushFileBuffers(file.h)) throw std::runtime_error("Cannot save game file. Check free space.");
}
struct Map {
    const unsigned char* data;
    ~Map() { if (data) UnmapViewOfFile(data); }
};
struct Lock {
    HANDLE handle;
    ~Lock() { ReleaseMutex(handle); }
};
}

std::wstring OpenGameBundle(const std::wstring& executable) {
    Handle file(CreateFileW(executable.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr));
    LARGE_INTEGER length{};
    if (file.h == INVALID_HANDLE_VALUE || !GetFileSizeEx(file.h, &length) || length.QuadPart < 56)
        throw std::runtime_error("Cannot read game executable.");
    Handle mapping(CreateFileMappingW(file.h, nullptr, PAGE_READONLY, 0, 0, nullptr));
    if (!mapping.h) throw std::runtime_error("Cannot map game executable.");
    Map map{static_cast<const unsigned char*>(MapViewOfFile(mapping.h, FILE_MAP_READ, 0, 0, 0))};
    if (!map.data) throw std::runtime_error("Cannot map game executable.");
    auto footer = map.data + length.QuadPart - 56;
    if (std::memcmp(footer, "GMIDESF1", 8)) return {};
    auto p = footer + 8; auto end = map.data + length.QuadPart;
    uint64_t offset = Number<uint64_t>(p, end), size = Number<uint64_t>(p, end); auto digest = Digest(p, end);
    if (offset < 64 || offset > uint64_t(length.QuadPart - 56) || size != uint64_t(length.QuadPart - 56) - offset || size < 12)
        throw std::runtime_error("Invalid package bounds.");
    p = map.data + offset; end = footer;
    Hash payload; payload.Add(p, static_cast<size_t>(size));
    if (payload.Finish() != digest) throw std::runtime_error("Game executable is damaged. Download or export it again.");
    if (std::memcmp(p, "GMBNDL01", 8)) throw std::runtime_error("Unsupported package format."); p += 8;
    auto count = Number<uint32_t>(p, end);
    if (!count || count > 100000) throw std::runtime_error("Invalid file count.");
    std::vector<Entry> entries; std::set<std::wstring> names; uint64_t total = 0;
    for (uint32_t i = 0; i < count; ++i) {
        auto chars = Number<uint32_t>(p, end); auto bytes = Number<uint64_t>(p, end), packed = Number<uint64_t>(p, end);
        auto hash = Digest(p, end);
        if (!chars || chars > 1024 || chars > size_t(end - p)) throw std::runtime_error("Invalid filename length.");
        auto name = Name(p, chars); p += chars; auto key = name; CharUpperBuffW(key.data(), static_cast<DWORD>(key.size()));
        if (!names.insert(key).second) throw std::runtime_error("Duplicate package filename.");
        if (packed > size_t(end - p) || bytes > 16ULL * 1024 * 1024 * 1024 - total) throw std::runtime_error("Package size exceeds limit.");
        entries.push_back({name, bytes, packed, hash, p}); p += packed; total += bytes;
    }
    if (p != end || !names.count(L"GAME.INI") || !names.count(L"WEB\\INDEX.HTML") || !names.count(L"WEBVIEW2LOADER.DLL"))
        throw std::runtime_error("Incomplete game package.");
    wchar_t local[32768];
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, local)))
        throw std::runtime_error("Cannot locate the local game cache.");
    auto parent = std::filesystem::path(local) / L"GmGames" / L"Packages";
    auto cache = parent / Hex(digest);
    Handle mutex(CreateMutexW(nullptr, FALSE, (L"Local\\GmIdeBundle-" + Hex(digest)).c_str()));
    if (!mutex.h) throw std::runtime_error("Cannot lock the game cache.");
    DWORD wait = WaitForSingleObject(mutex.h, 60000);
    if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) throw std::runtime_error("Another copy is preparing this game. Try again.");
    Lock lock{mutex.h};
    if (std::all_of(entries.begin(), entries.end(), [&](const Entry& e) { return Matches(cache / e.name, e); }))
        return cache.wstring();
    std::filesystem::create_directories(parent);
    auto staging = parent / (Hex(digest) + L".tmp-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    if (!std::filesystem::create_directory(staging)) throw std::runtime_error("Cannot prepare the game cache.");
    try {
        for (const auto& e : entries) Extract(e, staging);
        if (std::filesystem::exists(cache)) {
            // Only an invalid, generated package cache is replaced. Saves live elsewhere.
            auto old = staging.wstring() + L".old";
            std::filesystem::rename(cache, old); std::filesystem::rename(staging, cache);
            std::error_code ignored; std::filesystem::remove_all(old, ignored);
        } else std::filesystem::rename(staging, cache);
    } catch (...) {
        std::error_code ignored; std::filesystem::remove_all(staging, ignored); throw;
    }
    return cache.wstring();
}
