@echo off
pwsh -NoProfile -File "%~dp0upload-missing-packages.ps1" %*
exit /b %ERRORLEVEL%
