@echo off
setlocal
cd /d "%~dp0"
set "PY=C:\Users\Lumino Game 04\.workbuddy-ai\binaries\python\envs\default\Scripts\python.exe"
if not exist "%PY%" (
  echo Python not found:
  echo %PY%
  pause
  exit /b 1
)
"%PY%" launcher.py
if errorlevel 1 pause
