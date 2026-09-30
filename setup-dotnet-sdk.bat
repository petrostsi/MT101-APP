@echo off
setlocal EnableExtensions
rem ---------------------------------------------------------------------------
rem  Installs the .NET 8 SDK for the CURRENT USER only (no admin rights):
rem  %LOCALAPPDATA%\Microsoft\dotnet  -- publish.bat finds it there.
rem  Uses Microsoft's official dotnet-install.ps1 script.
rem ---------------------------------------------------------------------------
set "TARGET=%LOCALAPPDATA%\Microsoft\dotnet"
set "SCRIPT=%TEMP%\dotnet-install.ps1"

echo Downloading dotnet-install.ps1 ...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile '%SCRIPT%'" || goto :fail

echo Installing the .NET 8 SDK into %TARGET% ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Channel 8.0 -InstallDir "%TARGET%" -NoPath || goto :fail

echo.
"%TARGET%\dotnet.exe" --list-sdks
echo.
echo SDK ready. Now run publish.bat.
pause
exit /b 0

:fail
echo.
echo *** The SDK could not be installed (proxy / download blocked?). ***
pause
exit /b 1
