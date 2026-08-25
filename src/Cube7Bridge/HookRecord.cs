using System.Buffers.Binary;
using System.Net;

namespace Cube7Bridge;

public sealed record HookRecord(
    byte Direction,
    byte Api,
    uint Pid,
    ulong Timestamp100ns,
    ushort AddressFamily,
    ushort Port,
    byte[] Address,
    byte[] Payload)
{
    public const uint Magic = 0x4B483743;
    public const int HeaderSize = 44;

    public string RemoteAddress
    {
        get
        {
            try
            {
                if (AddressFamily == 2) return new IPAddress(Address.AsSpan(0, 4)).ToString();
                if (AddressFamily == 23) return new IPAddress(Address.AsSpan(0, 16)).ToString();
            }
            catch { }
            return "?";
        }
    }

    public static HookRecord Parse(ReadOnlySpan<byte> h, byte[] payload)
    {
        if (h.Length != HeaderSize) throw new InvalidDataException("Invalid hook header length.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(h[0..4]) != Magic) throw new InvalidDataException("Bad hook magic.");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(h[4..6]);
        if (version != 1) throw new InvalidDataException($"Unsupported hook version {version}.");
        var direction = h[6];
        var api = h[7];
        var pid = BinaryPrimitives.ReadUInt32LittleEndian(h[8..12]);
        var ts = BinaryPrimitives.ReadUInt64LittleEndian(h[12..20]);
        var af = BinaryPrimitives.ReadUInt16LittleEndian(h[20..22]);
        var port = BinaryPrimitives.ReadUInt16LittleEndian(h[22..24]);
        var addr = h[24..40].ToArray();
        var length = BinaryPrimitives.ReadUInt32LittleEndian(h[40..44]);
        if (length != payload.Length) throw new InvalidDataException("Payload length mismatch.");
        return new HookRecord(direction, api, pid, ts, af, port, addr, payload);
    }
}
