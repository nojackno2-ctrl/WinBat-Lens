# Project State & Handoff

## Current Objective
Add Discrete GPU (獨立顯示卡) detailed hardware information, VRAM capacity, driver version, and dual GPU (iGPU + dGPU) status detection to WinBat Lens C# WPF app.

## Project Status
- Full system real-time hardware power monitoring (CPU, GPU, RAM, Disk) completed.
- Adding `GpuInfo` model & `GpuInfoService.cs` to query WMI `Win32_VideoController` for discrete GPU details (e.g., NVIDIA GeForce / AMD Radeon), VRAM size in GB, driver version, and active/standby state.
- Updating `MainWindow.xaml` UI to display a dedicated **「🎮 顯示卡與獨立顯卡規格」** card.

## Active Problems / Needs Clarification
- None.

## Next Steps
1. Create `Models/GpuInfo.cs` data model.
2. Create `Services/GpuInfoService.cs` to query WMI for discrete GPU & integrated GPU details.
3. Update `MainWindow.xaml` and `MainWindow.xaml.cs` to bind dGPU specs.
4. Verify build with `dotnet build WinBatLens.csproj`.
5. Commit updates to Git.
