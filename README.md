# ⚡ WinBat Lens

> **Modern Windows Battery Diagnostics & Real-Time Full-System Hardware Power Monitoring Dashboard.**  
> **Windows 電池健康診斷與全系統硬體功耗即時監測儀表板。**

![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D4?style=for-the-badge&logo=windows&logoColor=white)
![Language](https://img.shields.io/badge/Language-C%23%20%2F%20WPF-239120?style=for-the-badge&logo=c-sharp&logoColor=white)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)
![Release](https://img.shields.io/badge/Release-v1.0.0-blue?style=for-the-badge)

---

## 📖 簡介 (Overview)

**WinBat Lens** 是一款專為 Windows 10 及 11 筆記型電腦與桌上型電腦打造的深色科技風 **電池健康度診斷與全系統硬體功耗即時監測儀表板**。

它不僅能解析 Windows 原生 `powercfg /batteryreport` 電池健康報告，更能以 **1 秒週期動態測量** 全系統硬體元件（CPU、獨立顯卡 dGPU、內建顯示晶片 iGPU、螢幕背光、Wi-Fi 網卡、SSD/HDD 磁碟、RAM 記憶體與主機板）的即時瓦數與負載，並將動態瓦數即時渲染懸浮於 Windows 工作列通知區域（系統托盤）中！

---

## ✨ 核心特色 (Key Features)

### 🔋 1. 電池健康度深度診斷 (Battery Health Diagnostics)
- **健康度百分比圓環 (Health Score Ring)**：直觀呈現目前滿電容量與原廠設計容量之比率。
- **容量損耗與循環次數**：精準計算損耗容量 (mWh) 與充放電循環次數。
- **📉 容量歷史衰退紀錄 (Capacity Degradation History)**：自適應滿版比例表格，輕鬆追蹤歷年滿電容量變化趨勢。

### ⚡ 2. 全系統硬體功耗即時分拆 (Full System Hardware Power Breakdown)
以 1 秒週期動態測量與計算各元件功耗：
- 🔲 **CPU 處理器**：動態負載與功耗 (W)
- 🎮 **獨立顯示卡 (dGPU)**：自動識別 NVIDIA GeForce RTX / AMD Radeon RX / Intel Arc 獨立顯卡，監測 VRAM 與高效能運算/待機省電狀態
- 🖼️ **內建顯示晶片 (iGPU)**：Intel Iris Xe / AMD Radeon Graphics 負載與功耗
- 🖥️ **螢幕面板與背光**：自動讀取面板亮度與背光耗電
- 📶 **Wi-Fi / 藍牙網卡**：即時網路吞吐量 (KB/s) 與無線通訊功耗
- 💾 **硬碟 (SSD / HDD)**：即時讀寫吞吐量 (MB/s) 與磁碟功耗
- 🧠 **記憶體 (RAM) 匯流排**：系統記憶體佔用率與 Bus 功耗
- 🔌 **主機板與 USB 週邊**：晶片組與匯流排基礎功耗

### 📈 3. 60 秒工作管理員風格動態波形圖 (Task Manager Style Live Graph)
- 具備背景網格線與 5 階 **Y 軸動態瓦數座標**（0%, 25%, 50%, 75%, 100% Max Wattage）。
- **雙軌獨立走勢線**：
  - 🟢 **充電走勢線 (Emerald Green `#10B981`)**：顯示 AC 注入電池之充電功率 (+W)
  - 🔵 **放電走勢線 (Cyan `#38BDF8`)**：顯示電池放電功率 (-W)
  - 🟣 **CPU 負載線 (Purple)** & 🟡 **獨顯負載線 (Amber)**

### 🔌 4. AC 變壓器總供電與雙軌拆解 (Total AC Adapter Input Power)
當連接 AC 充電器時，精準拆解並顯示：
$$\text{AC 變壓器插座總供電瓦數} = \text{電池充電瓦數 (+W)} + \text{全系統硬體即時耗電 (W)}$$

### 🟢🔴 5. 工作列懸浮雙色即時瓦數圖示 (Dynamic System Tray Wattage Icon)
- **100% 懸浮特大號整數**：工作列右下角托盤圖示直接懸浮顯示當前瓦數數字（如 `38` 或 `16`），無邊框無死角。
- **雙色語義切換**：
  - 🟢 **綠色數字 (`#10B981`)**：AC 充電中或滿電市電直供 (AC Input)
  - 🔴 **紅色數字 (`#EF4444`)**：電池放電中 (Discharging)

### 🌐 6. 一鍵雙語切換 (1-Click Dual-Language)
- 支援 **繁體中文 (Traditional Chinese)** 與 **English** 介面即時動態無縫切換。

### 🚀 7. 開機自動啟動與托盤常駐 (Auto-Startup & Tray Minimize)
- 支援登錄檔一鍵開機自啟動，最小化自動縮至系統托盤背景常駐監測。

---

## 📥 下載與執行 (Download & Execution)

### 免安裝單一執行檔 (Portable Executable)
您可直接下載編譯好的單一免安裝執行檔（約 3.1 MB），雙擊即可於任何 Windows 10 / 11 電腦執行：

👉 **[下載 WinBatLens.exe (v1.0.0 Release Build)](./publish/WinBatLens.exe)**

---

## 🛠️ 開發與編譯指南 (Building from Source)

### 系統需求 (Prerequisites)
- **作業系統**：Windows 10 / 11 (64-bit)
- **開發環境**：Visual Studio 2022 (建議安裝 .NET 桌面開發工作負載) 或 .NET 8.0 SDK

### 編譯步驟 (Build Steps)

```bash
# 1. 複製專案儲存庫 (Clone repository)
git clone https://github.com/nojackno2-ctrl/WinBat-Lens.git
cd WinBat-Lens

# 2. 建置專案 (Build project)
dotnet build WinBatLens.csproj -c Release

# 3. 發布單一免安裝執行檔 (Publish Single-File Executable)
dotnet publish WinBatLens.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./publish/
```

---

## 🔬 技術架構與 API (Tech Stack & Architecture)

- **UI 框架**：C# .NET 8.0 WPF (`net8.0-windows`)，採用自訂 Dark Glassmorphism 科技感設計系統。
- **系統 API & WMI**：
  - `kernel32.dll` -> `GetSystemPowerStatus`（電源狀態、電池剩餘時間）
  - `root\CIMV2` -> `Win32_Battery` & `Win32_VideoController`（電池 Charge/Discharge Rate 與獨立/內建顯卡規格）
  - `root\WMI` -> `WmiMonitorBrightness`（螢幕背光亮度）
  - `PerformanceCounter` -> `Processor`, `PhysicalDisk`, `GPU Engine`
  - `System.Net.NetworkInformation` -> `NetworkInterface`（即時網卡傳輸流量）
  - `user32.dll` -> `DestroyIcon`（Win32 GDI 記憶體安全管理）

---

## 📜 授權條款 (License)

本專案採用 **[MIT License](./LICENSE)** 授權條款釋出。歡迎自由使用、修改與二次分發。
