<#
.SYNOPSIS
  Удаляет остатки Lumisense в реестре после удаления программы: ассоциацию Lumisense.AudioFile,
  пункт контекстного меню «Открыть в Lumisense» (LumisenseOpen) и значения «Открыть с помощью».
.DESCRIPTION
  Нужен для копий, удалённых старым установщиком (Inno Setup), который оставлял пустые ключи.
  Записи установщика лежат в HKLM, поэтому для их удаления запустите PowerShell от имени администратора;
  без прав очищается только HKCU.
#>
$extensions = '.mp3', '.wav', '.wma', '.flac', '.m4a', '.aac', '.ogg', '.opus', '.aiff', '.aif'
$progId = 'Lumisense.AudioFile'
$menuKey = 'LumisenseOpen'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$roots = @('HKCU:\Software\Classes')
if ($isAdmin) { $roots += 'HKLM:\Software\Classes' }
else { Write-Warning 'Запущено без прав администратора: записи в HKLM (от Inno-установщика) не будут удалены.' }

foreach ($root in $roots) {
    $paths = @("$root\$progId", "$root\*\shell\$menuKey")
    foreach ($ext in $extensions) {
        $paths += "$root\SystemFileAssociations\$ext\shell\$menuKey"
        $paths += "$root\$ext\shell\$menuKey"
    }
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "Удалено: $path"
        }
    }
    foreach ($ext in $extensions) {
        $openWith = "$root\$ext\OpenWithProgids"
        if (Test-Path -LiteralPath $openWith) {
            Remove-ItemProperty -LiteralPath $openWith -Name $progId -ErrorAction SilentlyContinue
        }
    }
}

# Просим Проводник перечитать ассоциации (SHCNE_ASSOCCHANGED).
Add-Type -Namespace Lumisense -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);'
[Lumisense.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Готово. Если Windows всё ещё предлагает Lumisense по умолчанию, выберите другую программу: Параметры > Приложения > Приложения по умолчанию.'
