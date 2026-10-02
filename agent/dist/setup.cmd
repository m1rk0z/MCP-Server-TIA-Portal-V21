@echo off
rem ==========================================================================
rem  TIA MCP Agent - installazione sulla macchina con TIA Portal (VM)
rem
rem  Uso:   setup.cmd                       installazione guidata
rem         setup.cmd --silent [--port 8766] [--access-mode read-only^|read-write]
rem                            [--allow 192.168.56.1] [--new-token]
rem ==========================================================================
setlocal
cd /d "%~dp0"

rem no "pause" in unattended installs
set "SILENT="
echo %* | "%SystemRoot%\System32\find.exe" /i "--silent" >nul && set "SILENT=1"

if not exist "%~dp0TiaAgent.exe" (
  echo TiaAgent.exe non trovato. Estrarre TUTTO lo zip in una cartella prima di eseguire setup.cmd.
  if not defined SILENT pause
  exit /b 1
)

rem --- .NET Framework 4.8 richiesto (Release >= 528040), come per TIA Portal Openness
set "NETREL="
for /f "tokens=3" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| "%SystemRoot%\System32\find.exe" "Release"') do set "NETREL=%%a"
if not defined NETREL goto :nonet
set /a NETREL_DEC=%NETREL% 2>nul
if not defined NETREL_DEC goto :nonet
if %NETREL_DEC% LSS 528040 goto :nonet

rem TiaAgent.exe chiede i diritti di amministratore, aspetta l'installazione e ne restituisce il codice
"%~dp0TiaAgent.exe" install %*
exit /b %errorlevel%

:nonet
echo.
echo  Su questa macchina manca .NET Framework 4.8, necessario per l'agente.
echo  Scaricarlo da https://dotnet.microsoft.com/download/dotnet-framework/net48
echo.
if not defined SILENT pause
exit /b 1
