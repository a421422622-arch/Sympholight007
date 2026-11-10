@echo off
cd /d "%~dp0"
dotnet ShowControl.dll
if errorlevel 1 pause
