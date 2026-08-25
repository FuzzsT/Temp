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
#include <cstring>
#include <unordered_map>
#include "HookCommon.h"
#include "VirtualLaserCubeNative.h"
#include "LoopbackAutoconfigNative.h"

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
using connect_t = int (WSAAPI*)(SOCKET, const sockaddr*, int);
using WSAConnect_t = int (WSAAPI*)(SOCKET, const sockaddr*, int, LPWSABUF, LPWSABUF, LPQOS, LPQOS);
using socket_t = SOCKET (WSAAPI*)(int, int, int);
using closesocket_t = int (WSAAPI*)(SOCKET);

// MSVC x64 member-function ABI: RCX=this, then normal x64 argument registers.
using renderer_create_frame_t = void(__fastcall*)(void*, int, bool, bool);
using renderer_vertex_t = void(__fastcall*)(void*, float, float, uint32_t, int);
using renderer_vertex3_t = void(__fastcall*)(void*, float, float, float, uint32_t, int);

static sendto_t Real_sendto = nullptr;
static recvfrom_t Real_recvfrom = nullptr;
static WSASend_t Real_WSASend = nullptr;
static WSARecv_t Real_WSARecv = nullptr;
static WSASendTo_t Real_WSASendTo = nullptr;
static WSARecvFrom_t Real_WSARecvFrom = nullptr;
static send_t Real_send = nullptr;
static recv_t Real_recv = nullptr;
static bind_t Real_bind = nullptr;
static connect_t Real_connect = nullptr;
static WSAConnect_t Real_WSAConnect = nullptr;
static socket_t Real_socket = nullptr;
static closesocket_t Real_closesocket = nullptr;
static renderer_create_frame_t Real_RendererCreateNewFrame = nullptr;
static renderer_vertex_t Real_RendererVertex = nullptr;
static renderer_vertex3_t Real_RendererVertex3 = nullptr;

static HMODULE gModule = nullptr;
static HANDLE gPipe = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION gLock;
static CRITICAL_SECTION gVirtualLock;
static CRITICAL_SECTION gRendererLock;
static bool gLockReady = false;
static bool gVirtualLockReady = false;
static bool gRendererLockReady = false;
static bool gVirtualEnabled = false;
static bool gLoopbackEnabled = false;
static LoopbackRouteConfig gLoopbackConfig{};
static thread_local bool gInHook = false;
static std::unordered_map<SOCKET, VirtualLaserCubeNative> gVirtualState;

struct RendererState {
    int32_t rate = 0;
    uint16_t flags = 0;
    bool started = false;
    std::vector<RendererPointWire> points;
};
static std::unordered_map<void*, RendererState> gRendererState;
static constexpr size_t MAX_RENDER_POINTS = 65500;

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
    if (!remote && s != INVALID_SOCKET && getpeername(s, reinterpret_cast<sockaddr*>(&ss), &ssLen) == 0)
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

static void EmitRendererFrame(void* renderer) {
    if (!renderer || !gRendererLockReady) return;

    RendererState snapshot;
    bool have = false;
    EnterCriticalSection(&gRendererLock);
    auto it = gRendererState.find(renderer);
    if (it != gRendererState.end() && it->second.started && !it->second.points.empty()) {
        snapshot.rate = it->second.rate;
        snapshot.flags = it->second.flags;
        snapshot.started = true;
        snapshot.points.swap(it->second.points);
        have = true;
    }
    LeaveCriticalSection(&gRendererLock);
    if (!have) return;

    const size_t count = std::min(snapshot.points.size(), MAX_RENDER_POINTS);
    const size_t bytes = sizeof(RendererFrameWireHeader) + count * sizeof(RendererPointWire);
    std::vector<uint8_t> payload(bytes);
    RendererFrameWireHeader header{};
    header.magic = C7RF_MAGIC;
    header.version = C7RF_VERSION;
    header.flags = snapshot.flags;
    header.rate = snapshot.rate;
    header.pointCount = static_cast<uint32_t>(count);
    header.rendererId = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(renderer));
    memcpy(payload.data(), &header, sizeof(header));
    if (count)
        memcpy(payload.data() + sizeof(header), snapshot.points.data(), count * sizeof(RendererPointWire));

    Trace(HookDirection::Tx, HookApi::RendererFrame, INVALID_SOCKET, nullptr, payload.data(), static_cast<uint32_t>(payload.size()));
}

static void __fastcall Hook_RendererCreateNewFrame(void* self, int rate, bool flag1, bool flag2) {
    EmitRendererFrame(self);
    if (gRendererLockReady && self) {
        EnterCriticalSection(&gRendererLock);
        auto& state = gRendererState[self];
        state.rate = rate;
        state.flags = static_cast<uint16_t>((flag1 ? 1 : 0) | (flag2 ? 2 : 0));
        state.started = true;
        state.points.clear();
        state.points.reserve(4096);
        LeaveCriticalSection(&gRendererLock);
    }
    if (Real_RendererCreateNewFrame) Real_RendererCreateNewFrame(self, rate, flag1, flag2);
}

static void AppendRendererPoint(void* self, float x, float y, uint32_t color, int param) {
    if (!self || !gRendererLockReady) return;
    EnterCriticalSection(&gRendererLock);
    auto& state = gRendererState[self];
    if (!state.started) state.started = true;
    if (state.points.size() < MAX_RENDER_POINTS)
        state.points.push_back(RendererPointWire{x, y, color, static_cast<int32_t>(param)});
    LeaveCriticalSection(&gRendererLock);
}

static void __fastcall Hook_RendererVertex(void* self, float x, float y, uint32_t color, int param) {
    AppendRendererPoint(self, x, y, color, param);
    if (Real_RendererVertex) Real_RendererVertex(self, x, y, color, param);
}

static void __fastcall Hook_RendererVertex3(void* self, float x, float y, float z, uint32_t color, int param) {
    (void)z;
    AppendRendererPoint(self, x, y, color, param);
    if (Real_RendererVertex3) Real_RendererVertex3(self, x, y, z, color, param);
}

static bool GetMarkerPath(std::wstring& path) {
    if (!gModule) return false;
    wchar_t dllPath[MAX_PATH]{};
    DWORD n = GetModuleFileNameW(gModule, dllPath, MAX_PATH);
    if (!n || n >= MAX_PATH) return false;
    path.assign(dllPath, n);
    auto pos = path.find_last_of(L"\\/");
    if (pos == std::wstring::npos) return false;
    path.resize(pos + 1);
    path += C7HK_VIRTUAL_MARKER;
    return true;
}

static bool ReadMarker(std::string& text) {
    text.clear();
    std::wstring path;
    if (!GetMarkerPath(path)) return false;
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart < 0 || size.QuadPart > 64 * 1024) {
        CloseHandle(h);
        return false;
    }
    text.resize(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    BOOL ok = text.empty() || ReadFile(h, text.data(), static_cast<DWORD>(text.size()), &read, nullptr);
    CloseHandle(h);
    if (!ok) return false;
    text.resize(read);
    return true;
}

static void LoadRoutingMode() {
    std::string marker;
    if (!ReadMarker(marker)) {
        gVirtualEnabled = false;
        gLoopbackEnabled = false;
        return;
    }

    LoopbackRouteConfig route{};
    if (!marker.empty() && ParseLoopbackRouteConfig(marker, route)) {
        gLoopbackConfig = route;
        gLoopbackEnabled = true;
        gVirtualEnabled = false;
        return;
    }

    // Empty/legacy marker is retained for the native early-hook compatibility probe.
    gLoopbackEnabled = false;
    gVirtualEnabled = true;
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
    if (local.sin_addr.s_addr == htonl(INADDR_ANY)) local.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    SOCKET helper = Real_socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (helper == INVALID_SOCKET) return false;
    int sent = Real_sendto(helper, reinterpret_cast<const char*>(response.data()), static_cast<int>(response.size()), 0,
        reinterpret_cast<const sockaddr*>(&local), sizeof(local));
    Real_closesocket(helper);
    return sent == static_cast<int>(response.size());
}

static bool IsVirtualOpcodePort(uint8_t opcode, uint16_t port) {
    if (opcode == 0x27) return port == 45456;
    if (opcode == 0x77) return port == 45457;
    if (opcode == 0xA9) return port == 45458;
    return (opcode == 0x78 || opcode == 0x80 || opcode == 0x8A) && port == 45457;
}

static bool HandleVirtualTx(SOCKET s, const sockaddr* remote, const uint8_t* data, size_t len) {
    if (!gVirtualEnabled || gLoopbackEnabled || !data || len == 0 || !gVirtualLockReady) return false;
    const uint16_t port = RemotePort(s, remote);
    if (!IsVirtualOpcodePort(data[0], port)) return false;
    std::vector<uint8_t> payload(data, data + len);
    std::vector<uint8_t> response;
    EnterCriticalSection(&gVirtualLock);
    response = gVirtualState[s].Handle(payload);
    LeaveCriticalSection(&gVirtualLock);
    if (!response.empty() && !InjectResponseToSocket(s, response)) return false;
    return true;
}

static const sockaddr* RoutedDestination(const sockaddr* original, int originalLen, sockaddr_storage& storage, int& routedLen) {
    routedLen = originalLen;
    if (gLoopbackEnabled && RewriteLaserCubeDestination(original, originalLen, gLoopbackConfig, storage, routedLen))
        return reinterpret_cast<const sockaddr*>(&storage);
    return original;
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

static int WSAAPI Hook_bind(SOCKET s, const sockaddr* name, int namelen) {
    sockaddr_storage rewritten{};
    int rewrittenLen = namelen;
    if (gLoopbackEnabled && RewriteLaserCubeClientBind(name, namelen, gLoopbackConfig, rewritten, rewrittenLen))
        return Real_bind(s, reinterpret_cast<const sockaddr*>(&rewritten), rewrittenLen);
    return Real_bind(s, name, namelen);
}

static int WSAAPI Hook_connect(SOCKET s, const sockaddr* name, int namelen) {
    sockaddr_storage rewritten{};
    int rewrittenLen = namelen;
    const sockaddr* target = RoutedDestination(name, namelen, rewritten, rewrittenLen);
    return Real_connect(s, target, rewrittenLen);
}

static int WSAAPI Hook_WSAConnect(SOCKET s, const sockaddr* name, int namelen, LPWSABUF callerData, LPWSABUF calleeData, LPQOS sqos, LPQOS gqos) {
    sockaddr_storage rewritten{};
    int rewrittenLen = namelen;
    const sockaddr* target = RoutedDestination(name, namelen, rewritten, rewrittenLen);
    return Real_WSAConnect(s, target, rewrittenLen, callerData, calleeData, sqos, gqos);
}

static int WSAAPI Hook_sendto(SOCKET s, const char* buf, int len, int flags, const sockaddr* to, int tolen) {
    if (buf && len > 0) {
        Trace(HookDirection::Tx, HookApi::SendTo, s, to, reinterpret_cast<const uint8_t*>(buf), static_cast<uint32_t>(len));
        if (gLoopbackEnabled) {
            sockaddr_storage rewritten{};
            int rewrittenLen = tolen;
            const sockaddr* target = RoutedDestination(to, tolen, rewritten, rewrittenLen);
            return Real_sendto(s, buf, len, flags, target, rewrittenLen);
        }
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
        if (HandleVirtualTx(s, nullptr, copy.data(), copy.size()) && !ov) { if (sent) *sent = static_cast<DWORD>(copy.size()); return 0; }
    }
    return Real_WSASend(s, bufs, count, sent, flags, ov, cb);
}

static int WSAAPI Hook_WSARecv(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD received, LPDWORD flags, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb) {
    int r = Real_WSARecv(s, bufs, count, received, flags, ov, cb);
    if (r == 0 && received && *received > 0 && bufs && count && !ov) {
        auto copy = FlattenBuffers(bufs, count); if (copy.size() > *received) copy.resize(*received);
        if (!copy.empty()) Trace(HookDirection::Rx, HookApi::WSARecv, s, nullptr, copy.data(), static_cast<uint32_t>(copy.size()));
    }
    return r;
}

static int WSAAPI Hook_WSASendTo(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD sent, DWORD flags, const sockaddr* to, int tolen, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb) {
    auto copy = FlattenBuffers(bufs, count);
    if (!copy.empty()) {
        Trace(HookDirection::Tx, HookApi::WSASendTo, s, to, copy.data(), static_cast<uint32_t>(copy.size()));
        if (gLoopbackEnabled) {
            sockaddr_storage rewritten{};
            int rewrittenLen = tolen;
            const sockaddr* target = RoutedDestination(to, tolen, rewritten, rewrittenLen);
            return Real_WSASendTo(s, bufs, count, sent, flags, target, rewrittenLen, ov, cb);
        }
        if (HandleVirtualTx(s, to, copy.data(), copy.size()) && !ov) { if (sent) *sent = static_cast<DWORD>(copy.size()); return 0; }
    }
    return Real_WSASendTo(s, bufs, count, sent, flags, to, tolen, ov, cb);
}

static int WSAAPI Hook_WSARecvFrom(SOCKET s, LPWSABUF bufs, DWORD count, LPDWORD received, LPDWORD flags, sockaddr* from, LPINT fromlen, LPWSAOVERLAPPED ov, LPWSAOVERLAPPED_COMPLETION_ROUTINE cb) {
    int r = Real_WSARecvFrom(s, bufs, count, received, flags, from, fromlen, ov, cb);
    if (r == 0 && received && *received > 0 && bufs && count && !ov) {
        auto copy = FlattenBuffers(bufs, count); if (copy.size() > *received) copy.resize(*received);
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
    if (strcmp(procName, "bind") == 0) return 2;
    if (strcmp(procName, "connect") == 0) return 4;
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
                DWORD ignored = 0; VirtualProtect(&firstThunk->u1.Function, sizeof(uintptr_t), oldProtect, &ignored); return true;
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
    PatchIAT(module, "WS2_32.dll", "bind", reinterpret_cast<void*>(&Hook_bind), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "connect", reinterpret_cast<void*>(&Hook_connect), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "WSAConnect", reinterpret_cast<void*>(&Hook_WSAConnect), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "sendto", reinterpret_cast<void*>(&Hook_sendto), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "recvfrom", reinterpret_cast<void*>(&Hook_recvfrom), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "WSASend", reinterpret_cast<void*>(&Hook_WSASend), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "WSARecv", reinterpret_cast<void*>(&Hook_WSARecv), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "WSASendTo", reinterpret_cast<void*>(&Hook_WSASendTo), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "WSARecvFrom", reinterpret_cast<void*>(&Hook_WSARecvFrom), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "send", reinterpret_cast<void*>(&Hook_send), &dummy);
    dummy = nullptr; PatchIAT(module, "WS2_32.dll", "recv", reinterpret_cast<void*>(&Hook_recv), &dummy);

    PatchIAT(module, "ldCore.dll", "?createNewFrame@ldRendererOpenlase@@QEAAXH_N0@Z",
        reinterpret_cast<void*>(&Hook_RendererCreateNewFrame), reinterpret_cast<void**>(&Real_RendererCreateNewFrame));
    PatchIAT(module, "ldCore.dll", "?vertex@ldRendererOpenlase@@QEAAXMMIH@Z",
        reinterpret_cast<void*>(&Hook_RendererVertex), reinterpret_cast<void**>(&Real_RendererVertex));
    PatchIAT(module, "ldCore.dll", "?vertex3@ldRendererOpenlase@@QEAAXMMMIH@Z",
        reinterpret_cast<void*>(&Hook_RendererVertex3), reinterpret_cast<void**>(&Real_RendererVertex3));
}

static void PatchAllModules() {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());
    if (snap == INVALID_HANDLE_VALUE) { PatchModule(GetModuleHandleW(nullptr)); return; }
    MODULEENTRY32W me{}; me.dwSize = sizeof(me);
    if (Module32FirstW(snap, &me)) do { PatchModule(me.hModule); } while (Module32NextW(snap, &me));
    CloseHandle(snap);
}

static DWORD WINAPI InstallThread(LPVOID) {
    InitializeCriticalSection(&gLock);
    InitializeCriticalSection(&gVirtualLock);
    InitializeCriticalSection(&gRendererLock);
    gLockReady = true;
    gVirtualLockReady = true;
    gRendererLockReady = true;

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
    Real_connect = reinterpret_cast<connect_t>(GetProcAddress(ws2, "connect"));
    Real_WSAConnect = reinterpret_cast<WSAConnect_t>(GetProcAddress(ws2, "WSAConnect"));
    Real_socket = reinterpret_cast<socket_t>(GetProcAddress(ws2, "socket"));
    Real_closesocket = reinterpret_cast<closesocket_t>(GetProcAddress(ws2, "closesocket"));

    LoadRoutingMode();
    for (;;) { PatchAllModules(); Sleep(750); }
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
        if (gRendererLockReady) DeleteCriticalSection(&gRendererLock);
    }
    return TRUE;
}
