namespace Cube7Bridge;

public static class VirtualDeviceEmulationTests
{
    public static void Run()
    {
        var loopbackDefaults = new VirtualLaserCubeConfig();
        Require(loopbackDefaults.NetworkServerEnabled, "real localhost UDP server must be enabled by default");
        Require(loopbackDefaults.BindAddress == "127.0.0.1", "real UDP server must bind loopback only");

        var profile = LoopbackInjectionProfile.From(loopbackDefaults);
        string marker = profile.Serialize();
        Require(profile.Enabled && profile.RewriteDestinations && profile.RewriteClientBinds, "loopback injection rewrite policy");
        Require(marker.Contains("mode=loopback") && marker.Contains("address=127.0.0.1"), "loopback marker mode/address");
        Require(marker.Contains("alivePort=45456") && marker.Contains("commandPort=45457") && marker.Contains("dataPort=45458"), "loopback marker ports");

        var plan = LoopbackRuntimePlan.From(loopbackDefaults);
        Require(plan.StartExternalServer, "loopback plan starts real UDP server");
        Require(plan.WriteInjectionProfile, "loopback plan writes injection profile");
        Require(!plan.UseInjectedResponder, "loopback plan disables in-process protocol responder");

        var cfg = new BridgeConfig.VirtualDeviceConfig
        {
            Enabled = true,
            Network = true,
            UsbHid = "trace-first",
            PhysicalOutput = false,
            AllowSyntheticAuthentication = false
        };

        var state = new VirtualDeviceState(cfg);
        Require(state.Stage == VirtualDeviceStage.Offline, "virtual device starts offline");
        Require(!state.PhysicalOutputEnabled, "physical output must start disabled");

        var engine = new VirtualProtocolEngine(state, new VirtualLaserCubeConfig
        {
            BufferSize = 6000,
            DacRate = 30000,
            MaxDacRate = 30000,
            ModelName = "Cube7 Virtual Test Device"
        });

        var alive = engine.Process([0x27]);
        Require(alive.Response is [0x27, 0x00], "virtual discovery response");
        Require(state.Stage == VirtualDeviceStage.Discovered, "discovery state transition");

        var info = engine.Process([0x77]);
        Require(info.Response is { Length: 64 } && info.Response[0] == 0x77, "virtual full-info response");
        Require(state.Stage == VirtualDeviceStage.Identified, "full-info state transition");

        var auth = engine.Process([0xB0, 0x01, 0x02]);
        Require(auth.Response is null, "auth is trace-only and never forged");
        Require(state.Stage == VirtualDeviceStage.AuthenticationObserved, "auth observation state transition");
        Require(!state.AuthenticationAccepted, "synthetic authentication remains disabled");

        state.MarkReady("test-ready");
        Require(state.Stage == VirtualDeviceStage.Ready, "ready transition");

        var frame = new RendererFrame(30000, 0, 0x10,
        [
            new RendererPoint(-0.5f, 0.25f, 0x00FF0000, 0),
            new RendererPoint(0.5f, -0.25f, 0x0000FF00, 0)
        ]);
        state.ObserveRendererFrame(frame);
        Require(state.Stage == VirtualDeviceStage.Streaming, "renderer frame drives streaming state");
        Require(state.RendererFrames == 1 && state.RendererPoints == 2, "renderer counters");
        Require(!state.PhysicalOutputEnabled, "streaming remains virtual-only");

        var handles = new VirtualHandleTable();
        nint handle = handles.Register("\\\\?\\Cube7Virtual#C77D#0001", VirtualHandleKind.Hid);
        Require(handles.Contains(handle), "virtual handle registration");
        Require(handles.TryGet(handle, out var entry) && entry.Path.Contains("Cube7Virtual"), "virtual handle lookup");
        Require(handles.Close(handle), "virtual handle close");
        Require(!handles.Contains(handle), "virtual handle removed");

        var trace = new DeviceApiTrace();
        trace.Observe("SetupDiGetClassDevsW", "HID enumeration");
        trace.Observe("HidD_GetAttributes", "VID/PID query");
        trace.Observe("CreateFileW", "\\\\?\\Cube7Virtual#C77D#0001");
        trace.Observe("WriteFile", "3 bytes");
        trace.Observe("ReadFile", "64 bytes");
        trace.Observe("DeviceIoControl", "0x00220003");
        var snap = trace.Snapshot();
        Require(snap.Total == 6, "API trace total");
        Require(snap.ByFamily["SETUPAPI"] == 1, "SetupAPI trace classification");
        Require(snap.ByFamily["HID"] == 1, "HID trace classification");
        Require(snap.ByFamily["FILEIO"] == 3, "file I/O trace classification");
        Require(snap.ByFamily["DEVICEIO"] == 1, "DeviceIo trace classification");

        string root = Path.Combine(Path.GetTempPath(), "Cube7-VirtualDevice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string report = DeviceStateReport.Write(root, state, snap);
            Require(File.Exists(report), "device state report written");
            string json = File.ReadAllText(report);
            Require(json.Contains("Streaming") && json.Contains("physicalOutputEnabled"), "device report contents");

            string preview = RendererPreview.WriteSvg(root, frame);
            Require(File.Exists(preview), "renderer preview SVG written");
            string svg = File.ReadAllText(preview);
            Require(svg.Contains("<svg") && svg.Contains("polyline"), "renderer preview SVG contents");
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
