# Project State & Handoff

## Current Objective
Remove the square border box and dark background fill from `Services/DynamicTrayIconService.cs` so that ONLY standalone pure colored numbers (Green/Red) float directly on the Windows taskbar icon.

## Project Status
- User submitted screenshot pointing to the red square border box around the number 1, requesting: "不要有那個框框，單純的數字就好" (Remove the border box, standalone numbers only!).

## Next Steps
1. Update `Services/DynamicTrayIconService.cs`:
   - Remove `g.DrawRectangle` and `g.FillRectangle`.
   - Use `g.Clear(Color.Transparent)` to make background 100% transparent.
   - Maximize font size to `15-18pt Bold` for pure standalone numbers.
2. Verify build with `dotnet build WinBatLens.csproj`.
3. Commit updates to Git.
