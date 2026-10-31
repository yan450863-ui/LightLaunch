[CmdletBinding()]
param([string]$UpdateZip, [string]$InstallRoot)
$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot ('记录\中文 更新入口 '+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sourceBatch = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '源码\发布工具\更新.cmd'
$checks = @()
function Run-Batch([string]$Batch, [string]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $env:SystemRoot 'System32\cmd.exe'
    $start.Arguments = '/d /s /c ""'+$Batch+'" '+$Arguments+'"'
    $start.WorkingDirectory = Split-Path $Batch -Parent
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.WriteLine('')
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { throw 'Batch entry did not return within 30 seconds.' }
    $result = @{ExitCode=$process.ExitCode;Output=$stdout.GetAwaiter().GetResult();Error=$stderr.GetAwaiter().GetResult()}
    $process.Dispose()
    return $result
}
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw $Name }
    $script:checks += $Name
    Write-Output ('PASS '+$Name)
}
$bytes = [IO.File]::ReadAllBytes($sourceBatch)
$text = [Text.Encoding]::UTF8.GetString($bytes)
Check (-not $text.Contains("`n") -or -not [regex]::IsMatch($text, '(?<!\r)\n')) 'Source batch uses CRLF'
$batch = Join-Path $output '更新.cmd'
Copy-Item -LiteralPath $sourceBatch -Destination $batch
$stub = @'
param([string]$Value, [int]$Result = 0)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'reached.txt'), $Value, [Text.UTF8Encoding]::new($true))
exit $Result
'@
[IO.File]::WriteAllText((Join-Path $output '更新.ps1'), $stub, [Text.UTF8Encoding]::new($true))
$value = '中文 空格 & (括号)'
$run = Run-Batch $batch ('-Value "'+$value+'"')
Check ($run.ExitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $output 'reached.txt'))) 'Actual cmd.exe entry reaches Windows PowerShell'
Check ([IO.File]::ReadAllText((Join-Path $output 'reached.txt')) -eq $value) 'Unicode paths and quoted arguments survive the entry point'
$failed = Run-Batch $batch '-Result 7'
Check ($failed.ExitCode -eq 7) 'Failure exit code survives pause'

if ($UpdateZip) {
    $unpacked = Join-Path $output '解压 升级包'
    Expand-Archive -LiteralPath ([IO.Path]::GetFullPath($UpdateZip)) -DestinationPath $unpacked
    $batches = @(Get-ChildItem -LiteralPath $unpacked -Filter '更新.cmd' -Recurse -File)
    Check ($batches.Count -eq 1) 'ZIP contains one unambiguous update entry'
    $shipped = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($batches[0].FullName))
    Check (-not [regex]::IsMatch($shipped, '(?<!\r)\n')) 'Actual ZIP batch uses CRLF'
    if ($InstallRoot) {
        $manifest = Get-Content -LiteralPath (Join-Path $batches[0].DirectoryName '更新清单.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $before = @{}
        foreach ($entry in $manifest.Files) {
            $file = Join-Path ([IO.Path]::GetFullPath($InstallRoot)) ('程序\'+$entry.Name)
            $before[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        }
        $actual = Run-Batch $batches[0].FullName ('-InstallRoot "'+[IO.Path]::GetFullPath($InstallRoot)+'" -CheckOnly')
        Check ($actual.ExitCode -eq 0 -and $actual.Output.Contains('PASS upgrade source hashes')) 'Extracted CMD entry completes the real updater compatibility check'
        foreach ($file in $before.Keys) { Check ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $before[$file]) 'Read-only entry check preserves existing program files' }
    }
}
@{Passed=$checks.Count;Checks=$checks;Output=$output} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
Write-Output ('Entry checks: '+$checks.Count+' passed.')
