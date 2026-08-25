using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Cube7Bridge;

public sealed class VirtualLaserCubeServer
{
    private readonly VirtualLaserCubeConfig _cfg;
    private readonly string _captureDirectory;
    private readonly string _logPath;
    private readonly object _stateLock = new();
    private readonly object _logLock = new();

    public VirtualLaserCubeState State { get; }

    public VirtualLaserCubeServer(VirtualLaserCubeConfig cfg, string captureDirectory)
    {
        _cfg = cfg;
        _captureDirectory = captureDirectory;
        Directory.CreateDirectory(_captureDirectory);
        _logPath = Path.Combine(_captureDirectory, $"virtual-lasercube-{DateTime.Now:yyyyMMdd-HHmmss}.ndjson");
        State = new VirtualLaserCubeState { BufferFree = cfg.BufferSize };
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_cfg.Enabled)
        {
            Console.WriteLine("[virtual] disabled by config");
            return;
        }

        if (!IPAddress.TryParse(_cfg.BindAddress, out var bindAddress) || bindAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException($"virtualLaserCube.bindAddress must be IPv4: '{_cfg.BindAddress}'");

        using var alive = Bind(bindAddress, _cfg.AlivePort);
        using var command = Bind(bindAddress, _cfg.CommandPort);
        using var data = Bind(bindAddress, _cfg.DataPort);

        Console.WriteLine($"[virtual] LaserCube UDP online bind={bindAddress} ports={_cfg.AlivePort}/{_cfg.CommandPort}/{_cfg.DataPort}");
        Console.WriteLine("[virtual] physical-output=DISABLED; SET_OUTPUT is virtual state only");

        await Task.WhenAll(
            ReceiveLoopAsync(alive, _cfg.AlivePort, ct),
            ReceiveLoopAsync(command, _cfg.CommandPort, ct),
            ReceiveLoopAsync(data, _cfg.DataPort, ct));
    }

    private static UdpClient Bind(IPAddress address, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), port, "UDP port must be 1..65535");
        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.Client.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task ReceiveLoopAsync(UdpClient socket, int localPort, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult datagram = await socket.ReceiveAsync(ct);
                VirtualLaserCubeReply reply;
                lock (_stateLock)
                    reply = VirtualLaserCubeProtocol.HandleCommand(datagram.Buffer, State, _cfg);

                Log("rx", localPort, datagram.RemoteEndPoint, datagram.Buffer, reply);

                if (reply.Response is { Length: > 0 })
                {
                    await socket.SendAsync(reply.Response, reply.Response.Length, datagram.RemoteEndPoint);
                    Log("tx", localPort, datagram.RemoteEndPoint, reply.Response, reply);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
    }

    private void Log(string direction, int localPort, IPEndPoint remote, byte[] payload, VirtualLaserCubeReply reply)
    {
        string hexPrefix = Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 96)));
        var row = new
        {
            type = "virtual_lasercube",
            timeUtc = DateTime.UtcNow,
            direction,
            localPort,
            remote = remote.ToString(),
            opcode = payload.Length > 0 ? $"0x{payload[0]:X2}" : null,
            payloadLength = payload.Length,
            hexPrefix,
            kind = reply.Kind,
            pointCount = reply.PointCount,
            virtualOutputRequested = State.OutputRequested,
            physicalOutputEnabled = false
        };
        lock (_logLock)
            File.AppendAllText(_logPath, JsonSerializer.Serialize(row) + Environment.NewLine);
    }
}
