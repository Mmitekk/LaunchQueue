@echo off
setlocal
dotnet build "%~dp0src\LaunchQueue.csproj" -c Release -o "%~dp0app" --nologo
if errorlevel 1 (
  pause
  exit /b 1
)
pause
