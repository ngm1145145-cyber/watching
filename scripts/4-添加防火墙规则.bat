@echo off
chcp 65001 >nul
title Watching 防火墙规则
setlocal

rem ============================================================
rem  放行 Watching 服务端端口（默认 8899），让手机/其它电脑能连上来。
rem  需要管理员权限，会弹出 UAC 确认框。
rem ============================================================

net session >nul 2>&1
if errorlevel 1 (
    echo 需要管理员权限，正在请求提权...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
    exit /b
)

set PORT=8899
if not "%~1"=="" set PORT=%~1

echo 正在为 TCP 端口 %PORT% 添加入站放行规则...
netsh advfirewall firewall delete rule name="Watching Server (TCP %PORT%)" >nul 2>&1
netsh advfirewall firewall add rule name="Watching Server (TCP %PORT%)" dir=in action=allow protocol=TCP localport=%PORT%

echo.
echo 完成。当前规则：
netsh advfirewall firewall show rule name="Watching Server (TCP %PORT%)" | findstr /c:"规则名称" /c:"Rule Name" /c:"本地端口" /c:"LocalPort" /c:"已启用" /c:"Enabled"
echo.
pause
