# Dream Player — Windows frontend

Нативный интерфейс (C#, WinUI 3, unpackaged) поверх ядра
[`rs_dream_player`](../rs_dream_player) через его C ABI (`dream_player.dll`).

## Сборка (Windows)

1. Ядро (рядом, `..\rs_dream_player`):
   - на Windows: `cargo build --release` → `target\release\dream_player.dll`;
   - или кросс-сборка с Linux: `cargo build --release --target x86_64-pc-windows-gnu`.
2. Фронтенд: `dotnet build DreamPlayer -c Release` (или открыть в Visual Studio 2022
   с workload «WinUI application development»). DLL ядра копируется к exe
   автоматически; другой путь — `-p:CoreDll=path\to\dream_player.dll`.

Требуется .NET 9 SDK, Windows 10 1809+.

## Структура

- `Core/` — P/Invoke (`Native.cs`, 1:1 с `api/src/*.rs`), логируемая обёртка `CoreApi`, модели.
- `Services/` — плеер (очередь, shuffle/repeat, автопереход по колбэку ядра), библиотека,
  импорт/экспорт, лог взаимодействия с ядром, SMTC, настройки.
- `Views/` — оболочка (вкладки, меню, нижняя панель) и вкладки.
- `L.cs` — строки RU/EN.

Данные: `%LOCALAPPDATA%\DreamPlayer` (`data` — база и конфиг ядра, `logs\frontend.log`,
`frontend.json` — настройки фронтенда).
