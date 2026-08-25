# FullBridge 0.3.0 Virtual LaserCube Design

## Goal

Make LaserOS x64 detect a software-defined LaserCube endpoint while preserving the existing trace/injector and BLE discovery tooling. The virtual endpoint must emulate only the network-side discovery/status/data surface needed for LaserOS to enumerate and stream to it. It must not enable physical laser emission, write unknown BLE characteristics, or bypass interlocks.

## Evidence and root cause

The supplied capture contains one non-empty socket trace with 259 one-byte `0x27` RX records and no LaserCube UDP discovery/command/data traffic on ports 45456/45457/45458. Later socket captures are empty. The injector reports success, so the missing behavior is not DLL loading: the bridge has no virtual LaserCube responder that LaserOS can discover.

BLE advertising shows the intended CUBE target at `E4:66:E5:D2:6E:38`, but it advertises with an empty local name. The current configuration uses name matching and `address: null`, so the scanner does not select it for GATT enumeration.

## Architecture

`Cube7Bridge.exe --mode=full` will run four independent units:

1. Existing named-pipe socket trace server.
2. Existing injector watcher for `LaserOS.exe`.
3. New `VirtualLaserCubeServer` bound to the LaserCube UDP ports.
4. Existing BLE scanner, now configured with an exact CUBE address.

The virtual device exposes loopback/LAN UDP behavior compatible with the documented LaserCube protocol:

- `45456` ALIVE/discovery listener.
- `45457` command listener.
- `45458` point-data listener.
- `0x77 GET_FULL_INFO` returns a deterministic 64-byte device information packet.
- `0x78 ENABLE_BUFFER_SIZE_RESPONSE_ON_DATA` stores a virtual flag and acknowledges.
- `0x8A GET_RINGBUFFER_EMPTY_SAMPLE_COUNT` returns virtual free capacity.
- `0x80 SET_OUTPUT` stores only a virtual requested-output flag and acknowledges; it never drives BLE or physical output.
- `0xA9 SAMPLE_DATA` records message/frame metadata and XYRGB payload, updates virtual buffer accounting, and optionally replies with buffer-free information.

## Network behavior

The server binds IPv4 `0.0.0.0` so broadcasts sent by LaserOS can reach it. `EnableBroadcast` and `ReuseAddress` are enabled where supported. Responses are sent back to the source endpoint of each datagram. The model returned by `0x77` is `LaserCube Virtual CUBE7`, connection type `3` (Wi-Fi), with a deterministic six-byte serial and configurable IP/model fields.

Because the exact LaserOS discovery sequence is not yet proven, the server accepts `0x77` on both 45456 and 45457 and responds from the socket that received it. This is deliberately narrow: no arbitrary protocol guessing beyond the known documented opcodes.

## Data capture

Virtual-device traffic is written to a dedicated NDJSON file under the existing capture directory. Each record includes timestamp, local port, remote endpoint, direction, opcode, payload length, and decoded metadata. For `0xA9`, the capture includes message number, frame number, point count, and a bounded hex prefix; full raw point data is not duplicated into console output.

## BLE target

`BridgeConfig.BleConfig.Address` becomes a string MAC address in configuration so users can write `E4:66:E5:D2:6E:38` directly. Parsing converts it to the WinRT `ulong` address. Exact address matching takes priority over name matching. Default release configuration targets `E4:66:E5:D2:6E:38`.

BLE behavior remains read-only except for CCCD notification subscription. `allowBleWrites` remains `false` and is not connected to any physical-output path.

## Configuration

Add a `virtualLaserCube` section:

```json
{
  "enabled": true,
  "bindAddress": "0.0.0.0",
  "alivePort": 45456,
  "commandPort": 45457,
  "dataPort": 45458,
  "firmwareMajor": 1,
  "firmwareMinor": 0,
  "dacRate": 30000,
  "maxDacRate": 30000,
  "bufferSize": 6000,
  "modelNumber": 7,
  "modelName": "LaserCube Virtual CUBE7"
}
```

## Safety invariants

- `SET_OUTPUT` is virtual state only.
- No BLE characteristic payload writes are introduced.
- No interlock, key switch, E-stop, or LaserOS safety controls are bypassed.
- The bridge must log that physical output is disabled.
- Failure to bind a required UDP port makes virtual-device startup fail loudly instead of silently degrading.

## Validation

CI must build x64 and run a protocol self-test before packaging. The self-test starts the virtual server on test ports, sends `0x77`, `0x78`, `0x8A`, `0x80`, and `0xA9`, verifies response opcodes/lengths/state, and verifies `0x80` does not invoke any BLE writer. Release verification still requires `Cube7Bridge.exe`, `Cube7Injector.exe`, `LaserOSHook.dll`, and `config.json`.
