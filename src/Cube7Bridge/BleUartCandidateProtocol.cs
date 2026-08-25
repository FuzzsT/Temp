using System.Text.Json;

namespace Cube7Bridge;

public sealed record BleUartCandidateProfile(
    string Name,
    string Confidence,
    bool TransmitEnabled,
    string Service,
    string NotifyCharacteristic,
    string WriteCharacteristic,
    int NegotiatedMtu,
    int MaxAttPayload,
    string ChecksumModel,
    string Notes);

public sealed record BleUartCandidatePacket(byte Command, byte[] Payload, byte Checksum, bool ChecksumValid);

public static class BleUartCandidateProtocol
{
    public const byte StartByte = 0xAA;

    public static BleUartCandidateProfile Profile { get; } = new(
        Name: "community-aa-sum-v1-candidate",
        Confidence: "UNVERIFIED",
        TransmitEnabled: false,
        Service: "0000ffe0-0000-1000-8000-00805f9b34fb",
        NotifyCharacteristic: "0000ffe1-0000-1000-8000-00805f9b34fb",
        WriteCharacteristic: "0000ffe2-0000-1000-8000-00805f9b34fb",
        NegotiatedMtu: 247,
        MaxAttPayload: 244,
        ChecksumModel: "sum-mod-256",
        Notes: "Packet layout and command IDs are candidate-only. No FFE2 transmission is enabled by this profile.");

    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        int sum = 0;
        foreach (byte b in bytes) sum = (sum + b) & 0xFF;
        return (byte)sum;
    }

    public static byte[] BuildPacket(byte command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 255) throw new ArgumentOutOfRangeException(nameof(payload), "Candidate AA/CMD/LEN profile supports payloads up to 255 bytes.");
        var packet = new byte[4 + payload.Length];
        packet[0] = StartByte;
        packet[1] = command;
        packet[2] = (byte)payload.Length;
        payload.CopyTo(packet.AsSpan(3, payload.Length));
        packet[^1] = Checksum(packet.AsSpan(0, packet.Length - 1));
        return packet;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out BleUartCandidatePacket? packet)
    {
        packet = null;
        if (data.Length < 4 || data[0] != StartByte) return false;
        int payloadLength = data[2];
        int expected = 4 + payloadLength;
        if (data.Length != expected) return false;
        var payload = data.Slice(3, payloadLength).ToArray();
        byte actual = data[^1];
        byte expectedChecksum = Checksum(data[..^1]);
        packet = new BleUartCandidatePacket(data[1], payload, actual, actual == expectedChecksum);
        return true;
    }
}

public sealed class BleUartTraceWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _sync = new();
    public string Path { get; }

    public BleUartTraceWriter(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "ble-protocol-trace.ndjson");
        _writer = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void ObserveNotification(ReadOnlySpan<byte> data)
    {
        object row;
        if (data.Length == BleUartCandidateProtocol.Profile.MaxAttPayload && IsAllZero(data))
        {
            row = new
            {
                timeUtc = DateTime.UtcNow,
                direction = "notify",
                classification = "idle-zero-buffer",
                length = data.Length,
                profile = BleUartCandidateProtocol.Profile.Name,
                confidence = BleUartCandidateProtocol.Profile.Confidence
            };
        }
        else if (BleUartCandidateProtocol.TryParse(data, out var packet) && packet is not null)
        {
            row = new
            {
                timeUtc = DateTime.UtcNow,
                direction = "notify",
                classification = "aa-candidate-packet",
                length = data.Length,
                command = $"0x{packet.Command:X2}",
                payloadLength = packet.Payload.Length,
                checksum = $"0x{packet.Checksum:X2}",
                checksumValid = packet.ChecksumValid,
                payloadHex = Convert.ToHexString(packet.Payload),
                profile = BleUartCandidateProtocol.Profile.Name,
                confidence = BleUartCandidateProtocol.Profile.Confidence
            };
        }
        else
        {
            row = new
            {
                timeUtc = DateTime.UtcNow,
                direction = "notify",
                classification = "unknown",
                length = data.Length,
                dataHex = Convert.ToHexString(data),
                profile = BleUartCandidateProtocol.Profile.Name,
                confidence = BleUartCandidateProtocol.Profile.Confidence
            };
        }

        lock (_sync) _writer.WriteLine(JsonSerializer.Serialize(row));
    }

    public void WriteCandidate(string label, byte command, ReadOnlySpan<byte> payload)
    {
        byte[] packet = BleUartCandidateProtocol.BuildPacket(command, payload);
        var row = new
        {
            timeUtc = DateTime.UtcNow,
            direction = "dry-run-candidate",
            label,
            command = $"0x{command:X2}",
            packetHex = Convert.ToHexString(packet),
            transmit = false,
            confidence = BleUartCandidateProtocol.Profile.Confidence,
            profile = BleUartCandidateProtocol.Profile.Name
        };
        lock (_sync) _writer.WriteLine(JsonSerializer.Serialize(row));
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) if (b != 0) return false;
        return true;
    }

    public void Dispose() => _writer.Dispose();
}
