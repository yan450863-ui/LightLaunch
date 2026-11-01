$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
dotnet publish (Join-Path $PSScriptRoot 'LightLaunch\LightLaunch.csproj') -c Release --self-contained false -o (Join-Path $projectRoot '程序') --nologo
if ($LASTEXITCODE -ne 0) { throw '发布失败；请先关闭正在运行的轻启程序再重试。' }
& (Join-Path $PSScriptRoot '构建启动入口.ps1') -Destination (Join-Path $projectRoot '轻启.exe')
