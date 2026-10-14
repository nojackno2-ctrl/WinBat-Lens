//! Windows 系統工作列托盤（Tray Icon）。
//!
//! 圖示根據即時功率（瓦特 W）動態渲染清晰數字與色彩（充電綠色、放電紅色）。
//! 托盤擁有自己的執行緒與隱藏視窗，因此主視窗隱藏、不再重繪時，圖示、提示
//! 文字與右鍵選單仍然照常運作。

use std::sync::mpsc;
use std::sync::{Arc, Mutex};

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::{COLORREF, HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, SIZE, WPARAM};
use windows::Win32::Graphics::Gdi::{
    CreateBitmap, CreateCompatibleDC, CreateDIBSection, CreateFontW, DeleteDC, DeleteObject, DrawTextW,
    GetTextExtentPoint32W, SelectObject, SetBkMode, SetTextColor, ANTIALIASED_QUALITY, BITMAPINFO, BITMAPINFOHEADER,
    BI_RGB, CLIP_DEFAULT_PRECIS, DEFAULT_CHARSET, DIB_RGB_COLORS, DT_CENTER, DT_NOCLIP, DT_NOPREFIX, DT_SINGLELINE,
    DT_VCENTER, FW_BOLD, HGDIOBJ, OUT_DEFAULT_PRECIS, TRANSPARENT,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::Shell::{
    Shell_NotifyIconW, NIF_ICON, NIF_INFO, NIF_MESSAGE, NIF_SHOWTIP, NIF_TIP, NIIF_INFO, NIM_ADD, NIM_DELETE,
    NIM_MODIFY, NOTIFYICONDATAW, NOTIFY_ICON_DATA_FLAGS,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreateIconIndirect, CreatePopupMenu, CreateWindowExW, DefWindowProcW, DestroyIcon, DestroyMenu,
    DestroyWindow, DispatchMessageW, GetCursorPos, GetMessageW, GetWindowLongPtrW, LoadIconW, PostMessageW,
    PostQuitMessage, RegisterClassW, RegisterWindowMessageW, SendMessageW, SetForegroundWindow, SetMenuDefaultItem,
    SetWindowLongPtrW, TrackPopupMenu, TranslateMessage, GWLP_USERDATA, HICON, ICONINFO, IDI_APPLICATION, MF_CHECKED,
    MF_SEPARATOR, MF_STRING, MSG, TPM_NONOTIFY, TPM_RETURNCMD, TPM_RIGHTBUTTON, WINDOW_EX_STYLE, WINDOW_STYLE, WM_APP,
    WM_LBUTTONDBLCLK, WM_NULL, WM_RBUTTONUP, WNDCLASSW,
};

use crate::i18n::tr;
use crate::models::RealTimePowerState;
use crate::startup;
use crate::win::{wide, SendHwnd};

const WM_TRAY_CALLBACK: u32 = WM_APP + 1;
const WM_TRAY_REFRESH: u32 = WM_APP + 2;
const WM_TRAY_REMOVE: u32 = WM_APP + 3;

const ID_SHOW: usize = 1;
const ID_CHECK: usize = 2;
const ID_AUTO_START: usize = 3;
const ID_EXIT: usize = 4;

const ICON_SIZE: i32 = 32;

/// MAKEINTRESOURCE(1): the application icon embedded by build.rs.
const APP_ICON_RESOURCE: *const u16 = std::ptr::without_provenance(1);

/// #10B981 翡翠綠：充電。
const COLOR_CHARGING: [u8; 3] = [16, 185, 129];
/// #F43F5E 玫瑰紅：放電。
const COLOR_DISCHARGING: [u8; 3] = [244, 63, 94];
/// 板岩灰：未充放電 / 滿電待機。
const COLOR_IDLE: [u8; 3] = [148, 163, 184];

/// 使用者在托盤上做的事。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrayAction {
    Show,
    RunCheck,
    AutoStartChanged,
    Exit,
}

/// Formats tray-icon wattage. Keep sub-watt measurements visible with one
/// decimal place instead of rounding them to a misleading zero.
pub fn format_wattage_for_icon(watts: f64) -> String {
    if watts < 1.0 {
        return format!("{watts:.1}");
    }

    let whole = watts.round_ties_even() as i64;
    if whole > 99 {
        "99+".into()
    } else {
        whole.to_string()
    }
}

/// 托盤圖示要畫的文字與顏色。
pub fn icon_for_state(state: &RealTimePowerState) -> (String, [u8; 3]) {
    if state.is_charging && state.is_charge_rate_measured {
        (format_wattage_for_icon(state.charging_rate_w), COLOR_CHARGING)
    } else if state.is_discharge_rate_measured {
        (format_wattage_for_icon(state.discharge_rate_w), COLOR_DISCHARGING)
    } else {
        ("–".into(), COLOR_IDLE)
    }
}

/// 滑鼠停在托盤圖示上時顯示的提示文字。
pub fn tooltip_for_state(state: &RealTimePowerState) -> String {
    let power = if state.is_charging && state.is_charge_rate_measured {
        format!("+{:.1}W Charging", state.charging_rate_w)
    } else if state.is_charger_deficit {
        format!("-{:.1}W (charger too weak)", state.discharge_rate_w)
    } else if !state.is_ac_online && state.is_discharge_rate_measured {
        format!("-{:.1}W Discharging", state.discharge_rate_w)
    } else {
        "-- W".into()
    };

    if !state.has_battery {
        return format!("WinBat Lens - {}", state.power_status_text);
    }
    format!("WinBat Lens - {}\nLevel: {}% | {power}", state.power_status_text, state.battery_percent)
}

/// Turns white-on-black antialiased text into premultiplied ARGB in `rgb`:
/// GDI text rendering leaves the alpha channel untouched, so coverage is
/// recovered from the brightest channel of each pixel.
fn tint_coverage(pixels: &mut [u32], rgb: [u8; 3]) {
    for pixel in pixels {
        let coverage = (*pixel & 0xFF).max((*pixel >> 8) & 0xFF).max((*pixel >> 16) & 0xFF);
        let scale = |channel: u8| (channel as u32 * coverage + 127) / 255;
        *pixel = (coverage << 24) | (scale(rgb[0]) << 16) | (scale(rgb[1]) << 8) | scale(rgb[2]);
    }
}

/// Renders `text` centred in a 32x32 transparent icon, at the largest bold
/// size that still fits.
unsafe fn render_text_icon(text: &str, rgb: [u8; 3]) -> Option<HICON> {
    let dc = CreateCompatibleDC(None);
    if dc.is_invalid() {
        return None;
    }

    let info = BITMAPINFO {
        bmiHeader: BITMAPINFOHEADER {
            biSize: size_of::<BITMAPINFOHEADER>() as u32,
            biWidth: ICON_SIZE,
            // Negative height: top-down rows.
            biHeight: -ICON_SIZE,
            biPlanes: 1,
            biBitCount: 32,
            biCompression: BI_RGB.0,
            ..Default::default()
        },
        ..Default::default()
    };

    let mut bits = std::ptr::null_mut();
    let Ok(color) = CreateDIBSection(Some(dc), &info, DIB_RGB_COLORS, &mut bits, None, 0) else {
        let _ = DeleteDC(dc);
        return None;
    };
    let previous_bitmap = SelectObject(dc, HGDIOBJ(color.0));

    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, COLORREF(0x00FF_FFFF));

    let mut text_utf16: Vec<u16> = text.encode_utf16().collect();
    let make_font = |height: i32| {
        CreateFontW(
            -height,
            0,
            0,
            0,
            FW_BOLD.0 as i32,
            0,
            0,
            0,
            DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS,
            ANTIALIASED_QUALITY,
            0,
            w!("Segoe UI"),
        )
    };

    // Largest size whose text still fits the icon's width.
    let mut font = make_font(10);
    for height in (10..=30).rev() {
        let candidate = make_font(height);
        let previous_font = SelectObject(dc, HGDIOBJ(candidate.0));
        let mut extent = SIZE::default();
        let measured = GetTextExtentPoint32W(dc, &text_utf16, &mut extent).as_bool();
        SelectObject(dc, previous_font);

        if measured && extent.cx <= ICON_SIZE {
            let _ = DeleteObject(HGDIOBJ(font.0));
            font = candidate;
            break;
        }
        let _ = DeleteObject(HGDIOBJ(candidate.0));
    }

    let previous_font = SelectObject(dc, HGDIOBJ(font.0));
    let mut bounds = RECT { left: 0, top: 0, right: ICON_SIZE, bottom: ICON_SIZE };
    DrawTextW(dc, &mut text_utf16, &mut bounds, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOCLIP | DT_NOPREFIX);
    SelectObject(dc, previous_font);
    let _ = DeleteObject(HGDIOBJ(font.0));

    if !bits.is_null() {
        let pixels = std::slice::from_raw_parts_mut(bits.cast::<u32>(), (ICON_SIZE * ICON_SIZE) as usize);
        tint_coverage(pixels, rgb);
    }

    SelectObject(dc, previous_bitmap);
    let _ = DeleteDC(dc);

    // With a 32-bit colour bitmap the mask is ignored, but one must exist.
    let mask = CreateBitmap(ICON_SIZE, ICON_SIZE, 1, 1, None);
    let icon_info = ICONINFO { fIcon: true.into(), xHotspot: 0, yHotspot: 0, hbmMask: mask, hbmColor: color };
    let icon = CreateIconIndirect(&icon_info).ok();

    let _ = DeleteObject(HGDIOBJ(mask.0));
    let _ = DeleteObject(HGDIOBJ(color.0));
    icon
}

/// What the monitoring thread wants the tray to show next.
#[derive(Default)]
struct Pending {
    icon: Option<(String, [u8; 3])>,
    tooltip: Option<String>,
    balloon: Option<(String, String)>,
}

struct TrayState {
    data: NOTIFYICONDATAW,
    pending: Arc<Mutex<Pending>>,
    on_action: Box<dyn Fn(TrayAction)>,
    /// The dynamically drawn icon currently handed to the shell, owned here.
    drawn_icon: Option<HICON>,
    last_drawn: Option<(String, [u8; 3])>,
    last_tooltip: String,
    taskbar_created: u32,
}

fn copy_into(target: &mut [u16], text: &str) {
    let mut len = 0;
    for unit in text.encode_utf16().take(target.len() - 1) {
        target[len] = unit;
        len += 1;
    }
    // Do not leave half of a surrogate pair at the truncation point.
    if len > 0 && (0xD800..0xDC00).contains(&target[len - 1]) {
        len -= 1;
    }
    target[len..].fill(0);
}

impl TrayState {
    unsafe fn refresh(&mut self) {
        let pending = match self.pending.lock() {
            Ok(mut pending) => std::mem::take(&mut *pending),
            Err(_) => return,
        };

        let mut flags = NOTIFY_ICON_DATA_FLAGS(0);
        let mut replaced_icon = None;

        // Redrawing is skipped while the figure and colour are unchanged.
        if let Some((text, rgb)) = pending.icon.filter(|icon| self.last_drawn.as_ref() != Some(icon)) {
            if let Some(icon) = render_text_icon(&text, rgb) {
                replaced_icon = self.drawn_icon.replace(icon);
                self.data.hIcon = icon;
                self.last_drawn = Some((text, rgb));
                flags |= NIF_ICON;
            }
        }

        // Each change is a call into the shell, so the tooltip is only sent
        // when it would actually read differently.
        if let Some(tooltip) = pending.tooltip.filter(|tooltip| *tooltip != self.last_tooltip) {
            copy_into(&mut self.data.szTip, &tooltip);
            self.last_tooltip = tooltip;
            flags |= NIF_TIP | NIF_SHOWTIP;
        }

        if let Some((title, text)) = pending.balloon {
            copy_into(&mut self.data.szInfoTitle, &title);
            copy_into(&mut self.data.szInfo, &text);
            self.data.dwInfoFlags = NIIF_INFO;
            flags |= NIF_INFO;
        }

        if flags.0 != 0 {
            self.data.uFlags = flags;
            let _ = Shell_NotifyIconW(NIM_MODIFY, &self.data);
        }

        // The shell has copied the new icon by now, so the old one can go.
        if let Some(old) = replaced_icon {
            let _ = DestroyIcon(old);
        }
    }

    unsafe fn add_icon(&mut self) {
        self.data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        let _ = Shell_NotifyIconW(NIM_ADD, &self.data);
    }

    unsafe fn show_menu(&self, hwnd: HWND) {
        let Ok(menu) = CreatePopupMenu() else { return };

        let append = |id: usize, key: &'static str, checked: bool| {
            let label = wide(tr(key));
            let flags = if checked { MF_STRING | MF_CHECKED } else { MF_STRING };
            let _ = AppendMenuW(menu, flags, id, PCWSTR(label.as_ptr()));
        };

        append(ID_SHOW, "TrayShow", false);
        append(ID_CHECK, "TrayCheck", false);
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        append(ID_AUTO_START, "TrayAutoStart", startup::is_auto_start_enabled());
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        append(ID_EXIT, "TrayExit", false);
        let _ = SetMenuDefaultItem(menu, ID_SHOW as u32, 0);

        let mut cursor = POINT::default();
        let _ = GetCursorPos(&mut cursor);

        // Without foreground status the menu would not dismiss when the user
        // clicks elsewhere.
        let _ = SetForegroundWindow(hwnd);
        let chosen =
            TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, cursor.x, cursor.y, None, hwnd, None);
        let _ = PostMessageW(Some(hwnd), WM_NULL, WPARAM(0), LPARAM(0));
        let _ = DestroyMenu(menu);

        match chosen.0 as usize {
            ID_SHOW => (self.on_action)(TrayAction::Show),
            ID_CHECK => (self.on_action)(TrayAction::RunCheck),
            ID_AUTO_START => {
                startup::set_auto_start(!startup::is_auto_start_enabled());
                (self.on_action)(TrayAction::AutoStartChanged);
            }
            ID_EXIT => (self.on_action)(TrayAction::Exit),
            _ => {}
        }
    }

    unsafe fn remove(&mut self) {
        let _ = Shell_NotifyIconW(NIM_DELETE, &self.data);
        if let Some(icon) = self.drawn_icon.take() {
            let _ = DestroyIcon(icon);
        }
    }
}

unsafe extern "system" fn window_proc(hwnd: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    let state = GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut TrayState;
    if state.is_null() {
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }
    let state = &mut *state;

    match message {
        WM_TRAY_CALLBACK => {
            match lparam.0 as u32 {
                WM_LBUTTONDBLCLK => (state.on_action)(TrayAction::Show),
                WM_RBUTTONUP => state.show_menu(hwnd),
                _ => {}
            }
            LRESULT(0)
        }
        WM_TRAY_REFRESH => {
            state.refresh();
            LRESULT(0)
        }
        WM_TRAY_REMOVE => {
            state.remove();
            PostQuitMessage(0);
            LRESULT(0)
        }
        // Explorer restarted: the notification area is empty again.
        _ if message == state.taskbar_created && message != 0 => {
            state.add_icon();
            LRESULT(0)
        }
        _ => DefWindowProcW(hwnd, message, wparam, lparam),
    }
}

/// A handle to the tray icon, usable from any thread.
pub struct Tray {
    hwnd: SendHwnd,
    pending: Arc<Mutex<Pending>>,
}

impl Tray {
    /// Creates the notification icon on its own thread. `on_action` runs on
    /// that thread, so it must only do thread-safe work.
    pub fn spawn(on_action: impl Fn(TrayAction) + Send + 'static) -> Option<Self> {
        let pending = Arc::new(Mutex::new(Pending::default()));
        let (ready_tx, ready_rx) = mpsc::channel();

        let thread_pending = pending.clone();
        std::thread::Builder::new()
            .name("tray".into())
            .spawn(move || unsafe { run(thread_pending, Box::new(on_action), ready_tx) })
            .ok()?;

        let hwnd = ready_rx.recv().ok()??;
        Some(Self { hwnd, pending })
    }

    fn post(&self, update: impl FnOnce(&mut Pending)) {
        if let Ok(mut pending) = self.pending.lock() {
            update(&mut pending);
        }
        unsafe {
            let _ = PostMessageW(Some(self.hwnd.hwnd()), WM_TRAY_REFRESH, WPARAM(0), LPARAM(0));
        }
    }

    /// 根據即時電源與功率狀態更新圖示與提示文字。
    pub fn update(&self, state: &RealTimePowerState) {
        self.post(|pending| {
            pending.icon = Some(icon_for_state(state));
            pending.tooltip = Some(tooltip_for_state(state));
        });
    }

    pub fn show_balloon(&self, title: &str, text: &str) {
        self.post(|pending| pending.balloon = Some((title.to_string(), text.to_string())));
    }

    /// Takes the icon out of the notification area and stops the tray thread.
    /// If the process died without doing this, the icon would linger until the
    /// user happened to hover over it.
    pub fn remove(&self) {
        unsafe {
            SendMessageW(self.hwnd.hwnd(), WM_TRAY_REMOVE, Some(WPARAM(0)), Some(LPARAM(0)));
        }
    }
}

unsafe fn run(pending: Arc<Mutex<Pending>>, on_action: Box<dyn Fn(TrayAction)>, ready: mpsc::Sender<Option<SendHwnd>>) {
    let instance: HINSTANCE = GetModuleHandleW(None).map(Into::into).unwrap_or_default();
    let class_name = w!("WinBatLensTrayWindow");

    let class = WNDCLASSW {
        lpfnWndProc: Some(window_proc),
        hInstance: instance,
        lpszClassName: class_name,
        ..Default::default()
    };
    RegisterClassW(&class);

    let created = CreateWindowExW(
        WINDOW_EX_STYLE(0),
        class_name,
        w!("WinBat Lens"),
        WINDOW_STYLE(0),
        0,
        0,
        0,
        0,
        None,
        None,
        Some(instance),
        None,
    );
    let Ok(hwnd) = created else {
        let _ = ready.send(None);
        return;
    };

    let base_icon = LoadIconW(Some(instance), PCWSTR(APP_ICON_RESOURCE))
        .or_else(|_| LoadIconW(None, IDI_APPLICATION))
        .unwrap_or_default();

    let mut data = NOTIFYICONDATAW {
        cbSize: size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: hwnd,
        uID: 1,
        uCallbackMessage: WM_TRAY_CALLBACK,
        hIcon: base_icon,
        ..Default::default()
    };
    let tooltip = tr("TrayTooltip");
    copy_into(&mut data.szTip, tooltip);

    let mut state = Box::new(TrayState {
        data,
        pending,
        on_action,
        drawn_icon: None,
        last_drawn: None,
        last_tooltip: tooltip.to_string(),
        taskbar_created: RegisterWindowMessageW(w!("TaskbarCreated")),
    });
    state.add_icon();
    SetWindowLongPtrW(hwnd, GWLP_USERDATA, (&raw mut *state) as isize);

    let _ = ready.send(Some(SendHwnd(hwnd.0 as isize)));

    let mut message = MSG::default();
    while GetMessageW(&mut message, None, 0, 0).as_bool() {
        let _ = TranslateMessage(&message);
        DispatchMessageW(&message);
    }

    SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
    let _ = DestroyWindow(hwnd);
    drop(state);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keeps_sub_watt_power_visible() {
        assert_eq!(format_wattage_for_icon(0.1), "0.1");
        assert_eq!(format_wattage_for_icon(0.9), "0.9");
    }

    #[test]
    fn keeps_whole_watt_and_overflow_formatting() {
        assert_eq!(format_wattage_for_icon(1.4), "1");
        assert_eq!(format_wattage_for_icon(49.6), "50");
        assert_eq!(format_wattage_for_icon(99.4), "99");
        assert_eq!(format_wattage_for_icon(100.0), "99+");
    }

    #[test]
    fn icon_reflects_the_direction_of_power_flow() {
        let charging = RealTimePowerState {
            is_ac_online: true,
            is_charging: true,
            is_charge_rate_measured: true,
            charging_rate_w: 56.2,
            ..Default::default()
        };
        assert_eq!(icon_for_state(&charging), ("56".into(), COLOR_CHARGING));
        assert!(tooltip_for_state(&charging).ends_with("+56.2W Charging"));

        let deficit = RealTimePowerState {
            is_ac_online: true,
            is_charger_deficit: true,
            is_discharge_rate_measured: true,
            discharge_rate_w: 12.0,
            ..Default::default()
        };
        assert_eq!(icon_for_state(&deficit), ("12".into(), COLOR_DISCHARGING));
        assert!(tooltip_for_state(&deficit).ends_with("-12.0W (charger too weak)"));

        let idle = RealTimePowerState { is_ac_online: true, battery_percent: 95, ..Default::default() };
        assert_eq!(icon_for_state(&idle), ("–".into(), COLOR_IDLE));
        assert!(tooltip_for_state(&idle).ends_with("Level: 95% | -- W"));
    }

    #[test]
    fn tints_coverage_into_premultiplied_argb() {
        let mut pixels = [0x0000_0000, 0x00FF_FFFF, 0x0080_8080];
        tint_coverage(&mut pixels, [244, 63, 94]);

        assert_eq!(pixels[0], 0x0000_0000);
        assert_eq!(pixels[1], 0xFFF4_3F5E);
        assert_eq!(pixels[2] >> 24, 0x80);
        assert_eq!((pixels[2] >> 16) & 0xFF, 122);
    }

    #[test]
    fn truncates_long_text_without_splitting_surrogates() {
        let mut buffer = [0xFFFFu16; 4];
        copy_into(&mut buffer, "ab😀");
        assert_eq!(buffer, ['a' as u16, 'b' as u16, 0, 0]);

        let mut buffer = [0xFFFFu16; 8];
        copy_into(&mut buffer, "ok");
        assert_eq!(&buffer[..3], ['o' as u16, 'k' as u16, 0]);
    }
}
