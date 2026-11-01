@echo off
setlocal
chcp 65001 >nul
set "PSModulePath=%SystemRoot%\System32\WindowsPowerShell\v1.0\Modules"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0更新.ps1" %*
set "LightLaunchExitCode=%errorlevel%"
if not "%LightLaunchExitCode%"=="0" pause
exit /b %LightLaunchExitCode%
