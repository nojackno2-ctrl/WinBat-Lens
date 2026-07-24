# Project State & Handoff

## Current Objective
Redesign the bottom-left card (`📈 容量歷史紀錄` Capacity Degradation History) into a sleek, clean, scrollbar-free card with proportional column widths, and fix the remaining `主機板與 USB 外設` text overlap on the right panel.

## Project Status
- User submitted screenshot pointing out that the bottom-left card (`📈 容量歷史紀錄`) looked squished and bad ("左下的欄位也太失敗"), with horizontal scrollbars cutting off the `健康%` column.
- **Diagnosis**:
  - `ListView` with fixed pixel column widths (`90`, `80`, `80`, `55`) inside a `350px` sidebar forced a horizontal scrollbar.
  - The inner ListView container had awkward borders.

## Next Steps
1. Redesign `LvCapacityHistory` in `MainWindow.xaml`:
   - Replace rigid fixed-width `ListView` GridView columns with proportional star widths OR use a clean `ItemsControl` template with dark row borders (`#0C1322`).
   - Columns: `期間 (Period)`, `滿電 (Full Cap)`, `設計 (Design)`, `健康% (Health %)`.
   - 100% fill width without horizontal scrollbars!
2. Fix `主機板與 USB 外設` text overlap in the right panel.
3. Verify build with `dotnet build WinBatLens.csproj`.
4. Commit updates to Git.
