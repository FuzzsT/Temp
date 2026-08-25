namespace Cube7Bridge;

public static class BleOfficialSessionRuntime
{
    public static IReadOnlyList<string> DescribePlan(BleOfficialSessionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return
        [
            "CONNECT_UNPAIRED",
            $"SETTLE_{policy.ConnectSettleMilliseconds}MS",
            "READ_DEVICE_NAME_UNCACHED",
            "VALIDATE_GATT_UNCACHED",
            "SUBSCRIBE_FFE1_NOTIFY",
            $"SETTLE_{policy.NotifySettleMilliseconds}MS",
            "WRITE_FFE1_WITH_RESPONSE_0xAB",
            $"WAIT_0x8B_{policy.HandshakeTimeoutMilliseconds}MS"
        ];
    }
}

public sealed record BleOfficialSessionSummary(
    int Status,
    int Address,
    int BufferMax,
    int ConnectionInterval,
    string FirmwareBle,
    string FirmwareCpu,
    int DataFormatType,
    int SceneNumberMax,
    int ActivateType,
    string AppCompany,
    string Protocols,
    string AppVersion,
    bool SecretsRedacted,
    string PairingMode,
    string CommandChannel,
    bool PhysicalOutputEnabled)
{
    public static BleOfficialSessionSummary From(BleConnectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new BleOfficialSessionSummary(
            session.Status,
            session.Address,
            session.BufferMax,
            session.ConnectionInterval,
            session.FirmwareBle,
            session.FirmwareCpu,
            session.DataFormatType,
            session.SceneNumberMax,
            session.ActivateType,
            session.AppCompany,
            session.Protocols,
            session.AppVersion,
            SecretsRedacted: true,
            PairingMode: BleOfficialSessionPolicy.Default.PairingMode,
            CommandChannel: BleOfficialSessionPolicy.Default.CommandCharacteristic,
            PhysicalOutputEnabled: false);
    }
}
