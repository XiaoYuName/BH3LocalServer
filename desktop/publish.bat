@echo off
setlocal
cd /d "%~dp0"
call "..\server\publish.bat" %*
exit /b %errorlevel%
