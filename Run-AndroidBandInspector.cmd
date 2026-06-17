@echo off
setlocal

set "APP_DIR=%~dp0"
set "EXE=%APP_DIR%AndroidBandInspector.exe"
set "OUT=%APP_DIR%reports"

if not exist "%EXE%" (
  echo AndroidBandInspector.exe was not found in %APP_DIR%
  exit /b 1
)

"%EXE%" --out "%OUT%" %*
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo Exit code: %EXIT_CODE%
echo Reports directory: %OUT%
pause
exit /b %EXIT_CODE%
