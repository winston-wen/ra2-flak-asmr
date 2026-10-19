[CmdletBinding()]
param([switch]$Device, [string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$flakRoot = $PSScriptRoot
$flakDist = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $flakRoot $OutputDirectory }
$flakExe = Join-Path $flakDist 'RA2FlakASMR.exe'
if (!(Test-Path -LiteralPath $flakExe)) { throw 'Run ./build.ps1 first.' }
$flakReports = Join-Path $flakRoot 'build/test-results'
New-Item -ItemType Directory -Force -Path $flakReports | Out-Null
$flakCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$flakRegression = Join-Path $flakReports 'MixerRegression.exe'
$flakRegressionArguments = @('/nologo','/target:exe','/platform:x64','/optimize+','/utf8output',('/out:'+$flakRegression))
foreach($flakClip in Get-ChildItem -LiteralPath (Join-Path $flakRoot 'assets/audio') -Filter '*.wav') {
    $flakRegressionArguments += '/resource:'+$flakClip.FullName+','+$flakClip.Name
}
foreach($flakSource in @('src/Settings.cs','src/Mixer.cs','src/AttackSoundBus.cs','src/OilIncome.cs','tests/OilIncomeTests.cs','tests/AttackBatchTests.cs','tests/StartTimingTests.cs','tests/MixerRegression.cs')) {
    $flakRegressionArguments += Join-Path $flakRoot $flakSource
}
& $flakCompiler $flakRegressionArguments
if($LASTEXITCODE -ne 0){throw 'Mixer regression compilation failed.'}
& $flakRegression
if($LASTEXITCODE -ne 0){throw 'Mixer reference comparison failed.'}
function Invoke-FlakProcess([string[]]$Arguments) {
    $flakQuoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $flakProcess = Start-Process -FilePath $flakExe -ArgumentList $flakQuoted -WorkingDirectory $flakDist -WindowStyle Hidden -PassThru
    if (!$flakProcess.WaitForExit(60000)) { $flakProcess.Kill(); throw ('Test timed out: ' + $Arguments[0]) }
    if ($flakProcess.ExitCode -ne 0) {
        $flakFailureReport = @($Arguments | Where-Object { $_.EndsWith('.txt') -and (Test-Path -LiteralPath $_) })
        if ($flakFailureReport.Count -gt 0) { Get-Content -LiteralPath $flakFailureReport[0] | Write-Host }
        throw ('Test failed: ' + $Arguments[0])
    }
}
$flakMixerReport = Join-Path $flakReports 'mixer.txt'
Invoke-FlakProcess @('--self-test',$flakMixerReport)
Get-Content -LiteralPath $flakMixerReport | Write-Host
$flakUIReport = Join-Path $flakReports 'ui.txt'
Invoke-FlakProcess @('--ui-test',$flakUIReport,'--no-device')
Get-Content -LiteralPath $flakUIReport | Write-Host
foreach ($flakState in @('paused','playing')) {
    $flakActual = Join-Path $flakReports ($flakState + '.png')
    Invoke-FlakProcess @('--render-preview',$flakActual,$flakState)
    $flakExpected = Join-Path $flakRoot ('docs/images/' + $(if ($flakState -eq 'paused') {'preview'} else {'playing'}) + '.png')
    Invoke-FlakProcess @('--compare-preview',$flakExpected,$flakActual,(Join-Path $flakReports ($flakState + '-comparison.txt')))
    Write-Host ('PASS: ' + $flakState + ' matches approved prototype pixel-for-pixel.')
}
if ($Device) {
    $flakDeviceReport = Join-Path $flakReports 'device.txt'
    Invoke-FlakProcess @('--device-test',$flakDeviceReport)
    Get-Content -LiteralPath $flakDeviceReport | Write-Host
    $flakNotificationReport = Join-Path $flakReports 'notifications.txt'
    Invoke-FlakProcess @('--notification-test',$flakNotificationReport)
    Get-Content -LiteralPath $flakNotificationReport | Write-Host
    Invoke-FlakProcess @('--ui-test',(Join-Path $flakReports 'ui-with-device.txt'))
}
Write-Host 'PASS: all checks completed.'
