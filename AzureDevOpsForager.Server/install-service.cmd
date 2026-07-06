@echo off
REM ============================================================================
REM  Azure DevOps Forager - install the search Server as a Windows Service.
REM
REM  REASON (for an admin-approval request): registers the local search server so
REM  a self-hosted code-search index can be queried in the background and starts
REM  automatically on boot. Installing a Windows Service is a machine change, so
REM  it requires administrator rights.
REM
REM  HOW TO RUN:  right-click this file  ->  "Run as administrator".
REM ============================================================================
setlocal
set "SVCNAME=AzureDevOpsForagerServer"
set "EXEPATH=%~dp0AzureDevOpsForager.Server.exe"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo.
  echo   This script must be run as administrator.
  echo   Right-click install-service.cmd  ^->  "Run as administrator".
  echo.
  pause
  exit /b 1
)

if not exist "%EXEPATH%" (
  echo.
  echo   Could not find AzureDevOpsForager.Server.exe next to this script:
  echo   %EXEPATH%
  echo   Put install-service.cmd in the same folder as the Server exe.
  echo.
  pause
  exit /b 1
)

echo Installing service "%SVCNAME%"
echo   from: %EXEPATH%
echo.

sc create "%SVCNAME%" binPath= "%EXEPATH%" start= auto DisplayName= "Azure DevOps Forager Search Server"
sc description "%SVCNAME%" "Serves semantic + full-text code search over your indexed database."
sc start "%SVCNAME%"

echo.
echo Done. If you see SUCCESS / RUNNING above, the service is installed and started.
echo To remove it later, run uninstall-service.cmd as administrator.
pause
