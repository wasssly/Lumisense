
; Версия приходит через /DMyAppVersion (из тега релиза в release.yml); значение по умолчанию — для локальной
; сборки без параметра, чтобы iscc не падал на неизвестном символе.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
; Фиксированный AppId: по нему Inno Setup обновляет установку на месте, а не ставит вторую копию,
; и это не зависит от смены AppName; значение сгенерировано один раз и меняться не должно.
AppId={{B7D9F8B4-3E36-4B6C-9B7A-2E9B7B7C0B41}
AppName=Lumisense
AppVersion={#MyAppVersion}
AppPublisher=Lumisense

DefaultDirName={autopf}\Lumisense
DefaultGroupName=Lumisense
AllowNoIcons=yes

; Автообновление завершает плеер само (UpdateChecker.LaunchInstallerAndExit); CloseApplications — страховка на
; случай зависшего Lumisense.exe, RestartApplications возвращает его после установки.
CloseApplications=yes
RestartApplications=yes

OutputDir=..\
OutputBaseFilename=Lumisense_Setup

Compression=lzma2/ultra64
SolidCompression=yes
InternalCompressLevel=ultra64

MinVersion=0,6.1.7600
PrivilegesRequired=admin

SetupIconFile=..\Lumisense\Icons\app\lumisense.ico
UninstallDisplayIcon={app}\Lumisense.exe

WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=no

LanguageDetectionMethod=uilanguage
; Пользователь всегда видит русский и английский варианты, а не только автоматический выбор по Windows.
ShowLanguageDialog=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Путь — от папки Installer к выходу "dotnet publish -c Release -r win-x64 --self-contained true"; абсолютный
; путь ломал сборку на других машинах и в CI (release.yml).
Source: "..\Lumisense\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Иконка отдельно (если не попала в publish)
Source: "..\Lumisense\Icons\app\lumisense.ico"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
; Флажок отмечен по умолчанию, но пользователь может его снять; ярлык в [Icons]
; ставится только при выбранной задаче desktopicon.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{group}\Lumisense"; Filename: "{app}\Lumisense.exe"; WorkingDir: "{app}"; IconFilename: "{app}\lumisense.ico"
Name: "{commondesktop}\Lumisense"; Filename: "{app}\Lumisense.exe"; WorkingDir: "{app}"; IconFilename: "{app}\lumisense.ico"; Tasks: desktopicon
Name: "{group}\{cm:UninstallLumisense}"; Filename: "{uninstallexe}"

[Registry]
Root: HKCR; Subkey: ".mp3"; ValueType: string; ValueName: ""; ValueData: "Lumisense.AudioFile"; Flags: uninsdeletevalue
Root: HKCR; Subkey: ".wav"; ValueType: string; ValueName: ""; ValueData: "Lumisense.AudioFile"; Flags: uninsdeletevalue
Root: HKCR; Subkey: ".flac"; ValueType: string; ValueName: ""; ValueData: "Lumisense.AudioFile"; Flags: uninsdeletevalue
Root: HKCR; Subkey: ".m4a"; ValueType: string; ValueName: ""; ValueData: "Lumisense.AudioFile"; Flags: uninsdeletevalue
Root: HKCR; Subkey: ".aac"; ValueType: string; ValueName: ""; ValueData: "Lumisense.AudioFile"; Flags: uninsdeletevalue
Root: HKCR; Subkey: ".ogg"; ValueType: string; ValueName: ""; ValueData: "Lumisense.AudioFile"; Flags: uninsdeletevalue
Root: HKCR; Subkey: ".wma"; ValueType: string; ValueName: ""; ValueData: "Lumisense.AudioFile"; Flags: uninsdeletevalue

Root: HKCR; Subkey: "Lumisense.AudioFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Lumisense.exe,0"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "Lumisense.AudioFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue
; Пункт показывается только для поддерживаемых аудиофайлов. Wildcard (*) здесь намеренно
; не используется: он добавлял «Открыть в Lumisense» к текстовым и любым другим файлам.
Root: HKCR; Subkey: "SystemFileAssociations\.mp3\shell\LumisenseOpen"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInLumisense}"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.mp3\shell\LumisenseOpen\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.wav\shell\LumisenseOpen"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInLumisense}"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.wav\shell\LumisenseOpen\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.flac\shell\LumisenseOpen"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInLumisense}"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.flac\shell\LumisenseOpen\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.m4a\shell\LumisenseOpen"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInLumisense}"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.m4a\shell\LumisenseOpen\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.aac\shell\LumisenseOpen"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInLumisense}"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.aac\shell\LumisenseOpen\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.ogg\shell\LumisenseOpen"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInLumisense}"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.ogg\shell\LumisenseOpen\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.wma\shell\LumisenseOpen"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInLumisense}"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "SystemFileAssociations\.wma\shell\LumisenseOpen\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Lumisense.exe"; Description: "{cm:LaunchLumisense}"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[CustomMessages]
english.CreateDesktopIcon=Create a desktop shortcut
english.AdditionalIcons=Additional shortcuts:
english.UninstallLumisense=Uninstall Lumisense
english.OpenInLumisense=Open in Lumisense
english.LaunchLumisense=Launch Lumisense
russian.CreateDesktopIcon=Создать значок на рабочем столе
russian.AdditionalIcons=Дополнительные значки:
russian.UninstallLumisense=Удалить Lumisense
russian.OpenInLumisense=Открыть в Lumisense
russian.LaunchLumisense=Запустить Lumisense

[Code]
// %AppData%\Lumisense (настройки, плейлисты, избранное) Inno Setup сам не удаляет — файлы туда кладёт плеер.
// Спрашиваем в InitializeUninstall (последняя точка отмены), а не удаляем молча: при переустановке это была бы потеря.
var
  ShouldDeleteSettings: Boolean;

procedure RemoveLegacyWildcardContextMenu;
begin
  // До этой версии пункт регистрировался в *\shell и показывался для любого файла; старый ключ удаляем при
  // установке/обновлении, иначе он остаётся в реестре после перехода на SystemFileAssociations.<extension>.
  RegDeleteKeyIncludingSubkeys(HKEY_CLASSES_ROOT, '*\\shell\\LumisenseOpen');
end;

function InitializeSetup(): Boolean;
begin
  RemoveLegacyWildcardContextMenu;
  Result := True;
end;

function InstallerLanguageCode(): String;

begin
  if ActiveLanguage = 'english' then
    Result := 'en'
  else
    Result := 'ru';
end;

function DeleteUserDataPrompt(): String;
begin
  if ActiveLanguage = 'english' then
    Result := 'Also delete Lumisense settings and user data?' + #13#10 + #13#10 +
      'They are stored separately from the program in:' + #13#10 +
      ExpandConstant('{userappdata}') + '\Lumisense' + #13#10 + #13#10 +
      'Click "No" if you plan to reinstall Lumisense later and want to keep your current settings, playlists, and favorites.'
  else
    Result := 'Удалить также файлы настроек и пользовательские данные Lumisense?' + #13#10 + #13#10 +
      'Они хранятся отдельно от программы, в папке:' + #13#10 +
      ExpandConstant('{userappdata}') + '\Lumisense' + #13#10 + #13#10 +
      'Нажмите "Нет", если планируете переустановить Lumisense позже и хотите сохранить ' +
      'текущие настройки, плейлисты и избранное.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ForceDirectories(ExpandConstant('{userappdata}\Lumisense'));
    SaveStringToFile(ExpandConstant('{userappdata}\Lumisense\installer-language.txt'), InstallerLanguageCode(), False);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  // «Нет» (сохранить общие данные MSI/EXE) — безопасный выбор по умолчанию. Пользователь
  // всё ещё может осознанно выбрать «Да» при окончательном удалении Lumisense.
  ShouldDeleteSettings := (MsgBox(DeleteUserDataPrompt(), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and ShouldDeleteSettings then
    DelTree(ExpandConstant('{userappdata}\Lumisense'), True, True, True);
end;