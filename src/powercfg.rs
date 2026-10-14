//! 呼叫 Windows 系統指令 `powercfg /batteryreport` 產生 HTML 電池報告。
//! 包含 10 秒逾時保護與暫存檔自動清理。

use std::fs;
use std::io::Read;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const TIMEOUT: Duration = Duration::from_secs(10);

/// 離開作用域時刪除暫存報告，不論成功、失敗或逾時。
struct TempReport(PathBuf);

impl Drop for TempReport {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}

fn unique_temp_path() -> PathBuf {
    let nanos =
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |elapsed| elapsed.as_nanos());
    std::env::temp_dir().join(format!("winbat_report_{}_{nanos:x}.html", std::process::id()))
}

/// 將報告檔案位元組解碼為文字：依 BOM 辨識 UTF-8 / UTF-16，否則視為 UTF-8。
/// 無效位元組以替代字元取代，而不是讓整份報告讀取失敗。
pub fn decode_report(bytes: &[u8]) -> String {
    let utf16 = |bytes: &[u8], to_u16: fn([u8; 2]) -> u16| {
        let units: Vec<u16> = bytes.chunks_exact(2).map(|pair| to_u16([pair[0], pair[1]])).collect();
        String::from_utf16_lossy(&units)
    };

    if let Some(rest) = bytes.strip_prefix(b"\xEF\xBB\xBF") {
        String::from_utf8_lossy(rest).into_owned()
    } else if let Some(rest) = bytes.strip_prefix(b"\xFF\xFE") {
        utf16(rest, u16::from_le_bytes)
    } else if let Some(rest) = bytes.strip_prefix(b"\xFE\xFF") {
        utf16(rest, u16::from_be_bytes)
    } else {
        String::from_utf8_lossy(bytes).into_owned()
    }
}

/// 讀取並解碼一份電池報告 HTML 檔案。
pub fn read_report(path: &Path) -> std::io::Result<String> {
    fs::read(path).map(|bytes| decode_report(&bytes))
}

/// 執行 powercfg 產生電池報告，傳回 HTML 內文；失敗時傳回可直接顯示的錯誤訊息。
/// 會阻塞最多約 10 秒，請在背景執行緒呼叫。
pub fn generate_report() -> Result<String, String> {
    let report = TempReport(unique_temp_path());

    let mut child = Command::new("powercfg")
        .arg("/batteryreport")
        .arg("/output")
        .arg(&report.0)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .map_err(|error| format!("執行 powercfg 失敗: {error}"))?;

    let started = Instant::now();
    loop {
        match child.try_wait() {
            Ok(Some(_)) => break,
            Ok(None) if started.elapsed() >= TIMEOUT => {
                let _ = child.kill();
                let _ = child.wait();
                return Err("powercfg 執行逾時（超過 10 秒），已強制結束。".into());
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(50)),
            Err(error) => return Err(format!("執行 powercfg 失敗: {error}")),
        }
    }

    if report.0.exists() {
        return read_report(&report.0).map_err(|error| format!("無法讀取報告檔案: {error}"));
    }

    let mut stderr = String::new();
    if let Some(mut pipe) = child.stderr.take() {
        let mut bytes = Vec::new();
        let _ = pipe.read_to_end(&mut bytes);
        stderr = String::from_utf8_lossy(&bytes).trim().to_string();
    }

    Err(if stderr.is_empty() { "產生的報告檔案不存在。".into() } else { stderr })
}

#[cfg(test)]
mod tests {
    use super::decode_report;

    #[test]
    fn decodes_reports_by_byte_order_mark() {
        assert_eq!(decode_report(b"<html>"), "<html>");
        assert_eq!(decode_report(b"\xEF\xBB\xBF<html>"), "<html>");
        assert_eq!(decode_report(b"\xFF\xFE<\0h\0"), "<h");
        assert_eq!(decode_report(b"\xFE\xFF\0<\0h"), "<h");
        assert_eq!(decode_report(b"ok\xFF"), "ok\u{fffd}");
    }
}
