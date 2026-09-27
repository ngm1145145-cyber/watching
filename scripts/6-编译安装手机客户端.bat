@echo off
chcp 65001 >nul
title 编译安装 Watching 手机客户端
setlocal
cd /d "%~dp0"

rem ============================================================
rem  一键编译 APK 并安装到已连接的安卓手机（需开启 USB 调试）
rem  没有手机连接时会只编译，不安装。
rem ============================================================

set SDK=H:\ds-harness\android-sdk
set ADB=%SDK%\platform-tools\adb.exe

echo ============ 1. 编译 APK ============
powershell -ExecutionPolicy Bypass -File ".\build-apk.ps1"
if errorlevel 1 (
    echo.
    echo 编译失败。
    pause
    exit /b 1
)

echo.
echo ============ 2. 检查手机是否连接 ============
if not exist "%ADB%" (
    echo 找不到 adb：%ADB%
    echo 请手动把 dist\apk\WatchingMobile-1.0.0.apk 拷到手机里安装。
    pause
    exit /b 0
)

"%ADB%" devices
echo.
echo 如果上面列出了设备，按任意键安装；否则请用数据线连上手机并打开「USB 调试」。
pause >nul

"%ADB%" install -r "dist\apk\WatchingMobile-1.0.0.apk"
echo.
echo 安装完成。手机桌面上会出现「Watching」图标。
pause
