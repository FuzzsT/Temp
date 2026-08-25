# LaserOS 0.18.1 — Network LaserCube authentication trace

Target binary inspected during development:

- `LaserOS.exe`
- SHA-256: `21799b2b9c651be87d69a4d977fa09ef14b8ce22de13499a7974669c36991c0c`
- PE64 / x86-64

This document records passive interoperability findings only. It does not define an interlock bypass and FullBridge does not generate a synthetic authentication-success result.

## Imports used by LaserOS

The inspected executable imports all four security callback registration functions from `ldCore.dll`:

- `ldNetworkHardwareManager::setGenerateSecurityRequestCb`
- `ldNetworkHardwareManager::setAuthenticateSecurityCb`
- `ldUsbHardwareManager::setGenerateSecurityRequestCb`
- `ldUsbHardwareManager::setAuthenticateSecurityCb`

The network and USB managers are configured with the same two LaserOS callback functions.

Observed registration call sites in this executable:

- network generate callback registration: VA `0x1404D5945`
- network authenticate callback registration: VA `0x1404D5952`
- USB generate callback registration: VA `0x1404D595F`
- USB authenticate callback registration: VA `0x1404D596C`

Callback entry points passed by LaserOS:

- generate security request: VA `0x1404D52E0`
- authenticate security response: VA `0x1404D51F0`

These addresses are build-specific and are only diagnostics for the SHA-256 above.

## Network protocol state machine

Public `libLaserdockCore` source establishes this sequence:

1. LaserOS broadcasts `0x27` on UDP 45456.
2. A valid cube replies exactly `27 00`.
3. LaserOS creates `LaserdockNetworkDevice` for the response source address.
4. It requests `0x77 GET_FULL_INFO` on UDP 45457.
5. A successful 64-byte full-info packet moves the device to `AUTHENTICATING` and emits `DeviceNeedsAuthenticating`.
6. `ldNetworkHardwareManager` obtains request bytes from the registered generate callback and `LaserdockNetworkDevice::SecurityRequest` prepends opcode `0xB0`.
7. A successful `0xB0` acknowledgement causes the client to send a one-byte `0xB1` query.
8. The `0xB1` response is passed to the registered LaserOS authenticate callback.
9. Only when that callback returns true does the network device transition to `INITIALIZED`/`DeviceReady`.

Therefore discovery success alone is insufficient to populate the active hardware list.

## FullBridge 0.3.3 diagnostic stages

`handshake-summary.ndjson` records passive stage transitions with SHA-256 digests and payload lengths:

- `DISCOVERY_REQUEST`
- `DISCOVERY_ACCEPTED`
- `FULL_INFO_REQUEST`
- `FULL_INFO_ACCEPTED`
- `AUTH_REQUEST`
- `AUTH_REQUEST_ACK`
- `AUTH_RESPONSE_QUERY`
- `AUTH_RESPONSE_CAPTURED`
- `POST_AUTH_DEVICE_TRAFFIC`

A digest is included so repeated requests/responses can be compared without relying on console hex dumps.

## Interpretation

If a run stops at `DISCOVERY_REQUEST`, the 45456 discovery response path is broken.

If it reaches `FULL_INFO_ACCEPTED` but never `AUTH_REQUEST`, the full-info payload is not causing the expected initialization transition or the hooked path differs from the public library flow.

If it reaches `AUTH_REQUEST` but never `AUTH_REQUEST_ACK`, a real device-side security exchange has not been completed.

If it reaches `AUTH_RESPONSE_CAPTURED` but never produces post-auth initialization traffic, LaserOS likely rejected the response in its registered authenticate callback.

`POST_AUTH_DEVICE_TRAFFIC` is evidence of progress after the security response but is not treated as proof that physical laser output is safe or enabled.
