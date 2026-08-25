using System.Runtime.CompilerServices;

namespace Cube7Bridge;

public static class BleUartCandidateProtocolTests
{
    [ModuleInitializer]
    public static void RunWhenSelfTesting()
    {
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
            Run();
    }

    public static void Run()
    {
        var ping = BleUartCandidateProtocol.BuildPacket(0x09, []);
        Require(Convert.ToHexString(ping) == "AA0900B3", "AA/SUM candidate ping packet");

        var off = BleUartCandidateProtocol.BuildPacket(0x04, [0x00]);
        Require(Convert.ToHexString(off) == "AA040100AF", "AA/SUM candidate output-off packet");

        Require(BleUartCandidateProtocol.TryParse(ping, out var parsed) && parsed is not null, "candidate packet parse");
        Require(parsed!.Command == 0x09 && parsed.Payload.Length == 0 && parsed.ChecksumValid, "candidate packet fields");

        var bad = ping.ToArray();
        bad[^1] ^= 0x01;
        Require(BleUartCandidateProtocol.TryParse(bad, out var badParsed) && badParsed is not null && !badParsed.ChecksumValid, "candidate checksum mismatch detection");

        var profile = BleUartCandidateProtocol.Profile;
        Require(profile.Confidence == "UNVERIFIED", "candidate profile must remain unverified");
        Require(!profile.TransmitEnabled, "candidate profile must not enable FFE2 writes");
        Require(profile.NotifyCharacteristic.EndsWith("ffe1-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase), "FFE1 notify mapping");
        Require(profile.WriteCharacteristic.EndsWith("ffe2-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase), "FFE2 write mapping");
        Require(profile.MaxAttPayload == 244, "MTU 247 -> ATT payload 244");

        string root = Path.Combine(Path.GetTempPath(), "Cube7BleUartTrace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var trace = new BleUartTraceWriter(root);
            trace.ObserveNotification(ping);
            trace.ObserveNotification(new byte[244]);
            Require(File.Exists(trace.Path), "BLE UART trace file created");
            string text = File.ReadAllText(trace.Path);
            Require(text.Contains("checksumValid") && text.Contains("idle-zero-buffer"), "BLE UART trace classification");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
