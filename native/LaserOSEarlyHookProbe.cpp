#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <cstdint>
#include <iostream>

#pragma comment(lib, "Ws2_32.lib")

static void WritePassMarker() {
    wchar_t path[32768]{};
    DWORD n = GetEnvironmentVariableW(L"CUBE7_EARLYHOOK_PASSFILE", path, static_cast<DWORD>(std::size(path)));
    if (!n || n >= std::size(path)) return;
    HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    const char text[] = "PASS\r\n";
    DWORD written = 0;
    WriteFile(h, text, static_cast<DWORD>(sizeof(text) - 1), &written, nullptr);
    CloseHandle(h);
}

int wmain() {
    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return 10;

    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) { WSACleanup(); return 11; }

    DWORD timeoutMs = 2500;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));

    sockaddr_in target{};
    target.sin_family = AF_INET;
    target.sin_port = htons(45457);
    inet_pton(AF_INET, "127.0.0.1", &target.sin_addr);

    const char query[1] = { static_cast<char>(0x77) };
    int sent = sendto(s, query, 1, 0, reinterpret_cast<const sockaddr*>(&target), sizeof(target));
    if (sent != 1) {
        std::cerr << "EARLYHOOK_PROBE FAIL: sendto=" << sent << " err=" << WSAGetLastError() << "\n";
        closesocket(s); WSACleanup(); return 12;
    }

    uint8_t response[256]{};
    sockaddr_in from{};
    int fromLen = sizeof(from);
    int got = recvfrom(s, reinterpret_cast<char*>(response), sizeof(response), 0, reinterpret_cast<sockaddr*>(&from), &fromLen);
    int recvErr = got == SOCKET_ERROR ? WSAGetLastError() : 0;

    closesocket(s);
    WSACleanup();

    if (got != 64) {
        std::cerr << "EARLYHOOK_PROBE FAIL: expected 64-byte 0x77 response, got=" << got << " err=" << recvErr << "\n";
        return 13;
    }
    if (response[0] != 0x77) {
        std::cerr << "EARLYHOOK_PROBE FAIL: opcode=" << std::hex << static_cast<int>(response[0]) << "\n";
        return 14;
    }
    if (response[21] == 0 && response[22] == 0) {
        std::cerr << "EARLYHOOK_PROBE FAIL: zero buffer size\n";
        return 15;
    }

    WritePassMarker();
    std::cout << "EARLYHOOK_PROBE PASS: virtual LaserCube discovered before target startup\n";
    return 0;
}
