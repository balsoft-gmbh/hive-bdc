@echo off
REM Hive BDC, by Balsoft - service uninstaller (self-elevating)
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
cd /d "%~dp0"
"%~dp0HiveBDC.exe" uninstall
echo.
pause
