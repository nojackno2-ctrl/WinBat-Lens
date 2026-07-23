# Project State & Handoff

## Current Objective
Eliminate the scrollbar in the left column of `MainWindow.xaml` by compacting card heights, reducing ring gauge diameter (100x100), removing `<ScrollViewer>`, and sizing all 3 left cards to fit naturally within the window height.

## Project Status
- User requested: "調整各欄位的大小，我要左側欄都不要有拉桿" (Adjust card sizes so the left sidebar has NO scrollbar at all).

## Next Steps
1. Update `MainWindow.xaml`:
   - Replace Left Column `<ScrollViewer>` with a direct `<Grid>`/`<StackPanel>` (`VerticalAlignment="Stretch"`).
   - Compact Health Score ring gauge to `105x105` with `FontSize="32"`.
   - Compact Battery Specs list margins and padding to `12`.
   - Set Capacity History ListView height to `140` with `12` padding.
2. Verify build with `dotnet build WinBatLens.csproj`.
3. Commit updates to Git.
