using System.Buffers.Binary;
using System.Text;

namespace Cube7Bridge;

public sealed class VirtualLaserCubeConfig
{
    public bool Enabled { get; set; } = true;
    // The external UDP responder is diagnostic/test-only on a machine running LaserOS,
    // because LaserOS itself may bind the LaserCube command port. The injected hook is primary.
    public bool NetworkServerEnabled { get; set; } = false;
    public string BindAddress { get; set; } = "0.0.0.0";
    public int AlivePort { get; set; } = 45456;
    public int CommandPort { get; set; } = 45457;
    public int DataPort { get; set; } = 45458;
    public byte FirmwareMajor { get; set; } = 1;
    public byte FirmwareMinor { get; set; } = 0;
    public int DacRate { get; set; } = 30000;
    public int MaxDacRate { get; set; } = 30000;
    public int BufferSize { get; set; } = 6000;
    public byte ModelNumber { get; set; } = 7;
    public string ModelName { get; set; } = "LaserCube Virtual CUBE7";
}

public sealed class VirtualLaserCubeState
{
    public bool BufferResponseEnabled { get; set; }
    public bool OutputRequested { get; set; }
    public bool PhysicalOutputEnabled => false;
    public int BufferFree { get; set; }
    public byte LastMessageNumber { get; set; }
    public byte LastFrameNumber { get; set; }
    public int LastPointCount { get; set; }
}

public sealed record VirtualLaserCubeReply(byte[]? Response, string Kind, int PointCount = 0);

public static class VirtualLaserCubeProtocol
{
    private static readonly byte[] Serial = [0x43, 0x37, 0x56, 0x30, 0x30, 0x33]; // C7V003

    public static byte[] BuildFullInfo(VirtualLaserCubeConfig cfg, VirtualLaserCubeState state)
    {
        var response = new byte[64];
        response[0] = 0x77;
        response[1] = 0x00; // command result: success
        response[2] = 0x00; // full-info payload version used by libLaserdockCore
        response[3] = cfg.FirmwareMajor;
        response[4] = cfg.FirmwareMinor;
        response[5] = state.OutputRequested ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(10, 4), checked((uint)Math.Max(0, cfg.DacRate)));
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(14, 4), checked((uint)Math.Max(0, cfg.MaxDacRate)));
        ushort bufferFree = checked((ushort)Math.Clamp(state.BufferFree, 0, ushort.MaxValue));
        ushort bufferSize = checked((ushort)Math.Clamp(cfg.BufferSize, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(19, 2), bufferFree);
        BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(21, 2), bufferSize);
        response[23] = 100;
        response[24] = 25;
        response[25] = 3;
        Serial.CopyTo(response, 26);
        response[32] = 127;
        response[33] = 0;
        response[34] = 0;
        response[35] = 1;
        response[37] = cfg.ModelNumber;

        var modelBytes = Encoding.ASCII.GetBytes(cfg.ModelName ?? string.Empty);
        int modelLength = Math.Min(modelBytes.Length, 25);
        Array.Copy(modelBytes, 0, response, 38, modelLength);
        response[38 + modelLength] = 0;
        return response;
    }

    public static VirtualLaserCubeReply HandleCommand(ReadOnlySpan<byte> payload, VirtualLaserCubeState state, VirtualLaserCubeConfig cfg)
    {
        if (payload.IsEmpty) return new VirtualLaserCubeReply(null, "empty");
        if (state.BufferFree <= 0 || state.BufferFree > cfg.BufferSize) state.BufferFree = cfg.BufferSize;

        switch (payload[0])
        {
            // libLaserdockCore discovery: request is one byte 0x27 on alive port;
            // response must be exactly 27 00 before LaserOS constructs a network device.
            case 0x27:
                return new VirtualLaserCubeReply([0x27, 0x00], "get_alive");
            case 0x77:
                return new VirtualLaserCubeReply(BuildFullInfo(cfg, state), "get_full_info");
            case 0x78:
                if (payload.Length >= 2) state.BufferResponseEnabled = payload[1] != 0;
                return new VirtualLaserCubeReply([0x78], "set_buffer_response");
            case 0x8A:
            {
                ushort free = checked((ushort)Math.Clamp(state.BufferFree, 0, ushort.MaxValue));
                return new VirtualLaserCubeReply([0x8A, 0x00, (byte)(free & 0xFF), (byte)(free >> 8)], "get_buffer_free");
            }
            case 0x80:
                if (payload.Length >= 2) state.OutputRequested = payload[1] != 0;
                return new VirtualLaserCubeReply([0x80], "set_output_virtual_only");
            case 0xA9:
            {
                if (payload.Length < 4) return new VirtualLaserCubeReply(null, "sample_data_short");
                state.LastMessageNumber = payload[2];
                state.LastFrameNumber = payload[3];
                state.LastPointCount = Math.Max(0, (payload.Length - 4) / 10);
                state.BufferFree = cfg.BufferSize;
                if (!state.BufferResponseEnabled)
                    return new VirtualLaserCubeReply(null, "sample_data", state.LastPointCount);
                ushort free = checked((ushort)Math.Clamp(state.BufferFree, 0, ushort.MaxValue));
                return new VirtualLaserCubeReply([0xA9, (byte)(free & 0xFF), (byte)(free >> 8)], "sample_data", state.LastPointCount);
            }
            default:
                return new VirtualLaserCubeReply(null, $"unknown_0x{payload[0]:X2}");
        }
    }
}
