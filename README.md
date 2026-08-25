# Cube7 LaserOS Full Bridge 0.3.1 EARLYHOOK

Windows x64 bridge do diagnostyki **LaserOS.exe** oraz **Laserworld CUBE 7**.

## 0.3.1 — fix `NO LASER`

W 0.3.0 `LaserOSHook.dll` mógł zostać poprawnie załadowany (`PID=... OK`), ale nadal nie przechwytywać discovery LaserOS. Przyczyna została odtworzona testem integracyjnym: klasyczne funkcje Winsock `sendto` i `recvfrom` są często importowane z `WS2_32.dll` **po ordinalach** (`sendto=20`, `recvfrom=17`), a poprzedni IAT hook obsługiwał wyłącznie importy po nazwie.

0.3.1 patchuje zarówno importy po nazwie, jak i ordinale Winsock. CI uruchamia prawdziwy klient testowy: proces startuje jako `CREATE_SUSPENDED`, dostaje `LaserOSHook.dll` przed uruchomieniem, wysyła `0x77 GET_FULL_INFO` i musi odebrać prawidłową odpowiedź 64 B. Release nie jest pakowany, jeśli ten test nie przejdzie.

## Auto EarlyHook

`start-full.cmd` obsługuje teraz automatyczny early-hook:

1. jeżeli LaserOS już działa, FullBridge odczytuje ścieżkę `LaserOS.exe`;
2. wysyła normalne żądanie zamknięcia okna — **bez force kill**;
3. uruchamia LaserOS jako `CREATE_SUSPENDED`;
4. wstrzykuje `LaserOSHook.dll`;
5. czeka na pierwszy przebieg patchowania IAT;
6. dopiero wtedy wznawia LaserOS.

Jeżeli graceful restart się nie powiedzie, bridge pozostawia proces bez wymuszonego zamknięcia i przechodzi na zwykły watcher.

## Safety scope

- fizyczne wyjście lasera pozostaje wyłączone w warstwie Virtual LaserCube;
- `SET_OUTPUT 0x80` zmienia wyłącznie stan wirtualny;
- brak payload writes do BLE;
- brak bypassu interlock/E-stop.

## Wykryty CUBE 7

Domyślny target w `config.json`:

```text
BLE address: E4:66:E5:D2:6E:38
BLE name:    BLEAPP_C77D_V217
Service:     0000ffe0-0000-1000-8000-00805f9b34fb
FFE1:        Read / WriteWithoutResponse / Write / Notify
FFE2:        WriteWithoutResponse / Write
```

FullBridge 0.3.1 nadal używa vendor GATT tylko do READ/NOTIFY.

## Uruchomienie

```bat
start-full.cmd
```

Przy działającym LaserOS oczekiwane logi zawierają m.in.:

```text
[early] detected LaserOS path: ...\LaserOS.exe
[early] LaserOS already running PID=...; restarting gracefully so discovery is hooked before startup.
[early] suspended-launch injector started; LaserOS will resume only after LaserOSHook.dll is loaded.
[early] CREATE_SUSPENDED exe=...\LaserOS.exe
[inject] PID=... OK
[early] hook settle barrier complete
[early] resumed PID=... after hook injection
[hook] connected
```

Virtual LaserCube obsługuje discovery/trace dla portów `45456`, `45457`, `45458` oraz znane komendy `0x77`, `0x78`, `0x80`, `0x8A`, `0xA9`.

## Self-test

```bat
bin\Cube7Bridge.exe --self-test
```

Oczekiwany wynik:

```text
SELFTEST PASS: protocol + UDP virtual device + BLE target parser; physical-output=DISABLED
```

## Runtime

```text
Cube7-LaserOS-FullBridge-0.3.1-win-x64/
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

Prawidłowy wynik: `FULLBRIDGE_READY=1`.

Jeżeli LaserOS działa jako administrator, FullBridge również powinien być uruchomiony z tym samym poziomem integralności. Build docelowy jest x64.
