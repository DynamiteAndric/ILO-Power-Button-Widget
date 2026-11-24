# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 8 WPF desktop widget (Windows only) that shows the state of an HP ProLiant server through its iLO 4 and can power it on or shut it down gracefully. It also hosts the iLO HTML5 remote console in a WebView2 window. Styled as a sci-fi (Halo UNSC) HUD; the font is Bahnschrift, and no game assets are included. `README.md` is the user-facing spec (behavior, HUD messages, settings); `docs/PLAN*.md` are the approved implementation plans.

## Commands

```powershell
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ViewModelTests"                 # one class
dotnet test --filter "FullyQualifiedName~ViewModelTests.Refresh_shows_status_and_errors"  # one test
dotnet run --project src\IloWidget            # --settings <path> uses another settings file, --console opens the console
dotnet publish src\IloWidget -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

`publish\IloWidget.exe` is the build the user actually runs. Publishing fails to overwrite it while the widget is running, so the user must exit it first (tray > Exit). The app is single-instance per settings file (a named mutex derived from the settings path).

There is no linter configured. Tests are xUnit in `tests/IloWidget.Tests` (`UseWPF` is on so controls can be tested).

## Architecture

- **`Services/IloClient.cs`** is the only code that talks to iLO. `IIloClient` (status, thermal, power on, power button press) and `IIloWebSession` (web-UI login and logout for the console) are the seams. Tests replace them with `FakeClient`, or inject an `HttpMessageHandler` (`StubHandler`) into `IloClient`. API discovery tries `/redfish/v1`, then falls back to `/rest/v1`. Each power action has a Redfish body and a legacy body. HTTP 401 and 403 map to `IloErrorKind.Auth` and `IloErrorKind.Forbidden`. Socket and TLS failures map to `Unreachable`, `Tls` or `Certificate`.
- **`ViewModels/MainViewModel.cs`** is the state machine. `HudMapper` turns a `ServerStatus` or an `IloException` into a `HudReadout` (state, label, detail). The view model adds the time-based states on top:
  - After a power-on, `Booting` is held for 20 s even while iLO still reports Off.
  - After a power-button press, `Stopping` lasts up to 3 minutes.
  - Polling is fast (every 3 s) for 5 minutes after a power command.

  Time is injected (`Func<DateTime>`) so tests can control it.
- **Concurrency:** a `SemaphoreSlim` serializes all iLO calls. A poll that finds the gate busy is skipped. A power command waits for an in-flight poll to finish. Polls must **not** change `CanUsePower`: it is bound to `PowerButton.IsEnabled`, and disabling the button cancels a hold in progress. Only a pending power command disables it.
- **`MainWindow.xaml.cs`** owns the `DispatcherTimer` poll loop. It stops the timer around power commands and then uses `NextDelay`. It shows the shutdown confirmation (a hold on a running server only asks; `SHUT DOWN` sends the press). The window has three views (status, settings, display) toggled by `ShowView`.
- **`Controls/PowerButton.cs`**: a custom-drawn control that raises `Activated` only after a 1 s hold (mouse, Space or Enter). Releasing early, losing capture or being disabled cancels the hold.
- **`ConsoleWindow`**:
  - Logs in through `/json/login_session`. That call deliberately sends no Basic auth header: iLO would otherwise tie the session to the header, and the console page would reject it.
  - Hands the session cookie to WebView2.
  - Restyles `irc.html` by injecting CSS and JS (`Theme/ConsoleTheme.cs`).
  - Logs out when the window closes.
- **Settings** (`Services/SettingsStore.cs`) are stored in `%APPDATA%\IloWidget\settings.json`. The password is DPAPI-encrypted for the current user with fixed entropy. `WidgetSettings.Show*` flags choose the info rows. Thermal data is fetched only when a temperature or fan row is shown (`NeedsThermal`).
- **Diagnostic log** (`Services/IloLog.cs`) writes every request and response, failure chains, power decisions and HUD changes to `<settings dir>\logs\ilo-yyyyMMdd.log`, keeping 3 days. It is enabled in `App.OnStartup`; it is off (null directory) in tests. Headers are never logged, and `"password"` values are masked. Ask the user for these logs when debugging behavior against the real iLO.

## Invariants and iLO 4 quirks

- The widget must never force the power off. It only powers on (`ResetType: On`) and makes a momentary press (`PushPowerButton`, or the legacy `PowerButton`/`Press`). The button is inactive during POST.
- iLO 4 closes idle keep-alive connections after a few seconds. .NET retries a GET on such a stale connection but not a POST, so power POSTs failed with "response ended prematurely". Power commands therefore use a fresh connection (`freshConnection: true`), and the pooled idle timeout is 2 s. Do not add automatic retries for power commands: a second button press can undo the first.
- Before sending any power command, re-read the status and do nothing unless the state still allows the command.
- The self-signed certificate is accepted only when the user enables that option, and only for the iLO host.
