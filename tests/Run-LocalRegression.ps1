param([switch]$Desktop)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$sources = @(Get-ChildItem (Join-Path $project 'src') -Filter '*.cs' | ForEach-Object FullName)
$cases = @('FileOperationsRegression', 'MediaPreviewRegression', 'AppIconsRegression', 'TaskManagerRegression', 'FileBrowserPerformance', 'RuntimeRegression')
if ($Desktop) { $cases += 'DesktopRegression' }
foreach ($case in $cases) {
    $output = Join-Path $PSScriptRoot "$case.exe"
    & $compiler /nologo /target:exe /codepage:65001 "/main:PhoneAudioTray.$case" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$output" $sources (Join-Path $PSScriptRoot "$case.cs")
    if ($LASTEXITCODE -ne 0) { throw "$case compilation failed" }
    & $output
    if ($LASTEXITCODE -ne 0) { throw "$case failed" }
}
