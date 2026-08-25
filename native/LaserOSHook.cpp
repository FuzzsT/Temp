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
#include <unordered_map>
#include "HookCommon.h"
#include "VirtualLaserCubeNative.h"

#pragma comment(lib, "Ws2_32.lib")

using sendto_t = int (WSAAPI*)(SOCKET, const char*, int, int, const sockaddr*, int);
using recvfrom_t = int (WSAAPI*)(SOCKET, char*, int, int, sockaddr*, int*);
using WSASend_t = int (WSAAPI*)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
using WSARecv_t = int (WSAAPI*)(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
using WSASendTo_t = int (WSAAPI*)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const sockaddr*, int, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
using WSARecvFrom_t = int (WSAAPI*)(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, sockaddr*, LPINT, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
using send_t = int (WSAAPI*)(SOCKET, const char*, int, int);
using recv_t = int (WSAAPI*)(SOCKET, char*, int, int);
using bind_t = int (WSAAPI*)(SOCKET, const sockaddr*, int);
using socket_t = SOCKET (WSAAPI*)(int, int, int);
using closesocket_t = int (WSAAPI*)(SOCKET);

static sendto_t Real_sendto = nullptr;
static recvfrom_t Real_recvfrom = nullptr;
static WSASend_t Real_WSASend = nullptr;
static WSARecv_t Real_WSARecv = nullptr;
static WSASendTo_t Real_WSASendTo = nullptr;
static WSARecvFrom_t Real_WSARecvFrom = nullptr;
static send_t Real_send = nullptr;
static recv_t Real_recv = nullptr;
static bind_t Real_bind = nullptr;
static socket_t Real_socket = nullptr;
static closesocket_t Real_closesocket = nullptr;

static HMODULE gModule = nullptr;
static HANDLE gPipe = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION gLock;
static CRITICAL_SECTION gVirtualLock;
static bool gLockReady = false;
static bool gVirtualLockReady = false;
static bool gVirtualEnabled = false;
static thread_local bool gInHook = false;
static std::unordered_map<SOCKET, VirtualLaserCubeNative> gVirtualState;

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
    if (!remote && getpeername(s, reinterpret_cast<sockaddr*>(&ss), &ssLen) == 0)
        remote = reinterpret_cast<const sockaddr*>(&ss);

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
        if (ok && written == sizeof(h))
            ok = WriteFile(gPipe, data, h.payloadLength, &written, nullptr);
        if (!ok) {
            CloseHandle(gPipe);
            gPipe = INVALID_HANDLE_VALUE;
        }
    }

    LeaveCriticalSection(&gLock);
    gInHook = false;
}

static bool MarkerEnabled() {
    if (!gModule) return false;
    wchar_t dllPath[MAX_PATH]{};
    DWORD n = GetModuleFileNameW(gModule, dllPath, MAX_PATH);
    if (!n || n >= MAX_PATH) return false;
    std::wstring path(dllPath, n);
    auto pos = path.find_last_of(L"\\/");
    if (pos == std::wstring::npos) return false;
    path.resize(pos + 1);
    path += C7HK_VIRTUAL_MARKER;
    DWORD attrs = GetFileAttributesW(path.c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY);
}

static uint16_t RemotePort(SOCKET s, const sockaddr* remote) {
    sockaddr_storage ss{};
    int len = sizeof(ss);
    if (!remote) {
        if (getpeername(s, reinterpret_cast<sockaddr*>(&ss), &len) != 0) return 0;
        remote = reinterpret_cast<const sockaddr*>(&ss);
    }
    if (remote->sa_family == AF_INET)
        return ntohs(reinterpret_cast<const sockaddr_in*>(remote)->sin_port);
    if (remote->sa_family == AF_INET6)
        return ntohs(reinterpret_cast<const sockaddr_in6*>(remote)->sin6_port);
    return 0;
}

static bool EnsureSocketBound(SOCKET s, sockaddr_in& local) {
    int len = sizeof(local);
    ZeroMemory(&local, sizeof(local));
    if (getsockname(s, reinterpret_cast<sockaddr*>(&local), &len) == 0 && local.sin_family == AF_INET && local.sin_port != 0)
        return true;

    if (!Real_bind) return false;
    sockaddr_in any{};
    any.sin_family = AF_INET;
    any.sin_addr.s_addr = htonl(INADDR_ANY);
    any.sin_port = 0;
    if (Real_bind(s, reinterpret_cast<const sockaddr*>(&any), sizeof(any)) != 0) return false;

    len = sizeof(local);
    return getsockname(s, reinterpret_cast<sockaddr*>(&local), &len) == 0 && local.sin_family == AF_INET && local.sin_port != 0;
}

static bool InjectResponseToSocket(SOCKET target, const std::vector<uint8_t>& response) {
    if (response.empty() || !Real_socket || !Real_sendto || !Real_closesocket) return response.empty();

    sockaddr_in local{};
    if (!EnsureSocketBound(target, local)) return false;
    if (local.sin_addr.s_addr == htonl(INADDR_ANY))
        local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    SOCKET helper = Real_socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (helper == INVALID_SOCKET) return false;
    int sent = Real_sendto(helper, reinterpret_cast<const char*>(response.data()), static_cast<int>(response.size()), 0,
        reinterpret_cast<const sockaddr*>(&local), sizeof(local));
    Real_closesocket(helper);
    return sent == static_cast<int>(response.size());
}

static bool IsVirtualOpcodePort(uint8_t opcode, uint16_t port) {
    if (opcode == 0xA9) return port == 45458;
    if (opcode == 0x77) return port == 45456 || port == 45457;
    return (opcode == 0x78 || opcode == 0x80 || opcode == 0x8A) && port == 45457;
}

static bool HandleVirtualTx(SOCKET s, const sockaddr* remote, const uint8_t* data, size_t len) {
    if (!gVirtualEnabled || !data || len == 0 || !gVirtualLockReady) return false;
    const uint16_t port = RemotePort(s, remote);
    if (!IsVirtualOpcodePort(data[0], port)) return false;

    std::vector<uint8_t> payload(data, data + len);
    std::vector<uint8_t> response;
    EnterCriticalSection(&gVirtualLock);
    response = gVirtualState[s].Handle(payload);
    LeaveCriticalSection(&gVirtualLock);

    // SAMPLE_DATA may intentionally have no reply when buffer responses are disabled.
    if (!response.empty() && !InjectResponseToSocket(s, response))
        return false;
    return true;
}

static std::vector<uint8_t> FlattenBuffers(LPWSABUF bufs, DWORD count) {
    std::vector<uint8_t> copy;
    if (!bufs || !count) return copy;
    size_t total = 0;
    for (DWORD i = 0; i < count; ++i) total += bufs[i].len;
    total = std::min<size_t>(total, 1024u * 1024u);
    copy.reserve(total);
    for (DWORD i = 0; i < count && copy.size() < total; ++i) {
        size_t n = std::min<size_t>(bufs[i].len, total - copy.size());
        if (bufs[i].buf && n)
            copy.insert(copy.end(), reinterpret_cast<uint8_t*>(bufs[i].buf), reinterpret_cast<uint8_t*>(bufs[i].buf) + n);
    }
    return copy;
}

static int WSAAPI Hook_sendto(SOCKET s, const char* buf, int len, int flags, const sockaddr* to, int tolen) {
    if (buf && len > 0) {
        Trace(HookDirection::Tx, HookApi::SendTo, s, to, reinterpret_cast<const uint8_t*>(buf), static_cast<uint32_t>(len));
        if (HandleVirtualTx(s, to, reinterpret_cast<const uint8_t*>(buf), static_cast<size_t>(len))) return len;
    }
    return Real_sendto(s, buf, len, flags, to, tolen);
}

static int WSAAPI Hook_recvfrom(SOCKET s, char* buf, int len, int flags, sockaddr* from, int* fromlen) {
    int r = Real_recvfrom(s, buf, len, flags, from, fromlen);
    if (r > 0 && buf) Trace(HookDirection::Rx, HookApi::RecvFrom, s, from, reinterpret_cast<const uint8_t*>(buf), static_cast<uint32_t>(r));
    return r;
}

static int WSAAPI Hook_WSASend(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD sent, DWORD flags, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb) {
    auto copy = FlattenBuffers(bufs, count);
    if (!copy.empty()) {
        Trace(HookDirection::Tx, HookApi::WSASend, s, nullptr, copy.data(), static_cast<uint32_t>(copy.size()));
        if (HandleVirtualTx(s, nullptr, copy.data(), copy.size()) && !ov) {
            if (sent) *sent = static_cast<DWORD>(copy.size());
            return 0;
        }
    }
    return Real_WSASend(s, bufs, count, sent, flags, ov, cb);
}

static int WSAAPI Hook_WSARecv(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD received, LPDWORD flags, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb) {
    int r = Real_WSARecv(s, bufs, count, received, flags, ov, cb);
    if (r == 0 && received && *received > 0 && bufs && count && !ov) {
        auto copy = FlattenBuffers(bufs, count);
        if (copy.size() > *received) copy.resize(*received);
        if (!copy.empty()) Trace(HookDirection::Rx, HookApi::WSARecv, s, nullptr, copy.data(), static_cast<uint32_t>(copy.size()));
    }
    return r;
}

static int WSAAPI Hook_WSASendTo(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD sent, DWORD flags, const sockaddr* to, int tolen, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb) {
    auto copy = FlattenBuffers(bufs, count);
    if (!copy.empty()) {
        Trace(HookDirection::Tx, HookApi::WSASendTo, s, to, copy.data(), static_cast<uint32_t>(copy.size()));
        if (HandleVirtualTx(s, to, copy.data(), copy.size()) && !ov) {
            if (sent) *sent = static_cast<DWORD>(copy.size());
            return 0;
        }
    }
    return Real_WSASendTo(s, bufs, count, sent, flags, to, tolen, ov, cb);
}

static int WSAAPI Hook_WSARecvFrom(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD received, LPDWORD flags, sockaddr* from, LPINT fromlen, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb) {
    int r = Real_WSARecvFrom(s, bufs, count, received, flags, from, fromlen, ov, cb);
    if (r == 0 && received && *received > 0 && bufs && count && !ov) {
        auto copy = FlattenBuffers(bufs, count);
        if (copy.size() > *received) copy.resize(*received);
        if (!copy.empty()) Trace(HookDirection::Rx, HookApi::WSARecvFrom, s, from, copy.data(), static_cast<uint32_t>(copy.size()));
    }
    return r;
}

static int WSAAPI Hook_send(SOCKET s, const char* buf, int len, int flags) {
    if (buf && len > 0) {
        Trace(HookDirection::Tx, HookApi::Send, s, nullptr, reinterpret_cast<const uint8_t*>(buf), static_cast<uint32_t>(len));
        if (HandleVirtualTx(s, nullptr, reinterpret_cast<const uint8_t*>(buf), static_cast<size_t>(len))) return len;
    }
    return Real_send(s, buf, len, flags);
}

static int WSAAPI Hook_recv(SOCKET s, char* buf, int len, int flags) {
    int r = Real_recv(s, buf, len, flags);
    if (r > 0 && buf) Trace(HookDirection::Rx, HookApi::Recv, s, nullptr, reinterpret_cast<const uint8_t*>(buf), static_cast<uint32_t>(r));
    return r;
}

static WORD WinsockOrdinalForName(const char* procName) {
    // Classic Winsock 2 exports are commonly imported by ordinal by MSVC.
    // These values are stable WS2_32 ordinals: recv=16, recvfrom=17,
    // send=19, sendto=20. Newer WSA* routines are normally imported by name.
    if (strcmp(procName, "recv") == 0) return 16;
    if (strcmp(procName, "recvfrom") == 0) return 17;
    if (strcmp(procName, "send") == 0) return 19;
    if (strcmp(procName, "sendto") == 0) return 20;
    return 0;
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
            bool matches = false;
            if (IMAGE_SNAP_BY_ORDINAL(origThunk->u1.Ordinal)) {
                const WORD expected = WinsockOrdinalForName(procName);
                const WORD actual = static_cast<WORD>(IMAGE_ORDINAL(origThunk->u1.Ordinal));
                matches = expected != 0 && actual == expected;
            } else {
                auto* byName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + origThunk->u1.AddressOfData);
                matches = strcmp(reinterpret_cast<const char*>(byName->Name), procName) == 0;
            }
            if (!matches) continue;

            DWORD oldProtect = 0;
            if (!VirtualProtect(&firstThunk->u1.Function, sizeof(uintptr_t), PAGE_READWRITE, &oldProtect)) return false;
            auto current = reinterpret_cast<void*>(static_cast<uintptr_t>(firstThunk->u1.Function));
            if (current == hook) {
                DWORD ignored = 0;
                VirtualProtect(&firstThunk->u1.Function, sizeof(uintptr_t), oldProtect, &ignored);
                return true;
            }
            if (original && !*original) *original = current;
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
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "WSASendTo", reinterpret_cast<void*>(&Hook_WSASendTo), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "WSARecvFrom", reinterpret_cast<void*>(&Hook_WSARecvFrom), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "send", reinterpret_cast<void*>(&Hook_send), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "recv", reinterpret_cast<void*>(&Hook_recv), &dummy);
}

static void PatchAllModules() {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snap == INVALID_HANDLE_VALUE) {
        PatchModule(GetModuleHandleW(nullptr));
        return;
    }
    MODULEENTRY32W me{};
    me.dwSize = sizeof(me);
    if (Module32FirstW(snap, &me)) {
        do { PatchModule(me.hModule); } while (Module32NextW(snap, &me));
    }
    CloseHandle(snap);
}

static DWORD WINAPI InstallThread(LPVOID) {
    InitializeCriticalSection(&gLock);
    InitializeCriticalSection(&gVirtualLock);
    gLockReady = true;
    gVirtualLockReady = true;

    HMODULE ws2 = GetModuleHandleW(L"Ws2_32.dll");
    if (!ws2) ws2 = LoadLibraryW(L"Ws2_32.dll");
    Real_sendto = reinterpret_cast<sendto_t>(GetProcAddress(ws2, "sendto"));
    Real_recvfrom = reinterpret_cast<recvfrom_t>(GetProcAddress(ws2, "recvfrom"));
    Real_WSASend = reinterpret_cast<WSASend_t>(GetProcAddress(ws2, "WSASend"));
    Real_WSARecv = reinterpret_cast<WSARecv_t>(GetProcAddress(ws2, "WSARecv"));
    Real_WSASendTo = reinterpret_cast<WSASendTo_t>(GetProcAddress(ws2, "WSASendTo"));
    Real_WSARecvFrom = reinterpret_cast<WSARecvFrom_t>(GetProcAddress(ws2, "WSARecvFrom"));
    Real_send = reinterpret_cast<send_t>(GetProcAddress(ws2, "send"));
    Real_recv = reinterpret_cast<recv_t>(GetProcAddress(ws2, "recv"));
    Real_bind = reinterpret_cast<bind_t>(GetProcAddress(ws2, "bind"));
    Real_socket = reinterpret_cast<socket_t>(GetProcAddress(ws2, "socket"));
    Real_closesocket = reinterpret_cast<closesocket_t>(GetProcAddress(ws2, "closesocket"));

    gVirtualEnabled = MarkerEnabled();

    // Re-patch periodically so networking DLLs loaded after injection are also covered.
    for (;;) {
        PatchAllModules();
        Sleep(750);
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        gModule = module;
        DisableThreadLibraryCalls(module);
        HANDLE h = CreateThread(nullptr, 0, InstallThread, nullptr, 0, nullptr);
        if (h) CloseHandle(h);
    } else if (reason == DLL_PROCESS_DETACH) {
        if (gPipe != INVALID_HANDLE_VALUE) CloseHandle(gPipe);
        if (gLockReady) DeleteCriticalSection(&gLock);
        if (gVirtualLockReady) DeleteCriticalSection(&gVirtualLock);
    }
    return TRUE;
}
