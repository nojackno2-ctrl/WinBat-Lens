# Project State & Handoff

## Compact UI pass (latest, on top of v1.0.8)

The dashboard was sized for a much larger window than it needed. Shrunk it to
**1200x780** (was 1380x960; minimum 980x620, was 1060x800) by taking the space
out of padding, margins and corner radii rather than out of type.

The constraint given was that text must stay legible, so nothing is smaller than
11px, and several previously-tiny labels were made **larger**: the health-%
badges in the capacity table (9/10px -> 10/11px), the report timestamp (10 ->
11px) and the header subtitle (10 -> 11px). Body text sits at 12px, card titles
at 13px. What actually shrank: card padding 16 -> 11,9; card gaps 16 -> 9; outer
window margin 18 -> 10; right-panel padding 20 -> 12; health ring 115 -> 84px;
chart 160 -> 136px tall; button padding 16,8 -> 11,5; tab padding 14,10 -> 10,6.

Two structural fixes came out of it:

**The sidebar could not align with the right panel.** It was a `StackPanel`
inside a `ScrollViewer`, so its height was whatever the three cards summed to —
unrelated to the right panel's height, which stretches. The bottom edges never
matched and the last history row was clipped mid-row. The sidebar is now a
3-row `Grid` (`Auto` / `Auto` / `*`) with the capacity-history card in the `*`
row, so it fills the leftover height and both columns end on the same line. The
card's inner `MaxHeight="164"` went away with it; the list now grows with the
window.

**powercfg embeds newlines in date ranges.** `StripTags` only did `.Trim()`, so
the interior newline in

```
<td class="dateTime">2026-07-12
      - 2026-07-19</td>
```

survived into the cell text and a `TextBlock` rendered it as two lines, with the
second line clipped by the column width. `StripTags` now collapses every
whitespace run to a single space. This is shared by every parsed field, so all
of them are normalised, and `ExtractNumber` strips non-digits anyway.

Worth knowing for anyone confused by that first row: **powercfg keeps only the
last 7 days at daily granularity and merges everything older into one aggregated
row.** Verified by diffing two reports from consecutive days — 07-25's report
listed 07-12, 07-13, 07-14 ... individually; 07-26's report collapsed 07-12 to
07-19 into a single row reading 55,950 mWh, which is not any single day's value
(those were 55,811 / 55,929 / 55,969). The exact averaging powercfg uses is
undocumented and was not reverse-engineered. The range row will keep growing;
the daily rows stay a rolling 7.

The capacity table's header and data grids are now width-matched on purpose: the
data list's scrollbar is pinned visible and the header carries a 17px right
margin (`SystemParameters.VerticalScrollBarWidth`), so both grids get the same
305px and the columns line up in every state. Previously the header shifted by
the scrollbar width whenever the list happened to scroll.

Sizing note for future edits: the right panel's tab strip needs ~750px. At a
1200px window that leaves the sidebar about 20px of room to grow before the tabs
wrap — widen the window at the same time if the sidebar needs more.

## One colour per meaning (v1.0.8)

Discharge was being drawn in three different colours depending on where you
looked, and the dGPU shared a colour with it:

| surface | discharge (before) | dGPU (before) |
|---|---|---|
| headline number | amber `#F59E0B` | — |
| chart line + legend | cyan `#38BDF8` | amber `#F59E0B` |
| tray icon | red `#EF4444` | — |

So the amber in the headline and the amber line on the chart meant different
things, while the same concept changed colour three times.

Settled on **discharge = amber `#F59E0B`, charge = emerald `#10B981`, dGPU =
cyan `#38BDF8`**, applied to the headline, the chart polylines, the legend and
the tray icon. Charge was already emerald everywhere and did not move. Amber
won for discharge because the headline is the most-looked-at surface; the dGPU
took over the freed cyan.

Also removed with it: the `PolylineCpu` series and the orphaned purple legend
swatch (a `Border` with no label after its `TextBlock` was deleted earlier). The
CPU line had been pinned flat at zero since v1.0.5 removed CPU wattage. The
chart history tuple drops its `CpuW` slot, so it is now a 3-tuple.

## GPU list card removed (v1.0.7)

Removed the 「系統顯示卡清單」 card. It listed adapter name, VRAM, driver version
and driver date — inventory data, not power, and nothing else consumed it.

The cascade was larger than the card itself:

- `GpuInfo` dropped `GpuTypeTag`, `VramText`, `DriverVersion`, `DriverDate`,
  `Status` and `StatusClass`. Only `Name`, `IsDiscrete` and `VramBytes` remain —
  `VramBytes` solely because the discrete-GPU heuristic tests `>= 1 GB`.
- `GpuInfoService` no longer selects `DriverVersion`, `DriverDate`, `Status` or
  `Availability` from `Win32_VideoController`, and no longer formats a VRAM
  string or a status string.
- `RealTimePowerService.InstalledGpus` was the accessor that fed the card and is
  gone; `_cachedGpus` stays for resolving dGPU/iGPU names.
- `MainWindow.LoadGpuSpecs()` and the `GpuListTitle` localisation key are gone.

The tip box still claimed the app updates "CPU、獨顯、內顯、螢幕背光、Wi-Fi、磁碟與
記憶體功耗" every second — untrue since v1.0.6 removed those rows. Now just
「即時監測每 1 秒自動更新一次。」

## Charging shown in the headline; power-less rows removed (v1.0.6)

Two user-reported problems, the first a genuine bug I introduced in v1.0.5.

### Bug: the headline card read 0.0 W while charging at 56.1 W
The v1.0.5 refactor removed the *assignments* to `TotalSystemHardwareW` and
`AcTotalInputW` but left the fields on the model and **six UI references** to
them. Both stayed 0 forever, so on AC the main card showed `~0.0 W` and its
subtitle still carried the old v1.0.4 wording — while the subtitle's own
`電池充電 +56.1W 實測` proved a real measurement was in hand.

Fixed by deleting both fields outright and reworking the headline to show
whichever rate is actually measured:

| state | headline |
|---|---|
| charging | `+56.1 W` (emerald) |
| on battery | `-48.9 W` (amber) |
| on AC, pack idle | `-- W` |

The tray icon and its tooltip had the same defect and now follow the same rule
(a slate `–` glyph when there is nothing real to show). `DynamicTrayIconService`
also keyed its redraw cache on `IsAcOnline`, which no longer determines the
colour; the key is now the drawn text **and** colour, otherwise a state change
that kept the digits would have kept a stale colour.

Card title changed from 「電池總放電 / AC 變壓器總供電功率」 to
「電池充放電功率 (實測)」 — it never showed adapter power and now never will.

### Rows without a power sensor removed from the breakdown
Per the user: components whose power cannot be detected are meaningless in a
power breakdown. Removed the CPU, iGPU, screen, Wi-Fi, disk and RAM rows
entirely. **Only the dGPU row remains**, because NVML gives it a real wattage.

Their underlying utilisation values are still collected — `SystemPowerLoadStatus`
and the history log still use CPU/dGPU load — they are simply no longer
displayed on a page about wattage. A short note now explains on-screen why each
component is absent. Section retitled to 「硬體實測功耗」.

Verified live: charging read **54.5 W** through the production service at the
time of the build.

## All estimated wattage deleted (v1.0.5)

User decision, stated plainly: *"我不要推估的東西，如果真的沒辦法抓到真實的數據
就刪掉該功能"* — no estimates; if a real value cannot be obtained, remove the
feature. Every formula-derived wattage is now gone from the codebase rather
than being shown with a disclaimer.

### Deleted outright
| removed | what it was |
|---|---|
| `CpuPowerW` | `2.5 + usage*22.5` — unreadable here (OEM owns the AMD SMU) |
| `IgpuPowerW` | `1.0 + usage*12.0` — no consumer iGPU exposes package power |
| `ScreenPowerW` | `1.0 + brightness*5.5` |
| `DiskPowerW` | `0.4 + usage*3.2` |
| `WifiPowerW` | `0.6 + throughput-scaled` |
| `RamPowerW` | `0.8 + usage*1.7` |
| `MotherboardPowerW` | hard-coded `2.5` — no data source whatsoever |
| `TotalSystemHardwareW` | the sum of the above |
| `AcTotalInputW` | `charge + total` — Windows has no adapter-input API |
| `IsCpuPowerMeasured`, `IsTotalPowerMeasured` | no longer meaningful |

The motherboard/USB **row was removed from the UI entirely** (nothing real
remained in it), as was the CPU (W) series from the waveform chart and its
legend. The hardware-breakdown rows for CPU, iGPU, screen, disk, Wi-Fi and RAM
were **kept** — their utilisation, throughput, brightness and GB figures are
genuinely measured; only the wattage line was stripped out of each.

### What still shows a wattage, and why it is trustworthy
- **Battery discharge** — `IOCTL_BATTERY_QUERY_STATUS`. Verified live at
  **48.9 W** on battery. This is the whole machine's real draw.
- **Battery charge** — same IOCTL.
- **dGPU package power** — NVML, works unelevated.

Nothing else. When no real figure exists the UI shows `-- W` and the tip text
says why, e.g. on AC with a full pack: "此狀態下沒有可量測的系統功率（變壓器輸入
功率 Windows 並未提供）".

### Consequence worth knowing
**On AC there is now no system wattage at all.** That is correct rather than a
regression: the pack passes no current, and adapter input is vendor-EC
territory. The old screen filled that gap with an invented number.

`SystemPowerLoadStatus` is now derived from utilisation thresholds only; it
previously mixed in `TotalSystemHardwareW`. The CSV export swapped its
`螢幕功耗(W)` column (an estimate) for `獨顯功耗(W)` (real NVML).

## Real battery charge/discharge power (v1.0.4)

**The app was falling back to an estimate while the battery was reporting a
real figure the whole time.** `RealTimePowerService` read
`Win32_Battery.DischargeRate` (`root\CIMV2`), which is blank on this laptop and
on many others. `root\WMI BatteryStatus` and the underlying
`IOCTL_BATTERY_QUERY_STATUS` both work fine.

### The earlier "the EC does not report rate" conclusion was wrong
Every previous check ran with `RemainingCapacity == FullChargedCapacity` and
the charger plugged in — a full pack on AC passes no current, so **0 mW was the
correct answer** and was misread as "unsupported". The tell was that the driver
returned a real `0` rather than `BATTERY_UNKNOWN_RATE` (`0x80000000`), which is
the value the spec reserves for genuine unavailability.

Confirmed by unplugging while polling:

```
21:54:24  電池  放電中   -5.51 W    idle, just unplugged
21:54:38  電池  放電中  -22.29 W
21:54:57  電池  放電中  -30.91 W
21:58:45  電池  放電中  -39.90 W    under load
21:58:51  市電  ...      0.00 W     replugged
```

### New `Services/BatteryTelemetryService.cs`
Reads the battery class driver directly over
`IOCTL_BATTERY_QUERY_STATUS` (SetupDi enumeration of
`GUID_DEVCLASS_BATTERY` → `CreateFile` → `IOCTL_BATTERY_QUERY_TAG`).

- **No elevation, no WMI/COM.** Measured **0.4 ms average / 1.2 ms max** per
  read, so unlike the WMI path it is cheap enough to poll every second on the
  UI thread. (Contrast the LibreHardwareMonitor sweep at 85–256 ms, which had
  to be moved to a background timer.)
- Returns voltage in the same call, so the separate voltage WMI query is gone.
- `IsRateKnown` distinguishes a true `0` from `BATTERY_UNKNOWN_RATE`. **Do not
  treat 0 W as failure** — that is the bug this section exists to document.
- Direction comes from the `BATTERY_CHARGING`/`BATTERY_DISCHARGING` state flags
  with `Math.Abs` on the rate, because not every driver honours the
  negative-means-discharging convention.
- Re-queries the battery tag once on failure (the tag invalidates when a pack
  is swapped) before giving up.

### On battery, the headline number is now a real measurement
`DischargeRateW` with `IsDischargeRateMeasured` set is the **whole machine's
actual power draw, measured at the pack** — no per-component estimation, and it
neatly sidesteps the Armoury-Crate-owns-the-SMU problem that makes CPU package
power unreadable (see v1.0.3). This is the app's core metric.

### Two other fabrications removed while in here
- Charging wattage fell back to hard-coded `12.5 / 28.0 / 45.0 W` chosen by
  battery percentage. Now reports `--` when unknown.
- `IsCharging` was inferred from `BatteryPercent >= 98`. Wrong on any laptop
  with a charge limit — this ASUS stops around 95%, which the old test called
  "still charging". The driver's own `BATTERY_CHARGING` flag is now used.

### AC adapter input is still an estimate, and always will be
Windows exposes no API for adapter input wattage; it is vendor-EC territory
(hence Armoury Crate can show it). It stays `charge rate + hardware estimate`
and is rendered with a leading `~`. A USB-C PD machine could read the
negotiated contract over UCSI, but this laptop uses a barrel connector.

## Real hardware sensors + temperature removal (v1.0.3)

Triggered by the user noticing WinBat Lens disagreed with Armoury Crate during
a 3DMark run. It did, and the reasons were real.

### Every wattage was a formula, not a measurement
Verified by reverse-computing the on-screen numbers from the user's screenshot:
`dGPU 3.0 + 0.869*35.0 = 33.4 W` and `CPU 2.5 + 0.184*22.5 = 6.6 W` — both
matched the display exactly. The formulas also had hard ceilings that made
correct full-load readings impossible on this class of machine:

| component | old formula | ceiling |
|---|---|---|
| CPU | `2.5 + u*22.5` | 25.0 W |
| dGPU | `3.0 + u*35.0` | **38.0 W** |
| six others | — | 30.5 W |
| **total** | | **93.5 W** |

An RTX 3060 Laptop alone runs to 115 W TGP (Armoury Crate showed 60 W at that
moment against the app's 33.4 W). Worse, the estimate reported **0 W at idle**
while `nvidia-smi` measured **18.76 W**.

### "Battery temperature" was the CPU thermal zone
`GetWmiBatteryTelemetry` read `Win32_PerfFormattedData_Counters_ThermalZone
Information` — on this machine the only zone is `\_TZ.THRM`, measured at
90.9 °C — and stored it in `BatteryTemperatureC`, which the UI labelled
"電池物理狀態 (電壓/電流/溫度)" with the subtitle "WMI & ACPI 即時電氣感測".
The screenshot showed "battery" 81.1 °C while Armoury Crate showed CPU 81 °C.
A lithium pack at 81 °C would be a fire. Real battery-temperature sources
(`MSAcpi_ThermalZoneTemperature`, `MSBatteryClass`) are inaccessible here
(access denied / general failure).

**Per the user's decision, temperature was removed entirely** — model, UI
cards, ListView column, CSV column, and both localization dictionaries. Both
thermal-zone WMI queries are gone, which also drops two per-cycle WMI calls.

### New `Services/HardwareSensorService.cs`
Wraps LibreHardwareMonitorLib 0.9.6 (this forced `System.Management` 8.0.0 →
10.0.2). Every reading is a `double?`; `null` means the machine genuinely does
not report it, and the caller falls back to the formula **and says so**.

Measured on this machine (Ryzen + RTX 3060 Laptop), unelevated:

| sensor | result |
|---|---|
| NVIDIA GPU package power | ✅ real, 11–19 W tracking (NVML is userspace) |
| GPU core / hot-spot temp | ✅ real |
| Battery pack voltage | ✅ 15.83 V |
| CPU package power | ❌ 0 W — SMU owned by Armoury Crate; elevation does not help (tested) |
| AMD iGPU power | ❌ no such sensor exists |
| Battery charge/discharge W | ❌ EC does not report it (long-standing) |

Three non-obvious things this cost, all verified by measurement:

1. **A full sweep took 85–256 ms.** That cannot sit on the 1 s UI tick. Polling
   moved to a background `System.Threading.Timer` with `Interlocked` re-entry
   guard; the UI now only reads cached fields — **0.003 ms**.
2. **Updating CPU hardware is the most expensive part of a sweep**, and is
   pointless when RAPL reports nothing, so `_cpu` is nulled after
   `IsCpuPowerAvailable` comes back false.
3. **Every `ISensor` retains a day of value history by default.** Polling ~40
   sensors at 1 Hz grew private bytes 139 → 159 MB in three minutes. Fixed by
   setting `ISensor.ValuesTimeWindow = TimeSpan.Zero` + `ClearValues()` on all
   sensors after open. Note this is on **ISensor, not Computer** — `Computer`
   has no such property in 0.9.6. Private bytes are now flat at ~139 MB and the
   tray working set trims to **23.8 MB** (better than the 26.4 MB pre-sensor
   baseline). Warm start moved only ~373 → ~383 ms because `Initialize()`
   (~1.0 s) runs on the existing warmup thread, off the first-paint path.

`MainWindow.FormatPower` renders `18.8 W (實測)` for sensor values and
`~6.6 W (推估)` for formula values, so the two can never be confused.

### Closed: CPU power cannot be read on this machine, elevated or not
The user ran the sensor probe elevated. **Elevation does not help.** On an
AMD Ryzen 9 5900HS every SMU sensor still reports zero:

```
系統管理員權限: 是 ✔
[Cpu] AMD Ryzen 9 5900HS with Radeon Graphics
    Power       Core #1..#8 (SMU) = 0.00 W
    Power       Package           = 0.00 W
    Temperature Core (Tctl/Tdie)  = 0.00 °C
[GpuNvidia] NVIDIA GeForce RTX 3060 Laptop GPU
    Power       GPU Package       = 10.84 W    <-- works fine
```

CPU *temperature* reads zero too, and it shares the SMU mailbox with power —
so this is not a privilege problem, the whole SMU channel is unreachable.
Cause confirmed by process/service enumeration: `ArmouryCrateService`,
`ArmouryCrateControlInterface` and `ASUSOptimization` are running, and ASUS's
driver takes exclusive ownership of the AMD SMU mailbox. This is the standard
reason third-party tools read zero for AMD sensors on ROG laptops — Armoury
Crate can show CPU watts precisely because it is the one holding the channel.

**Do not build an elevation path.** It was tested and does not work, and the
only workaround would be asking the user to quit their OEM software. The CPU
figure stays a clearly-labelled estimate. Probe source kept at
`scratchpad/probe/` if another machine ever needs checking.

## Icon fix + startup/installer session (v1.0.2)

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
- **CPU package power is unavailable on ASUS ROG / AMD hardware** and stays a labelled estimate. Verified elevated: every SMU sensor reads 0 because Armoury Crate owns the SMU mailbox. Not fixable from this side — see the v1.0.3 section.
  headline wattage is still a formula estimate, not a measurement. A proper fix
  reads `IOCTL_BATTERY_QUERY_STATUS` against the battery device directly.
- **500-record history cap is memory-only** — nothing is persisted, so closing the
  app loses the log. CSV export is manual.
- **No automated tests.** Every verification in this file is a manual measurement;
  the parser (`BatteryReportParser`) in particular is regex-heavy and would
  benefit from unit tests over the two saved sample reports.
