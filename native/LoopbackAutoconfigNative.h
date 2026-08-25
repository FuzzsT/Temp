#pragma once

#include <winsock2.h>
#include <ws2tcpip.h>
#include <cstdint>
#include <cstring>
#include <sstream>
#include <string>

struct LoopbackRouteConfig {
    bool enabled = false;
    bool rewriteDestinations = false;
    bool rewriteClientBinds = false;
    uint16_t alivePort = 45456;
    uint16_t commandPort = 45457;
    uint16_t dataPort = 45458;
};

inline bool LoopbackParseBool(const std::string& value) {
    return value == "1" || value == "true" || value == "TRUE" || value == "yes" || value == "YES";
}

inline bool LoopbackParsePort(const std::string& value, uint16_t& out) {
    try {
        const int n = std::stoi(value);
        if (n < 1 || n > 65535) return false;
        out = static_cast<uint16_t>(n);
        return true;
    } catch (...) {
        return false;
    }
}

inline bool ParseLoopbackRouteConfig(const std::string& text, LoopbackRouteConfig& out) {
    LoopbackRouteConfig cfg{};
    bool modeLoopback = false;
    bool addressLoopback = false;

    std::istringstream input(text);
    std::string line;
    while (std::getline(input, line)) {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        const auto eq = line.find('=');
        if (eq == std::string::npos) continue;
        const std::string key = line.substr(0, eq);
        const std::string value = line.substr(eq + 1);
        if (key == "mode") modeLoopback = value == "loopback";
        else if (key == "enabled") cfg.enabled = LoopbackParseBool(value);
        else if (key == "address") addressLoopback = value == "127.0.0.1";
        else if (key == "alivePort") { if (!LoopbackParsePort(value, cfg.alivePort)) return false; }
        else if (key == "commandPort") { if (!LoopbackParsePort(value, cfg.commandPort)) return false; }
        else if (key == "dataPort") { if (!LoopbackParsePort(value, cfg.dataPort)) return false; }
        else if (key == "rewriteDestinations") cfg.rewriteDestinations = LoopbackParseBool(value);
        else if (key == "rewriteClientBinds") cfg.rewriteClientBinds = LoopbackParseBool(value);
        else if (key == "physicalOutput" && LoopbackParseBool(value)) return false;
        else if (key == "syntheticAuthentication" && LoopbackParseBool(value)) return false;
    }

    if (!modeLoopback || !addressLoopback || !cfg.enabled) return false;
    out = cfg;
    return true;
}

inline bool IsLaserCubeProtocolPort(uint16_t port, const LoopbackRouteConfig& cfg) {
    return port == cfg.alivePort || port == cfg.commandPort || port == cfg.dataPort;
}

inline bool RewriteLaserCubeDestination(
    const sockaddr* source,
    int sourceLen,
    const LoopbackRouteConfig& cfg,
    sockaddr_storage& rewritten,
    int& rewrittenLen) {
    if (!cfg.enabled || !cfg.rewriteDestinations || !source || sourceLen < static_cast<int>(sizeof(sockaddr_in))) return false;
    if (source->sa_family != AF_INET) return false;
    const auto* ipv4 = reinterpret_cast<const sockaddr_in*>(source);
    const uint16_t port = ntohs(ipv4->sin_port);
    if (!IsLaserCubeProtocolPort(port, cfg)) return false;

    ZeroMemory(&rewritten, sizeof(rewritten));
    auto* dst = reinterpret_cast<sockaddr_in*>(&rewritten);
    *dst = *ipv4;
    dst->sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    rewrittenLen = sizeof(sockaddr_in);
    return true;
}

inline bool RewriteLaserCubeClientBind(
    const sockaddr* source,
    int sourceLen,
    const LoopbackRouteConfig& cfg,
    sockaddr_storage& rewritten,
    int& rewrittenLen) {
    if (!cfg.enabled || !cfg.rewriteClientBinds || !source || sourceLen < static_cast<int>(sizeof(sockaddr_in))) return false;
    if (source->sa_family != AF_INET) return false;
    const auto* ipv4 = reinterpret_cast<const sockaddr_in*>(source);
    const uint16_t port = ntohs(ipv4->sin_port);
    if (!IsLaserCubeProtocolPort(port, cfg)) return false;

    ZeroMemory(&rewritten, sizeof(rewritten));
    auto* dst = reinterpret_cast<sockaddr_in*>(&rewritten);
    *dst = *ipv4;
    dst->sin_port = 0; // let Windows choose an ephemeral client port; server owns 45456/57/58
    rewrittenLen = sizeof(sockaddr_in);
    return true;
}
