# ⚡ WinBat Lens

<div align="center">

![Windows](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x64)-0078D6?logo=windows&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10.0%20LTS-512BD4?logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF%20XAML-blue)
![Version](https://img.shields.io/badge/Release-v1.1.7-brightgreen)
![License](https://img.shields.io/badge/License-MIT-green.svg)
![Tests](https://img.shields.io/badge/Tests-20%20Passed-success)

**專為 Windows 筆電與行動裝置設計的高精準度電池健康度診斷、即時充放電功耗與硬體分析儀表板**

[功能特點](#-核心亮點) • [畫面導覽](#-功能分頁與介面導覽) • [立即下載](#-下載與安裝) • [系統需求](#-系統需求) • [開發建置](#-開發與建置) • [授權](#-授權條款)

</div>

---

## 📖 簡介

**WinBat Lens** 是一款輕量、高效且以「精確真實數據」為核心原則的 Windows 電池與硬體功耗監控工具。

許多使用者常遇到電池壽命衰退、插上 PD 充電器卻發現電量越充越少（充放電入不敷出）、或是市面上軟體用公式胡亂推算虛假總功耗等問題。WinBat Lens 透過直接解析 Windows 原生 `powercfg /batteryreport` 報告、呼叫底層 ACPI 電池驅動程式（IOCTL）、讀取 Intel/AMD RAPL 功耗與 NVIDIA/AMD 獨立顯卡感測器，為你提供客觀、未經修飾的即時數據。

---

## 🌟 核心亮點

### 1. 🔋 真實電池健康度與容量分析
- **精準解析系統報告**：深度解析 Windows `powercfg /batteryreport`，讀取原廠設計容量（Design Capacity）與目前滿電容量（Full Charge Capacity）。
- **即時驅動容量校驗**：直接從電池驅動讀取即時電壓、當前剩餘容量與充放電狀態，避免單位（mAh / mWh）混算。
- **直觀健康評級**：自動計算電池健康度百分比，並提供 A+ 至 F 的綜合健康評級。
- **電池硬體細節**：呈現電池序號、製造商、化學成分、循環次數與溫度（若韌體有提供）。

### 2. ⚡ 即時充放電與多硬體功耗監測
- **電池端真實功率**：透過電池驅動 IOCTL 實測流入／流出電池的真實功率（W）。
- **CPU & 獨立顯卡（dGPU）實測**：
  - CPU Package 功耗（透過 RAPL 感測器讀取實測值）。
  - 獨立顯示卡功耗（透過 NVML / ADL 讀取實測瓦數與使用率）。
- **60 秒即時動態波形圖**：充電功率、放電功率與獨顯功耗統一整合在**同一個瓦數（W）Y 軸**，比例一致，一眼看出負載與充放電的動態對比。

### 3. 🔌 充電器供電診斷與「供電不足」赤字警示
- **外接電源充足性判定**：免提權讀取 Windows 官方電源狀態（`Adequate` 供電充足 / `Inadequate` 供電不足）。
- **插電放電赤字偵測（Charger Deficit）**：當使用瓦數較低的 USB-C PD 充電器或重負載時，若充電器無法負荷導致「插著電電池卻仍在放電」，系統會立即亮起玫瑰紅警示徽章，精確標註電池補上的缺口瓦數（例如 `-18.5 W`）與推估耗盡時間。

### 4. 📈 0% ~ 100% 電池電壓曲線與歷史記錄（v1.1.6 新功能）
- **電量百分比 vs 電壓趨勢**：在電量百分比每次跳動時記錄當下的電池端電壓，自動繪製出該筆電專屬的 0–100% 放電／充電電壓特性曲線。
- **統計數據持久化**：本機持久化儲存各電量點的平均電壓、最低／最高電壓、採樣次數與最後記錄時間，未測量到的電量區間真實留白、絕不內插造假。

### 5. 🪟 極低資源常駐與動態系統匣（System Tray）
- **動態系統匣圖示**：工作列系統匣圖示即時繪製目前充放電瓦數（支援 `< 1 W` 精確至小數點一位，如 `0.8 W`，大於 1 W 顯示整數或 `99+`）。
- **靜默開機啟動**：支援 `--background` 參數，開機自動常駐系統匣，不彈出干擾視窗。
- **超低背景資源開銷**：視窗顯示時 1 秒取樣；縮到系統匣後自動放寬為 5 秒取樣，並配合記憶體自動調校，長效常駐不卡頓。
- **單一執行個體喚醒**：重複開啟程式時會自動將已執行的實體喚醒至前景，不會重複佔用資源。

### 6. 🛡️ 誠實工程原則（Zero Fabricated Metrics）
- 堅持「**量得到的量到底，量不到的說清楚**」。
- 不使用隨意線性公式捏造「系統總功耗」或虛構「充電器額定瓦數」。
- 實測數據與系統推估數據在介面上嚴格明確標示。

---

## 🖥️ 功能分頁與介面導覽

```
┌────────────────────────────────────────────────────────────────────────┐
│  ⚡ WinBat Lens                                              v1.1.7    │
├────────────────────────────────────────────────────────────────────────┤
│ [健康度: 94.2% A]  [即時功率: -14.2 W]  [外接電源: 🔌 供電充足]  [42 循環] │
├────────────────────────────────────────────────────────────────────────┤
│  ⚡ 全系統硬體功耗分佈  │ 📉 功耗歷史紀錄 │ 🔋 電池電壓紀錄 │ 💡 維護建議 ...│
│ ┌────────────────────────────────────────────────────────────────────┐ │
│ │ 60 秒即時功耗波形圖（放電 / 充電 / 獨顯 同軸比較）                 │ │
│ │ ────────────────────────────────────────────────────────────────── │ │
│ │ CPU 功耗: 15.2 W (RAPL 實測)  │ 獨顯功耗: 12.0 W (NVML 實測)        │ │
│ └────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────┘
```

1. **頂部狀態總覽卡片**：一覽電池健康度百分比與等級、充放電即時瓦數、外接電源狀態、電池端電壓、溫度、循環次數與序號。
2. **⚡ 全系統硬體功耗分佈**：整合 CPU、GPU、電池即時數據與 60 秒即時動態波形圖。
3. **📉 即時功耗與充放電歷史紀錄**：記錄每一筆取樣明細，統計最高、最低與平均功率，支援匯出 CSV。
4. **🔋 電池電壓紀錄**：視覺化呈現 0%–100% 電量對應的端電壓曲線，附帶完整統計數據表與重置功能。
5. **💡 智慧診斷與維護建議**：分析電池損耗程度、充電循環次數，提供筆電電池日常保養與校正指引。
6. **⏱️ 續航估算表**：根據 Windows 系統歷史耗電紀錄，客觀預估滿電與當前電量的可使用時間。
7. **📊 最近使用紀錄**：檢視近期開機使用時長、待機/睡眠統計與充放電時間軸。

---

## 📥 下載與安裝

請至 [GitHub Releases](https://github.com/nojackno2-ctrl/WinBat-Lens/releases) 下載最新版本（**v1.1.7**）：

| 格式 | 檔案名稱 | 說明 |
| :--- | :--- | :--- |
| **🚀 安裝版** | `WinBatLens_v1.1.7_Setup_x64.exe` | Inno Setup 安裝程式，支援開機啟動設定與乾淨解除安裝。 |
| **📦 免安裝單檔版** | `WinBatLens_v1.1.7_Portable_x64.exe` | 單一可執行檔（Single-File），免安裝、隨開即用。 |
| **📁 免安裝壓縮包** | `WinBatLens_v1.1.7_Portable_x64.zip` | 包含可執行檔、README 與授權說明的 ZIP 封裝包。 |

> [!NOTE]
> **Windows SmartScreen 提示說明**：
> 本開源專案程式由 CI 與本機環境編譯，目前未包含昂貴的商業 Authenticode 數位簽章。初次執行時若 Windows Defender / SmartScreen 出現「Windows 已保護您的電腦」藍色提示，請點擊「**其他資訊**」並選擇「**仍要執行**」即可正常使用。

---

## ⚙️ 系統需求

- **作業系統**：Windows 10 / 11 64 位元 (x64)
- **硬體平台**：具備 ACPI 相容電池之筆記型電腦、Windows 平板或掌上型裝置（如 ASUS ROG、ThinkPad、Dell XPS、Surface 等）
- **執行環境**：程式已內建獨立執行環境（Self-Contained ReadyToRun），**使用者電腦無需預先安裝 .NET 執行階段**。
- **顯示卡監控（選配）**：NVIDIA（支援 NVML）或 AMD（支援 ADL）獨立顯示卡。

---

## 🛠️ 開發與建置

若您希望從原始碼自行建置 WinBat Lens：

### 需求條件
- Windows 10 / 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Inno Setup 6](https://jrsoftware.org/isdl.php)（僅在產出 Setup 安裝版時需要）

### 建置與單元測試
```powershell
# 1. 還原相依套件
dotnet restore .\WinBatLens.sln

# 2. 編譯 Release 版本（啟用 0 警告嚴格檢查）
dotnet build .\WinBatLens.sln -c Release -warnaserror

# 3. 執行單元測試
dotnet test .\tests\WinBatLens.Tests\WinBatLens.Tests.csproj -c Release
```

### 封裝發行檔
執行隨附的 PowerShell 腳本，即可一鍵在 `dist/` 目錄產出 Setup、Portable EXE 與 ZIP 三種發行檔：
```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

---

## 📐 架構與核心模組

- **UI 呈現層**：.NET 10 WPF，具備響應式排版、深淺色調適配與即時圖表渲染。
- **報告解析服務 (`Services/BatteryReportParser.cs`)**：強健的 HTML 報告解析器，支援無電池、缺漏容量等邊界情況。
- **底層驅動遙測 (`Services/BatteryTelemetryService.cs`)**：透過 Windows Device IOCTL 直接對接 `GUID_DEVINTERFACE_BATTERY`，讀取精確電壓、電流與充放電率。
- **硬體感測監控 (`Services/HardwareSensorService.cs`)**：結合 `LibreHardwareMonitorLib` 讀取 CPU RAPL 與 GPU NVML/ADL 實測瓦數。
- **電壓曲線紀錄 (`Services/BatteryVoltageHistoryService.cs`)**：依 SOC 百分比持久化端電壓統計紀錄。
- **電源狀態判定 (`Services/PowerSupplyService.cs`)**：手寫 WinRT COM 啟動讀取 `PowerSupplyStatus`，極低開銷。
- **系統匣與單實體管理 (`Services/DynamicTrayIconService.cs`, `Services/SingleInstanceService.cs`)**：動態繪製工作列圖示與處理行程間互斥鎖通訊。

---

## 📜 授權條款

本專案採用 [MIT License](LICENSE) 授權開源。
歡迎提交 Issue、建議或 Pull Request 共同完善！
