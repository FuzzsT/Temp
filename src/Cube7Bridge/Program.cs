using System.Diagnostics;

namespace Cube7Bridge;

internal static class Program
{
    private const string VirtualMarker = "VirtualLaserCube.enabled";

    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
            return await SelfTest.RunAsync();

        Console.Title = "Cube7 LaserOS Full Bridge";
        string mode = args.FirstOrDefault(a => a.StartsWith("--mode="))?.Split('=', 2)[1].ToLowerInvariant() ?? "full";
        string configPath = args.FirstOrDefault(a => a.StartsWith("--config="))?.Split('=', 2)[1] ?? "config.json";
        var cfg = BridgeConfig.Load(configPath);

        Console.WriteLine("Cube7 LaserOS Full Bridge 0.3.0");
        Console.WriteLine("Virtual LaserCube discovery enabled; physical-output=DISABLED.");
        Console.WriteLine("No BLE characteristic payload writes and no interlock/E-stop bypass.");
        Console.WriteLine($"mode={mode} process={cfg.LaserOsProcessName}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var tasks = new List<Task>();
        CaptureWriter? writer = null;
        string markerPath = Path.Combine(AppContext.BaseDirectory, VirtualMarker);

        try
        {
            if (mode is "full" or "trace")
            {
                writer = new CaptureWriter(cfg.CaptureDirectory);
                Console.WriteLine($"capture={writer.DirectoryPath}");
                var captureServer = new HookCaptureServer(writer);
                tasks.Add(captureServer.RunAsync(cts.Token));

                ConfigureVirtualMarker(markerPath, cfg.VirtualLaserCube.Enabled);
                if (cfg.VirtualLaserCube.Enabled)
                    Console.WriteLine("[virtual] injected responder armed for LaserCube UDP 45456/45457/45458");

                if (cfg.VirtualLaserCube.Enabled && cfg.VirtualLaserCube.NetworkServerEnabled)
                {
                    Console.WriteLine("[virtual] external UDP responder enabled (diagnostic mode)");
                    var networkServer = new VirtualLaserCubeServer(cfg.VirtualLaserCube, writer.DirectoryPath);
                    tasks.Add(networkServer.RunAsync(cts.Token));
                }

                if (cfg.AutoInject) StartInjector(cfg.LaserOsProcessName);
            }
            else
            {
                ConfigureVirtualMarker(markerPath, false);
            }

            if (mode is "full" or "ble")
            {
                var ble = new BleScanner(cfg.Ble, cfg.CaptureDirectory);
                tasks.Add(ble.RunAsync(cts.Token));
            }

            if (tasks.Count == 0)
            {
                Console.WriteLine("Use --mode=full, --mode=trace, or --mode=ble");
                return 2;
            }

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            return 0;
        }
        finally
        {
            ConfigureVirtualMarker(markerPath, false);
            writer?.Dispose();
        }
    }

    private static void ConfigureVirtualMarker(string markerPath, bool enabled)
    {
        try
        {
            if (enabled)
                File.WriteAllText(markerPath, "FullBridge 0.3.0 virtual LaserCube; physical-output=DISABLED\n");
            else if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            if (enabled) throw new IOException($"Cannot arm virtual LaserCube marker '{markerPath}': {ex.Message}", ex);
            Console.WriteLine($"[virtual] marker cleanup warning: {ex.Message}");
        }
    }

    private static void StartInjector(string processName)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Cube7Injector.exe");
        var dll = Path.Combine(AppContext.BaseDirectory, "LaserOSHook.dll");
        if (!File.Exists(exe) || !File.Exists(dll))
        {
            Console.WriteLine("[inject] FATAL: FullBridge package is incomplete: Cube7Injector.exe or LaserOSHook.dll is missing next to Cube7Bridge.exe.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--process \"{processName}\" --dll \"{dll}\"",
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = AppContext.BaseDirectory
            });
            Console.WriteLine("[inject] watcher started");
        }
        catch (Exception ex) { Console.WriteLine($"[inject] start failed: {ex.Message}"); }
    }
}
