[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'LightLaunch\LightLaunch.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid project version.' }
$releaseRoot = Join-Path $projectRoot "发布\$version"
if (Test-Path -LiteralPath $releaseRoot) { throw "Release directory already exists: $releaseRoot" }

# Build only committed source. Never archive local settings, test evidence or installers.
$sourceCommit = & git -C $projectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Commit the public source before packaging.' }
$changes = & git -C $projectRoot status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Commit tracked changes before packaging.' }
$packageName = "LightLaunch-$version-win-x64"
$stage = Join-Path $releaseRoot $packageName
$program = Join-Path $stage '程序'
New-Item -ItemType Directory -Path $program -Force | Out-Null
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $program --nologo '-p:DebugType=None' '-p:DebugSymbols=false' '-p:ContinuousIntegrationBuild=true'
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot '启动.cmd') -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot '资料\使用说明.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot '资料\开源致谢.md') -Destination $stage
$quickStart = @"
轻启 LightLaunch $version — Windows 11 x64

完整解压后双击“启动.cmd”，按 Windows UAC 提示确认管理员权限。
首次打开请选择自己的软件根目录，目录结构为：根目录 / 分类 / 软件文件夹。
本包自带 .NET 运行库。配置与检测记录首次运行后保存在同级“数据”目录。
升级前退出程序，保留原“数据”目录，用新包覆盖程序文件。
主程序：程序/LightLaunch.exe；详细操作见“使用说明.md”或程序内使用说明。
残留候选可能含个人配置或共享数据，删除前请核对；安装版目录迁移有兼容性限制。
当前程序未签名，程序启动的工具会继承管理员权限。

项目：https://github.com/yan450863-ui/LightLaunch
源码提交：$sourceCommit
许可：轻启采用 MIT，随附运行库按“许可”目录内各自的许可分发。
"@
[IO.File]::WriteAllText((Join-Path $stage '开始使用.txt'), $quickStart, [Text.UTF8Encoding]::new($true))

# Use the licenses belonging to the exact runtime packs restored for this publish.
$assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'LightLaunch\obj\project.assets.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$licenseRoot = Join-Path $stage '许可'
foreach ($runtime in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $entry = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -like "$runtime/*" })
    if ($entry.Count -ne 1) { throw "Cannot resolve runtime license: $runtime" }
    $packagePath = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder $entry[0].Value.path
        if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate; break }
    }
    if (-not $packagePath) { throw "Runtime package missing: $runtime" }
    $destination = Join-Path $licenseRoot ($entry[0].Name -replace '/', '-')
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $licenses = @(Get-ChildItem -LiteralPath $packagePath -File | Where-Object { $_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' })
    if (-not @($licenses | Where-Object { $_.Name -match '^LICENSE' }).Count) { throw "Runtime license missing: $runtime" }
    if ($runtime -like 'Microsoft.NETCore.*' -and -not @($licenses | Where-Object { $_.Name -like '*NOTICES*' }).Count) { throw 'Core runtime third-party notices missing.' }
    foreach ($license in $licenses) { Copy-Item -LiteralPath $license.FullName -Destination $destination }
}

$binaryZip = Join-Path $releaseRoot "$packageName.zip"
Compress-Archive -LiteralPath $stage -DestinationPath $binaryZip -CompressionLevel Optimal
$sourceZip = Join-Path $releaseRoot "LightLaunch-$version-source.zip"
& git -C $projectRoot archive --format=zip "--prefix=LightLaunch-$version-source/" "--output=$sourceZip" HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
$checksums = foreach ($file in @($binaryZip, $sourceZip)) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($file))"
}
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Write-Output "Release ready: $releaseRoot"
