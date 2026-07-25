; *** Inno Setup 6.5.0+ 繁體中文訊息檔 ***
;
; 此檔案為 WinBat Lens 專案自行維護的繁體中文翻譯，
; 訊息鍵值對應 Inno Setup 6 內建的 Default.isl。
;
; 注意：原文結尾沒有句號的訊息，翻譯時也不要加上句號，
; 因為 Inno Setup 會自動附加。

[LangOptions]
LanguageName=<7E41><9AD4><4E2D><6587>
LanguageID=$0404
LanguageCodePage=950

[Messages]

; *** 程式標題
SetupAppTitle=安裝程式
SetupWindowTitle=%1 - 安裝程式
UninstallAppTitle=解除安裝
UninstallAppFullTitle=%1 解除安裝

; *** 一般訊息
InformationTitle=資訊
ConfirmTitle=確認
ErrorTitle=錯誤

; *** SetupLdr 訊息
SetupLdrStartupMessage=即將安裝 %1，是否要繼續？
LdrCannotCreateTemp=無法建立暫存檔案，安裝程式已中止
LdrCannotExecTemp=無法在暫存資料夾中執行檔案，安裝程式已中止
HelpTextNote=

; *** 啟動錯誤訊息
LastErrorMessage=%1。%n%n錯誤 %2：%3
SetupFileMissing=安裝資料夾中缺少檔案 %1。請修正此問題，或重新取得一份新的程式。
SetupFileCorrupt=安裝檔案已損毀，請重新取得一份新的程式。
SetupFileCorruptOrWrongVer=安裝檔案已損毀，或與此版本的安裝程式不相容。請修正此問題，或重新取得一份新的程式。
InvalidParameter=命令列傳入了無效的參數：%n%n%1
SetupAlreadyRunning=安裝程式已在執行中。
WindowsVersionNotSupported=此程式不支援您電腦上執行的 Windows 版本。
WindowsServicePackRequired=此程式需要 %1 Service Pack %2 或更新的版本。
NotOnThisPlatform=此程式無法在 %1 上執行。
OnlyOnThisPlatform=此程式必須在 %1 上執行。
OnlyOnTheseArchitectures=此程式只能安裝在為下列處理器架構所設計的 Windows 版本上：%n%n%1
WinVersionTooLowError=此程式需要 %1 版本 %2 或更新的版本。
WinVersionTooHighError=此程式無法安裝在 %1 版本 %2 或更新的版本上。
AdminPrivilegesRequired=安裝此程式時，您必須以系統管理員身分登入。
PowerUserPrivilegesRequired=安裝此程式時，您必須以系統管理員或 Power Users 群組成員的身分登入。
SetupAppRunningError=安裝程式偵測到 %1 目前正在執行中。%n%n請先關閉所有相關視窗，然後按「確定」繼續，或按「取消」結束安裝。
UninstallAppRunningError=解除安裝程式偵測到 %1 目前正在執行中。%n%n請先關閉所有相關視窗，然後按「確定」繼續，或按「取消」結束解除安裝。

; *** 啟動時的詢問
PrivilegesRequiredOverrideTitle=選擇安裝模式
PrivilegesRequiredOverrideInstruction=請選擇安裝模式
PrivilegesRequiredOverrideText1=%1 可以為所有使用者安裝（需要系統管理員權限），或只為您自己安裝。
PrivilegesRequiredOverrideText2=%1 可以只為您自己安裝，或為所有使用者安裝（需要系統管理員權限）。
PrivilegesRequiredOverrideAllUsers=為所有使用者安裝(&A)
PrivilegesRequiredOverrideAllUsersRecommended=為所有使用者安裝(&A)（建議）
PrivilegesRequiredOverrideCurrentUser=只為我自己安裝(&M)
PrivilegesRequiredOverrideCurrentUserRecommended=只為我自己安裝(&M)（建議）

; *** 其他錯誤
ErrorCreatingDir=安裝程式無法建立資料夾「%1」
ErrorTooManyFilesInDir=無法在資料夾「%1」中建立檔案，因為該資料夾內的檔案過多

; *** 安裝程式共用訊息
ExitSetupTitle=結束安裝程式
ExitSetupMessage=安裝尚未完成。如果您現在結束，將不會安裝此程式。%n%n您可以於稍後再次執行安裝程式以完成安裝。%n%n要結束安裝程式嗎？
AboutSetupMenuItem=關於安裝程式(&A)...
AboutSetupTitle=關於安裝程式
AboutSetupMessage=%1 版本 %2%n%3%n%n%1 官方網站：%n%4
AboutSetupNote=
TranslatorNote=

; *** 按鈕
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安裝(&I)
ButtonOK=確定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonYesToAll=全部皆是(&A)
ButtonNo=否(&N)
ButtonNoToAll=全部皆否(&O)
ButtonFinish=完成(&F)
ButtonBrowse=瀏覽(&B)...
ButtonWizardBrowse=瀏覽(&R)...
ButtonNewFolder=新增資料夾(&M)

; *** 「選擇語言」對話方塊
SelectLanguageTitle=選擇安裝語言
SelectLanguageLabel=請選擇安裝過程中使用的語言。

; *** 精靈共用文字
ClickNext=按「下一步」繼續，或按「取消」結束安裝程式。
BeveledLabel=
BrowseDialogTitle=瀏覽資料夾
BrowseDialogLabel=請在下列清單中選擇一個資料夾，然後按「確定」。
NewFolderName=新資料夾

; *** 「歡迎」頁面
WelcomeLabel1=歡迎使用 [name] 安裝精靈
WelcomeLabel2=即將在您的電腦上安裝 [name/ver]。%n%n建議您在繼續之前先關閉其他所有應用程式。

; *** 「密碼」頁面
WizardPassword=密碼
PasswordLabel1=此安裝程式受密碼保護。
PasswordLabel3=請輸入密碼，然後按「下一步」繼續。密碼區分大小寫。
PasswordEditLabel=密碼(&P)：
IncorrectPassword=您輸入的密碼不正確，請再試一次。

; *** 「授權合約」頁面
WizardLicense=授權合約
LicenseLabel=繼續之前，請先閱讀下列重要資訊。
LicenseLabel3=請閱讀下列授權合約。您必須接受合約條款後才能繼續安裝。
LicenseAccepted=我同意合約條款(&A)
LicenseNotAccepted=我不同意合約條款(&D)

; *** 「資訊」頁面
WizardInfoBefore=資訊
InfoBeforeLabel=繼續之前，請先閱讀下列重要資訊。
InfoBeforeClickLabel=當您準備好繼續安裝時，請按「下一步」。
WizardInfoAfter=資訊
InfoAfterLabel=繼續之前，請先閱讀下列重要資訊。
InfoAfterClickLabel=當您準備好繼續安裝時，請按「下一步」。

; *** 「使用者資訊」頁面
WizardUserInfo=使用者資訊
UserInfoDesc=請輸入您的資訊。
UserInfoName=使用者名稱(&U)：
UserInfoOrg=組織(&O)：
UserInfoSerial=序號(&S)：
UserInfoNameRequired=您必須輸入名稱。

; *** 「選擇安裝位置」頁面
WizardSelectDir=選擇安裝位置
SelectDirDesc=您想將 [name] 安裝在何處？
SelectDirLabel3=安裝程式將把 [name] 安裝至下列資料夾。
SelectDirBrowseLabel=按「下一步」繼續。如果您想選擇其他資料夾，請按「瀏覽」。
DiskSpaceGBLabel=至少需要 [gb] GB 的可用磁碟空間。
DiskSpaceMBLabel=至少需要 [mb] MB 的可用磁碟空間。
CannotInstallToNetworkDrive=安裝程式無法安裝至網路磁碟機。
CannotInstallToUNCPath=安裝程式無法安裝至 UNC 路徑。
InvalidPath=您必須輸入含磁碟機代號的完整路徑，例如：%n%nC:\APP%n%n或下列格式的 UNC 路徑：%n%n\\server\share
InvalidDrive=您所選擇的磁碟機或 UNC 共用不存在或無法存取，請另外選擇。
DiskSpaceWarningTitle=磁碟空間不足
DiskSpaceWarning=安裝程式至少需要 %1 KB 的可用空間，但所選磁碟機只有 %2 KB 可用。%n%n仍要繼續嗎？
DirNameTooLong=資料夾名稱或路徑太長。
InvalidDirName=資料夾名稱無效。
BadDirName32=資料夾名稱不可包含下列任何字元：%n%n%1
DirExistsTitle=資料夾已存在
DirExists=資料夾：%n%n%1%n%n已經存在，仍要安裝至該資料夾嗎？
DirDoesntExistTitle=資料夾不存在
DirDoesntExist=資料夾：%n%n%1%n%n不存在，要建立該資料夾嗎？

; *** 「選擇元件」頁面
WizardSelectComponents=選擇元件
SelectComponentsDesc=要安裝哪些元件？
SelectComponentsLabel2=請勾選您要安裝的元件，並取消勾選不需安裝的元件。準備好後請按「下一步」繼續。
FullInstallation=完整安裝
CompactInstallation=精簡安裝
CustomInstallation=自訂安裝
NoUninstallWarningTitle=元件已存在
NoUninstallWarning=安裝程式偵測到您的電腦上已安裝下列元件：%n%n%1%n%n取消勾選這些元件並不會將其解除安裝。%n%n仍要繼續嗎？
ComponentSize1=%1 KB
ComponentSize2=%1 MB
ComponentsDiskSpaceGBLabel=目前的選擇至少需要 [gb] GB 的磁碟空間。
ComponentsDiskSpaceMBLabel=目前的選擇至少需要 [mb] MB 的磁碟空間。

; *** 「選擇附加工作」頁面
WizardSelectTasks=選擇附加工作
SelectTasksDesc=要執行哪些附加工作？
SelectTasksLabel2=請選擇安裝 [name] 時要一併執行的附加工作，然後按「下一步」。

; *** 「選擇開始功能表資料夾」頁面
WizardSelectProgramGroup=選擇開始功能表資料夾
SelectStartMenuFolderDesc=安裝程式應將程式捷徑放在何處？
SelectStartMenuFolderLabel3=安裝程式將在下列開始功能表資料夾中建立程式捷徑。
SelectStartMenuFolderBrowseLabel=按「下一步」繼續。如果您想選擇其他資料夾，請按「瀏覽」。
MustEnterGroupName=您必須輸入資料夾名稱。
GroupNameTooLong=資料夾名稱或路徑太長。
InvalidGroupName=資料夾名稱無效。
BadGroupName=資料夾名稱不可包含下列任何字元：%n%n%1
NoProgramGroupCheck2=不要建立開始功能表資料夾(&D)

; *** 「準備安裝」頁面
WizardReady=準備安裝
ReadyLabel1=安裝程式已準備好在您的電腦上安裝 [name]。
ReadyLabel2a=按「安裝」開始安裝，若要檢視或變更任何設定請按「上一步」。
ReadyLabel2b=按「安裝」開始安裝。
ReadyMemoUserInfo=使用者資訊：
ReadyMemoDir=安裝位置：
ReadyMemoType=安裝類型：
ReadyMemoComponents=已選擇的元件：
ReadyMemoGroup=開始功能表資料夾：
ReadyMemoTasks=附加工作：

; *** 下載頁面
DownloadingLabel2=正在下載檔案...
ButtonStopDownload=停止下載(&S)
StopDownload=確定要停止下載嗎？
ErrorDownloadAborted=下載已中止
ErrorDownloadFailed=下載失敗：%1 %2
ErrorDownloadSizeFailed=取得檔案大小失敗：%1 %2
ErrorProgress=無效的進度：%2 之 %1
ErrorFileSize=無效的檔案大小：預期為 %1，實際為 %2

; *** 解壓縮頁面
ExtractingLabel=正在解壓縮檔案...
ButtonStopExtraction=停止解壓縮(&S)
StopExtraction=確定要停止解壓縮嗎？
ErrorExtractionAborted=解壓縮已中止
ErrorExtractionFailed=解壓縮失敗：%1

; *** 壓縮檔解壓縮失敗細節
ArchiveIncorrectPassword=密碼不正確
ArchiveIsCorrupted=壓縮檔已損毀
ArchiveUnsupportedFormat=不支援此壓縮檔格式

; *** 「準備安裝中」頁面
WizardPreparing=正在準備安裝
PreparingDesc=安裝程式正在準備於您的電腦上安裝 [name]。
PreviousInstallNotCompleted=先前程式的安裝或移除作業尚未完成，您必須重新啟動電腦才能完成該作業。%n%n重新啟動電腦後，請再次執行安裝程式以完成 [name] 的安裝。
CannotContinue=安裝程式無法繼續，請按「取消」結束。
ApplicationsFound=下列應用程式正在使用安裝程式需要更新的檔案。建議您允許安裝程式自動關閉這些應用程式。
ApplicationsFound2=下列應用程式正在使用安裝程式需要更新的檔案。建議您允許安裝程式自動關閉這些應用程式。安裝完成後，安裝程式將嘗試重新啟動這些應用程式。
CloseApplications=自動關閉這些應用程式(&A)
DontCloseApplications=不要關閉這些應用程式(&D)
ErrorCloseApplications=安裝程式無法自動關閉所有應用程式。建議您在繼續之前，先自行關閉所有正在使用待更新檔案的應用程式。
PrepareToInstallNeedsRestart=安裝程式必須重新啟動您的電腦。重新啟動後，請再次執行安裝程式以完成 [name] 的安裝。%n%n要立即重新啟動嗎？

; *** 「安裝中」頁面
WizardInstalling=正在安裝
InstallingLabel=請稍候，安裝程式正在您的電腦上安裝 [name]。

; *** 「安裝完成」頁面
FinishedHeadingLabel=[name] 安裝精靈完成
FinishedLabelNoIcons=安裝程式已完成 [name] 的安裝。
FinishedLabel=安裝程式已在您的電腦上完成 [name] 的安裝，您可以透過已建立的捷徑啟動此應用程式。
ClickFinish=按「完成」結束安裝程式。
FinishedRestartLabel=為完成 [name] 的安裝，安裝程式必須重新啟動您的電腦。要立即重新啟動嗎？
FinishedRestartMessage=為完成 [name] 的安裝，安裝程式必須重新啟動您的電腦。%n%n要立即重新啟動嗎？
ShowReadmeCheck=是，我想要檢視 README 檔案
YesRadio=是，立即重新啟動電腦(&Y)
NoRadio=否，我稍後再自行重新啟動電腦(&N)
RunEntryExec=執行 %1
RunEntryShellExec=檢視 %1

; *** 「需要下一張磁片」相關
ChangeDiskTitle=安裝程式需要下一張磁片
SelectDiskLabel2=請插入磁片 %1 並按「確定」。%n%n如果此磁片上的檔案位於下列以外的資料夾，請輸入正確路徑或按「瀏覽」。
PathLabel=路徑(&P)：
FileNotInDir2=在「%2」中找不到檔案「%1」，請插入正確的磁片或選擇其他資料夾。
SelectDirectoryLabel=請指定下一張磁片的位置。

; *** 安裝階段訊息
SetupAborted=安裝尚未完成。%n%n請修正問題後再次執行安裝程式。
AbortRetryIgnoreSelectAction=選擇動作
AbortRetryIgnoreRetry=重試(&T)
AbortRetryIgnoreIgnore=忽略錯誤並繼續(&I)
AbortRetryIgnoreCancel=取消安裝
RetryCancelSelectAction=選擇動作
RetryCancelRetry=重試(&T)
RetryCancelCancel=取消

; *** 安裝狀態訊息
StatusClosingApplications=正在關閉應用程式...
StatusCreateDirs=正在建立資料夾...
StatusExtractFiles=正在解壓縮檔案...
StatusDownloadFiles=正在下載檔案...
StatusCreateIcons=正在建立捷徑...
StatusCreateIniEntries=正在建立 INI 項目...
StatusCreateRegistryEntries=正在建立登錄項目...
StatusRegisterFiles=正在註冊檔案...
StatusSavingUninstall=正在儲存解除安裝資訊...
StatusRunProgram=正在完成安裝...
StatusRestartingApplications=正在重新啟動應用程式...
StatusRollback=正在復原變更...

; *** 其他錯誤
ErrorInternal2=內部錯誤：%1
ErrorFunctionFailedNoCode=%1 失敗
ErrorFunctionFailed=%1 失敗；代碼 %2
ErrorFunctionFailedWithMessage=%1 失敗；代碼 %2。%n%3
ErrorExecutingProgram=無法執行檔案：%n%1

; *** 登錄錯誤
ErrorRegOpenKey=開啟登錄機碼時發生錯誤：%n%1\%2
ErrorRegCreateKey=建立登錄機碼時發生錯誤：%n%1\%2
ErrorRegWriteKey=寫入登錄機碼時發生錯誤：%n%1\%2

; *** INI 錯誤
ErrorIniEntry=在檔案「%1」中建立 INI 項目時發生錯誤。

; *** 檔案複製錯誤
FileAbortRetryIgnoreSkipNotRecommended=略過此檔案(&S)（不建議）
FileAbortRetryIgnoreIgnoreNotRecommended=忽略錯誤並繼續(&I)（不建議）
SourceIsCorrupted=來源檔案已損毀
SourceDoesntExist=來源檔案「%1」不存在
SourceVerificationFailed=來源檔案驗證失敗：%1
VerificationSignatureDoesntExist=簽章檔案「%1」不存在
VerificationSignatureInvalid=簽章檔案「%1」無效
VerificationKeyNotFound=簽章檔案「%1」使用了未知的金鑰
VerificationFileNameIncorrect=檔案名稱不正確
VerificationFileTagIncorrect=檔案標籤不正確
VerificationFileSizeIncorrect=檔案大小不正確
VerificationFileHashIncorrect=檔案雜湊值不正確
ExistingFileReadOnly2=無法取代現有檔案，因為該檔案被標記為唯讀。
ExistingFileReadOnlyRetry=移除唯讀屬性並重試(&R)
ExistingFileReadOnlyKeepExisting=保留現有檔案(&K)
ErrorReadingExistingDest=嘗試讀取現有檔案時發生錯誤：
FileExistsSelectAction=選擇動作
FileExists2=檔案已存在。
FileExistsOverwriteExisting=覆寫現有檔案(&O)
FileExistsKeepExisting=保留現有檔案(&K)
FileExistsOverwriteOrKeepAll=後續衝突皆比照處理(&D)
ExistingFileNewerSelectAction=選擇動作
ExistingFileNewer2=現有檔案比安裝程式要安裝的檔案還新。
ExistingFileNewerOverwriteExisting=覆寫現有檔案(&O)
ExistingFileNewerKeepExisting=保留現有檔案(&K)（建議）
ExistingFileNewerOverwriteOrKeepAll=後續衝突皆比照處理(&D)
ErrorChangingAttr=嘗試變更現有檔案的屬性時發生錯誤：
ErrorCreatingTemp=嘗試在目標資料夾中建立檔案時發生錯誤：
ErrorReadingSource=嘗試讀取來源檔案時發生錯誤：
ErrorCopying=嘗試複製檔案時發生錯誤：
ErrorDownloading=嘗試下載檔案時發生錯誤：
ErrorExtracting=嘗試解壓縮檔案時發生錯誤：
ErrorReplacingExistingFile=嘗試取代現有檔案時發生錯誤：
ErrorRestartReplace=RestartReplace 失敗：
ErrorRenamingTemp=嘗試重新命名目標資料夾中的檔案時發生錯誤：
ErrorRegisterServer=無法註冊 DLL/OCX：%1
ErrorRegSvr32Failed=RegSvr32 失敗，結束代碼 %1
ErrorRegisterTypeLib=無法註冊類型庫：%1

; *** 解除安裝顯示名稱標記
UninstallDisplayNameMark=%1 (%2)
UninstallDisplayNameMarks=%1 (%2、%3)
UninstallDisplayNameMark32Bit=32 位元
UninstallDisplayNameMark64Bit=64 位元
UninstallDisplayNameMarkAllUsers=所有使用者
UninstallDisplayNameMarkCurrentUser=目前使用者

; *** 安裝後錯誤
ErrorOpeningReadme=嘗試開啟 README 檔案時發生錯誤。
ErrorRestartingComputer=安裝程式無法重新啟動電腦，請手動重新啟動。

; *** 解除安裝程式訊息
UninstallNotFound=檔案「%1」不存在，無法解除安裝。
UninstallOpenError=無法開啟檔案「%1」，無法解除安裝
UninstallUnsupportedVer=此版本的解除安裝程式無法辨識解除安裝記錄檔「%1」的格式，無法解除安裝
UninstallUnknownEntry=解除安裝記錄檔中出現未知的項目 (%1)
ConfirmUninstall=您確定要完整移除 %1 及其所有元件嗎？
UninstallOnlyOnWin64=此安裝只能在 64 位元的 Windows 上解除安裝。
OnlyAdminCanUninstall=此安裝只能由具有系統管理員權限的使用者解除安裝。
UninstallStatusLabel=請稍候，正在從您的電腦移除 %1。
UninstalledAll=%1 已成功從您的電腦移除。
UninstalledMost=%1 解除安裝完成。%n%n部分項目無法移除，您可以手動將其刪除。
UninstalledAndNeedsRestart=為完成 %1 的解除安裝，必須重新啟動您的電腦。%n%n要立即重新啟動嗎？
UninstallDataCorrupted=檔案「%1」已損毀，無法解除安裝

; *** 解除安裝階段訊息
ConfirmDeleteSharedFileTitle=要移除共用檔案嗎？
ConfirmDeleteSharedFile2=系統顯示下列共用檔案已不再被任何程式使用，您要讓解除安裝程式移除此共用檔案嗎？%n%n如果仍有程式正在使用此檔案而將其移除，這些程式可能無法正常運作。若不確定，請選擇「否」。將此檔案保留在系統中不會造成任何損害。
SharedFileNameLabel=檔案名稱：
SharedFileLocationLabel=位置：
WizardUninstalling=解除安裝狀態
StatusUninstalling=正在解除安裝 %1...

; *** 關機封鎖原因
ShutdownBlockReasonInstallingApp=正在安裝 %1。
ShutdownBlockReasonUninstallingApp=正在解除安裝 %1。

[CustomMessages]

NameAndVersion=%1 版本 %2
AdditionalIcons=附加捷徑：
CreateDesktopIcon=建立桌面捷徑(&D)
CreateQuickLaunchIcon=建立快速啟動列捷徑(&Q)
ProgramOnTheWeb=%1 官方網站
UninstallProgram=解除安裝 %1
LaunchProgram=啟動 %1
AssocFileExtension=將 %1 與 %2 副檔名建立關聯(&A)
AssocingFileExtension=正在將 %1 與 %2 副檔名建立關聯...
AutoStartProgramGroupDescription=啟動：
AutoStartProgram=自動啟動 %1
AddonHostProgramNotFound=在您選擇的資料夾中找不到 %1。%n%n仍要繼續嗎？
