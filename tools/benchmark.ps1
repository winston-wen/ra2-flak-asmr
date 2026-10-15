[CmdletBinding()]
param([switch]$Live)
$ErrorActionPreference='Stop'
$flakRoot=Split-Path -Parent $PSScriptRoot
$flakExe=Join-Path $flakRoot 'dist/RA2FlakASMR.exe'
if(!(Test-Path -LiteralPath $flakExe)){throw 'Run ./build.ps1 first.'}
$flakReports=Join-Path $flakRoot 'build/performance'
New-Item -ItemType Directory -Force -Path $flakReports | Out-Null
foreach($flakMode in @('benchmark')+$(if($Live){@('live-benchmark')}else{@()})) {
    $flakReport=Join-Path $flakReports ($flakMode+'.txt')
    $flakProcess=Start-Process -FilePath $flakExe -ArgumentList @('--'+$flakMode,('"'+$flakReport+'"')) -WindowStyle Hidden -PassThru
    if(!$flakProcess.WaitForExit(60000)){$flakProcess.Kill();throw 'Benchmark timed out.'}
    Get-Content -LiteralPath $flakReport
    if($flakProcess.ExitCode -ne 0){throw 'Benchmark failed.'}
}
