using System;
using System.Reflection;

namespace WinBatLens.Services
{
    /// <summary>
    /// The application version, read from the assembly rather than hard-coded,
    /// so the number on screen can never drift from the &lt;Version&gt; in
    /// WinBatLens.csproj — bumping the release only touches the project file.
    /// </summary>
    public static class AppInfo
    {
        /// <summary>Release number alone, e.g. "1.1.2".</summary>
        public static string Version { get; } = ReadVersion();

        /// <summary>Version as shown in the UI, e.g. "v1.1.2".</summary>
        public static string DisplayVersion { get; } = "v" + Version;

        private static string ReadVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();

                // AssemblyInformationalVersion carries the <Version> value
                // verbatim, so it is the closest thing to what the csproj says.
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
                // A version string is decoration; never let it stop startup.
            }

            return "?";
        }

        /// <summary>
        /// Reduces any of the version spellings the build emits to the plain
        /// three-field release number, so two of them can be compared:
        /// AssemblyInformationalVersion / ProductVersion arrive as
        /// "1.1.2+&lt;commit sha&gt;" once SourceLink is active, and FileVersion
        /// always carries a fourth field the csproj never sets meaningfully.
        /// Returns null for anything unusable.
        /// </summary>
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
