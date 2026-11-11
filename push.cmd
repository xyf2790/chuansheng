@echo off
rem 传声 · 一键推送（GitHub 直连不通时用这个，会自动换可用 IP）
setlocal
cd /d "%~dp0"
if exist "tools\ghpush.exe" (
    tools\ghpush.exe %*
) else (
    echo 没找到 tools\ghpush.exe，退回普通推送
    git push %*
)
set "RC=%errorlevel%"
echo.
if "%RC%"=="0" (echo 推送完成) else (echo 推送失败，退出码 %RC%)
pause
exit /b %RC%