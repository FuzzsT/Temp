#pragma once
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

class VirtualLaserCubeNative {
public:
    static constexpr uint16_t BufferSize = 6000;
    static constexpr uint32_t DacRate = 30000;

    std::vector<uint8_t> Handle(const std::vector<uint8_t>& payload) {
        if (payload.empty()) return {};
        switch (payload[0]) {
        case 0x77: return FullInfo();
        case 0x78:
            if (payload.size() >= 2) bufferResponseEnabled_ = payload[1] != 0;
            return {0x78};
        case 0x80:
            if (payload.size() >= 2) outputRequested_ = payload[1] != 0;
            return {0x80};
        case 0x8A:
            return {0x8A, 0x00, static_cast<uint8_t>(BufferSize & 0xFF), static_cast<uint8_t>((BufferSize >> 8) & 0xFF)};
        case 0xA9:
            if (payload.size() >= 4) {
                lastMessageNumber_ = payload[2];
                lastFrameNumber_ = payload[3];
                lastPointCount_ = static_cast<int>((payload.size() - 4) / 10);
            }
            if (bufferResponseEnabled_)
                return {0xA9, static_cast<uint8_t>(BufferSize & 0xFF), static_cast<uint8_t>((BufferSize >> 8) & 0xFF)};
            return {};
        default:
            return {};
        }
    }

    bool BufferResponseEnabled() const { return bufferResponseEnabled_; }
    bool OutputRequested() const { return outputRequested_; }
    bool PhysicalOutputEnabled() const { return false; }
    uint8_t LastMessageNumber() const { return lastMessageNumber_; }
    uint8_t LastFrameNumber() const { return lastFrameNumber_; }
    int LastPointCount() const { return lastPointCount_; }

private:
    static void PutU16(std::vector<uint8_t>& b, size_t off, uint16_t v) {
        b[off] = static_cast<uint8_t>(v & 0xFF);
        b[off + 1] = static_cast<uint8_t>((v >> 8) & 0xFF);
    }
    static void PutU32(std::vector<uint8_t>& b, size_t off, uint32_t v) {
        b[off] = static_cast<uint8_t>(v & 0xFF);
        b[off + 1] = static_cast<uint8_t>((v >> 8) & 0xFF);
        b[off + 2] = static_cast<uint8_t>((v >> 16) & 0xFF);
        b[off + 3] = static_cast<uint8_t>((v >> 24) & 0xFF);
    }

    std::vector<uint8_t> FullInfo() const {
        std::vector<uint8_t> b(64, 0);
        b[0] = 0x77;
        b[3] = 1;
        b[4] = 0;
        b[5] = outputRequested_ ? 1 : 0;
        PutU32(b, 10, DacRate);
        PutU32(b, 14, DacRate);
        PutU16(b, 19, BufferSize);
        PutU16(b, 21, BufferSize);
        b[23] = 100;
        b[24] = 25;
        b[25] = 3; // Wi-Fi-style LaserCube transport
        const uint8_t serial[6] = {0x43, 0x37, 0x56, 0x30, 0x30, 0x33}; // C7V003
        std::memcpy(b.data() + 26, serial, sizeof(serial));
        b[32] = 127; b[33] = 0; b[34] = 0; b[35] = 1;
        b[37] = 7;
        const std::string model = "LaserCube Virtual CUBE7";
        const size_t n = std::min<size_t>(model.size(), 25);
        std::memcpy(b.data() + 38, model.data(), n);
        b[38 + n] = 0;
        return b;
    }

    bool bufferResponseEnabled_ = false;
    bool outputRequested_ = false;
    uint8_t lastMessageNumber_ = 0;
    uint8_t lastFrameNumber_ = 0;
    int lastPointCount_ = 0;
};
