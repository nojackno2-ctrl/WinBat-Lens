; WinBat Lens Inno Setup Script
#define MyAppName "WinBat Lens"
#define MyAppVersion "1.1.1"
#define MyAppPublisher "WinBat Lens Team"
#define MyAppExeName "WinBatLens.exe"
#define MyAppId "{{D2B3F0E1-8E4B-4D2A-9A2C-5F1B3E7A902A}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
LicenseFile=..\LICENSE
SetupIconFile=..\app_icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\dist
OutputBaseFilename=WinBatLens_v{#MyAppVersion}_Setup_x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The Start Menu folder is fixed to DefaultGroupName; whether shortcuts get
; created is controlled by the "startmenuicon" task on the Tasks page instead.
DisableProgramGroupPage=yes
ShowLanguageDialog=yes
PrivilegesRequiredOverridesAllowed=commandline dialog
AppMutex=WinBatLens_SingleInstance_Mutex
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
CreateUninstallRegKey=yes
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
; Traditional Chinese is listed first so it is the default selection.
; ChineseTraditional.isl is maintained in this folder (Inno Setup does not
; ship a Chinese message file).
Name: "chinesetrad"; MessagesFile: "ChineseTraditional.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startmenuicon"; Description: "{cm:CreateStartMenuIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:StartupOptions}"

[Files]
Source: "..\bin\Release\net8.0-windows\win-x64\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[CustomMessages]
chinesetrad.CreateStartMenuIcon=建立開始功能表捷徑
chinesetrad.AutoStartTask=開機時自動於背景啟動 WinBat Lens
chinesetrad.StartupOptions=啟動選項：
english.CreateStartMenuIcon=Create a &Start Menu shortcut
english.AutoStartTask=Automatically start WinBat Lens in the background on Windows startup
english.StartupOptions=Startup options:

[Registry]
; Deliberately HKCU (not HKA/HKLM): the app's own StartupService reads and
; writes this exact per-user Run value, so the in-app "開機自動啟動" checkbox and
; this installer task stay in sync. ISCC emits a UsedUserAreasWarning for this
; in admin install mode; that is expected and harmless when the user elevates
; with their own account, which is the normal UAC-consent path.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WinBatLens"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
