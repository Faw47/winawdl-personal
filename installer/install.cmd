@echo off
rem Starts install.ps1 with administrator rights. Windows asks for them first.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%~dp0install.ps1\"'"
