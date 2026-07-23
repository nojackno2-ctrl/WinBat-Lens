# Project State & Handoff

## Current Objective
Add AC charging wattage (+W), charging speed mode (Fast Charging, Trickle Charging, Fully Charged Pass-through), and AC charger status to `RealTimePowerService.cs` and WPF UI in WinBat Lens.

## Project Status
- Task manager style waveform graph, system tray, app icon, full system hardware breakdown, and event history completed.
- User requested: display AC charging wattage (充電瓦數) and charging status details when AC adapter is connected.

## Active Problems / Needs Clarification
- None.

## Next Steps
1. Update `Models/BatteryReportData.cs` to add `ChargingRateW`, `IsCharging`, `ChargingStatusText` to `RealTimePowerState`.
2. Update `Services/RealTimePowerService.cs` to query WMI `ChargeRate` or estimate charging wattage based on battery % charging curve.
3. Update `MainWindow.xaml` and `MainWindow.xaml.cs` to display glowing AC charging wattage (+W) and charging status badges.
4. Verify build with `dotnet build WinBatLens.csproj`.
5. Commit updates to Git.
