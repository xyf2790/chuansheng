# 传声 · Transmit

![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.x-512BD4)
![scrcpy](https://img.shields.io/badge/scrcpy-4.1-3DDC84)
![license](https://img.shields.io/badge/license-MIT-green)

把手机的声音和画面投到 Windows：**音频转发、屏幕镜像、麦克风监听、剪贴板同步、文件互传、进程树管理**。
基于 [scrcpy](https://github.com/Genymobile/scrcpy) 的托盘小工具，**无账号、无服务器、不联网**，USB 或局域网直连。

> 目标很简单：电脑的耳机和键盘，接着用手机。

---

## 功能

| 能力 | 说明 |
|---|---|
| **手机声音进电脑** | 手机放歌/看视频，声音从电脑耳机出来 |
| **画面镜像** | 一个窗口看手机屏幕，**音画同步**（同一进程解码，不会错位） |
| **手机麦克风** | 两种用法：**监听**（菜单开关，默认关、独立）／**共享**（无菜单项，默认关，开画面时自动跟随） |
| **手机音量** | 在电脑上直接调手机媒体音量（走 `input keyevent`，绕开部分机型对 `cmd media_session --set` 的拦截） |
| **剪贴板互通** | 纯文本，双向 |
| **文件互传** | 推送/取回、目录树浏览、搜索、删除（带回收站可还原） |
| **进程树** | 按内存排序、按父进程展开、应用图标 + 中文名、强停应用 |
| **托盘常驻** | 无主窗口，右键菜单操作；退出时用 Job 对象兜底杀掉子进程 |
| **命令行** | 所有功能都能用 `pat --xxx` 调用，可接脚本 |

## 系统要求

**电脑**

| 项目 | 要求 |
|---|---|
| 系统 | Windows 10 1809+ / Windows 11，**64 位** |
| 运行时 | .NET Framework 4.x（Win10 1903 以后系统自带） |
| 驱动 | USB 方式需要手机 ADB 驱动（多数 Win10/11 自动装好）；**WiFi 方式不需要任何驱动** |

**手机**

| 项目 | 要求 |
|---|---|
| Android | **11 及以上**才有音频转发；10 及更早只能看画面、**没有声音** |
| 开发者选项 | 必须打开（设置 → 关于手机 → 连点版本号 7 次） |
| 调试开关 | USB：开「USB 调试」；WiFi：开「无线调试」 |
| 网络 | WiFi 方式需与电脑同一局域网 |

## 下载与使用

1. 到 [Releases](https://github.com/xyf2790/chuansheng/releases) 下载 `Transmit-v1.0.0.exe`（便携版单文件，约 25 MB，内含 scrcpy 与 adb）
2. 双击运行（文件名可随意改）→ **首次会解压约 25 MB** 到 `%LOCALAPPDATA%\PhoneAudioTray\`（5~15 秒）
3. 托盘出现图标，**双击**打开连接窗口 → 选设备 → 连接
4. 右键托盘菜单可开画面、调音量、传文件、看进程

详细说明见 **[使用手册.md](./使用手册.md)**（含常见问题排查、命令行完整命令表）。

## 命令行

程序目录里的 `pat.cmd` 是包装器（GUI 程序不会把输出交给控制台，这个脚本处理了等待与输出）：

```cmd
cd /d "<程序所在文件夹>"
pat --status
pat --connect-usb
pat --video on
pat --volume-phone 80
pat --stop-all
```

`pat --help` 查看全部命令。也可以用程序内的「调试窗口 → 本机（程序命令）」直接敲 `--status`。

## 从源码构建

需要 Windows 自带的 C# 编译器（.NET Framework 4.8 自带 `csc.exe`，无需装 Visual Studio）：

```cmd
build.cmd            :: 编译开发版 传声.exe（运行时去 D:\Apply\Scrcpy 或 settings.txt 的 scrcpyDir 找 scrcpy）
build.cmd portable   :: 打包单文件版（需要把 scrcpy 运行时放在同目录或设置 SCRCPY_DIR）
```

## 推送不上去怎么办

国内直连 GitHub 经常出现「ping 通、但 443 握手超时」，`git push` 报：

```
Failed to connect to github.com:443 after 21000 ms
```

而浏览器能打开 GitHub。原因是 DNS 解析到的那个 IP 被干扰，**换一个 IP 就能通**。

本仓库带了一个小工具 `tools/ghpush.exe`（源码 `tools/GhPush.cs`）：自己起一个本地转发，把 github.com:443 指向实测可用的 IP，再执行推送，失败会依次换下一个 IP。

```cmd
push.cmd          :: 一键：自动换 IP 推送
```

> 附：可用 IP 会变，当前候选写在 `tools/GhPush.cs` 顶部的 `Candidates` 数组里，随时可加。

## 目录结构

```
src/                  C# 源码（13 个文件，单文件编译，无 NuGet 依赖）
  Program.cs          托盘、会话管理、命令分发
  Ui.cs               连接窗口 / 音量 / 调试窗口
  FileBrowser.cs      文件浏览、回收站
  Transfer.cs         推送、拉取、搜索
  ProcessTree.cs      进程树
  AppIcons.cs         APK 图标提取 + 包名中文名映射（236 条）
  Toast.cs            Windows 通知（WinRT，经 PowerShell 5.1 桥接）
  Shortcut.cs         创建带 AppUserModelID 的开始菜单快捷方式
  Cli.cs              命名管道 IPC + 命令行解析
  Welcome.cs          首次启动引导
  JobObject.cs        保证退出时子进程一起结束
  Payload.cs          单文件版：释放内嵌的 scrcpy/adb
  PinOverlay.cs       窗口置顶浮层（已改由托盘菜单控制，保留作参考）
使用手册.md           面向使用者的完整手册
pat.cmd              命令行包装器
build.cmd            构建脚本
LICENSE              MIT（本仓库代码）
```

## 实现要点

- **音视频同源**：画面和播放音频跑在**同一个 scrcpy 进程**里，共用内部时钟，天然同步；关窗口＝进程结束，所以会自动退回「仅音频」而不是断连。
- **麦克风独立**：`--audio-source=mic` 另起一路 `scrcpy --no-video --no-window`，与画面互不影响。
- **不产生孤儿进程**：子进程挂到 Windows Job 对象（`KILL_ON_JOB_CLOSE`），主程序退出/崩溃时一起结束。
- **通知可显示**：桌面程序要发 Toast，必须有带 `System.AppUserModel.ID` 的开始菜单快捷方式，程序首次运行会自动创建。
- **跨进程命令**：GUI 程序跑 `--status` 时创建命名管道服务端，第二份实例只发指令不新开界面。

## 已知限制

- Android 10 及以下**没有声音**（scrcpy 的音频捕获 API 是 11 才有的）
- 剪贴板只同步**纯文本**（图片/文件不行，Android 剪贴板机制限制）
- 不支持 iOS、纯血鸿蒙
- WiFi 延迟受路由器影响，看视频够用，打游戏不行
- 部分 App（微信、淘宝等）APK 资源混淆，取不到图标 → 回退为首字母头像
- 无数字签名，SmartScreen / 杀软可能提示

## 第三方组件与许可

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| [scrcpy](https://github.com/Genymobile/scrcpy) | 4.1 | **Apache-2.0** | 画面镜像与音频转发核心 |
| [Android platform-tools (adb)](https://developer.android.com/tools/releases/platform-tools) | 随包 | **Apache-2.0** | 设备通信 |

`传声.exe` 只是 scrcpy 的图形外壳与附加功能（托盘、音量、文件、进程、通知）。投屏和音频能力来自 scrcpy，功劳属于它。

**分发注意**：本项目的 Release 附件中包含 scrcpy 与 adb 的二进制，按 Apache-2.0 要求同时附带其许可文本；二次分发时请保留 `LICENSE-scrcpy.txt` 与来源说明。

## 免责声明

本项目为个人工具，按现状提供，不保证适用于任何特定用途。通过 adb 对设备进行的操作（删除文件、强停应用等）由使用者自行承担后果。

## 许可

本仓库代码采用 **MIT** 许可，详见 [LICENSE](LICENSE)。
