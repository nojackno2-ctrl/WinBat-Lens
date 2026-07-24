# Project State & Handoff

## Current Objective
Update `Services/DynamicTrayIconService.cs` to display **ONLY pure numbers** (e.g., `38` or `18` or `0`) without letters ("W", "AC", etc.), preventing text wrap and maximizing readability in the system tray.

## Project Status
- User submitted screenshot showing "A" and "C" wrapping vertically in the tray icon, requesting: "顯示數字就好" (Only display the numbers!).

## Next Steps
1. Update `Services/DynamicTrayIconService.cs`:
   - Change `textToDraw` to contain ONLY numeric digits (e.g. `38`, `18`, `0`).
   - Remove "W" and "AC" strings.
   - Set `StringFormatFlags.NoWrap`.
   - Keep Green (`#10B981`) for charging/full and Red (`#EF4444`) for discharging.
2. Verify build with `dotnet build WinBatLens.csproj`.
3. Commit updates to Git.
