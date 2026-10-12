@echo off
rem Starts uninstall.ps1 with administrator rights, from a copy outside the folder it removes.
copy /y "%~dp0uninstall.ps1" "%TEMP%\winawdl-uninstall.ps1" >nul
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%TEMP%\winawdl-uninstall.ps1\"'"
