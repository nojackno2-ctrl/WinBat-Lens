# Project State & Handoff

## Current Objective
Synchronize the tray icon wattage with the main dashboard card (`AcTotalInputW` when AC connected, `DischargeRateW` when on battery):
- When AC connected: Tray Icon renders `Math.Round(AcTotalInputW)` (e.g. `28.9W` -> `29`) in Green (`#10B981`).
- When on Battery: Tray Icon renders `Math.Round(DischargeRateW)` (e.g. `15.7W` -> `16`) in Red (`#EF4444`).

## Project Status
- User submitted screenshot pointing out that the main dashboard card showed `28.9W` while the tray icon showed `12` ("資訊不一樣" - Information doesn't match!).

## Next Steps
1. Update `Services/DynamicTrayIconService.cs`:
   - Change AC mode to render `state.AcTotalInputW` so it matches `28.9W` -> `29` on the main card.
   - Retain battery mode rendering `state.DischargeRateW`.
2. Verify build with `dotnet build WinBatLens.csproj`.
3. Commit updates to Git.
