using System.IO.Pipes;
using System.Text;

namespace Cube7Bridge;

public sealed class HookCaptureServer : IDisposable
{
    private readonly CaptureWriter _writer;
    private readonly LaserCubeHandshakeTracker _handshake;
    private readonly RendererFrameCapture _renderer;
    private readonly PipelineStatus _status;
    private readonly RendererFrameStatistics _stats;
    private readonly DryRunTranslationCapture? _translation;
    private readonly VirtualDeviceState _virtualDevice;
    private readonly DeviceApiTrace _apiTrace = new();
    private readonly BridgeConfig.VirtualDeviceConfig _virtualConfig;
    private long _rendererFrames;

    public HookCaptureServer(
        CaptureWriter writer,
        PipelineStatus status,
        RendererFrameStatistics stats,
        bool writeDryRunTranslation,
        BridgeConfig.VirtualDeviceConfig? virtualDeviceConfig = null)
    {
        _writer = writer;
        _status = status;
        _stats = stats;
        _handshake = new LaserCubeHandshakeTracker(writer.DirectoryPath);
        _renderer = new RendererFrameCapture(writer.DirectoryPath);
        _virtualConfig = virtualDeviceConfig ?? new BridgeConfig.VirtualDeviceConfig();
        _virtualDevice = new VirtualDeviceState(_virtualConfig);
        if (writeDryRunTranslation)
            _translation = new DryRunTranslationCapture(writer.DirectoryPath);
    }

    public VirtualDeviceSnapshot VirtualDeviceSnapshot => _virtualDevice.Snapshot();
    public DeviceApiTraceSnapshot DeviceApiSnapshot => _apiTrace.Snapshot();

    public async Task RunAsync(CancellationToken ct)
    {
        _status.Set("LASEROS_HOOK", "WAITING", "waiting for injected hook pipe");
        _status.Set("VIRTUAL_DEVICE", _virtualConfig.Enabled ? "ARMED" : "DISABLED", $"network={_virtualConfig.Network} usbHid={_virtualConfig.UsbHid} physical-output=OFF");
        _status.Set("USB_HID_TRACE", _virtualConfig.UsbHid.Equals("trace-first", StringComparison.OrdinalIgnoreCase) ? "ARMED" : "DISABLED", "passive API observation only");
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                "Cube7LaserOSBridge", PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024 * 1024, 1024 * 1024);
            Console.WriteLine("[hook] waiting for LaserOSHook.dll...");
            await pipe.WaitForConnectionAsync(ct);
            Console.WriteLine("[hook] connected");
            _status.Set("LASEROS_HOOK", "OK", "LaserOSHook.dll connected");
            Console.WriteLine($"[stage] handshake summary: {Path.Combine(_writer.DirectoryPath, "handshake-summary.ndjson")}");
            Console.WriteLine($"[renderer] frame summary: {_renderer.SummaryPath}");
            Console.WriteLine($"[virtual] state report: {Path.Combine(_writer.DirectoryPath, "virtual-device-state.json")}");
            if (_translation is not null)
                Console.WriteLine($"[translator] dry-run summary: {_translation.Path}");
            Console.WriteLine("[renderer] tap is capture-only; physical output remains OFF.");
            try
            {
                var header = new byte[HookRecord.HeaderSize];
                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    await ReadExactlyAsync(pipe, header, ct);
                    uint len = BitConverter.ToUInt32(header, 40);
                    if (len > 1024 * 1024) throw new InvalidDataException($"Captured packet too large: {len}");
                    var payload = new byte[len];
                    await ReadExactlyAsync(pipe, payload, ct);
                    var record = HookRecord.Parse(header, payload);
                    _writer.Write(record);

                    if (ObserveDeviceApi(record))
                    {
                        WriteDeviceReport();
                        Console.WriteLine($"[device-api] {Describe(record)}");
                        continue;
                    }

                    if (RendererFrameDecoder.TryDecode(record, out var frame) && frame is not null)
                    {
                        _renderer.Write(record, frame);
                        _stats.Observe(frame);
                        _virtualDevice.ObserveRendererFrame(frame);
                        var normalized = DryRunTranslator.Translate(frame);
                        _translation?.Write(normalized);
                        long n = Interlocked.Increment(ref _rendererFrames);
                        var snap = _stats.Snapshot();
                        _status.Set("RENDERER_TAP", "OK", $"frames={snap.Frames} rate={frame.Rate}pps points={frame.PointCount} max={snap.MaxPoints}");
                        _status.Set("TRANSLATOR", "DRY-RUN", $"normalized={normalized.PointCount} physical-output=OFF");
                        _status.Set("VIRTUAL_DEVICE", "STREAMING", $"frames={_virtualDevice.RendererFrames} points={_virtualDevice.RendererPoints} physical-output=OFF");
                        if (_virtualConfig.WriteRendererPreview && (n == 1 || n % 60 == 0))
                            RendererPreview.WriteSvg(_writer.DirectoryPath, frame);
                        if (n == 1 || n % 60 == 0)
                            WriteDeviceReport();
                        if (n <= 5 || n % 60 == 0)
                            Console.WriteLine($"[renderer] frame={n} points={frame.PointCount} rate={frame.Rate} flags=0x{frame.Flags:X} normalized={normalized.PointCount}");
                        continue;
                    }

                    Console.WriteLine($"[{(record.Direction == 1 ? "TX" : "RX")}] {record.RemoteAddress}:{record.Port} {record.Payload.Length}B {Describe(record)}");
                    string? transition = _handshake.Observe(record);
                    if (transition is not null)
                    {
                        Console.WriteLine(transition);
                        UpdateHandshakeStatus(transition);
                        WriteDeviceReport();
                    }
                }
            }
            catch (EndOfStreamException)
            {
                _status.Set("LASEROS_HOOK", "DISCONNECTED", "hook pipe closed");
            }
            catch (IOException ex)
            {
                _status.Set("LASEROS_HOOK", "ERROR", ex.Message);
                Console.WriteLine($"[hook] disconnected: {ex.Message}");
            }
        }
    }

    private bool ObserveDeviceApi(HookRecord record)
    {
        string? api = record.Api switch
        {
            30 => "SetupDiGetClassDevsW",
            31 => "HidD_GetAttributes",
            32 => "CreateFileW",
            33 => "WriteFile",
            34 => "ReadFile",
            35 => "DeviceIoControl",
            36 => "DeviceIoControl",
            _ => null
        };
        if (api is null) return false;

        string detail = record.Api is 30 or 31 or 32 or 35 or 36
            ? DecodeText(record.Payload)
            : $"bytes={record.Payload.Length} preview={Convert.ToHexString(record.Payload.AsSpan(0, Math.Min(16, record.Payload.Length)))}";
        _apiTrace.Observe(api, detail);
        var snap = _apiTrace.Snapshot();
        string families = string.Join(',', snap.ByFamily.Select(kv => $"{kv.Key}:{kv.Value}"));
        _status.Set("USB_HID_TRACE", "OBSERVED", $"events={snap.Total} {families}");
        return true;
    }

    private void UpdateHandshakeStatus(string transition)
    {
        if (transition.Contains("DISCOVERY_REQUEST", StringComparison.Ordinal))
            _status.Set("DISCOVERY_0x27", "ACTIVE", "request observed");
        if (transition.Contains("DISCOVERY_ACCEPTED", StringComparison.Ordinal))
        {
            _status.Set("DISCOVERY_0x27", "OK", "27 00 accepted");
            _virtualDevice.MarkDiscovered();
            _status.Set("VIRTUAL_DEVICE", "DISCOVERED", "0x27 discovery accepted");
        }
        if (transition.Contains("FULL_INFO_REQUEST", StringComparison.Ordinal))
            _status.Set("FULL_INFO_0x77", "ACTIVE", "request observed");
        if (transition.Contains("FULL_INFO_ACCEPTED", StringComparison.Ordinal))
        {
            _status.Set("FULL_INFO_0x77", "OK", "64-byte response accepted");
            _virtualDevice.MarkIdentified();
            _status.Set("VIRTUAL_DEVICE", "IDENTIFIED", "0x77 full-info accepted");
        }
        if (transition.Contains("AUTH_REQUEST", StringComparison.Ordinal) || transition.Contains("AUTH_RESPONSE", StringComparison.Ordinal))
        {
            _status.Set("AUTH_B0_B1", "ACTIVE", "B0/B1 handshake observed");
            _virtualDevice.MarkAuthenticationObserved();
            _status.Set("VIRTUAL_DEVICE", "AUTH-TRACE", "authentication observed; no synthetic success response");
        }
        if (transition.Contains("AUTH_RESPONSE_CAPTURED", StringComparison.Ordinal))
            _status.Set("AUTH_B0_B1", "CAPTURED", "response captured; LaserOS validation pending");
        if (transition.Contains("POST_AUTH_DEVICE_TRAFFIC", StringComparison.Ordinal))
        {
            _status.Set("AUTH_B0_B1", "OK", "post-auth device traffic observed");
            _virtualDevice.MarkReady("post-auth device traffic observed");
            _status.Set("VIRTUAL_DEVICE", "READY", "post-auth traffic observed; physical-output=OFF");
        }
    }

    private void WriteDeviceReport()
    {
        if (!_virtualConfig.WriteDeviceStateReport) return;
        try { DeviceStateReport.Write(_writer.DirectoryPath, _virtualDevice, _apiTrace.Snapshot()); }
        catch (Exception ex) { Console.WriteLine($"[virtual] state report warning: {ex.Message}"); }
    }

    private static string DecodeText(byte[] payload)
    {
        if (payload.Length == 0) return string.Empty;
        try { return Encoding.UTF8.GetString(payload).TrimEnd('\0'); }
        catch { return Convert.ToHexString(payload.AsSpan(0, Math.Min(32, payload.Length))); }
    }

    private static string Describe(HookRecord r)
    {
        switch (r.Api)
        {
            case 30: return $"SETUPAPI {DecodeText(r.Payload)}";
            case 31: return $"HID ATTR {DecodeText(r.Payload)}";
            case 32: return $"CREATEFILE {DecodeText(r.Payload)}";
            case 33: return $"WRITEFILE {r.Payload.Length}B";
            case 34: return $"READFILE {r.Payload.Length}B";
            case 35: return $"DEVICEIO TX {DecodeText(r.Payload)}";
            case 36: return $"DEVICEIO RX {r.Payload.Length}B";
        }
        if (r.Api == RendererFrameDecoder.RendererFrameApi) return "RENDER_FRAME";
        if (r.Payload.Length == 0) return "empty";
        return r.Payload[0] switch
        {
            0x27 => r.Payload.Length == 2 && r.Payload[1] == 0 ? "GET_ALIVE RESPONSE" : "GET_ALIVE",
            0x77 => r.Payload.Length == 64 ? "GET_FULL_INFO RESPONSE" : "GET_FULL_INFO",
            0x78 => "BUFFER_RESPONSE",
            0x80 => "SET_OUTPUT",
            0x82 => "SET_ILDA_RATE",
            0x8A => "RINGBUFFER_QUERY",
            0x8D => "CLEAR_RINGBUFFER",
            0x9A => "SAMPLE_DATA_COMPRESSED",
            0xA0 => "SET_BUFFER_THRESHOLD",
            0xA9 => $"SAMPLE_DATA ({Math.Max(0, (r.Payload.Length - 4) / 10)} pts)",
            0xB0 => r.Payload.Length == 2 ? "SECURITY_REQUEST ACK" : "SECURITY_REQUEST",
            0xB1 => r.Payload.Length == 1 ? "SECURITY_RESPONSE QUERY" : "SECURITY_RESPONSE",
            _ => $"op=0x{r.Payload[0]:X2}"
        };
    }

    private static async Task ReadExactlyAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int pos = 0;
        while (pos < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(pos), ct);
            if (n == 0) throw new EndOfStreamException();
            pos += n;
        }
    }

    public void Dispose()
    {
        WriteDeviceReport();
        _translation?.Dispose();
        _renderer.Dispose();
    }
}
