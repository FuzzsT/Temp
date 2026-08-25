using System.Globalization;
using System.Text.Json;

namespace Cube7Bridge;

public sealed class BridgeConfig
{
    public string LaserOsProcessName { get; set; } = "LaserOS.exe";
    public bool AutoInject { get; set; } = true;
    public bool TraceOnly { get; set; } = true;
    public bool AllowBleWrites { get; set; } = false;
    public string CaptureDirectory { get; set; } = "captures";
    public EarlyHookConfig EarlyHook { get; set; } = new();
    public BleConfig Ble { get; set; } = new();
    public VirtualLaserCubeConfig VirtualLaserCube { get; set; } = new();

    public sealed class EarlyHookConfig
    {
        public bool Enabled { get; set; } = true;
        public bool RestartRunningLaserOs { get; set; } = true;
        public int GracefulCloseTimeoutSeconds { get; set; } = 10;
        public string? LaserOsExePath { get; set; }
    }

    public sealed class BleConfig
    {
        public string[] NameContains { get; set; } = ["CUBE", "Laserworld"];
        public bool AutoConnect { get; set; } = true;
        public bool SubscribeNotifications { get; set; } = true;
        public string? Address { get; set; }
    }

    public static BridgeConfig Load(string path)
    {
        if (!File.Exists(path)) return new BridgeConfig();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BridgeConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BridgeConfig();
    }

    public static ulong ParseBluetoothAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Bluetooth address is empty.");
        string hex = value.Trim().Replace(":", string.Empty).Replace("-", string.Empty);
        if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong address))
            throw new FormatException($"Invalid Bluetooth address '{value}'. Expected AA:BB:CC:DD:EE:FF.");
        return address;
    }

    public static string FormatBluetoothAddress(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((address >> (i * 8)) & 0xFF).ToString("X2")));
}
