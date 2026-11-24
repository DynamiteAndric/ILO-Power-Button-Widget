# iLO 4 server status desktop widget (Halo UNSC HUD theme)

## Context

`D:\Project\ILO` is empty. The goal is a small Windows 11 desktop widget that shows the state of an HP ProLiant server through its iLO 4, lets the user enter the iLO address and credentials inside the widget, and has one big power-on button. Decisions already made with the user:

- **Desktop widget** (frameless window on the desktop), not a Widgets Board (Win+W) widget.
- **UNSC HUD blue** Halo look.

The iLO protocol is already proven in `C:\Users\jpast\.claude\skills\ilo-server-power\scripts\ilo.py`; the widget ports that logic to C# rather than inventing it. Like the skill, the widget can only power **on**, never off.

## Stack

.NET 8 WPF (`net8.0-windows`, SDK 8.0.101 is installed). One NuGet dependency: `System.Security.Cryptography.ProtectedData` (DPAPI). Tray icon via built-in WinForms `NotifyIcon` (`UseWindowsForms=true`). Tests in xUnit.

## Layout

```
D:\Project\ILO\
  IloWidget.sln
  src\IloWidget\
    IloWidget.csproj
    App.xaml(.cs)              startup, tray icon, single-instance mutex
    MainWindow.xaml(.cs)       the widget: status view + settings view
    Theme\Halo.xaml            colors, brushes, control styles, animations
    Controls\PowerButton.cs    hold-to-activate round button
    Services\IloClient.cs      iLO REST/Redfish calls
    Services\SettingsStore.cs  load/save settings, DPAPI password
    Services\Autostart.cs      HKCU Run key toggle
    ViewModels\MainViewModel.cs  polling loop, state machine, commands
    Models\ServerStatus.cs, WidgetSettings.cs
  tests\IloWidget.Tests\       IloClient + SettingsStore + state mapping tests
  README.md
```

## iLO client (port of `ilo.py`)

- `HttpClient` over `SocketsHttpHandler` with `UseProxy=false`, 15 s timeout, Basic auth header.
- API detection: `GET /redfish/v1/Systems/1/`, fall back to `/rest/v1/Systems/1/`; cache the base per host.
- Status: `Power` or `PowerState`, `Model`, `SerialNumber`, `Status.Health`, `Oem.Hp.PostState`, `BiosVersion`.
- Power on: Redfish `POST /redfish/v1/Systems/1/Actions/ComputerSystem.Reset/` `{"ResetType":"On"}`; legacy `POST /rest/v1/Systems/1` `{"Action":"Reset","ResetType":"On"}`.
- TLS: certificate validated by default; a settings toggle "Accept self-signed certificate" disables validation for that host only (iLO ships self-signed).
- Typed errors mapped to HUD messages: 401 → `ACCESS DENIED`, 403 → `INSUFFICIENT PRIVILEGE`, cert failure → `CERT REJECTED`, connect/timeout → `NO LINK`, no API → `FIRMWARE UNSUPPORTED`.

## Settings

- Stored in `%APPDATA%\IloWidget\settings.json`: host, user, password (DPAPI `CurrentUser`-encrypted, base64), accept-self-signed, poll interval (default 10 s), always-on-top, window position.
- Edited in the widget itself: gear icon flips the panel to a settings view (host, user, `PasswordBox`, toggles, `TEST LINK` and `SAVE` buttons). First run with no config opens it automatically.
- The password is never written to logs or shown after saving.

## Widget behavior

- Frameless, transparent, ~320×420, drag anywhere to move, not in the taskbar, position remembered. Right-click menu and tray icon: Settings, Always on top, Start with Windows, Refresh, Exit.
- Poll every 10 s; every 3 s for 5 minutes after a power-on until `FinishedPost`.
- States shown:

| iLO data | HUD label | Color |
|---|---|---|
| not configured | `AWAITING CONFIG` | dim blue |
| Off | `OFFLINE` | amber |
| On + `InPost*` | `BOOT SEQUENCE` | pulsing cyan |
| On + `FinishedPost` (or null) | `ONLINE` | bright cyan |
| error | message from the table above | red |

- Also shows model, host, health (highlighted when not `OK`), BIOS version, last-contact time.
- **Power button**: large round button in the center. Enabled only when the server is Off. Press and hold ~1 s (ring charges around it) to send power-on, so a stray click cannot start hardware. When the server is on it becomes a lit, non-interactive indicator.

## Halo UNSC HUD theme (`Theme\Halo.xaml`)

- Translucent dark navy panel (`#CC061018`) with chamfered corners drawn as a `Path`, thin cyan (`#4FD8FF`) outline with outer glow (`DropShadowEffect`), faint scanline/hex overlay.
- Uppercase letter-spaced text in **Bahnschrift** (ships with Windows; no Halo fonts or game assets are bundled).
- Segmented "shield bar" across the top reflecting state (full/cyan online, filling during boot, empty/amber offline, flashing red on error).
- Power button: concentric rings, glowing power glyph, charge arc animation while held, pulse on success.
- Styled TextBox/PasswordBox/CheckBox/Button/ContextMenu to match.

## Steps

1. Scaffold solution, WPF project, test project; `git init` with a `.gitignore`.
2. `IloClient` + models, with unit tests using a stub `HttpMessageHandler` (Redfish path, legacy fallback, 401/403, power-on request bodies).
3. `SettingsStore` (DPAPI round-trip test) and `Autostart`.
4. `MainViewModel`: polling, state mapping (tested), commands.
5. `Halo.xaml` theme, `PowerButton`, `MainWindow` status and settings views.
6. Tray icon, context menu, single instance, position persistence.
7. README (build, run, publish, iLO user privilege "Virtual Power and Reset").

## Verification

- `dotnet build` and `dotnet test` pass.
- Run against a local fake iLO (small script in the scratchpad serving `/redfish/v1/Systems/1/` with switchable Off → InPost → FinishedPost and a 401 mode) and screenshot each HUD state, including the hold-to-power-on flow.
- Real test: you enter the iLO address and credentials in the widget's settings panel (I never see the password); confirm status matches the iLO web UI. Powering on the real server happens only when you press the button yourself.
- `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true` yields a single `IloWidget.exe`.

## Known risk

Very old iLO 4 firmware offers only TLS 1.0/1.1, which Windows 11 disables by default. If the link fails for that reason the widget will show `NO LINK` with a TLS hint; the fix is an iLO firmware update (2.30+ also enables Redfish).
