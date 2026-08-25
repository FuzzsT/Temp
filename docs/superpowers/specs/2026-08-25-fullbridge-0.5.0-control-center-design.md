# FullBridge 0.5.0 Control Center — Design

## Goal

Turn the current FullBridge 0.4.0 renderer-tap/diagnostic runtime into a single Windows x64 operator application that exposes LaserOS, hook, renderer, BLE/GATT, capture, protocol and safety state in one GUI, while keeping the renderer-to-CUBE transport in DRY-RUN mode and leaving physical laser output disabled by default.

## Current baseline

The repository already contains:

- early/late injection of `LaserOSHook.dll` into `LaserOS.exe`;
- Winsock capture and passive LaserCube handshake tracing;
- `RendererFrameDecoder` and `RendererFrameCapture` for `ldRendererOpenlase` frames;
- BLE scan/GATT inventory for the exact CUBE target `E4:66:E5:D2:6E:38`;
- an optional virtual LaserCube responder;
- a self-test and Windows x64 release workflow.

The 0.4.0 default config intentionally leaves the virtual LaserCube and physical transport disabled. 0.5.0 keeps that safety posture.

## Architecture

### 1. Shared Core

Create a reusable `Cube7Bridge.Core` project and move non-UI bridge state/contracts into it. The console runtime and GUI consume the same interfaces instead of duplicating logic.

Primary services:

- `BridgeRuntimeService` — owns lifecycle and top-level state transitions;
- `LaserOsService` — process discovery, early launch/inject and hook state;
- `RendererMonitor` — renderer frame counters, FPS, point rate and last-frame snapshot;
- `BleDeviceManager` — scan, exact target matching, connect/reconnect and GATT readiness state;
- `CaptureManager` — capture session lifecycle and support-pack export;
- `ProtocolMonitor` — recent UDP/handshake events and stage tracking;
- `DryRunTranslator` — converts `RendererFrame` into a normalized `Cube7Frame` without writing vendor GATT payloads;
- `BridgeHealthEvaluator` — derives a single health/status summary from subsystem states.

### 2. WPF Control Center

Add `Cube7ControlCenter` targeting `net8.0-windows` with WPF.

The window contains five pages:

1. **Overview** — subsystem status cards and operator actions.
2. **Renderer** — live XY preview, frame rate, points/frame, points/s, dropped/invalid frames.
3. **BLE / GATT** — target identity, RSSI, connection state, services, characteristics and notification state.
4. **Protocol / Capture** — handshake stage, recent protocol events, capture paths and export button.
5. **Settings / Safety** — configuration, DRY-RUN status and safety constraints.

The GUI must never infer that the physical laser is armed. It renders physical output as `DISABLED` unless a future explicitly reviewed transport subsystem reports otherwise.

## State model

`BridgeRuntimeSnapshot` is immutable and published to UI subscribers.

It contains:

- `LaserOsState`: `NotRunning | Starting | Running | Hooking | Hooked | Error`;
- `RendererState`: `Idle | Receiving | Stalled | Error`;
- `BleState`: `Disabled | Scanning | Connecting | DiscoveringGatt | Ready | Reconnecting | Error`;
- `ProtocolStage`: existing passive handshake stage enum;
- `TranslatorState`: `Disabled | DryRunReady | DryRunReceiving | Error`;
- `CaptureState`: `Stopped | Recording | Error`;
- `PhysicalOutputEnabled`: always `false` in 0.5.0;
- timestamps and latest error strings per subsystem.

## BLE Manager behavior

The exact configured address is authoritative. Name matching is fallback-only.

Reconnect policy:

- reconnect only after a previously established connection is lost;
- delays: 1 s, 2 s, 4 s, 8 s, 15 s, then remain at 15 s;
- reconnect loop stops immediately on user disconnect, application shutdown or cancellation;
- after reconnect, re-enumerate GATT and resubscribe to safe notification characteristics;
- no automatic vendor `Write`/`WriteWithoutResponse` operations in 0.5.0.

The manager exposes:

- target address/name;
- RSSI;
- connection and GATT state;
- reconnect count;
- last notification timestamp;
- service/characteristic inventory;
- last error.

## Renderer metrics and preview

`RendererMonitor` consumes decoded renderer frames and maintains a rolling 2-second window.

Metrics:

- frames/s;
- points/s;
- mean/min/max points per frame;
- invalid frame count;
- last frame age;
- renderer rate/flags/id;
- normalized XY bounds.

The WPF preview draws a bounded sample of the latest frame to avoid UI stalls. Rendering is capped at 30 UI refreshes per second even if the renderer produces faster updates.

## DRY-RUN translator

`DryRunTranslator` converts `RendererPoint` into a transport-neutral `Cube7Point` and reports:

- point count;
- clipping count;
- normalized coordinate range;
- color conversion summary;
- estimated serialized payload size.

It does **not** call BLE write APIs and does not emit device commands. This creates a testable seam for a later reviewed live transport without changing the GUI or renderer pipeline.

## Control Center actions

Overview buttons:

- **Start / Restart LaserOS with Early Hook**;
- **Reconnect CUBE 7**;
- **Start / Stop Capture**;
- **Open Capture Folder**;
- **Export Support Pack**;
- **Run Self-Test**.

No button arms or enables physical optical output.

## Support pack

`CaptureManager.ExportSupportPackAsync()` creates a ZIP containing:

- runtime snapshot JSON;
- effective config with secrets omitted;
- renderer summaries;
- handshake summary;
- protocol capture NDJSON;
- BLE/GATT diagnostic log;
- VERSION.json;
- SHA-256 manifest.

The exporter excludes raw authentication payload bodies from its human-readable summary and records only length/hash where the existing tracker already uses this policy.

## Configuration

Extend `config.json` with:

```json
{
  "controlCenter": {
    "enabled": true,
    "startMinimized": false,
    "rendererPreviewFps": 30,
    "maxPreviewPoints": 6000
  },
  "ble": {
    "autoConnect": true,
    "subscribeNotifications": true,
    "reconnectEnabled": true
  },
  "translator": {
    "enabled": true,
    "mode": "dry-run"
  }
}
```

`allowBleWrites` remains `false` by default and is not consumed by the 0.5.0 translator.

## Error handling

Every subsystem reports structured error state instead of terminating the entire application. Fatal startup errors are limited to missing/corrupt required runtime binaries or invalid configuration that prevents safe startup.

The GUI shows:

- subsystem;
- short error message;
- timestamp;
- suggested recovery action where deterministic.

## Testing

### Core unit tests

- bridge health derivation;
- BLE reconnect state machine/backoff;
- renderer rolling metrics;
- renderer-to-`Cube7Frame` DRY-RUN conversion;
- support-pack manifest generation;
- config defaults and validation.

### Integration/self-test

Existing `Cube7Bridge.exe --self-test` remains and is expanded to cover the new core services in deterministic simulation mode.

Add `Cube7ControlCenter.exe --self-test` for a headless construction/startup test that does not open LaserOS or Bluetooth.

### Release verification

CI must build:

- `Cube7Bridge.exe`;
- `Cube7ControlCenter.exe`;
- `Cube7Injector.exe`;
- `LaserOSHook.dll`.

The packaged ZIP must pass `verify-release.ps1` and both self-tests before upload.

## Safety constraints

These are release-blocking requirements:

- physical laser output is disabled in 0.5.0;
- no automatic optical output enable;
- no interlock/E-stop bypass;
- no fabricated authentication success;
- no vendor BLE payload writes from the 0.5.0 translator;
- `SET_OUTPUT` in the optional virtual LaserCube remains virtual-only;
- GUI must clearly display `OUTPUT: DISABLED` and `TRANSLATOR: DRY-RUN`.

## Deliverable

Windows x64 release:

```text
Cube7-LaserOS-FullBridge-0.5.0-win-x64/
├── Cube7ControlCenter.exe
├── start-control-center.cmd
├── start-full.cmd
├── start-trace.cmd
├── start-ble.cmd
├── README.md
├── VERSION.json
├── verify-release.ps1
├── docs/
├── protocols/
├── scripts/
└── bin/
    ├── Cube7Bridge.exe
    ├── Cube7Injector.exe
    ├── LaserOSHook.dll
    └── config.json
```

The first 0.5.0 release is considered successful when the Control Center can show live renderer metrics/preview, stable BLE/GATT readiness for the configured CUBE 7, passive protocol/handshake state, capture/export functionality, and a DRY-RUN translated frame stream while physical output remains disabled.