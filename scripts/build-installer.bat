@echo off
chcp 65001 >nul
title Build Installer

echo ========================================
echo  Building Installer
echo ========================================
echo.

:: 检查 PowerShell 脚本是否存在
if not exist "%~dp0build-installer.ps1" (
    echo [ERROR] build-installer.ps1 not found!
    echo.
    echo Press any key to exit...
    pause >nul
    exit /b 1
)

:: 执行 PowerShell 脚本
echo Running build-installer.ps1...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1"

:: 检查执行结果
if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Script failed with error code: %errorlevel%
) else (
    echo.
    echo [SUCCESS] Build completed successfully!
)

echo.
echo Press any key to exit...
pause >nul