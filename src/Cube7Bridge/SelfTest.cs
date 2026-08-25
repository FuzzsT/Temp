using System.Buffers.Binary;

namespace Cube7Bridge;

public static class SelfTest
{
    public static Task<int> RunAsync()
    {
        try
        {
            var cfg = new VirtualLaserCubeConfig { BufferSize = 6000, DacRate = 30000, MaxDacRate = 30000, ModelName = "LaserCube Virtual CUBE7" };
            var state = new VirtualLaserCubeState { BufferFree = cfg.BufferSize };

            var info = VirtualLaserCubeProtocol.BuildFullInfo(cfg, state);
            Require(info.Length == 64, "0x77 response length");
            Require(info[0] == 0x77, "0x77 opcode");
            Require(BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(10, 4)) == 30000, "DAC rate");
            Require(BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(21, 2)) == 6000, "buffer size");

            var r78 = VirtualLaserCubeProtocol.HandleCommand([0x78, 0x01], state, cfg);
            Require(r78.Response is [0x78], "0x78 ack");
            Require(state.BufferResponseEnabled, "buffer responses enabled");

            var r8a = VirtualLaserCubeProtocol.HandleCommand([0x8A], state, cfg);
            Require(r8a.Response is { Length: 4 } && r8a.Response[0] == 0x8A, "0x8A response");
            Require(BinaryPrimitives.ReadUInt16LittleEndian(r8a.Response.AsSpan(2, 2)) == 6000, "0x8A free buffer");

            var r80 = VirtualLaserCubeProtocol.HandleCommand([0x80, 0x01], state, cfg);
            Require(r80.Response is [0x80], "0x80 ack");
            Require(state.OutputRequested, "virtual output request tracked");
            Require(!state.PhysicalOutputEnabled, "physical output remains disabled");

            var sample = new byte[24];
            sample[0] = 0xA9; sample[1] = 0x00; sample[2] = 0x12; sample[3] = 0x34;
            var ra9 = VirtualLaserCubeProtocol.HandleCommand(sample, state, cfg);
            Require(ra9.PointCount == 2, "0xA9 point count");
            Require(ra9.Response is { Length: 3 } && ra9.Response[0] == 0xA9, "0xA9 buffer response");
            Require(state.LastMessageNumber == 0x12 && state.LastFrameNumber == 0x34, "0xA9 sequence tracking");

            Console.WriteLine("SELFTEST PASS: protocol model; physical-output=DISABLED");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SELFTEST FAIL: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
