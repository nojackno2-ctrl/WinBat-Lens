# Project State & Handoff

## Current Objective
Fix the taskbar and system tray icon loading issue so that the custom WinBat Lens icon displays cleanly on the Windows Taskbar, Window Header, and System Tray.

## Project Status
- User reported screenshot showing default generic window icon on the Windows Taskbar ("工具欄的圖示沒有出來").
- **Diagnosis**:
  1. `app_icon.ico` and `app_icon.png` were missing `<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>` in `WinBatLens.csproj`.
  2. `MainWindow.xaml` used `Icon="app_icon.png"` which required WPF pack URI or embedded resource `Icon="app_icon.ico"`.
  3. `MainWindow.xaml.cs` needed explicit `pack://application:,,,/app_icon.ico` resource stream loading for `NotifyIcon`.

## Next Steps
1. Update `WinBatLens.csproj` to include `app_icon.ico` and `app_icon.png` as Resources and set `CopyToOutputDirectory` to `PreserveNewest`.
2. Update `MainWindow.xaml` to use `Icon="app_icon.ico"`.
3. Update `MainWindow.xaml.cs` to set `this.Icon` via `pack://application:,,,/app_icon.ico` and load system tray icon from resource stream.
4. Verify build with `dotnet build WinBatLens.csproj`.
5. Commit fixes to Git.
