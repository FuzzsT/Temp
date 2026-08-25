using System.Reflection;
using Cube7Bridge;

static void Require(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

var asm = typeof(RendererPoint).Assembly;
var crypto = asm.GetType("Cube7Bridge.CubeOfficialCrypto");
Require(crypto is not null, "CubeOfficialCrypto type is missing");

var encrypt = crypto!.GetMethod("EncryptMessage", BindingFlags.Public | BindingFlags.Static, [typeof(byte[]), typeof(byte[]), typeof(byte[])]);
var decrypt = crypto.GetMethod("DecryptMessage", BindingFlags.Public | BindingFlags.Static, [typeof(byte[]), typeof(byte[]), typeof(byte[])]);
Require(encrypt is not null, "CubeOfficialCrypto.EncryptMessage is missing");
Require(decrypt is not null, "CubeOfficialCrypto.DecryptMessage is missing");

var key = Convert.FromHexString("77663C3A3F65686C52783A3B70495446");
var iv = Convert.FromHexString("3551657148312A29772D5C6E5F484562");
var plain = Convert.FromHexString("AB1234002B000000000000000000000003010000004368696E6154656D65694149000000000100000000000001020304");
var expected = Convert.FromHexString("AB84327E9567CFD9F7F8F0670228B655FC38C116C8D7F2D02188A11AEEB7315899BD40E031534AC25A133364168FE5F129");

var encrypted = (byte[])encrypt!.Invoke(null, [plain, key, iv])!;
Require(encrypted.SequenceEqual(expected), "official v26 known connect encryption vector mismatch");

var decrypted = (byte[])decrypt!.Invoke(null, [encrypted, key, iv])!;
Require(decrypted.AsSpan(0, plain.Length).SequenceEqual(plain), "official v26 decrypt round-trip mismatch");

var longPlain = new byte[1 + 240 + 4];
longPlain[0] = 0xAD;
for (int i = 0; i < 240; i++) longPlain[1 + i] = (byte)i;
"TAIL"u8.CopyTo(longPlain.AsSpan(241));
var longEncrypted = (byte[])encrypt.Invoke(null, [longPlain, key, iv])!;
Require(longEncrypted.Length == longPlain.Length, "240-byte encrypted body plus tail must preserve message length");
Require(longEncrypted.AsSpan(^4).SequenceEqual("TAIL"u8), "bytes after first 240 body bytes must remain plaintext");
var longDecrypted = (byte[])decrypt.Invoke(null, [longEncrypted, key, iv])!;
Require(longDecrypted.SequenceEqual(longPlain), "long official crypto round-trip mismatch");

Console.WriteLine("CRYPTO CONTRACT PASS: v26 AES-128-CTR known vector + 240-byte body boundary");
