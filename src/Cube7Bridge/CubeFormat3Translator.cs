using System.Buffers.Binary;

namespace Cube7Bridge;

public sealed record CubeFormat3Frame(
    int Rate,
    int PointCount,
    byte[] LayerPayload,
    int ExactColors,
    int QuantizedColors,
    float MinX,
    float MaxX,
    float MinY,
    float MaxY);

/// <summary>
/// Converts captured ldRendererOpenlase points into the Cube application
/// dataFormatType=3 layer representation recovered from Cube.zip.
/// This class only serializes bytes; it does not perform BLE writes or change
/// optical-output state.
/// </summary>
public static class CubeFormat3Translator
{
    private const int MaxPoints = ushort.MaxValue;
    private const int MaxRepeat = 4096;

    private sealed record ExpandedPoint(float X, float Y, byte R, byte G, byte B);

    public static CubeFormat3Frame Translate(RendererFrame frame, bool invertY)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var expanded = Expand(frame.Points, invertY);
        if (expanded.Count > MaxPoints)
            throw new InvalidDataException($"Renderer frame expands to {expanded.Count} points; Cube type3 layer count is limited to {MaxPoints}.");

        var payload = new byte[2 + expanded.Count * 6];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), checked((ushort)expanded.Count));

        int exactColors = 0;
        int quantizedColors = 0;
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;

        for (int i = 0; i < expanded.Count; i++)
        {
            var p = expanded[i];
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);

            ushort x = MapCoordinate(p.X);
            ushort y = MapCoordinate(p.Y);
            byte flags = BuildFlags(expanded, i);

            int paletteIndex = CubeAppPalette.IndexOfExact(p.R, p.G, p.B);
            if (paletteIndex >= 0)
                exactColors++;
            else
            {
                paletteIndex = CubeAppPalette.FindNearestIndex(p.R, p.G, p.B);
                quantizedColors++;
            }
            if ((uint)paletteIndex > byte.MaxValue)
                throw new InvalidDataException($"Palette index {paletteIndex} does not fit dataFormatType=3 byte field.");

            int off = 2 + i * 6;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off, 2), x);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(off + 2, 2), y);
            payload[off + 4] = flags;
            payload[off + 5] = (byte)paletteIndex;
        }

        if (expanded.Count == 0)
            minX = maxX = minY = maxY = 0;

        return new CubeFormat3Frame(
            frame.Rate,
            expanded.Count,
            payload,
            exactColors,
            quantizedColors,
            minX,
            maxX,
            minY,
            maxY);
    }

    private static List<ExpandedPoint> Expand(RendererPoint[] points, bool invertY)
    {
        var result = new List<ExpandedPoint>();
        foreach (var p in points)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                continue;

            int repeat = p.Param <= 0 ? 1 : Math.Clamp(p.Param, 1, MaxRepeat);
            if (result.Count + repeat > MaxPoints)
                throw new InvalidDataException("Renderer repeat expansion exceeds Cube type3 layer limit.");

            byte r = (byte)((p.Color >> 16) & 0xFF);
            byte g = (byte)((p.Color >> 8) & 0xFF);
            byte b = (byte)(p.Color & 0xFF);
            float y = invertY ? -p.Y : p.Y;
            for (int i = 0; i < repeat; i++)
                result.Add(new ExpandedPoint(p.X, y, r, g, b));
        }
        return result;
    }

    public static ushort MapCoordinate(float value)
    {
        if (!float.IsFinite(value)) return 0;
        double clamped = Math.Clamp((double)value, -1.0, 1.0);
        double scaled = (clamped + 1.0) * 32767.5;
        return checked((ushort)Math.Clamp((int)Math.Floor(scaled + 0.5), 0, 65535));
    }

    private static byte BuildFlags(IReadOnlyList<ExpandedPoint> points, int index)
    {
        if (points.Count == 0) return 0;

        int state = index == 0 ? 0x40 : 0;
        int end = index == points.Count - 1 ? 0x80 : 0;
        if (end != 0) state = 0;

        double angle = InteriorAngle(points, index);
        int angleBits = double.IsFinite(angle)
            ? Math.Clamp((int)Math.Floor(angle / 180.0 * 63.0), 0, 63)
            : 0;
        return checked((byte)(angleBits | state | end));
    }

    private static double InteriorAngle(IReadOnlyList<ExpandedPoint> points, int index)
    {
        if (points.Count <= 1) return 180.0;
        var current = points[index];
        var prev = index == 0 ? points[^1] : points[index - 1];
        var next = index + 1 == points.Count ? points[0] : points[index + 1];

        double ax = prev.X - current.X;
        double ay = prev.Y - current.Y;
        double bx = next.X - current.X;
        double by = next.Y - current.Y;
        double den = Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by);
        if (den <= double.Epsilon) return double.NaN;
        double dot = (ax * bx + ay * by) / den;
        dot = Math.Clamp(dot, -1.0, 1.0);
        return Math.Acos(dot) * (180.0 / Math.PI);
    }
}
