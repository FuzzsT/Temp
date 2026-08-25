#pragma once
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <cstdint>

static constexpr uint32_t C7HK_MAGIC = 0x4B483743; // 'C7HK' little-endian
static constexpr uint16_t C7HK_VERSION = 1;
static constexpr wchar_t C7HK_PIPE[] = L"\\\\.\\pipe\\Cube7LaserOSBridge";
static constexpr wchar_t C7HK_VIRTUAL_MARKER[] = L"VirtualLaserCube.enabled";

enum class HookDirection : uint8_t { Tx = 1, Rx = 2 };
enum class HookApi : uint8_t {
    SendTo = 1,
    RecvFrom = 2,
    WSASend = 3,
    WSARecv = 4,
    Send = 5,
    Recv = 6,
    WSASendTo = 7,
    WSARecvFrom = 8
};

#pragma pack(push, 1)
struct HookRecordHeader {
    uint32_t magic;
    uint16_t version;
    uint8_t direction;
    uint8_t api;
    uint32_t pid;
    uint64_t timestamp100ns;
    uint16_t addressFamily;
    uint16_t port;
    uint8_t address[16];
    uint32_t payloadLength;
};
#pragma pack(pop)

static_assert(sizeof(HookRecordHeader) == 44, "HookRecordHeader size mismatch");
