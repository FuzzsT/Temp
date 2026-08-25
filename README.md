# Cube7 LaserOS Full Bridge 0.4.2 — BLE-UART-PROTOCOL-TRACE

Windows x64 bridge do diagnostyki **LaserOS.exe** oraz **Laserworld CUBE 7**.

## Status 0.4.2

0.4.2 rozszerza 0.4.1 HARDENED-DRYRUN o pasywną warstwę analizy vendor BLE UART. Fizyczne wyjście lasera pozostaje wyłączone, `allowBleWrites=false`, a profil protokołu nie ma ścieżki transmisji do FFE2.

Najważniejsze elementy:

- wszystko z 0.4.1: SHA-256 preflight, renderer tap, pipeline status, dry-run translator i support bundle;
- potwierdzony target BLE `E4:66:E5:D2:6E:38` / `BLEAPP_C77D_V217`;
- mapowanie GATT FFE0/FFE1/FFE2;
- automatyczne połączenie i subskrypcja FFE1 notifications;
- klasyfikacja 244-bajtowego bufora zer jako `idle-zero-buffer`;
- parser kandydackiego formatu `[AA][CMD][LEN][PAYLOAD][SUM8]`;
- walidacja sumy modulo 256 dla przechwyconych pakietów pasujących do tego formatu;
- generator kandydackich pakietów wyłącznie do logu (`direction=dry-run-candidate`, `transmit=false`);
- `ble-protocol-trace.ndjson` do dalszego porównania z ruchem oficjalnej aplikacji;
- profil protokołu jest jawnie oznaczony `UNVERIFIED`.

Materiał źródłowy dla warstwy BLE zawiera sprzeczne mapy command ID oraz różne hipotezy checksum (SUM/XOR/CRC). Z tego powodu 0.4.2 **nie traktuje command IDs jako zweryfikowanych i nie wysyła vendor payloads do FFE2**.

## Zweryfikowany LaserOS

```text
LaserOS x64 v0.18.1 BETA
C:\Program Files\LaserOS\LaserOS.exe
SHA-256:
21799b2b9c651be87d69a4d977fa09ef14b8ce22de13499a7974669c36991c0c
```

Domyślna konfiguracja wymaga dokładnie tego SHA-256. Jeśli plik jest inny, FullBridge zatrzyma injection przed uruchomieniem hooka i pokaże `LASEROS_PREFLIGHT BLOCKED`.

## Status pipeline

Co kilka sekund konsola drukuje m.in.:

```text
LASEROS_PREFLIGHT    OK         LaserOS SHA-256 verified
LASEROS_HOOK         OK         LaserOSHook.dll connected
DISCOVERY_0x27       OK         27 00 accepted
FULL_INFO_0x77       OK         64-byte response accepted
AUTH_B0_B1           WAITING    no auth traffic observed
RENDERER_TAP         OK         frames=120 rate=30000pps points=842 max=900
CUBE7_BLE            OK         E4:66:E5:D2:6E:38 BLEAPP_C77D_V217
BLE_PROTOCOL         TRACE      community-aa-sum-v1-candidate confidence=UNVERIFIED transmit=DISABLED
TRANSLATOR           DRY-RUN    normalized=842 physical-output=OFF
PHYSICAL_OUTPUT      DISABLED   hard safety invariant
```

`AUTH_B0_B1=CAPTURED` oznacza, że odpowiedź B1 została przechwycona, ale nie jest to równoznaczne z zaakceptowaną autoryzacją. `AUTH_B0_B1=OK` pojawia się dopiero po zaobserwowaniu ruchu post-auth.

## Renderer tap

0.4.x przechwytuje ramki renderer przed warstwą sprzętową LaserCube. Pliki sesji:

```text
captures/<timestamp>/
  capture.ndjson
  handshake-summary.ndjson
  renderer-frames.ndjson
  renderer-frames.bin
  translator-dryrun.ndjson
  ble-*.ndjson
  ble-protocol-trace.ndjson
```

`translator-dryrun.ndjson` zawiera znormalizowane punkty 12-bit:

```text
X,Y = 0..4095
R,G,B = 0..4095
```

Translator nie wysyła tych punktów do CUBE 7.

## BLE target CUBE 7

```text
Address: E4:66:E5:D2:6E:38
Name:    BLEAPP_C77D_V217
Service: 0000ffe0-0000-1000-8000-00805f9b34fb
FFE1:    Read / WriteWithoutResponse / Write / Notify
FFE2:    WriteWithoutResponse / Write
Observed idle FFE1 read: 244 x 00
Negotiated MTU observed by probe: 247
```

Domyślne 0.4.2:

```text
autoConnect=true
subscribeNotifications=true
allowBleWrites=false
BLE protocol profile=community-aa-sum-v1-candidate
confidence=UNVERIFIED
transmit=false
```

CCCD notification subscription jest używana wyłącznie do odbioru FFE1. FullBridge nie wykonuje vendor payload writes do FFE1/FFE2.

### Candidate protocol trace

0.4.2 sprawdza, czy odebrane bytes pasują do kandydackiego formatu:

```text
AA CMD LEN PAYLOAD... CHECKSUM
CHECKSUM candidate = sum(previous bytes) mod 256
```

Wyniki trafiają do:

```text
ble-protocol-trace.ndjson
```

Klasyfikacje:

```text
idle-zero-buffer
AA-candidate-packet
unknown
```

Plik zawiera także dwa **dry-run candidates** służące jako referencja do porównania z przyszłym capture oficjalnej aplikacji. Nie są one transmitowane.

## Support bundle

Po `Ctrl+C` FullBridge automatycznie tworzy:

```text
captures/<timestamp>/support-bundle.zip
```

W środku znajdują się m.in. status, preflight, renderer stats, config i bezpieczne `*.ndjson`, w tym `ble-protocol-trace.ndjson`. Surowe pliki binarne są celowo wykluczone z automatycznego bundle.

## Uruchomienie

Rozpakuj release do pustego katalogu i uruchom:

```bat
start-full.cmd
```

Tryby:

```bat
start-full.cmd
start-trace.cmd
start-ble.cmd
```

`start-ble.cmd` jest najszybszą opcją do zebrania nowego FFE1 trace bez uruchamiania LaserOS.

## Self-test

```bat
bin\Cube7Bridge.exe --self-test
```

Self-test obejmuje również kandydacki packet builder/parser, checksum mismatch detection, profil `UNVERIFIED`, brak transmisji i klasyfikację idle 244-byte FFE1 buffer.

## Runtime

```text
Cube7-LaserOS-FullBridge-0.4.2-BLE-UART-PROTOCOL-TRACE-win-x64/
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

Weryfikacja release:

```powershell
.\verify-release.ps1 -BinDir .\bin
```

Prawidłowy wynik: `FULLBRIDGE_READY=1`.

Jeśli LaserOS działa z podwyższonym poziomem integralności, FullBridge powinien być uruchomiony z tym samym poziomem uprawnień, aby injection mogło się udać.
