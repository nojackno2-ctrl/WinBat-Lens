//! Small Win32 conveniences shared by the platform modules.

use windows::core::PCWSTR;
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{
    MessageBoxW, SystemParametersInfoW, IDYES, MB_ICONERROR, MB_ICONINFORMATION, MB_ICONQUESTION, MB_ICONWARNING,
    MB_OK, MB_SETFOREGROUND, MB_YESNO, MESSAGEBOX_STYLE, SPI_GETCLIENTAREAANIMATION,
    SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS,
};

/// A null-terminated UTF-16 copy of `text`.
pub fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

/// A window handle that may be handed to another thread. An HWND is just an
/// identifier; every use of it here goes through thread-safe user32 calls.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SendHwnd(pub isize);

impl SendHwnd {
    pub fn hwnd(self) -> HWND {
        HWND(self.0 as *mut _)
    }
}

#[derive(Debug, Clone, Copy)]
pub enum MessageIcon {
    Info,
    Warning,
    Error,
}

impl MessageIcon {
    fn style(self) -> MESSAGEBOX_STYLE {
        match self {
            Self::Info => MB_ICONINFORMATION,
            Self::Warning => MB_ICONWARNING,
            Self::Error => MB_ICONERROR,
        }
    }
}

fn message_box(owner: Option<HWND>, text: &str, title: &str, style: MESSAGEBOX_STYLE) -> bool {
    let text = wide(text);
    let title = wide(title);
    let result = unsafe { MessageBoxW(owner, PCWSTR(text.as_ptr()), PCWSTR(title.as_ptr()), style | MB_SETFOREGROUND) };
    result == IDYES
}

/// Shows a modal message box with an OK button.
pub fn show_message(owner: Option<HWND>, text: &str, title: &str, icon: MessageIcon) {
    message_box(owner, text, title, MB_OK | icon.style());
}

/// Asks a Yes/No question; returns true for Yes.
pub fn ask_yes_no(owner: Option<HWND>, text: &str, title: &str) -> bool {
    message_box(owner, text, title, MB_YESNO | MB_ICONQUESTION)
}

/// Whether the user has left "animate controls and elements inside windows"
/// enabled. Every decorative animation in the dashboard respects it.
pub fn client_area_animation_enabled() -> bool {
    let mut enabled = windows::core::BOOL(1);
    let read = unsafe {
        SystemParametersInfoW(
            SPI_GETCLIENTAREAANIMATION,
            0,
            Some((&raw mut enabled).cast()),
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
        )
    };
    read.is_err() || enabled.as_bool()
}
