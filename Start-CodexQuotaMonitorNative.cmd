@echo off
setlocal

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "EXE=%ROOT%\publish\win-x64\CodexQuotaMonitor.Wpf.exe"
set "DLL=%ROOT%\publish\win-x64\CodexQuotaMonitor.Wpf.dll"
set "PROJECT=%ROOT%\src\CodexQuotaMonitor.Wpf\CodexQuotaMonitor.Wpf.csproj"
set "CODEX_QUOTA_MONITOR_NATIVE_HOME=%ROOT%"

set "CONSOLE_MODE=0"
for %%A in (%*) do (
    if /I "%%~A"=="--check" set "CONSOLE_MODE=1"
    if /I "%%~A"=="--once" set "CONSOLE_MODE=1"
)

if exist "%EXE%" (
    if "%CONSOLE_MODE%"=="1" (
        if exist "%DLL%" (
            dotnet "%DLL%" %*
        ) else (
            "%EXE%" %*
        )
        exit /b %ERRORLEVEL%
    )
    start "" "%EXE%" %*
    exit /b 0
)

dotnet run --project "%PROJECT%" -- %*
exit /b %ERRORLEVEL%
