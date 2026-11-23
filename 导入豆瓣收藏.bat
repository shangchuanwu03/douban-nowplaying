@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在从豆瓣拉取「我听过的音乐」并导入本地…
echo 需要应用处于运行状态（先双击 启动.bat）。
echo.
node scripts\import-collection.js
echo.
pause
