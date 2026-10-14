//! Windows 電池報告 HTML 檔案（powercfg /batteryreport）之解析與資料提取。
//! 支援搭配即時電池驅動遙測數據（[`PackInfo`]）進行綜合健康度分析與覆蓋。

use std::sync::LazyLock;

use chrono::Local;
use regex::Regex;

use crate::battery::PackInfo;
use crate::models::*;

fn re(pattern: &str) -> Regex {
    Regex::new(pattern).expect("built-in pattern is valid")
}

/// 「標題 …… 第一個 <td> 內容」形式的欄位擷取樣式。
fn field_re(label: &str) -> Regex {
    re(&format!(r"(?is){label}.*?<td[^>]*>(.*?)</td>"))
}

/// 「區段標題 …… 第一個 <table> 內容」形式的區段擷取樣式。
fn section_re(heading: &str) -> Regex {
    re(&format!(r"(?is){heading}.*?<table[^>]*>(.*?)</table>"))
}

static COMPUTER_NAME: LazyLock<Regex> = LazyLock::new(|| field_re("COMPUTER NAME"));
static PRODUCT_NAME: LazyLock<Regex> = LazyLock::new(|| field_re("SYSTEM PRODUCT NAME"));
static BIOS: LazyLock<Regex> = LazyLock::new(|| field_re("BIOS"));
static OS_BUILD: LazyLock<Regex> = LazyLock::new(|| field_re("OS BUILD"));
static REPORT_TIME: LazyLock<Regex> = LazyLock::new(|| field_re("REPORT TIME"));

static NAME: LazyLock<Regex> = LazyLock::new(|| field_re("NAME"));
static MANUFACTURER: LazyLock<Regex> = LazyLock::new(|| field_re("MANUFACTURER"));
static SERIAL_NUMBER: LazyLock<Regex> = LazyLock::new(|| field_re("SERIAL NUMBER"));
static CHEMISTRY: LazyLock<Regex> = LazyLock::new(|| field_re("CHEMISTRY"));
static DESIGN_CAPACITY: LazyLock<Regex> = LazyLock::new(|| field_re("DESIGN CAPACITY"));
static FULL_CHARGE_CAPACITY: LazyLock<Regex> = LazyLock::new(|| field_re("FULL CHARGE CAPACITY"));
static CYCLE_COUNT: LazyLock<Regex> = LazyLock::new(|| field_re("CYCLE COUNT"));

static INSTALLED_BATTERIES: LazyLock<Regex> = LazyLock::new(|| section_re("INSTALLED BATTERIES"));
static CAPACITY_HISTORY: LazyLock<Regex> = LazyLock::new(|| section_re("BATTERY CAPACITY HISTORY"));
static USAGE_HISTORY: LazyLock<Regex> = LazyLock::new(|| section_re("USAGE HISTORY"));
static LIFE_ESTIMATES: LazyLock<Regex> = LazyLock::new(|| section_re("BATTERY LIFE ESTIMATES"));
static RECENT_USAGE: LazyLock<Regex> = LazyLock::new(|| section_re("RECENT USAGE"));

static TR: LazyLock<Regex> = LazyLock::new(|| re(r"(?is)<tr[^>]*>(.*?)</tr>"));
static TD: LazyLock<Regex> = LazyLock::new(|| re(r"(?is)<td[^>]*>(.*?)</td>"));
static TAG: LazyLock<Regex> = LazyLock::new(|| re(r"<[^>]+>"));
static WHITESPACE: LazyLock<Regex> = LazyLock::new(|| re(r"\s+"));

/// 解析 powercfg 電池報告 HTML 內容，並可選擇性疊加即時電池驅動遙測資料。
///
/// `pack` 為 None 時僅解析報告本文（例如使用者開啟外部 HTML 檔案時）。
pub fn parse(html: &str, pack: Option<&PackInfo>) -> BatteryReportData {
    let mut data = BatteryReportData {
        system_info: parse_system_info(html),
        battery_specs: parse_battery_specs(html),
        capacity_history: parse_capacity_history(html),
        usage_history: parse_usage_history(html),
        battery_life_estimates: parse_battery_life_estimates(html),
        recent_usage: parse_recent_usage(html),
        loaded_at: Local::now().format("%Y-%m-%dT%H:%M:%S").to_string(),
        ..Default::default()
    };

    // 必須在計算指標前執行：健康度、損耗與診斷皆依據合併後的規格計算
    if let Some(pack) = pack.filter(|pack| pack.is_valid) {
        overlay_driver_specs(&mut data.battery_specs, pack);
    }

    data.health_metrics = calculate_health_metrics(&data.battery_specs);
    data.diagnostics = generate_diagnostics(&data);
    data
}

/// 以即時電池驅動數據覆蓋 powercfg 報告數據，取得最新且精確之容量與製造日期資訊。
fn overlay_driver_specs(specs: &mut BatterySpecs, pack: &PackInfo) {
    // 檢查單位是否一致（避免 mWh 與 mAh 混用）
    let units_match = !pack.is_capacity_relative && (specs.unit == "mWh" || specs.design_capacity <= 0);

    if units_match {
        if pack.designed_capacity_mwh > 0 {
            specs.design_capacity = pack.designed_capacity_mwh;
            specs.unit = "mWh".into();
            specs.capacities_from_driver = true;
        }

        if pack.full_charged_capacity_mwh > 0 {
            specs.full_charge_capacity = pack.full_charged_capacity_mwh;
            specs.unit = "mWh".into();
            specs.capacities_from_driver = true;
        }
    }

    // 充電循環次數：若報告中缺失則採用驅動程式讀取值
    if pack.cycle_count.is_some() {
        specs.cycle_count = pack.cycle_count;
    }

    specs.manufacture_date = pack.manufacture_date;
    specs.age_years = pack.manufacture_date.map(age_years);

    // 識別資訊：優先保留報告文字，若為空則退回採用驅動程式回報值
    if is_blank(&specs.chemistry) && !pack.chemistry.trim().is_empty() {
        specs.chemistry = pack.chemistry.clone();
    }
    if is_blank(&specs.name) && !pack.device_name.trim().is_empty() {
        specs.name = pack.device_name.clone();
    }
    if is_blank(&specs.manufacturer) && !pack.manufacture_name.trim().is_empty() {
        specs.manufacturer = pack.manufacture_name.clone();
    }
}

/// 判斷字串是否為空或是預設佔位符（N/A、Primary Battery、Windows PC 等）。
fn is_blank(value: &str) -> bool {
    matches!(value.trim(), "" | "N/A" | "Primary Battery" | "Windows PC" | "Li-ion")
}

fn capture(pattern: &Regex, html: &str) -> Option<String> {
    pattern.captures(html).map(|caps| strip_tags(&caps[1]))
}

/// 自 HTML 解析系統基本資訊（電腦名稱、產品名稱、BIOS、OS 版本與報告時間）。
fn parse_system_info(html: &str) -> SystemInfo {
    let mut info = SystemInfo::default();

    if let Some(value) = capture(&COMPUTER_NAME, html) {
        info.computer_name = value;
    }
    if let Some(value) = capture(&PRODUCT_NAME, html) {
        info.system_product_name = value;
    }
    if let Some(value) = capture(&BIOS, html) {
        info.bios = value;
    }
    if let Some(value) = capture(&OS_BUILD, html) {
        info.os_build = value;
    }
    if let Some(value) = capture(&REPORT_TIME, html) {
        info.report_time = value;
    }

    info
}

/// 自 HTML 解析已安裝電池規格（名稱、製造商、序號、化學材質、設計容量、滿充容量、循環次數）。
fn parse_battery_specs(html: &str) -> BatterySpecs {
    let mut specs = BatterySpecs::default();

    // 定位 INSTALLED BATTERIES 區塊
    let block = INSTALLED_BATTERIES.captures(html).map(|caps| caps.get(1).map_or("", |m| m.as_str())).unwrap_or(html);

    if let Some(value) = capture(&NAME, block) {
        if !value.to_uppercase().contains("COMPUTER") {
            specs.name = value;
        }
    }
    if let Some(value) = capture(&MANUFACTURER, block) {
        specs.manufacturer = value;
    }
    if let Some(value) = capture(&SERIAL_NUMBER, block) {
        specs.serial_number = value;
    }
    if let Some(value) = capture(&CHEMISTRY, block) {
        specs.chemistry = value;
    }
    if let Some(raw) = capture(&DESIGN_CAPACITY, block) {
        specs.design_capacity = extract_number(&raw);
        if raw.to_lowercase().contains("mah") {
            specs.unit = "mAh".into();
        }
    }
    if let Some(raw) = capture(&FULL_CHARGE_CAPACITY, block) {
        specs.full_charge_capacity = extract_number(&raw);
    }
    if let Some(raw) = capture(&CYCLE_COUNT, block) {
        let value = extract_number(&raw);
        specs.cycle_count = (value > 0).then_some(value);
    }

    specs
}

/// 傳回指定區段表格中，每一列的儲存格文字（已去除標籤）。
fn table_rows(section: &Regex, html: &str) -> Vec<Vec<String>> {
    let Some(caps) = section.captures(html) else {
        return Vec::new();
    };

    TR.captures_iter(&caps[1]).map(|row| TD.captures_iter(&row[1]).map(|cell| strip_tags(&cell[1])).collect()).collect()
}

/// 解析歷史電池容量變遷表格。
fn parse_capacity_history(html: &str) -> Vec<CapacityHistoryItem> {
    table_rows(&CAPACITY_HISTORY, html)
        .into_iter()
        .filter(|cells| cells.len() >= 3)
        .filter_map(|cells| {
            let period = cells[0].clone();
            let full_charge = extract_number(&cells[1]);
            let design = extract_number(&cells[2]);

            (!period.trim().is_empty() && full_charge > 0 && design > 0 && !period.to_uppercase().contains("PERIOD"))
                .then(|| CapacityHistoryItem::new(period, full_charge, design))
        })
        .collect()
}

/// 解析歷史使用時間統計表格（電池使用時間 vs 插電時間）。
fn parse_usage_history(html: &str) -> Vec<UsageHistoryItem> {
    table_rows(&USAGE_HISTORY, html)
        .into_iter()
        .filter(|cells| cells.len() >= 3 && is_data_row(&cells[0], "PERIOD"))
        .map(|cells| UsageHistoryItem {
            period: cells[0].clone(),
            battery_duration: cells[1].clone(),
            ac_duration: cells[2].clone(),
        })
        .collect()
}

/// 解析電池續航估算表格。
fn parse_battery_life_estimates(html: &str) -> Vec<BatteryLifeEstimateItem> {
    table_rows(&LIFE_ESTIMATES, html)
        .into_iter()
        .filter(|cells| cells.len() >= 3 && is_data_row(&cells[0], "PERIOD"))
        .map(|cells| BatteryLifeEstimateItem {
            period: cells[0].clone(),
            full_charge_estimate: cells[1].clone(),
            design_cap_estimate: cells[2].clone(),
        })
        .collect()
}

/// 解析近期使用歷程紀錄表格。
fn parse_recent_usage(html: &str) -> Vec<RecentUsageItem> {
    table_rows(&RECENT_USAGE, html)
        .into_iter()
        .filter(|cells| cells.len() >= 4 && is_data_row(&cells[0], "START TIME"))
        .map(|cells| RecentUsageItem {
            start_time: cells[0].clone(),
            state: cells[1].clone(),
            source: cells[2].clone(),
            capacity_remaining: cells[3].clone(),
        })
        .collect()
}

fn is_data_row(first_cell: &str, header_marker: &str) -> bool {
    !first_cell.trim().is_empty() && !first_cell.to_uppercase().contains(header_marker)
}

/// 計算健康度指標（健康百分比、損耗率、容量差額與狀態等級說明）。
fn calculate_health_metrics(specs: &BatterySpecs) -> HealthMetrics {
    // 若無設計容量，代表此裝置無電池（如桌上型電腦或電池已拔除）
    if specs.design_capacity <= 0 {
        return HealthMetrics {
            has_battery: false,
            is_health_measured: false,
            status_label: "無電池裝置".into(),
            status_class: StatusClass::None,
            summary_text: "未偵測到電池，本機可能為桌上型電腦或電池已卸除。電池健康度數據不適用，但全系統即時硬體功耗監測仍可正常使用。".into(),
            ..Default::default()
        };
    }

    if specs.full_charge_capacity <= 0 {
        return HealthMetrics {
            has_battery: true,
            is_health_measured: false,
            status_label: "無法判定".into(),
            status_class: StatusClass::None,
            summary_text: "電池缺少滿電容量資料，無法計算健康度。".into(),
            ..Default::default()
        };
    }

    let design = specs.design_capacity as f64;
    let current = specs.full_charge_capacity as f64;

    let health_percent = round1(current / design * 100.0).min(100.0);
    let wear_percent = round1(100.0 - health_percent).max(0.0);

    let (status_label, status_class, summary_text) = if health_percent < 60.0 {
        ("嚴重衰退", StatusClass::Danger, "滿電容量已衰退超過 40%，建議安排更換電池以確保續航力與電源穩定度。")
    } else if health_percent < 80.0 {
        ("需要注意", StatusClass::Warning, "電池有些微損耗，續航時間可能已縮短，請注意散熱與充放電習慣。")
    } else {
        ("健康良好", StatusClass::Good, "電池狀態優良，蓄電與充電發揮理想效能。")
    };

    HealthMetrics {
        has_battery: true,
        is_health_measured: true,
        health_percent,
        wear_percent,
        capacity_loss: (specs.design_capacity - specs.full_charge_capacity).max(0),
        status_label: status_label.into(),
        status_class,
        summary_text: summary_text.into(),
    }
}

/// 根據電池報告數據與指標，自動產生健康與維護診斷提示清單。
fn generate_diagnostics(report: &BatteryReportData) -> Vec<DiagnosticItem> {
    let metrics = &report.health_metrics;
    let specs = &report.battery_specs;
    let tip = |kind, title: String, description: String| DiagnosticItem { kind, title, description };

    if !metrics.has_battery {
        return vec![tip(
            DiagnosticKind::Info,
            "未偵測到電池裝置".into(),
            "本機可能為桌上型電腦，或電池已被卸除，因此電池健康度、容量與循環次數等數據不適用。全系統即時硬體功耗監測功能仍可正常運作。".into(),
        )];
    }

    if !metrics.is_health_measured {
        return vec![tip(
            DiagnosticKind::Info,
            "健康度無法判定".into(),
            "報告缺少設計容量或滿電容量，未顯示虛假的健康百分比。".into(),
        )];
    }

    let mut tips = Vec::new();
    let health = metrics.health_percent;

    if health < 70.0 {
        tips.push(tip(
            DiagnosticKind::Danger,
            "電池容量顯著衰退".into(),
            format!("目前最高容量僅為原廠設計值的 {health}%，外出的實際使用時間將有所減少。"),
        ));
    } else if health < 80.0 {
        tips.push(tip(
            DiagnosticKind::Warning,
            "電池進入磨耗階段".into(),
            format!(
                "滿電容量為原廠設計值的 {health}%，累積損耗量達 {} {}。",
                thousands(metrics.capacity_loss as i64),
                specs.unit
            ),
        ));
    } else {
        tips.push(tip(
            DiagnosticKind::Success,
            "電池健康狀況優異".into(),
            format!("最高滿電容量達到原廠設計值的 {health}%，性能表現非常良好。"),
        ));
    }

    match specs.cycle_count {
        Some(cycles) if cycles > 500 => tips.push(tip(
            DiagnosticKind::Warning,
            "充放電循環次數較高".into(),
            format!("目前累積 {cycles} 次循環。大多數鋰電池在 300-500 次循環後容量會逐漸降低。"),
        )),
        Some(cycles) => tips.push(tip(
            DiagnosticKind::Info,
            "充放電循環狀態正常".into(),
            format!("目前已累積 {cycles} 次循環。"),
        )),
        None => tips.push(tip(
            DiagnosticKind::Info,
            "此電池未回報循環次數".into(),
            "powercfg 報告與電池驅動 (IOCTL_BATTERY_QUERY_INFORMATION) 都沒有循環次數，代表這顆電池的韌體並未實作該欄位，而不是讀取失敗。".into(),
        )),
    }

    if let (Some(years), Some(date)) = (specs.age_years, specs.manufacture_date) {
        tips.push(tip(
            if years >= 4.0 { DiagnosticKind::Warning } else { DiagnosticKind::Info },
            format!("電池役齡約 {years:.1} 年"),
            format!(
                "電芯出廠日期為 {}（讀自電池驅動，powercfg 報告沒有這個欄位）。鋰電池即使少用也會隨時間自然老化，役齡可用來判斷目前的衰退幅度是偏快還是正常。",
                date.format("%Y-%m-%d")
            ),
        ));
    }

    tips.push(tip(
        DiagnosticKind::Tip,
        "鋰電池保養指南".into(),
        "若長期連接 AC 電源使用，建議啟用原廠筆電軟體的「充電上限保護 (80% 充電)」以維護鋰電池壽命。".into(),
    ));

    tips
}

/// 清除 HTML 標籤、解碼 HTML 實體並整合連續空白，傳回乾淨文字。
fn strip_tags(input: &str) -> String {
    if input.trim().is_empty() {
        return String::new();
    }
    let clean = TAG.replace_all(input, " ");
    let clean = html_decode(&clean);
    WHITESPACE.replace_all(&clean, " ").trim().to_string()
}

/// 解碼 powercfg 報告實際會出現的 HTML 實體（具名常用實體與數字實體）。
fn html_decode(input: &str) -> String {
    if !input.contains('&') {
        return input.to_string();
    }

    let mut out = String::with_capacity(input.len());
    let mut rest = input;

    while let Some(start) = rest.find('&') {
        out.push_str(&rest[..start]);
        let tail = &rest[start..];

        // 實體最長不過十來個字元；找不到分號就當成一般的 & 字元。
        let decoded = tail
            .char_indices()
            .take(12)
            .find(|(_, ch)| *ch == ';')
            .and_then(|(end, _)| decode_entity(&tail[1..end]).map(|ch| (ch, end)));

        match decoded {
            Some((ch, end)) => {
                out.push(ch);
                rest = &tail[end + 1..];
            }
            None => {
                out.push('&');
                rest = &tail[1..];
            }
        }
    }

    out.push_str(rest);
    out
}

fn decode_entity(name: &str) -> Option<char> {
    match name {
        "amp" => Some('&'),
        "lt" => Some('<'),
        "gt" => Some('>'),
        "quot" => Some('"'),
        "apos" => Some('\''),
        "nbsp" => Some('\u{a0}'),
        _ => {
            let number = name.strip_prefix('#')?;
            let code = match number.strip_prefix(['x', 'X']) {
                Some(hex) => u32::from_str_radix(hex, 16).ok()?,
                None => number.parse().ok()?,
            };
            char::from_u32(code)
        }
    }
}

/// 從字串中提取所有數字並轉為整數。
fn extract_number(text: &str) -> i32 {
    let digits: String = text.chars().filter(char::is_ascii_digit).collect();
    digits.parse().unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use chrono::NaiveDate;

    use super::*;

    const REPORT: &str = r#"
        <html><body>
        <h2>COMPUTER NAME</h2><table><tr><td>ROG-TEST</td></tr></table>
        <h2>SYSTEM PRODUCT NAME</h2><table><tr><td>Test Laptop</td></tr></table>
        <h2>REPORT TIME</h2><table><tr><td>2026-08-03 12:00:00</td></tr></table>
        <h2>INSTALLED BATTERIES</h2>
        <table>
          <tr><th>NAME</th><td>ASUS Battery</td></tr>
          <tr><th>MANUFACTURER</th><td>ASUSTeK</td></tr>
          <tr><th>SERIAL NUMBER</th><td>ABC123</td></tr>
          <tr><th>CHEMISTRY</th><td>Li-ion</td></tr>
          <tr><th>DESIGN CAPACITY</th><td>76,000 mWh</td></tr>
          <tr><th>FULL CHARGE CAPACITY</th><td>56,000 mWh</td></tr>
          <tr><th>CYCLE COUNT</th><td>0</td></tr>
        </table>
        <h2>BATTERY CAPACITY HISTORY</h2>
        <table>
          <tr><th>PERIOD</th><th>FULL CHARGE CAPACITY</th><th>DESIGN CAPACITY</th></tr>
          <tr><td>2026-07-12
            - 2026-07-19</td><td>55,900 mWh</td><td>76,000 mWh</td></tr>
        </table>
        <h2>BATTERY LIFE ESTIMATES</h2>
        <table>
          <tr><td>PERIOD</td><td>AT FULL CHARGE</td><td>AT DESIGN CAPACITY</td></tr>
          <tr><td>2026-07-19</td><td>3:10:00</td><td>4:20:00</td></tr>
        </table>
        <h2>RECENT USAGE</h2>
        <table>
          <tr><td>START TIME</td><td>STATE</td><td>SOURCE</td><td>CAPACITY REMAINING</td></tr>
          <tr><td>2026-08-03 09:00:00</td><td>Active</td><td>Battery</td><td>80 % &amp; 44,800 mWh</td></tr>
        </table>
        </body></html>
        "#;

    /// 測試正確解析規格、歷史紀錄並轉置換行空白。
    #[test]
    fn parses_specs_history_and_normalizes_whitespace() {
        let report = parse(REPORT, None);

        assert_eq!(report.system_info.computer_name, "ROG-TEST");
        assert_eq!(report.system_info.system_product_name, "Test Laptop");
        assert_eq!(report.system_info.report_time, "2026-08-03 12:00:00");
        assert_eq!(report.battery_specs.name, "ASUS Battery");
        assert_eq!(report.battery_specs.manufacturer, "ASUSTeK");
        assert_eq!(report.battery_specs.design_capacity, 76_000);
        assert_eq!(report.battery_specs.full_charge_capacity, 56_000);
        assert_eq!(report.battery_specs.cycle_count, None);
        assert!(report.health_metrics.has_battery);
        assert_eq!(report.health_metrics.health_percent, 73.7);
        assert_eq!(report.health_metrics.status_class, StatusClass::Warning);
        assert_eq!(report.capacity_history.len(), 1);
        assert_eq!(report.capacity_history[0].period, "2026-07-12 - 2026-07-19");
        assert_eq!(report.capacity_history[0].health_percent, 73.6);
    }

    #[test]
    fn parses_estimate_and_usage_tables_without_their_header_rows() {
        let report = parse(REPORT, None);

        assert_eq!(report.battery_life_estimates.len(), 1);
        assert_eq!(report.battery_life_estimates[0].full_charge_estimate, "3:10:00");
        assert_eq!(report.battery_life_estimates[0].design_cap_estimate, "4:20:00");
        assert_eq!(report.recent_usage.len(), 1);
        assert_eq!(report.recent_usage[0].source, "Battery");
        assert_eq!(report.recent_usage[0].capacity_remaining, "80 % & 44,800 mWh");
    }

    /// 測試結合驅動程式實測 PackInfo 進行數值覆蓋。
    #[test]
    fn overlays_compatible_live_driver_pack_info() {
        let pack = PackInfo {
            is_valid: true,
            designed_capacity_mwh: 75_998,
            full_charged_capacity_mwh: 56_032,
            cycle_count: Some(123),
            chemistry: "LION".into(),
            manufacture_date: NaiveDate::from_ymd_opt(2023, 1, 2),
            ..Default::default()
        };

        let report = parse(REPORT, Some(&pack));

        assert_eq!(report.battery_specs.design_capacity, 75_998);
        assert_eq!(report.battery_specs.full_charge_capacity, 56_032);
        assert_eq!(report.battery_specs.cycle_count, Some(123));
        assert!(report.battery_specs.capacities_from_driver);
        assert_eq!(report.battery_specs.manufacture_date, NaiveDate::from_ymd_opt(2023, 1, 2));
        assert!(report.battery_specs.age_years.is_some());
        assert_eq!(report.health_metrics.health_percent, 73.7);
    }

    /// 測試當報告單位為 mAh 時，不強行混合 mWh 的驅動實測數據。
    #[test]
    fn does_not_mix_driver_mwh_with_report_mah() {
        const MAH_REPORT: &str = r#"
            <h2>INSTALLED BATTERIES</h2><table>
            <tr><th>DESIGN CAPACITY</th><td>10,000 mAh</td></tr>
            <tr><th>FULL CHARGE CAPACITY</th><td>9,000 mAh</td></tr>
            </table>
            "#;
        let pack = PackInfo {
            is_valid: true,
            designed_capacity_mwh: 80_000,
            full_charged_capacity_mwh: 50_000,
            ..Default::default()
        };

        let report = parse(MAH_REPORT, Some(&pack));

        assert_eq!(report.battery_specs.unit, "mAh");
        assert_eq!(report.battery_specs.design_capacity, 10_000);
        assert_eq!(report.battery_specs.full_charge_capacity, 9_000);
        assert!(!report.battery_specs.capacities_from_driver);
    }

    /// 測試當滿電容量缺失時，不會誤判為 0% 健康度。
    #[test]
    fn does_not_treat_missing_full_charge_capacity_as_zero_percent_health() {
        const INCOMPLETE_REPORT: &str = r#"
            <h2>INSTALLED BATTERIES</h2><table>
            <tr><th>DESIGN CAPACITY</th><td>50,000 mWh</td></tr>
            <tr><th>FULL CHARGE CAPACITY</th><td>--</td></tr>
            </table>
            "#;

        let report = parse(INCOMPLETE_REPORT, None);

        assert!(report.health_metrics.has_battery);
        assert!(!report.health_metrics.is_health_measured);
        assert_eq!(report.health_metrics.status_label, "無法判定");
        assert_eq!(report.diagnostics.len(), 1);
        assert_eq!(report.diagnostics[0].kind, DiagnosticKind::Info);
        assert_eq!(report.diagnostics[0].title, "健康度無法判定");
    }

    /// 測試無電池裝置（例如桌上型電腦）時顯示中性診斷訊息。
    #[test]
    fn reports_neutral_diagnostics_when_no_battery_exists() {
        let report = parse("<html><body><h1>Desktop report</h1></body></html>", None);

        assert!(!report.health_metrics.has_battery);
        assert_eq!(report.diagnostics.len(), 1);
        assert_eq!(report.diagnostics[0].kind, DiagnosticKind::Info);
        assert!(report.diagnostics[0].title.contains("未偵測到電池"));
    }

    #[test]
    fn decodes_html_entities() {
        assert_eq!(html_decode("a &amp; b &lt;c&gt; &#65;&#x42; &nope; & end"), "a & b <c> AB &nope; & end");
        assert_eq!(strip_tags("<span>  76,000&nbsp;mWh </span>"), "76,000 mWh");
    }

    #[test]
    fn exports_pascal_case_json() {
        let json = serde_json::to_string(&parse(REPORT, None)).unwrap();
        assert!(json.contains("\"BatterySpecs\""));
        assert!(json.contains("\"FullChargeCapacity\":56000"));
        assert!(json.contains("\"Type\":\"warning\""));
    }
}
