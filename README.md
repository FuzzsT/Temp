# Cube7 LaserOS Full Bridge 0.1.0

Windows toolkit do analizy i budowy bridge'a pomiędzy **LaserOS.exe** a **Laserworld CUBE 7**.

## Co jest gotowe

- `Cube7Injector.exe` — obserwuje `LaserOS.exe` i ładuje lokalny `LaserOSHook.dll`.
- `LaserOSHook.dll` — IAT hook dla `sendto`, `recvfrom`, `WSASend`, `WSARecv`; nie modyfikuje pakietów.
- `Cube7Bridge.exe` — named-pipe capture, dekoder znanych ramek LaserCube UDP i aktywny skaner BLE/GATT.
- `--mode=full` — trace LaserOS + BLE jednocześnie.
- `--mode=trace` — tylko LaserOS socket trace.
- `--mode=ble` — tylko BLE/GATT.
- capture do `capture.ndjson` + `capture.rawbin`.
- domyślnie **brak BLE write** i brak prób obchodzenia interlock/E-stop.

## Ważne ograniczenie

Laserworld CUBE 7 i Wicked Lasers/LaserOS LaserCube to różne urządzenia. Publiczny protokół LaserCube UDP jest używany tutaj tylko do rozpoznawania ruchu LaserOS. Publiczny protokół BLE CUBE 7 nie jest znany, dlatego pierwsza wersja robi enumerację GATT, READ oraz NOTIFY/INDICATE, ale nie wysyła zgadywanych komend.

Pełny live-output do wejścia **ILDA DB25** wymaga fizycznego DAC — DB25 ILDA jest sygnałem analogowym i sam bridge programowy go nie zastąpi.

## Budowanie na Windows

Wymagania:

- Windows 10/11 x64
- Visual Studio 2022 Build Tools: Desktop development with C++
- .NET SDK 8

PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1 -Arch x64 -Publish
```

Wyniki będą w `bin\`.

## Uruchomienie

```powershell
.\bin\Cube7Bridge.exe --mode=full --config=.\bin\config.json
```

lub `start-full.cmd` po zbudowaniu.

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

Hook działa jawnie i lokalnie:

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
