@echo off
rem Builds LoopLagLimiter (plugin + preloader patcher) against your own game + r2modman profile,
rem installs both into that profile, and makes the Thunderstore zip.
rem Needs the .NET SDK. Folder paths are set in the .csproj files.
setlocal
cd /d "%~dp0"
dotnet build LoopLagLimiter.csproj -c Release || goto :fail
dotnet build patcher\LoopLagLimiter.Patcher.csproj -c Release || goto :fail
set "PROFILE=%APPDATA%\r2modmanPlus-local\RiskOfRain2\profiles\Quality\BepInEx"
if not exist "%PROFILE%\plugins\Videowiz92-LoopLagLimiter" mkdir "%PROFILE%\plugins\Videowiz92-LoopLagLimiter"
if not exist "%PROFILE%\patchers\Videowiz92-LoopLagLimiter" mkdir "%PROFILE%\patchers\Videowiz92-LoopLagLimiter"
copy /y "bin\Release\LoopLagLimiter.dll" "%PROFILE%\plugins\Videowiz92-LoopLagLimiter\" >nul
copy /y "patcher\bin\Release\LoopLagLimiter.Patcher.dll" "%PROFILE%\patchers\Videowiz92-LoopLagLimiter\" >nul
echo Installed plugin and patcher into %PROFILE%
if not exist "package\plugins" mkdir "package\plugins"
if not exist "package\patchers" mkdir "package\patchers"
copy /y "bin\Release\LoopLagLimiter.dll" "package\plugins\" >nul
copy /y "patcher\bin\Release\LoopLagLimiter.Patcher.dll" "package\patchers\" >nul
powershell -NoProfile -Command "Compress-Archive -Force -Path 'package\*' -DestinationPath 'LoopLagLimiter-thunderstore.zip'"
echo Thunderstore upload zip: %CD%\LoopLagLimiter-thunderstore.zip
pause
exit /b 0
:fail
echo BUILD FAILED
pause
exit /b 1
