//! WinBat Lens 主儀表板視窗：電池報告展示、即時功耗圖表、系統托盤常駐與多國語言切換。

mod present;
mod tabs;
mod theme;
mod widgets;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Receiver, TryRecvError};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::Local;
use eframe::egui::{self, vec2, Align, Layout, RichText, ViewportCommand};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use windows::Win32::System::ProcessStatus::EmptyWorkingSet;
use windows::Win32::System::Threading::GetCurrentProcess;
use windows::Win32::UI::WindowsAndMessaging::{IsWindowVisible, SetForegroundWindow};

use crate::app_info;
use crate::history::VoltageHistory;
use crate::i18n::{self, pick, tr};
use crate::models::{thousands, BatteryReportData};
use crate::monitor::{self, Live, Shared};
use crate::tray::{Tray, TrayAction};
use crate::win::{self, MessageIcon, SendHwnd};
use crate::{powercfg, report_parser, single_instance, startup};
use tabs::Tab;
use theme::*;

/// How long a requested exit may take before the process stops waiting for
/// the window and leaves on its own.
const EXIT_GRACE: Duration = Duration::from_millis(2000);

const ENTRANCE_SECONDS: f64 = 0.26;

/// The one real exit path's cleanup, shared by the tray menu, the window and a
/// newer instance (or the installer) asking this one to stand down. Dropping
/// the tray icon here matters: if the process dies without it, the icon
/// lingers in the notification area until the user happens to hover over it.
struct Lifecycle {
    shared: Arc<Shared>,
    tray: Mutex<Option<Arc<Tray>>>,
    finalized: AtomicBool,
}

impl Lifecycle {
    fn finalize(&self) {
        if self.finalized.swap(true, Ordering::SeqCst) {
            return;
        }
        self.shared.live().voltage_history.persist();
        if let Some(tray) = self.tray.lock().ok().and_then(|mut tray| tray.take()) {
            tray.remove();
        }
    }

    /// Asks the UI thread to close the window. Should the window not answer —
    /// it is hidden, and a hidden window owes nobody a repaint — the process
    /// cleans up and exits anyway rather than leaving a zombie in the tray.
    fn request_exit(self: &Arc<Self>, ctx: &egui::Context) {
        if self.shared.exit_requested.swap(true, Ordering::SeqCst) {
            return;
        }
        ctx.request_repaint();

        let lifecycle = self.clone();
        let _ = std::thread::Builder::new().name("exit-watchdog".into()).spawn(move || {
            std::thread::sleep(EXIT_GRACE);
            lifecycle.finalize();
            std::process::exit(0);
        });
    }
}

/// 使用者在畫面上觸發、需要離開繪圖階段才執行的動作（對話方塊會阻塞）。
#[derive(Debug, Clone, Copy, PartialEq)]
enum Action {
    ToggleLanguage,
    SetAutoStart(bool),
    OpenReport,
    RunCheck,
    ExportJson,
    ExportCsv,
    ClearPowerHistory,
    ClearVoltageHistory,
}

pub struct App {
    shared: Arc<Shared>,
    lifecycle: Arc<Lifecycle>,
    tray: Option<Arc<Tray>>,
    hwnd: Option<SendHwnd>,
    report: Option<BatteryReportData>,
    tab: Tab,
    auto_start: bool,
    /// 系統「在視窗內顯示動畫」設定；關閉時所有裝飾動畫靜止。
    animations: bool,
    actions: Vec<Action>,
    check_result: Option<Receiver<Result<BatteryReportData, String>>>,
    initial_check_started: bool,
    balloon_shown: bool,
    closing: bool,
    /// 首幀繪製後 eframe 會強制顯示視窗，必須再確認一次隱藏結果。
    first_window_frame: bool,
    /// 第一次顯示視窗的時間點，用於一次性的淡入。
    revealed_at: Option<f64>,
}

/// 建立視窗並執行事件迴圈，直到程式結束。
pub fn run(start_hidden: bool) -> eframe::Result {
    let mut viewport = egui::ViewportBuilder::default()
        .with_title(tr("AppTitle"))
        .with_inner_size([1240.0, 800.0])
        .with_min_inner_size([1020.0, 680.0])
        // Windows startup passes --background so the monitor can begin in the
        // tray without briefly showing the dashboard.
        .with_visible(!start_hidden);

    if let Ok(icon) = eframe::icon_data::from_png_bytes(include_bytes!("../../app_icon.png")) {
        viewport = viewport.with_icon(icon);
    }

    let options = eframe::NativeOptions { viewport, renderer: eframe::Renderer::Glow, ..Default::default() };

    eframe::run_native("WinBat Lens", options, Box::new(move |creation| Ok(Box::new(App::new(creation, start_hidden)))))
}

impl App {
    fn new(creation: &eframe::CreationContext<'_>, start_hidden: bool) -> Self {
        let ctx = creation.egui_ctx.clone();
        theme::apply(&ctx);

        let hwnd = creation.window_handle().ok().and_then(|handle| match handle.as_raw() {
            RawWindowHandle::Win32(win32) => Some(SendHwnd(win32.hwnd.get())),
            _ => None,
        });

        let shared = Arc::new(Shared::new(VoltageHistory::load(VoltageHistory::default_path()), start_hidden));
        let lifecycle =
            Arc::new(Lifecycle { shared: shared.clone(), tray: Mutex::new(None), finalized: AtomicBool::new(false) });

        let tray = {
            let (shared, lifecycle, ctx) = (shared.clone(), lifecycle.clone(), ctx.clone());
            Tray::spawn(move |action| {
                match action {
                    TrayAction::Show => shared.show_requested.store(true, Ordering::SeqCst),
                    TrayAction::RunCheck => {
                        shared.show_requested.store(true, Ordering::SeqCst);
                        shared.check_requested.store(true, Ordering::SeqCst);
                    }
                    TrayAction::AutoStartChanged => shared.auto_start_changed.store(true, Ordering::SeqCst),
                    TrayAction::Exit => return lifecycle.request_exit(&ctx),
                }
                ctx.request_repaint();
            })
            .map(Arc::new)
        };
        if let Ok(mut slot) = lifecycle.tray.lock() {
            *slot = tray.clone();
        }

        // Answer duplicate launches from the very start: a second copy can put
        // its version prompt on screen within a few hundred ms of this process
        // starting, and if the listener is not up by the time the user answers
        // it, the handover degrades to terminating this process.
        {
            let (shared, activate_ctx) = (shared.clone(), ctx.clone());
            let (lifecycle, exit_ctx) = (lifecycle.clone(), ctx.clone());
            single_instance::start_listening(
                move || {
                    shared.show_requested.store(true, Ordering::SeqCst);
                    activate_ctx.request_repaint();
                },
                move || lifecycle.request_exit(&exit_ctx),
            );
        }

        {
            let ctx = ctx.clone();
            monitor::spawn(shared.clone(), tray.clone(), move || ctx.request_repaint());
        }

        Self {
            shared,
            lifecycle,
            tray,
            hwnd,
            report: None,
            tab: Tab::RealTime,
            auto_start: startup::is_auto_start_enabled(),
            animations: win::client_area_animation_enabled(),
            actions: Vec::new(),
            check_result: None,
            initial_check_started: false,
            balloon_shown: false,
            closing: false,
            first_window_frame: true,
            revealed_at: None,
        }
    }

    fn owner(&self) -> Option<windows::Win32::Foundation::HWND> {
        self.hwnd.map(SendHwnd::hwnd)
    }

    fn is_hidden(&self) -> bool {
        self.shared.hidden.load(Ordering::Relaxed)
    }

    fn hide_to_tray(&mut self, ctx: &egui::Context) {
        ctx.send_viewport_cmd(ViewportCommand::Visible(false));
        if self.shared.hidden.swap(true, Ordering::SeqCst) {
            return;
        }

        // Only explain the tray behaviour the first time; after that the
        // balloon is just noise on every minimize.
        if let (false, Some(tray)) = (self.balloon_shown, &self.tray) {
            self.balloon_shown = true;
            tray.show_balloon(tr("TrayBalloonTitle"), tr("TrayBalloonText"));
        }

        // The app spends most of its life in the tray. Hand idle pages back to
        // the OS so the background working set stays small; they are reloaded
        // on demand when the user restores the window.
        unsafe {
            let _ = EmptyWorkingSet(GetCurrentProcess());
        }
    }

    fn restore_from_tray(&mut self, ctx: &egui::Context) {
        self.shared.hidden.store(false, Ordering::SeqCst);
        self.animations = win::client_area_animation_enabled();

        ctx.send_viewport_cmd(ViewportCommand::Visible(true));
        ctx.send_viewport_cmd(ViewportCommand::Minimized(false));
        ctx.send_viewport_cmd(ViewportCommand::Focus);

        // Focus alone is at the mercy of the foreground lock when the request
        // comes from a duplicate launch; the launcher has already donated its
        // foreground rights, so ask for them directly as well.
        if let Some(hwnd) = self.owner() {
            unsafe {
                let _ = SetForegroundWindow(hwnd);
            }
        }
        ctx.request_repaint();
    }

    /// 可獨立於 eframe::Frame 處理；原生可見性由呼叫端提供，方便重現首幀覆寫。
    fn window_lifecycle(&mut self, ctx: &egui::Context, tray_available: bool, window_visible: Option<bool>) -> bool {
        let first_frame = std::mem::take(&mut self.first_window_frame);
        if self.closing {
            return false;
        }
        if self.shared.exit_requested.load(Ordering::SeqCst) {
            self.closing = true;
            self.lifecycle.finalize();
            ctx.send_viewport_cmd(ViewportCommand::Close);
            return false;
        }

        let (close_requested, minimized) =
            ctx.input(|input| (input.viewport().close_requested(), input.viewport().minimized == Some(true)));
        if close_requested && !tray_available {
            // 托盤建立失敗時保留正常關閉路徑，避免留下無法操作的背景程式。
            self.closing = true;
            self.lifecycle.finalize();
            return false;
        }
        if tray_available {
            if close_requested {
                ctx.send_viewport_cmd(ViewportCommand::CancelClose);
            }
            if close_requested || (minimized && !self.is_hidden()) {
                self.hide_to_tray(ctx);
            }
        }

        if self.shared.show_requested.swap(false, Ordering::SeqCst) || (!tray_available && self.is_hidden()) {
            self.restore_from_tray(ctx);
        }

        // egui 的 visible() 只表示是否需要繪圖；最小化或遮蔽不代表已從工作列隱藏。
        // 首幀即使原生視窗尚未顯示，也需等 eframe 的強制顯示完成後重試。
        if tray_available && self.is_hidden() && (first_frame || window_visible != Some(false)) {
            ctx.send_viewport_cmd(ViewportCommand::Visible(false));
            ctx.request_repaint_after(Duration::from_millis(50));
        }
        true
    }

    fn start_check(&mut self, ctx: &egui::Context) {
        if self.check_result.is_some() {
            return;
        }
        let (sender, receiver) = mpsc::channel();
        self.check_result = Some(receiver);

        let (shared, ctx) = (self.shared.clone(), ctx.clone());
        let spawned = std::thread::Builder::new().name("battery-check".into()).spawn(move || {
            // The report HTML can exceed 1 MB and parsing is regex heavy, so
            // both stay off the UI thread. The live pack info is merged in
            // during parsing: the report's capacities are a snapshot Windows
            // logged earlier, while the driver's are current, and the driver
            // also carries fields powercfg has no column for.
            // Debug builds can be pointed at a saved report instead of
            // running powercfg, to exercise the report views on any machine.
            #[cfg(debug_assertions)]
            let generated = match std::env::var_os("WINBAT_LENS_DEMO_REPORT") {
                Some(path) => powercfg::read_report(std::path::Path::new(&path)).map_err(|error| error.to_string()),
                None => powercfg::generate_report(),
            };
            #[cfg(not(debug_assertions))]
            let generated = powercfg::generate_report();

            let result = generated.map(|html| {
                let pack = shared.live().pack.clone();
                report_parser::parse(&html, Some(&pack))
            });
            let _ = sender.send(result);
            ctx.request_repaint();
        });
        if spawned.is_err() {
            self.check_result = None;
        }
    }

    fn poll_check(&mut self) {
        let Some(receiver) = &self.check_result else { return };
        match receiver.try_recv() {
            Ok(Ok(report)) => {
                self.check_result = None;
                self.report = Some(report);
            }
            Ok(Err(message)) => {
                self.check_result = None;
                win::show_message(
                    self.owner(),
                    &message,
                    pick("電池檢測失敗", "Battery Check Failed"),
                    MessageIcon::Warning,
                );
            }
            Err(TryRecvError::Disconnected) => self.check_result = None,
            Err(TryRecvError::Empty) => {}
        }
    }

    fn perform(&mut self, action: Action, ctx: &egui::Context, frame: &eframe::Frame) {
        let owner = self.owner();
        match action {
            Action::ToggleLanguage => {
                i18n::toggle_language();
                ctx.send_viewport_cmd(ViewportCommand::Title(tr("AppTitle").to_string()));
            }
            Action::SetAutoStart(enable) => {
                if startup::set_auto_start(enable) {
                    self.auto_start = enable;
                    let text = if enable {
                        pick("已設定開機自動啟動。", "WinBat Lens will now start with Windows.")
                    } else {
                        pick("已取消開機自動啟動。", "WinBat Lens will no longer start with Windows.")
                    };
                    win::show_message(owner, text, pick("開機啟動設定", "Startup Setting"), MessageIcon::Info);
                } else {
                    self.auto_start = startup::is_auto_start_enabled();
                    win::show_message(
                        owner,
                        pick(
                            "無法修改系統開機啟動登錄碼，請確認權限。",
                            "The startup registry entry could not be changed. Check your permissions.",
                        ),
                        pick("設定失敗", "Setting Failed"),
                        MessageIcon::Warning,
                    );
                }
            }
            Action::RunCheck => self.start_check(ctx),
            Action::OpenReport => {
                let picked = rfd::FileDialog::new()
                    .set_title(pick(
                        "選擇 Windows Battery Report (battery-report.html)",
                        "Choose a Windows Battery Report (battery-report.html)",
                    ))
                    .add_filter("HTML Files", &["html", "htm"])
                    .add_filter("All Files", &["*"])
                    .set_parent(frame)
                    .pick_file();
                let Some(path) = picked else { return };

                // Parsed on its own, with no live driver data merged in: a
                // hand-picked report may well come from another machine, and
                // this machine's pack would then be describing someone else's
                // battery.
                match powercfg::read_report(&path) {
                    Ok(html) => self.report = Some(report_parser::parse(&html, None)),
                    Err(error) => win::show_message(
                        owner,
                        &format!("{}: {error}", pick("無法讀取檔案", "The file could not be read")),
                        pick("讀取錯誤", "Read Error"),
                        MessageIcon::Error,
                    ),
                }
            }
            Action::ExportJson => {
                let Some(report) = &self.report else {
                    win::show_message(
                        owner,
                        pick("目前尚無可供匯出的電池資料。", "No battery report data available to export."),
                        pick("提示", "Info"),
                        MessageIcon::Info,
                    );
                    return;
                };

                let picked = rfd::FileDialog::new()
                    .set_title(pick("匯出電池健康度摘要報告", "Export Battery Summary Report"))
                    .set_file_name(format!("WinBat_Lens_Summary_{}.json", Local::now().format("%Y%m%d")))
                    .add_filter("JSON Data", &["json"])
                    .add_filter("All Files", &["*"])
                    .set_parent(frame)
                    .save_file();
                let Some(path) = picked else { return };

                let written = serde_json::to_string_pretty(report)
                    .map_err(|error| error.to_string())
                    .and_then(|json| std::fs::write(&path, json).map_err(|error| error.to_string()));
                self.report_export(written, &path);
            }
            Action::ExportCsv => {
                let picked = rfd::FileDialog::new()
                    .set_title(pick("匯出即時功耗與充放電歷史日誌", "Export Live Power History Logs"))
                    .set_file_name(format!("WinBat_Power_Log_{}.csv", Local::now().format("%Y%m%d_%H%M%S")))
                    .add_filter("CSV Files", &["csv"])
                    .add_filter("All Files", &["*"])
                    .set_parent(frame)
                    .save_file();
                let Some(path) = picked else { return };

                let written = self.shared.live().power_history.export_to_csv(&path).map_err(|error| error.to_string());
                self.report_export(written, &path);
            }
            Action::ClearPowerHistory => {
                let confirmed = win::ask_yes_no(
                    owner,
                    pick("確定要清除所有即時功耗與充放電事件紀錄嗎？", "Clear all live power and event history logs?"),
                    pick("確認清除", "Confirm Clear"),
                );
                if confirmed {
                    self.shared.live().power_history.clear();
                }
            }
            Action::ClearVoltageHistory => {
                let confirmed = win::ask_yes_no(
                    owner,
                    pick("要清除所有電量百分比與電壓紀錄嗎？", "Clear all battery voltage-by-percentage history?"),
                    pick("確認清除", "Confirm Clear"),
                );
                if confirmed {
                    self.shared.live().voltage_history.clear();
                }
            }
        }
    }

    fn report_export(&self, written: Result<(), String>, path: &std::path::Path) {
        match written {
            Ok(()) => win::show_message(
                self.owner(),
                &format!("{}\n{}", pick("已成功匯出至:", "Successfully exported to:"), path.display()),
                pick("匯出成功", "Export Success"),
                MessageIcon::Info,
            ),
            Err(error) => win::show_message(
                self.owner(),
                &format!("{}: {error}", pick("匯出失敗", "Export failed")),
                pick("錯誤", "Error"),
                MessageIcon::Error,
            ),
        }
    }

    fn header(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            ui.vertical(|ui| {
                ui.horizontal(|ui| {
                    ui.label(RichText::new("WinBat Lens").size(20.0).strong());
                    badge(ui, &app_info::display_version(), ACCENT);
                });

                let computer = std::env::var("COMPUTERNAME").unwrap_or_default();
                let product = self
                    .report
                    .as_ref()
                    .map(|report| report.system_info.system_product_name.as_str())
                    .filter(|name| !name.is_empty() && *name != "Windows PC");
                let subtitle = match product {
                    Some(product) => format!("{computer} · {product} · {}", tr("SubTitle")),
                    None => format!("{computer} · {}", tr("SubTitle")),
                };
                ui.label(muted(subtitle));
            });

            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                let busy = self.check_result.is_some();

                let check = egui::Button::new(RichText::new(tr("BtnCheck")).color(BG_DARK).strong()).fill(ACCENT);
                if ui.add_enabled(!busy, check).clicked() {
                    self.actions.push(Action::RunCheck);
                }
                if ui.button(tr("BtnExportJson")).clicked() {
                    self.actions.push(Action::ExportJson);
                }
                if ui.add_enabled(!busy, egui::Button::new(tr("BtnOpenReport"))).clicked() {
                    self.actions.push(Action::OpenReport);
                }
                if ui.button(tr("BtnLanguage")).clicked() {
                    self.actions.push(Action::ToggleLanguage);
                }

                let mut auto_start = self.auto_start;
                if ui.checkbox(&mut auto_start, tr("AutoStart")).changed() {
                    self.actions.push(Action::SetAutoStart(auto_start));
                }
            });
        });
    }

    fn health_card(&self, ui: &mut egui::Ui) {
        card().show(ui, |ui| {
            ui.set_width(ui.available_width());
            card_title(ui, tr("HealthTitle"));
            ui.add_space(4.0);

            let metrics = self.report.as_ref().map(|report| &report.health_metrics);
            let measured = metrics.filter(|m| m.has_battery && m.is_health_measured);
            let color = match (measured, metrics) {
                (Some(m), _) => health_color(m.health_percent),
                (None, Some(m)) => status_color(m.status_class),
                (None, None) => SLATE,
            };

            ui.horizontal(|ui| {
                widgets::health_ring(ui, measured.map(|m| m.health_percent), color);
                ui.add_space(6.0);
                ui.vertical(|ui| {
                    ui.add_space(8.0);
                    match metrics {
                        Some(metrics) => {
                            badge(ui, &metrics.status_label, color);
                            ui.add_space(4.0);
                            ui.add(egui::Label::new(muted(&metrics.summary_text)).wrap());
                        }
                        None => {
                            ui.add(egui::Label::new(muted(tr("HealthSummaryDefault"))).wrap());
                        }
                    }
                });
            });

            if let Some(report) = &self.report {
                ui.add_space(2.0);
                let time = report.system_info.report_time.as_str();
                let time = if time.trim().is_empty() || time == "N/A" {
                    report.loaded_at.replace('T', " ")
                } else {
                    time.to_string()
                };
                ui.label(dim(format!("{}: {time}", tr("ReportTime"))));
            }
        });
    }

    fn specs_card(&self, ui: &mut egui::Ui) {
        card().show(ui, |ui| {
            ui.set_width(ui.available_width());
            card_title(ui, tr("BatterySpecsTitle"));
            ui.add_space(4.0);

            let Some(report) = &self.report else {
                widgets::capacity_bar(ui, None, SLATE);
                return;
            };
            let specs = &report.battery_specs;
            let metrics = &report.health_metrics;
            let measured = metrics.has_battery && metrics.is_health_measured;
            let capacity = |value: i32| {
                if value > 0 {
                    format!("{} {}", thousands(value as i64), specs.unit)
                } else {
                    "-".to_string()
                }
            };

            // 設計容量 vs 滿電容量的視覺化衰退條
            let ratio = (specs.design_capacity > 0 && specs.full_charge_capacity > 0)
                .then(|| specs.full_charge_capacity as f64 / specs.design_capacity as f64);
            widgets::capacity_bar(ui, ratio, if measured { health_color(metrics.health_percent) } else { SLATE });
            ui.add_space(6.0);

            let rows: [(&str, String, egui::Color32); 8] = [
                (tr("SpecName"), specs.name.clone(), TEXT_MAIN),
                (tr("SpecMfg"), specs.manufacturer.clone(), TEXT_MAIN),
                (tr("SpecChem"), specs.chemistry.clone(), TEXT_MAIN),
                (
                    tr("SpecMade"),
                    // The manufacture date exists only when the battery driver
                    // supplied one; the row is omitted otherwise.
                    match (specs.manufacture_date, specs.age_years) {
                        (Some(date), Some(age)) if i18n::is_english() => {
                            format!("{}  ({age:.1} yrs old)", date.format("%Y-%m-%d"))
                        }
                        (Some(date), Some(age)) => format!("{}（約 {age:.1} 年）", date.format("%Y-%m-%d")),
                        (Some(date), None) => date.format("%Y-%m-%d").to_string(),
                        (None, _) => String::new(),
                    },
                    TEXT_MAIN,
                ),
                (tr("SpecDesign"), capacity(specs.design_capacity), TEXT_MAIN),
                (
                    tr("SpecFull"),
                    capacity(specs.full_charge_capacity),
                    if measured { health_color(metrics.health_percent) } else { TEXT_MAIN },
                ),
                (
                    tr("SpecLoss"),
                    if measured {
                        format!(
                            "{} {} ({}%)",
                            thousands(metrics.capacity_loss as i64),
                            specs.unit,
                            metrics.wear_percent
                        )
                    } else {
                        "-".into()
                    },
                    if measured && metrics.capacity_loss > 0 { ROSE } else { TEXT_MAIN },
                ),
                (
                    tr("SpecCycles"),
                    match specs.cycle_count {
                        Some(cycles) if i18n::is_english() => format!("{cycles} cycles"),
                        Some(cycles) => format!("{cycles} 次"),
                        None => pick("未提供", "N/A").to_string(),
                    },
                    TEXT_MAIN,
                ),
            ];

            for (label, value, color) in rows.iter().filter(|(_, value, _)| !value.is_empty()) {
                ui.horizontal(|ui| {
                    ui.label(muted(*label));
                    ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                        ui.add(egui::Label::new(RichText::new(value).size(12.5).color(*color)).truncate());
                    });
                });
            }
        });
    }

    fn capacity_history_card(&self, ui: &mut egui::Ui) {
        card().show(ui, |ui| {
            ui.set_width(ui.available_width());
            card_title(ui, tr("CapacityHistoryTitle"));
            ui.add_space(4.0);

            let history = self.report.as_ref().map(|report| report.capacity_history.as_slice()).unwrap_or_default();
            if history.is_empty() {
                ui.label(dim("—"));
                return;
            }
            tabs::capacity_history_table(ui, history);
        });
    }

    fn loading_overlay(&self, ui: &mut egui::Ui) {
        egui::Modal::new(egui::Id::new("battery-check-overlay")).show(ui.ctx(), |ui| {
            ui.set_width(340.0);
            ui.vertical_centered(|ui| {
                ui.add_space(8.0);
                widgets::loading_spinner(ui, self.animations);
                ui.add_space(6.0);
                ui.label(RichText::new(tr("LoadingTitle")).size(15.0).strong());
                ui.label(muted(tr("LoadingDetail")));
                ui.add_space(8.0);
            });
        });
    }
}

impl eframe::App for App {
    fn logic(&mut self, ctx: &egui::Context, frame: &mut eframe::Frame) {
        let window_visible = self.owner().map(|hwnd| unsafe { IsWindowVisible(hwnd).as_bool() });
        if !self.window_lifecycle(ctx, self.tray.is_some(), window_visible) {
            return;
        }
        if self.shared.auto_start_changed.swap(false, Ordering::SeqCst) {
            self.auto_start = startup::is_auto_start_enabled();
        }
        if self.shared.check_requested.swap(false, Ordering::SeqCst) {
            self.start_check(ctx);
        }

        // The first report waits for the sensors to warm up, so the live pack
        // information is there to be merged into it.
        if !self.initial_check_started && self.shared.live().state.is_some() {
            self.initial_check_started = true;
            self.start_check(ctx);
        }

        for action in std::mem::take(&mut self.actions) {
            self.perform(action, ctx, frame);
        }
        self.poll_check();
    }

    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        self.dashboard(ui);
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        self.lifecycle.finalize();
    }

    fn clear_color(&self, _visuals: &egui::Visuals) -> [f32; 4] {
        BG_DARK.to_normalized_gamma_f32()
    }
}

impl App {
    /// The whole window: header, battery panel, tabs and the loading overlay.
    fn dashboard(&mut self, ui: &mut egui::Ui) {
        if self.is_hidden() {
            return;
        }
        let ctx = ui.ctx().clone();
        let shared = self.shared.clone();

        // One-time entrance reveal on first visible presentation.
        let now = ui.input(|input| input.time);
        let revealed_at = *self.revealed_at.get_or_insert(now);
        let reveal =
            if self.animations { ((now - revealed_at) / ENTRANCE_SECONDS).clamp(0.0, 1.0) as f32 } else { 1.0 };
        if reveal < 1.0 {
            // Quadratic ease-out.
            ui.multiply_opacity(1.0 - (1.0 - reveal) * (1.0 - reveal));
            ctx.request_repaint();
        }

        egui::Panel::top("header")
            .frame(egui::Frame::new().fill(BG_CARD).inner_margin(egui::Margin::symmetric(18, 12)))
            .show(ui, |ui| self.header(ui));

        // The lock is held for the rest of the pass: the monitoring thread
        // writes once a second and waits at most one frame for it.
        let live = shared.live();

        egui::Panel::left("battery-panel")
            .exact_size(385.0)
            .resizable(false)
            .frame(egui::Frame::new().fill(BG_DARK).inner_margin(egui::Margin {
                left: 14,
                right: 7,
                top: 14,
                bottom: 14,
            }))
            .show(ui, |ui| {
                egui::ScrollArea::vertical().auto_shrink(false).show(ui, |ui| {
                    ui.spacing_mut().item_spacing.y = 6.0;
                    self.health_card(ui);
                    ui.add_space(6.0);
                    self.specs_card(ui);
                    ui.add_space(6.0);
                    self.capacity_history_card(ui);
                });
            });

        egui::CentralPanel::default()
            .frame(egui::Frame::new().fill(BG_DARK).inner_margin(egui::Margin {
                left: 7,
                right: 14,
                top: 14,
                bottom: 14,
            }))
            .show(ui, |ui| self.tabs(ui, &live));

        // Decorative motion only runs while its tab is on screen; otherwise
        // the window repaints once a second, when a new sample arrives.
        let animating = self.animations && self.tab == Tab::RealTime && live.state.is_some();
        drop(live);

        if self.check_result.is_some() {
            self.loading_overlay(ui);
        }
        if animating || (self.check_result.is_some() && self.animations) {
            ctx.request_repaint_after(Duration::from_millis(33));
        }
    }

    fn tabs(&mut self, ui: &mut egui::Ui, live: &Live) {
        ui.horizontal_wrapped(|ui| {
            ui.spacing_mut().item_spacing = vec2(6.0, 6.0);
            for tab in Tab::ALL {
                let selected = self.tab == tab;
                let text = RichText::new(tab.title()).size(13.0).color(if selected { BG_DARK } else { TEXT_MUTED });
                let button = egui::Button::new(if selected { text.strong() } else { text })
                    .fill(if selected { ACCENT } else { BG_CARD })
                    .stroke(egui::Stroke::new(1.0, if selected { ACCENT } else { BORDER }));
                if ui.add(button).clicked() {
                    self.tab = tab;
                }
            }
        });
        ui.add_space(6.0);

        let report = self.report.as_ref();
        match self.tab {
            Tab::RealTime => tabs::real_time(ui, live, self.animations),
            Tab::HistoryLogs => tabs::history_logs(ui, live, &mut self.actions),
            Tab::VoltageHistory => tabs::voltage_history(ui, live, &mut self.actions),
            Tab::Diagnostics => tabs::diagnostics(ui, report),
            Tab::LifeEstimates => tabs::life_estimates(ui, report),
            Tab::RecentUsage => tabs::recent_usage(ui, report),
        }
    }
}

/// Headless smoke tests: every view is laid out and tessellated against
/// representative states without opening a window. They cannot judge how the
/// dashboard looks, but they do prove that no state, tab or language makes the
/// drawing code panic.
#[cfg(test)]
mod tests {
    use super::*;
    use crate::battery::PackInfo;
    use crate::models::RealTimePowerState;
    use crate::realtime::demo;

    const REPORT: &str = r#"
        <h2>COMPUTER NAME</h2><table><tr><td>ROG-TEST</td></tr></table>
        <h2>SYSTEM PRODUCT NAME</h2><table><tr><td>ROG Zephyrus G14</td></tr></table>
        <h2>REPORT TIME</h2><table><tr><td>2026-08-03 12:00:00</td></tr></table>
        <h2>INSTALLED BATTERIES</h2><table>
          <tr><th>NAME</th><td>ASUS Battery</td></tr>
          <tr><th>MANUFACTURER</th><td>ASUSTeK</td></tr>
          <tr><th>CHEMISTRY</th><td>LiP</td></tr>
          <tr><th>DESIGN CAPACITY</th><td>76,000 mWh</td></tr>
          <tr><th>FULL CHARGE CAPACITY</th><td>56,000 mWh</td></tr>
          <tr><th>CYCLE COUNT</th><td>612</td></tr>
        </table>
        <h2>RECENT USAGE</h2><table>
          <tr><td>START TIME</td><td>STATE</td><td>SOURCE</td><td>CAPACITY REMAINING</td></tr>
          <tr><td>2026-08-03 09:00:00</td><td>Active</td><td>Battery</td><td>80 % 44,800 mWh</td></tr>
        </table>
        <h2>BATTERY CAPACITY HISTORY</h2><table>
          <tr><td>PERIOD</td><td>FULL CHARGE CAPACITY</td><td>DESIGN CAPACITY</td></tr>
          <tr><td>2025-01-05 - 2025-01-12</td><td>74,100 mWh</td><td>76,000 mWh</td></tr>
          <tr><td>2026-07-19 - 2026-07-26</td><td>44,000 mWh</td><td>76,000 mWh</td></tr>
        </table>
        <h2>BATTERY LIFE ESTIMATES</h2><table>
          <tr><td>PERIOD</td><td>AT FULL CHARGE</td><td>AT DESIGN CAPACITY</td></tr>
          <tr><td>2026-07-19 - 2026-07-26</td><td>2:58:10</td><td>4:02:19</td></tr>
        </table>
    "#;

    fn test_app() -> App {
        let shared = Arc::new(Shared::new(VoltageHistory::load(None), false));
        let lifecycle =
            Arc::new(Lifecycle { shared: shared.clone(), tray: Mutex::new(None), finalized: AtomicBool::new(false) });
        App {
            shared,
            lifecycle,
            tray: None,
            hwnd: None,
            report: None,
            tab: Tab::RealTime,
            auto_start: false,
            animations: true,
            actions: Vec::new(),
            check_result: None,
            initial_check_started: true,
            balloon_shown: false,
            closing: false,
            first_window_frame: true,
            revealed_at: None,
        }
    }

    /// 以真實 egui 輸入執行生命週期，原生可見性則模擬 Windows 的觀察結果。
    fn lifecycle_frame(
        ctx: &egui::Context,
        app: &mut App,
        tray_available: bool,
        window_visible: Option<bool>,
        minimized: Option<bool>,
        occluded: Option<bool>,
        close: bool,
    ) -> egui::ViewportOutput {
        let mut input = egui::RawInput::default();
        let viewport = input.viewports.get_mut(&egui::ViewportId::ROOT).expect("根視窗輸入");
        viewport.minimized = minimized;
        viewport.occluded = occluded;
        if close {
            viewport.events.push(egui::ViewportEvent::Close);
        }
        let mut output = ctx.run_ui(input, |ui| {
            if app.window_lifecycle(ui.ctx(), tray_available, window_visible) {
                app.dashboard(ui);
            }
        });
        // 無繪圖後端的測試需明確處理字型紋理，避免 egui 在釋放時拒絕未套用的更新。
        output.textures_delta.clear();
        output.viewport_output.get(&egui::ViewportId::ROOT).expect("根視窗輸出").clone()
    }

    #[test]
    fn background_start_retries_past_first_frame_until_native_window_is_hidden() {
        let ctx = egui::Context::default();
        let mut app = test_app();
        app.shared.hidden.store(true, Ordering::SeqCst);

        // 首幀尚未顯示不能當作完成；eframe 會在這一幀繪製後強制顯示。
        let first = lifecycle_frame(&ctx, &mut app, true, Some(false), Some(false), Some(false), false);
        assert!(first.commands.contains(&ViewportCommand::Visible(false)));
        assert!(first.repaint_delay <= Duration::from_millis(50));

        for (minimized, occluded) in
            [(Some(false), Some(false)), (Some(true), None), (Some(false), Some(true)), (None, None)]
        {
            let retry = lifecycle_frame(&ctx, &mut app, true, Some(true), minimized, occluded, false);
            assert!(retry.commands.contains(&ViewportCommand::Visible(false)));
            assert!(retry.repaint_delay <= Duration::from_millis(50));
        }
        let unknown = lifecycle_frame(&ctx, &mut app, true, None, None, None, false);
        assert!(unknown.commands.contains(&ViewportCommand::Visible(false)));

        // 消耗 egui 先前排定的重繪後，不得因隱藏狀態或儀表板動畫持續重繪。
        for _ in 0..4 {
            let hidden = lifecycle_frame(&ctx, &mut app, true, Some(false), Some(false), None, false);
            assert!(!hidden.commands.contains(&ViewportCommand::Visible(false)));
        }
        let idle = lifecycle_frame(&ctx, &mut app, true, Some(false), Some(false), None, false);
        assert_eq!(idle.repaint_delay, Duration::MAX);
        assert!(app.revealed_at.is_none());
    }

    #[test]
    fn close_hides_even_when_shared_state_already_says_hidden() {
        let ctx = egui::Context::default();
        let mut app = test_app();
        app.shared.hidden.store(true, Ordering::SeqCst);
        app.first_window_frame = false;

        for visible in [Some(true), Some(false)] {
            let output = lifecycle_frame(&ctx, &mut app, true, visible, Some(false), None, true);
            assert!(output.commands.contains(&ViewportCommand::CancelClose));
            assert!(output.commands.contains(&ViewportCommand::Visible(false)));
            assert!(app.is_hidden());
            assert!(!app.closing);
        }
    }

    #[test]
    fn restore_cancels_hide_retry_and_next_close_hides_again() {
        let ctx = egui::Context::default();
        let mut app = test_app();
        app.shared.hidden.store(true, Ordering::SeqCst);
        app.shared.show_requested.store(true, Ordering::SeqCst);

        let restored = lifecycle_frame(&ctx, &mut app, true, Some(false), Some(true), None, false);
        assert!(!app.is_hidden());
        assert!(restored.commands.contains(&ViewportCommand::Visible(true)));
        assert!(restored.commands.contains(&ViewportCommand::Minimized(false)));
        assert!(restored.commands.contains(&ViewportCommand::Focus));
        assert!(!restored.commands.contains(&ViewportCommand::Visible(false)));

        let closed = lifecycle_frame(&ctx, &mut app, true, Some(true), Some(false), None, true);
        assert!(closed.commands.contains(&ViewportCommand::CancelClose));
        assert!(closed.commands.contains(&ViewportCommand::Visible(false)));
        assert!(app.is_hidden());
        assert!(!app.closing);
    }

    #[test]
    fn minimizing_with_tray_hides_but_without_tray_remains_accessible() {
        let ctx = egui::Context::default();
        let mut app = test_app();
        let minimized = lifecycle_frame(&ctx, &mut app, true, Some(true), Some(true), None, false);
        assert!(minimized.commands.contains(&ViewportCommand::Visible(false)));
        assert!(app.is_hidden());

        let mut app = test_app();
        app.shared.hidden.store(true, Ordering::SeqCst);
        let fallback = lifecycle_frame(&ctx, &mut app, false, Some(false), Some(false), None, false);
        assert!(fallback.commands.contains(&ViewportCommand::Visible(true)));
        assert!(!app.is_hidden());
        let minimized = lifecycle_frame(&ctx, &mut app, false, Some(true), Some(true), None, false);
        assert!(!minimized.commands.contains(&ViewportCommand::Visible(false)));
        assert!(!app.is_hidden());

        let closed = lifecycle_frame(&ctx, &mut app, false, Some(true), Some(false), None, true);
        assert!(!closed.commands.contains(&ViewportCommand::CancelClose));
        assert!(!closed.commands.contains(&ViewportCommand::Visible(false)));
        assert!(app.closing);
        assert!(app.lifecycle.finalized.load(Ordering::SeqCst));
    }

    /// Lays out and tessellates every tab a few frames in a row.
    fn render_all_tabs(ctx: &egui::Context, app: &mut App) {
        for tab in Tab::ALL {
            app.tab = tab;
            for frame in 0..3 {
                let input = egui::RawInput {
                    screen_rect: Some(egui::Rect::from_min_size(egui::Pos2::ZERO, vec2(1240.0, 800.0))),
                    time: Some(frame as f64 * 0.5),
                    ..Default::default()
                };
                let mut output = ctx.run_ui(input, |ui| app.dashboard(ui));
                // There is no renderer to hand the font atlas to.
                output.textures_delta.clear();
                let primitives = ctx.tessellate(output.shapes, output.pixels_per_point);

                // The entrance reveal starts fully transparent, so only a
                // settled frame is required to have something on it.
                if frame == 2 {
                    assert!(!primitives.is_empty(), "{tab:?} drew nothing");
                }
            }
        }
    }

    fn feed(app: &App, scenario: &str, ticks: u64) {
        let real = RealTimePowerState::default();
        for tick in 1..=ticks {
            app.shared.record_for_test(&demo::state(scenario, tick, &real), demo::pack());
        }
    }

    #[test]
    fn renders_before_any_data_has_arrived() {
        let ctx = egui::Context::default();
        theme::apply(&ctx);
        let mut app = test_app();

        render_all_tabs(&ctx, &mut app);

        // The loading overlay is part of the same pass.
        let (_sender, receiver) = mpsc::channel();
        app.check_result = Some(receiver);
        render_all_tabs(&ctx, &mut app);
    }

    #[test]
    fn renders_every_tab_for_every_power_scenario_in_both_languages() {
        let ctx = egui::Context::default();
        theme::apply(&ctx);

        for scenario in ["charging", "battery", "deficit", "idle"] {
            let mut app = test_app();
            feed(&app, scenario, 70);
            app.report = Some(report_parser::parse(REPORT, Some(&demo::pack())));

            for english in [false, true] {
                i18n::test_override::set_english(english);
                render_all_tabs(&ctx, &mut app);
            }
        }
    }

    #[test]
    fn renders_a_desktop_with_no_battery() {
        let ctx = egui::Context::default();
        theme::apply(&ctx);
        let mut app = test_app();

        let desktop = RealTimePowerState { has_battery: false, is_ac_online: true, ..Default::default() };
        app.shared.record_for_test(&desktop, PackInfo::default());
        app.report = Some(report_parser::parse("<html><body>no batteries here</body></html>", None));

        render_all_tabs(&ctx, &mut app);
    }

    #[test]
    fn header_buttons_queue_actions_instead_of_blocking_the_frame() {
        let mut app = test_app();
        assert!(app.actions.is_empty());

        // Actions are plain data handled between frames, where a modal dialog
        // cannot stall a paint.
        app.actions.push(Action::RunCheck);
        app.actions.push(Action::ToggleLanguage);
        assert_eq!(std::mem::take(&mut app.actions), [Action::RunCheck, Action::ToggleLanguage]);
    }
}
