# Project State & Handoff

## Current Objective
Separate Integrated GPU (iGPU) and Discrete GPU (dGPU) in the Real-Time Hardware Resource Load Breakdown UI and polling engine in WinBat Lens C# WPF app.

## Project Status
- Full system real-time monitoring and GPU info detection completed.
- Enhancing `RealTimePowerService.cs` to sample `phys_0` (iGPU) and `phys_1` (dGPU) using `GPU Engine` performance counters.
- Updating `MainWindow.xaml` to display separate real-time progress bars and wattages for both **內建顯示晶片 (iGPU)** and **獨立顯示卡 (dGPU)**.

## Active Problems / Needs Clarification
- None.

## Next Steps
1. Update `Models/BatteryReportData.cs` to add `DgpuUsagePercent` and `DgpuPowerW` alongside `IgpuUsagePercent` and `IgpuPowerW`.
2. Update `Services/RealTimePowerService.cs` to sample both physical GPU adapters (`phys_0` vs `phys_1`).
3. Update `MainWindow.xaml` and `MainWindow.xaml.cs` to display both iGPU and dGPU progress bars.
4. Verify build with `dotnet build WinBatLens.csproj`.
5. Commit updates to Git.
