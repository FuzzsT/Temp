using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace Cube7ControlCenter;

public partial class MainWindow : Window
{
    private readonly bool _startBridge;
    private readonly StringBuilder _protocol = new();
    private readonly StringBuilder _ble = new();
    private Process? _bridge;
    private string? _capturePath;
    private static readonly Regex StatusLine = new("^(LASEROS_HOOK|RENDERER_TAP|CUBE7_BLE|DISCOVERY_0x27|FULL_INFO_0x77|AUTH_B0_B1|TRANSLATOR|PHYSICAL_OUTPUT)\\s+([A-Z0-9_-]+)\\s*(.*)$", RegexOptions.Compiled);

    public MainWindow(bool startBridge = true)
    {
        InitializeComponent();
        _startBridge = startBridge;
        RuntimePathText.Text = $"Runtime: {ResolveBridgePath()}";
        Closed += (_, _) => StopOwnedBridge();
        Loaded += (_, _) =>
        {
            if (_startBridge) StartBridge();
        };
    }

    private string ResolveBridgePath()
    {
        string rootCandidate = System.IO.Path.Combine(AppContext.BaseDirectory, "bin", "Cube7Bridge.exe");
        if (File.Exists(rootCandidate)) return rootCandidate;
        string siblingCandidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "bin", "Cube7Bridge.exe"));
        if (File.Exists(siblingCandidate)) return siblingCandidate;
        string localCandidate = System.IO.Path.Combine(AppContext.BaseDirectory, "Cube7Bridge.exe");
        return localCandidate;
    }

    private string ResolveConfigPath(string bridgePath)
    {
        string besideBridge = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(bridgePath)!, "config.json");
        if (File.Exists(besideBridge)) return besideBridge;
        return System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");
    }

    private void StartBridge_Click(object sender, RoutedEventArgs e) => StartBridge();
    private void StopBridge_Click(object sender, RoutedEventArgs e) => StopOwnedBridge();

    private void StartBridge()
    {
        StopOwnedBridge();
        string bridge = ResolveBridgePath();
        if (!File.Exists(bridge))
        {
            FooterStatus.Text = $"Cube7Bridge.exe not found: {bridge}";
            HookState.Text = "MISSING";
            return;
        }

        string config = ResolveConfigPath(bridge);
        var psi = new ProcessStartInfo
        {
            FileName = bridge,
            Arguments = $"--mode=full --config=\"{config}\"",
            WorkingDirectory = System.IO.Path.GetDirectoryName(bridge)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        _bridge = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _bridge.OutputDataReceived += (_, e) => { if (e.Data is not null) Dispatcher.BeginInvoke(() => HandleLine(e.Data)); };
        _bridge.ErrorDataReceived += (_, e) => { if (e.Data is not null) Dispatcher.BeginInvoke(() => HandleLine("[stderr] " + e.Data)); };
        _bridge.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            FooterStatus.Text = $"Bridge exited code={_bridge?.ExitCode}";
            HookState.Text = "STOPPED";
        });
        try
        {
            _bridge.Start();
            _bridge.BeginOutputReadLine();
            _bridge.BeginErrorReadLine();
            FooterStatus.Text = $"Bridge PID={_bridge.Id}";
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "Start failed: " + ex.Message;
            _bridge.Dispose();
            _bridge = null;
        }
    }

    private void StopOwnedBridge()
    {
        var p = _bridge;
        _bridge = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(1500)) p.Kill(entireProcessTree: false);
            }
        }
        catch { }
        finally { p.Dispose(); }
        FooterStatus.Text = "Bridge stopped";
    }

    private async void RunSelfTest_Click(object sender, RoutedEventArgs e)
    {
        string bridge = ResolveBridgePath();
        if (!File.Exists(bridge)) { FooterStatus.Text = "Cube7Bridge.exe missing"; return; }
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = bridge,
                Arguments = "--self-test",
                WorkingDirectory = System.IO.Path.GetDirectoryName(bridge)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p is null) throw new InvalidOperationException("Could not start self-test");
            string stdout = await p.StandardOutput.ReadToEndAsync();
            string stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            FooterStatus.Text = p.ExitCode == 0 ? "Self-test PASS" : $"Self-test FAIL ({p.ExitCode})";
            AppendProtocol(stdout + stderr);
        }
        catch (Exception ex) { FooterStatus.Text = "Self-test error: " + ex.Message; }
    }

    private void OpenCaptures_Click(object sender, RoutedEventArgs e)
    {
        string? path = _capturePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            string bridge = ResolveBridgePath();
            path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(bridge)!, "captures");
        }
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { FooterStatus.Text = "Open captures failed: " + ex.Message; }
    }

    private void HandleLine(string line)
    {
        if (line.StartsWith("capture=", StringComparison.OrdinalIgnoreCase))
        {
            _capturePath = line[8..].Trim();
            CapturePathText.Text = "Capture: " + _capturePath;
        }

        var m = StatusLine.Match(line);
        if (m.Success)
        {
            string key = m.Groups[1].Value;
            string state = m.Groups[2].Value;
            string detail = m.Groups[3].Value.Trim();
            SetState(key, state, detail);
        }

        if (line.StartsWith("[renderer]", StringComparison.OrdinalIgnoreCase))
            RendererDetail.Text = line;
        if (line.StartsWith("[ble]", StringComparison.OrdinalIgnoreCase) || line.Contains("CUBE7_BLE", StringComparison.Ordinal))
            AppendBle(line);
        if (line.StartsWith("[RX]", StringComparison.Ordinal) || line.StartsWith("[TX]", StringComparison.Ordinal) || line.StartsWith("[stage]", StringComparison.Ordinal) || line.StartsWith("[hook]", StringComparison.Ordinal) || line.StartsWith("[virtual]", StringComparison.Ordinal))
            AppendProtocol(line);

        PipelineDetail.Text = $"Hook: {HookState.Text}\nRenderer: {RendererState.Text}\nCUBE7 BLE: {BleState.Text}\nDiscovery: {DiscoveryState.Text}\nFull info: {FullInfoState.Text}\nAuth: {AuthState.Text}";
    }

    private void SetState(string key, string state, string detail)
    {
        switch (key)
        {
            case "LASEROS_HOOK": HookState.Text = state; break;
            case "RENDERER_TAP": RendererState.Text = state; RendererDetail.Text = detail; break;
            case "CUBE7_BLE": BleState.Text = state; break;
            case "DISCOVERY_0x27": DiscoveryState.Text = state; break;
            case "FULL_INFO_0x77": FullInfoState.Text = state; break;
            case "AUTH_B0_B1": AuthState.Text = state; break;
        }
    }

    private void AppendBle(string line)
    {
        _ble.AppendLine(line);
        Trim(_ble);
        BleLog.Text = _ble.ToString();
        BleLog.ScrollToEnd();
    }

    private void AppendProtocol(string text)
    {
        foreach (string line in text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            _protocol.AppendLine(line);
        Trim(_protocol);
        ProtocolLog.Text = _protocol.ToString();
        ProtocolLog.ScrollToEnd();
    }

    private static void Trim(StringBuilder sb)
    {
        const int max = 60000;
        if (sb.Length > max) sb.Remove(0, sb.Length - max);
    }
}
