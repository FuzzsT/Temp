using System.Buffers.Binary;

namespace Cube7Bridge;

public static class LaserCubeDecoder
{
    public static object Decode(HookRecord r)
    {
        var p = r.Payload;
        if (p.Length == 0) return new { kind = "empty" };
        byte op = p[0];

        if (r.Direction == 1)
        {
            return op switch
            {
                0x77 => new { kind = "command", opcode = "0x77", name = "GET_FULL_INFO", length = p.Length },
                0x78 => new { kind = "command", opcode = "0x78", name = "ENABLE_BUFFER_SIZE_RESPONSE_ON_DATA", length = p.Length },
                0x80 => new { kind = "command", opcode = "0x80", name = "SET_OUTPUT", enabled = p.Length > 1 ? p[1] : (byte)0, length = p.Length },
                0x8A => new { kind = "command", opcode = "0x8A", name = "GET_RINGBUFFER_EMPTY_SAMPLE_COUNT", length = p.Length },
                0xA9 => DecodeSamples(p),
                _ => new { kind = "unknown_tx", opcode = $"0x{op:X2}", length = p.Length }
            };
        }

        // Best-effort status decoding. Exact layouts vary by firmware; raw payload is always preserved separately.
        if (p.Length >= 38)
        {
            uint ReadU32(int off) => off + 4 <= p.Length ? BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(off, 4)) : 0;
            ushort ReadU16(int off) => off + 2 <= p.Length ? BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off, 2)) : (ushort)0;
            return new
            {
                kind = "rx_status_candidate",
                length = p.Length,
                firmwareMajor = p.Length > 3 ? p[3] : (byte)0,
                firmwareMinor = p.Length > 4 ? p[4] : (byte)0,
                outputEnabled = p.Length > 5 ? p[5] : (byte)0,
                dacRate = ReadU32(10),
                maxDacRate = ReadU32(14),
                rxBufferFree = ReadU16(19),
                rxBufferSize = ReadU16(21),
                battery = p.Length > 23 ? p[23] : (byte)0,
                temperatureRaw = p.Length > 24 ? p[24] : (byte)0,
                connectionType = p.Length > 25 ? p[25] : (byte)0
            };
        }
        return new { kind = "unknown_rx", opcode = $"0x{op:X2}", length = p.Length };
    }

    private static object DecodeSamples(byte[] p)
    {
        if (p.Length < 4) return new { kind = "sample_data", malformed = true, length = p.Length };
        var body = p.AsSpan(4);
        var count = body.Length / 10;
        var preview = new List<object>();
        for (int i = 0; i < Math.Min(count, 8); i++)
        {
            var s = body.Slice(i * 10, 10);
            preview.Add(new
            {
                x = BinaryPrimitives.ReadUInt16LittleEndian(s[0..2]),
                y = BinaryPrimitives.ReadUInt16LittleEndian(s[2..4]),
                r = BinaryPrimitives.ReadUInt16LittleEndian(s[4..6]),
                g = BinaryPrimitives.ReadUInt16LittleEndian(s[6..8]),
                b = BinaryPrimitives.ReadUInt16LittleEndian(s[8..10])
            });
        }
        return new
        {
            kind = "sample_data",
            opcode = "0xA9",
            message = p[2],
            frame = p[3],
            points = count,
            trailingBytes = body.Length % 10,
            preview
        };
    }
}
