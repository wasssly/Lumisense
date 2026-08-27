; ============================================
; Lumisense Audio Player - Установщик
; ============================================

; Версия передаётся снаружи через /DMyAppVersion=X.Y.Z (так и делает workflow
; .github/workflows/release.yml — берёт её из тега релиза, например тег "v1.5.0" → "1.5.0").
; Значение по умолчанию — только для локальной сборки без параметра, чтобы iscc не падал с
; ошибкой "неизвестный символ" при ручном запуске.
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
; Фиксированный AppId — по нему Inno Setup узнаёт "это та же программа" при повторном
; запуске установщика с новой версией и обновляет её на месте (в ту же папку, поверх старых
; файлов), а не ставит рядом вторую копию. Через AppId (а не AppName) — так это работает
; надёжно даже если название программы когда-нибудь сменится. Значение сгенерировано один
; раз и дальше меняться не должно.
AppId={{B7D9F8B4-3E36-4B6C-9B7A-2E9B7B7C0B41}
AppName=Lumisense
AppVersion={#MyAppVersion}
AppPublisher=Lumisense
AppPublisherURL=https://lumisense.ru
AppSupportURL=https://lumisense.ru
AppUpdatesURL=https://lumisense.ru

DefaultDirName={autopf}\Lumisense
DefaultGroupName=Lumisense
AllowNoIcons=yes

; Автообновление из уже запущенного плеера (см. UpdateChecker.LaunchInstallerAndExit в самом
; приложении): плеер сам завершается перед запуском установщика, но CloseApplications здесь —
; страховка на случай, если что-то (например, запуск установщика вручную поверх работающей
; копии) оставило Lumisense.exe висеть в процессах. RestartApplications возвращает его обратно
; после установки, если CloseApplications пришлось его закрыть.
CloseApplications=yes
RestartApplications=yes

; Выходной файл
OutputDir=..\
OutputBaseFilename=Lumisense_Setup

; Сжатие
Compression=lzma2/ultra64
SolidCompression=yes
InternalCompressLevel=ultra64

; Системные требования
MinVersion=0,6.1.7600
PrivilegesRequired=admin

; Иконка
SetupIconFile=..\Lumisense\Icons\app\lumisense.ico
UninstallDisplayIcon={app}\Lumisense.exe

; Внешний вид
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=no

; Языки
LanguageDetectionMethod=uilanguage
; Пользователь всегда видит русский и английский варианты, а не только автоматический выбор по Windows.
ShowLanguageDialog=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

; ============================================
; ФАЙЛЫ ДЛЯ УСТАНОВКИ
; ============================================

[Files]
; Путь — относительно этого .iss-файла (папка Installer), к стандартной выходной папке
; "dotnet publish -c Release -r win-x64 --self-contained true" для проекта Lumisense (см.
; TargetFramework/RuntimeIdentifier в Lumisense.csproj). Раньше здесь был захардкожен
; конкретный путь на диске одного компьютера ("C:\Users\Administrator\..."), из-за чего
; сборка ломалась на любой другой машине, включая CI (см. .github/workflows/release.yml).
Source: "..\Lumisense\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Иконка отдельно (если не попала в publish)
Source: "..\Lumisense\Icons\app\lumisense.ico"; DestDir: "{app}"; Flags: ignoreversion

; ============================================
; ДОПОЛНИТЕЛЬНЫЕ ЗАДАЧИ (флажки на странице мастера)
; ============================================

[Tasks]
; Флажок на отдельной странице мастера ("Выберите дополнительные задачи") — отмечен по
; умолчанию (Flags: unchecked отсутствует), но пользователь может снять галочку и не получить
; ярлык на рабочем столе. Сам ярлык в [Icons] ниже ставится только если эта задача выбрана
; (Tasks: desktopicon).
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

; ============================================
; ЯРЛЫКИ
; ============================================

[Icons]
Name: "{group}\Lumisense"; Filename: "{app}\Lumisense.exe"; WorkingDir: "{app}"; IconFilename: "{app}\lumisense.ico"
Name: "{commondesktop}\Lumisense"; Filename: "{app}\Lumisense.exe"; WorkingDir: "{app}"; IconFilename: "{app}\lumisense.ico"; Tasks: desktopicon
Name: "{group}\{cm:UninstallLumisense}"; Filename: "{uninstallexe}"

; ============================================
; АССОЦИАЦИЯ ФАЙЛОВ
; ============================================

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
Root: HKCR; Subkey: "*\shell\LumisenseOpen"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInLumisense}"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "*\shell\LumisenseOpen\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue

; ============================================
; ЗАПУСК ПОСЛЕ УСТАНОВКИ
; ============================================

[Run]
Filename: "{app}\Lumisense.exe"; Description: "{cm:LaunchLumisense}"; Flags: postinstall nowait skipifsilent

; ============================================
; УДАЛЕНИЕ
; ============================================

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; ============================================
; УДАЛЕНИЕ ДАННЫХ НАСТРОЕК (%AppData%\Lumisense)
; ============================================

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
// [UninstallDelete] выше уже безусловно удаляет {app} (саму программу в Program Files) —
// это просто файлы плеера, отслеживаемые самим Inno Setup, спрашивать тут нечего.
//
// А вот %AppData%\Lumisense (settings.json — настройки, плейлисты, избранное, все
// пользовательские данные, см. SettingsManager в AppSettings.cs) Inno Setup сам по себе
// никогда не трогает: не он их туда клал, это делает уже сам плеер во время работы,
// и его штатный механизм удаления файлов про эту папку просто не знает.
//
// Спрашиваем явно, до начала удаления (InitializeUninstall — самая ранняя точка, до которой
// процесс ещё можно отменить), а не удаляем её тихо и безусловно: пользователь может
// деинсталлировать плеер, чтобы переустановить его заново (например, при обновлении вручную
// или переносе на другой диск), и в этом случае удалять его настройки, плейлисты и избранное
// заодно — было бы неожиданным и необратимым сюрпризом.
var
  ShouldDeleteSettings: Boolean;

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