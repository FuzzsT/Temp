using System.Buffers.Binary;
using System.Text.Json;

namespace Cube7Bridge;

public sealed record RendererPoint(float X, float Y, uint Color, int Param);
public sealed record RendererFrame(int Rate, ushort Flags, ulong RendererId, RendererPoint[] Points)
{
    public int PointCount => Points.Length;
}

public static class RendererFrameDecoder
{
    public const byte RendererFrameApi = 20;
    public const uint Magic = 0x4D524652; // bytes RFRM
    public const int HeaderSize = 24;
    public const int PointSize = 16;

    public static bool TryDecode(HookRecord record, out RendererFrame? frame)
    {
        frame = null;
        if (record.Api != RendererFrameApi || record.Payload.Length < HeaderSize) return false;
        var p = record.Payload.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(p[0..4]) != Magic) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(p[4..6]) != 1) return false;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(p[6..8]);
        int rate = BinaryPrimitives.ReadInt32LittleEndian(p[8..12]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(p[12..16]);
        ulong rendererId = BinaryPrimitives.ReadUInt64LittleEndian(p[16..24]);
        if (count > 65500) return false;
        int pointCount = checked((int)count);
        int expected = checked(HeaderSize + pointCount * PointSize);
        if (record.Payload.Length != expected) return false;

        var points = new RendererPoint[pointCount];
        int off = HeaderSize;
        for (int i = 0; i < points.Length; i++, off += PointSize)
        {
            float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(p.Slice(off, 4)));
            float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(p.Slice(off + 4, 4)));
            uint color = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(off + 8, 4));
            int param = BinaryPrimitives.ReadInt32LittleEndian(p.Slice(off + 12, 4));
            points[i] = new RendererPoint(x, y, color, param);
        }
        frame = new RendererFrame(rate, flags, rendererId, points);
        return true;
    }
}

public sealed class RendererFrameCapture : IDisposable
{
    private readonly StreamWriter _summary;
    private readonly FileStream _raw;
    private readonly object _sync = new();
    public string SummaryPath { get; }
    public string RawPath { get; }

    public RendererFrameCapture(string directory)
    {
        Directory.CreateDirectory(directory);
        SummaryPath = Path.Combine(directory, "renderer-frames.ndjson");
        RawPath = Path.Combine(directory, "renderer-frames.bin");
        _summary = new StreamWriter(new FileStream(SummaryPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        _raw = new FileStream(RawPath, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    public void Write(HookRecord record, RendererFrame frame)
    {
        lock (_sync)
        {
            long rawOffset = _raw.Position;
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)record.Payload.Length);
            _raw.Write(length);
            _raw.Write(record.Payload);
            _raw.Flush();

            var preview = frame.Points.Take(8).Select(p => new { x = p.X, y = p.Y, color = $"0x{p.Color:X8}", param = p.Param }).ToArray();
            var row = new
            {
                timeUtc = DateTime.FromFileTimeUtc((long)record.Timestamp100ns),
                rate = frame.Rate,
                flags = frame.Flags,
                rendererId = $"0x{frame.RendererId:X}",
                points = frame.PointCount,
                rawOffset,
                rawLength = record.Payload.Length,
                preview
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
