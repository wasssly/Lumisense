<#
.SYNOPSIS
Samples a running Lumisense process once per second and appends one summary row to a CSV file.

.DESCRIPTION
Use it for the performance baseline (docs/PERFORMANCE_BASELINE.md): start the player, prepare the scenario
(playing, paused, window in tray, ...), then run this script for the scenario and do not touch the mouse.
Context switches per second are a proxy for thread wake-ups. For exact wake-up counts use Windows Performance Recorder.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Scenario,

    [ValidateRange(10, 3600)]
    [int]$Seconds = 60,

    [string]$ProcessName = 'Lumisense',

    [string]$OutputCsv = (Join-Path (Split-Path -Parent $PSScriptRoot) 'lumisense-baseline.csv')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Percentile {
    param([double[]]$Values, [double]$Fraction)

    if ($Values.Count -eq 0) { return 0.0 }
    $sorted = $Values | Sort-Object
    $index = [Math]::Min($sorted.Count - 1, [Math]::Max(0, [int][Math]::Ceiling($Fraction * $sorted.Count) - 1))
    return [double]$sorted[$index]
}

$processes = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($processes.Count -eq 0) {
    throw "Process '$ProcessName' was not found. Start the player and prepare the scenario first."
}
if ($processes.Count -gt 1) {
    throw "More than one '$ProcessName' process is running. Close the extra instances so the measurement is unambiguous."
}

$processId = $processes[0].Id
$version = ''
try { $version = $processes[0].MainModule.FileVersion } catch { $version = 'unknown' }

function Read-Sample {
    $process = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $processId"
    if ($null -eq $process) { throw 'The process exited during the measurement.' }

    $threads = @(Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Thread -Filter "IDProcess = $processId")
    $switches = 0.0
    foreach ($thread in $threads) { $switches += [double]$thread.ContextSwitchesPersec }

    return [pscustomobject]@{
        Cpu          = [double]$process.PercentProcessorTime
        Switches     = $switches
        WriteBytes   = [double]$process.IOWriteBytesPersec
        PrivateBytes = [double]$process.WorkingSetPrivate
        Threads      = [int]$process.ThreadCount
        Handles      = [int]$process.HandleCount
    }
}

# The first formatted sample covers an undefined interval, so it is read and thrown away.
$null = Read-Sample

$cpu = New-Object System.Collections.Generic.List[double]
$switchRates = New-Object System.Collections.Generic.List[double]
$writtenBytes = 0.0
$peakPrivateBytes = 0.0
$threadCount = 0
$handleCount = 0

Write-Host "Measuring '$Scenario' for $Seconds s (process $processId). Do not touch the mouse." -ForegroundColor Cyan
for ($second = 0; $second -lt $Seconds; $second++) {
    Start-Sleep -Seconds 1
    $sample = Read-Sample
    $cpu.Add($sample.Cpu)
    $switchRates.Add($sample.Switches)
    $writtenBytes += $sample.WriteBytes
    if ($sample.PrivateBytes -gt $peakPrivateBytes) { $peakPrivateBytes = $sample.PrivateBytes }
    $threadCount = $sample.Threads
    $handleCount = $sample.Handles
}

$cpuValues = $cpu.ToArray()
$switchValues = $switchRates.ToArray()

$row = [pscustomobject][ordered]@{
    Timestamp                 = (Get-Date).ToString('s')
    Scenario                  = $Scenario
    Version                   = $version
    Seconds                   = $Seconds
    CpuAvgPercentOfOneCore    = [Math]::Round(($cpuValues | Measure-Object -Average).Average, 2)
    CpuP95PercentOfOneCore    = [Math]::Round((Get-Percentile -Values $cpuValues -Fraction 0.95), 2)
    CpuMaxPercentOfOneCore    = [Math]::Round(($cpuValues | Measure-Object -Maximum).Maximum, 2)
    ContextSwitchesPerSecAvg  = [Math]::Round(($switchValues | Measure-Object -Average).Average, 1)
    ContextSwitchesPerSecMax  = [Math]::Round(($switchValues | Measure-Object -Maximum).Maximum, 1)
    DiskWrittenKiB            = [Math]::Round($writtenBytes / 1KB, 1)
    PrivateWorkingSetPeakMiB  = [Math]::Round($peakPrivateBytes / 1MB, 1)
    Threads                   = $threadCount
    Handles                   = $handleCount
}

$row | Export-Csv -Path $OutputCsv -Append -NoTypeInformation -Encoding UTF8
$row | Format-List
Write-Host "Row appended to $OutputCsv" -ForegroundColor Green
