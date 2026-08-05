using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供 Windows 電池報告 HTML 檔案（powercfg /batteryreport）之解析與資料提取服務。
    /// 支援搭配即時電池驅動遙測數據（<see cref="BatteryTelemetryService.PackInfo"/>）進行綜合健康度分析與覆蓋。
    /// </summary>
    public class BatteryReportParser
    {
        /// <summary>
        /// 解析 powercfg 電池報告 HTML 內容，並可選擇性疊加即時電池驅動遙測資料。
        /// </summary>
        /// <param name="htmlContent">powercfg /batteryreport 所產生的 HTML 檔案內容。</param>
        /// <param name="pack">
        /// 來自 <see cref="BatteryTelemetryService"/> 之即時電池驅動資訊；若為 null，則僅解析報告本文（例如使用者開啟外部 HTML 檔案時）。
        /// </param>
        /// <returns>解析完成之 <see cref="BatteryReportData"/> 完整報告模型。</returns>
        public static BatteryReportData Parse(string htmlContent, BatteryTelemetryService.PackInfo? pack = null)
        {
            var data = new BatteryReportData();

            data.SystemInfo = ParseSystemInfo(htmlContent);
            data.BatterySpecs = ParseBatterySpecs(htmlContent);
            data.CapacityHistory = ParseCapacityHistory(htmlContent);
            data.UsageHistory = ParseUsageHistory(htmlContent);
            data.BatteryLifeEstimates = ParseBatteryLifeEstimates(htmlContent);
            data.RecentUsage = ParseRecentUsage(htmlContent);

            // 必須在計算指標前執行：健康度、損耗與診斷皆依據合併後的規格計算
            if (pack != null && pack.IsValid) OverlayDriverSpecs(data.BatterySpecs, pack);

            // 計算健康指標與診斷提示
            data.HealthMetrics = CalculateHealthMetrics(data.BatterySpecs);
            data.Diagnostics = GenerateDiagnostics(data);

            return data;
}
        /// <summary>
        /// 以即時電池驅動數據覆蓋 powercfg 報告數據，取得最新且精確之容量與製造日期資訊。
        /// </summary>
        private static void OverlayDriverSpecs(BatterySpecs specs, BatteryTelemetryService.PackInfo pack)
        {
            // 檢查單位是否一致（避免 mWh 與 mAh 混用）
            bool unitsMatch = !pack.IsCapacityRelative
                && (specs.Unit == "mWh" || specs.DesignCapacity <= 0);

            if (unitsMatch)
            {
                if (pack.DesignedCapacityMWh > 0)
                {
                    specs.DesignCapacity = pack.DesignedCapacityMWh;
                    specs.Unit = "mWh";
                    specs.CapacitiesFromDriver = true;
                }

                if (pack.FullChargedCapacityMWh > 0)
                {
                    specs.FullChargeCapacity = pack.FullChargedCapacityMWh;
                    specs.Unit = "mWh";
                    specs.CapacitiesFromDriver = true;
                }
            }

            // 充電循環次數：若報告中缺失則採用驅動程式讀取值
            if (pack.CycleCount.HasValue) specs.CycleCount = pack.CycleCount;

            specs.ManufactureDate = pack.ManufactureDate;

            // 識別資訊：優先保留報告文字，若為空則退回採用驅動程式回報值
            if (IsBlank(specs.Chemistry) && !string.IsNullOrWhiteSpace(pack.Chemistry))
                specs.Chemistry = pack.Chemistry;

            if (IsBlank(specs.Name) && !string.IsNullOrWhiteSpace(pack.DeviceName))
                specs.Name = pack.DeviceName;

            if (IsBlank(specs.Manufacturer) && !string.IsNullOrWhiteSpace(pack.ManufactureName))
                specs.Manufacturer = pack.ManufactureName;
        }

        /// <summary>
        /// 判斷字串是否為空或是預設佔位符（N/A、Primary Battery、Windows PC 等）。
        /// </summary>
        private static bool IsBlank(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;

            string v = value.Trim();
            return v == "N/A"
                || v == "Primary Battery"
                || v == "Windows PC"
                || v == "Li-ion";
        }

        /// <summary>
        /// 自 HTML 解析系統基本資訊（電腦名稱、產品名稱、BIOS、OS 版本與報告時間）。
        /// </summary>
        private static SystemInfo ParseSystemInfo(string html)
        {
            var info = new SystemInfo();

            var compMatch = Regex.Match(html, @"COMPUTER NAME[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (compMatch.Success) info.ComputerName = StripTags(compMatch.Groups[1].Value);

            var prodMatch = Regex.Match(html, @"SYSTEM PRODUCT NAME[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (prodMatch.Success) info.SystemProductName = StripTags(prodMatch.Groups[1].Value);

            var biosMatch = Regex.Match(html, @"BIOS[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (biosMatch.Success) info.Bios = StripTags(biosMatch.Groups[1].Value);

            var osMatch = Regex.Match(html, @"OS BUILD[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (osMatch.Success) info.OsBuild = StripTags(osMatch.Groups[1].Value);

            var timeMatch = Regex.Match(html, @"REPORT TIME[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (timeMatch.Success) info.ReportTime = StripTags(timeMatch.Groups[1].Value);

            return info;
        }

        /// <summary>
        /// 自 HTML 解析已安裝電池規格（名稱、製造商、序號、化學材質、設計容量、滿充容量、循環次數）。
        /// </summary>
        private static BatterySpecs ParseBatterySpecs(string html)
        {
            var specs = new BatterySpecs();

            // 定位 INSTALLED BATTERIES 區塊
            var sectionMatch = Regex.Match(html, @"INSTALLED BATTERIES[\s\S]*?<table[^>]*>([\s\S]*?)<\/table>", RegexOptions.IgnoreCase);
            string searchBlock = sectionMatch.Success ? sectionMatch.Groups[1].Value : html;

            var nameMatch = Regex.Match(searchBlock, @"NAME[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (nameMatch.Success)
            {
                string val = StripTags(nameMatch.Groups[1].Value);
                if (!val.ToUpper().Contains("COMPUTER")) specs.Name = val;
            }

            var mfgMatch = Regex.Match(searchBlock, @"MANUFACTURER[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (mfgMatch.Success) specs.Manufacturer = StripTags(mfgMatch.Groups[1].Value);

            var snMatch = Regex.Match(searchBlock, @"SERIAL NUMBER[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (snMatch.Success) specs.SerialNumber = StripTags(snMatch.Groups[1].Value);

            var chemMatch = Regex.Match(searchBlock, @"CHEMISTRY[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (chemMatch.Success) specs.Chemistry = StripTags(chemMatch.Groups[1].Value);

            var desMatch = Regex.Match(searchBlock, @"DESIGN CAPACITY[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (desMatch.Success)
            {
                string raw = StripTags(desMatch.Groups[1].Value);
                specs.DesignCapacity = ExtractNumber(raw);
                if (raw.ToLower().Contains("mah")) specs.Unit = "mAh";
            }

            var fullMatch = Regex.Match(searchBlock, @"FULL CHARGE CAPACITY[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (fullMatch.Success)
            {
                specs.FullChargeCapacity = ExtractNumber(StripTags(fullMatch.Groups[1].Value));
            }

            var cycleMatch = Regex.Match(searchBlock, @"CYCLE COUNT[\s\S]*?<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
            if (cycleMatch.Success)
            {
                int val = ExtractNumber(StripTags(cycleMatch.Groups[1].Value));
                specs.CycleCount = val > 0 ? val : null;
            }

            return specs;
        }

        /// <summary>
        /// 解析歷史電池容量變遷表格。
        /// </summary>
        private static List<CapacityHistoryItem> ParseCapacityHistory(string html)
        {
            var list = new List<CapacityHistoryItem>();

            var sectionMatch = Regex.Match(html, @"BATTERY CAPACITY HISTORY[\s\S]*?<table[^>]*>([\s\S]*?)<\/table>", RegexOptions.IgnoreCase);
            if (sectionMatch.Success)
            {
                string tableHtml = sectionMatch.Groups[1].Value;
                var trMatches = Regex.Matches(tableHtml, @"<tr[^>]*>([\s\S]*?)<\/tr>", RegexOptions.IgnoreCase);

                foreach (Match tr in trMatches)
                {
                    var tdMatches = Regex.Matches(tr.Groups[1].Value, @"<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
                    if (tdMatches.Count >= 3)
                    {
                        string period = StripTags(tdMatches[0].Groups[1].Value);
                        int fullCharge = ExtractNumber(StripTags(tdMatches[1].Groups[1].Value));
                        int designCap = ExtractNumber(StripTags(tdMatches[2].Groups[1].Value));

                        if (!string.IsNullOrWhiteSpace(period) && fullCharge > 0 && designCap > 0 && !period.ToUpper().Contains("PERIOD"))
                        {
                            list.Add(new CapacityHistoryItem
                            {
                                Period = period,
                                FullChargeCapacity = fullCharge,
                                DesignCapacity = designCap
                            });
                        }
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// 解析歷史使用時間統計表格（電池使用時間 vs 插電時間）。
        /// </summary>
        private static List<UsageHistoryItem> ParseUsageHistory(string html)
        {
            var list = new List<UsageHistoryItem>();
            var sectionMatch = Regex.Match(html, @"USAGE HISTORY[\s\S]*?<table[^>]*>([\s\S]*?)<\/table>", RegexOptions.IgnoreCase);

            if (sectionMatch.Success)
            {
                string tableHtml = sectionMatch.Groups[1].Value;
                var trMatches = Regex.Matches(tableHtml, @"<tr[^>]*>([\s\S]*?)<\/tr>", RegexOptions.IgnoreCase);

                foreach (Match tr in trMatches)
                {
                    var tdMatches = Regex.Matches(tr.Groups[1].Value, @"<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
                    if (tdMatches.Count >= 3)
                    {
                        string period = StripTags(tdMatches[0].Groups[1].Value);
                        string batDur = StripTags(tdMatches[1].Groups[1].Value);
                        string acDur = StripTags(tdMatches[2].Groups[1].Value);

                        if (!string.IsNullOrWhiteSpace(period) && !period.ToUpper().Contains("PERIOD"))
                        {
                            list.Add(new UsageHistoryItem
                            {
                                Period = period,
                                BatteryDuration = batDur,
                                AcDuration = acDur
                            });
                        }
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// 解析電池續航估算表格。
        /// </summary>
        private static List<BatteryLifeEstimateItem> ParseBatteryLifeEstimates(string html)
        {
            var list = new List<BatteryLifeEstimateItem>();
            var sectionMatch = Regex.Match(html, @"BATTERY LIFE ESTIMATES[\s\S]*?<table[^>]*>([\s\S]*?)<\/table>", RegexOptions.IgnoreCase);

            if (sectionMatch.Success)
            {
                string tableHtml = sectionMatch.Groups[1].Value;
                var trMatches = Regex.Matches(tableHtml, @"<tr[^>]*>([\s\S]*?)<\/tr>", RegexOptions.IgnoreCase);

                foreach (Match tr in trMatches)
                {
                    var tdMatches = Regex.Matches(tr.Groups[1].Value, @"<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
                    if (tdMatches.Count >= 3)
                    {
                        string period = StripTags(tdMatches[0].Groups[1].Value);
                        string fullEst = StripTags(tdMatches[1].Groups[1].Value);
                        string desEst = StripTags(tdMatches[2].Groups[1].Value);

                        if (!string.IsNullOrWhiteSpace(period) && !period.ToUpper().Contains("PERIOD"))
                        {
                            list.Add(new BatteryLifeEstimateItem
                            {
                                Period = period,
                                FullChargeEstimate = fullEst,
                                DesignCapEstimate = desEst
                            });
                        }
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// 解析近期使用歷程紀錄表格。
        /// </summary>
        private static List<RecentUsageItem> ParseRecentUsage(string html)
        {
            var list = new List<RecentUsageItem>();
            var sectionMatch = Regex.Match(html, @"RECENT USAGE[\s\S]*?<table[^>]*>([\s\S]*?)<\/table>", RegexOptions.IgnoreCase);

            if (sectionMatch.Success)
            {
                string tableHtml = sectionMatch.Groups[1].Value;
                var trMatches = Regex.Matches(tableHtml, @"<tr[^>]*>([\s\S]*?)<\/tr>", RegexOptions.IgnoreCase);

                foreach (Match tr in trMatches)
                {
                    var tdMatches = Regex.Matches(tr.Groups[1].Value, @"<td[^>]*>([\s\S]*?)<\/td>", RegexOptions.IgnoreCase);
                    if (tdMatches.Count >= 4)
                    {
                        string start = StripTags(tdMatches[0].Groups[1].Value);
                        string state = StripTags(tdMatches[1].Groups[1].Value);
                        string source = StripTags(tdMatches[2].Groups[1].Value);
                        string capRem = StripTags(tdMatches[3].Groups[1].Value);

                        if (!string.IsNullOrWhiteSpace(start) && !start.ToUpper().Contains("START TIME"))
                        {
                            list.Add(new RecentUsageItem
                            {
                                StartTime = start,
                                State = state,
                                Source = source,
                                CapacityRemaining = capRem
                            });
                        }
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// 計算健康度指標（健康百分比、損耗率、容量差額與狀態等級說明）。
        /// </summary>
        private static HealthMetrics CalculateHealthMetrics(BatterySpecs specs)
        {
            // 若無設計容量，代表此裝置無電池（如桌上型電腦或電池已拔除）
            if (specs.DesignCapacity <= 0)
            {
                return new HealthMetrics
                {
                    HasBattery = false,
                    IsHealthMeasured = false,
                    HealthPercent = 0,
                    WearPercent = 0,
                    CapacityLoss = 0,
                    StatusLabel = "無電池裝置",
                    StatusClass = "None",
                    SummaryText = "未偵測到電池，本機可能為桌上型電腦或電池已卸除。電池健康度數據不適用，但全系統即時硬體功耗監測仍可正常使用。"
                };
            }

            if (specs.FullChargeCapacity <= 0)
            {
                return new HealthMetrics
                {
                    HasBattery = true,
                    IsHealthMeasured = false,
                    StatusLabel = "無法判定",
                    StatusClass = "None",
                    SummaryText = "電池缺少滿電容量資料，無法計算健康度。"
                };
            }

            double design = specs.DesignCapacity > 0 ? specs.DesignCapacity : 1.0;
            double current = specs.FullChargeCapacity;

            double healthPercent = Math.Min(100.0, Math.Round((current / design) * 100.0, 1));
            double wearPercent = Math.Max(0.0, Math.Round(100.0 - healthPercent, 1));

            string statusLabel = "健康良好";
            string statusClass = "Good";
            string summaryText = "電池狀態優良，蓄電與充電發揮理想效能。";

            if (healthPercent < 60.0)
            {
                statusLabel = "嚴重衰退";
                statusClass = "Danger";
                summaryText = "滿電容量已衰退超過 40%，建議安排更換電池以確保續航力與電源穩定度。";
            }
            else if (healthPercent < 80.0)
            {
                statusLabel = "需要注意";
                statusClass = "Warning";
                summaryText = "電池有些微損耗，續航時間可能已縮短，請注意散熱與充放電習慣。";
            }

            return new HealthMetrics
            {
                IsHealthMeasured = true,
                HealthPercent = healthPercent,
                WearPercent = wearPercent,
                CapacityLoss = Math.Max(0, specs.DesignCapacity - specs.FullChargeCapacity),
                StatusLabel = statusLabel,
                StatusClass = statusClass,
                SummaryText = summaryText
            };
        }

        /// <summary>
        /// 根據電池報告數據與指標，自動產生健康與維護診斷提示清單。
        /// </summary>
        private static List<DiagnosticItem> GenerateDiagnostics(BatteryReportData report)
        {
            var tips = new List<DiagnosticItem>();
            var metrics = report.HealthMetrics;
            var specs = report.BatterySpecs;

            if (!metrics.HasBattery)
            {
                tips.Add(new DiagnosticItem
                {
                    Type = "info",
                    Title = "未偵測到電池裝置",
                    Description = "本機可能為桌上型電腦，或電池已被卸除，因此電池健康度、容量與循環次數等數據不適用。全系統即時硬體功耗監測功能仍可正常運作。"
                });
                return tips;
            }

            if (!metrics.IsHealthMeasured)
            {
                tips.Add(new DiagnosticItem
                {
                    Type = "info",
                    Title = "健康度無法判定",
                    Description = "報告缺少設計容量或滿電容量，未顯示虛假的健康百分比。"
                });
                return tips;
            }

            if (metrics.HealthPercent < 70.0)
            {
                tips.Add(new DiagnosticItem
                {
                    Type = "danger",
                    Title = "電池容量顯著衰退",
                    Description = $"目前最高容量僅為原廠設計值的 {metrics.HealthPercent}%，外出的實際使用時間將有所減少。"
                });
            }
            else if (metrics.HealthPercent < 80.0)
            {
                tips.Add(new DiagnosticItem
                {
                    Type = "warning",
                    Title = "電池進入磨耗階段",
                    Description = $"滿電容量為原廠設計值的 {metrics.HealthPercent}%，累積損耗量達 {metrics.CapacityLoss:N0} {specs.Unit}。"
                });
            }
            else
            {
                tips.Add(new DiagnosticItem
                {
                    Type = "success",
                    Title = "電池健康狀況優異",
                    Description = $"最高滿電容量達到原廠設計值的 {metrics.HealthPercent}%，性能表現非常良好。"
                });
            }

            if (specs.CycleCount.HasValue)
            {
                if (specs.CycleCount.Value > 500)
                {
                    tips.Add(new DiagnosticItem
                    {
                        Type = "warning",
                        Title = "充放電循環次數較高",
                        Description = $"目前累積 {specs.CycleCount.Value} 次循環。大多數鋰電池在 300-500 次循環後容量會逐漸降低。"
                    });
                }
                else
                {
                    tips.Add(new DiagnosticItem
                    {
                        Type = "info",
                        Title = "充放電循環狀態正常",
                        Description = $"目前已累積 {specs.CycleCount.Value} 次循環。"
                    });
                }
            }
            else
            {
                tips.Add(new DiagnosticItem
                {
                    Type = "info",
                    Title = "此電池未回報循環次數",
                    Description = "powercfg 報告與電池驅動 (IOCTL_BATTERY_QUERY_INFORMATION) 都沒有循環次數，代表這顆電池的韌體並未實作該欄位，而不是讀取失敗。"
                });
            }

            if (specs.AgeYears.HasValue && specs.ManufactureDate.HasValue)
            {
                double years = specs.AgeYears.Value;
                tips.Add(new DiagnosticItem
                {
                    Type = years >= 4.0 ? "warning" : "info",
                    Title = $"電池役齡約 {years:F1} 年",
                    Description = $"電芯出廠日期為 {specs.ManufactureDate.Value:yyyy-MM-dd}（讀自電池驅動，powercfg 報告沒有這個欄位）。鋰電池即使少用也會隨時間自然老化，役齡可用來判斷目前的衰退幅度是偏快還是正常。"
                });
            }

            tips.Add(new DiagnosticItem
            {
                Type = "tip",
                Title = "鋰電池保養指南",
                Description = "若長期連接 AC 電源使用，建議啟用原廠筆電軟體的「充電上限保護 (80% 充電)」以維護鋰電池壽命。"
            });

            return tips;
        }

        /// <summary>
        /// 清除 HTML 標籤、HtmlDecode 並整合連續空白，傳回乾淨文字。
        /// </summary>
        private static string StripTags(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            string clean = Regex.Replace(input, @"<[^>]+>", " ");
            clean = System.Net.WebUtility.HtmlDecode(clean);
            return Regex.Replace(clean, @"\s+", " ").Trim();
        }

        /// <summary>
        /// 從字串中提取所有數字並轉為整數。
        /// </summary>
        private static int ExtractNumber(string str)
        {
            if (string.IsNullOrWhiteSpace(str)) return 0;
            string digits = Regex.Replace(str, @"[^\d]", "");
            return int.TryParse(digits, out int result) ? result : 0;
        }
    }
}
