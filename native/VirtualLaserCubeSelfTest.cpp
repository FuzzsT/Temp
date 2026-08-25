#include <cassert>
#include <cstdint>
#include <vector>
#include <iostream>
#include "VirtualLaserCubeNative.h"

int main() {
    VirtualLaserCubeNative state;

    auto info = state.Handle({0x77});
    assert(info.size() == 64);
    assert(info[0] == 0x77);
    assert(info[21] == 0x70 && info[22] == 0x17); // 6000 LE

    auto enableBuffer = state.Handle({0x78, 0x01});
    assert(enableBuffer.size() == 1 && enableBuffer[0] == 0x78);

    auto buffer = state.Handle({0x8A});
    assert(buffer.size() == 4 && buffer[0] == 0x8A);
    assert(buffer[2] == 0x70 && buffer[3] == 0x17);

    auto output = state.Handle({0x80, 0x01});
    assert(output.size() == 1 && output[0] == 0x80);
    assert(state.OutputRequested());
    assert(!state.PhysicalOutputEnabled());

    std::vector<uint8_t> sample(14, 0);
    sample[0] = 0xA9;
    sample[2] = 0x12;
    sample[3] = 0x34;
    auto sampleResponse = state.Handle(sample);
    assert(sampleResponse.size() == 3 && sampleResponse[0] == 0xA9);
    assert(state.LastPointCount() == 1);

    std::cout << "NATIVE SELFTEST PASS: physical-output=DISABLED\n";
    return 0;
}
