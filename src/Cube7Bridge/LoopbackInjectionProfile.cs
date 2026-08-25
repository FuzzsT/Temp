using System.Net;
using System.Text;

namespace Cube7Bridge;

public sealed record LoopbackRuntimePlan(
    bool StartExternalServer,
    bool WriteInjectionProfile,
    bool UseInjectedResponder)
{
    public static LoopbackRuntimePlan From(VirtualLaserCubeConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        bool active = cfg.Enabled && cfg.NetworkServerEnabled;
        if (active)
        {
            // Validate the complete loopback endpoint contract before Program is allowed
            // to bind any server socket. This prevents a misconfigured 0.0.0.0/LAN
            // listener from being opened even transiently.
            _ = LoopbackInjectionProfile.From(cfg);
        }
        return new LoopbackRuntimePlan(
            StartExternalServer: active,
            WriteInjectionProfile: active,
            UseInjectedResponder: false);
    }
}

public sealed record LoopbackInjectionProfile(
    bool Enabled,
    string Address,
    int AlivePort,
    int CommandPort,
    int DataPort,
    bool RewriteDestinations,
    bool RewriteClientBinds)
{
    public static LoopbackInjectionProfile From(VirtualLaserCubeConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (!IPAddress.TryParse(cfg.BindAddress, out var ip) || !IPAddress.IsLoopback(ip))
            throw new InvalidOperationException("Loopback autoconfig requires a loopback bind address.");
        ValidatePort(cfg.AlivePort, nameof(cfg.AlivePort));
        ValidatePort(cfg.CommandPort, nameof(cfg.CommandPort));
        ValidatePort(cfg.DataPort, nameof(cfg.DataPort));
        return new LoopbackInjectionProfile(
            cfg.Enabled && cfg.NetworkServerEnabled,
            ip.ToString(),
            cfg.AlivePort,
            cfg.CommandPort,
            cfg.DataPort,
            true,
            true);
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.AppendLine("mode=loopback");
        sb.AppendLine($"enabled={(Enabled ? 1 : 0)}");
        sb.AppendLine($"address={Address}");
        sb.AppendLine($"alivePort={AlivePort}");
        sb.AppendLine($"commandPort={CommandPort}");
        sb.AppendLine($"dataPort={DataPort}");
        sb.AppendLine($"rewriteDestinations={(RewriteDestinations ? 1 : 0)}");
        sb.AppendLine($"rewriteClientBinds={(RewriteClientBinds ? 1 : 0)}");
        sb.AppendLine("physicalOutput=0");
        sb.AppendLine("syntheticAuthentication=0");
        return sb.ToString();
    }

    private static void ValidatePort(int port, string name)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(name, port, "UDP port must be 1..65535");
    }
}
