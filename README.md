# ⚡ WinBat Lens

Windows 電池健康度與即時功耗監測儀表板，使用 WPF 顯示 `powercfg /batteryreport`、電池驅動資料與可取得的硬體感測值。

## v1.1.4 變更重點

- 主視窗標題顯示目前版本；版號讀自組件，單一來源是 `WinBatLens.csproj`。
- 重複啟動不再安靜結束，而是把執行中的視窗帶回前景；兩份版本不同時會先詢問是否取代舊版。
- 安裝與解除安裝先送出具名結束事件並等待互斥物件釋放，不再與拒絕 WM_CLOSE、縮回系統匣的程式互卡（舊版沿用原本的提示流程）。
- 修正 Inno Setup 編譯失敗仍被回報為發行成功的問題。
- v1.1.3 的效能最佳化已在具備電池與獨立顯示卡的實機上驗證，因此本版為正式發行版。

## v1.1.3 變更重點

純效能最佳化，顯示的數值、更新頻率與文案都沒有改變。

- GPU Engine 執行個體名稱只解析一次並快取，不再每秒對約 600 個執行個體重做字串切割。
- 顯示卡名稱、彙總字典改為只算一次或重複使用；每秒只讀一次電池，不再重複發送 IOCTL。
- 電池 IOCTL 改用釘選堆疊變數，輪詢路徑上不再配置原生記憶體。
- 縮到系統匣時，硬體感測器掃描從 1 秒放寬到 5 秒，與托盤取樣節奏一致。
- 工作集回收改由堆積成長觸發，不再每分鐘無條件做一次會封鎖的 GC；並修正每次回收洩漏一個行程 handle 的問題。
- 系統匣提示文字、波形圖點集合與 Y 軸刻度改為只在內容真的改變時才更新。

此版本當時標記為預發行版：上述改動動到硬體讀取路徑，CI 已在 Windows 上通過建置與測試，但尚未在具備電池與獨立顯示卡的實機上驗證。該驗證已於 v1.1.4 完成。

## v1.1.2 變更重點

- 升級至 .NET 10 LTS。
- 硬體／WMI／Performance Counter 取樣移至背景執行緒，WPF UI 只負責繪製快照。
- 視窗可見時每秒更新；縮至系統托盤後每 5 秒更新，恢復視窗時立即顯示最新快照。
- 新增 BatteryReportParser 單元測試與 Windows GitHub Actions CI。
- 修正文案，明確區分「實測」與「推估」，不再宣稱能取得變壓器額定瓦數。

## 能可靠取得的資料

### 電池健康度

- 解析 Windows `powercfg /batteryreport`。
- 讀取電池驅動提供的設計容量、目前滿電容量、電量、電壓與充放電率。
- 優先使用即時驅動容量計算健康度；不同單位（mAh／mWh）不會混算。
- 電池不存在、韌體未提供循環次數或溫度時，會明確顯示不適用，而不是填入假數值。

### 即時功耗

- 電池放電／充電功率：電池驅動 IOCTL 實測，代表電池端流入或流出的功率。
- 獨立顯示卡功耗：硬體監測器能提供時使用實測值。
- CPU、iGPU、RAM、螢幕、磁碟、Wi‑Fi 與系統總功耗若沒有可靠硬體來源，不會被線性公式冒充成實測。
- 60 秒波形圖以同一個 W 軸比較放電、充電與獨顯功耗。

### 外接電源與 USB-C

Windows 可提供外接電源是否足夠的判定（Adequate／Inadequate／Not Present）。若插電後電池仍在放電，介面會顯示「外接電源不足」與電池端實測缺口。

本程式不宣稱 USB-C 類型或變壓器額定瓦數。這些數值在一般使用者模式下沒有跨硬體可靠的公開 API；可取得的電池充電率也不能證明插頭類型。

## 下載

最新版本請至 [GitHub Releases](https://github.com/nojackno2-ctrl/WinBat-Lens/releases) 下載：

- `WinBatLens_v1.1.4_Setup_x64.exe`：Inno Setup 安裝版。
- `WinBatLens_v1.1.4_Portable_x64.exe`：單一可攜執行檔。
- `WinBatLens_v1.1.4_Portable_x64.zip`：含執行檔、README 與授權檔的 ZIP。

目前公開發行檔的簽章需由發行者在本機提供 Authenticode 憑證或簽章服務後完成；沒有簽章的檔案可能觸發 Windows SmartScreen 提示。

## 開發與建置

需求：

- Windows 10／11 x64。
- .NET 10 SDK。
- Inno Setup 6（只在製作安裝版時需要）。

建置與測試：

```powershell
dotnet restore .\WinBatLens.sln
dotnet build .\WinBatLens.sln -c Release -warnaserror
dotnet test .\tests\WinBatLens.Tests\WinBatLens.Tests.csproj -c Release
```

製作三種發行檔：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

輸出在 `dist/`。專案的發行設定集中於 [WinBatLens.csproj](WinBatLens.csproj)，因此直接執行 `dotnet publish` 與發行腳本使用相同的 single-file、ReadyToRun 與壓縮設定。

## 架構

- UI：.NET 10 WPF。
- 電池報告：`Services/BatteryReportParser.cs`。
- 電池驅動 IOCTL：`Services/BatteryTelemetryService.cs`。
- 即時功耗與 Performance Counter：`Services/RealTimePowerService.cs`。
- 背景硬體感測：`Services/HardwareSensorService.cs`。
- Windows 電源供應判定：`Services/PowerSupplyService.cs`。

## 授權

[MIT License](LICENSE)
