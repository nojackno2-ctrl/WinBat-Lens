# Project State & Handoff

## Current Objective
Separate Battery Discharge Wattage (放電功率 W) and AC Charging Wattage (充電功率 +W) into two distinct polylines (Cyan vs Glowing Emerald Green) in the waveform chart in `MainWindow.xaml`.

## Project Status
- User raised concern: Charging power (+W) and Discharging power (-W) were plotted using the same polyline.
- **Solution**:
  - Add `PolylineCharge` (Glowing Emerald `#10B981`) for AC Charging Wattage (+W).
  - Keep `PolylineDischarge` (Cyan `#38BDF8`) for Battery Discharging Wattage (-W).
  - Update legend and data structures to separate `DischargeW` and `ChargeW`.

## Next Steps
1. Update `MainWindow.xaml` to add `PolylineCharge` and update legend labels.
2. Update `MainWindow.xaml.cs` to store `(DischargeW, ChargeW, CpuPct, GpuPct)` in `_chartHistory` and render both polylines independently.
3. Verify build with `dotnet build WinBatLens.csproj`.
4. Commit updates to Git.
