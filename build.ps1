param([switch]$Portable, [string]$Runtime = "$env:LOCALAPPDATA\PhoneAudioTray\4.1")
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $version = [regex]::Match((Get-Content src\Program.cs -Raw), 'AppVersion = "([^"]+)"').Groups[1].Value
    $output = if ($Portable) { "Transmit-v$version.exe" } else { 'Transmit-dev.exe' }
    $compileArgs = @('/nologo', '/target:winexe', '/codepage:65001', '/optimize+', '/win32icon:app.ico', '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', "/out:$output")
    if ($Portable) {
        foreach ($name in @('scrcpy.exe','scrcpy-server','scrcpy.ico','avcodec-62.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','SDL3.dll','libusb-1.0.dll','adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll')) {
            $resource = Join-Path $Runtime $name
            if (!(Test-Path -LiteralPath $resource -PathType Leaf) -or (Get-Item -LiteralPath $resource).Length -eq 0) { throw "Missing or empty runtime file: $resource" }
            $compileArgs += "/resource:$resource,$name"
        }
        $compileArgs += '/resource:app.ico,app.ico', '/resource:使用手册.md,manual.md'
    }
    $compileArgs += @(Get-ChildItem src -Filter '*.cs' | ForEach-Object FullName)
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" $compileArgs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    Get-Item -LiteralPath $output | Select-Object FullName,Length
} finally { Pop-Location }
