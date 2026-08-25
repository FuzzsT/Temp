using System.Text.Json;

namespace Cube7Bridge;

public sealed record RendererTapHealthSnapshot(
    string State,
    string Detail,
    long Frames,
    long Points,
    DateTime? HookConnectedUtc,
    DateTime? LastFrameUtc,
    TimeSpan HookAge,
    TimeSpan? LastFrameAge);

public static class RendererTapHealthEvaluator
{
    public static RendererTapHealthSnapshot Evaluate(
        bool hookConnected,
        DateTime? hookConnectedUtc,
        RendererStatsSnapshot stats,
        DateTime nowUtc,
        TimeSpan noFrameTimeout)
    {
        if (!hookConnected || hookConnectedUtc is null)
            return new RendererTapHealthSnapshot("WAITING", "hook pipe not connected", stats.Frames, stats.Points, hookConnectedUtc, stats.LastFrameUtc, TimeSpan.Zero, null);

        var hookAge = nowUtc - hookConnectedUtc.Value;
        TimeSpan? frameAge = stats.LastFrameUtc is null ? null : nowUtc - stats.LastFrameUtc.Value;

        if (stats.Frames > 0)
        {
            string state = frameAge <= noFrameTimeout ? "ACTIVE" : "STALLED";
            string detail = state == "ACTIVE"
                ? $"frames={stats.Frames} points={stats.Points} last={frameAge?.TotalSeconds:F1}s"
                : $"renderer frames stopped; last={frameAge?.TotalSeconds:F1}s ago";
            return new RendererTapHealthSnapshot(state, detail, stats.Frames, stats.Points, hookConnectedUtc, stats.LastFrameUtc, hookAge, frameAge);
        }

        if (hookAge >= noFrameTimeout)
            return new RendererTapHealthSnapshot(
                "ERROR",
                $"hook connected for {hookAge.TotalSeconds:F1}s but no renderer frames arrived; check ldRendererOpenlase hook path",
                0, 0, hookConnectedUtc, null, hookAge, null);

        return new RendererTapHealthSnapshot(
            "WAITING",
            $"hook connected; waiting for first renderer frame ({hookAge.TotalSeconds:F1}s)",
            0, 0, hookConnectedUtc, null, hookAge, null);
    }
}

public sealed class RendererTapHealthWriter
{
    private readonly string _path;
    private readonly object _sync = new();

    public RendererTapHealthWriter(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = System.IO.Path.Combine(directory, "renderer-hook-health.json");
    }

    public string Path => _path;

    public void Write(RendererTapHealthSnapshot snapshot)
    {
        lock (_sync)
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, true);
        }
    }
}
