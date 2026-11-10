param(
    [Parameter(Mandatory)][string]$ElectronArchive,
    [string]$Dotnet = 'dotnet',
    [string]$RuntimePacks,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9-]*$')][string]$DestinationName = 'ShowControl-Desktop-win-x64'
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$archive = (Resolve-Path -LiteralPath $ElectronArchive).Path
$destination = Join-Path $workspace "dist/$DestinationName"
if (Test-Path -LiteralPath $destination) {
    throw "发布目录已存在。请先将旧发布目录移到别处。 $destination"
}

$project = Join-Path $workspace 'ShowControl/ShowControl.csproj'
$artifacts = Join-Path $workspace '.tools/showcontrol-desktop-build'
$publish = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '--artifacts-path', $artifacts, '-o', (Join-Path $destination 'resources/engine'))
if ($RuntimePacks) {
    $publish += @('--source', (Resolve-Path -LiteralPath $RuntimePacks).Path)
}
& $Dotnet @publish
if ($LASTEXITCODE -ne 0) {
    throw "灯控引擎构建失败，退出码 $LASTEXITCODE。"
}

Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
Rename-Item -LiteralPath (Join-Path $destination 'electron.exe') -NewName 'ShowControlDesktop.exe'
$crashReporter = Join-Path $destination 'electron_wer.dll'
if (Test-Path -LiteralPath $crashReporter) {
    Rename-Item -LiteralPath $crashReporter -NewName 'ShowControlDesktop_wer.dll'
}
$appDirectory = Join-Path $destination 'resources/app'
New-Item -ItemType Directory -Path $appDirectory | Out-Null
foreach ($file in @('package.json', 'main.cjs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $appDirectory
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $destination
Write-Output (Join-Path $destination 'ShowControlDesktop.exe')
