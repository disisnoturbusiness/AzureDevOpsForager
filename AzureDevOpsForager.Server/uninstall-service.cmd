@echo off
REM ============================================================================
REM  Azure DevOps Forager - remove the search Server Windows Service.
REM  REASON (for admin-approval): unregisters the background search service.
REM  HOW TO RUN:  right-click this file  ->  "Run as administrator".
REM ============================================================================
setlocal
set "SVCNAME=AzureDevOpsForagerServer"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo.
  echo   This script must be run as administrator.
  echo   Right-click uninstall-service.cmd  ^->  "Run as administrator".
  echo.
  pause
  exit /b 1
)

sc stop "%SVCNAME%"
sc delete "%SVCNAME%"

echo.
echo Done. Service "%SVCNAME%" removed.
pause
