# Plan: configurable widget display + Remote Console button at the bottom

## Context

The widget always shows the same five info rows (MODEL, ILO ADDRESS, HEALTH, BIOS, LAST CONTACT) in a fixed 330x512 window, with the REMOTE CONSOLE button sitting between the power button and the rows. You want to:

1. Choose which rows the widget shows — each row individually.
2. Get new optional rows: **HOST NAME, CPU, MEMORY, POST STATE, TEMPERATURE, FANS**.
3. Have the widget **shrink to fit** when rows are hidden.
4. Move the **REMOTE CONSOLE** button to the very bottom of the widget (below the info rows).
5. Let the power button **gracefully shut the server down** when it is on — only after an explicit confirmation.

Not in scope (you did not pick them): hiding the power button, console button, shield bar or detail line; serial number, power draw, iLO firmware rows.

## Design

### 1. Settings model — `src/IloWidget/Models/WidgetSettings.cs`
Add one bool per row. Existing rows default `true`, new rows default `false`, so an existing `settings.json` (which lacks these keys) keeps today's look.

- `ShowModel`, `ShowAddress`, `ShowHealth`, `ShowBios`, `ShowLastContact` = `true`
- `ShowHostName`, `ShowCpu`, `ShowMemory`, `ShowPostState`, `ShowTemperatures`, `ShowFans` = `false`
- `[JsonIgnore] NeedsThermal => ShowTemperatures || ShowFans`

`Clone()` (MemberwiseClone) and `SettingsStore` need no change.

### 2. iLO data — `Models/ServerStatus.cs`, `Services/IloClient.cs`
**Free rows (same `Systems/1` call as today)** — extend `ServerStatus` with `HostName`, `Cpu`, `MemoryGb`, read tolerantly for both Redfish and legacy `/rest/v1` naming:
- Host name: `HostName`
- CPU: `ProcessorSummary.Count` + `.Model`, falling back to `Processors.Count` + `.ProcessorFamily` → e.g. `1x Intel Xeon E3-1220L`
- Memory: `MemorySummary.TotalSystemMemoryGiB`, falling back to `Memory.TotalSystemMemoryGB` → `16 GB`
- POST state: already in `ServerStatus.PostState`, just not shown.

**Sensor rows (one extra request per poll, only when a sensor row is enabled)** — new record `ThermalStatus(int? AmbientC, int? CpuC, int? FanPercent)` and a new `IIloClient.GetThermalAsync(ct)` that GETs `{_base}/Chassis/1/Thermal/`:
- Ambient: `Temperatures[]` entry with `PhysicalContext == "Intake"` or `Name` containing "Ambient"
- CPU: hottest entry with `PhysicalContext == "CPU"` or `Name` containing "CPU"
- Reading: `ReadingCelsius`, falling back to `CurrentReading`; entries with `Status.State == "Absent"` or reading 0 are skipped
- Fans: highest `CurrentReading` (fallback `Reading`) among `Fans[]` that are not absent — percent

Parsing lives in a static `IloClient.ParseThermal(JsonElement)` so it is unit-testable; add a small `Num(...)` helper next to the existing `Str(...)`.

> I could not find iLO 4-specific reference docs for these field names (HPE's online docs cover iLO 5+), so the parser accepts both the Redfish and the legacy names. First thing to check against your real server after implementing.

### 3. View model — `ViewModels/MainViewModel.cs`
- New bindable strings `HostName`, `Cpu`, `Memory`, `PostState`, `Temperature` (`AMB 22°C / CPU 40°C`), `Fans` (`35%`), all `--` when unknown; reset in `ApplySettings` like the existing ones.
- `RefreshAsync`: after `GetStatusAsync`, if `Settings.NeedsThermal`, call `GetThermalAsync` in its own `try/catch (IloException)` — a thermal failure shows `--` in those rows and never turns the widget into an error state.
- `ApplySettings` raises `PropertyChanged(nameof(Settings))` so row-visibility bindings refresh.

### 4. Status view layout — `MainWindow.xaml`
- New row order: header → shield bar → status label → detail → power button → power hint → **info rows** → **REMOTE CONSOLE button** (last, `Margin="0,12,0,0"`).
- Info rows: replace the fixed 5x18px grid with a `StackPanel` of one 18px two-column `Grid` per row (11 rows), each with `Visibility="{Binding Settings.ShowXxx, Converter={StaticResource BoolToVis}}"`. Add a `BooleanToVisibilityConverter` resource in `App.xaml`. The HEALTH row keeps its amber `HealthBad` trigger.
- Power button row changes from `*` to `Auto`.

### 5. Shrink to fit — `MainWindow.xaml` / `.xaml.cs`
- Window: remove `Height="512"`, add `SizeToContent="Height"`.
- Move the outline `Path`, and the two corner-accent `Path`s into a `Canvas` (so they don't affect measured size) and rebuild their geometry from the root grid's actual size in a `SizeChanged` handler (`UpdatePanelShape`): `M 20,0 L w,0 L w,h-20 L w-20,h L 0,h L 0,20 Z` plus the bottom-right accent. The scanline rectangle keeps clipping to `PanelShape.Data`.
- `SettingsView` gets a fixed `Height="452"` so it looks exactly as today.
- Vertical placement: `PlaceWindow` currently uses `Height` in the constructor, which is unknown with `SizeToContent`. Re-evaluate `Top` on every size change: `Top = min(saved Top ?? workArea.Bottom - ActualHeight - 16, screen bottom - ActualHeight)`. Result: a default-placed widget stays glued to the bottom-right corner as it grows/shrinks; a user-placed widget keeps its top edge, and is only nudged up if it would run off the screen.

### 6. Display configuration UI — `MainWindow.xaml` / `.xaml.cs`, `App.xaml.cs`
The existing settings panel is full, so add a third view **`DisplayView`** ("UNSC // DISPLAY CONFIGURATION"):
- Two-column `UniformGrid` of 11 `HudCheckBox`es (MODEL, ILO ADDRESS, HEALTH, BIOS, LAST CONTACT, HOST NAME, CPU, MEMORY, POST STATE, TEMPERATURE, FANS), a small note "TEMPERATURE / FANS ADD ONE ILO REQUEST PER POLL", and **SAVE / CANCEL** buttons (`HudButton`).
- `ShowDisplay()` fills the checkboxes from `_vm.Settings`; SAVE clones settings, sets the flags, `TrySave`, `_vm.ApplySettings`, returns to the status view and re-polls. It does **not** close the remote console (address/credentials unchanged) — so `ApplySettings` keeping the client is not required, but the console-logout step from `SaveSettings_Click` is skipped.
- Entry points: `DISPLAY` item in the right-click menu (next to SETTINGS) and `Display` in the tray menu.
- Refactor the two-view show/hide into one small `ShowView(FrameworkElement)` helper used by status/settings/display.

### 7. Soft power-down from the power button (with confirmation)
Today the button is live only while the server is off, and `IloClient` "deliberately cannot power it off". New behaviour: while the server is **ONLINE**, holding the button (same 1 s hold) asks for confirmation and then sends a **graceful** shutdown. There is still no forced power-off.

- **`IloClient` / `IIloClient`**: new `PressPowerButtonAsync(ct)` — a momentary power-button press, which the OS handles as an ACPI shutdown.
  - Redfish: `POST /redfish/v1/Systems/1/Actions/ComputerSystem.Reset/` `{"ResetType":"PushPowerButton"}` (iLO 4 has no `GracefulShutdown` type)
  - Legacy: `POST /rest/v1/Systems/1` `{"Action":"PowerButton","PushType":"Press","Target":"/Oem/Hp"}`
  - Same status-code/`ThrowForAuth` handling as `PowerOnAsync`; share the POST code between the two. Update the class comment ("cannot *force* it off").
- **New `HudState.Stopping`** (`Models/ServerStatus.cs`): amber + pulsing like `Booting` (`Theme/StateToBrushConverter.cs`, pulse `DataTrigger` and `IsLit` trigger in `MainWindow.xaml`), shield 0.5, hint `SERVER SHUTTING DOWN`, console still openable.
- **`MainViewModel`**:
  - `CanPowerOff => State == Online && !_busy`; `CanUsePower => CanPowerOn || CanPowerOff` (bound to the button's `IsEnabled`). Not available while Booting/POST — a press there would cut power immediately.
  - `PowerHint` for Online becomes `HOLD TO SHUT DOWN`; new `PowerTip` for the tooltip.
  - `PowerOffAsync()`: re-reads the status first and does nothing unless the server is on and past POST (mirrors `PowerOnAsync`), sends the press, records `_powerOffSentAt`, starts the fast-poll window and shows `SHUTTING DOWN / POWER BUTTON PRESSED`.
  - `ApplyStatus`: while iLO still reports On within 3 minutes of the press, keep showing `Stopping`; when it reports Off → `OFFLINE`; after 3 minutes still On → back to `ONLINE` (the OS ignored the press — e.g. hung or no ACPI handler).
- **Confirmation UI** (`MainWindow.xaml` / `.xaml.cs`): `Power_Activated` branches — Offline → power on as today; Online → show an inline confirm strip in place of the hint line: `CONFIRM SHUTDOWN?` with **SHUT DOWN** and **CANCEL** (`HudButton`s, CANCEL is the Esc/default-safe one). Nothing is sent until SHUT DOWN is clicked. The strip closes on CANCEL, on opening Settings/Display, or automatically if the state stops being Online.
- **Privilege**: same iLO "Virtual Power and Reset" privilege that power-on already needs.

### 8. Tests — `tests/IloWidget.Tests`
- Power-down: `PressPowerButtonAsync` posts the right body for Redfish and legacy; `PowerOffAsync` sends nothing when the server is off or in POST; after the press the state is `Stopping`, stays `Stopping` while iLO reports On, becomes `Offline` when it reports Off, and reverts to `Online` after the 3-minute window (using the injectable `now`); `CanPowerOff` only when Online.
- `FakeClient` gains `GetThermalAsync` (+ call counter, optional error).
- `IloClientTests`: new system fields parsed (Redfish and legacy names); `ParseThermal` on a sample with ambient, CPU, an absent sensor and a fan; `GetThermalAsync` hits `/redfish/v1/Chassis/1/Thermal/`.
- `ViewModelTests`: thermal not requested when both sensor rows are off; requested when one is on; thermal `IloException` leaves state Online and rows `--`; `ApplySettings` raises `Settings`.
- Settings: JSON without the new keys loads with existing rows on, new rows off.

### 9. Docs
- `README.md`: describe the DISPLAY panel, the new rows, the button's new position, and replace "The widget cannot power the server off" with the hold-and-confirm graceful shutdown.
- After approval, copy this plan to `docs/PLAN-display-config.md` (per your standing preference).

## Files touched
`Models/WidgetSettings.cs`, `Models/ServerStatus.cs`, `Services/IloClient.cs`, `ViewModels/MainViewModel.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `App.xaml`, `App.xaml.cs`, `Theme/StateToBrushConverter.cs`, tests (`ViewModelTests.cs`, `IloClientTests.cs`), `README.md`, `docs/PLAN-display-config.md`.

## Verification
1. `dotnet build IloWidget.sln` and `dotnet test` — all existing + new tests pass.
2. Run the widget against your ProLiant:
   - Default look: same five rows, REMOTE CONSOLE button now at the bottom, window slightly shorter/taller only as content dictates.
   - Right-click → DISPLAY: untick rows → widget shrinks, outline and corner accents follow; tick all 11 → widget grows and stays on screen.
   - Enable TEMPERATURE / FANS / CPU / MEMORY / HOST NAME and confirm real values appear (this validates the iLO 4 field names); with the server off, rows show `--` or ambient only without an error state.
   - With the server ONLINE: hold the power button → confirm strip appears, nothing sent; CANCEL → back to normal. Hold again → SHUT DOWN → widget shows SHUTTING DOWN (amber, pulsing), then OFFLINE once the OS has shut down; power-on by holding still works afterwards.
   - Open SETTINGS → panel looks as before; SAVE/CANCEL return to the compact status view.
   - Restart the app → choices persist; drag the widget, restart → position persists.
