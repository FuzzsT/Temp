#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <cstdio>
#include <cstring>
#include "LoopbackAutoconfigNative.h"

static int Fail(const char* message) {
    std::fprintf(stderr, "LOOPBACK SELFTEST FAIL: %s\n", message);
    return 1;
}

int main() {
    LoopbackRouteConfig cfg{};
    const char* marker =
        "mode=loopback\n"
        "enabled=1\n"
        "address=127.0.0.1\n"
        "alivePort=45456\n"
        "commandPort=45457\n"
        "dataPort=45458\n"
        "rewriteDestinations=1\n"
        "rewriteClientBinds=1\n"
        "physicalOutput=0\n"
        "syntheticAuthentication=0\n";

    if (!ParseLoopbackRouteConfig(marker, cfg) || !cfg.enabled) return Fail("profile parse");

    sockaddr_in remote{};
    remote.sin_family = AF_INET;
    remote.sin_port = htons(45457);
    inet_pton(AF_INET, "255.255.255.255", &remote.sin_addr);
    sockaddr_storage rewritten{};
    int rewrittenLen = 0;
    if (!RewriteLaserCubeDestination(reinterpret_cast<const sockaddr*>(&remote), sizeof(remote), cfg, rewritten, rewrittenLen))
        return Fail("destination not rewritten");
    const auto* routed = reinterpret_cast<const sockaddr_in*>(&rewritten);
    if (ntohl(routed->sin_addr.s_addr) != INADDR_LOOPBACK || ntohs(routed->sin_port) != 45457)
        return Fail("destination must be 127.0.0.1:45457");

    sockaddr_in local{};
    local.sin_family = AF_INET;
    local.sin_port = htons(45458);
    local.sin_addr.s_addr = htonl(INADDR_ANY);
    sockaddr_storage rebound{};
    int reboundLen = 0;
    if (!RewriteLaserCubeClientBind(reinterpret_cast<const sockaddr*>(&local), sizeof(local), cfg, rebound, reboundLen))
        return Fail("client bind not rewritten");
    const auto* bound = reinterpret_cast<const sockaddr_in*>(&rebound);
    if (bound->sin_port != 0) return Fail("client bind must use ephemeral port");

    sockaddr_in unrelated{};
    unrelated.sin_family = AF_INET;
    unrelated.sin_port = htons(443);
    inet_pton(AF_INET, "8.8.8.8", &unrelated.sin_addr);
    sockaddr_storage untouched{};
    int untouchedLen = 0;
    if (RewriteLaserCubeDestination(reinterpret_cast<const sockaddr*>(&unrelated), sizeof(unrelated), cfg, untouched, untouchedLen))
        return Fail("unrelated network traffic must not be rewritten");

    std::puts("LOOPBACK SELFTEST PASS: destination rewrite + bind collision avoidance");
    return 0;
}
