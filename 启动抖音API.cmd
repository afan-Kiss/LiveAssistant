@echo off
chcp 65001 >nul
cd /d "%~dp0"

if exist "抖音直播弹幕助手.exe" (
  echo 正在启动抖音直播弹幕助手（监控弹幕 / 发弹幕 / HTTP API）...
  start "douyin-api" /D "%~dp0" "抖音直播弹幕助手.exe"
  echo 已启动。健康检查: http://127.0.0.1:4723/api/health
  exit /b 0
)

if exist "douyin-danmaku.exe" (
  echo 未找到「抖音直播弹幕助手.exe」，改用 douyin-danmaku.exe -api
  start "douyin-api" /D "%~dp0" "douyin-danmaku.exe" -api
  echo 已启动。健康检查: http://127.0.0.1:4723/api/health
  exit /b 0
)

echo 当前目录找不到抖音直播弹幕助手.exe
echo 请先运行 scripts\sync-sidecars.py 同步 sidecar，或把 抖音直播弹幕助手.exe 放到本目录
pause
exit /b 1
