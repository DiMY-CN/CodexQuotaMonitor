@echo off
setlocal

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "EXE=%ROOT%\publish\win-x64-self-contained\CodexQuotaMonitor.Wpf.exe"
set "LEGACY_EXE=%ROOT%\publish\win-x64\CodexQuotaMonitor.Wpf.exe"
set "DLL=%ROOT%\publish\win-x64\CodexQuotaMonitor.Wpf.dll"
set "PROJECT=%ROOT%\src\CodexQuotaMonitor.Wpf\CodexQuotaMonitor.Wpf.csproj"
set "FALLBACK_DLL=%ROOT%\src\CodexQuotaMonitor.Wpf\bin\Release\net8.0-windows\CodexQuotaMonitor.Wpf.dll"
set "CODEX_QUOTA_MONITOR_NATIVE_HOME=%ROOT%"

set "CONSOLE_MODE=0"
for %%A in (%*) do (
    if /I "%%~A"=="--check" set "CONSOLE_MODE=1"
    if /I "%%~A"=="--once" set "CONSOLE_MODE=1"
)

if exist "%EXE%" (
    if "%CONSOLE_MODE%"=="1" (
        "%EXE%" %*
        exit /b %ERRORLEVEL%
    )
    start "" "%EXE%" %*
    exit /b 0
)

if exist "%LEGACY_EXE%" (
    if "%CONSOLE_MODE%"=="1" (
        if exist "%DLL%" (
            dotnet "%DLL%" %*
        ) else (
            "%LEGACY_EXE%" %*
        )
        exit /b %ERRORLEVEL%
    )
    start "" "%LEGACY_EXE%" %*
    exit /b 0
)

if "%CONSOLE_MODE%"=="1" (
    dotnet build "%PROJECT%" -c Release -nologo >nul
    if errorlevel 1 exit /b %ERRORLEVEL%
    dotnet "%FALLBACK_DLL%" %*
    exit /b %ERRORLEVEL%
)

dotnet run --project "%PROJECT%" -- %*
exit /b %ERRORLEVEL%
