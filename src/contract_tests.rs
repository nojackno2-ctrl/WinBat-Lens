//! The installer script and the application agree on a handful of names and
//! paths that nothing else checks. These tests pin them so a change on one
//! side cannot silently break upgrades, shortcuts or the clean hand-over from
//! a running instance.

use std::fs;
use std::path::Path;

fn read(relative: &str) -> String {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join(relative);
    fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("cannot read {}: {error}", path.display()))
        .trim_start_matches('\u{feff}')
        .to_string()
}

fn installer_lines() -> Vec<String> {
    read("installer/WinBatLens.iss").lines().filter(|line| !line.is_empty()).map(str::to_string).collect()
}

fn single<'a>(lines: &'a [String], matches: impl Fn(&str) -> bool, what: &str) -> &'a str {
    let found: Vec<&String> = lines.iter().filter(|line| matches(line)).collect();
    assert_eq!(found.len(), 1, "expected exactly one {what}, found {}", found.len());
    found[0]
}

#[test]
fn installer_provides_consistent_upgrade_and_diagnostics_metadata() {
    let lines = installer_lines();

    for directive in [
        "DisableProgramGroupPage=yes",
        "UsePreviousAppDir=yes",
        "UsePreviousGroup=yes",
        "UsePreviousTasks=yes",
        "CreateUninstallRegKey=yes",
        "SetupLogging=yes",
        "UninstallLogging=yes",
        "CloseApplicationsFilter={#MyAppExeName}",
        "RestartApplications=no",
        "AppPublisherURL={#MyAppURL}",
        "AppSupportURL={#MyAppURL}/issues",
        "AppUpdatesURL={#MyAppURL}/releases",
    ] {
        assert!(lines.iter().any(|line| line == directive), "installer is missing `{directive}`");
    }
}

#[test]
fn start_menu_and_optional_shortcut_policy_is_preserved() {
    let lines = installer_lines();

    let start_menu = single(&lines, |l| l.starts_with("Name: \"{group}\\{#MyAppName}\";"), "Start Menu shortcut");
    let uninstall = single(&lines, |l| l.contains("{cm:UninstallProgram,{#MyAppName}}"), "uninstall shortcut");
    let desktop_task = single(&lines, |l| l.starts_with("Name: \"desktopicon\";"), "desktop icon task");
    let startup_task = single(&lines, |l| l.starts_with("Name: \"autostart\";"), "auto-start task");

    // The application and uninstall shortcuts are unconditional.
    assert!(!start_menu.contains("Tasks:"));
    assert!(!uninstall.contains("Tasks:"));
    assert!(start_menu.contains("WorkingDir: \"{app}\""));
    assert!(start_menu.contains("AppUserModelID:"));

    // Desktop icon and Windows startup are opt-in.
    assert!(desktop_task.contains("Flags: unchecked"));
    assert!(startup_task.contains("Flags: unchecked"));
    assert!(!lines.iter().any(|line| line.starts_with("Name: \"startmenuicon\";")));
}

#[test]
fn installer_purges_the_extraction_cache_left_by_dotnet_builds() {
    let script = read("installer/WinBatLens.iss");
    let lines = installer_lines();

    let purge = "Type: filesandordirs; Name: \"{localappdata}\\Temp\\.net\\{#MyAppName}\"";
    assert_eq!(lines.iter().filter(|line| *line == purge).count(), 2, "expected install and uninstall purge entries");
    assert!(script.contains("CleanDotNetTempCache()"));
}

#[test]
fn installer_packages_the_cargo_release_binary() {
    let lines = installer_lines();
    let manifest = read("Cargo.toml");

    // The [[bin]] name decides the file cargo produces; the installer, the
    // Run-key entry and the single-instance lookup all expect this exact name.
    assert!(manifest.contains("name = \"WinBatLens\""), "Cargo.toml must build a binary named WinBatLens");
    assert!(lines.iter().any(|line| line == "#define MyAppExeName \"WinBatLens.exe\""));
    assert!(lines.iter().any(|line| line == "#define MyPublishExe \"..\\target\\release\\WinBatLens.exe\""));
}

#[test]
fn installer_and_app_share_the_single_instance_contract() {
    let lines = installer_lines();
    let app = read("src/single_instance.rs");
    let startup = read("src/startup.rs");

    // The installer signals the exit event and waits on the mutex; a rename on
    // either side would leave setup overwriting a running EXE.
    assert!(lines.iter().any(|line| line == "#define MyAppMutex \"WinBatLens_SingleInstance_Mutex\""));
    assert!(lines.iter().any(|line| line == "#define MyAppExitEvent \"WinBatLens_Exit_Event\""));
    assert!(app.contains("w!(\"WinBatLens_SingleInstance_Mutex\")"));
    assert!(app.contains("w!(\"WinBatLens_Exit_Event\")"));
    assert!(app.contains("w!(\"WinBatLens_Activate_Event\")"));

    // The installer's auto-start task and the in-app checkbox write the same
    // per-user Run value, with the same background flag.
    let run_entry = single(&lines, |l| l.starts_with("Root: HKCU;") && l.contains("CurrentVersion\\Run"), "Run entry");
    assert!(run_entry.contains("ValueName: \"WinBatLens\""));
    assert!(run_entry.contains("--background"));
    assert!(startup.contains("w!(\"WinBatLens\")"));
    assert!(startup.contains("\"--background\""));
}

#[test]
fn release_tooling_reads_the_version_from_cargo() {
    let build_script = read("build-release.ps1");
    let release_workflow = read(".github/workflows/release.yml");

    assert!(build_script.contains("Cargo.toml"));
    assert!(build_script.contains("cargo build --release"));
    assert!(release_workflow.contains("Cargo.toml"));
    for stale in ["WinBatLens.csproj", "dotnet"] {
        assert!(!build_script.contains(stale), "build-release.ps1 still mentions {stale}");
        assert!(!release_workflow.contains(stale), "release.yml still mentions {stale}");
    }
}
