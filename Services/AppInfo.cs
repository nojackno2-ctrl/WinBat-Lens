using System;
using System.Reflection;

namespace WinBatLens.Services
{
    /// <summary>
    /// 提供應用程式版本號統一讀取服務。
    /// 自動自 Assembly（對應 WinBatLens.csproj 中的 &lt;Version&gt; 屬性）讀取，避免硬編碼版本號與專案檔不一致。
    /// </summary>
    public static class AppInfo
    {
        /// <summary>純版本號字串（例如："1.1.4"）。</summary>
        public static string Version { get; } = ReadVersion();

        /// <summary>顯示於 UI 上的版本字串（例如："v1.1.4"）。</summary>
        public static string DisplayVersion { get; } = "v" + Version;

        /// <summary>
        /// 自目前執行的 Assembly 讀取 InformationalVersion 或 Assembly Version。
        /// </summary>
        private static string ReadVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();

                // AssemblyInformationalVersion 對應 csproj 內的 <Version> 設定
                var informational = Normalize(assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion);

                if (informational != null) return informational;

                var version = assembly.GetName().Version;
                if (version != null)
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }
            }
            catch
            {
                // 版本號讀取失敗時不影響應用程式啟動
}
            return "?";
        }

        /// <summary>
        /// 將版本號字串正規化為標準的「三位數」發行版本格式 (Major.Minor.Build)。
        /// 去除 SourceLink 產生的 Git Commit Hash 標記 (+sha) 與補零的第四位。
        /// </summary>
        /// <param name="raw">原始版本字串。</param>
        /// <returns>正規化後之三欄位版本號，若格式無效則傳回 null。</returns>
        public static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            int plus = raw.IndexOf('+');
            if (plus >= 0) raw = raw.Substring(0, plus);

            raw = raw.Trim();
            if (raw.Length == 0) return null;

            var parts = raw.Split('.');
            if (parts.Length == 4 && parts[3] == "0")
            {
                raw = string.Join(".", parts, 0, 3);
            }

            return raw;
        }
    }
}
