# Project State & Handoff

## Current Objective
Fix all text overlaps and layout distortions in `MainWindow.xaml` for the Hardware Power Breakdown card and restore clean structured cards.

## Project Status
- User submitted screenshot showing text overlaps in the Hardware Power Breakdown card (e.g. `螢幕面板與背光` overlapping `~6.5W`, `主機板與 USB 外設` description overlapping `~2.5W`).
- **Diagnosis**:
  - Vertical spacing inside `Grid.Column="0"` of each hardware row was compressed.
  - Absence of row background containers caused text to clash visually.

## Next Steps
1. Redesign Hardware Power Breakdown rows in `MainWindow.xaml`:
   - Wrap each hardware item in a clean dark container (`Border Background="#0C1322" CornerRadius="8" Padding="10,10"`).
   - Use explicit row height spacing and clear vertical stack panels so title and wattage text never collide.
   - Set Column 0 `Width="260"`, Column 1 `Width="*"`, Column 2 `Width="160"`.
2. Restore `<ScrollViewer>` to Left Column with auto scrollbar visibility.
3. Verify build with `dotnet build WinBatLens.csproj`.
4. Commit updates to Git.
