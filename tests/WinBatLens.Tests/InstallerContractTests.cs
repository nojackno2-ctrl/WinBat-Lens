using Xunit;

namespace WinBatLens.Tests;

public sealed class InstallerContractTests
{
    [Fact]
    public void InstallerProvidesConsistentUpgradeAndDiagnosticsMetadata()
    {
        var script = ReadInstallerScript();

        foreach (var directive in new[]
        {
            "DisableProgramGroupPage=yes",
            "UsePreviousAppDir=yes",
            "UsePreviousGroup=yes",
            "UsePreviousTasks=yes",
            "CreateUninstallRegKey=yes",
            "SetupLogging=yes",
            "UninstallLogging=yes",
            "CloseApplicationsFilter={#MyAppExeName}",
            "RestartApplications=no"
        })
        {
            Assert.Contains(directive, script.Lines, StringComparer.Ordinal);
        }

        Assert.Contains("AppPublisherURL={#MyAppURL}", script.Lines, StringComparer.Ordinal);
        Assert.Contains("AppSupportURL={#MyAppURL}/issues", script.Lines, StringComparer.Ordinal);
        Assert.Contains("AppUpdatesURL={#MyAppURL}/releases", script.Lines, StringComparer.Ordinal);
    }

    [Fact]
    public void StartMenuAndOptionalShortcutPolicyIsPreserved()
    {
        var script = ReadInstallerScript();
        var startMenuShortcut = Assert.Single(script.Lines, line =>
            line.StartsWith("Name: \"{group}\\{#MyAppName}\";", StringComparison.Ordinal));
        var uninstallShortcut = Assert.Single(script.Lines, line =>
            line.Contains("{cm:UninstallProgram,{#MyAppName}}", StringComparison.Ordinal));
        var desktopTask = Assert.Single(script.Lines, line =>
            line.StartsWith("Name: \"desktopicon\";", StringComparison.Ordinal));
        var startupTask = Assert.Single(script.Lines, line =>
            line.StartsWith("Name: \"autostart\";", StringComparison.Ordinal));

        Assert.DoesNotContain("Tasks:", startMenuShortcut, StringComparison.Ordinal);
        Assert.DoesNotContain("Tasks:", uninstallShortcut, StringComparison.Ordinal);
        Assert.Contains("WorkingDir: \"{app}\"", startMenuShortcut, StringComparison.Ordinal);
        Assert.Contains("AppUserModelID:", startMenuShortcut, StringComparison.Ordinal);
        Assert.Contains("Flags: unchecked", desktopTask, StringComparison.Ordinal);
        Assert.Contains("Flags: unchecked", startupTask, StringComparison.Ordinal);
        Assert.DoesNotContain(script.Lines, line => line.StartsWith("Name: \"startmenuicon\";", StringComparison.Ordinal));
    }

    private static (string Content, string[] Lines) ReadInstallerScript()
    {
        var repositoryRoot = FindRepositoryRoot();
        var content = File.ReadAllText(Path.Combine(repositoryRoot, "installer", "WinBatLens.iss"));
        return (content, content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinBatLens.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
