using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Cube7Bridge;

public sealed class BleScanner
{
    private static readonly Guid DeviceNameUuid = Guid.Parse("00002a00-0000-1000-8000-00805f9b34fb");
    private static readonly Guid Ffe0Uuid = Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb");
    private static readonly Guid Ffe1Uuid = Guid.Parse(BleOfficialSessionPolicy.Default.CommandCharacteristic);
    private static readonly Guid Ffe2Uuid = Guid.Parse("0000ffe2-0000-1000-8000-00805f9b34fb");

    private readonly BridgeConfig.BleConfig _cfg;
    private readonly PipelineStatus? _status;
    private readonly ConcurrentDictionary<ulong, string> _seen = new();
    private readonly string _captureDirectory;
    private readonly string _logPath;
    private readonly object _logLock = new();
    private readonly BleOfficialSessionPolicy _policy = BleOfficialSessionPolicy.Default;

    public BleScanner(BridgeConfig.BleConfig cfg, string captureDirectory, PipelineStatus? status = null)
    {
        _cfg = cfg;
        _status = status;
        _captureDirectory = captureDirectory;
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

        _status?.Set("BLE_TRANSPORT", "SCANNING", "Windows pairing disabled; waiting for BLE advertisement");
        _status?.Set("BLE_SESSION", "WAITING", "official 0xAB/0x8B session not started");
        _status?.Set("CUBE7_BLE", "SCANNING", exactAddress.HasValue ? $"target={BridgeConfig.FormatBluetoothAddress(exactAddress.Value)}" : "name filter");

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.Received += (_, e) =>
        {
            string name = e.Advertisement.LocalName ?? string.Empty;
            string addressText = BridgeConfig.FormatBluetoothAddress(e.BluetoothAddress);
            if (_seen.TryAdd(e.BluetoothAddress, name))
            {
                Console.WriteLine($"[ble] {addressText} RSSI={e.RawSignalStrengthInDBm} name='{name}'");
                Log(new
                {
                    type = "advertisement",
                    timeUtc = DateTime.UtcNow,
                    address = addressText,
                    addressRaw = e.BluetoothAddress,
                    rssi = e.RawSignalStrengthInDBm,
                    name,
                    serviceUuids = e.Advertisement.ServiceUuids.Select(x => x.ToString()).ToArray()
                });
            }

            bool matchAddr = exactAddress.HasValue && exactAddress.Value == e.BluetoothAddress;
            bool matchName = !exactAddress.HasValue && _cfg.NameContains.Any(x => !string.IsNullOrWhiteSpace(x) && name.Contains(x, StringComparison.OrdinalIgnoreCase));
            if (matchAddr || matchName)
                tcs.TrySetResult(e.BluetoothAddress);
        };

        watcher.Start();
        Console.WriteLine("[ble] active scan started; Windows Pair is NOT used.");
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            ulong address = await tcs.Task;
            watcher.Stop();
            if (_cfg.AutoConnect)
                await ConnectOfficialSessionWithRetriesAsync(address, ct);
        }
        catch (OperationCanceledException)
        {
            watcher.Stop();
        }
        catch (Exception ex)
        {
            watcher.Stop();
            _status?.Set("CUBE7_BLE", "ERROR", ex.Message);
            _status?.Set("BLE_SESSION", "ERROR", ex.Message);
            throw;
        }
    }

    private async Task ConnectOfficialSessionWithRetriesAsync(ulong address, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= _policy.MaxConnectAttempts; attempt++)
        {
            try
            {
                _status?.Set("BLE_TRANSPORT", "CONNECTING", $"attempt={attempt}/{_policy.MaxConnectAttempts} pairing=DISABLED cache=UNCACHED");
                await ConnectOfficialSessionAttemptAsync(address, attempt, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Console.WriteLine($"[ble] official session attempt {attempt}/{_policy.MaxConnectAttempts} failed: {ex.Message}");
                Log(new { type = "session_attempt_failed", timeUtc = DateTime.UtcNow, attempt, error = ex.Message });
                _status?.Set("BLE_SESSION", attempt < _policy.MaxConnectAttempts ? "RETRY" : "ERROR", $"attempt={attempt} {ex.Message}");
                if (attempt < _policy.MaxConnectAttempts)
                    await Task.Delay(500, ct);
            }
        }

        throw new InvalidOperationException($"official BLE session failed after {_policy.MaxConnectAttempts} attempts: {last?.Message}", last);
    }

    private async Task ConnectOfficialSessionAttemptAsync(ulong address, int attempt, CancellationToken ct)
    {
        string addressText = BridgeConfig.FormatBluetoothAddress(address);
        Console.WriteLine($"[ble] opening unpaired target {addressText} attempt={attempt}...");

        using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
        if (dev is null) throw new InvalidOperationException("BluetoothLEDevice.FromBluetoothAddressAsync returned null");

        bool alreadyPaired = false;
        try { alreadyPaired = dev.DeviceInformation.Pairing.IsPaired; } catch { }
        if (alreadyPaired)
            Console.WriteLine("[ble] warning: device is already paired in Windows; no pair/unpair operation will be performed.");

        _status?.Set("BLE_TRANSPORT", "CONNECTED", $"{addressText} pairing={(_policy.PairingMode)} existingPair={alreadyPaired} cache=UNCACHED");
        await Task.Delay(_policy.ConnectSettleMilliseconds, ct);

        var servicesResult = await dev.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"uncached GetGattServices failed: {servicesResult.Status}; Windows pairing is not requested");

        GattCharacteristic? deviceNameCharacteristic = null;
        GattCharacteristic? ffe1 = null;
        GattCharacteristic? ffe2 = null;
        bool ffe0Found = false;
        int services = 0;
        int characteristics = 0;

        foreach (var service in servicesResult.Services)
        {
            services++;
            if (service.Uuid == Ffe0Uuid) ffe0Found = true;
            Console.WriteLine($"[gatt] SERVICE {service.Uuid}");
            Log(new { type = "service", timeUtc = DateTime.UtcNow, uuid = service.Uuid });

            var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success)
                continue;

            foreach (var ch in charsResult.Characteristics)
            {
                characteristics++;
                var props = ch.CharacteristicProperties;
                Console.WriteLine($"[gatt]   CHAR {ch.Uuid} props={props}");
                Log(new { type = "characteristic", timeUtc = DateTime.UtcNow, service = service.Uuid, uuid = ch.Uuid, properties = props.ToString() });

                if (ch.Uuid == DeviceNameUuid) deviceNameCharacteristic = ch;
                if (service.Uuid == Ffe0Uuid && ch.Uuid == Ffe1Uuid) ffe1 = ch;
                if (service.Uuid == Ffe0Uuid && ch.Uuid == Ffe2Uuid) ffe2 = ch;
            }
        }

        if (deviceNameCharacteristic is null)
            throw new InvalidOperationException("uncached GATT Device Name 2A00 is missing");

        var nameRead = await deviceNameCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (nameRead.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"uncached Device Name read failed: {nameRead.Status}");
        string deviceName = System.Text.Encoding.UTF8.GetString(ToBytes(nameRead.Value)).TrimEnd('\0');
        if (!string.Equals(deviceName, _policy.ExpectedDeviceName, StringComparison.Ordinal))
            throw new InvalidOperationException($"unexpected BLE identity '{deviceName}', expected '{_policy.ExpectedDeviceName}'");

        if (!ffe0Found || ffe1 is null || ffe2 is null)
            throw new InvalidOperationException("expected FFE0/FFE1/FFE2 vendor profile is missing");

        var ffe1Props = ffe1.CharacteristicProperties;
        bool ffe1Usable = ffe1Props.HasFlag(GattCharacteristicProperties.Read)
            && ffe1Props.HasFlag(GattCharacteristicProperties.Notify)
            && (ffe1Props.HasFlag(GattCharacteristicProperties.Write) || ffe1Props.HasFlag(GattCharacteristicProperties.WriteWithoutResponse));
        if (!ffe1Usable)
            throw new InvalidOperationException($"FFE1 capabilities mismatch: {ffe1Props}");

        var ffe2Props = ffe2.CharacteristicProperties;
        if (!(ffe2Props.HasFlag(GattCharacteristicProperties.Write) || ffe2Props.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)))
            throw new InvalidOperationException($"FFE2 capabilities mismatch: {ffe2Props}");

        Console.WriteLine($"[ble] identity OK name='{deviceName}' services={services} chars={characteristics}; command+notify=FFE1");
        _status?.Set("BLE_TRANSPORT", "OK", $"{addressText} name='{deviceName}' unpaired/uncached FFE1 write+notify");

        var ack = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        ffe1.ValueChanged += (_, e) =>
        {
            byte[] data = ToBytes(e.CharacteristicValue);
            byte command = data.Length > 0 ? data[0] : (byte)0;
            Console.WriteLine($"[gatt] FFE1 NOTIFY len={data.Length} cmd=0x{command:X2} payload=REDACTED");
            Log(new { type = "notify_meta", timeUtc = DateTime.UtcNow, uuid = ffe1.Uuid, length = data.Length, command = $"0x{command:X2}", payloadRedacted = true });

            if (data.Length == 0 || command != BleOfficialSessionCodec.ConnectAck)
                return;
            try
            {
                byte[] clear = BleOfficialSessionCodec.DecryptMessage(data, null);
                if (clear.Length > 0 && clear[0] == BleOfficialSessionCodec.ConnectAck)
                    ack.TrySetResult(clear);
            }
            catch (Exception ex)
            {
                ack.TrySetException(new InvalidOperationException($"cannot decrypt 0x8B connect response: {ex.Message}", ex));
            }
        };

        var subscribeStatus = await ffe1.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (subscribeStatus != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"FFE1 notification subscription failed: {subscribeStatus}");

        await Task.Delay(_policy.NotifySettleMilliseconds, ct);

        byte[] random = RandomNumberGenerator.GetBytes(4);
        byte[] request = BleOfficialSessionCodec.BuildConnectRequest(random, 0u, [1, 0, 0]);
        byte[] encryptedRequest = BleOfficialSessionCodec.EncryptMessage(request, null);
        if (encryptedRequest.Length == 0 || encryptedRequest[0] != BleOfficialSessionCodec.CmdConnect)
            throw new InvalidOperationException("session write guard rejected non-0xAB command");

        using (var dataWriter = new DataWriter())
        {
            dataWriter.WriteBytes(encryptedRequest);
            var writeStatus = await ffe1.WriteValueAsync(dataWriter.DetachBuffer(), GattWriteOption.WriteWithResponse);
            Console.WriteLine($"[gatt] FFE1 WRITE_WITH_RESPONSE cmd=0xAB len={encryptedRequest.Length} status={writeStatus} payload=REDACTED");
            Log(new { type = "session_write_meta", timeUtc = DateTime.UtcNow, uuid = ffe1.Uuid, command = "0xAB", length = encryptedRequest.Length, writeWithResponse = true, status = writeStatus.ToString(), payloadRedacted = true });
            if (writeStatus != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"FFE1 0xAB WriteWithResponse failed: {writeStatus}");
        }

        byte[] clearAck = await ack.Task.WaitAsync(TimeSpan.FromMilliseconds(_policy.HandshakeTimeoutMilliseconds), ct);
        BleConnectSession session = BleOfficialSessionCodec.ParseConnectResponse(clearAck);
        BleOfficialSessionCodec.ValidateSession(session);
        BleCryptoContext sessionCrypto = BleOfficialSessionCodec.DeriveSessionCrypto(session);

        try
        {
            var summary = BleOfficialSessionSummary.From(session);
            string summaryPath = Path.Combine(_captureDirectory, "ble-session-summary.json");
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[ble-session] READY fwCPU={session.FirmwareCpu} fwBLE={session.FirmwareBle} bufferMax={session.BufferMax} dataFormat={session.DataFormatType} activate={session.ActivateType} secrets=REDACTED");
            Console.WriteLine($"[ble-session] summary={summaryPath}");
            _status?.Set("BLE_SESSION", "READY", $"fwCPU={session.FirmwareCpu} bufferMax={session.BufferMax} dataFormat={session.DataFormatType} activate={session.ActivateType}");
            _status?.Set("BLE_PROTOCOL", "OFFICIAL-SESSION", "0xAB/0x8B established on FFE1; arbitrary control writes disabled");
            _status?.Set("CUBE7_BLE", "OK", $"{addressText} name='{deviceName}' session=READY");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionCrypto.Key);
            CryptographicOperations.ZeroMemory(sessionCrypto.Iv);
            CryptographicOperations.ZeroMemory(random);
            CryptographicOperations.ZeroMemory(request);
            CryptographicOperations.ZeroMemory(encryptedRequest);
            CryptographicOperations.ZeroMemory(clearAck);
        }

        Console.WriteLine("[ble-session] keeping FFE1 notification subscription alive; physical output remains OFF.");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            try
            {
                await ffe1.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
        }
    }

    private static byte[] ToBytes(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        byte[] data = new byte[(int)buffer.Length];
        reader.ReadBytes(data);
        return data;
    }
}
