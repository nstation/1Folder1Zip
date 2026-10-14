param([string]$PackageCache)
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PackageCache)) { $PackageCache = Join-Path $taskRoot '.build\packages' }
$taskExe = Join-Path $taskRoot 'artifacts\win-x64\1Folder1Zip.exe'
if (-not (Test-Path -LiteralPath $taskExe)) { throw 'Publish the self-contained EXE first.' }
$taskVersion = ([Version](Get-Item -LiteralPath $taskExe).VersionInfo.FileVersion).ToString(3)
$taskRuntimeConfigPath = Join-Path $taskRoot 'src\OneFolderOneZip.App\bin\Release\net10.0-windows\win-x64\1Folder1Zip.runtimeconfig.json'
$taskRuntimeConfig = Get-Content -LiteralPath $taskRuntimeConfigPath -Raw | ConvertFrom-Json
$taskCoreVersion = ($taskRuntimeConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App').version
$taskDesktopVersion = ($taskRuntimeConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.WindowsDesktop.App').version
if (-not $taskCoreVersion -or -not $taskDesktopVersion) { throw 'The build must be self-contained.' }
$taskCorePack = Join-Path $PackageCache ('microsoft.netcore.app.runtime.win-x64\' + $taskCoreVersion)
$taskDesktopPack = Join-Path $PackageCache ('microsoft.windowsdesktop.app.runtime.win-x64\' + $taskDesktopVersion)
$taskNotices = @(
    @{ Source = (Join-Path $taskCorePack 'LICENSE.TXT'); Name = 'DOTNET-RUNTIME-LICENSE.txt' },
    @{ Source = (Join-Path $taskCorePack 'THIRD-PARTY-NOTICES.TXT'); Name = 'DOTNET-RUNTIME-NOTICES.txt' },
    @{ Source = (Join-Path $taskDesktopPack 'LICENSE'); Name = 'WPF-LICENSE.txt' }
)
foreach ($taskNotice in $taskNotices) {
    if (-not (Test-Path -LiteralPath $taskNotice.Source)) { throw ('Missing runtime notice: ' + $taskNotice.Name) }
}
$taskOutput = Join-Path $taskRoot 'artifacts\releases'
[System.IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskStageParent = [System.IO.Path]::GetFullPath((Join-Path $taskRoot '.build\release-stage'))
$taskStageId = [Guid]::NewGuid().ToString('N')
$taskStage = Join-Path $taskStageParent $taskStageId
$taskTemporaryZip = Join-Path $taskOutput ('.release-' + $taskStageId + '.zip')
$taskArchive = Join-Path $taskOutput ('1Folder1Zip-' + $taskVersion + '-win-x64.zip')
try {
    [System.IO.Directory]::CreateDirectory((Join-Path $taskStage 'licenses')) | Out-Null
    Copy-Item -LiteralPath $taskExe -Destination (Join-Path $taskStage '1Folder1Zip.exe')
    Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskStage 'LICENSE.txt')
    foreach ($taskNotice in $taskNotices) {
        Copy-Item -LiteralPath $taskNotice.Source -Destination (Join-Path $taskStage ('licenses\' + $taskNotice.Name))
    }
    $taskReadme = @"
1Folder1Zip $taskVersion
Developer: @nstation

Windows 10 / 11 x64

1Folder1Zip.exe を起動し、フォルダーをドロップしてください。
各フォルダーの隣にZIPを作成します。元データは残ります。
EXEに直接フォルダーをドロップした起動は、全件成功すると終了します。

Launch 1Folder1Zip.exe and drop folders onto its window.
Each ZIP is saved beside its original folder. Originals are kept.
Dropping folders onto the EXE exits automatically after all jobs succeed.

Application license: MIT (LICENSE.txt).
Bundled .NET $taskCoreVersion / Windows Desktop $taskDesktopVersion notices: licenses/.
"@
    [System.IO.File]::WriteAllText((Join-Path $taskStage 'README.txt'), $taskReadme, [System.Text.UTF8Encoding]::new($false))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($taskStage, $taskTemporaryZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    [System.IO.File]::Move($taskTemporaryZip, $taskArchive, $true)
    $taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskHashLine = $taskHash + '  ' + [System.IO.Path]::GetFileName($taskArchive) + "`n"
    [System.IO.File]::WriteAllText((Join-Path $taskOutput 'SHA256SUMS.txt'), $taskHashLine, [System.Text.UTF8Encoding]::new($false))
    Write-Output ('Created: ' + $taskArchive)
} finally {
    if (Test-Path -LiteralPath $taskTemporaryZip) { Remove-Item -LiteralPath $taskTemporaryZip }
    if (Test-Path -LiteralPath $taskStage) {
        $taskResolvedStage = (Resolve-Path -LiteralPath $taskStage).Path
        if (-not $taskResolvedStage.StartsWith($taskStageParent + '\', [StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($taskResolvedStage) -ne $taskStageId) { throw 'Unexpected staging cleanup path.' }
        Remove-Item -LiteralPath $taskResolvedStage -Recurse -Force
    }
}
