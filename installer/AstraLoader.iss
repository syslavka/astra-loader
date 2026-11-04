#ifndef PayloadDir
  #define PayloadDir "..\publish"
#endif
#ifndef OutputRoot
  #define OutputRoot "..\dist"
#endif
#define AppVersion "3.3.1"
#define AppExe "AstraLoader.exe"
#define AppMutexes "Local\AstraLoader-Menu,Global\NL-Cleaner-Operation,Local\NL-Cleaner-Preview,Local\AstraLoader-RecoveryPreview"

[Setup]
AppId={{F8AD48ED-235A-4A39-B5F9-306BE9944898}
AppName=Astra Loader
AppVersion={#AppVersion}
AppVerName=Astra Loader {#AppVersion}
AppPublisher=Astra Loader
AppComments=Удаление Roblox и управление блокировками установки
VersionInfoDescription=Astra Loader Setup
VersionInfoVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Astra Loader
DefaultGroupName=Astra Loader
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
WizardStyle=modern dark polar
WizardSizePercent=110
SetupIconFile=..\Assets\AstraLoader.ico
WizardSmallImageFile=..\Assets\AppIcon.png
WizardImageFile=..\Assets\WizardBanner.png
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Astra Loader
OutputDir={#OutputRoot}
OutputBaseFilename=AstraLoader-Setup
Compression=lzma2
SolidCompression=yes
AppMutex={#AppMutexes}
SetupMutex=AstraLoader.Setup
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
Uninstallable=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"


[Messages]
WelcomeLabel1=Мастер установки Astra Loader
WelcomeLabel2=Приложение для удаления Roblox и управления блокировками установки.%n%nУстановка не запускает очистку. Операции выполняются после явного выбора в приложении.%n%nНажмите «Далее», чтобы продолжить.

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"
Name: "startupmenu"; Description: "Открывать меню при входе в Windows (без очистки)"; GroupDescription: "Автозапуск:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.pfx,*.cer,sign.ps1,certificates\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Astra Loader"; Filename: "{app}\{#AppExe}"; Parameters: "--manual"; WorkingDir: "{app}"; AppUserModelID: "AstraLoader.Desktop"
Name: "{autoprograms}\Astra Loader — проверка без изменений"; Filename: "{app}\{#AppExe}"; Parameters: "--dry-run"; WorkingDir: "{app}"; AppUserModelID: "AstraLoader.Desktop"
Name: "{autodesktop}\Astra Loader"; Filename: "{app}\{#AppExe}"; Parameters: "--manual"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "AstraLoader.Desktop"

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--manual"; Description: "Открыть Astra Loader"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure InitializeWizard;
begin
  if RegValueExists(HKCU, RunKey, 'Astra Loader') or RegValueExists(HKCU, RunKey, 'NL-Cleaner') then
    WizardSelectTasks('desktopicon,startupmenu');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Command: String;
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('startupmenu') then
      Command := '--startup-enable'
    else
      Command := '--startup-disable';
    if not Exec(ExpandConstant('{app}\{#AppExe}'), Command, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      MsgBox('Программа установлена, но настройку автозапуска выполнить не удалось. Настройте её в окне приложения.', mbInformation, MB_OK)
    else if ExitCode <> 0 then
      MsgBox('Программа установлена, но автозапуск не изменён. Настройте его в окне приложения.', mbInformation, MB_OK);
  end;
end;

function InitializeUninstall: Boolean;
var
  Choice, ExitCode: Integer;
  AppPath, StartupCommand: String;
  RestoreBlocks: Boolean;
begin
  Result := False;
  if CheckForMutexes('{#AppMutexes}') then
  begin
    MsgBox('Сначала закройте Astra Loader и дождитесь завершения его операций, затем повторите удаление.', mbInformation, MB_OK);
    Exit;
  end;
  RestoreBlocks := True;
  if not UninstallSilent then
  begin
    Choice := MsgBox('Снять блокировки Roblox, созданные приложением, перед удалением?' + #13#10 + #13#10 +
      'Да — восстановить доступ, затем удалить программу.' + #13#10 +
      'Нет — удалить только программу; блокировки и данные восстановления останутся.' + #13#10 +
      'Отмена — ничего не удалять.' + #13#10 + #13#10 +
      'Удалённые ранее файлы не восстанавливаются.', mbConfirmation, MB_YESNOCANCEL or MB_DEFBUTTON1);
    if Choice = IDCANCEL then Exit;
    RestoreBlocks := Choice = IDYES;
  end;
  AppPath := ExpandConstant('{app}\{#AppExe}');
  if RestoreBlocks then
  begin
    if not FileExists(AppPath) then
    begin
      MsgBox('Файл приложения отсутствует. Переустановите Astra Loader, восстановите доступ и повторите удаление. Данные восстановления сохранены.', mbError, MB_OK);
      Exit;
    end;
    if not Exec(AppPath, '--restore-and-exit', ExpandConstant('{app}'), SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
    begin
      MsgBox('Не удалось запустить восстановление. Удаление отменено.', mbError, MB_OK);
      Exit;
    end;
    if ExitCode <> 0 then
    begin
      MsgBox('Восстановление не завершено или запрос администратора отменён. Удаление отменено. Приложение и данные восстановления сохранены.', mbError, MB_OK);
      Exit;
    end;
  end;
  if RegQueryStringValue(HKCU, RunKey, 'Astra Loader', StartupCommand) then
    if CompareText(StartupCommand, '"' + AppPath + '" --manual') = 0 then
      if not RegDeleteValue(HKCU, RunKey, 'Astra Loader') then
      begin
        MsgBox('Не удалось удалить запись автозапуска. Удаление приложения отменено.', mbError, MB_OK);
        Exit;
      end;
  Result := True;
end;
