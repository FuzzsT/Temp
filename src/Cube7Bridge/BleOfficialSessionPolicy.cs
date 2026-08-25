namespace Cube7Bridge;

public sealed record BleOfficialSessionPolicy(
    string PairingMode,
    bool UseCachedServices,
    int ConnectSettleMilliseconds,
    int NotifySettleMilliseconds,
    int HandshakeTimeoutMilliseconds,
    int MaxConnectAttempts,
    string ExpectedDeviceName,
    string CommandCharacteristic,
    string NotifyCharacteristic)
{
    public static BleOfficialSessionPolicy Default { get; } = new(
        PairingMode: "unpaired",
        UseCachedServices: false,
        ConnectSettleMilliseconds: 1500,
        NotifySettleMilliseconds: 200,
        HandshakeTimeoutMilliseconds: 6000,
        MaxConnectAttempts: 3,
        ExpectedDeviceName: "BLEAPP_C77D_V217",
        CommandCharacteristic: "0000ffe1-0000-1000-8000-00805f9b34fb",
        NotifyCharacteristic: "0000ffe1-0000-1000-8000-00805f9b34fb");
}
