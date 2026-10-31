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
New-Item -ItemType Directory -Path (Join-Path $stage '软件文件夹') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $stage '软件文件夹\把软件放在这里.txt'), '每款软件保留完整文件夹放在这里；单个 EXE 也可直接放入。轻启首次启动默认读取此目录。', [Text.UTF8Encoding]::new($true))
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $program --nologo '-p:DebugType=None' '-p:DebugSymbols=false' '-p:ContinuousIntegrationBuild=true'
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Copy-Item -LiteralPath (Join-Path $projectRoot '启动.cmd') -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot '资料\使用说明.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot '资料\开源致谢.md') -Destination $stage
$quickStart = @"
轻启 LightLaunch $version — Windows 11 x64

完整解压后双击“启动.cmd”，按 Windows UAC 提示确认管理员权限。
首次打开默认读取同级“软件文件夹”。每款软件放一个完整文件夹，单 EXE 也可直接放入。
也可以更换软件根目录，或切换“按分类放软件”模式。
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
$runtimeConfig = Get-Content -LiteralPath (Join-Path $program 'LightLaunch.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$licenseRoot = Join-Path $stage '许可'
foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
    $runtime = "$($framework.name).Runtime.win-x64"
    $packageId = $runtime.ToLowerInvariant()
    $runtimeVersion = [string]$framework.version
    $packagePath = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path (Join-Path $folder $packageId) $runtimeVersion
        if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate; break }
    }
    if (-not $packagePath) { throw "Runtime package missing: $runtime" }
    $destination = Join-Path $licenseRoot "$runtime-$runtimeVersion"
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

$updateStage = Join-Path $releaseRoot "LightLaunch-$version-update"
New-Item -ItemType Directory -Path (Join-Path $updateStage '程序文件') -Force | Out-Null
$updateFiles = @('LightLaunch.exe', 'LightLaunch.dll', 'LightLaunch.deps.json', 'LightLaunch.runtimeconfig.json')
$updateManifest = [ordered]@{ Version=$version; FromVersion='0.1.10'; Files=@(); Runtime=@() }
foreach ($name in $updateFiles) {
    $file = Join-Path $program $name
    Copy-Item -LiteralPath $file -Destination (Join-Path $updateStage '程序文件')
    $updateManifest.Files += @{Name=$name; SHA256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash}
}
foreach ($name in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','System.Windows.Forms.dll')) {
    $updateManifest.Runtime += @{Name=$name; SHA256=(Get-FileHash -LiteralPath (Join-Path $program $name) -Algorithm SHA256).Hash}
}
[IO.File]::WriteAllText((Join-Path $updateStage '更新清单.json'), ($updateManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($true))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '发布工具\更新.ps1') -Destination $updateStage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '发布工具\更新.cmd') -Destination $updateStage
[IO.File]::WriteAllText((Join-Path $updateStage '升级说明.txt'), "完整解压后双击 更新.cmd，选择老版本轻启的主文件夹。`r`n支持运行库兼容的 0.1.10；仅更新轻启自身文件，保留用户软件和配置。更新前自动备份，失败自动尝试恢复。`r`n更新完成后，如旧列表将软件拆成了子目录，请将目录模式切换为 直接放软件。`r`n", [Text.UTF8Encoding]::new($true))
$updateZip = Join-Path $releaseRoot "LightLaunch-$version-update.zip"
Compress-Archive -LiteralPath $updateStage -DestinationPath $updateZip -CompressionLevel Optimal
$checksums = foreach ($file in @($binaryZip, $sourceZip, $updateZip)) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($file))"
}
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))
Write-Output "Release ready: $releaseRoot"
