@echo off
cd /d "%~dp0"
if not exist "%~dp0dist\Hygieia.exe" (
  echo Hygieia.exe was not found. Publish it with: dotnet publish src\Hygieia\Hygieia.csproj -c Release -o dist
  pause
  exit /b 1
)
start "" "%~dp0dist\Hygieia.exe"
