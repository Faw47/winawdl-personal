@echo off
rem Starts uninstall.ps1 with administrator rights, from a copy outside the folder it removes.
rem With /yes it asks nothing: that is how Windows' own Uninstall button starts it, having asked.
set "ASKED="
if /i "%~1"=="/yes" set "ASKED=,'-Yes'"
copy /y "%~dp0uninstall.ps1" "%TEMP%\winawdl-uninstall.ps1" >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%TEMP%\winawdl-uninstall.ps1\"'%ASKED%"
