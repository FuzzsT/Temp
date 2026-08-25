using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using System.Text.Json;

namespace Cube7Bridge;

public sealed class BleScanner
{
    private readonly BridgeConfig.BleConfig _cfg;
    private readonly PipelineStatus? _status;
    private readonly ConcurrentDictionary<ulong, string> _seen = new();
    private readonly string _logPath;
    private readonly object _logLock = new();

    public BleScanner(BridgeConfig.BleConfig cfg, string captureDirectory, PipelineStatus? status = null)
    {
        _cfg = cfg;
        _status = status;
        Directory.CreateDirectory(captureDirectory);
        _logPath = Path.Combine(captureDirectory, $"ble-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson");
    }

    private void Log(object value)
    {
        lock (_logLock) File.AppendAllText(_logPath, JsonSerializer.Serialize(value) + Environment.NewLine);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        ulong? exactAddress = null;
        if (!string.IsNullOrWhiteSpace(_cfg.Address))
        {
            exactAddress = BridgeConfig.ParseBluetoothAddress(_cfg.Address);
            Console.WriteLine($"[ble] exact target={BridgeConfig.FormatBluetoothAddress(exactAddress.Value)}");
        }

        _status?.Set("CUBE7_BLE", "SCANNING", exactAddress.HasValue ? $"target={BridgeConfig.FormatBluetoothAddress(exactAddress.Value)}" : "name filter");

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName ?? string.Empty;
            string addressText = BridgeConfig.FormatBluetoothAddress(e.BluetoothAddress);
            if (_seen.TryAdd(e.BluetoothAddress, name))
            {
                var uuids = string.Join(',', e.Advertisement.ServiceUuids.Select(x => x.ToString()));
                Console.WriteLine($"[ble] {addressText} RSSI={e.RawSignalStrengthInDBm} name='{name}' services=[{uuids}]");
                Log(new { type = "advertisement", timeUtc = DateTime.UtcNow, address = addressText, addressRaw = e.BluetoothAddress, rssi = e.RawSignalStrengthInDBm, name, serviceUuids = e.Advertisement.ServiceUuids.Select(x => x.ToString()).ToArray(), manufacturer = e.Advertisement.ManufacturerData.Select(x => new { companyId = x.CompanyId, data = ToHex(x.Data) }).ToArray(), dataSections = e.Advertisement.DataSections.Select(x => new { dataType = x.DataType, data = ToHex(x.Data) }).ToArray() });
            }

            bool matchAddr = exactAddress.HasValue && exactAddress.Value == e.BluetoothAddress;
            bool matchName = !exactAddress.HasValue && _cfg.NameContains.Any(x => !string.IsNullOrWhiteSpace(x) && name.Contains(x, StringComparison.OrdinalIgnoreCase));
            if (matchAddr)
                Console.WriteLine($"[ble] exact target matched {addressText} RSSI={e.RawSignalStrengthInDBm}");
            if (matchAddr || matchName)
            {
                _status?.Set("CUBE7_BLE", "OK", $"{addressText} RSSI={e.RawSignalStrengthInDBm} name='{name}'");
                tcs.TrySetResult(e.BluetoothAddress);
            }
        };

        watcher.Start();
        Console.WriteLine("[ble] active scan started; no payload writes are performed.");
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            var address = await tcs.Task;
            watcher.Stop();
            if (_cfg.AutoConnect) await EnumerateGattAsync(address, ct);
        }
        catch (OperationCanceledException) { watcher.Stop(); }
        catch (Exception ex)
        {
            watcher.Stop();
            _status?.Set("CUBE7_BLE", "ERROR", ex.Message);
            throw;
        }
    }

    private async Task EnumerateGattAsync(ulong address, CancellationToken ct)
    {
        string addressText = BridgeConfig.FormatBluetoothAddress(address);
        Console.WriteLine($"[ble] connecting {addressText}...");
        using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (dev is null)
        {
            _status?.Set("CUBE7_BLE", "ERROR", "cannot open device");
            Console.WriteLine("[ble] cannot open device");
            return;
        }
        Console.WriteLine($"[ble] device name='{dev.Name}' status={dev.ConnectionStatus}");

        var servicesResult = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            _status?.Set("CUBE7_BLE", "WARN", $"GATT={servicesResult.Status}");
            Console.WriteLine($"[ble] GetGattServices status={servicesResult.Status}; pairing may be required.");
            return;
        }

        int services = 0;
        int characteristics = 0;
        foreach (var service in servicesResult.Services)
        {
            services++;
            Console.WriteLine($"[gatt] SERVICE {service.Uuid}");
            Log(new { type = "service", timeUtc = DateTime.UtcNow, uuid = service.Uuid });
            var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                Console.WriteLine($"[gatt]   characteristics status={charsResult.Status}");
                continue;
            }

            foreach (var ch in charsResult.Characteristics)
            {
                characteristics++;
                var props = ch.CharacteristicProperties;
                Console.WriteLine($"[gatt]   CHAR {ch.Uuid} props={props}");
                Log(new { type = "characteristic", timeUtc = DateTime.UtcNow, service = service.Uuid, uuid = ch.Uuid, properties = props.ToString() });
                if (props.HasFlag(GattCharacteristicProperties.Read))
                {
                    try
                    {
                        var rr = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                        if (rr.Status == GattCommunicationStatus.Success)
                        {
                            var hex = ToHex(rr.Value);
                            Console.WriteLine($"[gatt]     READ {hex}");
                            Log(new { type = "read", timeUtc = DateTime.UtcNow, service = service.Uuid, uuid = ch.Uuid, value = hex });
                        }
                        else
                            Console.WriteLine($"[gatt]     READ status={rr.Status}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[gatt]     READ error={ex.Message}"); }
                }

                if (_cfg.SubscribeNotifications && (props.HasFlag(GattCharacteristicProperties.Notify) || props.HasFlag(GattCharacteristicProperties.Indicate)))
                {
                    ch.ValueChanged += (_, e) =>
                    {
                        var hex = ToHex(e.CharacteristicValue);
                        Console.WriteLine($"[gatt]     NOTIFY {ch.Uuid} {hex}");
                        Log(new { type = "notify", timeUtc = DateTime.UtcNow, service = service.Uuid, uuid = ch.Uuid, value = hex });
                    };
                    var mode = props.HasFlag(GattCharacteristicProperties.Notify)
                        ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                        : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                    try
                    {
                        var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(mode);
                        Console.WriteLine($"[gatt]     SUBSCRIBE {status}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[gatt]     SUBSCRIBE error={ex.Message}"); }
                }
            }
        }

        _status?.Set("CUBE7_BLE", "OK", $"{addressText} name='{dev.Name}' services={services} chars={characteristics}");
        Console.WriteLine("[ble] GATT enumeration complete. Keeping notification subscriptions alive; Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
    }

    private static string ToHex(IBuffer b)
    {
        using var reader = DataReader.FromBuffer(b);
        var data = new byte[(int)b.Length];
        reader.ReadBytes(data);
        return Convert.ToHexString(data);
    }
}
