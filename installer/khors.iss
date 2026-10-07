; Установщик KHORS Desktop (ROADMAP 5.1, docs/SPEC.md, раздел 2). Inno Setup 6 (лицензия Inno Setup — NOTICE).
; Собирается скриптом tools/installer/build.ps1: он публикует приложение и службу в каталог Staging и передаёт
; версию из Directory.Build.props. Вручную: ISCC.exe /DAppVersion=1.0.0 /DStaging=<каталог> installer\khors.iss
;
; Что делает: копирует окно, службу и ядра в %ProgramFiles%\KHORS; по задаче «служба» (и всегда при обновлении,
; если служба уже стоит) запускает «khors-service.exe install» — служба копирует себя и ядра в
; %ProgramFiles%\KHORS\service и регистрируется оттуда. Удаление: «khors-service.exe uninstall» (служба, фильтры WFP,
; адаптер KHORS), возврат системного прокси пользователя, по желанию — данные %APPDATA%\KHORS (docs/SPEC.md, 7.6).

#ifndef AppVersion
  #error Передайте версию: /DAppVersion=1.0.0
#endif
#ifndef Staging
  #error Передайте каталог с файлами: /DStaging=<каталог>
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "KHORS Desktop"
#define AppExe "khors-desktop.exe"
#define ServiceExe "khors-service.exe"

[Setup]
; Постоянный идентификатор: по нему обновление находит прежнюю установку. Не менять.
AppId={{6F1C2B7E-4A3D-4E59-9B0C-8D2E7A5F1C34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=KHORS contributors
AppCopyright=Copyright 2026 KHORS contributors
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\KHORS
DefaultGroupName=KHORS
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Открытое окно KHORS (мьютекс единственного экземпляра, WindowsSingleInstance) — установщик просит его закрыть.
AppMutex=Local\KHORS-Desktop
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\src\Khors.App\Assets\khors.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=khors-desktop-{#AppVersion}-win-x64-setup
ShowLanguageDialog=auto

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.TaskService=Установить службу KHORS (нужна для режима TUN и Kill Switch)
en.TaskService=Install the KHORS service (required for TUN mode and Kill Switch)
ru.ServiceInstalling=Установка службы KHORS…
en.ServiceInstalling=Installing the KHORS service…
ru.ServiceFailed=Не удалось установить службу KHORS (код %1). Режим «Системный прокси» работает и без неё; службу можно установить позже в «Настройки → Служба KHORS».
en.ServiceFailed=Failed to install the KHORS service (code %1). System proxy mode works without it; the service can be installed later in Settings → KHORS service.
ru.ServiceRemoving=Удаление службы KHORS, фильтров и адаптера…
en.ServiceRemoving=Removing the KHORS service, filters and adapter…
ru.DeleteUserData=Удалить также профили и настройки KHORS (%1)?%n%nЕсли вы собираетесь установить KHORS снова, выберите «Нет».
en.DeleteUserData=Also delete KHORS profiles and settings (%1)?%n%nChoose No if you plan to install KHORS again.
ru.LaunchApp=Запустить KHORS
en.LaunchApp=Launch KHORS

[Tasks]
Name: "service"; Description: "{cm:TaskService}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Staging}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Staging}\{#ServiceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Staging}\cores\*"; DestDir: "{app}\cores"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Staging}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Staging}\NOTICE"; DestDir: "{app}"; Flags: ignoreversion

; Ядра прежней версии, которых нет в новой, не должны оставаться.
[InstallDelete]
Type: filesandordirs; Name: "{app}\cores"

[Icons]
Name: "{autoprograms}\KHORS"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\KHORS"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Служба, фильтры WFP (в том числе «Постоянно») и адаптер KHORS. Каталог %ProgramFiles%\KHORS\service служба удаляет сама.
Filename: "{app}\{#ServiceExe}"; Parameters: "uninstall"; Flags: runhidden waituntilterminated; StatusMsg: "{cm:ServiceRemoving}"; RunOnceId: "RemoveService"
; Системный прокси: окно возвращает его при выходе; если KHORS был убит, а сторож не успел — возвращаем здесь.
Filename: "{app}\{#AppExe}"; Parameters: "--restore-proxy"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreProxy"

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
const
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\KhorsService';

function ServiceInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, ServiceKey);
end;

// Служба ставится по задаче, а уже установленная — обновляется всегда (иначе осталась бы служба старой версии
// с другим протоколом IPC).
procedure InstallService;
var
  ResultCode: Integer;
begin
  if not (WizardIsTaskSelected('service') or ServiceInstalled) then
    Exit;

  WizardForm.StatusLabel.Caption := CustomMessage('ServiceInstalling');
  if not Exec(ExpandConstant('{app}\{#ServiceExe}'), 'install', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)
    or (ResultCode <> 0) then
    SuppressibleMsgBox(FmtMessage(CustomMessage('ServiceFailed'), [IntToStr(ResultCode)]), mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallService;
end;

// Обновление: задача «служба» отмечена, если служба уже стоит; при первой установке — по умолчанию тоже.
procedure InitializeWizard;
begin
  if ServiceInstalled then
    WizardSelectTasks('service');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  DataDir := ExpandConstant('{userappdata}\KHORS');
  if DirExists(DataDir) and not UninstallSilent then
    if MsgBox(FmtMessage(CustomMessage('DeleteUserData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(DataDir, True, True, True);
end;
