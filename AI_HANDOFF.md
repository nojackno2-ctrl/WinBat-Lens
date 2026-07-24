# Project State & Handoff

## Current Objective
Implement dynamic real-time System Tray Icon wattage rendering in C# WPF:
- Renders live net wattage (e.g. `+38W` or `-18W`) directly onto the 32x32 system tray icon.
- Renders in **Green (`#10B981`)** when AC Charging > Consumption.
- Renders in **Red (`#EF4444`)** when Discharging / Power draw > AC.

## Project Status
- User requested: "我要新增一個功能在工具列可以做一個小圖示顯示當前耗電總瓦數，充電大於耗電顯示綠色數字跟瓦數，反之紅色字體"

## Next Steps
1. Create `Services/DynamicTrayIconService.cs`:
   - Generates a 32x32 icon bitmap dynamically containing the wattage number.
   - Handles text formatting (`+38W` or `-18W`), background badge, text alignment, and proper `DestroyIcon` GDI cleanup to prevent leaks.
2. Integrate into `MainWindow.xaml.cs`:
   - Call `DynamicTrayIconService.UpdateTrayIcon(_notifyIcon, state)` every 1s during `UpdateLivePowerUI()`.
3. Verify build with `dotnet build WinBatLens.csproj`.
4. Commit updates to Git.
