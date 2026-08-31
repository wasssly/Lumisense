[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Container })]
    [string]$BasePublishDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Container })]
    [string]$CandidatePublishDirectory,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$')]
    [string]$BaseVersion,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$')]
    [string]$CandidateVersion,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\VelopackPrototype\Releases')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($BaseVersion -eq $CandidateVersion) {
    throw 'BaseVersion and CandidateVersion must differ.'
}

$mainExe = Join-Path $CandidatePublishDirectory 'Lumisense.exe'
if (-not (Test-Path $mainExe -PathType Leaf)) {
    throw "Candidate publish directory does not contain Lumisense.exe: $CandidatePublishDirectory"
}

$baseExe = Join-Path $BasePublishDirectory 'Lumisense.exe'
if (-not (Test-Path $baseExe -PathType Leaf)) {
    throw "Baseline publish directory does not contain Lumisense.exe: $BasePublishDirectory"
}

$toolPath = Join-Path $env:USERPROFILE '.dotnet\tools'
if ($env:Path -notlike "*$toolPath*") {
    $env:Path = "$toolPath;$env:Path"
}

$existing = Get-Command vpk -ErrorAction SilentlyContinue
if ($null -eq $existing) {
    dotnet tool install --global vpk --version 1.2.0
}

# У vpk 1.2.0 нет команды --version; факт наличия CLI уже проверен выше.
Write-Host 'Using Velopack CLI vpk 1.2.0'

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Базовая версия может быть собрана до добавления VelopackApp.Build().Run(), поэтому для
# измерения разрешён только здесь --skipVeloAppCheck. Candidate проверяется без обхода.
& vpk pack `
    --packId Wasssly.Lumisense `
    --packVersion $BaseVersion `
    --packDir $BasePublishDirectory `
    --mainExe Lumisense.exe `
    --runtime win-x64 `
    --outputDir $OutputDirectory `
    --channel prototype `
    --noInst true `
    --skipVeloAppCheck true
if ($LASTEXITCODE -ne 0) { throw 'Baseline package creation failed.' }

& vpk pack `
    --packId Wasssly.Lumisense `
    --packVersion $CandidateVersion `
    --packDir $CandidatePublishDirectory `
    --mainExe Lumisense.exe `
    --runtime win-x64 `
    --outputDir $OutputDirectory `
    --channel prototype `
    --packTitle Lumisense `
    --packAuthors wasssly `
    --shortcuts StartMenuRoot `
    --icon (Join-Path $PSScriptRoot '..\Lumisense\Icons\app\lumisense.ico') `
    --noPortable true `
    --msi true `
    --instLocation PerMachine
if ($LASTEXITCODE -ne 0) { throw 'Candidate package creation failed.' }

# Velopack включает выбранный канал в имена артефактов: <id>-<version>-prototype-<kind>.nupkg.
# Используем одно имя канала в pack и здесь, чтобы поиск не зависел от случайного порядка файлов.
$channel = 'prototype'
$base = Get-ChildItem $OutputDirectory -Filter "Wasssly.Lumisense-$BaseVersion-$channel-full.nupkg" | Select-Object -First 1
$full = Get-ChildItem $OutputDirectory -Filter "Wasssly.Lumisense-$CandidateVersion-$channel-full.nupkg" | Select-Object -First 1
$delta = Get-ChildItem $OutputDirectory -Filter "Wasssly.Lumisense-$CandidateVersion-$channel-delta.nupkg" | Select-Object -First 1
$msi = Get-ChildItem $OutputDirectory -Filter '*.msi' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1

if ($null -eq $base -or $null -eq $full -or $null -eq $delta -or $null -eq $msi) {
    Get-ChildItem $OutputDirectory | Format-Table Name, Length
    throw 'Expected baseline full package, candidate full package, delta package and MSI were not all created.'
}

$reconstructed = Join-Path $OutputDirectory "reconstructed-$CandidateVersion.nupkg"
& vpk delta patch --base $base.FullName --patch $delta.FullName --output $reconstructed
if ($LASTEXITCODE -ne 0) { throw 'Delta reconstruction failed.' }

$fullHash = (Get-FileHash $full.FullName -Algorithm SHA256).Hash
$reconstructedHash = (Get-FileHash $reconstructed -Algorithm SHA256).Hash
if ($fullHash -ne $reconstructedHash) {
    throw 'The SHA-256 of the reconstructed package differs from the candidate full package.'
}

$measurement = [ordered]@{
    baseVersion = $BaseVersion
    candidateVersion = $CandidateVersion
    fullPackage = [ordered]@{ name = $full.Name; bytes = $full.Length; sha256 = $fullHash }
    deltaPackage = [ordered]@{ name = $delta.Name; bytes = $delta.Length; sha256 = (Get-FileHash $delta.FullName -Algorithm SHA256).Hash; percentOfFull = [math]::Round(($delta.Length / $full.Length) * 100, 2) }
    msi = [ordered]@{ name = $msi.Name; bytes = $msi.Length; sha256 = (Get-FileHash $msi.FullName -Algorithm SHA256).Hash }
    reconstructedFullSha256 = $reconstructedHash
}

$measurementPath = Join-Path $OutputDirectory 'delta-measurement.json'
$measurement | ConvertTo-Json -Depth 5 | Set-Content -Path $measurementPath -Encoding utf8
$measurement | ConvertTo-Json -Depth 5
Write-Host "SUCCESS: full and delta packages were created and reconstructed package SHA-256 matches."
