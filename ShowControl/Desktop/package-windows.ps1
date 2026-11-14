param(
    [Parameter(Mandatory)][string]$ElectronArchive,
    [string]$Dotnet = 'dotnet',
    [string]$ResourceEditor = 'rcedit',
    [string]$RuntimePacks,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9-]*$')][string]$DestinationName = 'SymphoCue-win-x64'
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$archive = (Resolve-Path -LiteralPath $ElectronArchive).Path
$resourceTool = (Get-Command $ResourceEditor -CommandType Application -ErrorAction Stop).Source
$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package.json') -Raw | ConvertFrom-Json
$icon = Join-Path $PSScriptRoot 'symphocue.ico'
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
$executableName = "$($metadata.productName).exe"
$executable = Join-Path $destination $executableName
Rename-Item -LiteralPath (Join-Path $destination 'electron.exe') -NewName $executableName
$crashReporter = Join-Path $destination 'electron_wer.dll'
if (Test-Path -LiteralPath $crashReporter) {
    Rename-Item -LiteralPath $crashReporter -NewName "$($metadata.productName)_wer.dll"
}
& $resourceTool $executable --set-icon $icon --set-file-version $metadata.version --set-product-version $metadata.version `
    --set-version-string ProductName $metadata.productName --set-version-string FileDescription $metadata.productName `
    --set-version-string InternalName $metadata.productName --set-version-string OriginalFilename $executableName
if ($LASTEXITCODE -ne 0) {
    throw "Windows 图标和版本信息写入失败，退出码 $LASTEXITCODE。"
}
$appDirectory = Join-Path $destination 'resources/app'
New-Item -ItemType Directory -Path $appDirectory | Out-Null
foreach ($file in @('package.json', 'main.cjs', 'symphocue.ico')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $appDirectory
}
Copy-Item -LiteralPath (Join-Path $workspace 'ShowControl/wwwroot/branding/symphocue-icon.png') -Destination $appDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $destination
Write-Output $executable
