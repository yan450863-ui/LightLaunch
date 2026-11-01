[CmdletBinding()]
param([string]$InstallRoot, [switch]$CheckOnly, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$manifestPath = Join-Path $PSScriptRoot '更新清单.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
try {
    if (-not $InstallRoot) {
        $picker = New-Object System.Windows.Forms.FolderBrowserDialog
        $picker.Description = '选择老版本轻启的主文件夹（内含 程序 文件夹）'
        $picker.ShowNewFolderButton = $false
        if ($picker.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { exit 0 }
        $InstallRoot = $picker.SelectedPath
    }
    $InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
    $program = Join-Path $InstallRoot '程序'
    foreach ($directory in @($PSScriptRoot, (Join-Path $PSScriptRoot '程序文件'), $InstallRoot, $program)) {
        for ($current = [IO.DirectoryInfo]::new($directory); $current; $current = $current.Parent) {
            if (-not $current.Exists -or ($current.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '更新位置不存在或包含目录链接。' }
        }
    }
    $oldDll = Join-Path $program 'LightLaunch.dll'
    $oldVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($oldDll).FileVersion
    $supportedVersions = if ($manifest.FromVersions) { @($manifest.FromVersions) } else { @($manifest.FromVersion) }
    $allowedVersions = @($supportedVersions | ForEach-Object { $_ + '.0' }) + @($manifest.Version + '.0')
    if ($oldVersion -notin $allowedVersions) { throw ('此升级包支持 '+($supportedVersions -join '、')+'，请使用对应版本或完整包更新并保留数据目录。') }
    if (@($manifest.Files).Count -ne 4 -or @($manifest.Runtime).Count -ne 5 -or @($manifest.Files.Name | Select-Object -Unique).Count -ne 4 -or @($manifest.Runtime.Name | Select-Object -Unique).Count -ne 5) { throw '更新清单缺少文件或存在重复项。' }
    foreach ($entry in @($manifest.Files) + @($manifest.Runtime)) {
        if ($entry.Name -notmatch '^[A-Za-z0-9.\-]+$' -or $entry.Name.Contains('..')) { throw '更新清单格式错误。' }
    }
    foreach ($entry in $manifest.Files) {
        if ($entry.Name -notin @('LightLaunch.exe','LightLaunch.dll','LightLaunch.deps.json','LightLaunch.runtimeconfig.json')) { throw '清单包含非程序文件。' }
        $source = Join-Path (Join-Path $PSScriptRoot '程序文件') $entry.Name
        if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '更新文件不能是链接。' }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.SHA256) { throw '升级包文件校验失败，请重新下载。' }
    }
    foreach ($entry in $manifest.Runtime) {
        if ($entry.Name -notin @('coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','System.Windows.Forms.dll')) { throw '运行库清单格式错误。' }
        $existing = Join-Path $program $entry.Name
        if ((Get-Item -LiteralPath $existing).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '现有运行库不能是链接。' }
        if ((Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash -ne $entry.SHA256) { throw '现有运行库与此升级包不兼容。未修改老版本，请改用完整包。' }
    }
    if ($manifest.Launcher) {
        if ($manifest.Launcher.Name -ne '轻启.exe') { throw '启动入口清单格式错误。' }
        $launcherSource = Join-Path $PSScriptRoot '轻启.exe'
        if ((Get-Item -LiteralPath $launcherSource).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '启动入口不能是链接。' }
        if ((Get-FileHash -LiteralPath $launcherSource -Algorithm SHA256).Hash -ne $manifest.Launcher.SHA256) { throw '启动入口校验失败。' }
        $launcherTarget = Join-Path $InstallRoot '轻启.exe'
        if ((Test-Path -LiteralPath $launcherTarget) -and ((Get-Item -LiteralPath $launcherTarget).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '现有启动入口不能是链接。' }
    }
    if ($CheckOnly) { Write-Output 'PASS upgrade source hashes, target version and runtime compatibility'; exit 0 }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -InstallRoot "' + $InstallRoot + '"'
        if ($NoLaunch) { $arguments += ' -NoLaunch' }
        $elevated = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments -Wait -PassThru
        exit $elevated.ExitCode
    }
    foreach ($process in Get-Process -Name LightLaunch -ErrorAction SilentlyContinue) {
        if ($process.Path -eq (Join-Path $program 'LightLaunch.exe')) {
            if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)) { throw '轻启仍有操作未完成。请正常退出后重新更新；未强制关闭。' }
        }
    }
    $backup = Join-Path $InstallRoot ('数据\升级备份\'+$oldVersion+'-'+[Guid]::NewGuid().ToString('N'))
    # Validate backup ancestry before creating directories or touching program files.
    for ($current = [IO.DirectoryInfo]::new($backup); $current; $current=$current.Parent) {
        if ($current.Exists -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '备份目录包含链接。' }
    }
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($entry in $manifest.Files) {
        $existing = Join-Path $program $entry.Name
        if ((Get-Item -LiteralPath $existing).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '现有程序文件不能是链接。' }
        Copy-Item -LiteralPath $existing -Destination $backup
    }
    $launcherWasPresent = $false
    if ($manifest.Launcher -and (Test-Path -LiteralPath $launcherTarget)) { Copy-Item -LiteralPath $launcherTarget -Destination $backup; $launcherWasPresent = $true }
    $changed = @()
    $launcherChanged = $false
    try {
        foreach ($entry in $manifest.Files) {
            $changed += $entry.Name
            Copy-Item -LiteralPath (Join-Path (Join-Path $PSScriptRoot '程序文件') $entry.Name) -Destination (Join-Path $program $entry.Name) -Force
            if ((Get-FileHash -LiteralPath (Join-Path $program $entry.Name) -Algorithm SHA256).Hash -ne $entry.SHA256) { throw '更新后文件校验失败。' }
        }
        if ($manifest.Launcher) {
            $launcherChanged = $true
            Copy-Item -LiteralPath $launcherSource -Destination $launcherTarget -Force
            if ((Get-FileHash -LiteralPath $launcherTarget -Algorithm SHA256).Hash -ne $manifest.Launcher.SHA256) { throw '更新后启动入口校验失败。' }
        }
    } catch {
        $failure = $_
        foreach ($name in $changed) { Copy-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $program $name) -Force }
        if ($launcherChanged) { if ($launcherWasPresent) { Copy-Item -LiteralPath (Join-Path $backup '轻启.exe') -Destination $launcherTarget -Force } elseif (Test-Path -LiteralPath $launcherTarget) { Remove-Item -LiteralPath $launcherTarget -Force } }
        throw ('更新失败，已恢复原程序：'+$failure.Exception.Message)
    }
    [IO.File]::WriteAllText((Join-Path $backup '恢复说明.txt'), "退出轻启后，将此目录的四个 LightLaunch 程序文件复制回主文件夹的 程序 目录。如本目录含轻启.exe，将它复制回主文件夹；若更新前没有轻启.exe，可删除更新中新加的入口。软件和配置未被升级包改动。", [Text.UTF8Encoding]::new($true))
    if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $program 'LightLaunch.exe') -WorkingDirectory $program }
    Write-Output ('更新成功：'+$manifest.Version+'；原程序备份：'+$backup)
} catch {
    if ($CheckOnly) { Write-Error $_; exit 1 }
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, '轻启更新未完成', 'OK', 'Warning') | Out-Null
    exit 1
}
