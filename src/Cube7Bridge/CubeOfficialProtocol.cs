using System.Buffers.Binary;
using System.Text;

namespace Cube7Bridge;

public sealed record CubeDataAck(byte Command, ushort Address, byte Status)
{
    public bool IsSuccess => Status is 0 or 2 or 3;
}

/// <summary>
/// Pure serializer/parser for the official Cube application transport envelope
/// recovered and regression-tested in LaserCube AIO v26.
///
/// This class performs no BLE I/O, no encryption, and no optical-output state
/// changes. It only builds plaintext AD/A5 frames and parses 0x85 ACKs.
/// </summary>
public static class CubeOfficialProtocol
{
    public const byte CmdData = 0xAD;
    public const byte CmdIncremental = 0xA5;
    public const byte AckData = 0x85;
    public const ushort Address = 0x1234;
    public const byte FunctionRealTimePlay = 2;
    public const byte ActionPlayStart = 2;

    public static byte[][] BuildPatternTransferFrames(
        byte[] layerPayload,
        int dataFormatType,
        int bufferMax,
        string timestamp,
        byte runArg)
    {
        ArgumentNullException.ThrowIfNull(layerPayload);
        ValidateDataFormatType(dataFormatType);
        if (bufferMax <= 47) throw new ArgumentOutOfRangeException(nameof(bufferMax), "device bufferMax must be > 47");
        ValidateTimestamp(timestamp);

        return BuildTransferFrames(
            FunctionRealTimePlay,
            ActionPlayStart,
            dataFormatType,
            timestamp,
            layerCount: 1,
            runArg,
            layerPayload,
            bufferMax);
    }

    public static CubeDataAck ParseDataAck(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length < 4) throw new InvalidDataException("ACK is shorter than 4 bytes");
        if (message[0] != AckData)
            throw new InvalidDataException($"unexpected ACK command 0x{message[0]:X2}; expected 0x{AckData:X2}");
        ushort address = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(1, 2));
        return new CubeDataAck(message[0], address, message[3]);
    }

    private static byte[][] BuildTransferFrames(
        byte function,
        byte action,
        int dataFormatType,
        string timestamp,
        byte layerCount,
        byte runArg,
        byte[] data,
        int bufferMax)
    {
        int firstCap = bufferMax - 47;
        int nextCap = bufferMax - 5;

        var frames = new List<byte[]>();
        int firstLen = Math.Min(firstCap, data.Length);
        frames.Add(BuildMainFrame(
            function,
            action,
            dataFormatType,
            timestamp,
            layerCount,
            runArg,
            data.AsSpan(0, firstLen),
            data.Length));

        int pos = firstLen;
        while (pos < data.Length)
        {
            int len = Math.Min(nextCap, data.Length - pos);
            frames.Add(BuildIncrementalFrame(data.AsSpan(pos, len)));
            pos += len;
        }
        return frames.ToArray();
    }

    private static byte[] BuildMainFrame(
        byte function,
        byte action,
        int dataFormatType,
        string timestamp,
        byte layerCount,
        byte runArg,
        ReadOnlySpan<byte> chunk,
        int totalDataLength)
    {
        const int payloadFixedLength = 42;
        int payloadLength = payloadFixedLength + chunk.Length;
        var frame = new byte[5 + payloadLength];
        frame[0] = CmdData;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1, 2), Address);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3, 2), checked((ushort)payloadLength));

        int off = 5;
        frame[off++] = function;
        frame[off++] = action;
        frame[off++] = 0; // option
        frame[off++] = checked((byte)dataFormatType);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(off, 4), checked((uint)totalDataLength));
        off += 4;
        frame[off++] = layerCount;
        frame[off++] = runArg;

        WriteAsciiPadded(frame.AsSpan(off, 30), "A" + timestamp);
        off += 30;
        frame[off++] = 0; // reserved BE16
        frame[off++] = 0;
        chunk.CopyTo(frame.AsSpan(off));
        return frame;
    }

    private static byte[] BuildIncrementalFrame(ReadOnlySpan<byte> data)
    {
        var frame = new byte[5 + data.Length];
        frame[0] = CmdIncremental;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1, 2), Address);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3, 2), checked((ushort)data.Length));
        data.CopyTo(frame.AsSpan(5));
        return frame;
    }

    private static void WriteAsciiPadded(Span<byte> destination, string value)
    {
        destination.Clear();
        var bytes = Encoding.Latin1.GetBytes(value);
        if (bytes.Length > destination.Length)
            throw new ArgumentException($"string too long for {destination.Length}-byte field", nameof(value));
        bytes.CopyTo(destination);
    }

    private static void ValidateTimestamp(string timestamp)
    {
        if (timestamp.Length != 14 || timestamp.Any(c => c < '0' || c > '9'))
            throw new ArgumentException("timestamp must be yyyyMMddHHmmss / 14 digits", nameof(timestamp));
    }

    private static void ValidateDataFormatType(int dataFormatType)
    {
        if (dataFormatType is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(dataFormatType), "unsupported official dataFormatType");
    }
}
