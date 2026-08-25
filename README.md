# Cube7 LaserOS Full Bridge 0.3.3 — AUTH TRACE

Windows x64 bridge do diagnostyki **LaserOS.exe** oraz **Laserworld CUBE 7**.

## 0.3.3 — pasywna diagnostyka `0xB0/0xB1`

0.3.2 naprawił rzeczywisty etap discovery `0x27 -> 27 00`. 0.3.3 dodaje automatyczny tracker całego handshake'u LaserOS:

```text
DISCOVERY_REQUEST
DISCOVERY_ACCEPTED
FULL_INFO_REQUEST
FULL_INFO_ACCEPTED
AUTH_REQUEST
AUTH_REQUEST_ACK
AUTH_RESPONSE_QUERY
AUTH_RESPONSE_CAPTURED
POST_AUTH_DEVICE_TRAFFIC
```

Każda zmiana etapu jest wypisywana jako `[stage] ...` oraz zapisywana w:

```text
captures/<timestamp>/handshake-summary.ndjson
```

Podsumowanie zawiera długość payloadu i SHA-256, dzięki czemu można porównywać kolejne challenge/response bez ręcznego przeglądania pełnego hexdumpu.

FullBridge **nie generuje syntetycznego `AUTH OK`**. Tracker jest pasywny i służy do ustalenia, czy LaserOS zatrzymuje się na discovery, full-info, ACK `0xB0`, odczycie `0xB1`, czy dopiero na własnym callbacku weryfikującym odpowiedź.

## Wynik analizy konkretnego LaserOS 0.18.1

Przeanalizowany plik:

```text
LaserOS.exe
SHA-256 21799b2b9c651be87d69a4d977fa09ef14b8ce22de13499a7974669c36991c0c
```

Ten build importuje z `ldCore.dll` zarówno callback generatora żądania bezpieczeństwa, jak i callback weryfikujący odpowiedź — osobno dla Network i USB managera. Oba transporty dostają te same funkcje LaserOS.

Szczegółowy zapis analizy znajduje się w:

```text
docs/LaserOS-0.18.1-auth-flow.md
```

## 0.3.2 — rzeczywisty root cause `NO LASER`

Analiza `LaserOS.exe` oraz publicznego `libLaserdockCore` wykazała, że discovery sieciowego LaserCube **nie zaczyna się od `0x77`**.

Rzeczywista sekwencja LaserOS jest następująca:

1. LaserOS wysyła jeden bajt `0x27` (`LASERCUBE_GET_ALIVE`) na broadcast UDP **45456**.
2. Urządzenie musi odpowiedzieć **dokładnie `27 00`**.
3. Dopiero wtedy LaserOS tworzy `LaserdockNetworkDevice` dla adresu nadawcy odpowiedzi.
4. Nowy obiekt wysyła `0x77 GET_FULL_INFO` na UDP **45457**.
5. `0x77` musi zwrócić 64 B, z `byte[1]=00` (success) oraz `byte[2]=00` (payload version 0).

0.3.1 emulował `0x77`, ale nie `0x27`, więc LaserOS nigdy nie tworzył urządzenia i pola Projector Setup pozostawały `?`.

Routing:

```text
0x27 -> UDP 45456
0x77/0x78/0x80/0x82/0x8A/0x8D/0xA0/0xB0/0xB1 -> UDP 45457
0xA9/0x9A -> UDP 45458
```

## EarlyHook dla instalacji użytkownika

Domyślny `config.json` wskazuje wykrytą instalację:

```text
C:\Program Files\LaserOS\LaserOS.exe
```

`start-full.cmd` uruchamia early-hook:

1. jeżeli LaserOS działa, używa ścieżki EXE i wysyła normalne żądanie zamknięcia;
2. uruchamia LaserOS jako `CREATE_SUSPENDED`;
3. wstrzykuje `LaserOSHook.dll`;
4. czeka na pierwszy przebieg patchowania IAT;
5. dopiero wtedy wznawia LaserOS.

Nie jest wykonywany force-kill.

## Oczekiwany log 0.3.3

```text
Cube7 LaserOS Full Bridge 0.3.3
[early] ...
[inject] PID=... OK
[hook] connected
[stage] DISCOVERY_REQUEST ...
[stage] DISCOVERY_ACCEPTED ...
[stage] FULL_INFO_REQUEST ...
[stage] FULL_INFO_ACCEPTED ...
```

Jeżeli LaserOS przejdzie dalej:

```text
[stage] AUTH_REQUEST ...
[stage] AUTH_REQUEST_ACK ...
[stage] AUTH_RESPONSE_QUERY ...
[stage] AUTH_RESPONSE_CAPTURED ...
```

Jeżeli pojawi się `AUTH_RESPONSE_CAPTURED`, ale nie `POST_AUTH_DEVICE_TRAFFIC`, najbardziej prawdopodobnym blokiem jest callback weryfikujący odpowiedź w samym LaserOS.

## Wykryty Laserworld CUBE 7

```text
BLE address: E4:66:E5:D2:6E:38
BLE name:    BLEAPP_C77D_V217
Service:     0000ffe0-0000-1000-8000-00805f9b34fb
FFE1:        Read / WriteWithoutResponse / Write / Notify
FFE2:        WriteWithoutResponse / Write
```

BLE pozostaje w 0.3.3 w trybie READ/NOTIFY. Brak vendor payload writes.

## Safety scope

- fizyczne wyjście lasera pozostaje wyłączone w warstwie Virtual LaserCube;
- `SET_OUTPUT 0x80` zmienia tylko stan wirtualny;
- brak BLE characteristic payload writes;
- brak bypassu interlock/E-stop;
- brak syntetycznego pozytywnego wyniku autoryzacji.

## Uruchomienie

```bat
start-full.cmd
```

Self-test:

```bat
bin\Cube7Bridge.exe --self-test
```

Oczekiwany wynik:

```text
SELFTEST PASS: 0x27 discovery + protocol + passive B0/B1 auth trace + UDP virtual device + BLE target parser; physical-output=DISABLED
```

## Runtime

```text
Cube7-LaserOS-FullBridge-0.3.3-win-x64/
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

Jeżeli LaserOS działa jako administrator, FullBridge powinien zostać uruchomiony z tym samym poziomem integralności. Build docelowy jest x64.
