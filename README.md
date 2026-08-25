# Cube7 LaserOS Full Bridge 0.2.0 FULLBRIDGE

Windows toolkit do analizy i budowy bridge'a pomiędzy **LaserOS.exe** a **Laserworld CUBE 7**.

## 0.2.0 FULLBRIDGE runtime fix

Release 0.2.0 jest pakowany jako gotowy do uruchomienia layout Windows. Katalog `bin` MUSI zawierać wszystkie cztery pliki runtime: `Cube7Bridge.exe`, `Cube7Injector.exe`, `LaserOSHook.dll` i `config.json`.

Przyczyna błędu z 0.1.0 była konkretna: `native/build-native.ps1` budował `Cube7Injector.exe` i `LaserOSHook.dll` do `bin`, ale następny `dotnet publish -o bin` czyścił/nadpisywał katalog i usuwał pliki natywne. W 0.2.0 kolejność jest odwrócona: najpierw publish .NET, potem build natywny. `verify-release.ps1` oraz CI blokują release, jeśli któregoś z wymaganych plików brakuje.

## Co jest gotowe

- `Cube7Injector.exe` — obserwuje `LaserOS.exe` i ładuje lokalny `LaserOSHook.dll`.
- `LaserOSHook.dll` — IAT hook dla `sendto`, `recvfrom`, `WSASend`, `WSARecv`; nie modyfikuje pakietów.
- `Cube7Bridge.exe` — named-pipe capture, dekoder znanych ramek LaserCube UDP i aktywny skaner BLE/GATT.
- `--mode=full` — trace LaserOS + BLE jednocześnie.
- `--mode=trace` — tylko LaserOS socket trace.
- `--mode=ble` — tylko BLE/GATT.
- capture do `capture.ndjson` + `capture.rawbin`.
- domyślnie **brak BLE write** i brak prób obchodzenia interlock/E-stop.

## Gotowy pakiet runtime

Po buildzie / z GitHub Actions struktura jest taka:

```text
Cube7-LaserOS-FullBridge-0.2.0-win-x64/
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

Przed uruchomieniem można wykonać:

```powershell
.\verify-release.ps1 -BinDir .\bin
```

Prawidłowy wynik kończy się `FULLBRIDGE_READY=1`.

## Ważne ograniczenie

Laserworld CUBE 7 i Wicked Lasers/LaserOS LaserCube to różne urządzenia. Publiczny protokół LaserCube UDP jest używany tutaj tylko do rozpoznawania ruchu LaserOS. Publiczny protokół BLE CUBE 7 nie jest znany, dlatego bridge wykonuje enumerację GATT, READ oraz NOTIFY/INDICATE, ale nie wysyła zgadywanych komend.

Pełny live-output do wejścia **ILDA DB25** wymaga fizycznego DAC — DB25 ILDA jest sygnałem analogowym i sam bridge programowy go nie zastąpi.

## Budowanie na Windows

Wymagania:

- Windows 10/11 x64
- Visual Studio Build Tools z Desktop development with C++
- .NET SDK 8

PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1 -Arch x64 -Publish
```

Wyniki będą w `bin\` i build automatycznie sprawdzi kompletność runtime.

## Uruchomienie

Najprościej:

```text
start-full.cmd
```

albo bezpośrednio:

```powershell
.\bin\Cube7Bridge.exe --mode=full --config=.\bin\config.json
```

Jeżeli LaserOS pracuje jako administrator, bridge/injector również uruchom jako administrator. Bitowość hook DLL musi odpowiadać bitowości LaserOS; domyślnie build jest x64.

## Dane BLE

`BleScanner`:

- aktywnie skanuje reklamy BLE,
- dopasowuje nazwy zawierające `CUBE` / `Laserworld`,
- wylicza wszystkie GATT services,
- wylicza characteristic UUID + properties,
- czyta tylko characteristic z `Read`,
- subskrybuje `Notify` / `Indicate`,
- nie zapisuje do characteristic.

Po pierwszym skanie ustaw dokładny adres w `config.json`, żeby nie połączyć się z innym urządzeniem.

## Auto-hook

```text
Cube7Bridge.exe
  -> Cube7Injector.exe --process LaserOS.exe
      -> LoadLibraryW(LaserOSHook.dll)
          -> IAT: sendto / recvfrom / WSASend / WSARecv
              -> \\.\pipe\Cube7LaserOSBridge
                  -> capture + decode
```

Nie ma persistence, stealth, AV bypass ani modyfikacji plików LaserOS.

## Następny etap po capture

Po uzyskaniu dumpa GATT i logów z oficjalnej aplikacji można uzupełnić `protocols/cube7-ble-template.json` o rzeczywiste UUID, framing, opcode i checksum, a następnie dodać kontrolowany translator LaserOS -> CUBE 7.
