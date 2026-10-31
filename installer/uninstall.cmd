@echo off
setlocal
rem Remove this user's Explorer and startup entries before UAC changes accounts.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" -UserOnly
if errorlevel 1 exit /b %errorlevel%
rem The elevated script removes machine files from a copy outside that folder.
rem With /yes it asks nothing: that is how Windows' own Uninstall button starts it.
set "WINAWDL_UNINSTALL_YES="
if /i "%~1"=="/yes" set "WINAWDL_UNINSTALL_YES=1"
copy /y "%~dp0uninstall.ps1" "%TEMP%\winawdl-uninstall.ps1" >nul
if errorlevel 1 exit /b %errorlevel%
set "WINAWDL_UNINSTALL_SCRIPT=%TEMP%\winawdl-uninstall.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$childArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',([char]34 + $env:WINAWDL_UNINSTALL_SCRIPT + [char]34)); if ($env:WINAWDL_UNINSTALL_YES) { $childArgs += '-Yes' }; $p = Start-Process powershell -Verb RunAs -ArgumentList $childArgs -Wait -PassThru; exit $p.ExitCode"
exit /b %errorlevel%
