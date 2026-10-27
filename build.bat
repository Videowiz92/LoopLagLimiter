@echo off
rem Builds LoopLagLimiter against your own game + r2modman profile, installs it, and makes the Thunderstore zip.
rem Needs the .NET SDK. Folder paths are set in LoopLagLimiter.csproj.
setlocal
cd /d "%~dp0"
dotnet build LoopLagLimiter.csproj -c Release
if errorlevel 1 (
  echo BUILD FAILED
  pause
  exit /b 1
)
set "DEST=%APPDATA%\r2modmanPlus-local\RiskOfRain2\profiles\Quality\BepInEx\plugins\Videowiz92-LoopLagLimiter"
if not exist "%DEST%" mkdir "%DEST%"
copy /y "bin\Release\LoopLagLimiter.dll" "%DEST%\" >nul
echo Installed to %DEST%
copy /y "bin\Release\LoopLagLimiter.dll" "package\" >nul
powershell -NoProfile -Command "Compress-Archive -Force -Path 'package\*' -DestinationPath 'LoopLagLimiter-thunderstore.zip'"
echo Thunderstore upload zip: %CD%\LoopLagLimiter-thunderstore.zip
pause
