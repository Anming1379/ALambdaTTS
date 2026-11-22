# ALambda TTS 打包脚本
#
# 流程：dotnet publish（框架依赖单文件）-> Inno Setup 编译安装包
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1
#       powershell ... -File .\build-installer.ps1 -Version 1.0.1

param(
    [string]$Version = '1.1.2',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'ALambdaTTS.csproj'
$publishDir = Join-Path $root 'publish\app'
$iss = Join-Path $root 'installer\ALambdaTTS.iss'
$iscc = 'D:\Program Files (x86)\Inno Setup 6\ISCC.exe'

Write-Host '== 1/3 清理旧的发布目录 ==' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host '== 2/3 dotnet publish ==' -ForegroundColor Cyan
dotnet publish $project -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）" }

Write-Host '== 3/3 Inno Setup 编译安装包 ==' -ForegroundColor Cyan
if (-not (Test-Path $iscc)) {
    Write-Host "没找到 ISCC.exe：$iscc" -ForegroundColor Yellow
    Write-Host '程序已经发布在 publish\app，安装包跳过。'
    exit 0
}

& $iscc "/DMyAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（$LASTEXITCODE）" }

$setup = Get-ChildItem (Join-Path $root 'installer\Output') -Filter 'ALambdaTTS-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($setup) {
    Write-Host ''
    Write-Host "完成：$($setup.FullName)（$([math]::Round($setup.Length / 1MB, 1)) MB）" -ForegroundColor Green
}
