using System.Reflection;

namespace Cube7Bridge;

public static class BleOfficialSessionTests
{
    public static void Run()
    {
        var codec = Type.GetType("Cube7Bridge.BleOfficialSessionCodec");
        Require(codec is not null, "official BLE session codec type missing");

        var build = codec!.GetMethod("BuildConnectRequest", BindingFlags.Public | BindingFlags.Static);
        var encrypt = codec.GetMethod("EncryptMessage", BindingFlags.Public | BindingFlags.Static);
        var decrypt = codec.GetMethod("DecryptMessage", BindingFlags.Public | BindingFlags.Static);
        var parse = codec.GetMethod("ParseConnectResponse", BindingFlags.Public | BindingFlags.Static);
        Require(build is not null && encrypt is not null && decrypt is not null && parse is not null,
            "official BLE codec API incomplete");

        byte[] random = [0x01, 0x02, 0x03, 0x04];
        byte[] expectedRequest = Convert.FromHexString(
            "AB1234002B000000000000000000000003010000004368696E6154656D65694149000000000100000000000001020304");
        byte[] request = (byte[])build!.Invoke(null, [random, 0u, new byte[] { 1, 0, 0 }])!;
        Require(request.SequenceEqual(expectedRequest), "0xAB connect request differs from 321.zip contract");

        byte[] expectedEncrypted = Convert.FromHexString(
            "AB84327E9567CFD9F7F8F0670228B655FC38C116C8D7F2D02188A11AEEB7315899BD40E031534AC25A133364168FE5F129");
        byte[] encrypted = (byte[])encrypt!.Invoke(null, [request, null])!;
        Require(encrypted.SequenceEqual(expectedEncrypted), "AES-CTR default handshake vector mismatch");

        byte[] decrypted = (byte[])decrypt!.Invoke(null, [encrypted, null])!;
        Require(decrypted.AsSpan(0, request.Length).SequenceEqual(request), "AES-CTR decrypt round-trip mismatch");

        byte[] response = Convert.FromHexString(
            "8B12340000F4000C0201000201040208014445564943454B455931323334353637000000000000000000000000000000005345435245544B4559313233343536370000000000000000000000000000000050524F445543544B4559313233343536000000000000000000000000000000000000007B000B0016002C00000000000000000000000001000000437562654C6173657254656D65694149010000000000000102030400");
        object session = parse!.Invoke(null, [response])!;
        var t = session.GetType();
        Require((int)t.GetProperty("Status")!.GetValue(session)! == 0, "connect status");
        Require((int)t.GetProperty("BufferMax")!.GetValue(session)! == 244, "bufferMax parse");
        Require((int)t.GetProperty("DataFormatType")!.GetValue(session)! == 2, "dataFormatType parse");
        Require((int)t.GetProperty("ActivateType")!.GetValue(session)! == 1, "activateType parse");
        Require((string)t.GetProperty("FirmwareCpu")!.GetValue(session)! == "2.1.4", "firmwareCpu parse");
        Require((string)t.GetProperty("AppCompany")!.GetValue(session)! == "CubeLaserTemeiAI", "appCompany parse");

        var validate = codec.GetMethod("ValidateSession", BindingFlags.Public | BindingFlags.Static);
        Require(validate is not null, "official BLE session validation missing");
        validate!.Invoke(null, [session]);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
