//! 深色儀表風格的色彩、字型與共用外框。
//!
//! 語意顏色只用在真實遙測上：綠色代表充電或省電、紅色代表耗電、藍色是獨顯
//! 數列、琥珀色是兩者皆非的電池數值；青色保留給互動元件。

use std::sync::Arc;

use eframe::egui::{
    self, Color32, CornerRadius, FontData, FontDefinitions, FontFamily, FontId, Frame, Margin, RichText, Stroke,
    TextStyle, Ui,
};

use crate::models::StatusClass;

pub const BG_DARK: Color32 = Color32::from_rgb(0x07, 0x0A, 0x0E);
pub const BG_CARD: Color32 = Color32::from_rgb(0x0E, 0x14, 0x1D);
pub const BG_RAISED: Color32 = Color32::from_rgb(0x13, 0x1C, 0x27);
pub const BG_INSET: Color32 = Color32::from_rgb(0x0A, 0x0E, 0x15);
pub const BG_CONTROL: Color32 = Color32::from_rgb(0x15, 0x1E, 0x2A);
pub const BG_CONTROL_HOVER: Color32 = Color32::from_rgb(0x1E, 0x2A, 0x3A);

pub const BORDER: Color32 = Color32::from_rgb(0x22, 0x30, 0x41);
pub const BORDER_STRONG: Color32 = Color32::from_rgb(0x37, 0x4B, 0x61);
pub const BORDER_SUBTLE: Color32 = Color32::from_rgb(0x17, 0x22, 0x2E);

pub const ACCENT: Color32 = Color32::from_rgb(0x38, 0xBD, 0xF8);
pub const ACCENT_DARK: Color32 = Color32::from_rgb(0x02, 0x84, 0xC7);

/// 紅：耗電 / 放電。
pub const ROSE: Color32 = Color32::from_rgb(0xFB, 0x71, 0x85);
/// 綠：充電 / 省電 / 健康。
pub const EMERALD: Color32 = Color32::from_rgb(0x34, 0xD3, 0x99);
/// 藍：獨顯數列。
pub const GPU_BLUE: Color32 = Color32::from_rgb(0x60, 0xA5, 0xFA);
/// 琥珀：介於兩者之間。
pub const AMBER: Color32 = Color32::from_rgb(0xFB, 0xBF, 0x24);
pub const SLATE: Color32 = Color32::from_rgb(0x94, 0xA3, 0xB8);

pub const TEXT_MAIN: Color32 = Color32::from_rgb(0xF1, 0xF5, 0xF9);
pub const TEXT_MUTED: Color32 = Color32::from_rgb(0x94, 0xA3, 0xB8);
pub const TEXT_DIM: Color32 = Color32::from_rgb(0x64, 0x74, 0x8B);

/// 同一顏色的半透明版本，用於徽章底色與面積填滿。
pub fn tint(color: Color32, alpha: u8) -> Color32 {
    Color32::from_rgba_unmultiplied(color.r(), color.g(), color.b(), alpha)
}

/// The colour for a health percentage. The 80% / 60% boundaries are the ones
/// the report parser uses to pick the status wording, so the ring can never
/// show green next to a label that says the pack is degraded.
pub fn health_color(percent: f64) -> Color32 {
    if percent < 60.0 {
        ROSE
    } else if percent < 80.0 {
        AMBER
    } else {
        EMERALD
    }
}

pub fn status_color(class: StatusClass) -> Color32 {
    match class {
        StatusClass::Good => EMERALD,
        StatusClass::Warning => AMBER,
        StatusClass::Danger => ROSE,
        StatusClass::None => SLATE,
    }
}

/// Green below `idle_below`, red at or above `busy_at_or_above`, amber in
/// between — the saving/spending scale used by the hardware rows.
pub fn load_color(value: f64, idle_below: f64, busy_at_or_above: f64) -> Color32 {
    if value < idle_below {
        EMERALD
    } else if value < busy_at_or_above {
        AMBER
    } else {
        ROSE
    }
}

/// Grades the active Windows power plan by what it costs. Matched on both
/// languages because PowrProf hands back whatever the plan is named on this
/// machine, and custom OEM plans keep the English word even on a Chinese
/// Windows.
pub fn power_plan_color(plan_name: &str) -> Color32 {
    let lower = plan_name.to_lowercase();
    if plan_name.trim().is_empty() {
        SLATE
    } else if plan_name.contains("效能") || lower.contains("performance") || lower.contains("ultimate") {
        ROSE
    } else if plan_name.contains("省電")
        || plan_name.contains("節能")
        || lower.contains("saver")
        || lower.contains("eco")
    {
        EMERALD
    } else {
        AMBER
    }
}

/// 卡片外框。
pub fn card() -> Frame {
    Frame::new()
        .fill(BG_CARD)
        .stroke(Stroke::new(1.0, BORDER))
        .corner_radius(CornerRadius::same(10))
        .inner_margin(Margin::same(14))
}

/// 卡片內的次級區塊。
pub fn inset() -> Frame {
    Frame::new()
        .fill(BG_INSET)
        .stroke(Stroke::new(1.0, BORDER_SUBTLE))
        .corner_radius(CornerRadius::same(8))
        .inner_margin(Margin::same(10))
}

/// 卡片標題（小型大寫風格的灰色標籤）。
pub fn card_title(ui: &mut Ui, text: &str) {
    ui.label(RichText::new(text).size(12.5).color(TEXT_MUTED).strong());
}

pub fn muted(text: impl Into<String>) -> RichText {
    RichText::new(text).size(12.0).color(TEXT_MUTED)
}

pub fn dim(text: impl Into<String>) -> RichText {
    RichText::new(text).size(11.5).color(TEXT_DIM)
}

/// 語意狀態徽章（藥丸形）。
pub fn badge(ui: &mut Ui, text: &str, color: Color32) -> egui::Response {
    Frame::new()
        .fill(tint(color, 0x20))
        .stroke(Stroke::new(1.0, color))
        .corner_radius(CornerRadius::same(255))
        .inner_margin(Margin::symmetric(10, 3))
        .show(ui, |ui| ui.label(RichText::new(text).size(12.0).color(color).strong()))
        .response
}

/// 數值用的等寬數字字型。
pub fn mono(size: f32) -> FontId {
    FontId::new(size, FontFamily::Monospace)
}

fn read_system_font(file_name: &str) -> Option<Vec<u8>> {
    let windows = std::env::var_os("WINDIR").or_else(|| std::env::var_os("SystemRoot"))?;
    std::fs::read(std::path::Path::new(&windows).join("Fonts").join(file_name)).ok()
}

/// 載入系統字型：Segoe UI 為主，微軟正黑體（或其他 CJK 字型）補上中文字形。
/// 字型直接讀自 Windows，不內嵌於執行檔；全部缺席時退回 egui 內建字型。
fn install_fonts(ctx: &egui::Context) {
    let mut fonts = FontDefinitions::default();
    let mut ui_fonts = Vec::new();

    if let Some(bytes) = read_system_font("segoeui.ttf") {
        fonts.font_data.insert("segoe-ui".into(), Arc::new(FontData::from_owned(bytes)));
        ui_fonts.push("segoe-ui".to_string());
    }

    // 第一個找得到的 CJK 字型。
    for file_name in ["msjh.ttc", "msjh.ttf", "msyh.ttc", "mingliu.ttc", "simsun.ttc"] {
        if let Some(bytes) = read_system_font(file_name) {
            fonts.font_data.insert("cjk".into(), Arc::new(FontData::from_owned(bytes)));
            ui_fonts.push("cjk".to_string());
            break;
        }
    }

    for family in [FontFamily::Proportional, FontFamily::Monospace] {
        let entries = fonts.families.entry(family.clone()).or_default();
        // Proportional: system fonts first. Monospace: keep the built-in
        // tabular face first and only fall back to the system fonts for CJK.
        let at = if family == FontFamily::Proportional { 0 } else { entries.len().min(1) };
        for (offset, name) in ui_fonts.iter().enumerate() {
            entries.insert(at + offset, name.clone());
        }
    }

    ctx.set_fonts(fonts);
}

/// 套用整體深色儀表風格。
pub fn apply(ctx: &egui::Context) {
    install_fonts(ctx);

    ctx.set_theme(egui::Theme::Dark);
    ctx.all_styles_mut(|style| {
        style.text_styles = [
            (TextStyle::Small, FontId::proportional(11.5)),
            (TextStyle::Body, FontId::proportional(13.5)),
            (TextStyle::Button, FontId::proportional(13.0)),
            (TextStyle::Heading, FontId::proportional(18.0)),
            (TextStyle::Monospace, FontId::monospace(13.0)),
        ]
        .into();

        style.spacing.item_spacing = egui::vec2(8.0, 6.0);
        style.spacing.button_padding = egui::vec2(12.0, 6.0);
        style.spacing.interact_size.y = 28.0;
        style.spacing.scroll.bar_width = 8.0;

        let visuals = &mut style.visuals;
        visuals.dark_mode = true;
        visuals.override_text_color = Some(TEXT_MAIN);
        visuals.panel_fill = BG_DARK;
        visuals.window_fill = BG_CARD;
        visuals.window_stroke = Stroke::new(1.0, BORDER_STRONG);
        visuals.extreme_bg_color = BG_INSET;
        visuals.faint_bg_color = tint(SLATE, 0x0A);
        visuals.hyperlink_color = ACCENT;
        visuals.selection.bg_fill = tint(ACCENT, 0x40);
        visuals.selection.stroke = Stroke::new(1.0, ACCENT);

        let radius = CornerRadius::same(6);
        let widgets = &mut visuals.widgets;
        widgets.noninteractive.bg_fill = BG_CARD;
        widgets.noninteractive.bg_stroke = Stroke::new(1.0, BORDER);
        widgets.noninteractive.fg_stroke = Stroke::new(1.0, TEXT_MAIN);
        widgets.noninteractive.corner_radius = radius;

        widgets.inactive.bg_fill = BG_CONTROL;
        widgets.inactive.weak_bg_fill = BG_CONTROL;
        widgets.inactive.bg_stroke = Stroke::new(1.0, BORDER);
        widgets.inactive.fg_stroke = Stroke::new(1.0, TEXT_MAIN);
        widgets.inactive.corner_radius = radius;

        widgets.hovered.bg_fill = BG_CONTROL_HOVER;
        widgets.hovered.weak_bg_fill = BG_CONTROL_HOVER;
        widgets.hovered.bg_stroke = Stroke::new(1.0, ACCENT);
        widgets.hovered.fg_stroke = Stroke::new(1.0, TEXT_MAIN);
        widgets.hovered.corner_radius = radius;

        widgets.active.bg_fill = ACCENT_DARK;
        widgets.active.weak_bg_fill = ACCENT_DARK;
        widgets.active.bg_stroke = Stroke::new(1.0, ACCENT);
        widgets.active.fg_stroke = Stroke::new(1.0, TEXT_MAIN);
        widgets.active.corner_radius = radius;

        widgets.open = widgets.hovered;
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn health_colour_boundaries_match_the_report_parser() {
        assert_eq!(health_color(59.9), ROSE);
        assert_eq!(health_color(60.0), AMBER);
        assert_eq!(health_color(79.9), AMBER);
        assert_eq!(health_color(80.0), EMERALD);
    }

    #[test]
    fn grades_power_plans_in_both_languages() {
        assert_eq!(power_plan_color("高效能"), ROSE);
        assert_eq!(power_plan_color("Ultimate Performance"), ROSE);
        assert_eq!(power_plan_color("省電"), EMERALD);
        assert_eq!(power_plan_color("Power saver"), EMERALD);
        assert_eq!(power_plan_color("平衡"), AMBER);
        assert_eq!(power_plan_color("  "), SLATE);
    }

    #[test]
    fn load_colour_scale() {
        assert_eq!(load_color(4.9, 5.0, 15.0), EMERALD);
        assert_eq!(load_color(5.0, 5.0, 15.0), AMBER);
        assert_eq!(load_color(15.0, 5.0, 15.0), ROSE);
    }
}
