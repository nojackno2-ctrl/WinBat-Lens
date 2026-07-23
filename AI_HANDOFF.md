# Project State & Handoff

## Current Objective
Move `📈 容量歷史數據紀錄` (Capacity Degradation History) to the Left ScrollViewer Column (`Grid.Column="0"`) so that the Right Column (`Grid.Column="1"`) containing the Real-Time Monitor Tab gets FULL vertical height.

## Project Status
- User requested layout change: Move Capacity Degradation History to the left sidebar, freeing up full height for the real-time monitoring dashboard on the right.

## Next Steps
1. Update `MainWindow.xaml`:
   - Move `📈 容量歷史數據紀錄` Border into Left ScrollViewer (`Grid.Column="0"`).
   - Remove Row 1 from Right Grid (`Grid.Column="1"`), making Row 0 take `Height="*"`.
2. Verify build with `dotnet build WinBatLens.csproj`.
3. Commit updates to Git.
