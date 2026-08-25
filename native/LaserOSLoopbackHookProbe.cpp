#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <cstdio>
#include <cstring>

#pragma comment(lib, "Ws2_32.lib")

using socket_fn = SOCKET (WSAAPI*)(int, int, int);
using bind_fn = int (WSAAPI*)(SOCKET, const sockaddr*, int);
using recvfrom_fn = int (WSAAPI*)(SOCKET, char*, int, int, sockaddr*, int*);
using closesocket_fn = int (WSAAPI*)(SOCKET);

static int Fail(const char* message) {
    std::fprintf(stderr, "LOOPBACK HOOK PROBE FAIL: %s WSA=%d\n", message, WSAGetLastError());
    return 1;
}

static bool WritePassFile() {
    char path[32768]{};
    DWORD n = GetEnvironmentVariableA("CUBE7_LOOPBACK_PASSFILE", path, static_cast<DWORD>(sizeof(path)));
    if (!n || n >= sizeof(path)) return false;
    HANDLE h = CreateFileA(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    const char ok[] = "loopback-hook-pass\n";
    DWORD written = 0;
    BOOL result = WriteFile(h, ok, static_cast<DWORD>(sizeof(ok) - 1), &written, nullptr);
    CloseHandle(h);
    return result && written == sizeof(ok) - 1;
}

int main() {
    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return Fail("WSAStartup");

    // Give the injected DLL time to install IAT hooks after the suspended launch resumes.
    Sleep(1600);

    HMODULE ws2 = GetModuleHandleW(L"Ws2_32.dll");
    if (!ws2) ws2 = LoadLibraryW(L"Ws2_32.dll");
    auto Real_socket = reinterpret_cast<socket_fn>(GetProcAddress(ws2, "socket"));
    auto Real_bind = reinterpret_cast<bind_fn>(GetProcAddress(ws2, "bind"));
    auto Real_recvfrom = reinterpret_cast<recvfrom_fn>(GetProcAddress(ws2, "recvfrom"));
    auto Real_closesocket = reinterpret_cast<closesocket_fn>(GetProcAddress(ws2, "closesocket"));
    if (!Real_socket || !Real_bind || !Real_recvfrom || !Real_closesocket) return Fail("real Winsock exports");

    // Server sockets use dynamically resolved exports so they are not rewritten by the test hook.
    SOCKET commandServer = Real_socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    SOCKET dataServer = Real_socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (commandServer == INVALID_SOCKET || dataServer == INVALID_SOCKET) return Fail("server socket");

    BOOL exclusive = TRUE;
    setsockopt(commandServer, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));
    setsockopt(dataServer, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive));

    sockaddr_in commandAddr{};
    commandAddr.sin_family = AF_INET;
    commandAddr.sin_port = htons(45457);
    commandAddr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (Real_bind(commandServer, reinterpret_cast<const sockaddr*>(&commandAddr), sizeof(commandAddr)) != 0)
        return Fail("real command server bind");

    sockaddr_in dataAddr{};
    dataAddr.sin_family = AF_INET;
    dataAddr.sin_port = htons(45458);
    dataAddr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (Real_bind(dataServer, reinterpret_cast<const sockaddr*>(&dataAddr), sizeof(dataAddr)) != 0)
        return Fail("real data server bind");

    DWORD timeout = 2500;
    setsockopt(commandServer, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));

    // This is the simulated LaserOS client path and must go through imported Winsock calls.
    SOCKET client = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (client == INVALID_SOCKET) return Fail("client socket");

    BOOL broadcast = TRUE;
    setsockopt(client, SOL_SOCKET, SO_BROADCAST, reinterpret_cast<const char*>(&broadcast), sizeof(broadcast));

    sockaddr_in requestedLocal{};
    requestedLocal.sin_family = AF_INET;
    requestedLocal.sin_port = htons(45458);
    requestedLocal.sin_addr.s_addr = htonl(INADDR_ANY);
    if (bind(client, reinterpret_cast<const sockaddr*>(&requestedLocal), sizeof(requestedLocal)) != 0)
        return Fail("hooked client bind should be remapped to ephemeral");

    sockaddr_in actualLocal{};
    int actualLocalLen = sizeof(actualLocal);
    if (getsockname(client, reinterpret_cast<sockaddr*>(&actualLocal), &actualLocalLen) != 0)
        return Fail("getsockname client");
    if (ntohs(actualLocal.sin_port) == 45458 || actualLocal.sin_port == 0)
        return Fail("client bind was not remapped to a real ephemeral port");

    sockaddr_in broadcastCommand{};
    broadcastCommand.sin_family = AF_INET;
    broadcastCommand.sin_port = htons(45457);
    broadcastCommand.sin_addr.s_addr = htonl(INADDR_BROADCAST);
    const unsigned char request[] = {0x77};
    if (sendto(client, reinterpret_cast<const char*>(request), sizeof(request), 0,
               reinterpret_cast<const sockaddr*>(&broadcastCommand), sizeof(broadcastCommand)) != sizeof(request))
        return Fail("hooked sendto");

    unsigned char received[32]{};
    sockaddr_in peer{};
    int peerLen = sizeof(peer);
    int n = Real_recvfrom(commandServer, reinterpret_cast<char*>(received), sizeof(received), 0,
                          reinterpret_cast<sockaddr*>(&peer), &peerLen);
    if (n != 1 || received[0] != 0x77)
        return Fail("loopback server did not receive rewritten command datagram");

    closesocket(client);
    Real_closesocket(commandServer);
    Real_closesocket(dataServer);
    WSACleanup();

    if (!WritePassFile()) return Fail("pass file");
    std::puts("LOOPBACK HOOK PROBE PASS: real localhost server + injected destination/bind routing");
    return 0;
}
