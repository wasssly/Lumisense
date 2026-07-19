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
ShowLanguageDialog=auto

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
Source: "..\Lumisense\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Иконка отдельно (если не попала в publish)
Source: "..\Lumisense\Icons\app\lumisense.ico"; DestDir: "{app}"; Flags: ignoreversion

; ============================================
; ЯРЛЫКИ
; ============================================

[Icons]
Name: "{group}\Lumisense"; Filename: "{app}\Lumisense.exe"; WorkingDir: "{app}"; IconFilename: "{app}\lumisense.ico"
Name: "{commondesktop}\Lumisense"; Filename: "{app}\Lumisense.exe"; WorkingDir: "{app}"; IconFilename: "{app}\lumisense.ico"
Name: "{group}\Удалить Lumisense"; Filename: "{uninstallexe}"

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
Root: HKCR; Subkey: "*\shell\LumisenseOpen"; ValueType: string; ValueName: ""; ValueData: "Открыть в Lumisense"; Flags: uninsdeletevalue
Root: HKCR; Subkey: "*\shell\LumisenseOpen\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Lumisense.exe"" ""%1"""; Flags: uninsdeletevalue

; ============================================
; ЗАПУСК ПОСЛЕ УСТАНОВКИ
; ============================================

[Run]
Filename: "{app}\Lumisense.exe"; Description: "Запустить Lumisense"; Flags: postinstall nowait skipifsilent

; ============================================
; УДАЛЕНИЕ
; ============================================

[UninstallDelete]
Type: filesandordirs; Name: "{app}"