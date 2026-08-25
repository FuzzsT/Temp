# FullBridge 0.3.0 Virtual LaserCube Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make LaserOS x64 discover a safe virtual LaserCube endpoint and capture its UDP command/data stream while selecting the real CUBE 7 by exact BLE address for read-only GATT analysis.

**Architecture:** Add an isolated UDP virtual-device server to the existing `Cube7Bridge.exe`, keep the injector/capture path unchanged, and run both concurrently in `--mode=full`. The virtual server emulates documented LaserCube discovery/status/buffer/data opcodes but treats output enable as software-only state and never forwards it to BLE.

**Tech Stack:** .NET 8 / C# on Windows, WinRT Bluetooth APIs, UDP sockets, existing native MSVC injector/hook, GitHub Actions Windows 2025.

**Spec:** `docs/superpowers/specs/2026-08-25-fullbridge-virtual-lasercube-design.md`

## Global Constraints

- Target Windows x64 release remains self-contained .NET 8.
- Physical laser output remains disabled by bridge code.
- `allowBleWrites=false` remains the default and no BLE payload writer is added.
- No interlock/E-stop/key-switch bypass.
- Exact CUBE target is `E4:66:E5:D2:6E:38`.
- Required runtime files remain `Cube7Bridge.exe`, `Cube7Injector.exe`, `LaserOSHook.dll`, and `config.json`.

---

### Task 1: Protocol model and virtual-device self-test

**Files:**
- Create: `src/Cube7Bridge/VirtualLaserCubeProtocol.cs`
- Create: `src/Cube7Bridge/SelfTest.cs`
- Modify: `src/Cube7Bridge/Program.cs`

**Interfaces:**
- Produces: `VirtualLaserCubeProtocol.BuildFullInfo(VirtualLaserCubeConfig cfg, bool outputRequested) : byte[]`
- Produces: `VirtualLaserCubeProtocol.HandleCommand(ReadOnlySpan<byte> payload, VirtualLaserCubeState state, VirtualLaserCubeConfig cfg) : VirtualLaserCubeReply`
- Produces: `SelfTest.RunAsync() : Task<int>` invoked by `--self-test`.

- [ ] **Step 1: Write the failing self-test path**

Add a `--self-test` branch in `Program.Main` that calls a not-yet-existing `SelfTest.RunAsync()`. CI build must fail because `SelfTest` does not exist.

- [ ] **Step 2: Verify RED in GitHub Actions**

Push the branch with only the failing reference and run the Windows workflow. Expected: compile failure naming missing `SelfTest`.

- [ ] **Step 3: Implement protocol model and self-test**

Implement deterministic 64-byte `0x77` response, `0x78`, `0x8A`, virtual `0x80`, and `0xA9` metadata/buffer behavior. Self-test asserts response opcode/length, buffer flag behavior, output-requested software state, and sample-data point count.

- [ ] **Step 4: Verify GREEN**

Run `dotnet run --project src/Cube7Bridge/Cube7Bridge.csproj -- --self-test` in CI before packaging. Expected: `SELFTEST PASS` and exit code 0.

### Task 2: UDP VirtualLaserCubeServer

**Files:**
- Create: `src/Cube7Bridge/VirtualLaserCubeServer.cs`
- Modify: `src/Cube7Bridge/BridgeConfig.cs`
- Modify: `src/Cube7Bridge/Program.cs`
- Modify: `config.json`

**Interfaces:**
- Consumes: `VirtualLaserCubeProtocol.HandleCommand(...)`.
- Produces: `VirtualLaserCubeServer.RunAsync(CancellationToken ct) : Task`.
- Produces config: `BridgeConfig.VirtualLaserCubeConfig VirtualLaserCube`.

- [ ] **Step 1: Extend the self-test with UDP integration expectations**

Start `VirtualLaserCubeServer` on ephemeral test ports, send `0x77` to alive and command ports, assert 64-byte `0x77` response; send `0x8A` and assert four-byte response; send `0x80 01` and assert virtual state only; send `0xA9` and assert optional three-byte buffer response when enabled.

- [ ] **Step 2: Verify RED**

Expected compile failure because `VirtualLaserCubeServer` does not exist.

- [ ] **Step 3: Implement server**

Bind `0.0.0.0` on 45456/45457/45458 by default. Use one receive loop per socket, reply to sender, log bind failures as fatal, and write virtual-device NDJSON records.

- [ ] **Step 4: Verify GREEN**

Run `--self-test` and require all UDP integration assertions to pass.

### Task 3: Exact BLE target parsing

**Files:**
- Modify: `src/Cube7Bridge/BridgeConfig.cs`
- Modify: `src/Cube7Bridge/BleScanner.cs`
- Modify: `config.json`
- Modify: `src/Cube7Bridge/SelfTest.cs`

**Interfaces:**
- Produces: `BridgeConfig.BleConfig.Address` as `string?`.
- Produces: `BridgeConfig.ParseBluetoothAddress(string) : ulong`.

- [ ] **Step 1: Add failing address-parser assertions**

Assert `E4:66:E5:D2:6E:38` parses to `0xE466E5D26E38` and formats back identically.

- [ ] **Step 2: Verify RED**

Expected compile failure because parser does not exist / property type mismatches.

- [ ] **Step 3: Implement exact-address handling**

Parse colon/hyphen MAC strings, compare exact `ulong` address before name matching, and print `[ble] exact target matched` when selected. Set release `config.json` to `E4:66:E5:D2:6E:38`.

- [ ] **Step 4: Verify GREEN**

Run self-test; parser and round-trip assertions must pass.

### Task 4: Full-mode orchestration and release packaging

**Files:**
- Modify: `src/Cube7Bridge/Program.cs`
- Modify: `.github/workflows/build-windows.yml`
- Modify: `README.md`
- Modify: `VERSION.json`

**Interfaces:**
- `--mode=full` starts hook capture + injector + virtual UDP device + BLE scanner.
- `--mode=trace` starts hook capture + injector + virtual UDP device but no BLE.
- `--mode=ble` remains BLE-only.

- [ ] **Step 1: Add CI self-test gate**

After build, run `bin/Cube7Bridge.exe --self-test`. Packaging cannot run if it exits nonzero.

- [ ] **Step 2: Update orchestration and version**

Print `Cube7 LaserOS Full Bridge 0.3.0`, start virtual-device task in `full` and `trace`, and print `physical-output=DISABLED` prominently.

- [ ] **Step 3: Package and inspect artifact**

Require `Cube7Bridge.exe`, `Cube7Injector.exe`, `LaserOSHook.dll`, `config.json`; package `Cube7-LaserOS-FullBridge-0.3.0-win-x64.zip` and run the existing ZIP-content verifier.

- [ ] **Step 4: Verify final CI**

Expected workflow steps: Build FullBridge = success, Self-test Virtual LaserCube = success, Verify required runtime = success, Stage package = success, Verify packaged ZIP = success, Upload artifact = success.

### Task 5: Runtime smoke test without physical emission

**Files:**
- No production-code change unless verification exposes a defect.

- [ ] **Step 1: Download CI artifact**

Download the generated artifact and inspect ZIP contents locally.

- [ ] **Step 2: Verify binaries and configuration**

Confirm PE x64 files exist and `config.json` has `virtualLaserCube.enabled=true`, ports 45456/45457/45458, BLE address `E4:66:E5:D2:6E:38`, and `allowBleWrites=false`.

- [ ] **Step 3: Run offline package verification**

Run the release verifier against extracted package layout. Do not enable physical laser output.

- [ ] **Step 4: Fast-forward `main` only after all verification passes**

Move `main` to the verified feature commit and report the commit, workflow run, artifact name, and SHA-256.
