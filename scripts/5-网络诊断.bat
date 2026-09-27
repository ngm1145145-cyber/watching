@echo off
chcp 65001 >nul
title Watching 网络诊断
setlocal

rem ============================================================
rem  排查「客户端连不上服务端」的常见原因
rem ============================================================

echo ============ Watching 网络诊断 ============
echo.

echo [1] 本机 IPv4 地址（客户端应该填这些）：
ipconfig | findstr /c:"IPv4"
echo.

echo [2] 服务端是否在监听 8899？
netstat -ano | findstr ":8899"
if errorlevel 1 (
    echo    没有发现监听 —— 服务端可能没启动，或者端口被改了。
    echo    请先运行「1-启动服务端.bat」，并在托盘图标右键确认端口。
) else (
    echo    正常：有 LISTENING 记录。
)
echo.

echo [3] 本机自测（应输出 200/101 等 HTTP 响应）：
powershell -NoProfile -Command "try { $r = Invoke-WebRequest -Uri 'http://127.0.0.1:8899/health' -UseBasicParsing -TimeoutSec 5; Write-Host ('    OK ' + $r.StatusCode + ' ' + $r.Content) } catch { Write-Host ('    失败：' + $_.Exception.Message) }"
echo.

echo [4] 防火墙里是否有 Watching 规则？
netsh advfirewall firewall show rule name="Watching Server (TCP 8899)" | findstr /c:"规则名称" /c:"Rule Name"
if errorlevel 1 (
    echo    没有找到规则。若手机 / 其它电脑连不上，请以管理员身份运行「4-添加防火墙规则.bat」。
) else (
    echo    已存在规则。
)
echo.

echo [5] 客户端连接时若提示「服务端未开启远程控制」，说明远程控制是关闭的（默认关闭，属正常）。
echo     需要在服务端托盘右键 → 设置 → 打开「允许客户端远程控制」（需要先设置密码）。
echo.
pause
