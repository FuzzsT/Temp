using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cube7Bridge;

/// <summary>
/// Persists renderer frames after Cube.zip-compatible dataFormatType=3 translation.
/// This class is intentionally capture-only: it never opens BLE and never enables optical output.
/// </summary>
public sealed class CubeFormat3Capture : IDisposable
{
    private readonly StreamWriter _summary;
    private readonly FileStream _raw;
    private readonly object _sync = new();

    public string SummaryPath { get; }
    public string RawPath { get; }

    public CubeFormat3Capture(string directory)
    {
        Directory.CreateDirectory(directory);
        SummaryPath = Path.Combine(directory, "cube-format3.ndjson");
        RawPath = Path.Combine(directory, "cube-format3.bin");
        _summary = new StreamWriter(new FileStream(SummaryPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _raw = new FileStream(RawPath, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public void Write(RendererFrame frame, bool blackout)
    {
        var converted = CubeFormat3Translator.Translate(frame, blackout);
        var payload = converted.LayerPayload;

        lock (_sync)
        {
            long rawOffset = _raw.Position;
            Span<byte> prefix = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));
            _raw.Write(prefix);
            _raw.Write(payload);
            _raw.Flush();

            var row = new
            {
                timeUtc = DateTime.UtcNow,
                rendererId = $"0x{frame.RendererId:X}",
                rate = frame.Rate,
                flags = frame.Flags,
                pointCount = converted.PointCount,
                payloadLength = payload.Length,
                payloadSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                rawOffset,
                blackout,
                dryRun = true,
                physicalOutput = false,
                dataFormatType = 3,
                paletteEntries = CubeAppPalette.Count
            };
            _summary.WriteLine(JsonSerializer.Serialize(row));
        }
    }

    public void Dispose()
    {
        _summary.Dispose();
        _raw.Dispose();
    }
}
