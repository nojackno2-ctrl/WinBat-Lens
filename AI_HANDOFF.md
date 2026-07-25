# Project State & Handoff

## Icon fix + startup/installer session (latest)

### The app icon never appeared — root cause found and fixed
`app_icon.ico` was **not an ICO file**. It was a JPEG with a hand-written 22-byte
ICO header glued on the front (`00 00 01 00 01 00 ...` followed immediately by
`FF D8 FF E0 ... JFIF`). `app_icon.png` was the same JPEG with a `.png` name.
Windows could not parse either, so the exe shipped with no icon and Explorer fell
back to the generic window placeholder — exactly what the desktop shortcut showed.

Both files were regenerated from the original 1024×1024 artwork:
- `app_icon.ico` — 10 entries (16/20/24/32/40/48/64/96/128 as **32-bit BMP/DIB**
  with AND mask, plus 256 as PNG).
  **The sub-256 entries must stay DIB**: `System.Drawing.Icon.ToBitmap()` — which
  `MainWindow.InitSystemTrayIcon` uses to build the `NotifyIcon` — mis-decodes
  PNG-compressed ICO entries and renders colour noise. A first attempt using PNG
  entries for every size looked fine in Explorer but garbled through GDI+.
- `app_icon.png` — a real 256×256 PNG (used for the WPF window/taskbar icon).
- Regeneration script kept at `scratchpad/make_icon2.ps1` if the art changes.

Verified: `Icon.ExtractAssociatedIcon` on the new `dist` exe returns the battery
artwork; the old installed copy still returns the generic placeholder.

> After installing, Windows may still show the cached old icon. `ie4uinit.exe
> -show` (or a re-login) refreshes the shell icon cache.

### Startup speed
Enabled `PublishReadyToRun` + `TieredPGO`. Measured warm start to first paint
(`WaitForInputIdle`, median of 3, this machine):

| config | exe size | warm start |
|---|---|---|
| no R2R + compressed (old) | 69 MB | ~409 ms |
| **R2R + compressed (chosen)** | **73 MB** | **~373 ms** |
| R2R, uncompressed | 171 MB | ~350 ms |

Compression stays **on**: turning it off buys ~23 ms for +98 MB. `build-release.ps1`
no longer passes packaging flags on the command line — they all live in the csproj
so a plain `dotnet publish` produces an identical binary.

### Tray working set now stays trimmed
`TrimWorkingSet()` ran only once, on minimize. Measured over 4 minutes in the tray
the working set climbed straight back (52 → 119 MB) as the OS paged the app in.
`LivePowerTimer_Tick` now re-trims every 60 ticks for as long as the window is hidden.

| in tray | before | after |
|---|---|---|
| on minimize | 52.7 MB | 52.0 MB |
| +2 min | 63.3 MB | 58.3 MB |
| +4 min | **112.1 MB** | **26.4 MB** |

GDI 38 / USER ~35 / handles ~844 held flat across both runs, and private bytes
stayed ~112–137 MB — no handle or managed leak.

### Installer: Traditional Chinese + Start Menu option
- **`installer/ChineseTraditional.isl`** — Inno Setup 6 ships no Chinese language
  file, so the full translation is authored and maintained in this repo (UTF-8
  **with BOM**, `LanguageID=$0404`, `LanguageCodePage=950`, `LanguageName` written
  as `<hex>` escapes because it is read before the encoding is known). Listed
  first in `[Languages]` so it is the default; `ShowLanguageDialog=yes`.
- New **`startmenuicon`** task controls the `{group}` shortcuts;
  `DisableProgramGroupPage=yes` fixes the folder to `DefaultGroupName` so the
  choice is a single checkbox on the Tasks page rather than a whole wizard page.
- `desktopicon` no longer defaults to unchecked.
- Task/group captions moved into a per-language `[CustomMessages]` block.
- ISCC still warns `PrivilegesRequired=admin` + HKCU. That is deliberate and
  documented inline: the app's own `StartupService` reads/writes that exact
  per-user Run value, so the installer task and the in-app checkbox must agree.

### Runtime honesty: no more invented readings
Fallback constants were being displayed as if measured. Removed:
CPU 12.5%, disk 2.0%/0.5 MB-s, RAM 16/8 GB, Wi-Fi 120 kbps, temperature 36.5 °C.
`RealTimePowerState` gained `IsVoltageMeasured` / `IsTemperatureMeasured` /
`IsBrightnessMeasured`; `BatteryTelemetryText` renders `--` per-field, and the
brightness row shows `-- (無法讀取)` on panels without `WmiMonitorBrightness`.
Voltage keeps a 15.4 V nominal internally (the `I = P / V` maths needs it) but is
no longer *shown* as a reading.

### Other
- `GetWifiThroughputKbps` enumerated every NIC once per second — the heaviest
  remaining per-tick call. Now on the same `WmiRefreshMs` throttle as the WMI
  queries, reporting the average over the window.
- Tray menu, tooltip and balloon text are localized and re-label on language
  toggle (previously hard-coded Chinese).
- The minimize balloon tip now shows **once** per session instead of every time.
- CSV export escapes embedded quotes per RFC 4180.

## Startup + tray-idle optimization session
Goal: faster first paint / first tick, and near-zero work while minimized to tray.

1. **Background warmup** (`RealTimePowerService.Initialize()`, called via `Task.Run`
   in `MainWindow_Loaded` before the 1s timer starts): forces the static ctor
   (PerformanceCounters + GPU WMI enumeration), primes the GPU Engine counter
   category and all slow WMI caches off the UI thread. Previously the first
   monitoring tick paid all of this (potentially seconds) on the UI thread.
2. **`LoadGpuSpecs` reuses `RealTimePowerService.InstalledGpus`** — removed the
   duplicate `Win32_VideoController` WMI query at startup.
3. **Tray-idle short-circuit**: `UpdateLivePowerUI` returns right after updating
   tray icon/tooltip + history when `!IsVisible`; waveform history still
   accumulates but `RedrawWaveformChart` is skipped. `RestoreFromTray` calls
   `UpdateLivePowerUI()` once so the window is fresh on restore.
4. **Tray icon regeneration skipped when unchanged** (`DynamicTrayIconService`
   caches last drawn text + AC state) — most seconds the rounded wattage is
   identical, so the per-second GDI bitmap/font/icon churn is gone.
5. **WMI throttle timestamps switched `DateTime.Now` → `Environment.TickCount64`.**
6. **`PowerCfgService`**: `powercfg` is killed on the 10s timeout (was left running).
7. **Battery report parsing moved off the UI thread** (`Task.Run` in
   `RunBatteryCheckAsync`); real-exit path now stops the timer and disposes the
   tray icon in `MainWindow_Closing` (Window `Unloaded` is unreliable).
- Verified `dotnet build WinBatLens.csproj -c Debug` -> 0 warnings, 0 errors.

## Current Objective
Synchronizing codebase to GitHub repository and creating GitHub Release v1.0.1 with Installer (`Setup.exe`), Portable Single Executable (`.exe`), and Portable ZIP (`.zip`) packages.

## Power-Only Focus & Windows Power Plan Integration (latest)
- Refocused telemetry strictly on **Power Consumption ($W$)**, battery energy metrics ($V, A, W, \text{mWh}$), and Windows Power Efficiency modes.
- Added native zero-allocation Windows Power Scheme detection via `PowrProf.dll` (`PowerGetActiveScheme`, `PowerReadFriendlyName`) to display active Windows Power Plan (e.g., `Balanced`, `High Performance`, `Power Saver`, `Turbo`).
- Updated `RealTimePowerState.PowerPlanName`, `LocalizationService`, `MainWindow.xaml` (added Power Scheme row), and `MainWindow.xaml.cs`.
- Verified `dotnet build WinBatLens.csproj -c Debug` -> 0 warnings, 0 errors.

## Battery Hardware Telemetry Integration (latest)
- Added real-time **Battery Voltage ($V$)**, **Current ($A$)**, and **Temperature ($^\circ C$)** monitoring to WinBat Lens.
- Polling via cached `root\WMI:BatteryStatus` (Voltage), ACPI / $I = P / V$ derivation (Current), and `Win32_PerfFormattedData_Counters_ThermalZoneInformation` / `MSThermal_ThermalZoneTemperature` (Temperature).
- Updated models (`RealTimePowerState`, `PowerHistoryRecord`), services (`RealTimePowerService`, `RealTimePowerHistoryService`, `LocalizationService`), and WPF UI (`MainWindow.xaml`, `MainWindow.xaml.cs`).
- Dashboard top cards now feature a dedicated 3rd telemetry card, hardware power breakdown list includes a physical telemetry row, and event history log table/CSV export includes Voltage, Current, and Temp columns.
- Verified `dotnet build WinBatLens.csproj -c Debug` -> 0 warnings, 0 errors.

## Waveform Chart Unit Unification (latest)
- Unified waveform graph (60s live graph) to display all metrics using **Wattage (W)** on the same Y-axis scale.
- Changed CPU and dGPU trendlines from percentage (`%`) to power consumption in Watts (`CpuPowerW` and `DgpuPowerW`).
- Updated graph legend and localization strings from `CPU (%)` / `獨顯 (%)` to `CPU (W)` / `獨顯 (W)`.
- Verified `dotnet build WinBatLens.csproj -c Debug` -> 0 warnings, 0 errors.

## Release Packaging & Version Upgrade Session (latest)
- Added **Single Instance Mutex** (`WinBatLens_SingleInstance_Mutex`) in `App.xaml.cs`.
- Updated **Inno Setup Script** (`installer/WinBatLens.iss`):
  - Fixed `AppId={{D2B3F0E1-8E4B-4D2A-9A2C-5F1B3E7A902A}` for continuous version tracking across builds.
  - Configured `AppMutex=WinBatLens_SingleInstance_Mutex`, `UsePreviousAppDir=yes`, `UsePreviousGroup=yes`, `UsePreviousTasks=yes`, `CloseApplications=yes`.
  - Installer automatically detects existing installed version, safely shuts down background instances, updates binaries, updates registry version numbers, and preserves user preferences.
- Generated **Dist Packages** (`dist/`):
  - `WinBatLens_v1.0.1_Setup_x64.exe` (Installer Setup with in-place update support).
  - `WinBatLens_v1.0.1_Portable_x64.exe` (Standalone Portable Single Executable).
  - `WinBatLens_v1.0.1_Portable_x64.zip` (Portable ZIP archive).



## Memory optimization session (latest)
Goal: reduce the footprint of this always-on, once-per-second tray monitor.

Changes (all in `Services/RealTimePowerService.cs`, `MainWindow.xaml.cs`, `WinBatLens.csproj`):
1. **RAM stats via native `GlobalMemoryStatusEx`** instead of a per-second
   `Win32_OperatingSystem` WMI query — removes heavy COM allocation each tick.
2. **Throttled the remaining WMI queries** (brightness, charge rate, discharge
   rate) to a 4s cache (`WmiRefreshSeconds`); they change slowly. Tray wattage
   still moves each second via the hardware estimate. ~75% fewer WMI calls.
3. **Frozen, shared `SolidColorBrush`/`DoubleCollection`** in `MainWindow` —
   `UpdateLivePowerUI` + `DrawChartGridlines` no longer allocate brushes per tick.
4. **Waveform redraw** iterates the `Queue` directly (no `.ToList()`/`.Max()` LINQ each tick).
5. **GC tuning** in csproj/runtimeconfig: `Server=false`, `Concurrent=false`,
   `System.GC.ConserveMemory=5`.
6. **`EmptyWorkingSet` + `GC.Collect` on minimize-to-tray** (`TrimWorkingSet()` in
   `MainWindow.HideToTray`) — the app's dominant use case.

Measured (Release self-contained single-file, this machine):
- Steady-state visible: WS ~200–220 MB (WPF framework baseline dominates), flat over
  time (no more per-tick heap creep — the key anti-leak win).
- **Minimized to tray: WS drops ~201 MB → ~61 MB (~70% less) and stays stable.**
- Build: `dotnet build/publish WinBatLens.csproj -c Release` → 0 warnings, 0 errors.

Not yet committed at time of writing.

## Prior session: code review + reliability fixes
Verified on a live AMD iGPU + NVIDIA RTX 3060 dGPU laptop.

## Project Status
- **Build**: `dotnet build WinBatLens.csproj -c Debug` → 0 warnings, 0 errors. Launches clean (no `winbat_crash.log`).
  - NOTE: because the csproj sets `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, build output lives in `bin/Debug/net8.0-windows/win-x64/`, not `bin/Debug/net8.0-windows/`.
- **Verified working on this machine** (by replaying each underlying data source):
  - Battery %/AC (`Win32_Battery` / `GetSystemPowerStatus`), brightness (`WmiMonitorBrightness`), RAM (`Win32_OperatingSystem`), CPU/disk PerformanceCounters, GPU list (`Win32_VideoController`).
  - dGPU real-time load: GPU Engine counter LUIDs map exactly to DXGI adapter LUIDs; NVIDIA RTX 3060 correctly flagged discrete.
  - `powercfg /batteryreport` generation + regex parsing (health 74%, absent cycle-count handled). Report labels are English even on zh-TW Windows, so the parser is locale-safe.
- **Known caveat**: `Win32_Battery.ChargeRate`/`DischargeRate` return blank on this laptop, so on-screen charge/discharge wattage is an estimate from built-in formulas, not a measurement (common — many laptops' EC does not expose these).

## Fixes applied this session (not yet committed at time of writing)
1. **No-battery / desktop machines no longer show a misleading "0% critically degraded" health score.**
   - `HealthMetrics.HasBattery` flag added; `CalculateHealthMetrics` returns a neutral "無電池裝置" state when `DesignCapacity <= 0`; `GenerateDiagnostics` emits a single informational tip instead of degradation warnings; UI shows `—` for health/capacity and hides the `%` sign.
   - Verified with the real parser: desktop report → `HasBattery=false`; laptop report → 74.2% "需要注意" (no regression).
2. **DXGI LUID map no longer caches an empty result on enumeration failure.**
   - `EnsureLuidMap()` returns without caching when `DxgiAdapterService.GetAdapters()` yields 0 adapters, so a transient DXGI failure can no longer strand the dGPU at 0% for the whole session.

## Open items / suggestions (not yet done)
- **Unsigned binaries** — the biggest remaining distribution problem. SmartScreen
  shows "Windows protected your PC" on every other machine and some AV engines
  flag self-extracting single-file .NET exes. Needs a real code-signing
  certificate (OV ~US$200/yr, or EV for instant SmartScreen reputation).
- **`.git` is ~334 MB** because `publish/WinBatLens.exe` + `.pdb` were committed
  historically. `publish/` is now gitignored, but the blobs remain in history —
  shrinking it needs a history rewrite (`git filter-repo`), which rewrites SHAs.
- **The 1-second `DispatcherTimer` never stops while in the tray.** Sampling at
  1 Hz forever is what forces the repeated working-set trims. Dropping to ~5 s
  while hidden would cut background CPU ~80%; the tray wattage number would just
  update less often. Not done because it changes observable behaviour.
- **`Win32_Battery.ChargeRate`/`DischargeRate` are blank on this laptop**, so the
  headline wattage is still a formula estimate, not a measurement. A proper fix
  reads `IOCTL_BATTERY_QUERY_STATUS` against the battery device directly.
- **500-record history cap is memory-only** — nothing is persisted, so closing the
  app loses the log. CSV export is manual.
- **No automated tests.** Every verification in this file is a manual measurement;
  the parser (`BatteryReportParser`) in particular is regex-heavy and would
  benefit from unit tests over the two saved sample reports.
