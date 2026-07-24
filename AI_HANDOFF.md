# Project State & Handoff

## Current Objective
Fix `Desktop\WinBatLens.exe` crash by embedding `app_icon.png` and `app_icon.ico` as assembly `<Resource>` items loaded via `pack://application:,,,/` URIs instead of looking for loose files in `BaseDirectory`.

## Project Status
- User reported: `"C:\Users\nojac\Desktop\WinBatLens.exe"這是我從github上下載來的，無法啟動` (Downloaded to Desktop, fails to launch!).
- **Diagnosis**: WPF XAML and `InitializeTrayIcon()` attempted to load `app_icon.png` and `app_icon.ico` from disk (`AppDomain.CurrentDomain.BaseDirectory`), which failed when `WinBatLens.exe` was moved to Desktop alone.

## Next Steps
1. Update `WinBatLens.csproj`:
   - Embed `app_icon.ico` and `app_icon.png` as `<Resource>` items.
   - Remove `<None Update="...">` items.
2. Update `MainWindow.xaml`:
   - Change `Icon="app_icon.png"` to `Icon="pack://application:,,,/app_icon.png"`.
   - Change `<Image Source="app_icon.png"/>` to `<Image Source="pack://application:,,,/app_icon.png"/>`.
3. Update `MainWindow.xaml.cs`:
   - Load tray icon directly from embedded assembly streams via `Application.GetResourceStream()`.
4. Rebuild self-contained release executable (`dotnet publish`).
5. Test launching `"C:\Users\nojac\Desktop\WinBatLens.exe"`.
6. Commit, tag v1.0.1, push to GitHub, and update GitHub Releases.
