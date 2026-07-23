# Project State & Handoff

## Current Objective
Move the Capacity Degradation History card below the Real-Time Power & Hardware Load Monitor tab section in `MainWindow.xaml`.

## Project Status
- Previous session completed App Icon, System Tray, and Windows Auto-Startup.
- User submitted screenshot requesting layout change: move `📈 容量歷史數據紀錄` below the `⚡ 全系統硬體功耗分佈` real-time monitoring panel.

## Active Problems / Needs Clarification
- None.

## Next Steps
1. Update `MainWindow.xaml` to swap the grid row positions of TabControl and Capacity History.
2. Verify layout via `dotnet build WinBatLens.csproj`.
3. Commit updates to Git.
