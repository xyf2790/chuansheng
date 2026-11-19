param(
    [string]$Runtime = "$env:LOCALAPPDATA\PhoneAudioTray\4.1",
    [string]$Serial = '',
    [string]$Remote = ('/sdcard/Download/Transmit-regression-' + [guid]::NewGuid().ToString('N'))
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$output = Join-Path $PSScriptRoot 'TransferRegression.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:exe /codepage:65001 /main:PhoneAudioTray.TransferRegression /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$output" $sources (Join-Path $PSScriptRoot 'TransferRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Regression harness compilation failed' }
if (!$Serial) { throw 'Specify -Serial from adb devices; tests write only synthetic files to -Remote.' }
& $output $Runtime $Serial (Join-Path $PSScriptRoot 'samples') $Remote
if ($LASTEXITCODE -ne 0) { throw 'Transfer regression failed' }
