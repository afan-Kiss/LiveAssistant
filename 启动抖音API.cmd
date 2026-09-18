@echo off
chcp 65001 >nul
cd /d "%~dp0"

if exist "cdp-danmaku.exe" (
  echo 正在启动抖音 CDP ...
  start "douyin-cdp" /D "%~dp0" "cdp-danmaku.exe"
  echo 已启动。健康检查: http://127.0.0.1:17891/api/health
  exit /b 0
)

if exist "sidecars\douyin-cdp\cdp-danmaku.exe" (
  echo 正在启动 sidecars\douyin-cdp\cdp-danmaku.exe ...
  start "douyin-cdp" /D "%~dp0sidecars\douyin-cdp" "cdp-danmaku.exe"
  echo 已启动。健康检查: http://127.0.0.1:17891/api/health
  exit /b 0
)

echo 当前目录找不到 cdp-danmaku.exe
echo 请把抖音 CDP 程序放到本目录或 sidecars\douyin-cdp\
pause
