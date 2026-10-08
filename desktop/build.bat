@echo off
setlocal
cd /d "%~dp0"
dotnet build "src\BH3.Launcher\BH3.Launcher.csproj" -c Release --nologo
if errorlevel 1 exit /b %errorlevel%
dotnet "src\BH3.Launcher\bin\Release\net10.0-windows\BH3.Launcher.dll" --self-test --report "verification\self-test.json"
exit /b %errorlevel%
