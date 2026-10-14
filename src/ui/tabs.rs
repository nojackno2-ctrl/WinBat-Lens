//! 右側六個分頁的內容。

use eframe::egui::{self, Align, Color32, Layout, RichText, Ui};
use egui_extras::{Column, TableBuilder};

use super::present;
use super::theme::*;
use super::widgets;
use super::Action;
use crate::history::BadgeClass;
use crate::i18n::{self, format as format_text, pick, tr};
use crate::models::{thousands, BatteryReportData, CapacityHistoryItem, DiagnosticKind, RealTimePowerState};
use crate::monitor::Live;

const ROW_HEIGHT: f32 = 24.0;
const HEADER_HEIGHT: f32 = 26.0;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tab {
    RealTime,
    HistoryLogs,
    VoltageHistory,
    Diagnostics,
    LifeEstimates,
    RecentUsage,
}

impl Tab {
    pub const ALL: [Tab; 6] =
        [Tab::RealTime, Tab::HistoryLogs, Tab::VoltageHistory, Tab::Diagnostics, Tab::LifeEstimates, Tab::RecentUsage];

    pub fn title(self) -> &'static str {
        tr(match self {
            Tab::RealTime => "TabRealTime",
            Tab::HistoryLogs => "TabHistoryLogs",
            Tab::VoltageHistory => "TabVoltageHistory",
            Tab::Diagnostics => "TabDiagnostics",
            Tab::LifeEstimates => "TabLifeEstimates",
            Tab::RecentUsage => "TabRecentUsage",
        })
    }
}

fn header_cell(ui: &mut Ui, text: &str) {
    ui.label(RichText::new(text).size(12.0).color(TEXT_MUTED).strong());
}

fn cell(ui: &mut Ui, text: impl Into<String>) {
    ui.add(egui::Label::new(RichText::new(text).size(12.5)).truncate());
}

fn colored_cell(ui: &mut Ui, text: impl Into<String>, color: Color32) {
    ui.add(egui::Label::new(RichText::new(text).size(12.5).color(color)).truncate());
}

/// 沒有資料時的說明文字，取代一片空白。
fn empty_state(ui: &mut Ui, text: &str) {
    ui.add_space(28.0);
    ui.vertical_centered(|ui| ui.label(dim(text)));
    ui.add_space(28.0);
}

/// 一行硬體數據：左側標題與副標，右側數值。
fn hardware_row(ui: &mut Ui, label: &str, sub: &str, value: &str, color: Color32, right: &str) {
    inset().show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.horizontal(|ui| {
            ui.vertical(|ui| {
                ui.label(RichText::new(label).size(13.0));
                ui.label(dim(sub));
            });
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                if !right.is_empty() {
                    ui.label(muted(right));
                }
                ui.label(RichText::new(value).font(mono(15.0)).color(color));
            });
        });
    });
}

/// 分頁一：全系統硬體功耗分佈。
pub fn real_time(ui: &mut Ui, live: &Live, animate: bool) {
    let Some(state) = &live.state else {
        empty_state(ui, pick("正在讀取硬體感測器…", "Reading hardware sensors…"));
        return;
    };

    egui::ScrollArea::vertical().auto_shrink(false).show(ui, |ui| {
        ui.spacing_mut().item_spacing.y = 6.0;
        hero_card(ui, state, animate);
        ui.add_space(2.0);
        metric_tiles(ui, state);
        ui.add_space(2.0);
        waveform_card(ui, live);
        ui.add_space(2.0);
        hardware_card(ui, state);
    });
}

fn hero_card(ui: &mut Ui, state: &RealTimePowerState, animate: bool) {
    let status = present::hero_status(state);

    card().show(ui, |ui| {
        ui.set_width(ui.available_width());
        widgets::power_schematic(ui, state, &status, animate);

        ui.horizontal_wrapped(|ui| {
            // The short label keeps the pill compact; the tooltip carries the
            // complete, honest description of where the figure comes from.
            badge(ui, &status.label, status.color).on_hover_text(&status.tooltip);
            ui.label(muted(&state.charging_status_text));
        });

        // A separate line, reserved for the deficit / inadequate charger case.
        if let Some(verdict) = present::charger_verdict(state) {
            ui.label(RichText::new(verdict).size(12.0).color(AMBER));
        }
    });
}

fn metric_tiles(ui: &mut Ui, state: &RealTimePowerState) {
    ui.columns(2, |columns| {
        card().show(&mut columns[0], |ui| {
            ui.set_width(ui.available_width());
            card_title(ui, tr("CardEstTime"));
            ui.add(egui::Label::new(RichText::new(&state.estimated_time_remaining_text).size(17.0).strong()).wrap());

            let percent = if !state.has_battery {
                pick("未偵測到電池", "No battery detected").to_string()
            } else if i18n::is_english() {
                format!("Battery {}%", state.battery_percent)
            } else {
                format!("電量 {}%", state.battery_percent)
            };
            // The watt-hour figure is the pack's own reading, which has real
            // resolution where the Windows percentage is a rounded integer.
            let detail = if state.is_energy_measured {
                format!("{percent} · {} · SoC {:.0}%", state.battery_energy_text, state.true_soc_percent)
            } else {
                percent
            };
            ui.label(muted(detail));
        });

        card().show(&mut columns[1], |ui| {
            ui.set_width(ui.available_width());
            card_title(ui, tr("CardTelemetry"));
            ui.label(RichText::new(&state.battery_telemetry_text).font(mono(17.0)).color(present::flow_color(state)));
            ui.add(egui::Label::new(muted(present::temperature_line(state))).wrap());
        });
    });
}

fn waveform_card(ui: &mut Ui, live: &Live) {
    card().show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.horizontal(|ui| {
            card_title(ui, tr("WaveformTitle"));
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                // Right-to-left layout, so listed in reverse reading order.
                ui.spacing_mut().item_spacing.x = 4.0;
                for (color, key) in [(GPU_BLUE, "LegendGpu"), (EMERALD, "LegendCharge"), (ROSE, "LegendDischarge")] {
                    widgets::legend_item(ui, color, tr(key));
                    ui.add_space(8.0);
                }
            });
        });
        ui.add_space(2.0);
        widgets::waveform_chart(ui, &live.chart, 170.0);
    });
}

fn hardware_card(ui: &mut Ui, state: &RealTimePowerState) {
    let en = i18n::is_english();

    card().show(ui, |ui| {
        ui.set_width(ui.available_width());
        card_title(ui, tr("HardwareTitle"));
        ui.add_space(2.0);

        hardware_row(
            ui,
            tr("HwBatteryTelemetry"),
            tr("HwTelemetrySource"),
            &state.battery_telemetry_text,
            present::flow_color(state),
            "",
        );

        // Energy in the pack, and its full-charge capacity against the
        // factory design figure — the live version of the health score.
        if state.is_energy_measured {
            let sub = if state.driver_health_percent > 0.0 {
                let (capacity, health) = (&state.battery_capacity_health_text, state.driver_health_percent);
                if en {
                    format!("Full charge {capacity}, health {health:.1}%")
                } else {
                    format!("滿電 {capacity}，健康度 {health:.1}%")
                }
            } else {
                pick("電池驅動實測容量", "Measured by the battery driver").to_string()
            };
            let soc = if en {
                format!("SoC {:.1}%", state.true_soc_percent)
            } else {
                format!("真實 SoC {:.1}%", state.true_soc_percent)
            };
            hardware_row(ui, tr("HwEnergy"), &sub, &state.battery_energy_text, AMBER, &soc);
        }

        // Power plan, coloured by what it costs: performance spends, saver
        // conserves, balanced sits between the two.
        hardware_row(
            ui,
            tr("HwPowerPlan"),
            tr("HwPowerPlanState"),
            &state.power_plan_name,
            power_plan_color(&state.power_plan_name),
            "",
        );

        // The discrete GPU is the only component with a real power sensor, so
        // it is the only component row on this page.
        //
        // Green while the card is idling and saving power, red once it is
        // genuinely drawing. Where the card reports real watts those are the
        // better signal — an idle-but-hot dGPU can sit at 0% utilisation and
        // still burn 10 W.
        let dgpu_color = if state.is_dgpu_power_measured {
            load_color(state.dgpu_power_w, 5.0, 15.0)
        } else if state.has_discrete_gpu {
            load_color(state.dgpu_usage_percent, 5.0, 40.0)
        } else {
            load_color(state.igpu_usage_percent, 5.0, 40.0)
        };
        let dgpu_power =
            if state.is_dgpu_power_measured { format!("{:.1} W", state.dgpu_power_w) } else { "-- W".to_string() };

        // Without a discrete card the row describes the integrated GPU
        // instead: its utilisation is real, its wattage is not readable.
        let (gpu_label, gpu_name, gpu_usage, gpu_status) = if state.has_discrete_gpu {
            (tr("HwDgpu"), &state.dgpu_name, state.dgpu_usage_percent, state.dgpu_status_text.clone())
        } else {
            (tr("HwIgpu"), &state.igpu_name, state.igpu_usage_percent, format!("{:.1}%", state.igpu_usage_percent))
        };

        inset().show(ui, |ui| {
            ui.set_width(ui.available_width());
            ui.horizontal(|ui| {
                ui.vertical(|ui| {
                    ui.label(RichText::new(gpu_label).size(13.0));
                    ui.label(dim(gpu_name));
                });
                ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                    ui.label(RichText::new(dgpu_power).font(mono(15.0)).color(dgpu_color));
                    ui.label(RichText::new(gpu_status).size(12.0).color(dgpu_color));
                    widgets::load_bar(ui, gpu_usage as f32 / 100.0, dgpu_color, 120.0);
                });
            });
        });

        ui.add_space(4.0);
        ui.add(egui::Label::new(muted(present::live_power_tip(state))).wrap());
    });
}

fn badge_color(class: BadgeClass) -> Color32 {
    match class {
        BadgeClass::Info => ACCENT,
        BadgeClass::Warning => AMBER,
        BadgeClass::Success => EMERALD,
        BadgeClass::Danger => ROSE,
    }
}

/// 分頁二：即時功耗與充放電歷史紀錄。
pub fn history_logs(ui: &mut Ui, live: &Live, actions: &mut Vec<Action>) {
    card().show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.set_min_height(ui.available_height());

        ui.horizontal(|ui| {
            card_title(ui, tr("HistoryLogHeader"));
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                if ui.button(tr("BtnClearHistory")).clicked() {
                    actions.push(Action::ClearPowerHistory);
                }
                if ui.button(tr("BtnExportCsv")).clicked() {
                    actions.push(Action::ExportCsv);
                }
            });
        });
        ui.add_space(4.0);

        let records = live.power_history.records();
        if records.is_empty() {
            empty_state(ui, tr("PowerHistoryEmpty"));
            return;
        }

        egui::ScrollArea::horizontal().auto_shrink(false).show(ui, |ui| {
            TableBuilder::new(ui)
                .id_salt("power-history")
                .striped(true)
                .auto_shrink(false)
                .column(Column::exact(140.0))
                .column(Column::exact(118.0))
                .column(Column::exact(104.0))
                .column(Column::exact(62.0))
                .column(Column::exact(128.0))
                .column(Column::exact(70.0))
                .column(Column::exact(96.0))
                .column(Column::exact(76.0))
                .column(Column::remainder().at_least(380.0))
                .header(HEADER_HEIGHT, |mut header| {
                    for key in [
                        "ColTime",
                        "ColEvent",
                        "ColPower",
                        "ColBattery",
                        "ColTelemetry",
                        "ColCpu",
                        "ColDgpu",
                        "ColDgpuPower",
                        "ColSummary",
                    ] {
                        header.col(|ui| header_cell(ui, tr(key)));
                    }
                })
                .body(|body| {
                    body.rows(ROW_HEIGHT, records.len(), |mut row| {
                        let record = &records[row.index()];
                        row.col(|ui| {
                            ui.label(RichText::new(&record.timestamp_text).font(mono(12.0)));
                        });
                        row.col(|ui| colored_cell(ui, &record.event_type, badge_color(record.badge)));
                        row.col(|ui| cell(ui, record.discharge_rate_text()));
                        row.col(|ui| cell(ui, record.battery_percent_text()));
                        row.col(|ui| cell(ui, record.telemetry_text()));
                        row.col(|ui| cell(ui, record.cpu_usage_text()));
                        row.col(|ui| cell(ui, record.dgpu_usage_text()));
                        row.col(|ui| cell(ui, record.dgpu_power_text()));
                        row.col(|ui| cell(ui, &record.summary_text));
                    });
                });
        });
    });
}

/// 分頁三：電池電壓紀錄 (0-100%)。
pub fn voltage_history(ui: &mut Ui, live: &Live, actions: &mut Vec<Action>) {
    let history = &live.voltage_history;
    let points = history.points();

    card().show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.set_min_height(ui.available_height());

        ui.horizontal(|ui| {
            card_title(ui, tr("VoltageHistoryTitle"));
            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                if ui.button(tr("BtnClearVoltageHistory")).clicked() {
                    actions.push(Action::ClearVoltageHistory);
                }
            });
        });
        ui.label(muted(format_text(
            tr("VoltageHistorySummary"),
            &[&points.len().to_string(), &history.total_sample_count().to_string()],
        )));
        ui.label(dim(tr("VoltageHistoryHint")));
        ui.add_space(4.0);

        widgets::voltage_chart(ui, points, 230.0);
        ui.add_space(6.0);

        if points.is_empty() {
            return;
        }

        TableBuilder::new(ui)
            .id_salt("voltage-history")
            .striped(true)
            .auto_shrink(false)
            .column(Column::exact(90.0))
            .column(Column::exact(120.0))
            .column(Column::exact(150.0))
            .column(Column::exact(100.0))
            .column(Column::remainder())
            .header(HEADER_HEIGHT, |mut header| {
                for key in [
                    "VoltagePercentColumn",
                    "VoltageAverageColumn",
                    "VoltageRangeColumn",
                    "VoltageEventsColumn",
                    "VoltageLastColumn",
                ] {
                    header.col(|ui| header_cell(ui, tr(key)));
                }
            })
            .body(|body| {
                body.rows(ROW_HEIGHT, points.len(), |mut row| {
                    let point = &points[row.index()];
                    row.col(|ui| cell(ui, point.battery_percent_text()));
                    row.col(|ui| colored_cell(ui, point.average_voltage_text(), ACCENT));
                    row.col(|ui| cell(ui, point.voltage_range_text()));
                    row.col(|ui| cell(ui, point.sample_count.to_string()));
                    row.col(|ui| cell(ui, point.last_recorded_at_text()));
                });
            });
    });
}

fn diagnostic_style(kind: DiagnosticKind) -> (Color32, &'static str) {
    match kind {
        DiagnosticKind::Info => (ACCENT, "i"),
        DiagnosticKind::Warning => (AMBER, "!"),
        DiagnosticKind::Danger => (ROSE, "!"),
        DiagnosticKind::Success => (EMERALD, "✔"),
        DiagnosticKind::Tip => (SLATE, "★"),
    }
}

/// 分頁四：智慧診斷與維護建議。
pub fn diagnostics(ui: &mut Ui, report: Option<&BatteryReportData>) {
    let items = report.map(|report| report.diagnostics.as_slice()).unwrap_or_default();
    if items.is_empty() {
        card().show(ui, |ui| {
            ui.set_width(ui.available_width());
            empty_state(ui, tr("DiagnosticsEmpty"));
        });
        return;
    }

    egui::ScrollArea::vertical().auto_shrink(false).show(ui, |ui| {
        ui.spacing_mut().item_spacing.y = 8.0;
        for item in items {
            let (color, glyph) = diagnostic_style(item.kind);
            egui::Frame::new()
                .fill(tint(color, 0x10))
                .stroke(egui::Stroke::new(1.0, tint(color, 0x80)))
                .corner_radius(egui::CornerRadius::same(10))
                .inner_margin(egui::Margin::same(14))
                .show(ui, |ui| {
                    ui.set_width(ui.available_width());
                    ui.horizontal(|ui| {
                        let (icon, _) = ui.allocate_exact_size(egui::vec2(26.0, 26.0), egui::Sense::hover());
                        ui.painter().circle_filled(icon.center(), 13.0, tint(color, 0x30));
                        ui.painter().text(
                            icon.center(),
                            egui::Align2::CENTER_CENTER,
                            glyph,
                            egui::FontId::proportional(14.0),
                            color,
                        );
                        ui.vertical(|ui| {
                            ui.label(RichText::new(&item.title).size(14.5).color(color).strong());
                            ui.add(
                                egui::Label::new(RichText::new(&item.description).size(13.0).color(TEXT_MUTED)).wrap(),
                            );
                        });
                    });
                });
        }
    });
}

/// 以三欄或四欄文字表格呈現報告中的一個區段。
fn text_table<const N: usize>(ui: &mut Ui, id: &str, headers: [&str; N], widths: [f32; N], rows: &[[&str; N]]) {
    let mut table = TableBuilder::new(ui).id_salt(id).striped(true).auto_shrink(false);
    for (index, width) in widths.iter().enumerate() {
        table = table.column(if index + 1 == N { Column::remainder() } else { Column::exact(*width) });
    }

    table
        .header(HEADER_HEIGHT, |mut header| {
            for title in headers {
                header.col(|ui| header_cell(ui, title));
            }
        })
        .body(|body| {
            body.rows(ROW_HEIGHT, rows.len(), |mut row| {
                for value in rows[row.index()] {
                    row.col(|ui| cell(ui, value));
                }
            });
        });
}

/// 分頁五：續航估算表。
pub fn life_estimates(ui: &mut Ui, report: Option<&BatteryReportData>) {
    card().show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.set_min_height(ui.available_height());
        card_title(ui, tr("TabLifeEstimates"));
        ui.add_space(4.0);

        let items = report.map(|report| report.battery_life_estimates.as_slice()).unwrap_or_default();
        if items.is_empty() {
            empty_state(ui, tr("LifeEstimatesEmpty"));
            return;
        }

        let rows: Vec<[&str; 3]> = items
            .iter()
            .map(|item| [item.period.as_str(), item.full_charge_estimate.as_str(), item.design_cap_estimate.as_str()])
            .collect();
        text_table(
            ui,
            "life-estimates",
            [tr("ColPeriod"), tr("ColFullEstimate"), tr("ColDesignEstimate")],
            [260.0, 220.0, 0.0],
            &rows,
        );
    });
}

/// 分頁六：最近使用紀錄。
pub fn recent_usage(ui: &mut Ui, report: Option<&BatteryReportData>) {
    card().show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.set_min_height(ui.available_height());
        card_title(ui, tr("TabRecentUsage"));
        ui.add_space(4.0);

        let items = report.map(|report| report.recent_usage.as_slice()).unwrap_or_default();
        if items.is_empty() {
            empty_state(ui, tr("RecentUsageEmpty"));
            return;
        }

        let rows: Vec<[&str; 4]> = items
            .iter()
            .map(|item| {
                [item.start_time.as_str(), item.state.as_str(), item.source.as_str(), item.capacity_remaining.as_str()]
            })
            .collect();
        text_table(
            ui,
            "recent-usage",
            [tr("ColStartTime"), tr("ColState"), tr("ColSource"), tr("ColCapacityRemaining")],
            [200.0, 180.0, 140.0, 0.0],
            &rows,
        );
    });
}

/// 左側面板的容量歷史紀錄表。外層面板已可捲動，因此表格本身不再捲動。
pub fn capacity_history_table(ui: &mut Ui, history: &[CapacityHistoryItem]) {
    TableBuilder::new(ui)
        .id_salt("capacity-history")
        .striped(true)
        .vscroll(false)
        .column(Column::remainder().clip(true))
        .column(Column::exact(64.0))
        .column(Column::exact(64.0))
        .column(Column::exact(48.0))
        .header(HEADER_HEIGHT, |mut header| {
            for key in ["ColPeriod", "ColFullCap", "ColDesignCap", "ColHealthPct"] {
                header.col(|ui| header_cell(ui, tr(key)));
            }
        })
        .body(|body| {
            body.rows(22.0, history.len(), |mut row| {
                let item = &history[row.index()];
                row.col(|ui| {
                    ui.add(egui::Label::new(RichText::new(&item.period).size(11.5)).truncate())
                        .on_hover_text(&item.period);
                });
                row.col(|ui| cell(ui, thousands(item.full_charge_capacity as i64)));
                row.col(|ui| cell(ui, thousands(item.design_capacity as i64)));
                row.col(|ui| {
                    colored_cell(ui, format!("{:.1}", item.health_percent), health_color(item.health_percent))
                });
            });
        });
}
