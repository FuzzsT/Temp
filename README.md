# Cube7 LaserOS Full Bridge 0.3.2 — REAL 0x27 DISCOVERY

Windows x64 bridge do diagnostyki **LaserOS.exe** oraz **Laserworld CUBE 7**.

## 0.3.2 — rzeczywisty root cause `NO LASER`

Analiza `LaserOS.exe` oraz publicznego `libLaserdockCore` wykazała, że discovery sieciowego LaserCube **nie zaczyna się od `0x77`**.

Rzeczywista sekwencja LaserOS jest następująca:

1. LaserOS wysyła jeden bajt `0x27` (`LASERCUBE_GET_ALIVE`) na broadcast UDP **45456**.
2. Urządzenie musi odpowiedzieć **dokładnie `27 00`**.
3. Dopiero wtedy LaserOS tworzy `LaserdockNetworkDevice` dla adresu nadawcy odpowiedzi.
4. Nowy obiekt wysyła `0x77 GET_FULL_INFO` na UDP **45457**.
5. `0x77` musi zwrócić 64 B, z `byte[1]=00` (success) oraz `byte[2]=00` (payload version 0).

0.3.1 emulował `0x77`, ale nie `0x27`, więc LaserOS nigdy nie tworzył urządzenia i pola Projector Setup pozostawały `?`.

0.3.2 dodaje `0x27 -> 27 00` zarówno do implementacji .NET, jak i natywnego respondera w `LaserOSHook.dll`, oraz poprawia routing portów:

```text
0x27 -> UDP 45456
0x77/0x78/0x80/0x8A -> UDP 45457
0xA9 -> UDP 45458
```

## EarlyHook dla instalacji użytkownika

Domyślny `config.json` wskazuje wykrytą instalację:

```text
C:\Program Files\LaserOS\LaserOS.exe
```

`start-full.cmd` uruchamia early-hook:

1. jeżeli LaserOS działa, odczytuje/wykorzystuje ścieżkę EXE i wysyła normalne żądanie zamknięcia;
2. uruchamia LaserOS jako `CREATE_SUSPENDED`;
3. wstrzykuje `LaserOSHook.dll`;
4. czeka na pierwszy przebieg patchowania IAT;
5. dopiero wtedy wznawia LaserOS.

Nie jest wykonywany force-kill.

## Oczekiwany log 0.3.2

Najważniejsze linie po uruchomieniu `start-full.cmd`:

```text
Cube7 LaserOS Full Bridge 0.3.2
[early] ...
[inject] PID=... OK
[early] resumed PID=... after hook injection
[hook] connected
[TX] ...:45456 1B GET_ALIVE
[RX] ... 2B GET_ALIVE RESPONSE
[TX] ...:45457 1B GET_FULL_INFO
```

Jeżeli po `GET_FULL_INFO` pojawi się:

```text
SECURITY_REQUEST
```

czyli opcode `0xB0`, discovery jest już naprawione, a następnym etapem do analizy jest autoryzacja LaserCube (`0xB0/0xB1`). 0.3.2 **nie fałszuje jeszcze odpowiedzi bezpieczeństwa** — loguje te opcode'y, aby można było odtworzyć prawidłową sekwencję na podstawie realnego ruchu.

## Wykryty Laserworld CUBE 7

```text
BLE address: E4:66:E5:D2:6E:38
BLE name:    BLEAPP_C77D_V217
Service:     0000ffe0-0000-1000-8000-00805f9b34fb
FFE1:        Read / WriteWithoutResponse / Write / Notify
FFE2:        WriteWithoutResponse / Write
```

BLE pozostaje w 0.3.2 w trybie READ/NOTIFY. Brak vendor payload writes.

## Safety scope

- fizyczne wyjście lasera pozostaje wyłączone w warstwie Virtual LaserCube;
- `SET_OUTPUT 0x80` zmienia tylko stan wirtualny;
- brak BLE characteristic payload writes;
- brak bypassu interlock/E-stop.

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
SELFTEST PASS: 0x27 discovery + protocol + UDP virtual device + BLE target parser; physical-output=DISABLED
```

## Runtime

```text
Cube7-LaserOS-FullBridge-0.3.2-win-x64/
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
