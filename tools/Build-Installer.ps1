# Builds the portable app and the per-user installer.
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File tools/Build-Installer.ps1
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

Write-Host '==> 1/4 发布文件夹版到 publish\app' -ForegroundColor Cyan
& $dotnet publish 'src/TidyC.App/TidyC.App.csproj' -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o 'publish/app' -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'folder publish failed' }

Write-Host '==> 2/4 打包安装负载 payload.zip' -ForegroundColor Cyan
$stage = 'artifacts/installer-stage/app'
Remove-Item 'artifacts/installer-stage' -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null
robocopy 'publish/app' $stage /E /XD 'TidyC-Data' /NFL /NDL /NJH /NJS /NP | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = 'installer/TidyC.Setup/payload.zip'
Remove-Item $payload -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Resolve-Path $stage).Path,
    (Join-Path (Get-Location) $payload),
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Host '==> 3/4 发布安装程序（单文件，自包含）' -ForegroundColor Cyan
& $dotnet publish 'installer/TidyC.Setup/TidyC.Setup.csproj' -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -o 'publish' -v q --nologo
if ($LASTEXITCODE -ne 0) { throw 'installer publish failed' }

Write-Host '==> 4/4 校验' -ForegroundColor Cyan
$setup = Get-Item 'publish/TidyC-Setup.exe'
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash
"TidyC-Setup.exe  {0:N1} MB  SHA256 {1}" -f ($setup.Length / 1MB), $hash
