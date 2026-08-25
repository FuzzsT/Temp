#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <winnt.h>
#include <tlhelp32.h>
#include <vector>
#include <string>
#include <algorithm>
#include <cstdint>
#include "HookCommon.h"

#pragma comment(lib, "Ws2_32.lib")

using sendto_t = int (WSAAPI*)(SOCKET, const char*, int, int, const sockaddr*, int);
using recvfrom_t = int (WSAAPI*)(SOCKET, char*, int, int, sockaddr*, int*);
using WSASend_t = int (WSAAPI*)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
using WSARecv_t = int (WSAAPI*)(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
using send_t = int (WSAAPI*)(SOCKET, const char*, int, int);
using recv_t = int (WSAAPI*)(SOCKET, char*, int, int);

static sendto_t Real_sendto = nullptr;
static recvfrom_t Real_recvfrom = nullptr;
static WSASend_t Real_WSASend = nullptr;
static WSARecv_t Real_WSARecv = nullptr;
static send_t Real_send = nullptr;
static recv_t Real_recv = nullptr;
static HANDLE gPipe = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION gLock;
static bool gLockReady = false;
static thread_local bool gInHook = false;

static uint64_t Now100ns() {
    FILETIME ft{};
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER u{};
    u.LowPart = ft.dwLowDateTime;
    u.HighPart = ft.dwHighDateTime;
    return u.QuadPart;
}

static void FillAddress(const sockaddr* sa, HookRecordHeader& h) {
    h.addressFamily = 0;
    h.port = 0;
    ZeroMemory(h.address, sizeof(h.address));
    if (!sa) return;
    h.addressFamily = static_cast<uint16_t>(sa->sa_family);
    if (sa->sa_family == AF_INET) {
        const auto* a = reinterpret_cast<const sockaddr_in*>(sa);
        h.port = ntohs(a->sin_port);
        memcpy(h.address, &a->sin_addr, 4);
    } else if (sa->sa_family == AF_INET6) {
        const auto* a = reinterpret_cast<const sockaddr_in6*>(sa);
        h.port = ntohs(a->sin6_port);
        memcpy(h.address, &a->sin6_addr, 16);
    }
}

static bool EnsurePipe() {
    if (gPipe != INVALID_HANDLE_VALUE) return true;
    gPipe = CreateFileW(C7HK_PIPE, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    return gPipe != INVALID_HANDLE_VALUE;
}

static void Trace(HookDirection dir, HookApi api, SOCKET s, const sockaddr* remote, const uint8_t* data, uint32_t len) {
    if (!data || len == 0 || gInHook || !gLockReady) return;
    gInHook = true;
    EnterCriticalSection(&gLock);

    sockaddr_storage ss{};
    int ssLen = sizeof(ss);
    if (!remote && getpeername(s, reinterpret_cast<sockaddr*>(&ss), &ssLen) == 0) {
        remote = reinterpret_cast<const sockaddr*>(&ss);
    }

    HookRecordHeader h{};
    h.magic = C7HK_MAGIC;
    h.version = C7HK_VERSION;
    h.direction = static_cast<uint8_t>(dir);
    h.api = static_cast<uint8_t>(api);
    h.pid = GetCurrentProcessId();
    h.timestamp100ns = Now100ns();
    FillAddress(remote, h);
    h.payloadLength = std::min<uint32_t>(len, 1024u * 1024u);

    if (EnsurePipe()) {
        DWORD written = 0;
        BOOL ok = WriteFile(gPipe, &h, sizeof(h), &written, nullptr);
        if (ok && written == sizeof(h)) {
            ok = WriteFile(gPipe, data, h.payloadLength, &written, nullptr);
        }
        if (!ok) {
            CloseHandle(gPipe);
            gPipe = INVALID_HANDLE_VALUE;
        }
    }

    LeaveCriticalSection(&gLock);
    gInHook = false;
}

static int WSAAPI Hook_sendto(SOCKET s, const char* buf, int len, int flags, const sockaddr* to, int tolen) {
    if (buf && len > 0) Trace(HookDirection::Tx, HookApi::SendTo, s, to, reinterpret_cast<const uint8_t*>(buf), static_cast<uint32_t>(len));
    return Real_sendto(s, buf, len, flags, to, tolen);
}

static int WSAAPI Hook_recvfrom(SOCKET s, char* buf, int len, int flags, sockaddr* from, int* fromlen) {
    int r = Real_recvfrom(s, buf, len, flags, from, fromlen);
    if (r > 0 && buf) Trace(HookDirection::Rx, HookApi::RecvFrom, s, from, reinterpret_cast<const uint8_t*>(buf), static_cast<uint32_t>(r));
    return r;
}

static int WSAAPI Hook_WSASend(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD sent, DWORD flags, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb) {
    if (bufs && count) {
        std::vector<uint8_t> copy;
        size_t total = 0;
        for (DWORD i = 0; i < count; ++i) total += bufs[i].len;
        total = std::min<size_t>(total, 1024u * 1024u);
        copy.reserve(total);
        for (DWORD i = 0; i < count && copy.size() < total; ++i) {
            size_t n = std::min<size_t>(bufs[i].len, total - copy.size());
            if (bufs[i].buf && n) copy.insert(copy.end(), reinterpret_cast<uint8_t*>(bufs[i].buf), reinterpret_cast<uint8_t*>(bufs[i].buf) + n);
        }
        if (!copy.empty()) Trace(HookDirection::Tx, HookApi::WSASend, s, nullptr, copy.data(), static_cast<uint32_t>(copy.size()));
    }
    return Real_WSASend(s, bufs, count, sent, flags, ov, cb);
}

static int WSAAPI Hook_WSARecv(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD received, LPDWORD flags, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb) {
    int r = Real_WSARecv(s, bufs, count, received, flags, ov, cb);
    if (r == 0 && received && *received > 0 && bufs && count && !ov) {
        std::vector<uint8_t> copy;
        size_t remain = *received;
        for (DWORD i = 0; i < count && remain; ++i) {
            size_t n = std::min<size_t>(bufs[i].len, remain);
            if (bufs[i].buf && n) copy.insert(copy.end(), reinterpret_cast<uint8_t*>(bufs[i].buf), reinterpret_cast<uint8_t*>(bufs[i].buf) + n);
            remain -= n;
        }
        if (!copy.empty()) Trace(HookDirection::Rx, HookApi::WSARecv, s, nullptr, copy.data(), static_cast<uint32_t>(copy.size()));
    }
    return r;
}

static int WSAAPI Hook_send(SOCKET s, const char* buf, int len, int flags) {
    if (buf && len > 0) Trace(HookDirection::Tx, HookApi::Send, s, nullptr, reinterpret_cast<const uint8_t*>(buf), static_cast<uint32_t>(len));
    return Real_send(s, buf, len, flags);
}

static int WSAAPI Hook_recv(SOCKET s, char* buf, int len, int flags) {
    int r = Real_recv(s, buf, len, flags);
    if (r > 0 && buf) Trace(HookDirection::Rx, HookApi::Recv, s, nullptr, reinterpret_cast<const uint8_t*>(buf), static_cast<uint32_t>(r));
    return r;
}

static bool PatchIAT(HMODULE module, const char* importedDll, const char* procName, void* hook, void** original) {
    if (!module) return false;
    auto* base = reinterpret_cast<uint8_t*>(module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return false;
    auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return false;

    auto* desc = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);
    for (; desc->Name; ++desc) {
        const char* dll = reinterpret_cast<const char*>(base + desc->Name);
        if (_stricmp(dll, importedDll) != 0) continue;

        auto* firstThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + desc->FirstThunk);
        auto* origThunk = desc->OriginalFirstThunk ? reinterpret_cast<IMAGE_THUNK_DATA*>(base + desc->OriginalFirstThunk) : firstThunk;
        for (; origThunk->u1.AddressOfData; ++origThunk, ++firstThunk) {
            if (IMAGE_SNAP_BY_ORDINAL(origThunk->u1.Ordinal)) continue;
            auto* byName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + origThunk->u1.AddressOfData);
            if (strcmp(reinterpret_cast<const char*>(byName->Name), procName) != 0) continue;

            DWORD oldProtect = 0;
            if (!VirtualProtect(&firstThunk->u1.Function, sizeof(uintptr_t), PAGE_READWRITE, &oldProtect)) return false;
            if (original && !*original) *original = reinterpret_cast<void*>(static_cast<uintptr_t>(firstThunk->u1.Function));
#ifdef _WIN64
            firstThunk->u1.Function = reinterpret_cast<ULONGLONG>(hook);
#else
            firstThunk->u1.Function = reinterpret_cast<DWORD>(hook);
#endif
            DWORD ignored = 0;
            VirtualProtect(&firstThunk->u1.Function, sizeof(uintptr_t), oldProtect, &ignored);
            FlushInstructionCache(GetCurrentProcess(), &firstThunk->u1.Function, sizeof(uintptr_t));
            return true;
        }
    }
    return false;
}

static void PatchModule(HMODULE module) {
    void* dummy = nullptr;
    PatchIAT(module, "WS2_32.dll", "sendto", reinterpret_cast<void*>(&Hook_sendto), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "recvfrom", reinterpret_cast<void*>(&Hook_recvfrom), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "WSASend", reinterpret_cast<void*>(&Hook_WSASend), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "WSARecv", reinterpret_cast<void*>(&Hook_WSARecv), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "send", reinterpret_cast<void*>(&Hook_send), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "recv", reinterpret_cast<void*>(&Hook_recv), &dummy);
}

static DWORD WINAPI InstallThread(LPVOID) {
    InitializeCriticalSection(&gLock);
    gLockReady = true;

    HMODULE ws2 = GetModuleHandleW(L"Ws2_32.dll");
    if (!ws2) ws2 = LoadLibraryW(L"Ws2_32.dll");
    Real_sendto = reinterpret_cast<sendto_t>(GetProcAddress(ws2, "sendto"));
    Real_recvfrom = reinterpret_cast<recvfrom_t>(GetProcAddress(ws2, "recvfrom"));
    Real_WSASend = reinterpret_cast<WSASend_t>(GetProcAddress(ws2, "WSASend"));
    Real_WSARecv = reinterpret_cast<WSARecv_t>(GetProcAddress(ws2, "WSARecv"));
    Real_send = reinterpret_cast<send_t>(GetProcAddress(ws2, "send"));
    Real_recv = reinterpret_cast<recv_t>(GetProcAddress(ws2, "recv"));

    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snap != INVALID_HANDLE_VALUE) {
        MODULEENTRY32W me{}; me.dwSize = sizeof(me);
        if (Module32FirstW(snap, &me)) {
            do { PatchModule(me.hModule); } while (Module32NextW(snap, &me));
        }
        CloseHandle(snap);
    } else {
        PatchModule(GetModuleHandleW(nullptr));
    }
    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        HANDLE h = CreateThread(nullptr, 0, InstallThread, nullptr, 0, nullptr);
        if (h) CloseHandle(h);
    } else if (reason == DLL_PROCESS_DETACH) {
        if (gPipe != INVALID_HANDLE_VALUE) CloseHandle(gPipe);
        if (gLockReady) DeleteCriticalSection(&gLock);
    }
    return TRUE;
}
