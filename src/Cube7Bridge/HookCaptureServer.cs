using System.IO.Pipes;

namespace Cube7Bridge;

public sealed class HookCaptureServer
{
    private readonly CaptureWriter _writer;
    public HookCaptureServer(CaptureWriter writer) => _writer = writer;

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
                    Console.WriteLine($"[{(record.Direction == 1 ? "TX" : "RX")}] {record.RemoteAddress}:{record.Port} {record.Payload.Length}B {Describe(record)}");
                }
            }
            catch (EndOfStreamException) { }
            catch (IOException ex) { Console.WriteLine($"[hook] disconnected: {ex.Message}"); }
        }
    }

    private static string Describe(HookRecord r)
    {
        if (r.Payload.Length == 0) return "empty";
        return r.Payload[0] switch
        {
            0x27 => r.Payload.Length == 2 && r.Payload[1] == 0 ? "GET_ALIVE RESPONSE" : "GET_ALIVE",
            0x77 => "GET_FULL_INFO",
            0x78 => "BUFFER_RESPONSE",
            0x80 => "SET_OUTPUT",
            0x82 => "SET_ILDA_RATE",
            0x8A => "RINGBUFFER_QUERY",
            0x8D => "CLEAR_RINGBUFFER",
            0x9A => "SAMPLE_DATA_COMPRESSED",
            0xA0 => "SET_BUFFER_THRESHOLD",
            0xA9 => $"SAMPLE_DATA ({Math.Max(0, (r.Payload.Length - 4) / 10)} pts)",
            0xB0 => "SECURITY_REQUEST",
            0xB1 => "SECURITY_RESPONSE",
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
}
