//! WinBat Lens — Windows 電池健康度與即時硬體耗電監測。
//!
//! 產生、解析並視覺化 `powercfg /batteryreport`，同時以電池驅動實測值持續監測
//! 整機耗電。

#![windows_subsystem = "windows"]

mod app_info;
mod battery;
#[cfg(test)]
mod contract_tests;
mod gpu;
mod history;
mod i18n;
mod models;
mod monitor;
mod powercfg;
mod realtime;
mod report_parser;
mod single_instance;
mod startup;
mod tray;
mod ui;
mod win;

use std::io::Write;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};

use win::MessageIcon;

/// 崩潰日誌的位置：優先放在執行檔旁，裝在 Program Files 而無法寫入時改放
/// `%LOCALAPPDATA%\WinBatLens`。
fn crash_log_candidates() -> Vec<PathBuf> {
    let beside_exe = std::env::current_exe().ok().and_then(|exe| Some(exe.parent()?.join("winbat_crash.log")));
    let local_app_data =
        std::env::var_os("LOCALAPPDATA").map(|base| PathBuf::from(base).join("WinBatLens").join("winbat_crash.log"));
    beside_exe.into_iter().chain(local_app_data).collect()
}

fn write_crash_log(report: &str) -> Option<PathBuf> {
    crash_log_candidates().into_iter().find(|path| {
        if let Some(directory) = path.parent() {
            let _ = std::fs::create_dir_all(directory);
        }
        std::fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .and_then(|mut file| writeln!(file, "{report}\n----------------------------------------"))
            .is_ok()
    })
}

/// 記錄未預期的 panic 並告知使用者，而不是讓視窗無聲消失。
fn install_panic_hook() {
    static REPORTED: AtomicBool = AtomicBool::new(false);

    std::panic::set_hook(Box::new(|info| {
        let thread = std::thread::current();
        let report = format!(
            "[WinBat Lens Crash Report]\nVersion: {}\nThread: {}\nTime: {}\nError: {info}\n\nStack Trace:\n{}",
            app_info::VERSION,
            thread.name().unwrap_or("unnamed"),
            chrono::Local::now().format("%Y-%m-%d %H:%M:%S"),
            std::backtrace::Backtrace::force_capture(),
        );
        let log_path = write_crash_log(&report);

        // 一次崩潰只提示一次；背景執行緒連鎖 panic 時不重複跳出對話方塊。
        if REPORTED.swap(true, Ordering::SeqCst) {
            return;
        }

        let location = log_path.map_or_else(String::new, |path| format!("\n\n詳細紀錄已寫入 {}", path.display()));
        win::show_message(
            None,
            &format!("WinBat Lens 啟動或執行時發生例外狀況:\n\n{info}{location}"),
            "WinBat Lens - 執行階段錯誤",
            MessageIcon::Error,
        );
    }));
}

fn main() {
    install_panic_hook();

    // 若已有其他執行個體運作中，即交由其喚醒，本行程自動結束。
    if !single_instance::try_claim_ownership() {
        return;
    }

    let start_hidden = std::env::args().any(|argument| argument.eq_ignore_ascii_case(startup::BACKGROUND_ARGUMENT));
    let result = ui::run(start_hidden);
    single_instance::release();

    if let Err(error) = result {
        win::show_message(
            None,
            &format!("WinBat Lens 無法建立視窗:\n\n{error}"),
            "WinBat Lens - 執行階段錯誤",
            MessageIcon::Error,
        );
    }
}
