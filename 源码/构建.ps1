$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
dotnet publish (Join-Path $PSScriptRoot 'LightLaunch\LightLaunch.csproj') -c Release --self-contained false -o (Join-Path $projectRoot '程序') --nologo
if ($LASTEXITCODE -ne 0) { throw '发布失败；请先关闭正在运行的轻启程序再重试。' }
