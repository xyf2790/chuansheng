param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Executable).Path)
$payload = $assembly.GetType('PhoneAudioTray.Payload', $true)
$extract = $payload.GetMethod('ExtractResource', [Reflection.BindingFlags]'Static,NonPublic')
$matches = $payload.GetMethod('ResourceMatches', [Reflection.BindingFlags]'Static,NonPublic')
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('Transmit-portable-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    $names = $assembly.GetManifestResourceNames()
    if ($names.Count -lt 14 -or $names -notcontains 'adb.exe' -or $names -notcontains 'manual.md') { throw 'Incomplete portable resources' }
    foreach ($name in $names) {
        $destinationName = if ($name -eq 'manual.md') { '使用手册.md' } else { $name }
        $destination = Join-Path $temporaryRoot $destinationName
        $source = $assembly.GetManifestResourceStream($name)
        try { $extract.Invoke($null, @($source, [string]$destination)) | Out-Null } finally { $source.Dispose() }
        if (!$matches.Invoke($null, @($assembly, [string]$name, [string]$temporaryRoot))) { throw "Resource mismatch: $name" }
    }
    $adb = Join-Path $temporaryRoot 'adb.exe'
    [IO.File]::WriteAllBytes($adb, [byte[]]@(1))
    if ($matches.Invoke($null, @($assembly, 'adb.exe', [string]$temporaryRoot))) { throw 'Truncated runtime accepted' }
    $source = $assembly.GetManifestResourceStream('adb.exe')
    try { $extract.Invoke($null, @($source, [string]$adb)) | Out-Null } finally { $source.Dispose() }
    if (!$matches.Invoke($null, @($assembly, 'adb.exe', [string]$temporaryRoot))) { throw 'Runtime repair failed' }
    Write-Output 'PASS portable resources, isolated extraction, truncated runtime detection and repair'
} finally {
    # This exact GUID directory is created above, solely for this check.
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}
