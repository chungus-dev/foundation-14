@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0translate-scp-ru.ps1" %*
exit /b %ERRORLEVEL%
