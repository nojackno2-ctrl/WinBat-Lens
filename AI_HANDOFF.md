# Project State & Handoff

## Current Objective
Add real-time hardware power consumption monitoring (即時耗電與放電功率監測) to WinBat Lens C# WPF application.

## Project Status
- UI contrast fixes and C# .NET 8 WPF architecture completed.
- Adding `RealTimePowerService.cs` (P/Invoke `GetSystemPowerStatus`, WMI `Win32_Battery`, and `PerformanceCounter` for live CPU / RAM / Battery Discharge Rate in mW).
- Updating `MainWindow.xaml` to include a new **「⚡ 即時耗電與系統監測」** live dashboard card with auto-refresh timer (`DispatcherTimer` 1s tick).

## Active Problems / Needs Clarification
- None.

## Next Steps
1. Create `Services/RealTimePowerService.cs` with P/Invoke & WMI hardware metrics.
2. Update `MainWindow.xaml` and `MainWindow.xaml.cs` to bind live power metrics.
3. Verify compilation with `dotnet build WinBatLens.csproj`.
4. Commit updates to Git repository.
