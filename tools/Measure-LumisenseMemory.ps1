<#
.SYNOPSIS
Long-run memory and handle trend check for Lumisense (leak hunting).

.DESCRIPTION
Samples a running Lumisense process every few seconds, writes a time series to a CSV file and prints a trend summary.
Optionally presses the global "Next track" media key at a fixed interval, so hundreds of track changes happen unattended.
WARNING: the media key goes to the whole system. Close other players, or they may react to it as well.
A real leak shows as private memory (and/or handle, GDI, USER counts) that keeps growing after the warm-up period;
a normal working set rises at first and then stays on a plateau.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 1440)]
    [int]$Minutes = 60,

    [ValidateRange(5, 600)]
    [int]$SampleSeconds = 30,

    # 0 = do not press anything. 15..30 is a good value for a shuffle playlist of 100+ tracks.
    [ValidateRange(0, 3600)]
    [int]$NextTrackEverySeconds = 0,

    # Samples taken before this many minutes are ignored when the trend is computed.
    [ValidateRange(0, 120)]
    [int]$WarmupMinutes = 10,

    [string]$ProcessName = 'Lumisense',

    [string]$OutputCsv = (Join-Path (Split-Path -Parent $PSScriptRoot) 'lumisense-memory.csv')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LumisenseProbe
{
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
'@

$processes = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($processes.Count -eq 0) { throw "Process '$ProcessName' was not found. Start the player first." }
if ($processes.Count -gt 1) { throw "More than one '$ProcessName' process is running. Close the extra instances." }

$process = $processes[0]
$processId = $process.Id
$version = ''
try { $version = $process.MainModule.FileVersion } catch { $version = 'unknown' }

function Send-NextTrackKey {
    # VK_MEDIA_NEXT_TRACK = 0xB0, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2
    [LumisenseProbe]::keybd_event(0xB0, 0, 0x1, [UIntPtr]::Zero)
    [LumisenseProbe]::keybd_event(0xB0, 0, 0x3, [UIntPtr]::Zero)
}

function Read-MemorySample {
    $process.Refresh()
    $perf = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $processId"
    return [pscustomobject]@{
        PrivateMiB    = [Math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        WorkingSetMiB = [Math]::Round($process.WorkingSet64 / 1MB, 1)
        PrivateWsMiB  = if ($null -ne $perf) { [Math]::Round([double]$perf.WorkingSetPrivate / 1MB, 1) } else { 0.0 }
        Handles       = $process.HandleCount
        Threads       = $process.Threads.Count
        GdiObjects    = [LumisenseProbe]::GetGuiResources($process.Handle, 0)
        UserObjects   = [LumisenseProbe]::GetGuiResources($process.Handle, 1)
    }
}

$rows = New-Object System.Collections.Generic.List[object]
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$nextSample = 0
$nextKey = if ($NextTrackEverySeconds -gt 0) { $NextTrackEverySeconds } else { [int]::MaxValue }
$trackChanges = 0
$endSeconds = $Minutes * 60

Write-Host "Monitoring process $processId for $Minutes min (sample every $SampleSeconds s)." -ForegroundColor Cyan
if ($NextTrackEverySeconds -gt 0) { Write-Host "Pressing Next track every $NextTrackEverySeconds s." -ForegroundColor Yellow }

while ($stopwatch.Elapsed.TotalSeconds -lt $endSeconds) {
    $elapsed = $stopwatch.Elapsed.TotalSeconds
    if ($process.HasExited) { throw 'The process exited during the run.' }

    if ($elapsed -ge $nextKey) {
        Send-NextTrackKey
        $trackChanges++
        $nextKey += $NextTrackEverySeconds
    }

    if ($elapsed -ge $nextSample) {
        $sample = Read-MemorySample
        $row = [pscustomobject][ordered]@{
            Minute        = [Math]::Round($elapsed / 60, 2)
            TrackChanges  = $trackChanges
            PrivateMiB    = $sample.PrivateMiB
            WorkingSetMiB = $sample.WorkingSetMiB
            PrivateWsMiB  = $sample.PrivateWsMiB
            Handles       = $sample.Handles
            Threads       = $sample.Threads
            GdiObjects    = $sample.GdiObjects
            UserObjects   = $sample.UserObjects
        }
        $rows.Add($row)
        Write-Host ("{0,7:N1} min  private {1,7:N1} MiB  handles {2,5}  GDI {3,4}  USER {4,4}  tracks {5}" -f $row.Minute, $row.PrivateMiB, $row.Handles, $row.GdiObjects, $row.UserObjects, $trackChanges)
        $nextSample += $SampleSeconds
    }

    Start-Sleep -Milliseconds 500
}

$rows | Export-Csv -Path $OutputCsv -NoTypeInformation -Encoding UTF8
Write-Host "Time series saved to $OutputCsv" -ForegroundColor Green

function Get-SlopePerHour {
    param([object[]]$Points, [string]$Property)

    if ($Points.Count -lt 3) { return $null }
    $n = $Points.Count
    $sumX = 0.0; $sumY = 0.0; $sumXY = 0.0; $sumXX = 0.0
    foreach ($point in $Points) {
        $x = [double]$point.Minute; $y = [double]$point.$Property
        $sumX += $x; $sumY += $y; $sumXY += $x * $y; $sumXX += $x * $x
    }
    $denominator = $n * $sumXX - $sumX * $sumX
    if ($denominator -eq 0) { return $null }
    return [Math]::Round((($n * $sumXY - $sumX * $sumY) / $denominator) * 60, 2)
}

$steady = @($rows | Where-Object { $_.Minute -ge $WarmupMinutes })
Write-Host ''
Write-Host "Trend after the $WarmupMinutes-minute warm-up ($($steady.Count) samples):" -ForegroundColor Cyan
if ($steady.Count -lt 3) {
    Write-Host 'Too few samples after the warm-up: increase -Minutes or lower -WarmupMinutes.' -ForegroundColor Yellow
}
else {
    foreach ($property in 'PrivateMiB', 'PrivateWsMiB', 'Handles', 'GdiObjects', 'UserObjects') {
        $slope = Get-SlopePerHour -Points $steady -Property $property
        Write-Host ("  {0,-13} {1,8} per hour" -f $property, $slope)
    }

    Write-Host ''
    Write-Host 'Reading the result: a slope near zero is fine. Steady growth of private memory (tens of MiB per hour) or of handles / GDI / USER objects over many track changes points to a leak.' -ForegroundColor DarkGray
}
