# 回归检查

需要 Windows、.NET Framework 4.x 和 PowerShell，无 NuGet 依赖。

```powershell
./tests/Run-LocalRegression.ps1
./tests/Run-LocalRegression.ps1 -Desktop
./tests/Verify-Portable.ps1 -Executable ./Transmit-v1.1.5.exe
```

默认使用测试进程模拟 ADB，覆盖失败退出码、超时、取消、预览缓存、任务管理器解析和两万条目录的界面响应。`-Desktop` 额外验证托盘左键和自启动开关，会暂时修改当前用户的自启动项并在 finally 中恢复原值。

`Verify-Portable.ps1` 在独立临时目录检查发布包内嵌资源、解压和截断运行文件修复，不会启动程序或覆盖当前安装。

真机传输检查必须显式指定设备：

```powershell
./tests/Run-TransferRegression.ps1 -Runtime '<scrcpy/adb 所在目录>' -Serial '<adb devices 中的序列号>' -Remote '/sdcard/Download/Transmit-regression'
```

该检查会生成并传输合成文件，远端目录请使用专门的测试目录，不要指向已有个人文件目录。真实设备测试数据不会提交到仓库。
