$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskExe = Join-Path $taskRoot 'artifacts\win-x64\1Folder1Zip.exe'
$fixture = Join-Path $taskRoot ('.build\launch-' + [Guid]::NewGuid().ToString('N'))
$standaloneFolder = Join-Path $fixture 'EXE ドロップ'
$forwardedFolder = Join-Path $fixture '転送 テスト'
foreach ($folder in @($standaloneFolder, $forwardedFolder)) {
    [System.IO.Directory]::CreateDirectory($folder) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $folder '日本語.txt'), '起動確認用のテストデータ')
}
$primary = $null
try {
    $standalone = Start-Process -FilePath $taskExe -ArgumentList ('"' + $standaloneFolder + '"') -WindowStyle Hidden -PassThru
    if (-not $standalone.WaitForExit(20000)) { throw 'EXE-drop launch did not automatically exit.' }
    if (-not [System.IO.File]::Exists($standaloneFolder + '.zip')) { throw 'EXE-drop launch produced no ZIP.' }
    Write-Output 'PASS: self-contained EXE-drop launch and automatic exit'

    $primary = Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru
    if (-not $primary.WaitForInputIdle(10000)) { throw 'Primary instance did not initialize.' }
    $secondary = Start-Process -FilePath $taskExe -ArgumentList ('"' + $forwardedFolder + '"') -WindowStyle Hidden -PassThru
    if (-not $secondary.WaitForExit(20000)) { throw 'Secondary instance did not exit after forwarding.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not [System.IO.File]::Exists($forwardedFolder + '.zip')) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Forwarded folder produced no ZIP.' }
        Start-Sleep -Milliseconds 100
    }
    $primary.Refresh()
    if ($primary.HasExited) { throw 'The GUI instance closed after forwarded input.' }
    Write-Output 'PASS: single-instance forwarding and GUI result retention'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($folder in @($standaloneFolder, $forwardedFolder)) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($folder + '.zip')
        try {
            if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -ne '日本語.txt') { throw 'Unexpected ZIP contents.' }
            $reader = New-Object System.IO.StreamReader($archive.Entries[0].Open())
            try { if ($reader.ReadToEnd() -ne '起動確認用のテストデータ') { throw 'ZIP data changed.' } }
            finally { $reader.Dispose() }
        } finally { $archive.Dispose() }
    }
    Write-Output 'PASS: published EXE preserves Unicode names and file contents'
} finally {
    if ($primary -and -not $primary.HasExited) {
        $primary.CloseMainWindow() | Out-Null
        if (-not $primary.WaitForExit(10000)) {
            $primary.Refresh()
            if ([System.IO.Path]::GetFullPath($primary.Path).Equals($taskExe, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $primary.Id
            }
        }
    }
}
