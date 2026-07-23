# Project State & Handoff

## Current Objective
Add total AC adapter input wattage (`AcTotalInputW` = Battery Charging Wattage + System Hardware Power Consumption W) to `RealTimePowerService.cs` and `MainWindow.xaml`.

## Project Status
- User requested: display total AC supplied wattage (AC 變壓器總供電瓦數 = 充電瓦數 + 硬體耗電瓦數), not just battery charging wattage.

## Next Steps
1. Update `Models/BatteryReportData.cs` to add `AcTotalInputW`, `TotalSystemHardwareW`, and `AcTotalInputText` to `RealTimePowerState`.
2. Update `Services/RealTimePowerService.cs` to calculate `AcTotalInputW = ChargingRateW + TotalSystemHardwareW`.
3. Update `MainWindow.xaml` and `MainWindow.xaml.cs` to display total AC input wattage and breakdown badges.
4. Verify build with `dotnet build WinBatLens.csproj`.
5. Commit updates to Git.
