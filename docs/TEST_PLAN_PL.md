# Plan testu CUBE 7 / LaserOS

1. Zostaw fizyczny interlock / E-stop aktywny, a emisję wyłączoną.
2. Uruchom `Cube7Bridge.exe --mode=ble` i zanotuj nazwę, adres, UUID usług i charakterystyk.
3. Uruchom `Cube7Bridge.exe --mode=trace` przed LaserOS.
4. Uruchom LaserOS. Injector automatycznie podłączy `LaserOSHook.dll` do procesu `LaserOS.exe`.
5. W LaserOS wykonaj kolejno tylko zmiany programowe bez ekspozycji wiązki: connect/disconnect, wybór urządzenia, zmiana parametrów podglądu.
6. Sprawdź `captures/YYYYMMDD-HHMMSS/capture.ndjson` oraz `capture.rawbin`.
7. Dla BLE uruchom oficjalną aplikację CUBE i wykonuj po jednej zmianie: preset, kolor, speed, pozycja, mode. Porównuj notification/read values.
8. Nie włączaj automatycznych zapisów do nieznanych charakterystyk. `allowBleWrites=false` jest celowe.

## Oczekiwany rezultat

- identyfikacja LaserOS transportu: `sendto` / `WSASend`, porty, opcodes;
- pełny dump GATT CUBE 7;
- korelacja zmian w oficjalnej aplikacji z notification/read;
- dopiero po potwierdzeniu UUID + framing można dodać bezpieczny writer/translator.
