# Cube7 LaserOS Full Bridge 0.6.0 — LOOPBACK-AUTOCONFIG

Windows x64 bridge do testowania **LaserOS.exe** z wirtualnym urządzeniem LaserCube/CUBE 7 bez fizycznego wyjścia lasera.

## Co zmienia 0.6.0

0.6.0 zastępuje główny in-process responder z 0.5.0 realnym serwerem UDP na localhost i używa wstrzykniętego hooka wyłącznie do automatycznej konfiguracji Winsock po stronie LaserOS:

- realny `Cube7Bridge.exe` nasłuchuje na `127.0.0.1:45456/45457/45458`;
- `LaserOSHook.dll` przepisuje destination LaserCube na loopback;
- lokalne bindy klienta LaserOS do `45456/45457/45458` są remapowane na porty ephemeral, aby nie kolidowały z serwerem;
- serwer startuje i binduje wszystkie trzy porty **przed** zapisaniem profilu hooka i **przed** wznowieniem LaserOS z suspended-launch;
- in-process protocol responder w DLL jest w trybie loopback wyłączony;
- early DLL injection, renderer tap, capture, BLE trace i support bundle pozostają;
- authentication `0xB0/0xB1` pozostaje **trace-only** — brak syntetycznego `AUTH OK`.

## Twarde invariants bezpieczeństwa

Domyślna konfiguracja 0.6.0 wymusza:

```text
physicalOutput=false
allowBleWrites=false
allowSyntheticAuthentication=false
interlockBypass=false
```

FullBridge emuluje wyłącznie warstwę programową urządzenia. Nie wysyła vendor payloadów do fizycznego CUBE 7 i nie obchodzi E-stop/interlock.

## Zweryfikowany LaserOS

```text
LaserOS x64 v0.18.1 BETA
C:\Program Files\LaserOS\LaserOS.exe
SHA-256:
21799b2b9c651be87d69a4d977fa09ef14b8ce22de13499a7974669c36991c0c
```

Jeżeli SHA-256 nie pasuje, injection jest blokowane przez preflight.

## Domyślna konfiguracja sieciowa

```json
"virtualLaserCube": {
  "enabled": true,
  "networkServerEnabled": true,
  "bindAddress": "127.0.0.1",
  "alivePort": 45456,
  "commandPort": 45457,
  "dataPort": 45458,
  "firmwareMajor": 1,
  "firmwareMinor": 0,
  "dacRate": 30000,
  "maxDacRate": 30000,
  "bufferSize": 6000,
  "modelNumber": 7,
  "modelName": "Cube7 Virtual Test Device"
}
```

Profil przekazywany do DLL ma postać:

```text
mode=loopback
enabled=1
address=127.0.0.1
alivePort=45456
commandPort=45457
dataPort=45458
rewriteDestinations=1
rewriteClientBinds=1
physicalOutput=0
syntheticAuthentication=0
```

## Przepływ runtime

```text
Cube7Bridge.exe
  1. preflight SHA LaserOS
  2. bind real UDP server 127.0.0.1:45456/45457/45458
  3. write loopback injection profile
  4. CREATE_SUSPENDED LaserOS.exe
  5. inject LaserOSHook.dll
  6. hook settle barrier
  7. ResumeThread LaserOS.exe

LaserOS destination :45456/:45457/:45458
             |
             | Winsock destination rewrite
             v
127.0.0.1:45456/:45457/:45458
             |
             v
      Cube7Bridge.exe
```

Jeżeli LaserOS próbuje lokalnie zrobić `bind()` na canonical portach LaserCube, hook zmienia lokalny port na `0`, dzięki czemu Windows przydziela realny port ephemeral. Pozwala to serwerowi i klientowi działać na tym samym komputerze bez kolizji portów.

## Sekwencja discovery

```text
LaserOS -> UDP 45456: 27
Server  ->             27 00

LaserOS -> UDP 45457: 77
Server  ->             64-byte FULL_INFO
```

Dalsze komendy `0x78`, `0x80`, `0x8A` i `0xA9` obsługuje realny `VirtualLaserCubeServer`. `0x80` zmienia tylko stan wirtualny; `physicalOutputEnabled` pozostaje zawsze `false`.

## Renderer i diagnostyka

Renderer tap działa niezależnie od fizycznego sprzętu. Przechwycone punkty są normalizowane do:

```text
X,Y   = 0..4095
R,G,B = 0..4095
```

Podgląd diagnostyczny:

```text
captures/<timestamp>/renderer-preview.svg
```

Stan i capture:

```text
captures/<timestamp>/
  capture.ndjson
  handshake-summary.ndjson
  renderer-frames.ndjson
  renderer-frames.bin
  renderer-preview.svg
  translator-dryrun.ndjson
  virtual-device-state.json
  virtual-lasercube-*.ndjson
  ble-*.ndjson
  ble-protocol-trace.ndjson
  support-bundle.zip
```

## BLE CUBE 7

Potwierdzony target:

```text
Address: E4:66:E5:D2:6E:38
Name:    BLEAPP_C77D_V217
Service: 0000ffe0-0000-1000-8000-00805f9b34fb
FFE1:    Read / WriteWithoutResponse / Write / Notify
FFE2:    WriteWithoutResponse / Write
```

BLE pozostaje pasywne: subskrypcja FFE1 jest dozwolona, ale vendor payload writes są wyłączone.

## Uruchomienie

Rozpakuj release do pustego katalogu i uruchom:

```bat
start-full.cmd
```

Oczekiwane kluczowe wpisy startowe:

```text
Cube7 LaserOS Full Bridge 0.6.0 LOOPBACK-AUTOCONFIG
[loopback] real UDP server ready at 127.0.0.1 ports=45456/45457/45458
[loopback] injection profile armed: destination rewrite + client bind collision avoidance; in-process protocol responder=OFF
[early] suspended-launch injector started; LaserOS will resume only after LaserOSHook.dll is loaded.
```

Dostępne tryby:

```bat
start-full.cmd
start-trace.cmd
start-ble.cmd
```

## CI / self-test

Windows CI sprawdza między innymi:

- model protokołu `0x27/0x77/0x78/0x80/0x8A/0xA9`;
- realny localhost UDP round-trip;
- parser profilu loopback;
- destination rewrite do `127.0.0.1`;
- remap canonical client bind do realnego portu ephemeral;
- injected-loopback probe uruchamiany przez `Cube7Injector.exe`;
- wymagane pliki runtime;
- bezpieczne wartości konfiguracji;
- zawartość finalnego ZIP-a.
