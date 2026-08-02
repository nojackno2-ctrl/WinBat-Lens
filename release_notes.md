## 📈 WinBat Lens v1.1.1

波形圖的放電、充電與獨顯功耗現在共用同一個瓦數刻度。移除獨顯專用的右側刻度後，相同高度永遠代表相同 W，能直接比較整機電池放電與獨顯功耗。

---

## 🔌 WinBat Lens v1.1.0

USB-C 充電：能量到的量到底，量不到的說清楚 —— 順便修掉一個把最關鍵數字藏起來的 bug。

---

### 📥 下載

| 版本 | 檔案 | 大小 |
|---|---|---|
| **安裝版** | `WinBatLens_v1.1.0_Setup_x64.exe` | 71.8 MB |
| **免安裝版（單一執行檔）** | `WinBatLens_v1.1.0_Portable_x64.exe` | 76.7 MB |
| **免安裝版（ZIP）** | `WinBatLens_v1.1.0_Portable_x64.zip` | 71.0 MB |

---

### 🐛 修正：插著電卻還在放電，之前完全看不見

這是本版最重要的修正。

`RealTimePowerService` 過去**只在 `!IsAcOnline` 時才讀放電功率**。所以「充電器帶不動、電池邊插邊掉」這個狀態，會落進「市電直供、電池未充放電」那條分支，畫面顯示 `-- W`、狀態寫「無可量測功率」。

而那正是用 USB-C 充電時最該看到的數字 —— 65W 的 PD 充電器撐不住一台吃更多瓦的機器，電池就默默補上差額，App 卻把它藏起來。

現在會標記為 `IsChargerDeficit`，缺口以**實測**放電瓦數呈現，並一致貫穿：

- 主數字改為 `-X.X W`
- 狀態徽章顯示「外接電源供電不足 — 電池補上 -X.X W（電池實測）」，配色為玫瑰紅（電真的在從電池流出，綠色會是謊話）
- 60 秒波形圖的放電線正常繪出，不再是一條零
- 工作列圖示顯示紅色瓦數、提示文字標註 `(charger too weak)`
- 歷史紀錄不再自相矛盾（過去會寫「市電正常」卻同時記著非零放電），改記「⚠️ 外接電源不足」
- 剩餘時間改由「電池剩餘 Wh ÷ 實測缺口」計算
- `BatteryCurrentA` 跟著實際電流方向走，不再假設「在 AC 上就是充電」

---

### 🔋 新增：Windows 對充電器的供電判定

新的 `PowerSupplyService` 讀取 `Windows.System.Power.PowerManager.PowerSupplyStatus` —— Windows 唯一一個描述**充電器本身**而非電池的官方、免提權訊號：

| 值 | 顯示 |
|---|---|
| `Adequate` | 🔌 外接電源供電充足 |
| `Inadequate` | ⚠️ 外接電源供電能力不足以支撐目前的系統負載（琥珀色警示） |
| `NotPresent` / 讀取失敗 | 整列隱藏，不顯示佔位文字 |

用**手寫 WinRT activation**（一個 IID、一個 vtable slot）而非 C# projection：projection 需要把目標框架換成 `net8.0-windows10.0.x`，會把整包 Windows SDK projection 組件塞進這個有在調校體積與冷啟動時間的 single-file bundle。實測讀取成本 **0.022 µs**，因此每秒直接讀取、不做快取。

---

### ❌ 為什麼沒有「變壓器 / 充電器瓦數」

會想要的是 USB-C Power Delivery 協商出來的電壓電流，也就是真正的充電器瓦數。**在一般使用者權限的 Windows 程式裡拿不到。** 這不是推測，是對實機（ASUS ROG Zephyrus G14，當時正以 USB-C 供電）逐條驗證的結果：

| 管道 | 結果 |
|---|---|
| UCM-UCSI ACPI 裝置 (`ACPI\USBC000\0`) | 存在，但它註冊的兩個 device interface 完全不在公開 SDK 中 —— 驅動對驅動用，沒有文件化的使用者模式 IOCTL |
| `BATTERY_USB_CHARGER_STATUS` (poclass.h) | 確實帶有 PD 合約旗標、埠的 mA 與 mV，但走 `IOCTL_BATTERY_SET_INFORMATION` **寫入**方向，由這類筆電沒有的 Charging Arbitration Driver 推入。不存在對應的查詢層級 |
| `POWER_ADAPTER_STATUS.MaxOutputPower` | 就是額定瓦數，但 batclass.h 僅透過**核心模式** adapter miniclass callback 提供；`ACPI\ACPI0003` 上的通用 Microsoft AC Adapter 驅動不提供 |
| 電池 Customized I/O（OEM 專用逃生口） | 實測回報 `SupportedInputs = 0`、`SupportedOutputs = 0` —— 什麼都沒開放 |
| ASUS ATK WMI (`AsusAtkWmi_WMNB` / `DSTS`) | ROG 機種確實知道充電來源，但每次查詢皆「拒絕存取」，需要提權，而本程式刻意不提權執行 |

所以整個儀表板不會出現任何變壓器瓦數。這與本專案一貫的原則一致：**量不到的數字就不顯示，而不是估一個看起來合理的。** 上面兩項新增的都是實測值。

---

### ⚙️ 相容性與成本

- 穩態 CPU 佔用維持 **2.8% / 單核**，與前一版相同 —— 新增的讀取在量測誤差之內。
- 目標框架、封裝方式、體積策略皆未變動。
- 不支援 `PowerManager` 的環境會靜默降級：該列隱藏，其餘功能不受影響。
