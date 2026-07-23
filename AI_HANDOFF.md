# Project State & Handoff

## Current Objective
Propose and implement additional hardware power consumption categories (螢幕背光功耗, Wi-Fi 無線網路功耗, 記憶體功耗, 主機板晶片組與外設功耗) to provide the most comprehensive full-system power breakdown in WinBat Lens C# WPF app.

## Project Status
- CPU, dGPU, iGPU, Disk, and RAM monitoring completed.
- Adding Screen Display Brightness Power (螢幕背光), Wi-Fi Wireless Adapter Power (無線網路), and Motherboard Base Power to `RealTimePowerService.cs`.
- Updating `Models/BatteryReportData.cs` and `MainWindow.xaml` to display the full multi-component power breakdown.

## Active Problems / Needs Clarification
- None.

## Next Steps
1. Update `Models/BatteryReportData.cs` with new power fields (ScreenPowerW, WifiPowerW, ScreenBrightnessPercent, WifiThroughputKbps).
2. Update `Services/RealTimePowerService.cs` to query WmiMonitorBrightness and Wi-Fi network throughput.
3. Update `MainWindow.xaml` and `MainWindow.xaml.cs` to show the full hardware power breakdown table and cards.
4. Verify build with `dotnet build WinBatLens.csproj`.
5. Commit updates to Git.
