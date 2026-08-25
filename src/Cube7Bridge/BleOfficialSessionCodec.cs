using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cube7Bridge;

public sealed record BleCryptoContext(byte[] Key, byte[] Iv)
{
    public BleCryptoContext : this(Key, Iv)
    {
        if (Key is null || Key.Length != 16) throw new ArgumentException("AES-128 key must be 16 bytes", nameof(Key));
        if (Iv is null || Iv.Length != 16) throw new ArgumentException("AES-CTR IV must be 16 bytes", nameof(Iv));
    }
}

public sealed class BleConnectSession
{
    public int Status { get; init; }
    public int Address { get; init; }
    public int BufferMax { get; init; }
    public int ConnectionInterval { get; init; }
    public string FirmwareBle { get; init; } = string.Empty;
    public string FirmwareCpu { get; init; } = string.Empty;
    public int DataFormatType { get; init; }
    public int SceneNumberMax { get; init; }
    public int ActivateType { get; init; }
    public string DeviceKey { get; init; } = string.Empty;
    public string DeviceSecret { get; init; } = string.Empty;
    public string ProductKey { get; init; } = string.Empty;
    public string CommunicationMac { get; init; } = string.Empty;
    public string Mac { get; init; } = string.Empty;
    public long MakerKey { get; init; }
    public int BleHardwareKey { get; init; }
    public int MacHardwareKey { get; init; }
    public int TestFirmware { get; init; }
    public string AppCompany { get; init; } = string.Empty;
    public string Protocols { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
    public byte[] Random { get; init; } = [];
}

public static class BleOfficialSessionCodec
{
    public const byte CmdConnect = 0xAB;
    public const byte ConnectAck = 0x8B;
    public const ushort Address = 0x1234;
    public const string ExpectedAppCompany = "CubeLaserTemeiAI";

    private static readonly BleCryptoContext DefaultContext = new(
        Convert.FromHexString("77663C3A3F65686C52783A3B70495446"),
        Convert.FromHexString("3551657148312A29772D5C6E5F484562"));

    public static byte[] BuildConnectRequest(byte[] random4, uint userKey, byte[] appVersion)
    {
        ArgumentNullException.ThrowIfNull(random4);
        ArgumentNullException.ThrowIfNull(appVersion);
        if (random4.Length != 4) throw new ArgumentException("connect random must be exactly 4 bytes", nameof(random4));

        byte[] version = new byte[3];
        Array.Copy(appVersion, version, Math.Min(3, appVersion.Length));
        byte[] company = AsciiPadded("ChinaTemeiAI", 16);

        using var ms = new MemoryStream();
        WriteBe(ms, userKey, 4);
        WriteBe(ms, 0, 4);
        WriteBe(ms, 3, 4);
        ms.Write(version);
        WriteBe(ms, 0, 1);
        ms.Write(company);
        ms.Write([1, 0, 0]);
        WriteBe(ms, 0, 1);
        ms.Write([0, 0, 0]);
        ms.Write(random4);
        byte[] payload = ms.ToArray();

        byte[] frame = new byte[5 + payload.Length];
        frame[0] = CmdConnect;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1, 2), Address);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3, 2), checked((ushort)payload.Length));
        payload.CopyTo(frame, 5);
        return frame;
    }

    public static byte[] EncryptMessage(byte[] message, BleCryptoContext? context)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length == 0) throw new ArgumentException("message is empty", nameof(message));
        var ctx = context ?? DefaultContext;

        byte head = message[0];
        byte[] body = message.Skip(1).ToArray();
        byte[] tail = [];
        if (body.Length > 240)
        {
            tail = body.Skip(240).ToArray();
            body = body.Take(240).ToArray();
        }

        int paddedLength = body.Length == 0 ? 0 : ((body.Length + 15) / 16) * 16;
        Array.Resize(ref body, paddedLength);
        byte[] encrypted = AesCtr(body, ctx);
        byte[] result = new byte[1 + encrypted.Length + tail.Length];
        result[0] = head;
        encrypted.CopyTo(result, 1);
        tail.CopyTo(result, 1 + encrypted.Length);
        return result;
    }

    public static byte[] DecryptMessage(byte[] message, BleCryptoContext? context)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length == 0) throw new ArgumentException("message is empty", nameof(message));
        var ctx = context ?? DefaultContext;

        byte head = message[0];
        byte[] body = message.Skip(1).ToArray();
        byte[] tail = [];
        if (body.Length > 240)
        {
            tail = body.Skip(240).ToArray();
            body = body.Take(240).ToArray();
        }

        byte[] clear = AesCtr(body, ctx);
        byte[] result = new byte[1 + clear.Length + tail.Length];
        result[0] = head;
        clear.CopyTo(result, 1);
        tail.CopyTo(result, 1 + clear.Length);
        return result;
    }

    public static BleConnectSession ParseConnectResponse(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length < 4) throw new ArgumentException("ACK is shorter than 4 bytes", nameof(message));
        if (message[0] != ConnectAck) throw new ArgumentException($"unexpected ACK command 0x{message[0]:X2}", nameof(message));

        int address = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(1, 2));
        int status = message[3];
        if (status != 0) return new BleConnectSession { Status = status, Address = address };

        int pos = 4;
        ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || pos + count > message.Length) throw new ArgumentException("connect response is truncated", nameof(message));
            var result = message.AsSpan(pos, count);
            pos += count;
            return result;
        }

        int bufferMax = BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        int connectionInterval = BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        string fwBle = string.Join('.', Take(3).ToArray().Select(x => x.ToString()));
        string fwCpu = string.Join('.', Take(3).ToArray().Select(x => x.ToString()));
        int dataFormat = Take(1)[0];
        int sceneMax = Take(1)[0];
        int activateType = Take(1)[0];
        string keyA = DecodeField(Take(32));
        string keyB = DecodeField(Take(32));
        string productKey = DecodeField(Take(32));
        long makerKey = BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        int bleHw = BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        int macHw = BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        int testFw = Take(1)[0];
        int extLen = Take(1)[0];
        int availableExt = Math.Min(extLen, message.Length - pos);
        byte[] ext = Take(availableExt).ToArray();

        int epos = 0;
        ReadOnlySpan<byte> ExtTake(int count)
        {
            if (count < 0 || epos + count > ext.Length)
            {
                epos = ext.Length;
                return ReadOnlySpan<byte>.Empty;
            }
            var result = ext.AsSpan(epos, count);
            epos += count;
            return result;
        }

        ExtTake(4); // userKey
        ExtTake(4); // userAuth
        ExtTake(4); // appSN
        byte[] appVerRaw = ExtTake(3).ToArray();
        ExtTake(1); // appProfile
        string appCompany = DecodeField(ExtTake(16));
        byte[] protocolsRaw = ExtTake(3).ToArray();
        ExtTake(1); // resourceAuth
        ExtTake(3); // resourceVer
        byte[] randomRaw = ExtTake(4).ToArray();
        ExtTake(1); // contract

        return new BleConnectSession
        {
            Status = status,
            Address = address,
            BufferMax = bufferMax,
            ConnectionInterval = connectionInterval,
            FirmwareBle = fwBle,
            FirmwareCpu = fwCpu,
            DataFormatType = dataFormat,
            SceneNumberMax = sceneMax,
            ActivateType = activateType,
            DeviceKey = activateType == 1 ? keyA : string.Empty,
            DeviceSecret = activateType == 1 ? keyB : string.Empty,
            ProductKey = productKey,
            CommunicationMac = activateType == 0 ? keyA : string.Empty,
            Mac = activateType == 0 ? keyB : string.Empty,
            MakerKey = makerKey,
            BleHardwareKey = bleHw,
            MacHardwareKey = macHw,
            TestFirmware = testFw,
            AppCompany = appCompany,
            Protocols = Dotted(protocolsRaw),
            AppVersion = Dotted(appVerRaw),
            Random = randomRaw
        };
    }

    public static void ValidateSession(BleConnectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Status != 0) throw new InvalidOperationException($"official connect handshake rejected: status={session.Status}");
        if (!string.Equals(session.AppCompany, ExpectedAppCompany, StringComparison.Ordinal))
            throw new InvalidOperationException($"unexpected official appCompany: '{session.AppCompany}'");
        if (session.ActivateType != 1)
            throw new InvalidOperationException("device is not in the activated official-session state; activation is not bypassed");
        if (session.BufferMax <= 47 || session.BufferMax > 512)
            throw new InvalidOperationException($"invalid device bufferMax: {session.BufferMax}");
        if (session.DataFormatType is < 0 or > 4)
            throw new InvalidOperationException($"unsupported dataFormatType: {session.DataFormatType}");
    }

    private static byte[] AesCtr(byte[] data, BleCryptoContext context)
    {
        if (data.Length == 0) return [];
        if ((data.Length & 15) != 0) throw new ArgumentException("AES-CTR input must be block aligned", nameof(data));

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = context.Key;
        using ICryptoTransform encryptor = aes.CreateEncryptor();

        byte[] counter = context.Iv.ToArray();
        byte[] stream = new byte[16];
        byte[] result = new byte[data.Length];
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            encryptor.TransformBlock(counter, 0, 16, stream, 0);
            for (int i = 0; i < 16; i++) result[offset + i] = (byte)(data[offset + i] ^ stream[i]);
            IncrementCounter(counter);
        }
        return result;
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            counter[i]++;
            if (counter[i] != 0) break;
        }
    }

    private static byte[] AsciiPadded(string value, int length)
    {
        byte[] raw = Encoding.Latin1.GetBytes(value);
        if (raw.Length > length) throw new ArgumentException($"string too long for {length}-byte field", nameof(value));
        byte[] output = new byte[length];
        raw.CopyTo(output, 0);
        return output;
    }

    private static string DecodeField(ReadOnlySpan<byte> raw) => Encoding.Latin1.GetString(raw).Replace("\0", string.Empty);

    private static string Dotted(byte[] values) => values.Length == 0 ? string.Empty : string.Join('.', values.Select(x => x.ToString()));

    private static void WriteBe(Stream stream, ulong value, int width)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes[(8 - width)..]);
    }
}
