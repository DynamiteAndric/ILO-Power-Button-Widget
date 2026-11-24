# iLO Widget: built-in HTML5 remote console

## Context

The widget (`D:\Project\ILO`, .NET 8 WPF) shows server status and powers the server on. The user wants to open the iLO 4 HTML5 remote desktop from it. Chosen approach: a **built-in console window** (WebView2), not the default browser.

Findings from read-only probes of the user's iLO (192.168.100.49, ProLiant MicroServer Gen8):

- Firmware **2.82**; the standalone HTML5 console page is `https://<ilo>/irc.html` (HTTP 200). `/html/irc.html` does not exist.
- `irc.html` authenticates through the web-UI session: it reads the `sessionKey` cookie (`iLO.getSessionKey()`), or a `?token=` query value.
- A web session is created with `POST /json/login_session` `{"method":"login","user_login":…,"password":…}` → `session_key`; ended with `{"method":"logout","session_key":…}`.

So the widget can log in with the stored credentials, hand the session cookie to an embedded browser and show the console with no login page.

## Design

### 1. Web session in `src/IloWidget/Services/IloClient.cs`
- Add `Task<string> OpenWebSessionAsync(ct)` and `Task CloseWebSessionAsync(string key, ct)` (new small interface `IIloWebSession`, implemented by `IloClient`). Reuse the existing `SendAsync`, `ThrowForAuth` and `Translate` error mapping; missing `session_key` → `IloException(Protocol)`.
- Static helper `ConsoleUri(host)` → `https://{NormalizeHost(host)}/irc.html`.

### 2. Console window: `src/IloWidget/ConsoleWindow.xaml(.cs)` (new)
- NuGet `Microsoft.Web.WebView2`. WebView2 cannot render in a transparent layered window, so this window is **opaque**: `WindowChrome` with a custom Halo title bar (`UNSC // REMOTE CONSOLE - <host>`, reconnect and close buttons, cyan outline) styled from `Theme/Halo.xaml`; resizable, default 1100×760, shown in the taskbar.
- Start-up sequence:
  1. `CoreWebView2Environment.CreateAsync(null, %LOCALAPPDATA%\IloWidget\WebView2)`; `EnsureCoreWebView2Async`.
  2. `OpenWebSessionAsync()` using `SettingsStore.Unprotect` (same as the status client).
  3. `CookieManager.AddOrUpdateCookie(CreateCookie("sessionKey", key, host, "/"))` with `IsSecure = true`.
  4. Navigate to `ConsoleUri`.
- TLS: `ServerCertificateErrorDetected` → `AlwaysAllow` **only** when the "Accept self-signed certificate" setting is on and the request host is the iLO host; otherwise the default error page.
- Containment: cancel `NavigationStarting` and `NewWindowRequested` for any host other than the iLO; disable dev tools, default context menus and password autosave.
- On close: `CloseWebSessionAsync` (best effort, 3 s timeout), delete the cookie, dispose WebView2. iLO has a limited number of sessions, so logging out matters.
- Errors (login refused, unreachable, WebView2 runtime missing) are shown in the window in HUD style using `HudMapper.Map(IloException)`; no crash.

### 3. Entry points
- `MainWindow.xaml`: a `REMOTE CONSOLE` `HudButton` under the power hint (window height +38), plus a context-menu item; `App.xaml.cs`: a tray-menu item.
- `MainViewModel`: `CanOpenConsole` = configured and last poll was not an error.
- Only one console window at a time; a second click activates the existing one. Changing settings closes it.

### 4. Packaging and docs
- `IloWidget.csproj`: add the package and `IncludeNativeLibrariesForSelfExtract` so the single-file publish still yields one exe (WebView2Loader.dll is native).
- README: console section, requirements, limitations. Copy this plan to `docs/PLAN-remote-console.md` (same tracking convention as `docs/PLAN.md`).

## Files

- Modify: `Services/IloClient.cs`, `ViewModels/MainViewModel.cs`, `MainWindow.xaml(.cs)`, `App.xaml.cs`, `Theme/Halo.xaml` (title-bar styles), `IloWidget.csproj`, `README.md`
- New: `ConsoleWindow.xaml`, `ConsoleWindow.xaml.cs`, `tests/IloWidget.Tests/WebSessionTests.cs`, `docs/PLAN-remote-console.md`

## Verification

- `dotnet build`, `dotnet test`: new tests cover login/logout request bodies, missing `session_key`, 401 mapping, `ConsoleUri`, `CanOpenConsole`.
- Fake iLO (scratchpad script, self-signed TLS) extended with `/json/login_session` and a dummy `/irc.html` that prints whether it received the `sessionKey` cookie: confirms login → cookie injection → certificate handling → logout on close, with a screenshot of the console window.
- Real iLO: you click `REMOTE CONSOLE` yourself; expected result is the server screen with keyboard and mouse working. I cannot run this step without your credentials.
- `dotnet publish … -p:PublishSingleFile=true` still produces a single working `IloWidget.exe`.

## Things you should know

- **License:** on iLO 4 the remote console after POST normally needs an iLO Advanced license. Without it the console works during boot and then iLO shows a license message. This cannot be worked around by the widget.
- **Privilege:** the iLO user needs "Remote Console" in addition to "Virtual Power and Reset".
- **Power-off becomes reachable:** HPE's console page has its own power menu (press, hold, reset, cold boot). The widget's own button stays power-on only, but inside the console window you can shut the server down.
- **Unverified until run:** that the injected `sessionKey` cookie alone is enough for `irc.html` on 2.82. Fallback if not: navigate to `irc.html?token=<key>`, which the page explicitly supports.
