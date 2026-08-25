using System.Text.Json;

namespace Cube7Bridge;

public sealed class CaptureWriter : IDisposable
{
    private readonly StreamWriter _json;
    private readonly FileStream _raw;
    private readonly object _sync = new();

    public string DirectoryPath { get; }

    public CaptureWriter(string baseDirectory)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        DirectoryPath = Path.GetFullPath(Path.Combine(baseDirectory, stamp));
        Directory.CreateDirectory(DirectoryPath);
        _json = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "capture.ndjson"), FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _raw = new FileStream(Path.Combine(DirectoryPath, "capture.rawbin"), FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public void Write(HookRecord r)
    {
        lock (_sync)
        {
            long rawOffset = _raw.Position;
            _raw.Write(r.Payload);
            _raw.Flush();
            var item = new
            {
                timeUtc = DateTime.FromFileTimeUtc((long)r.Timestamp100ns),
                direction = r.Direction == 1 ? "TX" : "RX",
                api = r.Api,
                pid = r.Pid,
                remote = r.RemoteAddress,
                port = r.Port,
                payloadLength = r.Payload.Length,
                rawOffset,
                hexPrefix = Convert.ToHexString(r.Payload.AsSpan(0, Math.Min(r.Payload.Length, 64))),
                decoded = LaserCubeDecoder.Decode(r)
            };
            _json.WriteLine(JsonSerializer.Serialize(item));
        }
    }

    public void Dispose()
    {
        _json.Dispose();
        _raw.Dispose();
    }
}
