using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Cube7Bridge;

public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        try
        {
            ProtocolModelTests();
            BluetoothAddressTests();
            RendererFrameTests();
            HandshakeTrackerTests();
            HardenedDryRunTests();
            await UdpServerTestsAsync();
            Console.WriteLine("SELFTEST PASS: hardened preflight + pipeline status + renderer stats + dry-run translator + support bundle + renderer tap + protocol; physical-output=DISABLED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SELFTEST FAIL: {ex.Message}");
            return 1;
        }
    }

    private static void ProtocolModelTests()
    {
        var cfg = new VirtualLaserCubeConfig { BufferSize = 6000, DacRate = 30000, MaxDacRate = 30000, ModelName = "LaserCube Virtual CUBE7" };
        var state = new VirtualLaserCubeState { BufferFree = cfg.BufferSize };
        var alive = VirtualLaserCubeProtocol.HandleCommand([0x27], state, cfg);
        Require(alive.Response is [0x27, 0x00], "0x27 alive response must be 27 00");
        var info = VirtualLaserCubeProtocol.BuildFullInfo(cfg, state);
        Require(info.Length == 64, "0x77 response length");
        Require(info[0] == 0x77, "0x77 opcode");
        Require(info[1] == 0x00 && info[2] == 0x00, "0x77 result/payload-version bytes");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(10, 4)) == 30000, "DAC rate");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(21, 2)) == 6000, "buffer size");
        var r78 = VirtualLaserCubeProtocol.HandleCommand([0x78, 0x01], state, cfg);
        Require(r78.Response is [0x78], "0x78 ack");
        Require(state.BufferResponseEnabled, "buffer responses enabled");
        var r8a = VirtualLaserCubeProtocol.HandleCommand([0x8A], state, cfg);
        Require(r8a.Response is { Length: 4 } && r8a.Response[0] == 0x8A, "0x8A response");
        Require(BinaryPrimitives.ReadUInt16LittleEndian(r8a.Response.AsSpan(2, 2)) == 6000, "0x8A free buffer");
        var r80 = VirtualLaserCubeProtocol.HandleCommand([0x80, 0x01], state, cfg);
        Require(r80.Response is [0x80], "0x80 ack");
        Require(state.OutputRequested, "virtual output request tracked");
        Require(!state.PhysicalOutputEnabled, "physical output remains disabled");
        var sample = new byte[24];
        sample[0] = 0xA9; sample[1] = 0x00; sample[2] = 0x12; sample[3] = 0x34;
        var ra9 = VirtualLaserCubeProtocol.HandleCommand(sample, state, cfg);
        Require(ra9.PointCount == 2, "0xA9 point count");
        Require(ra9.Response is { Length: 3 } && ra9.Response[0] == 0xA9, "0xA9 buffer response");
        Require(state.LastMessageNumber == 0x12 && state.LastFrameNumber == 0x34, "0xA9 sequence tracking");
    }

    private static void BluetoothAddressTests()
    {
        const string expected = "E4:66:E5:D2:6E:38";
        ulong parsed = BridgeConfig.ParseBluetoothAddress(expected);
        Require(parsed == 0xE466E5D26E38UL, "BLE exact MAC parse");
        Require(BridgeConfig.FormatBluetoothAddress(parsed) == expected, "BLE exact MAC round-trip");
    }

    private static RendererFrame BuildRendererFrame()
    {
        return new RendererFrame(30000, 3, 0x12345678UL,
        [
            new RendererPoint(-0.5f, 0.25f, 0x00FF0000, 1),
            new RendererPoint(0.75f, -0.125f, 0x0000FF00, 2)
        ]);
    }

    private static void RendererFrameTests()
    {
        var payload = new byte[RendererFrameDecoder.HeaderSize + 2 * RendererFrameDecoder.PointSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), RendererFrameDecoder.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), 30000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16, 8), 0x12345678UL);
        void P(int off, float x, float y, uint color, int param)
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(off, 4), BitConverter.SingleToInt32Bits(x));
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(off + 4, 4), BitConverter.SingleToInt32Bits(y));
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(off + 8, 4), color);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(off + 12, 4), param);
        }
        P(RendererFrameDecoder.HeaderSize, -0.5f, 0.25f, 0x00FF0000, 1);
        P(RendererFrameDecoder.HeaderSize + RendererFrameDecoder.PointSize, 0.75f, -0.125f, 0x0000FF00, 2);
        var record = new HookRecord(1, RendererFrameDecoder.RendererFrameApi, 1234, (ulong)DateTime.UtcNow.ToFileTimeUtc(), 0, 0, new byte[16], payload);
        Require(RendererFrameDecoder.TryDecode(record, out var frame) && frame is not null, "renderer frame decode");
        Require(frame!.Rate == 30000 && frame.Flags == 3 && frame.RendererId == 0x12345678UL, "renderer frame header");
        Require(frame.PointCount == 2, "renderer point count");
        Require(Math.Abs(frame.Points[0].X + 0.5f) < 0.0001f && frame.Points[0].Color == 0x00FF0000, "renderer point 0");
        Require(Math.Abs(frame.Points[1].Y + 0.125f) < 0.0001f && frame.Points[1].Color == 0x0000FF00, "renderer point 1");
    }

    private static void HandshakeTrackerTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Cube7Bridge-handshake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tracker = new LaserCubeHandshakeTracker(dir);
        HookRecord R(byte[] payload) => new(1, 1, 1234, (ulong)DateTime.UtcNow.ToFileTimeUtc(), 2, 45457, [127, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], payload);
        Require(tracker.Observe(R([0x27]))?.Contains("DISCOVERY_REQUEST") == true, "handshake discovery request");
        Require(tracker.Observe(R([0x27, 0x00]))?.Contains("DISCOVERY_ACCEPTED") == true, "handshake discovery accepted");
        Require(tracker.Observe(R([0x77]))?.Contains("FULL_INFO_REQUEST") == true, "handshake full info request");
        var fullInfo = new byte[64]; fullInfo[0] = 0x77;
        Require(tracker.Observe(R(fullInfo))?.Contains("FULL_INFO_ACCEPTED") == true, "handshake full info accepted");
        var authRequest = new byte[50]; authRequest[0] = 0xB0;
        Require(tracker.Observe(R(authRequest))?.Contains("AUTH_REQUEST") == true, "handshake auth request");
        Require(tracker.Observe(R([0xB0, 0x00]))?.Contains("AUTH_REQUEST_ACK") == true, "handshake auth ack");
        Require(tracker.Observe(R([0xB1]))?.Contains("AUTH_RESPONSE_QUERY") == true, "handshake auth response query");
        var authResponse = new byte[38]; authResponse[0] = 0xB1;
        Require(tracker.Observe(R(authResponse))?.Contains("AUTH_RESPONSE_CAPTURED") == true, "handshake auth response captured");
        Require(tracker.Observe(R([0x82, 0x30, 0x75, 0x00, 0x00]))?.Contains("POST_AUTH_DEVICE_TRAFFIC") == true, "handshake post-auth traffic");
        string summary = Path.Combine(dir, "handshake-summary.ndjson");
        Require(File.Exists(summary), "handshake summary file");
        Require(File.ReadLines(summary).Count() >= 9, "handshake summary rows");
        Directory.Delete(dir, true);
    }

    private static void HardenedDryRunTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "Cube7Bridge-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string exe = Path.Combine(root, "LaserOS.exe");
            File.WriteAllText(exe, "laser-os-preflight-test");
            string expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant();
            var preflight = LaserOsPreflight.Verify(exe, expectedHash);
            Require(preflight.Exists && preflight.HashMatch && preflight.ActualSha256 == expectedHash, "LaserOS preflight exact SHA");
            Require(!LaserOsPreflight.Verify(exe, new string('0', 64)).HashMatch, "LaserOS preflight mismatch detection");

            var status = new PipelineStatus();
            status.Set("LASEROS_HOOK", "OK", "connected");
            status.Set("PHYSICAL_OUTPUT", "DISABLED", "dry-run invariant");
            string statusText = status.RenderText();
            Require(statusText.Contains("LASEROS_HOOK") && statusText.Contains("DISABLED"), "pipeline status render");

            var frame = BuildRendererFrame();
            var stats = new RendererFrameStatistics();
            stats.Observe(frame);
            stats.Observe(frame);
            var snap = stats.Snapshot();
            Require(snap.Frames == 2 && snap.Points == 4 && snap.LastRate == 30000 && snap.MaxPoints == 2, "renderer statistics");

            var normalized = DryRunTranslator.Translate(frame);
            Require(!normalized.PhysicalOutputEnabled && normalized.Points.Length == 2, "dry-run translator physical invariant");
            Require(normalized.Points.All(p => p.X is >= 0 and <= 4095 && p.Y is >= 0 and <= 4095), "dry-run translator coordinate clamp");
            Require(normalized.Points[0].R == 4095 && normalized.Points[0].G == 0 && normalized.Points[0].B == 0, "dry-run translator RGB mapping");

            string capture = Path.Combine(root, "capture");
            Directory.CreateDirectory(capture);
            File.WriteAllText(Path.Combine(capture, "renderer-frames.ndjson"), "{}\n");
            string cfg = Path.Combine(root, "config.json");
            File.WriteAllText(cfg, "{\"allowBleWrites\":false}");
            string bundle = SupportBundle.Create(capture, cfg, status, preflight, snap);
            Require(File.Exists(bundle), "support bundle created");
            using var zip = ZipFile.OpenRead(bundle);
            Require(zip.Entries.Any(e => e.FullName == "support/status.json"), "support bundle status");
            Require(zip.Entries.Any(e => e.FullName == "support/preflight.json"), "support bundle preflight");
            Require(zip.Entries.Any(e => e.FullName == "config/config.json"), "support bundle config");
            Require(zip.Entries.Any(e => e.FullName.EndsWith("renderer-frames.ndjson", StringComparison.OrdinalIgnoreCase)), "support bundle capture");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task UdpServerTestsAsync()
    {
        int seed = Random.Shared.Next(20000, 40000);
        var cfg = new VirtualLaserCubeConfig { BindAddress = "127.0.0.1", AlivePort = seed, CommandPort = seed + 1, DataPort = seed + 2, BufferSize = 6000, ModelName = "LaserCube Virtual CUBE7" };
        string dir = Path.Combine(Path.GetTempPath(), "Cube7Bridge-selftest-" + Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = new VirtualLaserCubeServer(cfg, dir);
        var serverTask = server.RunAsync(cts.Token);
        await Task.Delay(150, cts.Token);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = 2000;
        var alive = await RoundTripAsync(udp, cfg.AlivePort, [0x27], cts.Token);
        Require(alive is [0x27, 0x00], "UDP discovery 0x27 -> 27 00");
        var info = await RoundTripAsync(udp, cfg.CommandPort, [0x77], cts.Token);
        Require(info.Length == 64 && info[0] == 0x77 && info[1] == 0 && info[2] == 0, "UDP command 0x77 full info");
        var enable = await RoundTripAsync(udp, cfg.CommandPort, [0x78, 0x01], cts.Token);
        Require(enable is [0x78], "UDP 0x78");
        var buffer = await RoundTripAsync(udp, cfg.CommandPort, [0x8A], cts.Token);
        Require(buffer.Length == 4 && buffer[0] == 0x8A, "UDP 0x8A");
        var output = await RoundTripAsync(udp, cfg.CommandPort, [0x80, 0x01], cts.Token);
        Require(output is [0x80], "UDP 0x80");
        Require(server.State.OutputRequested && !server.State.PhysicalOutputEnabled, "UDP virtual-only output");
        var sample = new byte[14]; sample[0] = 0xA9; sample[2] = 1; sample[3] = 2;
        var dataReply = await RoundTripAsync(udp, cfg.DataPort, sample, cts.Token);
        Require(dataReply.Length == 3 && dataReply[0] == 0xA9, "UDP 0xA9");
        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    private static async Task<byte[]> RoundTripAsync(UdpClient client, int port, byte[] payload, CancellationToken ct)
    {
        await client.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
        var receiveTask = client.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(2000, ct));
        if (completed != receiveTask) throw new TimeoutException($"UDP response timeout on port {port}");
        return (await receiveTask).Buffer;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
