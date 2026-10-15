[CmdletBinding()]
param([string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$flakRoot = $PSScriptRoot
$flakOutput = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $flakRoot $OutputDirectory }
$flakOutput = [IO.Path]::GetFullPath($flakOutput)
New-Item -ItemType Directory -Force -Path $flakOutput | Out-Null
$flakCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $flakCompiler)) { throw 'The Windows x64 .NET Framework C# compiler is required.' }
$flakExe = Join-Path $flakOutput 'RA2FlakASMR.exe'
$flakArguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/debug-','/utf8output',
    '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll',
    ('/win32manifest:' + (Join-Path $flakRoot 'src/app.manifest')),('/out:' + $flakExe))
$flakResources = @(Get-ChildItem -LiteralPath (Join-Path $flakRoot 'assets/audio') -Filter '*.wav' | Sort-Object Name)
$flakResources += @(Get-ChildItem -LiteralPath (Join-Path $flakRoot 'assets/images') -Filter '*.png' | Sort-Object Name)
$flakResources += @(Get-Item -LiteralPath (Join-Path $flakRoot 'assets/images/flak.ico'))
$flakResources += @(Get-Item -LiteralPath (Join-Path $flakRoot 'assets/fonts/westwood.bin'))
$flakResources += @(Get-Item -LiteralPath (Join-Path $flakRoot 'assets/fonts/FONT-LICENSE.txt'))
foreach ($flakResource in $flakResources) { $flakArguments += '/resource:' + $flakResource.FullName + ',' + $flakResource.Name }
$flakArguments += '/win32icon:' + (Join-Path $flakRoot 'assets/images/flak.ico')
foreach ($flakSource in @('FlakASMR.cs','WasapiEngine.cs','PixelForm.cs','AssemblyInfo.cs')) {
    $flakArguments += Join-Path $flakRoot ('src/' + $flakSource)
}
& $flakCompiler $flakArguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output $flakExe
