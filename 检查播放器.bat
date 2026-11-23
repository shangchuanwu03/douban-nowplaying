@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在读取系统媒体控制（SMTC）…
echo 请在目标播放器「正在播放」的状态下运行本检查。
echo.
node scripts\check-players.js
echo.
pause
