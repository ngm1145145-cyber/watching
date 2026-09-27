@echo off
chcp 65001 >nul
title Watching 手机连接方式
setlocal
cd /d "%~dp0"

rem ============================================================
rem  显示手机客户端的访问地址（手机和电脑要在同一个 WiFi / 局域网）
rem ============================================================

echo.
echo ============ 手机客户端连接方式 ============
echo.
echo  1. 确认手机和这台电脑连在同一个 WiFi（局域网）。
echo  2. 在手机浏览器里输入下面任意一个地址：
echo.

for /f "tokens=2 delims=:" %%a in ('ipconfig ^| findstr /c:"IPv4"') do (
    for /f "tokens=* delims= " %%b in ("%%a") do echo        http://%%b:8899/
)

echo.
echo  3. 进入页面后点「开始观看」；横屏观看更舒服。
echo     点一次画面显示控制条，双击画面切换全屏。
echo.
echo  提示：iPhone 请用 Safari 打开；安卓用 Chrome / 自带浏览器都可以。
echo        如果打不开，请在本机以管理员身份运行「4-添加防火墙规则.bat」。
echo.
pause
