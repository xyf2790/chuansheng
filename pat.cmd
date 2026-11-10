@echo off
rem 传声 · 命令行包装器
rem   pat --status       看状态
rem   pat --video on     开画面
rem   pat --help         全部命令
setlocal
set "OUTFILE=%TEMP%\pat-cli-out.txt"
if exist "%OUTFILE%" del "%OUTFILE%" >nul 2>&1
start "" /wait "%~dp0传声.exe" --out "%OUTFILE%" %*
if exist "%OUTFILE%" (
    type "%OUTFILE%"
    del "%OUTFILE%" >nul 2>&1
)
endlocal