using System.Security.Cryptography;

namespace Cube7Bridge;

/// <summary>
/// Pure implementation of the official Cube application's AES-128-CTR wrapper
/// regression-tested in LaserCube AIO v26. No BLE I/O is performed here.
/// </summary>
public static class CubeOfficialCrypto
{
    public const string DefaultKeyHex = "77663C3A3F65686C52783A3B70495446";
    public const string DefaultIvHex = "3551657148312A29772D5C6E5F484562";
    private const int MaxEncryptedBody = 240;
    private const int BlockSize = 16;

    public static byte[] EncryptMessage(byte[] message, byte[] key, byte[] iv)
    {
        Validate(message, key, iv);

        byte command = message[0];
        int bodyLength = message.Length - 1;
        int cryptoLength = Math.Min(bodyLength, MaxEncryptedBody);
        int tailLength = bodyLength - cryptoLength;

        byte[] cryptoBody = message.AsSpan(1, cryptoLength).ToArray();
        if (cryptoLength < MaxEncryptedBody)
            cryptoBody = ZeroPad(cryptoBody);

        byte[] transformed = TransformCtr(cryptoBody, key, iv);
        byte[] result = new byte[1 + transformed.Length + tailLength];
        result[0] = command;
        transformed.CopyTo(result, 1);
        if (tailLength > 0)
            message.AsSpan(1 + cryptoLength, tailLength).CopyTo(result.AsSpan(1 + transformed.Length));
        return result;
    }

    public static byte[] DecryptMessage(byte[] message, byte[] key, byte[] iv)
    {
        Validate(message, key, iv);

        byte command = message[0];
        int bodyLength = message.Length - 1;
        int cryptoLength = Math.Min(bodyLength, MaxEncryptedBody);
        int tailLength = bodyLength - cryptoLength;

        byte[] clearBody = TransformCtr(message.AsSpan(1, cryptoLength).ToArray(), key, iv);
        byte[] result = new byte[1 + clearBody.Length + tailLength];
        result[0] = command;
        clearBody.CopyTo(result, 1);
        if (tailLength > 0)
            message.AsSpan(1 + cryptoLength, tailLength).CopyTo(result.AsSpan(1 + clearBody.Length));
        return result;
    }

    private static byte[] ZeroPad(byte[] value)
    {
        int rem = value.Length % BlockSize;
        if (rem == 0) return value;
        Array.Resize(ref value, value.Length + (BlockSize - rem));
        return value;
    }

    private static byte[] TransformCtr(byte[] input, byte[] key, byte[] iv)
    {
        if (input.Length == 0) return Array.Empty<byte>();

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();

        byte[] counter = (byte[])iv.Clone();
        byte[] keystream = new byte[BlockSize];
        byte[] output = new byte[input.Length];

        for (int offset = 0; offset < input.Length; offset += BlockSize)
        {
            int written = encryptor.TransformBlock(counter, 0, BlockSize, keystream, 0);
            if (written != BlockSize)
                throw new CryptographicException("AES ECB counter block encryption returned an unexpected length.");

            int count = Math.Min(BlockSize, input.Length - offset);
            for (int i = 0; i < count; i++)
                output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);

            IncrementCounter(counter);
        }
        return output;
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            counter[i]++;
            if (counter[i] != 0) break;
        }
    }

    private static void Validate(byte[] message, byte[] key, byte[] iv)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);
        if (message.Length == 0) throw new ArgumentException("message is empty", nameof(message));
        if (key.Length != 16 || iv.Length != 16)
            throw new ArgumentException("AES-128 CTR key and IV must both be 16 bytes");
    }
}
