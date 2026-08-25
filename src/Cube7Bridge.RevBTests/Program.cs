using Cube7Bridge;

static void Require(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
}

var now = DateTime.UtcNow;
var empty = new RendererStatsSnapshot(0, 0, 0, 0, 0, null);
var waiting = RendererTapHealthEvaluator.Evaluate(false, null, empty, now, TimeSpan.FromSeconds(10));
Require(waiting.State == "WAITING", "renderer health waits before hook connection");

var error = RendererTapHealthEvaluator.Evaluate(true, now.AddSeconds(-11), empty, now, TimeSpan.FromSeconds(10));
Require(error.State == "ERROR", "renderer health reports zero-frame timeout");

var activeStats = new RendererStatsSnapshot(2, 4, 30000, 2, 2, now.AddSeconds(-1));
var active = RendererTapHealthEvaluator.Evaluate(true, now.AddSeconds(-5), activeStats, now, TimeSpan.FromSeconds(10));
Require(active.State == "ACTIVE", "renderer health becomes active after frames");

string root = Path.Combine(Path.GetTempPath(), "Cube7Bridge-revb-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var capture = new RendererFrameCapture(root);
    Require(!File.Exists(capture.SummaryPath) && !File.Exists(capture.RawPath), "renderer files are lazy before first frame");

    var frame = new RendererFrame(30000, 0, 1,
    [
        new RendererPoint(-0.25f, 0.25f, 0x00FF0000, 0),
        new RendererPoint(0.25f, -0.25f, 0x0000FF00, 0)
    ]);
    var payload = new byte[RendererFrameDecoder.HeaderSize + frame.PointCount * RendererFrameDecoder.PointSize];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0,4), RendererFrameDecoder.Magic);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4,2), 1);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8,4), frame.Rate);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12,4), (uint)frame.PointCount);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16,8), frame.RendererId);
    int off = RendererFrameDecoder.HeaderSize;
    foreach (var p in frame.Points)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(off,4), BitConverter.SingleToInt32Bits(p.X));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(off+4,4), BitConverter.SingleToInt32Bits(p.Y));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(off+8,4), p.Color);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(off+12,4), p.Param);
        off += RendererFrameDecoder.PointSize;
    }
    var record = new HookRecord(1, RendererFrameDecoder.RendererFrameApi, 1, (ulong)now.ToFileTimeUtc(), 0, 0, new byte[16], payload);
    capture.Write(record, frame);
    Require(File.Exists(capture.SummaryPath) && new FileInfo(capture.SummaryPath).Length > 0, "renderer summary created after first frame");
    Require(File.Exists(capture.RawPath) && new FileInfo(capture.RawPath).Length > 0, "renderer raw created after first frame");

    var cfg = new VirtualLaserCubeConfig { BufferSize = 6000, DacRate = 30000, MaxDacRate = 30000 };
    var state = new VirtualLaserCubeState { BufferFree = cfg.BufferSize };
    Require(VirtualLaserCubeProtocol.HandleCommand([0x27], state, cfg).Response is [0x27, 0x00], "diagnostic discovery response");
    var info = VirtualLaserCubeProtocol.HandleCommand([0x77], state, cfg).Response;
    Require(info is { Length: 64 } && info[0] == 0x77, "diagnostic full-info response");
    VirtualLaserCubeProtocol.HandleCommand([0x80, 0x01], state, cfg);
    Require(state.OutputRequested && !state.PhysicalOutputEnabled, "virtual output request never enables physical output");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

Console.WriteLine("REV-B TEST PASS: renderer watchdog + lazy capture + diagnostic discovery; physical-output=DISABLED");
