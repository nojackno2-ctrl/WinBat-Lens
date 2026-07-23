# Project State & Handoff

## Current Objective
Add Y-axis wattage (W) and percentage (%) coordinate scale labels (Y 軸瓦數與百分比刻度座標) to the Task Manager style waveform chart in `MainWindow.xaml`.

## Project Status
- User requested: waveform graph needs Y-axis coordinate scale showing wattage (瓦數) and percentage.

## Active Problems / Needs Clarification
- None.

## Next Steps
1. Update `MainWindow.xaml` to add Y-axis coordinate text blocks overlaying the left side of the chart container.
2. Update `MainWindow.xaml.cs` in `DrawChartGridlines()` and `RedrawWaveformChart()` to dynamically update Y-axis wattage values (`maxPowerW`, `75%`, `50%`, `25%`, `0 W`).
3. Verify build with `dotnet build WinBatLens.csproj`.
4. Commit updates to Git.
