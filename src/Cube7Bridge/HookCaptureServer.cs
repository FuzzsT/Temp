using System.IO.Pipes;

namespace Cube7Bridge;

public sealed class HookCaptureServer : IDisposable
{
    private readonly CaptureWriter _writer;
    private readonly LaserCubeHandshakeTracker _handshake;
    private readonly RendererFrameCapture _renderer;
    private readonly CubeFormat3Capture _format3;
    private long _rendererFrames;
    private bool _disposed;

    public HookCaptureServer(CaptureWriter writer)
    {
        _writer = writer;
        _handshake = new LaserCubeHandshakeTracker(writer.DirectoryPath);
        _renderer = new RendererFrameCapture(writer.DirectoryPath);
        _format3 = new CubeFormat3Capture(writer.DirectoryPath);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                "Cube7LaserOSBridge", PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024 * 1024, 1024 * 1024);
            Console.WriteLine("[hook] waiting for LaserOSHook.dll...");
            await pipe.WaitForConnectionAsync(ct);
            Console.WriteLine("[hook] connected");
            Console.WriteLine($"[stage] handshake summary: {Path.Combine(_writer.DirectoryPath, "handshake-summary.ndjson")}");
            Console.WriteLine($"[renderer] frame summary: {_renderer.SummaryPath}");
            Console.WriteLine($"[format3] dry-run summary: {_format3.SummaryPath}");
            Console.WriteLine($"[format3] dry-run binary: {_format3.RawPath}");
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

                    if (RendererFrameDecoder.TryDecode(record, out var frame) && frame is not null)
                    {
                        _renderer.Write(record, frame);
                        _format3.Write(frame, blackout: false);
                        long n = Interlocked.Increment(ref _rendererFrames);
                        if (n <= 5 || n % 60 == 0)
                            Console.WriteLine($"[renderer] frame={n} points={frame.PointCount} rate={frame.Rate} flags=0x{frame.Flags:X} format3=dry-run");
                        continue;
                    }

                    Console.WriteLine($"[{(record.Direction == 1 ? "TX" : "RX")}] {record.RemoteAddress}:{record.Port} {record.Payload.Length}B {Describe(record)}");
                    string? transition = _handshake.Observe(record);
                    if (transition is not null)
                        Console.WriteLine(transition);
                }
            }
            catch (EndOfStreamException) { }
            catch (IOException ex) { Console.WriteLine($"[hook] disconnected: {ex.Message}"); }
        }
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
        if (_disposed) return;
        _disposed = true;
        _renderer.Dispose();
        _format3.Dispose();
    }
}
