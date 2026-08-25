# Cube7 LaserOS Full Bridge 0.5.0 — VIRTUAL-DEVICE-EMU

Windows x64 bridge do testowania **LaserOS.exe** z wirtualnym urządzeniem LaserCube/CUBE 7 bez fizycznego wyjścia lasera.

## Co zmienia 0.5.0

0.5.0 rozwija warstwy 0.4.x w kierunku pełnego wirtualnego projektora testowego wewnątrz procesu LaserOS:

- early DLL injection przed discovery LaserOS;
- wstrzyknięty responder LaserCube UDP (`0x27`, `0x77`, buffer/status/data); 
- state machine wirtualnego urządzenia: `OFFLINE -> DISCOVERED -> IDENTIFIED -> AUTH-TRACE -> READY -> STREAMING`;
- osobny `VirtualProtocolEngine` oparty na istniejącym modelu LaserCube;
- authentication `0xB0/0xB1` pozostaje **trace-only** — brak syntetycznego `AUTH OK`;
- model `VirtualHandleTable` przygotowany pod przyszłą emulację USB/HID;
- klasyfikacja ścieżek `SetupAPI / HID / CreateFile / ReadFile / WriteFile / DeviceIoControl` po stronie diagnostycznej;
- renderer tap i generowany `renderer-preview.svg`;
- `virtual-device-state.json` ze stanem projektora i licznikami;
- dry-run translator `RendererFrame -> 12-bit XYRGB`;
- pasywna analiza BLE `FFE0/FFE1/FFE2` z 0.4.2;
- support bundle bez surowych binarnych capture.

## Twarde invariants bezpieczeństwa

Domyślna konfiguracja 0.5.0 wymusza:

```text
physicalOutput=false
allowBleWrites=false
allowSyntheticAuthentication=false
interlockBypass=false
```

FullBridge może emulować obecność urządzenia i przechwytywać ramki, ale nie wysyła vendor payloadów do fizycznego CUBE 7 i nie obchodzi E-stop/interlock.

## Zweryfikowany LaserOS

```text
LaserOS x64 v0.18.1 BETA
C:\Program Files\LaserOS\LaserOS.exe
SHA-256:
21799b2b9c651be87d69a4d977fa09ef14b8ce22de13499a7974669c36991c0c
```

Jeżeli SHA-256 nie pasuje, injection jest blokowane przez preflight.

## Wirtualne urządzenie

Domyślnie:

```json
"virtualDevice": {
  "enabled": true,
  "network": true,
  "usbHid": "trace-first",
  "physicalOutput": false,
  "allowSyntheticAuthentication": false,
  "writeDeviceStateReport": true,
  "writeRendererPreview": true
}
```

oraz:

```json
"virtualLaserCube": {
  "enabled": true,
  "networkServerEnabled": false,
  "alivePort": 45456,
  "commandPort": 45457,
  "dataPort": 45458,
  "modelName": "Cube7 Virtual Test Device"
}
```

`networkServerEnabled=false` jest celowe. Główny responder działa **wewnątrz LaserOSHook.dll**, dzięki czemu nie konkuruje z portami UDP wiązanymi przez sam LaserOS.

## Sekwencja discovery

```text
LaserOS -> UDP 45456: 27
Virtual ->             27 00

LaserOS -> UDP 45457: 77
Virtual ->             64-byte FULL_INFO
```

Po tym bridge śledzi dalsze komendy inicjalizacji. `B0/B1` są rejestrowane, ale nie są fałszowane.

## State machine

```text
OFFLINE
  -> DISCOVERED       0x27 accepted
  -> IDENTIFIED       0x77 full-info accepted
  -> AUTH-TRACE       B0/B1 observed
  -> READY            post-auth device traffic observed
  -> STREAMING        renderer frames observed
```

Stan jest zapisywany do:

```text
captures/<timestamp>/virtual-device-state.json
```

## Renderer preview

Renderer tap działa niezależnie od fizycznego sprzętu. Przechwycone punkty są normalizowane do:

```text
X,Y   = 0..4095
R,G,B = 0..4095
```

i używane do diagnostycznego podglądu:

```text
captures/<timestamp>/renderer-preview.svg
```

Nie jest to ścieżka fizycznej emisji.

## USB/HID

0.5.0 utrzymuje tryb:

```text
usbHid=trace-first
```

Warstwa modelowa rozpoznaje rodziny:

```text
SETUPAPI
HID
FILEIO
DEVICEIO
```

Nie tworzy jeszcze syntetycznego urządzenia PnP Windows i nie fałszuje VID/PID w systemie. Emulacja sieciowa pozostaje główną, potwierdzoną ścieżką LaserOS. USB/HID jest rozwijane dopiero po potwierdzeniu, że konkretna wersja LaserOS wykonuje tę ścieżkę.

## BLE CUBE 7

Potwierdzony target z capture:

```text
Address: E4:66:E5:D2:6E:38
Name:    BLEAPP_C77D_V217
Service: 0000ffe0-0000-1000-8000-00805f9b34fb
FFE1:    Read / WriteWithoutResponse / Write / Notify
FFE2:    WriteWithoutResponse / Write
```

BLE pozostaje pasywne: subskrypcja FFE1 jest dozwolona, ale vendor payload writes są wyłączone.

## Status pipeline

Przykład:

```text
LASEROS_PREFLIGHT    OK
LASEROS_HOOK         OK
DISCOVERY_0x27       OK
FULL_INFO_0x77       OK
AUTH_B0_B1           ACTIVE
VIRTUAL_DEVICE       AUTH-TRACE
USB_HID_TRACE        ARMED
RENDERER_TAP         OK
CUBE7_BLE            OK
BLE_PROTOCOL         TRACE
TRANSLATOR           DRY-RUN
PHYSICAL_OUTPUT      DISABLED
```

Po zaobserwowaniu ramki:

```text
VIRTUAL_DEVICE       STREAMING  frames=... points=... physical-output=OFF
```

## Pliki capture

```text
captures/<timestamp>/
  capture.ndjson
  handshake-summary.ndjson
  renderer-frames.ndjson
  renderer-frames.bin
  renderer-preview.svg
  translator-dryrun.ndjson
  virtual-device-state.json
  ble-*.ndjson
  ble-protocol-trace.ndjson
  support-bundle.zip
```

## Uruchomienie

Rozpakuj release do pustego katalogu i uruchom:

```bat
start-full.cmd
```

W trybie `full` bridge:

1. weryfikuje SHA LaserOS;
2. uruchamia LaserOS przez suspended-launch injector;
3. wstrzykuje `LaserOSHook.dll` przed discovery;
4. uzbraja wirtualny responder sieciowy;
5. przechwytuje handshake i renderer;
6. równolegle wykonuje pasywny BLE scan/notify trace.

Dostępne tryby:

```bat
start-full.cmd
start-trace.cmd
start-ble.cmd
```

## Self-test

```bat
bin\Cube7Bridge.exe --self-test
```

Self-test 0.5.0 obejmuje m.in.:

- discovery `0x27 -> 27 00`;
- 64-byte FULL_INFO;
- state machine wirtualnego urządzenia;
- brak syntetycznej autoryzacji;
- virtual handle table;
- klasyfikację API trace;
- renderer SVG preview;
- physical-output invariant;
- renderer/dry-run translator;
- BLE UART candidate parser z 0.4.2.

## Runtime

```text
Cube7-LaserOS-FullBridge-0.5.0-VIRTUAL-DEVICE-EMU-win-x64/
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

Weryfikacja:

```powershell
.\verify-release.ps1 -BinDir .\bin
```

Prawidłowy wynik kończy się:

```text
FULLBRIDGE_READY=1
```
