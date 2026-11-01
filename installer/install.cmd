@echo off
rem Install machine files as administrator, then register Explorer for this user.
setlocal
set "WINAWDL_INSTALL_SCRIPT=%~dp0install.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',([char]34 + $env:WINAWDL_INSTALL_SCRIPT + [char]34),'-SkipExplorer' -Wait -PassThru; exit $p.ExitCode"
if errorlevel 1 exit /b %errorlevel%
"%ProgramW6432%\winawdl\winawdl.exe" --explorer-install
if errorlevel 1 exit /b %errorlevel%
"%ProgramW6432%\winawdl\winawdl.exe" --notifications-install
if errorlevel 1 exit /b %errorlevel%
rem AirDrop in the Share sheet is a convenience: without it, winawdl works as before.
"%ProgramW6432%\winawdl\winawdl.exe" --share-install
if errorlevel 1 echo AirDrop could not be added to the Share sheet for this account.
exit /b 0
