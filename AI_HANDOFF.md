# Project State & Handoff

## Current Objective
Expand real-time hardware monitoring in WinBat Lens to include **GPU Utilization & Power** and **Disk (SSD/HDD) Activity & Power**.

## Project Status
- C# .NET 8 WPF application is fully functional.
- Adding GPU monitoring (via Windows `GPU Engine` / `GPU Adapter` PerformanceCounters and GPU power estimation) and Disk monitoring (via `PhysicalDisk` PerformanceCounters `% Disk Time` and Read/Write MB/s throughput).
- Updating `RealTimePowerService.cs`, `BatteryReportData.cs`, and `MainWindow.xaml` UI cards.

## Active Problems / Needs Clarification
- None.

## Next Steps
1. Update `Models/BatteryReportData.cs` to add GPU & Disk metrics.
2. Implement GPU & Disk metrics collection in `Services/RealTimePowerService.cs`.
3. Update `MainWindow.xaml` to add GPU & Disk cards and progress bars.
4. Verify build with `dotnet build WinBatLens.csproj`.
5. Commit updates to Git.
