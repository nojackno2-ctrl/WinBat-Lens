# ⚡ WinBat Lens

<div align="center">

![Windows](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x64)-0078D6?logo=windows&logoColor=white)
![Rust](https://img.shields.io/badge/Rust-stable%20(MSVC)-B7410E?logo=rust&logoColor=white)
![UI](https://img.shields.io/badge/UI-egui%20%2F%20eframe-blue)
![Version](https://img.shields.io/badge/Source-v2.0.1-brightgreen)
![License](https://img.shields.io/badge/License-MIT-green.svg)

**專為 Windows 筆電與行動裝置設計的電池健康度診斷、即時充放電功耗與硬體分析儀表板**

[功能特點](#-核心亮點) • [畫面導覽](#-功能分頁與介面導覽) • [立即下載](#-下載與安裝) • [系統需求](#-系統需求) • [開發建置](#-開發與建置) • [授權](#-授權條款)

</div>

---

## 📖 簡介

**WinBat Lens** 是一款輕量、以「精確真實數據」為核心原則的 Windows 電池與硬體功耗監控工具。

許多使用者常遇到電池壽命衰退、插上 PD 充電器卻發現電量越充越少（充放電入不敷出）、或是市面上軟體用公式胡亂推算虛假總功耗等問題。WinBat Lens 直接解析 Windows 原生 `powercfg /batteryreport` 報告、呼叫底層電池類別驅動程式（IOCTL），並讀取 NVIDIA 獨立顯卡的 NVML 功耗感測器，提供客觀、未經修飾的即時數據。

自 **v2.0.0** 起，整個程式以 **Rust** 重寫：單一原生執行檔，不需要 .NET 執行階段，也不再於 `%TEMP%` 解壓縮任何檔案。

---

## 🌟 核心亮點

### 1. 🔋 真實電池健康度與容量分析
- **解析系統報告**：解析 Windows `powercfg /batteryreport`，讀取原廠設計容量（Design Capacity）與目前滿電容量（Full Charge Capacity）。
- **即時驅動容量校驗**：以電池驅動回報的即時容量覆蓋報告快照，並避免單位（mAh / mWh）混算。
- **直觀健康評級**：計算健康度百分比，以綠（≥ 80%）、琥珀（60–80%）、紅（< 60%）三級呈現。
- **電池硬體細節**：名稱、製造商、化學材質、循環次數，以及驅動有提供時的出廠日期與電池溫度。

### 2. ⚡ 即時充放電與硬體功耗監測
- **電池端真實功率**：透過電池驅動 IOCTL 實測流入／流出電池的功率（W）。使用電池時，這就是整機的真實耗電。
- **獨立顯卡（dGPU）實測**：NVIDIA 顯卡透過 NVML 讀取實測瓦數；使用率來自 Windows GPU Engine 效能計數器。
- **60 秒即時波形圖**：充電功率、放電功率與獨顯功耗共用**同一個瓦數（W）Y 軸**，比例一致，可直接比較。

### 3. 🔌 充電器供電診斷與「供電不足」警示
- **外接電源充足性判定**：免提權讀取 Windows 官方電源狀態（`Adequate` 供電充足 / `Inadequate` 供電不足）。
- **插電放電赤字偵測（Charger Deficit）**：使用瓦數較低的 USB-C PD 充電器或重負載時，若「插著電電池卻仍在放電」，會立即以紅色標示電池補上的缺口瓦數（例如 `-18.5 W`）與可支撐時間。

### 4. 📈 0% ~ 100% 電池電壓曲線與歷史記錄
- **電量百分比 vs 電壓趨勢**：在電量百分比每次跳動時記錄當下的電池端電壓，繪製該筆電專屬的 0–100% 電壓特性曲線。
- **統計數據持久化**：本機儲存各電量點的平均、最低／最高電壓、採樣次數與最後記錄時間（`%LOCALAPPDATA%\WinBatLens`，與 v1.x 的紀錄檔相容）。未測量到的電量區間真實留白、絕不內插造假。

### 5. 🪟 低資源常駐與動態系統匣（System Tray）
- **動態系統匣圖示**：即時繪製目前充放電瓦數（`< 1 W` 精確至小數點一位，如 `0.8`；1 W 以上顯示整數或 `99+`），充電為綠色、放電為紅色。
- **靜默開機啟動**：支援 `--background` 參數，開機後直接常駐系統匣，不彈出視窗。
- **背景降頻取樣**：視窗顯示時每秒取樣；縮到系統匣後放寬為每 5 秒，並釋放閒置的工作集記憶體。
- **單一執行個體喚醒**：重複開啟時會把已執行的實體喚醒到前景；偵測到不同版本時可選擇直接替換。

### 6. 🛡️ 誠實工程原則（Zero Fabricated Metrics）
- 堅持「**量得到的量到底，量不到的說清楚**」。
- 不以使用率公式捏造「系統總功耗」、CPU 瓦數或「充電器額定瓦數」；讀不到的數值一律顯示 `--`。
- 桌上型電腦（無電池）會明確顯示「未偵測到電池」，而不是假裝正在充電。

---

## 🖥️ 功能分頁與介面導覽

```
┌────────────────────────────────────────────────────────────────────────────┐
│ WinBat Lens  v2.0.1          [開機自動啟動] [English] [開啟報告] [匯出] [檢測] │
├───────────────────────┬────────────────────────────────────────────────────┤
│ 電池健康度（環形儀表） │ [功耗分佈] [歷史紀錄] [電壓紀錄] [診斷] [續航] [使用] │
│ 電池規格 / 容量衰退條 │  市電 ──●──●──▶ [ +48.0 W ] ──●──●──▶ 電池 72%       │
│ 容量歷史紀錄          │  60 秒即時功率波形（放電 / 充電 / 獨顯 同軸比較）     │
│                       │  硬體實測功耗：電壓電流 │ 蓄電量 │ 電源計劃 │ 獨顯     │
└───────────────────────┴────────────────────────────────────────────────────┘
```

1. **左側電池面板**：健康度環形儀表與評級、電池規格、設計／滿電容量衰退條、容量歷史紀錄。
2. **⚡ 全系統硬體功耗分佈**：能量流示意圖（市電 → 電源中樞 → 電池）、剩餘時間、電壓／電流、60 秒波形圖與硬體實測列。
3. **📉 即時功耗與充放電歷史紀錄**：每 5 秒一筆取樣與插拔電事件，最多保留 500 筆，可匯出 CSV。
4. **🔋 電池電壓紀錄**：0%–100% 電量對應的端電壓曲線與統計表。
5. **💡 智慧診斷與維護建議**：依損耗程度、循環次數與役齡提供保養指引。
6. **⏱️ 續航估算表**：Windows 依歷史耗電紀錄估算的滿電／設計容量續航。
7. **📊 最近使用紀錄**：近期的使用狀態、電源來源與剩餘容量時間軸。

---

## 📥 下載與安裝

請至 [GitHub Releases](https://github.com/nojackno2-ctrl/WinBat-Lens/releases) 下載最新發行版本。每個版本提供三種格式：

| 格式 | 檔案名稱 | 說明 |
| :--- | :--- | :--- |
| **🚀 安裝版** | `WinBatLens_v<版本>_Setup_x64.exe` | Inno Setup 安裝程式；固定建立開始功能表的啟動／解除安裝捷徑，可自行釘選到開始或工作列。桌面捷徑與開機啟動為預設不勾選的選用項目。 |
| **📦 免安裝單檔版** | `WinBatLens_v<版本>_Portable_x64.exe` | 單一可執行檔，免安裝、隨開即用。 |
| **📁 免安裝壓縮包** | `WinBatLens_v<版本>_Portable_x64.zip` | 包含可執行檔、README 與授權說明。 |

> [!NOTE]
> Rust 重寫版（v2.x）目前以**預發行版**發布；Releases 頁面上標為 Latest 的正式版仍是 .NET 版（v1.1.8）。

> [!NOTE]
> **Windows SmartScreen / 智慧型應用程式控制**：
> 本開源專案的執行檔未包含商業 Authenticode 數位簽章。初次執行時若 SmartScreen 出現「Windows 已保護您的電腦」，請點擊「**其他資訊**」→「**仍要執行**」。若電腦啟用了「智慧型應用程式控制（Smart App Control）」，未簽署的執行檔可能會被直接封鎖且無法略過，這是該功能的設計。

---

## ⚙️ 系統需求

- **作業系統**：Windows 10 / 11 64 位元 (x64)
- **硬體平台**：具備 ACPI 相容電池之筆記型電腦、Windows 平板或掌上型裝置。桌上型電腦也能執行，但電池相關數值不適用。
- **執行環境**：原生執行檔，**無需安裝任何執行階段**；需要顯示卡驅動支援 OpenGL。
- **顯示卡功耗（選配）**：NVIDIA 獨立顯示卡（驅動程式隨附的 NVML）。AMD / Intel 獨顯會顯示使用率，但功耗顯示為 `-- W`。

---

## 🛠️ 開發與建置

### 需求條件
- Windows 10 / 11 x64
- [Rust](https://rustup.rs/) stable（`x86_64-pc-windows-msvc`），以及 Visual Studio 的「使用 C++ 的桌面開發」工作負載（提供 MSVC 連結器與 Windows SDK）
- [Inno Setup 6](https://jrsoftware.org/isdl.php)（僅在產出 Setup 安裝版時需要）

### 建置與測試
```powershell
# 格式與靜態檢查（警告視為錯誤）
cargo fmt --all -- --check
cargo clippy --all-targets --locked -- -D warnings

# 單元測試（含無視窗的 UI 冒煙測試與安裝程式契約測試）
cargo test --locked

# 編譯最佳化的發行版本：target\release\WinBatLens.exe
cargo build --release --locked
```

### 封裝發行檔
執行隨附的 PowerShell 腳本，即可在 `dist/` 目錄產出 Setup、Portable EXE 與 ZIP 三種發行檔。版本號只寫在 `Cargo.toml` 一處：
```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

### 在沒有電池的機器上檢視介面（僅除錯建置）
除錯建置可用環境變數注入模擬的電池情境，方便在桌機上檢查畫面。發行建置不包含這段程式碼，只會顯示真實讀數。
```powershell
$env:WINBAT_LENS_DEMO = "charging"   # charging | battery | deficit | idle
$env:WINBAT_LENS_DEMO_REPORT = "C:\path\to\battery-report.html"   # 選用：改讀既有報告
cargo run
```

---

## 📐 架構與核心模組

| 模組 | 職責 |
| :--- | :--- |
| `src/main.rs` | 進入點、崩潰日誌、單一執行個體判定 |
| `src/report_parser.rs` | `powercfg` HTML 報告解析、健康度計算與診斷建議，支援無電池、缺漏容量等邊界情況 |
| `src/battery.rs` | 透過 Device IOCTL 對接電池類別驅動，讀取功率、電壓、容量、溫度與電池識別資訊 |
| `src/gpu.rs` | DXGI 轉接卡枚舉、PDH GPU Engine 使用率、NVML 獨顯功耗 |
| `src/realtime.rs` | 整合每秒取樣並組成畫面所需的即時狀態（純函式 `compose_state` 可獨立測試） |
| `src/monitor.rs` | 背景監測執行緒與 UI／托盤共享的狀態 |
| `src/history.rs` | 功耗事件紀錄、CSV 匯出、電壓曲線紀錄與持久化 |
| `src/tray.rs` | 系統匣圖示（GDI 動態繪製瓦數）、提示文字與右鍵選單，擁有獨立執行緒 |
| `src/single_instance.rs` | 具名 Mutex／Event 的跨行程協調：喚醒既有實體、跨版本替換、回應安裝程式的結束要求 |
| `src/startup.rs` | HKCU Run 鍵的開機自動啟動 |
| `src/powercfg.rs` | 執行 `powercfg /batteryreport`（10 秒逾時、暫存檔自動清理） |
| `src/ui/` | egui 儀表板：主題與字型、自繪儀表元件、六個分頁、狀態文字呈現 |
| `src/contract_tests.rs` | 安裝程式腳本、發行工具與程式之間的名稱／路徑契約測試 |

---

## 📜 授權條款

本專案採用 [MIT License](LICENSE) 授權開源。
歡迎提交 Issue、建議或 Pull Request 共同完善！
