@echo off
setlocal
cd /d "%~dp0"
dotnet build BH3.slnx -c Release --nologo
exit /b %errorlevel%
