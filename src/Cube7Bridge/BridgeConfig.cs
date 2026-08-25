using System.Text.Json;

namespace Cube7Bridge;

public sealed class BridgeConfig
{
    public string LaserOsProcessName { get; set; } = "LaserOS.exe";
    public bool AutoInject { get; set; } = true;
    public bool TraceOnly { get; set; } = true;
    public bool AllowBleWrites { get; set; } = false;
    public string CaptureDirectory { get; set; } = "captures";
    public BleConfig Ble { get; set; } = new();

    public sealed class BleConfig
    {
        public string[] NameContains { get; set; } = ["CUBE", "Laserworld"];
        public bool AutoConnect { get; set; } = true;
        public bool SubscribeNotifications { get; set; } = true;
        public ulong? Address { get; set; }
    }

    public static BridgeConfig Load(string path)
    {
        if (!File.Exists(path)) return new BridgeConfig();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BridgeConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BridgeConfig();
    }
}
