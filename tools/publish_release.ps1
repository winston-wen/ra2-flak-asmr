[CmdletBinding()]
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$flakRoot = Split-Path -Parent $PSScriptRoot
$flakDist = Join-Path $flakRoot 'dist'
$flakExe = Join-Path $flakDist 'RA2FlakASMR.exe'
if (!(Test-Path -LiteralPath $flakExe)) { throw 'Release executable is missing.' }
foreach ($flakKey in @('GITHUB_REPOSITORY','GITHUB_SHA','GITHUB_REF','GITHUB_RUN_NUMBER','GITHUB_RUN_ID')) {
    if (![Environment]::GetEnvironmentVariable($flakKey)) { throw ('Missing environment variable: ' + $flakKey) }
}
$flakRef = $env:GITHUB_REF
if ($flakRef.StartsWith('refs/tags/v')) { $flakTag = $flakRef.Substring('refs/tags/'.Length) }
elseif ($flakRef -eq 'refs/heads/main') { $flakTag = 'build-' + $env:GITHUB_RUN_NUMBER }
else { throw 'Releases are limited to main and v* tags.' }
$flakHash = (Get-FileHash -LiteralPath $flakExe -Algorithm SHA256).Hash.ToLowerInvariant()
$flakChecksums = Join-Path $flakDist 'SHA256SUMS.txt'
[IO.File]::WriteAllText($flakChecksums,$flakHash + '  RA2FlakASMR.exe' + "`n",[Text.Encoding]::ASCII)
$flakNotes = Join-Path $flakDist 'RELEASE_NOTES.md'
$flakBody = @"
Windows x64 single-file executable. Audio, images and bitmap glyphs are embedded.

- Commit: $($env:GITHUB_SHA)
- Build: https://github.com/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:GITHUB_RUN_ID)
- Validation: mixer stress tests, numeric controls, hover/pressed hit states and pixel-exact paused/playing screenshots.

Download RA2FlakASMR.exe and run it. Space plays/pauses; click numbers to edit; hold +/- to adjust continuously.
Oil wells (0-32) uses the original 1000 capture bonus, 20 credits per 100 logic frames and one global credit counter / interrupting CreditUp voice. Simulation runs at 60 logic frames per second. Pause preserves well timers and never grants another capture bonus.
See the repository README, docs and third-party notices for usage and resource attribution.
"@
[IO.File]::WriteAllText($flakNotes,$flakBody,(New-Object Text.UTF8Encoding($false)))
if ($DryRun) {
    [pscustomobject]@{Repository=$env:GITHUB_REPOSITORY;Tag=$flakTag;Commit=$env:GITHUB_SHA;Assets=@($flakExe,$flakChecksums)} | ConvertTo-Json
    return
}
if (!(Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI is required to publish.' }
if (!$env:GH_TOKEN) { throw 'GH_TOKEN is required to publish.' }
# In Windows PowerShell 5.1, redirected native stderr can terminate the script.
# A missing release is expected on the first build; inspect the exit code instead.
$flakSavedPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'SilentlyContinue'
    & gh release view $flakTag --repo $env:GITHUB_REPOSITORY --json tagName 2>$null | Out-Null
    $flakExisting = $LASTEXITCODE -eq 0
} finally { $ErrorActionPreference = $flakSavedPreference }
if ($flakExisting) {
    & gh release upload $flakTag $flakExe $flakChecksums --repo $env:GITHUB_REPOSITORY --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Release asset upload failed.' }
} else {
    & gh release create $flakTag $flakExe $flakChecksums --repo $env:GITHUB_REPOSITORY --target $env:GITHUB_SHA --title ('RA2 Flak ASMR ' + $flakTag) --notes-file $flakNotes --draft
    if ($LASTEXITCODE -ne 0) { throw 'Draft release creation failed.' }
}
& gh release edit $flakTag --repo $env:GITHUB_REPOSITORY --draft=false --latest --notes-file $flakNotes
if ($LASTEXITCODE -ne 0) { throw 'Release publication failed.' }
Write-Host ('Published https://github.com/' + $env:GITHUB_REPOSITORY + '/releases/tag/' + $flakTag)
