# iLO Widget

A small Windows 11 desktop widget, styled as a Halo UNSC HUD, that shows the state of an
HP ProLiant server through its iLO 4 and can power the server on and shut it down gracefully.

- Status from iLO: offline, boot sequence (POST), online, shutting down, plus a configurable list of info rows.
- Big power button. **Press and hold it for one second**; a short click does nothing.
  - Server off: the hold powers it on. The widget shows `BOOT SEQUENCE` for at least 20 s, even if iLO still says off.
  - Server online: the hold asks `CONFIRM SHUTDOWN?`. Only `SHUT DOWN` sends anything: a momentary press of the
    power button, so the operating system shuts down gracefully. The widget shows `SHUTTING DOWN` until iLO reports
    the power off; if the OS ignores the press, it returns to `ONLINE` after three minutes.
  - The hold works with the mouse, or with Space or Enter. What it does is decided when the hold starts; if a status
    check changes the state during the hold, the hold is ignored.
  - The widget never forces the power off. The button is inactive during POST, while a command is being sent, and
    whenever the status shows an error.
  - If iLO does not answer after the command was sent, the widget shows `SHUTDOWN SENT?` or `POWER-ON SENT?`
    (`NO REPLY FROM ILO - CHECKING STATE`) and keeps the button inactive instead of inviting a second press, which could
    undo the first. If the command provably never left the PC, it shows `COMMAND FAILED` and you can try again.
  - Power commands are never retried automatically, and the state is re-read right before sending; nothing is sent
    unless it still allows the command.
- **Remote console**: the `REMOTE CONSOLE` button at the bottom opens the iLO HTML5 remote desktop in its own window, already logged in.
  It is available once iLO has answered (server off, booting, online or shutting down).
- **Display**: right-click > Display chooses which info rows are shown; the widget shrinks or grows to fit.
- iLO address and credentials are set inside the widget (gear icon, or right-click > Settings).
- Right-click menu and tray icon: refresh, settings, display, always on top, start with Windows, hide to tray, exit.
  Drag the widget anywhere; its position is remembered (a saved position that is no longer on any monitor is ignored,
  and the height follows the content within the current monitor).
  Only one widget runs per settings file.

## Requirements

- Windows 10/11 with the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (the SDK to build).
- iLO 4 reachable from this PC. The widget tries Redfish (`/redfish/v1`) first and falls back to `/rest/v1` when
  Redfish is not there (a redirect, 400, 404, 405 or 501). A 5xx answer does not switch API. Redirects are never followed.
- An iLO user with the **Virtual Power and Reset** privilege.

## Build, test, run

```powershell
dotnet build
dotnet test
dotnet run --project src\IloWidget
```

Single-file executable:

```powershell
dotnet publish src\IloWidget -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
.\publish\IloWidget.exe
```

## Remote console

`REMOTE CONSOLE` (also in the right-click and tray menus) opens the iLO HTML5 console page (`https://<ilo>/irc.html`)
in a built-in window. The widget logs in to the iLO web interface with the stored credentials, hands the session
to the embedded browser and logs out again when the window is closed. `IloWidget.exe --console` opens it after the first status check that allows it.
Closing the window, exiting the widget (also Alt+F4) or closing it while it is still logging in logs the session out.

- Needs iLO 4 firmware 2.70 or newer and the Microsoft Edge WebView2 Runtime (part of Windows 11).
- The iLO user needs the **Remote Console** privilege.
- After POST, iLO 4 normally requires an **iLO Advanced license** for the remote console; without one iLO shows
  a license message once the operating system starts. The widget cannot change that.
- HPE's console page has its own power menu (press, hold, reset, cold boot). The widget's power button can only
  power on and shut down gracefully, but from inside the console window the server can be forced off or reset.
- The console page's toolbar, menus, dialogs and status bar are restyled to the Halo theme by a stylesheet injected in the
  widget's window (`Theme/ConsoleTheme.cs`); nothing on the iLO is changed. Its full-screen and close icons are hidden
  because the window has its own.
- The self-signed certificate is accepted in the console window only if that option is enabled in the settings,
  and only for the iLO address. The window does not navigate to any other site. If the certificate is rejected, the
  window shows `CERT REJECTED` with the hint to allow the self-signed certificate, and a `RETRY` button.

## Settings

| Field | Notes |
|---|---|
| iLO address | IP or host name, e.g. `192.168.1.50`. Add `:port` only if your iLO does not use 443. |
| User / password | The password is encrypted with Windows DPAPI for your Windows account and is never stored in clear text. Leave the box empty to keep the stored one; this works only while the iLO address and user are unchanged (the label reads `PASSWORD (STORED - LEAVE EMPTY TO KEEP)`, otherwise `PASSWORD (REQUIRED FOR A NEW HOST OR USER)`). A settings file copied from another Windows account cannot be decrypted: the label reads `PASSWORD (UNREADABLE - ENTER AGAIN)`. |
| Poll interval | Seconds between status checks (3 to 3600, default 10). For five minutes after a power command the widget polls every 3 s (unless the server is online). |
| Accept self-signed certificate | iLO ships with a self-signed certificate. Turn this on only on a network you trust; otherwise install the iLO certificate into the Windows trust store. It is accepted only for the iLO address. |

The test button tries the typed values without saving. Saving is refused while a power command is in progress. Saving only
the poll interval, always-on-top or the shown rows keeps the link, the HUD state and an open console; changing the
address, user, password or certificate option closes the console and reconnects.

Settings live in `%APPDATA%\IloWidget\settings.json`. `IloWidget.exe --settings <path>` uses a different file
(a second widget for a second server); the path is made absolute, and each settings file has its own single instance
and its own "start with Windows" entry. The file is written atomically. If it exists but cannot be read (locked) or is
corrupt, the widget does not overwrite it by itself (dragging the widget or toggling always-on-top is not saved) until
you save from the settings panel; a corrupt file is first copied to `settings.json.bad`.

## Display

Right-click > Display (or the tray menu) lists the info rows; tick the ones the widget should show.

| Row | Source |
|---|---|
| Model, health, BIOS, POST state, host name, CPU, memory | The status request the widget makes anyway (`Systems/1`). Host name is the OS name reported to iLO and can be empty. |
| iLO address, last contact | The widget itself. |
| Temperature, fans | `Chassis/1/Thermal`: inlet (ambient) and hottest CPU temperature, fastest fan in percent. One extra iLO request per poll, made only while one of these rows is shown. |

Model, iLO address, health, BIOS and last contact are on by default.

## Diagnostic log

Every iLO request and response (method, path, body, HTTP status, time taken, and on failure the full exception chain)
is written to `%APPDATA%\IloWidget\logs\ilo-yyyyMMdd.log` (next to the settings file), together with the widget's
power decisions and HUD changes. Headers are not logged, and passwords and session keys are masked. Files older than three
days are deleted (at start-up and when the day changes). Two widgets can share the same log folder. Tray menu > Open log folder.

## HUD messages

| Message | Meaning |
|---|---|
| `ACCESS DENIED` | `CHECK USER NAME AND PASSWORD`: wrong user name or password (HTTP 401). `ACCOUNT DISABLED OR LACKS LOGIN PRIVILEGE`: iLO refused a status read (HTTP 403). |
| `INSUFFICIENT PRIVILEGE` | The iLO user lacks Virtual Power and Reset (HTTP 403 on a power command). |
| `CERT REJECTED` | The iLO certificate is not trusted; see the self-signed option. |
| `NO LINK` | iLO unreachable (network, VPN, wrong address), or the TLS handshake failed. Very old iLO 4 firmware only speaks TLS 1.0/1.1, which Windows 11 disables; update the iLO firmware. |
| `FIRMWARE UNSUPPORTED` | Neither `/redfish/v1` nor `/rest/v1` answered. |
| `BAD ADDRESS` | The iLO address in the settings is not valid. |
| `PASSWORD UNREADABLE` | The stored password cannot be decrypted (for example settings copied from another Windows account); enter it again in the settings. |
| `LINK ERROR` | iLO answered with something the widget cannot read; the detail line shows the reason. |
| `BOOT SEQUENCE` | The server is on but iLO does not report a finished POST (`POST IN PROGRESS`, or `SYSTEM STARTING` for any other POST state). The power button is inactive. `ONLINE` is shown only after POST has finished. |
| `COMMAND FAILED` | A power command was not delivered (iLO unreachable before sending); try again. |
| `SHUTDOWN SENT?` / `POWER-ON SENT?` | The command was sent but iLO did not answer; the widget keeps checking the state and the button stays inactive. |

A single failed status check (unreachable or unreadable answer) does not change the display: the detail line shows
`LINK UNSTABLE - RETRYING` and the next check follows after 3 s. A second failure in a row shows the error. Wrong
credentials, certificate and address errors show at once.

## Layout

- `src/IloWidget/Services/IloClient.cs` - iLO REST/Redfish calls
- `src/IloWidget/Services/ConsoleSession.cs` - remote console login and logout
- `src/IloWidget/Services/` - also settings (`SettingsStore.cs`), diagnostic log (`IloLog.cs`), start with Windows (`Autostart.cs`)
- `src/IloWidget/ConsoleWindow.xaml(.cs)` - remote console window (WebView2)
- `src/IloWidget/ViewModels/` - polling state and HUD text mapping
- `src/IloWidget/Controls/` - hold-to-activate power button, shield bar
- `src/IloWidget/Theme/Halo.xaml` - colors and control styles
- `tests/IloWidget.Tests/` - unit tests (client, web session, view model, settings, power button)
- `docs/PLAN.md`, `docs/PLAN-remote-console.md`, `docs/PLAN-display-config.md` - the implementation plans

The look is inspired by sci-fi HUDs; no game assets or fonts are included (the font is Bahnschrift, which ships with Windows).
