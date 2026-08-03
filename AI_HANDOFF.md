# Project State & Handoff

## Memory + CPU optimization pass (latest)

Goal: cut what this always-on tray monitor costs per tick, in both allocations
and CPU, without changing a single number the dashboard reports. No display
behaviour changed — every reading, cadence and label is what it was.

### Per-tick hot path (`Services/RealTimePowerService.cs`)
1. **GPU Engine instance names are parsed once, not ~600 times a second.**
   The sweep used to lower-case every instance name and take two `Substring`s
   from it on every tick — roughly 1,800 throwaway strings a second, all
   identical to the previous second's. The LUID/engine-type split is now cached
   per instance name (`_gpuEngineKeys`), pruned alongside `_gpuEngineSamples`,
   and `ExtractToken` matches case-insensitively so nothing is lower-cased.
   Parsing is also deferred until an instance reports non-zero utilisation,
   which is a small minority of them.
2. **The per-adapter aggregation dictionaries are reused**, cleared in place
   rather than rebuilt: a machine has two or three adapters and a handful of
   engine types, so the same dictionaries now last the life of the process.
3. **GPU names resolved once** (`EnsureGpuNames`). The adapter list cannot
   change under a running process, but two LINQ scans with two closures ran
   every second to reach a constant answer.
4. **One battery read per tick instead of two.** `GetCurrentPowerState` already
   held a `BatteryTelemetryService.Reading`; the voltage path used to call
   `TryRead()` again, which is three more device IOCTLs. The reading is now
   handed over (`GetBatteryVoltage(bool, double)`).

### Battery IOCTLs (`Services/BatteryTelemetryService.cs`)
5. **No native allocations on the polling path.** Every struct involved is
   blittable, so `DeviceIoControl` now has typed `ref`/`out` overloads and the
   marshaller pins stack locals. That removes 6 `AllocHGlobal`/`FreeHGlobal`
   pairs, 3 `StructureToPtr` and 2 `PtrToStructure` calls per second. The
   variable-length string levels (read once per pack) still use a heap buffer.
   Struct sizes are resolved once into static fields instead of by
   `Marshal.SizeOf<T>()` on each call.

### Background sensors (`Services/HardwareSensorService.cs`)
6. **`SetIdleMode(bool)`** drops the LibreHardwareMonitor sweep from 1 s to 5 s
   while the window is hidden, matching the tray tick that consumes it. That
   sweep is the largest single piece of background CPU the app spends (~16 ms
   per sweep, measured), and while minimized four of every five were discarded.
   Called from `HideToTray`/`RestoreFromTray`; deliberately lock-free so the UI
   thread can never block on `Initialize`'s long-held lock.

### UI (`MainWindow.xaml.cs`)
7. **Tray tooltip assigned only when it changes.** `NotifyIcon.Text` is a
   `Shell_NotifyIcon` call into the shell, not a field write, and it was made
   every second regardless.
8. **Waveform point collections written in place.** Three fresh
   `PointCollection`s (180 points) were handed to the Polylines each second;
   they are now allocated once and mutated (`EnsureChartPointCapacity`).
9. **Y-axis labels rewritten only when the scale changes**, instead of five
   text assignments — each of which WPF answers with a measure and arrange
   pass — every second.
10. **Working-set trimming is growth-driven.** A blocking gen2 collection plus
    `EmptyWorkingSet` ran every 60 s forever while hidden, whether or not
    anything had accumulated; it now runs only when the managed heap has grown
    at least 8 MB since the last trim. The trim on minimize is now compacting
    (the visual tree has just been released, so it is the one moment worth it).
11. **Handle leak fixed**: `Process.GetCurrentProcess().Handle` allocated a
    `Process` object and an OS handle on every trim and disposed neither. The
    `GetCurrentProcess()` pseudo-handle needs no release.
12. `_isTrayMode` is now written with `Volatile.Write`, pairing with the
    `Volatile.Read` the polling loop already used.

### History (`Services/RealTimePowerHistoryService.cs`)
13. `AddRecord`/`ClearHistory` take the same-thread path directly when the
    caller is already on the UI thread (it always is), instead of building a
    `DispatcherOperation` for it.

### CI
- `.github/workflows/ci.yml` also builds `claude/**` branches, so this work is
  compiled and tested on `windows-latest` like `agent/**` is.

### Verification status
**Not built or run in this environment** — the session is Linux and this is a
Windows-only WPF project with no .NET SDK present. Correctness rests on review
plus the Windows CI job; the numbers above are the measurement notes already
recorded in this file for the same code paths, not fresh measurements. Someone
on Windows should run `dotnet build WinBatLens.sln -c Release -warnaserror`,
`dotnet test`, and then confirm on a live machine that the dGPU utilisation row
still tracks load and that battery voltage/temperature still read.

### Not done, deliberately
- **`InvariantGlobalization`** would drop the ICU data from the single-file
  bundle and shrink both the download and the mapped footprint noticeably, but
  it changes culture-sensitive formatting for every user and could not be
  verified here. Worth measuring on Windows before taking.
- **Replacing `PerformanceCounterCategory.ReadCategory()` with raw PDH.** That
  call is now the app's dominant allocator (~1,200 `InstanceData` objects and
  as many fresh strings per sweep on a machine with ~600 GPU Engine instances).
  `PdhGetFormattedCounterArray` would avoid the managed objects entirely, but it
  is a rewrite of the load-measurement path and not something to land unverified.


## v1.1.2 quality upgrade merged and released

The requested quality pass was committed as `94ace83` on
`agent/v1.1.2-quality`, reviewed in PR #2, and merged into `main` as
`4dc33ad49c87c74eaf1ce920de164f61497da04d`. The existing v1.1.1 branch was
reviewed and merged first through PR #1. The public v1.1.2 release is:
https://github.com/nojackno2-ctrl/WinBat-Lens/releases/tag/v1.1.2

The worktree was clean before this pass;
all changes below are part of the same intended v1.1.2 scope:

- `WinBatLens.csproj`, `build-release.ps1` and `installer/WinBatLens.iss` now
  target .NET 10 and version 1.1.2.
- `MainWindow.xaml.cs` no longer performs `GetCurrentPowerState()` from a
  `DispatcherTimer`. A background cancellation-aware loop reads hardware and
  dispatches completed snapshots to WPF. Visible polling remains 1 second;
  tray polling is 5 seconds; working-set trimming remains once per minute while
  hidden.
- `tests/WinBatLens.Tests` contains four parser tests covering normal parsing,
  driver overlays, mAh/mWh guards and no-battery diagnostics. The legacy
  solution includes the test project.
- `.github/workflows/ci.yml` restores, builds with `-warnaserror`, tests and
  publishes on Windows using .NET 10.
- `README.md` and `release_notes.md` now describe only measured or explicitly
  estimated values and no longer claim adapter wattage.
- `build-release.ps1` accepts `WINBAT_SIGNING_CERTIFICATE` and optional
  `WINBAT_SIGNING_PASSWORD`, signs all generated EXEs with SHA-256 plus an RFC
  3161 timestamp, and verifies them with SignTool. Without a certificate it
  completes packaging with an explicit unsigned warning.

Validation completed locally: Debug and Release solution builds pass with 0
warnings / 0 errors; all 4 parser tests pass; PowerShell syntax parses; .NET 10
single-file publish and Inno Setup v6.7.3 produce all three v1.1.2 artifacts.
Both Windows CI runs on PR #2 passed before merge. The public EXEs are
unsigned because no Authenticode certificate was provided; the GitHub Release
notes state this explicitly.

| asset | bytes | SHA-256 |
|---|---:|---|
| `WinBatLens_v1.1.2_Portable_x64.exe` | 85,571,647 | `B77EDC64BF082C8F0B6395867DF73E68D9EE970D3A7F7FF3CB4A297559EA5AC1` |
| `WinBatLens_v1.1.2_Portable_x64.zip` | 79,387,508 | `33F5C7A88AD738143DE7FBDFD46E328120C558F1DBBF6174FE67711977E1758C` |
| `WinBatLens_v1.1.2_Setup_x64.exe` | 80,116,571 | `5DE2BE07A051682F67FC1249945F50CF662BCE159931C2686FC831B08BE207AF` |

## v1.1.1 GitHub release published

The shared-scale waveform change and 1.1.1 version metadata were committed as
`a4823aaca108d6502b4f4be1e935889fb60e3d9c` on branch
`agent/unify-waveform-scale-v1.1.1`; draft PR #1 targets `main`. Annotated tag
`v1.1.1` resolves exactly to that built commit. The final public, non-prerelease
GitHub Release is the repository's latest release:
https://github.com/nojackno2-ctrl/WinBat-Lens/releases/tag/v1.1.1

`build-release.ps1` needed process-scoped `-ExecutionPolicy Bypass` because the
machine blocks scripts by default. The final package passed Release publish,
Inno Setup compilation, asset name/count, file-version, ZIP-content and an
8-second hidden startup smoke test. GitHub reported the same SHA-256 digests as
the local files:

| asset | bytes | SHA-256 |
|---|---:|---|
| `WinBatLens_v1.1.1_Portable_x64.exe` | 80,454,959 | `3B0B3513ABD37F2EB3552002FD4F7DE695416709F66E29981A6A50BDD7E8279A` |
| `WinBatLens_v1.1.1_Portable_x64.zip` | 74,454,105 | `553F082B27CB8F9F281F196FC409F86F3F146F1B0F1B4BBC5394F2BD01EDEC33` |
| `WinBatLens_v1.1.1_Setup_x64.exe` | 75,315,533 | `E60F1A7C8C730AFD9E1B49584258B72FAF4BBB450A549F1CF8A138A440C8436E` |

Both EXEs remain unsigned, the repository's known distribution limitation.
The GitHub App could not create the PR (`403 Resource not accessible by
integration`), so the authenticated `gh` fallback was used as prescribed.
After the final documentation push, local and remote branch heads matched;
`gh pr checks 1` reported no checks on the branch, so there was no remote CI run
to await or claim as passed.

## Waveform dGPU / battery shared scale (current task)

The 60-second waveform now plots discharge, charge and measured dGPU power
against one shared wattage maximum. The separate blue right-hand dGPU axis was
removed: equal wattages now always appear at equal heights, so pack draw and
dGPU draw can be compared directly. The scale retains the 35 W floor and 15%
headroom, but its peak is calculated across all three series.

Changed: `MainWindow.xaml` and `MainWindow.xaml.cs`. Debug and Release builds
both pass with 0 warnings / 0 errors. Source assertions confirm that all former
right-axis names are gone and `GpuW` is divided by the same `maxPowerW` used by
charge/discharge. Live visual QA was attempted with Windows app automation, but
launch approval timed out before the Debug executable opened; no new rendered
screenshot was obtained in this task.

## USB-C charging: what the dashboard can and cannot say (latest)

Asked to report "USB charging wattage" while the machine was running off a
USB-C charger. **The adapter's own wattage is not obtainable**, and that was
established against this hardware rather than assumed. Every avenue, and how it
closed:

| avenue | result |
|---|---|
| UCM-UCSI ACPI device (`ACPI\USBC000\0`) | present, but its two device interfaces (`{4cedf9cf-…}`, `{ae05a169-…}`) appear nowhere in the public SDK — driver-to-driver only, no documented user-mode IOCTL |
| `BATTERY_USB_CHARGER_STATUS` (poclass.h) | carries the PD contract flag, port mA and mV — but travels in the `IOCTL_BATTERY_SET_INFORMATION` direction, pushed in by a Charging Arbitration Driver this class of laptop does not have. No matching query level exists |
| `POWER_ADAPTER_STATUS.MaxOutputPower` | the rated wattage, but batclass.h exposes it only through a **kernel-mode** adapter miniclass callback. The generic Microsoft AC Adapter driver on `ACPI\ACPI0003` does not provide it |
| battery Customized I/O (`IOCTL_QUERY_CUSTOMIZED_IO_CAPABILITIES`) | the OEM escape hatch. Answers `SupportedInputs = 0`, `SupportedOutputs = 0` — nothing exposed |
| ASUS ATK WMI (`AsusAtkWmi_WMNB`, `DSTS`) | knows the charge source on a ROG machine, but every query is access-denied unelevated, and this app runs unelevated by design |

So no adapter figure is shown anywhere, consistent with the rest of the
dashboard: an unobtainable number is left out, not estimated.

### What was added instead — both measured

**1. `PowerSupplyService` — `Windows.System.Power.PowerManager.PowerSupplyStatus`.**
The one documented, unelevated thing Windows will say about the *supply* rather
than the pack: `Adequate` / `Inadequate` / `NotPresent`. `Inadequate` is exactly
the under-powered-PD-charger case.

Reached by **raw WinRT activation** (one IID, one vtable slot) rather than the
C# projection on purpose: the projection needs a `net8.0-windows10.0.x` target,
which drags the SDK projection assembly into a single-file bundle whose size and
cold-start time this project measures and tunes. Measured cost of a read:
**0.022 us**, so it is taken fresh every tick with no caching. Verified by
compiling the real shipping file into a harness and driving it exactly as the
app does — activate on a background warmup thread, read from an STA thread.

**2. Plugged in and still discharging is no longer invisible.**
This was a real bug. `RealTimePowerService` only ever read a discharge rate when
`!IsAcOnline`, so a charger that cannot keep up — the pack draining *while the
cable is in* — fell into the "on AC, battery idle" branch and rendered as
`-- W` / "市電直供 | 電池未充放電". That is the single most useful number when
charging over USB-C, and the app was hiding it.

Now `IsChargerDeficit` is set, the shortfall is reported as a measured
`DischargeRateW`, and it flows through consistently: the headline number, the
badge (rose — the pack really is being spent, so green would be a lie), the
60-second chart's discharge trace, the tray icon digits and the tray tooltip.
Time-remaining in that state comes from pack energy over the measured deficit.
`BatteryCurrentA` also follows the real direction of flow now, instead of
assuming AC means charging.

Colour discipline from the previous commit is preserved: rose = power leaving
the pack, emerald = charging, amber = the supply warning (a caution, not a rate).

## The 1-second tick cost a third of a CPU core

Measured on this machine, window open, steady state: **374 ms of CPU per
second — 37.4% of one core, burned continuously by a battery monitor.** It is
now **30 ms/s (3.0%)**, a ~12x cut, with no change to a single displayed value.

### Where it went: one PerformanceCounter per GPU Engine instance

`RealTimePowerService.GetDualGpuUsage` held a `PerformanceCounter` per GPU
Engine instance and called `NextValue()` on each one, every tick. Every such
call re-reads the *whole* category's performance data block, so the loop was
quadratic in the instance count — and Windows exposes one GPU Engine instance
per process per engine type. **This machine has ~600.**

| per tick | wall | CPU |
|---|---|---|
| old: `GetInstanceNames()` + ~600 x `NextValue()` | 354-925 ms | 354-925 ms |
| new: 1 x `ReadCategory()` | 1-7 ms | ~0-30 ms |

`ReadCategory()` takes one snapshot of every instance in a single pass.
The per-instance rate is then computed from the previous tick's raw sample with
`CounterSampleCalculator.ComputeCounterValue`, which is exactly what
`NextValue()` was doing internally — so the cached `PerformanceCounter` objects
became a `Dictionary<string, CounterSample>` of last tick's raw samples. The
"two samples over an interval or the value is always 0" constraint that the old
cache existed to satisfy is unchanged; only the thing being cached moved.

Verified rather than assumed: both implementations were run against the same
category over the same intervals and compared per adapter, per engine type.
Same adapters, same engine types, agreement within 0.35 percentage points
(pure sampling noise — the two reads cannot be simultaneous). Driving the real
shipping method afterwards showed the dGPU tracking a live load through 0.4%,
16.5%, 33.2%, so nothing is stuck at zero.

Worth knowing: this was running on the **UI thread**, in `DispatcherTimer.Tick`.
The 1-second timer could not always finish its own tick.

Counter names come back localised on some systems, so the English
`"Utilization Percentage"` key is a preference with a substring search behind
it, and a missing counter reports 0% rather than a guess.

### And: sweeping sensors nothing reads

`HardwareSensorService` polled the CPU, dGPU, iGPU and battery once a second.
Only `DgpuPackageW` and `BatteryVoltageV` are ever read by anything — grep the
solution and `CpuPackageW`, `CpuTempC` and `IgpuPackageW` had no consumer at
all. `IHardware.Update()` is not free (measured, per sweep: dGPU 15.6 ms of CPU,
iGPU 6.2 ms, CPU 3.1 ms, battery 0.0 ms), so those were ~9 ms/s of pure waste.
The dead properties are gone along with the sweeps.

The CPU group is now also off in the `Computer` config. It is the part that
loads the ring-0 driver, and RAPL reads 0 W unelevated here anyway — the UI has
not reported CPU package power since the linear estimate was removed. Side
benefit: **`Computer.Open()` went from 934 ms to 248 ms**, off the warmup
thread, and the process no longer loads a kernel driver.

Both configurations were opened side by side to confirm the two values that
matter are identical with the CPU group disabled: GPU Package 23.3 W / 25.9 W
(live load), battery voltage 15.83 V in both.

### Still on the table

- The 1 Hz sensor sweep (~16 ms/s) is the largest single item left. It could
  drop to 2 s while minimized to the tray, at the cost of halving the
  resolution of the history the app records while hidden — a product call, not
  a performance one, so it was left alone.
- First call to `GetDualGpuUsage` still costs ~1 s (DXGI enumeration plus the
  cold registry blob). That already happens on the warmup thread via
  `RealTimePowerService.Initialize()`, never on the first UI tick.

## IOCTL_BATTERY_QUERY_INFORMATION: the third battery IOCTL

`BatteryTelemetryService` only ever used two of the battery class driver's
IOCTLs — `QUERY_TAG` and `QUERY_STATUS`. `QUERY_INFORMATION` was untouched, and
it holds most of what the driver knows. It is now wired in, along with the
`Capacity` field of `BATTERY_STATUS`, which was being read into the struct and
then thrown away.

Everything here is a direct device IOCTL on the handle the service already
holds: no elevation, no WMI, no COM.

### What this machine actually reports (ASUS ROG, unelevated)

| level | result |
|---|---|
| `BatteryInformation` — design/full capacity | ✅ 75,998 / **56,032** mWh |
| `BatteryInformation` — chemistry | ✅ `LIon` |
| `BatteryInformation` — cycle count | ❌ 0, firmware does not keep the tally |
| `BatteryDeviceName` / `BatteryManufactureName` | ✅ `ASUS Battery` / `ASUSTeK` |
| `BatteryManufactureDate` | ❌ not implemented |
| `BatteryTemperature` | ❌ not implemented |
| `BatteryEstimatedTime` | ❌ unknown on AC (untested on battery) |
| `BATTERY_STATUS.Capacity` | ✅ 56,032 mWh, real resolution |

`BatteryUniqueID` (level 7) is deliberately **not** queried: this firmware
answers with `ASUSTeKASUS Battery`, its manufacturer and device name
concatenated rather than a serial number, and nothing in the UI wants it.

Levels that fail three times in a row are given up on (`MaxLevelFailures`), so
an unimplemented one costs three IOCTLs at start-up and nothing afterwards.
The counters and the cached pack info reset on device re-open, since a
different pack may implement a different set. Pack information is re-read once
a minute (`PackInfoRefreshMs`) because capacities and cycle count drift over
hours; the strings and the manufacture date are carried over rather than
re-queried, as those never change for a given pack.

### The report's capacities were stale, and the driver's are not

The full-charge capacity in the powercfg report is a snapshot Windows logged
earlier. Measured side by side: **report 55,969 mWh, driver 56,032 mWh.**
`BatteryReportParser.Parse` now takes an optional `PackInfo` and prefers the
driver's figures, which moved the health score from 73.6% to 73.7% and the
capacity loss from 20,029 to 19,966 mWh. It also means a machine whose report
is empty or unparseable still gets a real health score instead of being
reported as having no battery at all.

Two guards on the overlay: a `BATTERY_CAPACITY_RELATIVE` pack reports
capacities in its own arbitrary units, and a report in mAh cannot be mixed with
the driver's mWh — in both cases the report's own numbers are left alone. And
`BtnOpenReport_Click` passes **no** pack info, because a hand-picked HTML report
may well come from another machine, where this machine's pack would be
describing someone else's battery.

### A hard-coded 56 Wh pack size is gone

Time-to-full was `(100 - percent) / 100 * 56.0 / chargeRate` — this laptop's
capacity baked into the formula and wrong on every other machine. It is now
`(FullChargedCapacity - Capacity) / chargeRate`, both terms measured.

Remaining runtime gained two fallbacks behind Windows' own forecast, which
reports nothing for the first minute or so after unplugging and used to leave
the card stuck on "估算中...": the driver's `BatteryEstimatedTime`, then
remaining watt-hours over the present draw. The second is arithmetic on two
pack measurements, not a utilisation curve.

State of charge now comes from `Capacity / FullChargedCapacity` where it used
to be an integer percentage from `GetSystemPowerStatus`, so a charge limit that
stops at 95% reads as 95.0% rather than being rounded into "full".

### Temperature, honestly this time

An earlier version displayed the CPU thermal zone as the battery's temperature
(see the v1.0.3 notes below). `BatteryTemperature` is the pack's own sensor and
is now read properly — converted from tenths of a degree Kelvin, with values
outside -40..80 °C rejected, since firmware that does not really implement the
level tends to answer 0 and would otherwise render as a plausible -273 °C.

**This machine does not implement it.** The code stays because it is correct
and nearly free on hardware that does. Nothing shows a permanent "--": the
telemetry card's subtitle carries the temperature when it exists and says
"此電池未回報溫度" when it does not. Same rule for the manufacture-date row and
the new energy row — both hide themselves rather than occupying the UI with
placeholders.

### UI

- **Specs card**: a 出廠日期 row (date plus age in years), collapsed unless the
  driver supplies one. Design/full capacity, chemistry and cycle count now fall
  back to (in fact prefer) driver values. Cycle count was hard-coded Chinese
  "次" even in English mode; localised.
- **Remaining-time card**: the level line appends real energy —
  `目前電池剩餘電量: 100% · 56.0 / 56.0 Wh`.
- **Telemetry card**: subtitle now states where the voltage came from, and
  carries pack temperature when available.
- **Hardware breakdown**: new 電池蓄電量 row — `56.0 / 56.0 Wh`, `真實 SoC
  100.0%`, subtitle `滿電 56.0 / 76.0 Wh，健康度 73.7%`. The neighbouring
  telemetry row's subtitle claimed "WMI & ACPI"; voltage comes from the IOCTL,
  so it now says so.
- **Diagnostics**: a blank cycle count now gets an explicit item saying the
  firmware does not implement the field rather than that the read failed, and a
  battery-age item appears when the manufacture date exists.

### Verification

Values above were read from a probe project compiling `BatteryTelemetryService`,
`BatteryReportParser` and the models directly, and the rendered UI text was read
out of the running app's UI Automation tree. **Not exercised on this hardware:**
the charge-time formula and both runtime fallbacks need the machine on battery
or charging; their inputs are all confirmed readable.

### Two fixes that came out of looking at the running app

**The chart briefly used independent battery and dGPU axes.** That made small
battery changes easier to see beside a heavily loaded GPU, but identical chart
heights represented different wattages and could not be compared directly. Per
the latest product decision, discharge, charge and dGPU are back on one shared
W axis. A high peak may make a smaller trace flatter, but its height now always
has one unambiguous wattage meaning.

**Whole-number health percentages did not line up.** `CapacityHistoryItem.
HealthPercent` is a double, so 74.0 rendered as `74` in a column of `73.6`s. The
binding now carries `StringFormat={}{0:F1}`.

## Compact UI pass (on top of v1.0.8)

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

## The palette, second pass (v1.0.9)

v1.0.8 (below) made each concept use one colour, but the colours themselves were
arbitrary — amber for discharge, cyan for the dGPU — and half the dashboard was
outside the rule: the hardware rows, the runtime estimate and the health card
were painted whatever looked good when they were written. So the page still had
green meaning "charging" in one card and "power plan" in another, and a pack at
73.6% health wore the same green ring as one at 100%.

The rule now is **what the colour means, not what it labels**:

| colour | meaning | where |
|---|---|---|
| red `#F43F5E` | spending power | discharge headline + badge, discharge line/legend, V\|A while draining, performance power plan, dGPU under load, tray icon |
| green `#10B981` | gaining or saving power | charge headline + badge, charge line/legend, V\|A while charging, saver power plan, idle dGPU, tray icon |
| blue `#3B82F6` | the dGPU series | chart line, legend, the dGPU row's wattage |
| amber `#F59E0B` | a battery figure that is neither | stored energy, runtime estimate, balanced power plan, pack idle on AC |

Defined once as `PowerDischarge` / `PowerCharge` / `PowerGpu` / `PowerNeutral` in
`App.xaml`, and repeated as frozen brushes in `MainWindow.xaml.cs` because a
brush assigned on a timer tick cannot come from a `StaticResource` lookup — the
two lists have to be kept in step by hand.

What moved off green: the runtime estimate and the `PowrProf` power-plan row,
both of which were emerald for no reason and diluted "charging". The energy row
moved off cyan for the same reason with respect to the dGPU.

Health is graded rather than fixed: `HealthGood` / `HealthWarn` / `HealthDanger`
on the same 80% / 60% cuts `BatteryReportParser` uses for the status wording, so
the ring, the number, the badge and the label can never disagree. Per-row grading
in the capacity-history list goes through `HealthGradeBrushConverter`
(`ConverterParameter=Badge` returns the translucent pill fill).

While colouring the ring it turned out `RingHealthProgress` **had never drawn at
all**: `StrokeDashArray="360" StrokeDashOffset="360"` put the whole circle inside
the dash pattern's off half, so the only visible circle was the `#1E293B` track
behind it. It now draws a real arc — dash units are multiples of the stroke
thickness, so the 84px circle is `π × 84 / 8 ≈ 33` units and the dash is that
times the health fraction, followed by a gap long enough never to repeat. The
ellipse carries a `-90°` `RotateTransform` so the arc starts at twelve o'clock
instead of three.

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
| on battery | `-48.9 W` (red) |
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
