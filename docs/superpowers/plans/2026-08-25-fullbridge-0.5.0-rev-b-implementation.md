# FullBridge 0.5.0 rev B — Implementation Plan

## Goal

Fix the two blockers proven by the user's 0.4.1 captures: renderer files remain zero bytes even when the hook pipe is connected, and LaserOS discovery remains at GET_ALIVE because the diagnostic virtual responder is not active. Add Control Center observability so these states are explicit instead of silently waiting.

## Safety invariants

- physical laser output remains disabled;
- no vendor BLE payload writes;
- no interlock/E-stop bypass;
- no fabricated authentication success;
- virtual SET_OUTPUT remains virtual state only.

## Tasks

1. Add a native renderer-hook health record (HookApi 21) containing patch flags and call/frame counters.
2. Emit renderer-hook health periodically from LaserOSHook.dll and increment counters in createNewFrame/vertex/vertex3 hooks.
3. Decode and persist renderer-hook-health.ndjson in Cube7Bridge; derive RENDERER_TAP=PATCHED/ACTIVE/ERROR instead of indefinite WAITING.
4. Add a renderer watchdog: hook connected + renderer patch installed + no renderer calls/frames for the configured interval becomes RENDERER_HOOK_IDLE; missing patches become RENDERER_HOOK_FAILED.
5. Add diagnostic virtual discovery mode. It may answer 0x27 and 0x77 only when explicitly enabled, but does not synthesize B0/B1 authentication success and never enables physical output.
6. Make diagnostic discovery status explicit in PipelineStatus.
7. Add self-tests for health wire decode/state evaluation and diagnostic virtual discovery safety invariants.
8. Add a minimal WPF Cube7ControlCenter executable showing Hook, Renderer, BLE, Discovery, Translator and Output states and an action to restart the bridge in diagnostic mode.
9. Update CI/release verification for the 0.5.0 branch and Control Center binary.
10. Package only after Windows build, both self-tests, native verification and ZIP verification pass.
