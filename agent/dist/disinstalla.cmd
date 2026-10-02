@echo off
rem TIA MCP Agent - disinstallazione (anche da Programmi e funzionalita')
setlocal
set "EXE=%ProgramFiles%\TiaAgent\TiaAgent.exe"
if not exist "%EXE%" set "EXE=%~dp0TiaAgent.exe"
"%EXE%" uninstall %*
exit /b %errorlevel%
