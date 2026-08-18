[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ChangelogPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$Version
)

<#
.SYNOPSIS
    Validates Lumisense changelog.json and renders the newest entry as GitHub Release notes.

.DESCRIPTION
    The script uses the same ordering convention as ChangelogLoader: dated entries are sorted
    chronologically, then undated entries continue in their file order. The final entry in that
    combined sequence becomes the release notes.

    Validation errors stop the workflow before any build, installer or release publishing step.
##>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$allowedTypes = @('added', 'changed', 'fixed', 'removed')
$sectionDefinitions = @(
    [pscustomobject]@{ Type = 'added'; Heading = 'Добавлено' },
    [pscustomobject]@{ Type = 'changed'; Heading = 'Изменено' },
    [pscustomobject]@{ Type = 'fixed'; Heading = 'Исправлено' },
    [pscustomobject]@{ Type = 'removed'; Heading = 'Удалено' }
)

function Test-Property {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return $null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]
}

function Convert-ChangelogDate {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][int]$EntryIndex
    )

    $formats = @('dd.MM.yyyy', 'dd.MM.yy')
    foreach ($format in $formats) {
        $parsed = [datetime]::MinValue
        if ([datetime]::TryParseExact(
                $Text,
                $format,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::None,
                [ref]$parsed)) {
            return $parsed
        }
    }

    throw "Запись changelog №$EntryIndex имеет некорректную дату '$Text'. Используйте dd.MM.yy или dd.MM.yyyy либо оставьте поле date пустым."
}

function Get-TextValue {
    param($Value)

    if ($null -eq $Value) {
        return ''
    }

    return ([string]$Value).Trim()
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Версия '$Version' должна иметь формат SemVer: X.Y.Z или X.Y.Z-prerelease."
}

if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
    throw "Файл changelog не найден: $ChangelogPath"
}

try {
    $rawJson = Get-Content -LiteralPath $ChangelogPath -Raw -Encoding UTF8
    $entries = @($rawJson | ConvertFrom-Json)
}
catch {
    throw "Не удалось прочитать JSON changelog '$ChangelogPath': $($_.Exception.Message)"
}

if ($entries.Count -eq 0) {
    throw 'changelog.json не содержит ни одной записи.'
}

$indexedEntries = @()
for ($index = 0; $index -lt $entries.Count; $index++) {
    $entry = $entries[$index]
    $entryNumber = $index + 1

    if (-not (Test-Property -Object $entry -Name 'changes')) {
        throw "В записи changelog №$entryNumber отсутствует массив changes."
    }

    $changes = @($entry.changes)
    if ($changes.Count -eq 0) {
        throw "Запись changelog №$entryNumber содержит пустой массив changes."
    }

    for ($changeIndex = 0; $changeIndex -lt $changes.Count; $changeIndex++) {
        $change = $changes[$changeIndex]
        $changeNumber = $changeIndex + 1

        if (-not (Test-Property -Object $change -Name 'type')) {
            throw "В записи №$entryNumber, изменении №$changeNumber отсутствует поле type."
        }

        $type = Get-TextValue $change.type
        if ($type -notin $allowedTypes) {
            throw "В записи №$entryNumber, изменении №$changeNumber указан недопустимый type '$type'. Разрешены: $($allowedTypes -join ', ')."
        }

        if (-not (Test-Property -Object $change -Name 'text')) {
            throw "В записи №$entryNumber, изменении №$changeNumber отсутствует поле text."
        }

        if ([string]::IsNullOrWhiteSpace((Get-TextValue $change.text))) {
            throw "В записи №$entryNumber, изменении №$changeNumber поле text не может быть пустым."
        }
    }

    $dateText = if (Test-Property -Object $entry -Name 'date') { Get-TextValue $entry.date } else { '' }
    $sortDate = if ([string]::IsNullOrWhiteSpace($dateText)) {
        $null
    }
    else {
        Convert-ChangelogDate -Text $dateText -EntryIndex $entryNumber
    }

    $indexedEntries += [pscustomobject]@{
        Index = $index
        EntryNumber = $entryNumber
        Entry = $entry
        DateText = $dateText
        SortDate = $sortDate
    }
}

$dated = @($indexedEntries |
    Where-Object { $null -ne $_.SortDate } |
    Sort-Object -Property SortDate, Index)
$undated = @($indexedEntries | Where-Object { $null -eq $_.SortDate })
$latest = @($dated + $undated | Select-Object -Last 1)

if ($latest.Count -ne 1) {
    throw 'Не удалось определить последнюю запись changelog.'
}

$latestEntry = $latest[0]
$notes = @("## Что нового в Lumisense $Version", '')
$sectionCount = 0
$changeCount = 0

foreach ($section in $sectionDefinitions) {
    $sectionChanges = @($latestEntry.Entry.changes | Where-Object { (Get-TextValue $_.type) -eq $section.Type })
    if ($sectionChanges.Count -eq 0) {
        continue
    }

    $sectionCount++
    $notes += "### $($section.Heading)"
    $notes += ''

    foreach ($change in $sectionChanges) {
        $notes += "- $(Get-TextValue $change.text)"
        $changeCount++
    }

    $notes += ''
}

if ($changeCount -eq 0) {
    throw "Последняя запись changelog №$($latestEntry.EntryNumber) не содержит изменений разрешённых типов."
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$markdown = ($notes -join [Environment]::NewLine).TrimEnd() + [Environment]::NewLine
[System.IO.File]::WriteAllText($OutputPath, $markdown, [System.Text.UTF8Encoding]::new($false))

$latestDateDescription = if ([string]::IsNullOrWhiteSpace($latestEntry.DateText)) {
    'без даты'
}
else {
    $latestEntry.DateText
}

$summary = @(
    '### Changelog validated',
    "- Source: $ChangelogPath",
    "- Latest entry: #$($latestEntry.EntryNumber) ($latestDateDescription)",
    "- Release notes: $sectionCount раздела(ов), $changeCount пунктов",
    "- Output: $OutputPath"
) -join [Environment]::NewLine

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding UTF8
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "notes_path=$OutputPath" -Encoding UTF8
}

Write-Host "changelog.json is valid. Latest entry #$($latestEntry.EntryNumber) generated $changeCount release-note items across $sectionCount sections."
