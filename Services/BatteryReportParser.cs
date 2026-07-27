using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinBatLens.Models;

namespace WinBatLens.Services
{
    public class BatteryReportParser
    {
        /// <summary>
        /// Parses a powercfg battery report, optionally overlaying what the
        /// battery driver reports right now.
        /// </summary>
        /// <param name="pack">
        /// Live pack information from <see cref="BatteryTelemetryService"/>, or
        /// null to parse the report on its own (as when the user opens someone
        /// else's saved HTML report).
        /// </param>
        public static BatteryReportData Parse(string htmlContent, BatteryTelemetryService.PackInfo? pack = null)
        {
            var data = new BatteryReportData();

            data.SystemInfo = ParseSystemInfo(htmlContent);
            data.BatterySpecs = ParseBatterySpecs(htmlContent);
            data.CapacityHistory = ParseCapacityHistory(htmlContent);
            data.UsageHistory = ParseUsageHistory(htmlContent);
            data.BatteryLifeEstimates = ParseBatteryLifeEstimates(htmlContent);
            data.RecentUsage = ParseRecentUsage(htmlContent);

            // Must run before the metrics: health, wear and every diagnostic is
            // computed from the merged specs.
            if (pack != null && pack.IsValid) OverlayDriverSpecs(data.BatterySpecs, pack);

            // Compute metrics & diagnostics
            data.HealthMetrics = CalculateHealthMetrics(data.BatterySpecs);
            data.Diagnostics = GenerateDiagnostics(data);

            return data;
        }

        /// <summary>
        /// Prefers the battery driver's live figures over the report's snapshot.
        /// </summary>
        /// <remarks>
        /// The report is generated from data Windows logged earlier, so its
        /// full-charge capacity lags: measured on the development machine the
        /// report said 55,969 mWh while the driver said 56,032 mWh. The driver
        /// also fills in fields the report leaves blank — cycle count and the
        /// manufacture date, which powercfg has no column for at all — so a
        /// machine whose report is empty or unparseable still gets a real
        /// health score instead of being reported as having no battery.
        /// </remarks>
        private static void OverlayDriverSpecs(BatterySpecs specs, BatteryTelemetryService.PackInfo pack)
        {
            // A relative-capacity pack reports capacities in its own arbitrary
            // units, and a report in mAh cannot be mixed with the driver's mWh.
            // In both cases the ratio is still sound, so health is left to the
            // report's own numbers rather than combining incompatible units.
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

            // Cycle count is blank in the report on a great many laptops; take
            // the driver's whenever it has one.
            if (pack.CycleCount.HasValue) specs.CycleCount = pack.CycleCount;

            specs.ManufactureDate = pack.ManufactureDate;

            // Identity: keep whatever the report gave, since powercfg tends to
            // carry the friendlier string, and fall back to the driver's.
            if (IsBlank(specs.Chemistry) && !string.IsNullOrWhiteSpace(pack.Chemistry))
                specs.Chemistry = pack.Chemistry;

            if (IsBlank(specs.Name) && !string.IsNullOrWhiteSpace(pack.DeviceName))
                specs.Name = pack.DeviceName;

            if (IsBlank(specs.Manufacturer) && !string.IsNullOrWhiteSpace(pack.ManufactureName))
                specs.Manufacturer = pack.ManufactureName;
        }

        /// <summary>
        /// True for an empty cell or one of the defaults the model starts with,
        /// which are placeholders rather than parsed values.
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

        private static BatterySpecs ParseBatterySpecs(string html)
        {
            var specs = new BatterySpecs();

            // Locate INSTALLED BATTERIES section specifically
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

        private static HealthMetrics CalculateHealthMetrics(BatterySpecs specs)
        {
            // No design capacity means the battery-report contains no battery at
            // all (e.g. a desktop PC, or the battery was removed). Report a neutral
            // "no battery" state instead of a misleading 0% "critically degraded"
            // score, which is what the old (current/1.0)*100 formula produced.
            if (specs.DesignCapacity <= 0)
            {
                return new HealthMetrics
                {
                    HasBattery = false,
                    HealthPercent = 0,
                    WearPercent = 0,
                    CapacityLoss = 0,
                    StatusLabel = "無電池裝置",
                    StatusClass = "None",
                    SummaryText = "未偵測到電池，本機可能為桌上型電腦或電池已卸除。電池健康度數據不適用，但全系統即時硬體功耗監測仍可正常使用。"
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
                HealthPercent = healthPercent,
                WearPercent = wearPercent,
                CapacityLoss = Math.Max(0, specs.DesignCapacity - specs.FullChargeCapacity),
                StatusLabel = statusLabel,
                StatusClass = statusClass,
                SummaryText = summaryText
            };
        }

        private static List<DiagnosticItem> GenerateDiagnostics(BatteryReportData report)
        {
            var tips = new List<DiagnosticItem>();
            var metrics = report.HealthMetrics;
            var specs = report.BatterySpecs;

            // Desktop / no-battery machine: skip all degradation warnings that
            // would otherwise be driven by a bogus 0% health score.
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
                // Worth saying out loud: a blank cycle count is the pack's
                // firmware declining to keep the tally, not a read failure.
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

        private static string StripTags(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            string clean = Regex.Replace(input, @"<[^>]+>", " ");
            clean = System.Net.WebUtility.HtmlDecode(clean);
            // powercfg splits date ranges across source lines, e.g.
            // "2026-07-12\n - 2026-07-18". Those newlines survive into the cell
            // text and make a TextBlock render two lines, so collapse every run
            // of whitespace into a single space.
            return Regex.Replace(clean, @"\s+", " ").Trim();
        }

        private static int ExtractNumber(string str)
        {
            if (string.IsNullOrWhiteSpace(str)) return 0;
            string digits = Regex.Replace(str, @"[^\d]", "");
            return int.TryParse(digits, out int result) ? result : 0;
        }
    }
}
