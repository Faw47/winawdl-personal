@echo off
rem Install machine files as administrator, then register Explorer for this user.
setlocal
set "WINAWDL_INSTALL_SCRIPT=%~dp0install.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',([char]34 + $env:WINAWDL_INSTALL_SCRIPT + [char]34),'-SkipExplorer' -Wait -PassThru; exit $p.ExitCode"
if errorlevel 1 exit /b %errorlevel%
"%ProgramW6432%\winawdl\winawdl.exe" --explorer-install
exit /b %errorlevel%
