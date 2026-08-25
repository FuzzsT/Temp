using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cube7Bridge;

public sealed record LaserOsPreflightResult(
    string Path,
    bool Exists,
    string? ExpectedSha256,
    string? ActualSha256,
    bool HashMatch,
    string State,
    string Detail);

public static class LaserOsPreflight
{
    public static LaserOsPreflightResult Verify(string? path, string? expectedSha256)
    {
        string resolved = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        string? expected = NormalizeHash(expectedSha256);
        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
            return new LaserOsPreflightResult(resolved, false, expected, null, false, "BLOCKED", "LaserOS.exe not found");

        string actual;
        using (var stream = File.OpenRead(resolved))
            actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        bool match = expected is null || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        return new LaserOsPreflightResult(
            resolved,
            true,
            expected,
            actual,
            match,
            match ? "OK" : "BLOCKED",
            expected is null ? "LaserOS exists; SHA not pinned" : match ? "LaserOS SHA-256 verified" : "LaserOS SHA-256 mismatch");
    }

    private static string? NormalizeHash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string hash = value.Trim().ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new FormatException("Expected LaserOS SHA-256 must contain exactly 64 hexadecimal characters.");
        return hash;
    }
}

public sealed record PipelineStatusEntry(string State, string Detail, DateTime UpdatedUtc);

public sealed class PipelineStatus
{
    private readonly ConcurrentDictionary<string, PipelineStatusEntry> _items = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string key, string state, string detail = "") =>
        _items[key] = new PipelineStatusEntry(state, detail, DateTime.UtcNow);

    public PipelineStatusEntry? Get(string key) => _items.TryGetValue(key, out var value) ? value : null;

    public IReadOnlyDictionary<string, PipelineStatusEntry> Snapshot() =>
        new SortedDictionary<string, PipelineStatusEntry>(_items, StringComparer.OrdinalIgnoreCase);

    public string RenderText()
    {
        var lines = Snapshot().Select(kv => $"{kv.Key,-20} {kv.Value.State,-10} {kv.Value.Detail}".TrimEnd());
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed record RendererStatsSnapshot(long Frames, long Points, int LastRate, int LastPoints, int MaxPoints, DateTime? LastFrameUtc);

public sealed class RendererFrameStatistics
{
    private readonly object _sync = new();
    private long _frames;
    private long _points;
    private int _lastRate;
    private int _lastPoints;
    private int _maxPoints;
    private DateTime? _lastFrameUtc;

    public void Observe(RendererFrame frame)
    {
        lock (_sync)
        {
            _frames++;
            _points += frame.PointCount;
            _lastRate = frame.Rate;
            _lastPoints = frame.PointCount;
            _maxPoints = Math.Max(_maxPoints, frame.PointCount);
            _lastFrameUtc = DateTime.UtcNow;
        }
    }

    public RendererStatsSnapshot Snapshot()
    {
        lock (_sync)
            return new RendererStatsSnapshot(_frames, _points, _lastRate, _lastPoints, _maxPoints, _lastFrameUtc);
    }
}

public sealed record Cube7NormalizedPoint(ushort X, ushort Y, ushort R, ushort G, ushort B, bool Blank, int Param);
public sealed record Cube7NormalizedFrame(int Rate, ulong RendererId, Cube7NormalizedPoint[] Points, bool PhysicalOutputEnabled)
{
    public int PointCount => Points.Length;
}

public static class DryRunTranslator
{
    public static Cube7NormalizedFrame Translate(RendererFrame frame)
    {
        var points = new Cube7NormalizedPoint[frame.PointCount];
        for (int i = 0; i < points.Length; i++)
        {
            var source = frame.Points[i];
            ushort x = NormalizeAxis(source.X);
            ushort y = NormalizeAxis(source.Y);
            byte r8 = (byte)((source.Color >> 16) & 0xFF);
            byte g8 = (byte)((source.Color >> 8) & 0xFF);
            byte b8 = (byte)(source.Color & 0xFF);
            ushort r = Expand8To12(r8);
            ushort g = Expand8To12(g8);
            ushort b = Expand8To12(b8);
            bool blank = r == 0 && g == 0 && b == 0;
            points[i] = new Cube7NormalizedPoint(x, y, r, g, b, blank, source.Param);
        }
        return new Cube7NormalizedFrame(frame.Rate, frame.RendererId, points, PhysicalOutputEnabled: false);
    }

    private static ushort NormalizeAxis(float value)
    {
        if (!float.IsFinite(value)) value = 0;
        double clamped = Math.Clamp(value, -1f, 1f);
        return (ushort)Math.Clamp((int)Math.Round((clamped + 1d) * 0.5d * 4095d), 0, 4095);
    }

    private static ushort Expand8To12(byte value) => (ushort)((value << 4) | (value >> 4));
}

public sealed class DryRunTranslationCapture : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _sync = new();
    public string Path { get; }

    public DryRunTranslationCapture(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "translator-dryrun.ndjson");
        _writer = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Write(Cube7NormalizedFrame frame)
    {
        lock (_sync)
        {
            var preview = frame.Points.Take(8).Select(p => new { p.X, p.Y, p.R, p.G, p.B, p.Blank, p.Param }).ToArray();
            _writer.WriteLine(JsonSerializer.Serialize(new
            {
                timeUtc = DateTime.UtcNow,
                frame.Rate,
                rendererId = $"0x{frame.RendererId:X}",
                points = frame.PointCount,
                physicalOutputEnabled = frame.PhysicalOutputEnabled,
                preview
            }));
        }
    }

    public void Dispose() => _writer.Dispose();
}

public static class SupportBundle
{
    public static string Create(
        string captureDirectory,
        string configPath,
        PipelineStatus status,
        LaserOsPreflightResult preflight,
        RendererStatsSnapshot rendererStats)
    {
        Directory.CreateDirectory(captureDirectory);
        string bundlePath = System.IO.Path.Combine(captureDirectory, "support-bundle.zip");
        var captureFiles = Directory.EnumerateFiles(captureDirectory, "*", SearchOption.AllDirectories)
            .Where(p => !string.Equals(System.IO.Path.GetFullPath(p), System.IO.Path.GetFullPath(bundlePath), StringComparison.OrdinalIgnoreCase))
            .Where(IsSafeDiagnosticFile)
            .ToArray();

        if (File.Exists(bundlePath)) File.Delete(bundlePath);
        using var zip = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
        foreach (string file in captureFiles)
        {
            string rel = System.IO.Path.GetRelativePath(captureDirectory, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, "captures/" + rel, CompressionLevel.Optimal);
        }

        if (File.Exists(configPath))
            zip.CreateEntryFromFile(configPath, "config/config.json", CompressionLevel.Optimal);

        WriteJson(zip, "support/status.json", status.Snapshot());
        WriteJson(zip, "support/preflight.json", preflight);
        WriteJson(zip, "support/renderer-stats.json", rendererStats);
        WriteJson(zip, "support/safety.json", new
        {
            physicalOutputEnabled = false,
            blePayloadWrites = false,
            interlockBypass = false,
            mode = "HARDENED-DRYRUN"
        });
        return bundlePath;
    }

    private static bool IsSafeDiagnosticFile(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        string name = System.IO.Path.GetFileName(path);
        if (name.Equals("support-bundle.zip", StringComparison.OrdinalIgnoreCase)) return false;
        // Raw payload captures may contain authentication material. Support bundle defaults to summaries/logs only.
        return ext is ".ndjson" or ".json" or ".log" or ".txt";
    }

    private static void WriteJson(ZipArchive zip, string name, object value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }
}
