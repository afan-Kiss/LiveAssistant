@echo off
setlocal
python "%~dp0publish-single.py"
if errorlevel 1 exit /b 1
python "%~dp0deploy-offline-status.py"
if errorlevel 1 exit /b 1
python "%~dp0e2e-newpc-verify.py"
exit /b %ERRORLEVEL%
