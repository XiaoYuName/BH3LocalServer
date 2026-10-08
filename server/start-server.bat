@echo off
setlocal
cd /d "%~dp0"
if not exist "dist\win-x64\Server\BH3.Server.exe" (
  echo Run publish.bat first.
  pause
  exit /b 1
)
"dist\win-x64\Server\BH3.Server.exe"
if errorlevel 1 pause
