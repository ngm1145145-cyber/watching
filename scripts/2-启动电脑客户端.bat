@echo off
chcp 65001 >nul
title Watching 电脑客户端
setlocal
cd /d "%~dp0"

rem ============================================================
rem  Watching 电脑客户端（查看端）
rem  用法：
rem    1-启动服务端.bat 之后，双击本脚本，或把服务端 IP 作为参数
rem     2-启动电脑客户端.bat 192.168.1.8
rem     2-启动电脑客户端.bat 192.168.1.8:8899
rem  连接成功后按 F11 进入全屏。
rem ============================================================

if exist "..\Watching.exe" (
    set EXE=..\Watching.exe
) else (
    set EXE=Watching.exe
)

set IP=%~1
set PWD=%~2

if "%IP%"=="" (
    echo 请输入服务端 IP 地址（可以直接回车，在界面里填写）：
    set /p IP=
)

if not "%IP%"=="" (
    if not "%PWD%"=="" (
        "%EXE%" --client --connect "%IP%" --password "%PWD%"
    ) else (
        "%EXE%" --client --connect "%IP%"
    )
) else (
    "%EXE%" --client
)
