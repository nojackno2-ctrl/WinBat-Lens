# Project State & Handoff

## Current Objective
Adjust left column width (`370px`) and column widths in the Capacity Degradation History table (`85px`, `60px`, `60px`, `*`) to eliminate text overlap between `75998` and the `73.4%` health badge.

## Project Status
- User submitted screenshot showing `75998` overlapping the green `73.4%` badge because vertical scrollbar squeezed the rightmost columns.
- **Diagnosis**:
  - Left column width `350px` was too narrow when scrollbar is visible.
  - Column 2 (`68px`) right alignment placed `75998` directly against Column 3.

## Next Steps
1. Update `MainWindow.xaml`:
   - Increase Left Column width to `370px` (`<ColumnDefinition Width="370"/>`).
   - Update header and item template ColumnDefinitions to `Width="85"`, `Width="60"`, `Width="60"`, `Width="*"`.
   - Add right margin `Margin="0,0,10,0"` to Column 2 text to prevent collision.
   - Add dark theme ScrollViewer style for the history list.
2. Verify build with `dotnet build WinBatLens.csproj`.
3. Commit updates to Git.
