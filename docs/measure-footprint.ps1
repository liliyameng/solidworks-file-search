# measure-footprint.ps1 — SwSearch resource footprint sampler (read-only).
# Samples working set / private bytes of the product processes plus all
# msedgewebview2.exe runtime processes and appends one CSV row per tick.
# It never starts, stops or modifies anything; Ctrl+C stops sampling.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\docs\measure-footprint.ps1 [-Seconds 300] [-IntervalSeconds 5]
# Suggested protocol (see docs/resource-optimization-2026-10-13.md):
#   visible 2 min -> hide to tray 5 min -> restore 1 min, then compare rows.
# The phase column is derived from the App window handle, so hiding to tray is
# recorded automatically.

param(
    [int]$Seconds = 300,
    [int]$IntervalSeconds = 5
)

$ErrorActionPreference = 'Continue'
$out = Join-Path $PSScriptRoot ('footprint-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.csv')
'timestamp,phase,app_ws_mb,app_priv_mb,broker_ws_mb,worker_ws_mb,viewer_ws_mb,webview2_count,webview2_ws_mb_total,webview2_priv_mb_total' | Out-File -LiteralPath $out -Encoding utf8

function Get-MaxWs([object[]]$set) {
    if ($null -eq $set -or $set.Count -eq 0) { return '' }
    return [math]::Round((($set | Measure-Object WorkingSet64 -Maximum).Maximum) / 1MB, 1)
}
function Get-MaxPriv([object[]]$set) {
    if ($null -eq $set -or $set.Count -eq 0) { return '' }
    return [math]::Round((($set | Measure-Object PrivateMemorySize64 -Maximum).Maximum) / 1MB, 1)
}
function Get-SumWs([object[]]$set) {
    if ($null -eq $set -or $set.Count -eq 0) { return 0 }
    return [math]::Round((($set | Measure-Object WorkingSet64 -Sum).Sum) / 1MB, 1)
}
function Get-SumPriv([object[]]$set) {
    if ($null -eq $set -or $set.Count -eq 0) { return 0 }
    return [math]::Round((($set | Measure-Object PrivateMemorySize64 -Sum).Sum) / 1MB, 1)
}

$deadline = (Get-Date).AddSeconds($Seconds)
$rowIndex = 0
Write-Output ("Sampling for {0}s every {1}s -> {2}" -f $Seconds, $IntervalSeconds, $out)

while ((Get-Date) -lt $deadline) {
    $rowIndex++
    $procs = Get-Process -ErrorAction SilentlyContinue
    $app = @($procs | Where-Object ProcessName -eq 'SwSearch.App')
    $broker = @($procs | Where-Object ProcessName -eq 'SwSearch.Broker')
    $worker = @($procs | Where-Object ProcessName -eq 'SwSearch.MetadataWorker')
    $viewer = @($procs | Where-Object ProcessName -eq 'SwSearch.PropertyViewer')
    $wv = @($procs | Where-Object ProcessName -eq 'msedgewebview2')

    $phase = 'no-app'
    if ($app.Count -gt 0) {
        if ($app[0].MainWindowHandle -ne 0) { $phase = 'visible' } else { $phase = 'hidden' }
    }

    $line = '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9}' -f `
        (Get-Date -Format 'HH:mm:ss'), $phase, `
        (Get-MaxWs $app), (Get-MaxPriv $app), `
        (Get-MaxWs $broker), (Get-MaxWs $worker), (Get-MaxWs $viewer), `
        $wv.Count, (Get-SumWs $wv), (Get-SumPriv $wv)
    $line | Out-File -LiteralPath $out -Append -Encoding utf8
    if ($rowIndex % 12 -eq 1) { Write-Output $line }
    Start-Sleep -Seconds $IntervalSeconds
}

Write-Output ("Done. {0} rows -> {1}" -f $rowIndex, $out)
