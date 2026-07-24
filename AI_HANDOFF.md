# Project State & Handoff

## Current Objective
Memory-usage optimization of WinBat Lens (C# .NET 8 WPF), following an earlier
full code review + two reliability fixes (documented below).

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
- `publish/WinBatLens.exe` + `.pdb` (~71 MB) are committed in git → `.git` is ~334 MB. Consider `git rm -r --cached publish` + add `publish/` to `.gitignore`.
- Distribution: the self-contained exe is unsigned, so SmartScreen / AV will warn on other machines.
