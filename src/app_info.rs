//! 應用程式版本號統一讀取。版本只寫在 Cargo.toml 一處，build.rs 會把同一個
//! 數字蓋進 EXE 的版本資源，因此 UI、安裝程式與單一執行個體交接看到的永遠一致。

/// 純版本號字串（例如："2.0.0"）。
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// 顯示於 UI 上的版本字串（例如："v2.0.0"）。
pub fn display_version() -> String {
    format!("v{VERSION}")
}

/// 將版本號字串正規化為標準的「三位數」發行版本格式 (Major.Minor.Build)。
/// 去除 Git Commit Hash 標記 (+sha) 與補零的第四位。
pub fn normalize(raw: &str) -> Option<String> {
    let raw = raw.split('+').next().unwrap_or("").trim();
    if raw.is_empty() {
        return None;
    }

    let parts: Vec<&str> = raw.split('.').collect();
    if parts.len() == 4 && parts[3] == "0" {
        return Some(parts[..3].join("."));
    }

    Some(raw.to_string())
}

#[cfg(test)]
mod tests {
    use super::normalize;

    #[test]
    fn normalizes_file_versions_to_three_fields() {
        assert_eq!(normalize("1.1.8.0").as_deref(), Some("1.1.8"));
        assert_eq!(normalize("1.1.8+abc123").as_deref(), Some("1.1.8"));
        assert_eq!(normalize(" 2.0.0 ").as_deref(), Some("2.0.0"));
        assert_eq!(normalize("1.2.3.4").as_deref(), Some("1.2.3.4"));
        assert_eq!(normalize("   "), None);
    }
}
