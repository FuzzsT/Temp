using System.Diagnostics;

namespace Cube7Bridge;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.Title = "Cube7 LaserOS Full Bridge";
        string mode = args.FirstOrDefault(a => a.StartsWith("--mode="))?.Split('=', 2)[1].ToLowerInvariant() ?? "full";
        string configPath = args.FirstOrDefault(a => a.StartsWith("--config="))?.Split('=', 2)[1] ?? "config.json";
        var cfg = BridgeConfig.Load(configPath);

        Console.WriteLine("Cube7 LaserOS Full Bridge 0.1.0");
        Console.WriteLine("Trace-first build: no BLE characteristic writes and no interlock bypass.");
        Console.WriteLine($"mode={mode} process={cfg.LaserOsProcessName}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var tasks = new List<Task>();
        CaptureWriter? writer = null;

        if (mode is "full" or "trace")
        {
            writer = new CaptureWriter(cfg.CaptureDirectory);
            Console.WriteLine($"capture={writer.DirectoryPath}");
            var server = new HookCaptureServer(writer);
            tasks.Add(server.RunAsync(cts.Token));
            if (cfg.AutoInject) StartInjector(cfg.LaserOsProcessName);
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
        finally { writer?.Dispose(); }
        return 0;
    }

    private static void StartInjector(string processName)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Cube7Injector.exe");
        var dll = Path.Combine(AppContext.BaseDirectory, "LaserOSHook.dll");
        if (!File.Exists(exe) || !File.Exists(dll))
        {
            Console.WriteLine("[inject] native binaries missing. Build native/ first or place Cube7Injector.exe + LaserOSHook.dll next to Cube7Bridge.exe.");
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
