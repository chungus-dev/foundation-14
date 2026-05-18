@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0translate-scp-en.ps1" %*
exit /b %ERRORLEVEL%
