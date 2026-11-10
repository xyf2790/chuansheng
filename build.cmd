@echo off
rem 传声 · 构建脚本（用 Windows 自带的 .NET Framework 编译器，不需要 Visual Studio）
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [错误] 找不到 csc.exe：%CSC%
    echo        需要 .NET Framework 4.x（Win10 1903+ 自带）
    pause & exit /b 1
)

rem 选项必须写在源文件之前，否则 csc 报 CS2022
set "OPTS=/nologo /target:winexe /codepage:65001 /optimize+ /win32icon:app.ico"
set "OPTS=!OPTS! /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll"
set "SRCS=src\Program.cs src\Ui.cs src\Welcome.cs src\JobObject.cs src\Payload.cs src\Transfer.cs"
set "SRCS=!SRCS! src\FileBrowser.cs src\ProcessTree.cs src\AppIcons.cs src\PinOverlay.cs"
set "SRCS=!SRCS! src\Toast.cs src\Shortcut.cs src\Cli.cs"

if /i "%~1"=="portable" goto :portable

echo === 编译开发版 传声.exe ===
"%CSC%" !OPTS! /out:传声.exe !SRCS!
if errorlevel 1 ( echo [失败] & pause & exit /b 1 )
echo [完成] 传声.exe
goto :end

:portable
set "SC=%~2"
if "%SC%"=="" set "SC=%SCRCPY_DIR%"
if "%SC%"=="" set "SC=..\Scrcpy"
if not exist "%SC%\scrcpy.exe" (
    echo [错误] 找不到 scrcpy.exe，用法：
    echo        build.cmd portable "D:\path\to\scrcpy"
    pause & exit /b 1
)

echo === 打包单文件 传声-便携版.exe ===
echo     scrcpy 目录: %SC%
set "RES="
for %%F in (scrcpy.exe scrcpy-server scrcpy.ico app.ico avcodec-62.dll avformat-62.dll avutil-60.dll swresample-6.dll SDL3.dll libusb-1.0.dll adb.exe AdbWinApi.dll AdbWinUsbApi.dll) do (
    if exist "%SC%\%%F" set "RES=!RES! /resource:%SC%\%%F,%%F"
)
set "RES=!RES! /resource:使用手册.md,manual.md"
"%CSC%" !OPTS! /out:传声-便携版.exe !RES! !SRCS!
if errorlevel 1 ( echo [失败] & pause & exit /b 1 )
echo [完成] 传声-便携版.exe

:end
echo.
echo 产物在当前目录。可执行文件不进 Git（见 .gitignore）。
pause