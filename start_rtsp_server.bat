@echo off
REM ============================================================
REM MediaMTX RTSP Server 启动脚本
REM ============================================================
REM 此脚本用于启动 MediaMTX (原 rtsp-simple-server) 服务器
REM MediaMTX 是一个轻量级的 RTSP/RTMP/HLS 流媒体服务器
REM ============================================================

echo.
echo ========================================
echo   启动 MediaMTX RTSP 流媒体服务器
echo ========================================
echo.

cd /d "%~dp0mediamtx_v1.15.3_windows_amd64"

if not exist "mediamtx.exe" (
    echo [错误] 未找到 mediamtx.exe
    echo 请确保 mediamtx_v1.15.3_windows_amd64 文件夹存在
    echo.
    pause
    exit /b 1
)

echo [信息] MediaMTX 配置文件: mediamtx.yml
echo [信息] 默认 RTSP 端口: 8554
echo [信息] 流地址: rtsp://localhost:8554/live_stream
echo.
echo [提示] 按 Ctrl+C 可停止服务器
echo.
echo ========================================
echo   服务器正在运行...
echo ========================================
echo.

REM 启动 MediaMTX
mediamtx.exe

echo.
echo [信息] MediaMTX 已停止
pause
