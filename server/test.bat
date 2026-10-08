@echo off
setlocal
cd /d "%~dp0"
call build.bat
if errorlevel 1 exit /b %errorlevel%
for %%P in (Protocol Game Persistence Server) do (
  dotnet "tests\BH3.%%P.Tests\bin\Release\net10.0\BH3.%%P.Tests.dll" -noLogo
  if errorlevel 1 exit /b 1
)
exit /b 0
