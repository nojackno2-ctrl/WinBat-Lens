# Project State & Handoff

## Current Objective
Implement continuous real-time hardware power and battery charge/discharge event history logging (即時功耗與電池充放電歷史紀錄) in WinBat Lens C# WPF app.

## Project Status
- System tray, app icon, full hardware power breakdown, and discrete GPU detection completed.
- Adding `RealTimePowerHistoryService.cs` to automatically sample, record, and persist:
  1. Live power consumption snapshots (Discharge rate W, CPU %, dGPU %, Screen W, Battery %).
  2. Battery charge/discharge state transition events (AC plug-in, battery discharge, high drain warnings).
  3. Exporting history to CSV/JSON format.
- Adding UI tab/panel for **「📉 即時功耗與充放電歷史紀錄」**.

## Next Steps
1. Create `Models/PowerHistoryRecord.cs`.
2. Create `Services/RealTimePowerHistoryService.cs` for automated sampling, event detection, and CSV export.
3. Update `MainWindow.xaml` with historical power & battery event list view and controls.
4. Update `MainWindow.xaml.cs` to record history on 1s timer ticks and bind to UI.
5. Verify build with `dotnet build WinBatLens.csproj`.
6. Commit updates to Git.
