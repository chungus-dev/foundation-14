@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0translate-all-ru.ps1" %*
exit /b %ERRORLEVEL%
