using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cube7Bridge;

public enum VirtualDeviceStage
{
    Offline = 0,
    Discovered = 1,
    Identified = 2,
    AuthenticationObserved = 3,
    Ready = 4,
    Streaming = 5
}

public sealed class VirtualDeviceState
{
    private readonly object _sync = new();
    private readonly BridgeConfig.VirtualDeviceConfig _config;

    public VirtualDeviceState(BridgeConfig.VirtualDeviceConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public VirtualDeviceStage Stage { get; private set; } = VirtualDeviceStage.Offline;
    public bool PhysicalOutputEnabled => false;
    public bool AuthenticationAccepted { get; private set; }
    public long RendererFrames { get; private set; }
    public long RendererPoints { get; private set; }
    public string LastDetail { get; private set; } = "offline";
    public DateTime UpdatedUtc { get; private set; } = DateTime.UtcNow;

    public void MarkDiscovered(string detail = "0x27 accepted") => SetStage(VirtualDeviceStage.Discovered, detail);
    public void MarkIdentified(string detail = "0x77 full-info accepted") => SetStage(VirtualDeviceStage.Identified, detail);

    public void MarkAuthenticationObserved(string detail = "B0/B1 observed")
    {
        lock (_sync)
        {
            Stage = Max(Stage, VirtualDeviceStage.AuthenticationObserved);
            AuthenticationAccepted = false;
            LastDetail = detail;
            UpdatedUtc = DateTime.UtcNow;
        }
    }

    public void MarkReady(string detail = "device ready") => SetStage(VirtualDeviceStage.Ready, detail);

    public void ObserveRendererFrame(RendererFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_sync)
        {
            RendererFrames++;
            RendererPoints += frame.PointCount;
            Stage = Max(Stage, VirtualDeviceStage.Streaming);
            LastDetail = $"renderer frame points={frame.PointCount} rate={frame.Rate}";
            UpdatedUtc = DateTime.UtcNow;
        }
    }

    public VirtualDeviceSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new VirtualDeviceSnapshot(
                Stage,
                PhysicalOutputEnabled,
                AuthenticationAccepted,
                RendererFrames,
                RendererPoints,
                LastDetail,
                UpdatedUtc,
                _config.Enabled,
                _config.Network,
                _config.UsbHid);
        }
    }

    private void SetStage(VirtualDeviceStage stage, string detail)
    {
        lock (_sync)
        {
            Stage = Max(Stage, stage);
            LastDetail = detail;
            UpdatedUtc = DateTime.UtcNow;
        }
    }

    private static VirtualDeviceStage Max(VirtualDeviceStage a, VirtualDeviceStage b) => a >= b ? a : b;
}

public sealed record VirtualDeviceSnapshot(
    VirtualDeviceStage Stage,
    bool PhysicalOutputEnabled,
    bool AuthenticationAccepted,
    long RendererFrames,
    long RendererPoints,
    string LastDetail,
    DateTime UpdatedUtc,
    bool Enabled,
    bool Network,
    string UsbHidMode);

public sealed class VirtualProtocolEngine
{
    private readonly VirtualDeviceState _device;
    private readonly VirtualLaserCubeConfig _protocolConfig;
    private readonly VirtualLaserCubeState _protocolState;

    public VirtualProtocolEngine(VirtualDeviceState device, VirtualLaserCubeConfig protocolConfig)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _protocolConfig = protocolConfig ?? throw new ArgumentNullException(nameof(protocolConfig));
        _protocolState = new VirtualLaserCubeState { BufferFree = protocolConfig.BufferSize };
    }

    public VirtualLaserCubeReply Process(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return new VirtualLaserCubeReply(null, "empty");

        switch (payload[0])
        {
            case 0x27:
            {
                var reply = VirtualLaserCubeProtocol.HandleCommand(payload, _protocolState, _protocolConfig);
                if (reply.Response is [0x27, 0x00]) _device.MarkDiscovered();
                return reply;
            }
            case 0x77:
            {
                var reply = VirtualLaserCubeProtocol.HandleCommand(payload, _protocolState, _protocolConfig);
                if (reply.Response is { Length: 64 }) _device.MarkIdentified();
                return reply;
            }
            case 0xB0:
            case 0xB1:
                _device.MarkAuthenticationObserved($"auth opcode=0x{payload[0]:X2} captured; no synthetic success response");
                return new VirtualLaserCubeReply(null, "auth_trace_only");
            default:
                return VirtualLaserCubeProtocol.HandleCommand(payload, _protocolState, _protocolConfig);
        }
    }
}

public enum VirtualHandleKind
{
    Unknown = 0,
    Hid = 1,
    File = 2,
    Pipe = 3
}

public sealed record VirtualHandleEntry(nint Handle, string Path, VirtualHandleKind Kind, DateTime CreatedUtc);

public sealed class VirtualHandleTable
{
    private long _next = unchecked((long)0x00000000C7000000UL);
    private readonly ConcurrentDictionary<nint, VirtualHandleEntry> _handles = new();

    public nint Register(string path, VirtualHandleKind kind)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Virtual handle path is required.", nameof(path));
        nint handle = (nint)Interlocked.Increment(ref _next);
        if (!_handles.TryAdd(handle, new VirtualHandleEntry(handle, path, kind, DateTime.UtcNow)))
            throw new InvalidOperationException("Could not allocate virtual handle.");
        return handle;
    }

    public bool Contains(nint handle) => _handles.ContainsKey(handle);
    public bool TryGet(nint handle, out VirtualHandleEntry entry) => _handles.TryGetValue(handle, out entry!);
    public bool Close(nint handle) => _handles.TryRemove(handle, out _);
    public IReadOnlyCollection<VirtualHandleEntry> Snapshot() => _handles.Values.OrderBy(x => x.Handle.ToInt64()).ToArray();
}

public sealed record DeviceApiTraceEvent(DateTime TimeUtc, string Api, string Family, string Detail);
public sealed record DeviceApiTraceSnapshot(long Total, IReadOnlyDictionary<string, long> ByFamily, DeviceApiTraceEvent[] Recent);

public sealed class DeviceApiTrace
{
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DeviceApiTraceEvent> _recent = new();
    private long _total;
    private const int MaxRecent = 64;

    public void Observe(string api, string detail = "")
    {
        if (string.IsNullOrWhiteSpace(api)) throw new ArgumentException("API name is required.", nameof(api));
        string family = Classify(api);
        var ev = new DeviceApiTraceEvent(DateTime.UtcNow, api, family, detail ?? string.Empty);
        lock (_sync)
        {
            _total++;
            _families[family] = _families.TryGetValue(family, out long n) ? n + 1 : 1;
            _recent.Enqueue(ev);
            while (_recent.Count > MaxRecent) _recent.Dequeue();
        }
    }

    public DeviceApiTraceSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new DeviceApiTraceSnapshot(
                _total,
                new SortedDictionary<string, long>(_families, StringComparer.OrdinalIgnoreCase),
                _recent.ToArray());
        }
    }

    public static string Classify(string api)
    {
        if (api.StartsWith("SetupDi", StringComparison.OrdinalIgnoreCase)) return "SETUPAPI";
        if (api.StartsWith("HidD_", StringComparison.OrdinalIgnoreCase) || api.StartsWith("HidP_", StringComparison.OrdinalIgnoreCase)) return "HID";
        if (api.Equals("DeviceIoControl", StringComparison.OrdinalIgnoreCase)) return "DEVICEIO";
        if (api is "CreateFileW" or "CreateFileA" or "ReadFile" or "WriteFile" or "CloseHandle" or "GetOverlappedResult" or "CancelIo" or "CancelIoEx") return "FILEIO";
        return "OTHER";
    }
}

public static class DeviceStateReport
{
    public static string Write(string directory, VirtualDeviceState state, DeviceApiTraceSnapshot apiTrace)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "virtual-device-state.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            timeUtc = DateTime.UtcNow,
            device = state.Snapshot(),
            apiTrace,
            invariants = new
            {
                physicalOutputEnabled = false,
                syntheticAuthentication = false,
                interlockBypass = false
            }
        }, options));
        return path;
    }
}

public static class RendererPreview
{
    public static string WriteSvg(string directory, RendererFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "renderer-preview.svg");
        var normalized = DryRunTranslator.Translate(frame);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4095 4095\" width=\"1024\" height=\"1024\">");
        sb.AppendLine("  <rect width=\"4095\" height=\"4095\" fill=\"black\"/>");
        var lit = normalized.Points.Where(p => !p.Blank).ToArray();
        if (lit.Length > 0)
        {
            string points = string.Join(' ', lit.Select(p => $"{p.X},{4095 - p.Y}"));
            sb.Append("  <polyline fill=\"none\" stroke=\"white\" stroke-width=\"8\" points=\"");
            sb.Append(points);
            sb.AppendLine("\"/>");
        }
        sb.AppendLine("</svg>");
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
