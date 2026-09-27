@echo off
chcp 65001 >nul
title Watching 服务端
setlocal
cd /d "%~dp0"

rem ============================================================
rem  Watching 服务端：静默后台运行，只在托盘显示一个小图标。
rem  被查看的电脑上运行这个脚本。
rem ============================================================

if exist "..\Watching.exe" (
    set EXE=..\Watching.exe
) else (
    set EXE=Watching.exe
)

echo 正在启动 Watching 服务端...
"%EXE%" --server %*

if errorlevel 1 (
    echo.
    echo 启动失败。请确认 Watching.exe 存在，且 8899 端口没有被其它程序占用。
    pause
)
