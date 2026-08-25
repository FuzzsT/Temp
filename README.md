# Cube7 LaserOS Full Bridge 0.4.1 — HARDENED-DRYRUN

Windows x64 bridge do diagnostyki **LaserOS.exe** oraz **Laserworld CUBE 7**.

## Status 0.4.1

0.4.1 porządkuje gałąź 0.4.x i dodaje warstwę hardeningu wokół istniejącego renderer tap. Fizyczne wyjście lasera pozostaje domyślnie i technicznie wyłączone w tym wydaniu.

Najważniejsze elementy:

- preflight konkretnego `LaserOS.exe` z SHA-256;
- fail-closed dla niezweryfikowanego builda LaserOS, gdy `requireVerifiedSha256=true`;
- czytelny status całego pipeline'u;
- statystyki renderer frames;
- translator `RendererFrame -> Cube7NormalizedFrame` działający wyłącznie w trybie dry-run;
- automatyczny `support-bundle.zip` po zakończeniu sesji;
- support bundle zawiera tylko bezpieczne logi/summaries/config — nie dodaje domyślnie `.rawbin` ani `.bin` z surowymi payloadami auth;
- brak BLE payload writes;
- brak bypassu interlock/E-stop;
- `PHYSICAL_OUTPUT=DISABLED` jest niezmiennikiem 0.4.1.

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
CUBE7_BLE            OK         E4:66:E5:D2:6E:38 ...
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
```

`translator-dryrun.ndjson` zawiera znormalizowane punkty 12-bit:

```text
X,Y = 0..4095
R,G,B = 0..4095
```

Translator nie wysyła tych punktów do CUBE 7.

## Support bundle

Po `Ctrl+C` FullBridge automatycznie tworzy:

```text
captures/<timestamp>/support-bundle.zip
```

W środku znajdują się:

```text
support/status.json
support/preflight.json
support/renderer-stats.json
support/safety.json
config/config.json
captures/*.ndjson
```

Surowe pliki binarne są celowo wykluczone z automatycznego support bundle, ponieważ mogą zawierać materiał związany z handshake/auth.

## BLE target CUBE 7

```text
Address: E4:66:E5:D2:6E:38
Name:    BLEAPP_C77D_V217
Service: 0000ffe0-0000-1000-8000-00805f9b34fb
FFE1:    Read / WriteWithoutResponse / Write / Notify
FFE2:    WriteWithoutResponse / Write
```

Domyślne 0.4.1:

```text
autoConnect=false
subscribeNotifications=false
allowBleWrites=false
```

Skan BLE służy do identyfikacji urządzenia i statusu. Nie są wysyłane vendor payload writes.

## Uruchomienie

Rozpakuj release do pustego katalogu i uruchom:

```bat
start-full.cmd
```

FullBridge przeprowadzi preflight, uruchomi/zhookuje LaserOS i rozpocznie renderer/BLE diagnostics.

Tryby:

```bat
start-full.cmd
start-trace.cmd
start-ble.cmd
```

## Self-test

```bat
bin\Cube7Bridge.exe --self-test
```

Oczekiwany wynik:

```text
SELFTEST PASS: hardened preflight + pipeline status + renderer stats + dry-run translator + support bundle + renderer tap + protocol; physical-output=DISABLED
```

## Runtime

```text
Cube7-LaserOS-FullBridge-0.4.1-HARDENED-DRYRUN-win-x64/
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
