; WinBat Lens Inno Setup Script
#define MyAppName "WinBat Lens"
#define MyAppPublisher "WinBat Lens Team"
#define MyAppExeName "WinBatLens.exe"
#define MyAppId "{{D2B3F0E1-8E4B-4D2A-9A2C-5F1B3E7A902A}"
#define MyPublishExe "..\bin\Release\net10.0-windows\win-x64\publish\WinBatLens.exe"

; Named kernel objects the running app owns. Both are part of a cross-version
; contract with Services/SingleInstanceService.cs and must never carry the
; version number: this installer has to recognise and stop an OLD build.
#define MyAppMutex "WinBatLens_SingleInstance_Mutex"
#define MyAppExitEvent "WinBatLens_Exit_Event"

; The release number lives in WinBatLens.csproj and nowhere else.
; build-release.ps1 reads it from there and passes it in as /DMyAppVersion=.
; The fallback for a hand-run ISCC reads it back off the published EXE, which
; [Files] requires to exist anyway — so the number stamped on the installer can
; never disagree with the binary inside it.
#ifndef MyAppVersion
  #define ExeVersion GetVersionNumbersString(MyPublishExe)
  #define MyAppVersion Copy(ExeVersion, 1, RPos(".", ExeVersion) - 1)
#endif

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
AppMutex={#MyAppMutex}
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
Source: "{#MyPublishExe}"; DestDir: "{app}"; Flags: ignoreversion
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
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WinBatLens"; ValueData: """{app}\{#MyAppExeName}"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EVENT_MODIFY_STATE = $0002;

{ Declared with LongWord rather than Boolean/THandle so nothing depends on how
  Pascal Script marshals a one-byte Boolean onto a four-byte Win32 BOOL. }
function OpenEvent(dwDesiredAccess: LongWord; bInheritHandle: LongWord; lpName: String): LongWord;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: LongWord): LongWord;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: LongWord): LongWord;
  external 'CloseHandle@kernel32.dll stdcall';

{ Asks a running WinBat Lens to shut itself down before we touch its files.

  Neither of the stock mechanisms can do this on their own, because the app
  deliberately refuses WM_CLOSE and hides to the tray instead (see
  MainWindow_Closing): the AppMutex prompt asks the user to close the app, but
  clicking the window's X only hides it and the mutex stays held; and the
  Restart Manager behind CloseApplications gets its graceful close refused, so
  it resorts to terminating the process, which strands the notification-area
  icon until the user happens to hover over it.

  Builds from v1.1.3 on listen on a named event for exactly this request and
  exit cleanly, tray icon included. Earlier builds have no listener, so
  OpenEvent fails and we leave them to the AppMutex prompt, unchanged. }
procedure StopRunningApp();
var
  EventHandle: LongWord;
  Ignored: LongWord;
  I: Integer;
begin
  if not CheckForMutexes('{#MyAppMutex}') then
    Exit;

  EventHandle := OpenEvent(EVENT_MODIFY_STATE, 0, '{#MyAppExitEvent}');
  if EventHandle = 0 then
    Exit;

  Ignored := SetEvent(EventHandle);
  Ignored := CloseHandle(EventHandle);

  { The app drops the mutex on its way out, so wait on that rather than on a
    fixed delay. Measured shutdown is under 100 ms; five seconds is headroom
    for a machine under load, not an expected wait. }
  for I := 1 to 50 do
  begin
    if not CheckForMutexes('{#MyAppMutex}') then
      Break;
    Sleep(100);
  end;
end;

function InitializeSetup(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Last chance before files are overwritten: the user can have launched the
    app from its tray icon while the wizard was still open. }
  StopRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;
