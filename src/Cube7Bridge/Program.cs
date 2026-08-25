using System.Diagnostics;

namespace Cube7Bridge;

internal static class Program
{
    private const string Version = "0.4.1";
    private const string VirtualMarker = "VirtualLaserCube.enabled";

    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
            return await SelfTest.RunAsync();

        Console.Title = "Cube7 LaserOS Full Bridge";
        string mode = args.FirstOrDefault(a => a.StartsWith("--mode="))?.Split('=', 2)[1].ToLowerInvariant() ?? "full";
        string configPath = Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--config="))?.Split('=', 2)[1] ?? "config.json");
        var cfg = BridgeConfig.Load(configPath);
        var status = new PipelineStatus();
        var rendererStats = new RendererFrameStatistics();

        status.Set("LASEROS_HOOK", "WAITING", "not connected yet");
        status.Set("DISCOVERY_0x27", "WAITING", "no discovery observed");
        status.Set("FULL_INFO_0x77", "WAITING", "no full-info observed");
        status.Set("AUTH_B0_B1", "WAITING", "no auth traffic observed");
        status.Set("RENDERER_TAP", "WAITING", "no renderer frames observed");
        status.Set("CUBE7_BLE", mode is "full" or "ble" ? "WAITING" : "SKIPPED", "target not scanned yet");
        status.Set("TRANSLATOR", "DRY-RUN", "normalized frames only; no device writes");
        status.Set("PHYSICAL_OUTPUT", "DISABLED", "hard safety invariant");

        Console.WriteLine($"Cube7 LaserOS Full Bridge {Version} HARDENED-DRYRUN");
        Console.WriteLine("Renderer tap + preflight + pipeline status + safe support bundle.");
        Console.WriteLine("Physical CUBE output is DISABLED; no BLE payload writes and no interlock/E-stop bypass.");
        Console.WriteLine($"mode={mode} process={cfg.LaserOsProcessName}");

        LaserOsPreflightResult preflight = new(string.Empty, false, cfg.Preflight.ExpectedLaserOsSha256, null, false, "SKIPPED", "LaserOS preflight not required for this mode");
        if (mode is "full" or "trace")
        {
            string? preflightPath = ResolveConfiguredOrRunningLaserOsPath(cfg);
            preflight = cfg.Preflight.Enabled
                ? LaserOsPreflight.Verify(preflightPath, cfg.Preflight.ExpectedLaserOsSha256)
                : new LaserOsPreflightResult(preflightPath ?? string.Empty, File.Exists(preflightPath), null, null, true, "SKIPPED", "preflight disabled by config");

            string shaDetail = preflight.ActualSha256 is null ? preflight.Detail : $"{preflight.Detail}; sha256={preflight.ActualSha256[..Math.Min(12, preflight.ActualSha256.Length)]}...";
            status.Set("LASEROS_PREFLIGHT", preflight.State, shaDetail);
            Console.WriteLine($"[preflight] {preflight.State} path='{preflight.Path}' {shaDetail}");

            if (cfg.Preflight.Enabled && cfg.Preflight.RequireVerifiedSha256 && (!preflight.Exists || !preflight.HashMatch))
            {
                Console.WriteLine("[preflight] FATAL: verified LaserOS build required. Injection was not started.");
                PrintStatus(status);
                return 3;
            }
        }
        else
        {
            status.Set("LASEROS_PREFLIGHT", "SKIPPED", "BLE-only mode");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var tasks = new List<Task>();
        CaptureWriter? writer = null;
        HookCaptureServer? captureServer = null;
        string markerPath = Path.Combine(AppContext.BaseDirectory, VirtualMarker);

        try
        {
            if (mode is "full" or "trace")
            {
                writer = new CaptureWriter(cfg.CaptureDirectory);
                Console.WriteLine($"capture={writer.DirectoryPath}");
                captureServer = new HookCaptureServer(writer, status, rendererStats, cfg.Hardening.WriteDryRunTranslation);
                tasks.Add(captureServer.RunAsync(cts.Token));

                ConfigureVirtualMarker(markerPath, cfg.VirtualLaserCube.Enabled);
                if (cfg.VirtualLaserCube.Enabled)
                    Console.WriteLine("[virtual] optional injected responder armed for LaserCube UDP 45456/45457/45458");
                else
                    Console.WriteLine("[renderer] virtual LaserCube disabled; renderer tap operates independently of Projector Setup device state.");

                if (cfg.VirtualLaserCube.Enabled && cfg.VirtualLaserCube.NetworkServerEnabled)
                {
                    Console.WriteLine("[virtual] external UDP responder enabled (diagnostic mode)");
                    var networkServer = new VirtualLaserCubeServer(cfg.VirtualLaserCube, writer.DirectoryPath);
                    tasks.Add(networkServer.RunAsync(cts.Token));
                }

                if (cfg.AutoInject && !StartInjector(cfg))
                    status.Set("LASEROS_HOOK", "ERROR", "injector could not be started");
            }
            else
            {
                ConfigureVirtualMarker(markerPath, false);
            }

            if (mode is "full" or "ble")
            {
                string bleCapture = writer?.DirectoryPath ?? cfg.CaptureDirectory;
                var ble = new BleScanner(cfg.Ble, bleCapture, status);
                tasks.Add(ble.RunAsync(cts.Token));
            }

            if (tasks.Count == 0)
            {
                Console.WriteLine("Use --mode=full, --mode=trace, or --mode=ble");
                return 2;
            }

            tasks.Add(StatusLoopAsync(status, cfg.Hardening.StatusIntervalSeconds, cts.Token));
            PrintStatus(status);

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            return 0;
        }
        finally
        {
            ConfigureVirtualMarker(markerPath, false);
            captureServer?.Dispose();
            writer?.Dispose();

            if (writer is not null && cfg.Hardening.AutomaticSupportBundle)
            {
                try
                {
                    string bundle = SupportBundle.Create(writer.DirectoryPath, configPath, status, preflight, rendererStats.Snapshot());
                    Console.WriteLine($"[support] bundle={bundle}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[support] bundle error={ex.Message}");
                }
            }
            PrintStatus(status);
        }
    }

    private static async Task StatusLoopAsync(PipelineStatus status, int intervalSeconds, CancellationToken ct)
    {
        int seconds = Math.Clamp(intervalSeconds, 2, 60);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            PrintStatus(status);
        }
    }

    private static void PrintStatus(PipelineStatus status)
    {
        Console.WriteLine("[status] --------------------------------------------------");
        Console.WriteLine(status.RenderText());
        Console.WriteLine("[status] --------------------------------------------------");
    }

    private static string? ResolveConfiguredOrRunningLaserOsPath(BridgeConfig cfg)
    {
        Process? running = null;
        try
        {
            running = FindLaserOsProcess(cfg.LaserOsProcessName);
            return ResolveLaserOsPath(cfg, running);
        }
        finally
        {
            running?.Dispose();
        }
    }

    private static void ConfigureVirtualMarker(string markerPath, bool enabled)
    {
        try
        {
            if (enabled)
                File.WriteAllText(markerPath, $"FullBridge {Version} virtual LaserCube; physical-output=DISABLED\n");
            else if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            if (enabled) throw new IOException($"Cannot arm virtual LaserCube marker '{markerPath}': {ex.Message}", ex);
            Console.WriteLine($"[virtual] marker cleanup warning: {ex.Message}");
        }
    }

    private static bool StartInjector(BridgeConfig cfg)
    {
        var injector = Path.Combine(AppContext.BaseDirectory, "Cube7Injector.exe");
        var dll = Path.Combine(AppContext.BaseDirectory, "LaserOSHook.dll");
        if (!File.Exists(injector) || !File.Exists(dll))
        {
            Console.WriteLine("[inject] FATAL: FullBridge package is incomplete: Cube7Injector.exe or LaserOSHook.dll is missing next to Cube7Bridge.exe.");
            return false;
        }

        if (cfg.EarlyHook.Enabled)
        {
            var running = FindLaserOsProcess(cfg.LaserOsProcessName);
            string? launchPath = ResolveLaserOsPath(cfg, running);

            if (running is not null && cfg.EarlyHook.RestartRunningLaserOs && !string.IsNullOrWhiteSpace(launchPath))
            {
                Console.WriteLine($"[early] LaserOS already running PID={running.Id}; restarting gracefully so renderer imports are hooked before startup.");
                bool closed = GracefullyClose(running, cfg.EarlyHook.GracefulCloseTimeoutSeconds);
                running.Dispose();
                running = null;

                if (closed)
                {
                    if (StartEarlyInjector(injector, dll, launchPath)) return true;
                    Console.WriteLine("[early] early-launch failed; falling back to process watcher.");
                }
                else
                {
                    Console.WriteLine("[early] LaserOS did not exit after graceful close request; no forced termination is performed. Falling back to late injection.");
                }
            }
            else if (running is null && !string.IsNullOrWhiteSpace(launchPath) && File.Exists(launchPath))
            {
                Console.WriteLine($"[early] LaserOS is not running; launching with renderer hook before startup: {launchPath}");
                if (StartEarlyInjector(injector, dll, launchPath)) return true;
                Console.WriteLine("[early] early-launch failed; falling back to process watcher.");
            }
            else
            {
                running?.Dispose();
            }
        }

        return StartWatchInjector(injector, dll, cfg.LaserOsProcessName);
    }

    private static Process? FindLaserOsProcess(string processName)
    {
        try
        {
            string baseName = Path.GetFileNameWithoutExtension(processName);
            return Process.GetProcessesByName(baseName).OrderBy(p => p.StartTime).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[early] process lookup warning: {ex.Message}");
            return null;
        }
    }

    private static string? ResolveLaserOsPath(BridgeConfig cfg, Process? running)
    {
        if (!string.IsNullOrWhiteSpace(cfg.EarlyHook.LaserOsExePath))
        {
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(cfg.EarlyHook.LaserOsExePath)); }
            catch (Exception ex) { Console.WriteLine($"[early] configured LaserOS path warning: {ex.Message}"); }
        }

        if (running is not null)
        {
            try
            {
                string? path = running.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    Console.WriteLine($"[early] detected LaserOS path: {path}");
                    return path;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[early] cannot read LaserOS executable path: {ex.Message}");
            }
        }
        return null;
    }

    private static bool GracefullyClose(Process process, int timeoutSeconds)
    {
        try
        {
            if (process.HasExited) return true;
            bool requested = process.CloseMainWindow();
            if (!requested) return false;
            int timeoutMs = Math.Clamp(timeoutSeconds, 1, 60) * 1000;
            return process.WaitForExit(timeoutMs);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[early] graceful close warning: {ex.Message}");
            return false;
        }
    }

    private static bool StartEarlyInjector(string injector, string dll, string launchPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = injector,
                Arguments = $"--launch \"{launchPath}\" --dll \"{dll}\"",
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = AppContext.BaseDirectory
            });
            Console.WriteLine("[early] suspended-launch injector started; LaserOS will resume only after LaserOSHook.dll is loaded.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[early] start failed: {ex.Message}");
            return false;
        }
    }

    private static bool StartWatchInjector(string injector, string dll, string processName)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = injector,
                Arguments = $"--process \"{processName}\" --dll \"{dll}\"",
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = AppContext.BaseDirectory
            });
            Console.WriteLine("[inject] watcher started");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[inject] start failed: {ex.Message}");
            return false;
        }
    }
}
