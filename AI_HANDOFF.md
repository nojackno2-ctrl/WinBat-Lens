# Project State & Handoff

## Current Objective
Add dual-language support (Traditional Chinese 繁體中文 ↔ English) with a 1-click language switcher button (`🌐 Language`) in the header bar of WinBat Lens.

## Project Status
- User requested: add an English version / language support ("加入英文版").

## Next Steps
1. Create `Services/LocalizationService.cs` containing UI string dictionaries for Traditional Chinese (`zh-TW`) and English (`en-US`).
2. Update `MainWindow.xaml` to add a `🌐 繁體中文 / English` toggle button in the header bar and assign name keys to text elements.
3. Update `MainWindow.xaml.cs` to apply language dictionary on startup and on language toggle.
4. Verify build with `dotnet build WinBatLens.csproj`.
5. Commit updates to Git.
