param([switch]$Publish, [switch]$Test, [switch]$Stress, [switch]$Package)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.build\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.build\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$appProject = Join-Path $taskRoot 'src\OneFolderOneZip.App\OneFolderOneZip.App.csproj'
if ($Package) { $Publish = $true }
& dotnet build (Join-Path $taskRoot '1Folder1Zip.slnx') -c Release --nologo -m:1 -nr:false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($Test -or $Stress) {
    $testArguments = @('run', '--project', (Join-Path $taskRoot 'tests\OneFolderOneZip.Tests'), '-c', 'Release', '--no-build')
    if ($Stress) { $testArguments += @('--', '--stress') }
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & dotnet run --project (Join-Path $taskRoot 'tests\OneFolderOneZip.UiTests') -c Release --no-build -- --japanese
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
}
if ($Publish) {
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:UseSharedCompilation=false -m:1 -nr:false -o (Join-Path $taskRoot 'artifacts\win-x64') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
if ($Package) {
    & (Join-Path $taskRoot 'tools\New-Release.ps1') -PackageCache $env:NUGET_PACKAGES
}
