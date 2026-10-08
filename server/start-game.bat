@echo off
setlocal
cd /d "%~dp0"
if exist "dist\win-x64-1.2.4\Launcher\BH3.Launcher.exe" (
  start "" "%~dp0dist\win-x64-1.2.4\Launcher\BH3.Launcher.exe"
  exit /b 0
)
if not exist "dist\win-x64\Launcher\BH3.Launcher.exe" (
  call publish.bat
  if errorlevel 1 exit /b 1
)
start "" "%~dp0dist\win-x64\Launcher\BH3.Launcher.exe"
