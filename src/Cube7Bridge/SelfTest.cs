using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Cube7Bridge;

public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        try
        {
            ProtocolModelTests();
            BluetoothAddressTests();
            await UdpServerTestsAsync();
            Console.WriteLine("SELFTEST PASS: 0x27 discovery + protocol + UDP virtual device + BLE target parser; physical-output=DISABLED");
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

        // LaserOS/libLaserdockCore discovery starts with 0x27 on UDP 45456
        // and accepts a device only after the exact two-byte response 27 00.
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

    private static async Task UdpServerTestsAsync()
    {
        int seed = Random.Shared.Next(20000, 40000);
        var cfg = new VirtualLaserCubeConfig
        {
            BindAddress = "127.0.0.1",
            AlivePort = seed,
            CommandPort = seed + 1,
            DataPort = seed + 2,
            BufferSize = 6000,
            ModelName = "LaserCube Virtual CUBE7"
        };
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

        var sample = new byte[14];
        sample[0] = 0xA9; sample[2] = 1; sample[3] = 2;
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
