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

        var derive = codec.GetMethod("DeriveSessionCrypto", BindingFlags.Public | BindingFlags.Static);
        Require(derive is not null, "session crypto derivation missing");
        object crypto = derive!.Invoke(null, [session])!;
        var cryptoType = crypto.GetType();
        Require(Convert.ToHexString((byte[])cryptoType.GetProperty("Key")!.GetValue(crypto)!) == "5345435245544B455931323334353637", "derived AES key mismatch");
        Require(Convert.ToHexString((byte[])cryptoType.GetProperty("Iv")!.GetValue(crypto)!) == "4445564943454B455931323334353637", "derived AES IV mismatch");

        var policyType = Type.GetType("Cube7Bridge.BleOfficialSessionPolicy");
        Require(policyType is not null, "official BLE runtime policy missing");
        object policy = policyType!.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Require((string)policyType.GetProperty("PairingMode")!.GetValue(policy)! == "unpaired", "Windows pairing must stay disabled");
        Require(!(bool)policyType.GetProperty("UseCachedServices")!.GetValue(policy)!, "GATT services must be uncached");
        Require((int)policyType.GetProperty("ConnectSettleMilliseconds")!.GetValue(policy)! == 1500, "connect settle must match 321.zip");
        Require((int)policyType.GetProperty("NotifySettleMilliseconds")!.GetValue(policy)! == 200, "notify settle must match 321.zip");
        Require((int)policyType.GetProperty("HandshakeTimeoutMilliseconds")!.GetValue(policy)! == 6000, "0x8B timeout must match 321.zip");
        Require((int)policyType.GetProperty("MaxConnectAttempts")!.GetValue(policy)! == 3, "connect attempts must match 321.zip");
        Require((string)policyType.GetProperty("ExpectedDeviceName")!.GetValue(policy)! == "BLEAPP_C77D_V217", "device identity gate");
        Require((string)policyType.GetProperty("CommandCharacteristic")!.GetValue(policy)! == "0000ffe1-0000-1000-8000-00805f9b34fb", "FFE1 command channel");
        Require((string)policyType.GetProperty("NotifyCharacteristic")!.GetValue(policy)! == "0000ffe1-0000-1000-8000-00805f9b34fb", "FFE1 notify channel");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
