@echo off
REM Avvia build.ps1 senza dover cambiare la policy di esecuzione di PowerShell.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
