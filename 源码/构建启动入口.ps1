param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 编译器不可用。' }
& $compiler /nologo /codepage:65001 /target:winexe /optimize+ /reference:System.Windows.Forms.dll "/win32icon:$(Join-Path $PSScriptRoot 'LightLaunch\LightLaunch.ico')" "/out:$Destination" (Join-Path $PSScriptRoot '启动入口.cs')
if ($LASTEXITCODE -ne 0) { throw '启动入口构建失败。' }
