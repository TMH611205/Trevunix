@echo off
rem Builds Trevunix.exe (single file, runs without .NET installed) next to this script.
rem Local builds never update themselves from the public release.
setlocal
cd /d "%~dp0"
taskkill /im Trevunix.exe /f >nul 2>&1
dotnet publish src\Trevunix\Trevunix.csproj -c Release -r win-x64 --nologo -o "%TEMP%\trevunix-build" ^
  -p:LocalBuild=true -p:SelfContained=true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:PublishReadyToRun=true -p:DebugType=none
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
copy /y "%TEMP%\trevunix-build\Trevunix.exe" "%~dp0Trevunix.exe" >nul
rmdir /s /q "%TEMP%\trevunix-build"
echo.
echo Done: %~dp0Trevunix.exe
pause
