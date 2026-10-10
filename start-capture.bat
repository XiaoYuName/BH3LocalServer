@echo off
setlocal
cd /d "%~dp0"
if not exist "capture\dist\BH3Capture-1.0.0-win-x64-single\BH3Capture.exe" (
  echo Capture tool has not been published. Run capture\publish.ps1 first.
  pause
  exit /b 1
)
start "" "%~dp0capture\dist\BH3Capture-1.0.0-win-x64-single\BH3Capture.exe"
