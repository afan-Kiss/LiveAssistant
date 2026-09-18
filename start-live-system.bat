@echo off
chcp 65001 >nul
cd /d "%~dp0"
title Start Live System

set "EXE=%~dp0StartLiveSystem\bin\Release\net8.0-windows\StartLiveSystem.exe"
if not exist "%EXE%" set "EXE=%~dp0StartLiveSystem\bin\Debug\net8.0-windows\StartLiveSystem.exe"
if not exist "%EXE%" set "EXE=%~dp0publish\StartLiveSystem\StartLiveSystem.exe"
if not exist "%EXE%" set "EXE=%~dp0StartLiveSystem.exe"

if exist "%EXE%" (
  "%EXE%" --config "%~dp0launcher.json" %*
  set ERR=%ERRORLEVEL%
  if not "%ERR%"=="0" (
    echo.
    echo 启动失败，退出码 %ERR%
    pause
  )
  exit /b %ERR%
)

where dotnet >nul 2>&1
if errorlevel 1 (
  echo 未找到 StartLiveSystem.exe，且本机没有 dotnet。
  pause
  exit /b 1
)

echo 正在编译 StartLiveSystem ...
dotnet build "%~dp0StartLiveSystem\StartLiveSystem.csproj" -c Release --nologo -v q
if errorlevel 1 (
  echo 编译失败
  pause
  exit /b 1
)

set "EXE=%~dp0StartLiveSystem\bin\Release\net8.0-windows\StartLiveSystem.exe"
"%EXE%" --config "%~dp0launcher.json" %*
set ERR=%ERRORLEVEL%
if not "%ERR%"=="0" (
  echo.
  echo 启动失败，退出码 %ERR%
  pause
)
exit /b %ERR%
