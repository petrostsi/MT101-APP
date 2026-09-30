@echo off
setlocal EnableExtensions
rem ---------------------------------------------------------------------------
rem  Builds dist\portable\SwiftBatchProcessorApp.exe: self-contained, single file,
rem  runs from any writable folder. No admin rights needed.
rem    publish.bat               run the tests, then publish
rem    publish.bat --skip-tests  publish only
rem ---------------------------------------------------------------------------
cd /d "%~dp0"

set "DOTNET="
for %%D in ("%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" "%USERPROFILE%\dotnet\dotnet.exe") do (
    if not defined DOTNET if exist "%%~D" (
        "%%~D" --list-sdks 2>nul | findstr /r "^[89]\. ^1[0-9]\." >nul && set "DOTNET=%%~D"
    )
)
if not defined DOTNET (
    where dotnet >nul 2>&1 && (dotnet --list-sdks 2>nul | findstr /r "^[89]\. ^1[0-9]\." >nul && set "DOTNET=dotnet")
)
if not defined DOTNET (
    echo No .NET 8 SDK was found.
    echo Run setup-dotnet-sdk.bat first ^(per-user install, no admin rights^).
    pause
    exit /b 1
)
echo Using: %DOTNET%
"%DOTNET%" --version

if /i not "%~1"=="--skip-tests" (
    echo.
    echo === Tests ===
    "%DOTNET%" test tests\SwiftBatchProcessor.Tests\SwiftBatchProcessor.Tests.csproj -c Release || goto :fail
)

echo.
echo === Publish ===
"%DOTNET%" publish src\SwiftBatchProcessorApp\SwiftBatchProcessorApp.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none -p:DebugSymbols=false -o dist\portable || goto :fail

rem Organisation defaults (git-ignored) travel with the exe so a new PC seeds its settings.
if exist SwiftBatch.defaults.json copy /y SwiftBatch.defaults.json dist\portable\ >nul

echo.
echo Done: %CD%\dist\portable\SwiftBatchProcessorApp.exe
pause
exit /b 0

:fail
echo.
echo *** BUILD FAILED ***
pause
exit /b 1
