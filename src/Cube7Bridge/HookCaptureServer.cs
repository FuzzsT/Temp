using System.IO.Pipes;

namespace Cube7Bridge;

public sealed class HookCaptureServer : IDisposable
{
    private readonly CaptureWriter _writer;
    private readonly LaserCubeHandshakeTracker _handshake;
    private readonly RendererFrameCapture _renderer;
    private readonly PipelineStatus _status;
    private readonly RendererFrameStatistics _stats;
    private readonly RendererTapHealthWriter _healthWriter;
    private readonly bool _writeDryRunTranslation;
    private DryRunTranslationCapture? _translation;
    private long _rendererFrames;
    private DateTime? _hookConnectedUtc;

    public HookCaptureServer(CaptureWriter writer, PipelineStatus status, RendererFrameStatistics stats, bool writeDryRunTranslation)
    {
        _writer = writer;
        _status = status;
        _stats = stats;
        _writeDryRunTranslation = writeDryRunTranslation;
        _handshake = new LaserCubeHandshakeTracker(writer.DirectoryPath);
        _renderer = new RendererFrameCapture(writer.DirectoryPath);
        _healthWriter = new RendererTapHealthWriter(writer.DirectoryPath);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _status.Set("LASEROS_HOOK", "WAITING", "waiting for injected hook pipe");
        _status.Set("RENDERER_TAP", "WAITING", "no renderer frames observed");
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                "Cube7LaserOSBridge", PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024 * 1024, 1024 * 1024);
            Console.WriteLine("[hook] waiting for LaserOSHook.dll...");
            await pipe.WaitForConnectionAsync(ct);
            _hookConnectedUtc = DateTime.UtcNow;
            Console.WriteLine("[hook] connected");
            _status.Set("LASEROS_HOOK", "OK", "LaserOSHook.dll connected");
            Console.WriteLine($"[stage] handshake summary: {Path.Combine(_writer.DirectoryPath, "handshake-summary.ndjson")}");
            Console.WriteLine($"[renderer] frame summary (created on first frame): {_renderer.SummaryPath}");
            Console.WriteLine($"[renderer] hook health: {_healthWriter.Path}");
            Console.WriteLine("[renderer] tap is capture-only; physical output remains OFF.");

            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watchdog = WatchRendererAsync(connectionCts.Token);
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

                    if (RendererFrameDecoder.TryDecode(record, out var frame) && frame is not null)
                    {
                        _renderer.Write(record, frame);
                        _stats.Observe(frame);
                        var normalized = DryRunTranslator.Translate(frame);
                        if (_writeDryRunTranslation)
                        {
                            _translation ??= new DryRunTranslationCapture(_writer.DirectoryPath);
                            _translation.Write(normalized);
                        }
                        long n = Interlocked.Increment(ref _rendererFrames);
                        var snap = _stats.Snapshot();
                        _status.Set("RENDERER_TAP", "ACTIVE", $"frames={snap.Frames} rate={frame.Rate}pps points={frame.PointCount} max={snap.MaxPoints}");
                        _status.Set("TRANSLATOR", "DRY-RUN", $"normalized={normalized.PointCount} physical-output=OFF");
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
            finally
            {
                connectionCts.Cancel();
                try { await watchdog; } catch (OperationCanceledException) { }
                _hookConnectedUtc = null;
            }
        }
    }

    private async Task WatchRendererAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(10);
        while (!ct.IsCancellationRequested)
        {
            var health = RendererTapHealthEvaluator.Evaluate(
                hookConnected: _hookConnectedUtc is not null,
                hookConnectedUtc: _hookConnectedUtc,
                stats: _stats.Snapshot(),
                nowUtc: DateTime.UtcNow,
                noFrameTimeout: timeout);
            _healthWriter.Write(health);

            if (health.State is "ERROR" or "STALLED")
                _status.Set("RENDERER_TAP", health.State, health.Detail);
            else if (health.State == "ACTIVE")
                _status.Set("RENDERER_TAP", "ACTIVE", health.Detail);
            else if (_stats.Snapshot().Frames == 0)
                _status.Set("RENDERER_TAP", "WAITING", health.Detail);

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private void UpdateHandshakeStatus(string transition)
    {
        if (transition.Contains("DISCOVERY_REQUEST", StringComparison.Ordinal))
            _status.Set("DISCOVERY_0x27", "ACTIVE", "request observed");
        if (transition.Contains("DISCOVERY_ACCEPTED", StringComparison.Ordinal))
            _status.Set("DISCOVERY_0x27", "OK", "27 00 accepted");
        if (transition.Contains("FULL_INFO_REQUEST", StringComparison.Ordinal))
            _status.Set("FULL_INFO_0x77", "ACTIVE", "request observed");
        if (transition.Contains("FULL_INFO_ACCEPTED", StringComparison.Ordinal))
            _status.Set("FULL_INFO_0x77", "OK", "64-byte response accepted");
        if (transition.Contains("AUTH_REQUEST", StringComparison.Ordinal))
            _status.Set("AUTH_B0_B1", "ACTIVE", "B0/B1 handshake observed");
        if (transition.Contains("AUTH_RESPONSE_CAPTURED", StringComparison.Ordinal))
            _status.Set("AUTH_B0_B1", "CAPTURED", "response captured; LaserOS validation pending");
        if (transition.Contains("POST_AUTH_DEVICE_TRAFFIC", StringComparison.Ordinal))
            _status.Set("AUTH_B0_B1", "OK", "post-auth device traffic observed");
    }

    private static string Describe(HookRecord r)
    {
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
        _translation?.Dispose();
        _renderer.Dispose();
    }
}
