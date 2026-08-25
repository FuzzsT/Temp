using System.Reflection;
using System.Text.Json;

namespace Cube7Bridge;

public static class BleOfficialSessionRuntimeTests
{
    public static void Run()
    {
        var runtime = Type.GetType("Cube7Bridge.BleOfficialSessionRuntime");
        Require(runtime is not null, "official BLE session runtime type missing");

        var describe = runtime!.GetMethod("DescribePlan", BindingFlags.Public | BindingFlags.Static);
        Require(describe is not null, "official BLE runtime plan missing");
        var steps = ((IEnumerable<string>)describe!.Invoke(null, [BleOfficialSessionPolicy.Default])!).ToArray();
        string[] expected =
        [
            "CONNECT_UNPAIRED",
            "SETTLE_1500MS",
            "READ_DEVICE_NAME_UNCACHED",
            "VALIDATE_GATT_UNCACHED",
            "SUBSCRIBE_FFE1_NOTIFY",
            "SETTLE_200MS",
            "WRITE_FFE1_WITH_RESPONSE_0xAB",
            "WAIT_0x8B_6000MS"
        ];
        Require(steps.SequenceEqual(expected), "official BLE runtime plan differs from 321.zip");

        var session = new BleConnectSession
        {
            Status = 0,
            Address = 0x1234,
            BufferMax = 244,
            ConnectionInterval = 12,
            FirmwareBle = "2.1.0",
            FirmwareCpu = "2.1.4",
            DataFormatType = 2,
            SceneNumberMax = 8,
            ActivateType = 1,
            DeviceKey = "DEVICEKEY1234567",
            DeviceSecret = "SECRETKEY1234567",
            ProductKey = "PRODUCTKEY123456",
            AppCompany = "CubeLaserTemeiAI",
            Protocols = "1.0.0",
            AppVersion = "1.0.0",
            Random = [1,2,3,4]
        };

        var summaryType = Type.GetType("Cube7Bridge.BleOfficialSessionSummary");
        Require(summaryType is not null, "safe BLE session summary type missing");
        var from = summaryType!.GetMethod("From", BindingFlags.Public | BindingFlags.Static);
        Require(from is not null, "safe BLE session summary factory missing");
        object summary = from!.Invoke(null, [session])!;
        string json = JsonSerializer.Serialize(summary);
        Require(json.Contains("2.1.4") && json.Contains("244") && json.Contains("CubeLaserTemeiAI"), "safe session summary lost public parameters");
        Require(!json.Contains("DEVICEKEY", StringComparison.OrdinalIgnoreCase), "deviceKey leaked into session summary");
        Require(!json.Contains("SECRETKEY", StringComparison.OrdinalIgnoreCase), "deviceSecret leaked into session summary");
        Require(!json.Contains("PRODUCTKEY", StringComparison.OrdinalIgnoreCase), "productKey leaked into session summary");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
