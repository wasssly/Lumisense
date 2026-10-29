<#
.SYNOPSIS
Creates a large test music folder from one audio file, for the "10 000 tracks" performance scenarios.

.DESCRIPTION
By default every track is a full copy of the source file. With -HardLink the tracks are NTFS hard links: they take no extra
disk space and are created much faster (the folder must be on the same NTFS volume as the source file).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path $_ -PathType Leaf })]
    [string]$SourceFile,

    [Parameter(Mandatory = $true)]
    [string]$Destination,

    [ValidateRange(1, 200000)]
    [int]$Count = 10000,

    [switch]$HardLink
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = Get-Item -LiteralPath $SourceFile
$extension = $source.Extension
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

if (-not $HardLink) {
    $estimateGiB = [Math]::Round(($source.Length * $Count) / 1GB, 2)
    Write-Host "Full copies will take about $estimateGiB GiB. Use -HardLink to avoid this." -ForegroundColor Yellow
}

for ($index = 1; $index -le $Count; $index++) {
    $name = 'Track {0:D5}{1}' -f $index, $extension
    $target = Join-Path $Destination $name
    if (Test-Path -LiteralPath $target) { continue }

    if ($HardLink) {
        New-Item -ItemType HardLink -Path $target -Target $source.FullName | Out-Null
    }
    else {
        Copy-Item -LiteralPath $source.FullName -Destination $target
    }

    if ($index % 500 -eq 0) {
        Write-Progress -Activity 'Creating test library' -Status "$index of $Count" -PercentComplete ($index * 100 / $Count)
    }
}

Write-Progress -Activity 'Creating test library' -Completed
Write-Host "Done: $Count files in $Destination" -ForegroundColor Green
