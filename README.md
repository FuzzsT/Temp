# Cube7 LaserOS Full Bridge 0.6.1 — BLE-SESSION-BRIDGE

Windows x64 bridge do testowania **LaserOS.exe** z realnym serwerem LaserCube UDP na localhost, early DLL injection oraz potwierdzoną z `321.zip` sesją BLE CUBE 7. Fizyczna emisja lasera pozostaje wyłączona.

## Co zmienia 0.6.1

0.6.1 zachowuje architekturę 0.6.0 `LOOPBACK-AUTOCONFIG`, ale zastępuje pasywny BLE inventory właściwym, źródłowo potwierdzonym transportem/session handshake CUBE:

- realny `Cube7Bridge.exe` nasłuchuje na `127.0.0.1:45456/45457/45458`;
- `LaserOSHook.dll` przepisuje destination LaserCube na loopback i remapuje kolizyjne bindy klienta na port ephemeral;
- LaserOS jest uruchamiany przez early suspended-launch injection;
- BLE nie wykonuje Windows Pair i nie wywołuje API pair/unpair;
- po połączeniu używane są wyłącznie uncached GATT services/characteristics;
- Device Name `2A00` musi być dokładnie `BLEAPP_C77D_V217`;
- wymagany jest profil `FFE0` z `FFE1` (Read/Notify/Write) i `FFE2` (Write), ale oficjalny session command channel to **FFE1**;
- po 1500 ms settle uruchamiane są FFE1 notifications, po kolejnych 200 ms wysyłany jest wyłącznie connect handshake `0xAB` przez `WriteWithResponse`;
- bridge czeka maksymalnie 6000 ms na zaszyfrowaną odpowiedź `0x8B`, parsuje firmware, `bufferMax`, `dataFormatType`, `activateType` i parametry sesji;
- klucze sesyjne są wyprowadzane zgodnie z potwierdzonym AES-CTR, ale nie są zapisywane do logów ani `ble-session-summary.json`;
- authentication LaserOS `0xB0/0xB1` nadal pozostaje **trace-only** i nie jest fałszowane.

## Twarde invariants bezpieczeństwa

Domyślna konfiguracja wymusza:

```text
physicalOutput=false
allowBleWrites=false
allowSyntheticAuthentication=false
interlockBypass=false
```

`allowBleWrites=false` oznacza brak dowolnych komend sterujących BLE. 0.6.1 ma jedną wąską, stałą operację sesyjną: potwierdzony connect `0xAB` na FFE1 z `WriteWithResponse`. Nie wysyła `output on`, ustawień mocy, parametrów pracy ani komend FFE2. Nie omija aktywacji, interlocka ani E-stop.

Jeżeli odpowiedź BLE zgłasza `activateType != 1`, sesja jest odrzucana. Bridge nie próbuje zmieniać ani obchodzić stanu aktywacji.

## Zweryfikowany LaserOS

```text
LaserOS x64 v0.18.1 BETA
C:\Program Files\LaserOS\LaserOS.exe
SHA-256:
21799b2b9c651be87d69a4d977fa09ef14b8ce22de13499a7974669c36991c0c
```

Jeżeli SHA-256 nie pasuje, injection jest blokowane przez preflight.

## Loopback autoconfig LaserOS

Domyślna sieć:

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

Profil runtime przekazywany do DLL:

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

Kolejność:

```text
Cube7Bridge.exe
  1. preflight SHA LaserOS
  2. bind real UDP server 127.0.0.1:45456/45457/45458
  3. write loopback injection profile
  4. CREATE_SUSPENDED LaserOS.exe
  5. inject LaserOSHook.dll
  6. hook settle barrier
  7. ResumeThread LaserOS.exe
```

LaserOS wysyłający ruch na canonical porty LaserCube jest kierowany przez hook do realnego serwera localhost. Lokalny `bind()` klienta na `45456/45457/45458` jest zamieniany na port `0`, więc Windows przydziela port ephemeral i nie dochodzi do kolizji z serwerem.

## Network discovery i pozostały etap auth

```text
LaserOS -> UDP 45456: 27
Server  ->             27 00

LaserOS -> UDP 45457: 77
Server  ->             64-byte FULL_INFO
```

Serwer obsługuje także `0x78`, `0x80`, `0x8A` i `0xA9`. `0x80` zmienia wyłącznie stan wirtualny; `physicalOutputEnabled` pozostaje zawsze `false`.

Publiczny `libLaserdockCore` przenosi urządzenie z listy inicjalizacyjnej do aktywnej dopiero po ukończeniu własnej sekwencji security/auth. Dlatego `NO LASER` może nadal pozostać, jeżeli LaserOS dochodzi do `0xB0/0xB1`, ale nie otrzymuje prawidłowej odpowiedzi security. 0.6.1 celowo nie generuje sztucznego `AUTH OK`.

## Oficjalna sesja BLE z 321.zip

Target:

```text
Address: E4:66:E5:D2:6E:38
Name:    BLEAPP_C77D_V217
Service: 0000ffe0-0000-1000-8000-00805f9b34fb
FFE1:    Read / WriteWithoutResponse / Write / Notify
FFE2:    WriteWithoutResponse / Write
```

Sekwencja runtime:

```text
CONNECT_UNPAIRED
SETTLE_1500MS
READ_DEVICE_NAME_UNCACHED
VALIDATE_GATT_UNCACHED
SUBSCRIBE_FFE1_NOTIFY
SETTLE_200MS
WRITE_FFE1_WITH_RESPONSE_0xAB
WAIT_0x8B_6000MS
BLE_SESSION READY
```

Handshake używa AES-128 CTR zgodnego z działającym projektem `321.zip`. Po poprawnym `0x8B` bridge waliduje m.in.:

```text
status == 0
appCompany == CubeLaserTemeiAI
activateType == 1
47 < bufferMax <= 512
dataFormatType in 0..4
```

Publiczne parametry są zapisywane do:

```text
captures/<timestamp>/ble-session-summary.json
```

Plik nie zawiera `deviceKey`, `deviceSecret`, `productKey`, klucza AES ani IV.

## Oczekiwany status po uruchomieniu

```text
LASEROS_PREFLIGHT    OK
LOOPBACK_SERVER      OK
LASEROS_HOOK         OK
BLE_TRANSPORT        OK
BLE_SESSION          READY  fwCPU=... bufferMax=... dataFormat=... activate=1
BLE_PROTOCOL         OFFICIAL-SESSION
DISCOVERY_0x27       OK
FULL_INFO_0x77       OK
AUTH_B0_B1           WAITING / ACTIVE
PHYSICAL_OUTPUT      DISABLED
```

Jeżeli `BLE_SESSION=READY`, ale LaserOS nadal pokazuje `NO LASER`, najważniejszy jest wtedy stan `DISCOVERY_0x27`, `FULL_INFO_0x77` i `AUTH_B0_B1`. To rozdziela problem fizycznego transportu CUBE od wewnętrznej inicjalizacji urządzenia sieciowego w LaserOS.

## Renderer i diagnostyka

Renderer tap nadal działa niezależnie od fizycznego sprzętu. Przechwycone punkty są normalizowane diagnostycznie do:

```text
X,Y   = 0..4095
R,G,B = 0..4095
```

Typowe pliki:

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
  ble-session-summary.json
  support-bundle.zip
```

## Uruchomienie

Rozpakuj release do pustego katalogu i uruchom:

```bat
start-full.cmd
```

Dostępne tryby:

```bat
start-full.cmd
start-trace.cmd
start-ble.cmd
```

`start-ble.cmd` pozwala osobno zweryfikować `BLE_TRANSPORT` i `BLE_SESSION` bez uruchamiania LaserOS.

## CI / self-test

Windows CI sprawdza:

- realny localhost UDP round-trip i injected loopback routing;
- destination rewrite i client-bind collision avoidance;
- `0x27/0x77/0x78/0x80/0x8A/0xA9` virtual network protocol;
- deterministyczny wektor `0xAB` z `321.zip`;
- zgodność AES-CTR encrypt/decrypt;
- parser i walidację `0x8B`;
- wyprowadzenie session key/IV;
- politykę `unpaired + uncached + FFE1 write/notify`;
- dokładną sekwencję runtime i redakcję sekretów;
- wymagane pliki PE, bezpieczne ustawienia i zawartość finalnego ZIP-a.
