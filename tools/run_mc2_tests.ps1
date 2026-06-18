# Runs the MagicaCloth2 characterization test suite end to end:
#   refresh -> wait for compile -> exec play-mode run -> wait for results.json -> print it.
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/run_mc2_tests.ps1            # full cycle
#   powershell -ExecutionPolicy Bypass -File tools/run_mc2_tests.ps1 -NoRefresh # skip recompile
param([switch]$NoRefresh, [string]$Only = '', [switch]$Summary)

function Cmd($c) {
    try { return (& "$PSScriptRoot/unity_cmd.ps1" -cmd $c) 2>$null } catch { return 'down' }
}

$resultsDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'ExportedAssets/test-results'
$filterPath = Join-Path $resultsDir 'filter.txt'
New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null
if ($Only -ne '') { Set-Content -Path $filterPath -Value $Only -NoNewline }
elseif (Test-Path $filterPath) { Remove-Item $filterPath -Force }

if (-not $NoRefresh) {
    Cmd 'refresh' | Out-Null
    Start-Sleep -Seconds 6
    $log = 'compiling'
    for ($i = 0; $i -lt 40; $i++) {
        $log = Cmd 'log'
        if ($log -and $log -ne 'compiling' -and $log -ne 'down') { break }
        Start-Sleep -Seconds 3
    }
    if ($log -like 'ERRORS*') { Write-Output "COMPILE $log"; exit 1 }
}

$results = Join-Path (Split-Path $PSScriptRoot -Parent) 'ExportedAssets/test-results/results.json'
if (Test-Path $results) { Remove-Item $results -Force }

Cmd "exec:MagicaCloth2.Tests.TestRunner.Run" | Out-Null

for ($i = 0; $i -lt 180; $i++) {
    if (Test-Path $results) { break }
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $results)) { Write-Output 'NO RESULTS (timeout)'; exit 1 }

if ($Summary) {
    $j = Get-Content $results -Raw | ConvertFrom-Json
    Write-Output ("TOTAL {0}  PASS {1}  FAIL {2}" -f $j.summary.total, $j.summary.pass, $j.summary.fail)
    $j.tests | Where-Object { $_.status -ne 'PASS' } | ForEach-Object { Write-Output ("FAIL: {0} -> {1}" -f $_.name, $_.message) }
}
else {
    Get-Content $results -Raw
}
