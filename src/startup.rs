//! Windows 登錄檔 (HKCU Run 鍵值) 開機自動啟動管理。
//!
//! 安裝程式的「開機自動啟動」選項寫入的是同一個 per-user Run 值，因此兩邊永遠同步。

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::ERROR_FILE_NOT_FOUND;
use windows::Win32::System::Registry::{
    RegCloseKey, RegDeleteValueW, RegOpenKeyExW, RegQueryValueExW, RegSetValueExW, HKEY, HKEY_CURRENT_USER,
    KEY_QUERY_VALUE, KEY_SET_VALUE, REG_SAM_FLAGS, REG_SZ,
};

use crate::win::wide;

const RUN_KEY: PCWSTR = w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
const APP_NAME: PCWSTR = w!("WinBatLens");

/// 開機自啟動時傳遞之背景常駐參數旗標（最小化至系統工作列托盤）。
pub const BACKGROUND_ARGUMENT: &str = "--background";

struct RunKey(HKEY);

impl RunKey {
    fn open(access: REG_SAM_FLAGS) -> Option<Self> {
        let mut key = HKEY::default();
        unsafe { RegOpenKeyExW(HKEY_CURRENT_USER, RUN_KEY, None, access, &mut key) }.is_ok().then_some(Self(key))
    }
}

impl Drop for RunKey {
    fn drop(&mut self) {
        unsafe {
            let _ = RegCloseKey(self.0);
        }
    }
}

/// 檢查當前使用者是否已啟用開機自動啟動。
pub fn is_auto_start_enabled() -> bool {
    let Some(key) = RunKey::open(KEY_QUERY_VALUE) else { return false };
    unsafe { RegQueryValueExW(key.0, APP_NAME, None, None, None, None) }.is_ok()
}

/// 開機啟動時執行的完整命令列：加上引號的 EXE 路徑與 `--background` 參數。
fn auto_start_command(exe_path: &str) -> String {
    format!("\"{exe_path}\" {BACKGROUND_ARGUMENT}")
}

/// 設定或取消開機自動啟動。設定成功傳回 true。
pub fn set_auto_start(enable: bool) -> bool {
    let Some(key) = RunKey::open(KEY_SET_VALUE) else { return false };

    if !enable {
        let result = unsafe { RegDeleteValueW(key.0, APP_NAME) };
        // 本來就沒有這個值，等同已取消。
        return result.is_ok() || result == ERROR_FILE_NOT_FOUND;
    }

    let Ok(exe_path) = std::env::current_exe() else { return false };
    let command = wide(&auto_start_command(&exe_path.to_string_lossy()));
    let bytes = unsafe { std::slice::from_raw_parts(command.as_ptr().cast::<u8>(), command.len() * 2) };

    unsafe { RegSetValueExW(key.0, APP_NAME, None, REG_SZ, Some(bytes)) }.is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn auto_start_command_quotes_the_path_and_starts_in_the_background() {
        assert_eq!(
            auto_start_command(r"C:\Program Files\WinBat Lens\WinBatLens.exe"),
            r#""C:\Program Files\WinBat Lens\WinBatLens.exe" --background"#
        );
    }
}
