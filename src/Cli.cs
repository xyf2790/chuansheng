// Cli.cs — 命令行控制：把命令通过命名管道发给正在运行的托盘程序
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class Ipc
    {
        public const string PipeName = "PhoneAudioTray.IPC";

        // ---------- 服务端（托盘程序里跑）----------
        public static void StartServer()
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                            PipeTransmissionMode.Message, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            string line;
                            using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true))
                                line = reader.ReadLine();

                            string result;
                            try
                            {
                                // 交给 UI 线程的定时器去执行（避免句柄未建时 Invoke 失败）
                                var req = new CommandRequest { Line = line };
                                Program.PendingCommands.Enqueue(req);
                                result = req.Done.Wait(30000) ? (req.Result ?? "OK") : "ERR 执行超时（可能有弹窗挡住了）";
                            }
                            catch (Exception ex) { result = "ERR " + ex.Message; }

                            using (var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                                writer.WriteLine(result ?? "OK");
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        // ---------- 客户端 ----------
        public static string Send(string command, int timeoutMs = 15000)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    client.Connect(timeoutMs);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        writer.WriteLine(command);
                    using (var reader = new StreamReader(client, Encoding.UTF8, false, 1024, true))
                        return reader.ReadLine() ?? "OK";
                }
            }
            catch (Exception ex)
            {
                return "ERR 连不上正在运行的程序（" + ex.GetType().Name + "）";
            }
        }

        public static bool IsRunning()
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    client.Connect(300);
                    return true;
                }
            }
            catch { return false; }
        }
    }

    internal sealed class CommandRequest
    {
        public string Line;
        public string Result;
        public readonly System.Threading.ManualResetEventSlim Done =
            new System.Threading.ManualResetEventSlim(false);
    }

    internal static class Cli
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern int GetFileType(IntPtr hFile);

        private const int STD_OUTPUT_HANDLE = -11;
        private const int FILE_TYPE_CHAR = 2;      // 控制台

        /// <summary>标准输出是不是真正的控制台（而不是管道/文件）</summary>
        private static bool StdoutIsConsole()
        {
            try
            {
                var h = GetStdHandle(STD_OUTPUT_HANDLE);
                return h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) == FILE_TYPE_CHAR;
            }
            catch { return false; }
        }

        private static bool _hasConsole;
        private static string _outFile;      // --out <文件>：把结果写进文件（给 pat.cmd 用）

        private static void EnableConsoleOutput()
        {
            try { _hasConsole = AttachConsole(ATTACH_PARENT_PROCESS); }
            catch { _hasConsole = false; }
        }

        /// <summary>输出到父窗口控制台；被 cmd 重定向时同样可用</summary>
        private static void Out(string s)
        {
            if (!string.IsNullOrEmpty(_outFile))
            {
                try { File.AppendAllText(_outFile, s + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
                return;
            }

            try
            {
                var stdout = Console.OpenStandardOutput();
                // 真控制台用系统代码页（GBK）；被重定向/管道用 UTF-8，脚本才读得对
                var enc = StdoutIsConsole() ? Encoding.Default : new UTF8Encoding(false);
                using (var w = new StreamWriter(stdout, enc) { AutoFlush = true })
                    w.WriteLine(s);
            }
            catch { }

            // 兜底：控制台不可用时也落一份日志，方便排查
            try
            {
                var log = Path.Combine(Path.GetTempPath(), "PhoneAudioTray-cli.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log));
                File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>有参数就按命令行处理；返回 true 表示本次运行只是发命令，处理完应退出</summary>
        public static bool TryRun(string[] args)
        {
            if (args == null || args.Length == 0) return false;

            // 先剥离 --out <文件>
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    _outFile = args[++i];
                    continue;
                }
                rest.Add(args[i]);
            }
            if (rest.Count == 0) return false;

            var line = BuildCommand(rest.ToArray());
            if (line == null) return false;          // 不是可识别的命令，按正常启动处理

            EnableConsoleOutput();

            // 程序没在跑，而且这条命令需要它跑起来
            if (!Ipc.IsRunning())
            {
                if (line == "status")
                {
                    Out("程序没在运行");
                    return true;
                }

                var exe = Application.ExecutablePath;
                try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }); } catch { }

                for (int i = 0; i < 40 && !Ipc.IsRunning(); i++) Thread.Sleep(250);
                if (!Ipc.IsRunning())
                {
                    Out("ERR 启动失败");
                    return true;
                }
                Thread.Sleep(500);   // 等它把菜单搭好
            }

            var result = Ipc.Send(line);
            Out(result);

            // 没有控制台（比如双击运行）：把结果弹出来，别让人两眼一抹黑
            var informational = line == "status" || line == "devices" || line == "help";
            if (!_hasConsole && informational)
            {
                try { MessageBox.Show(result, "传声", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                catch { }
            }

            return true;
        }

        private static string Arg(string[] a, int index)
        {
            return a.Length > index ? a[index].Trim() : null;
        }

        /// <summary>把命令行参数翻译成一条指令</summary>
        private static string BuildCommand(string[] a)
        {
            var cmd = a[0].TrimStart('-', '/').ToLowerInvariant();

            switch (cmd)
            {
                case "i":                    // 兼容 --i 这种误写
                case "ipc":
                    return Arg(a, 1);
                case "status":
                    return "status";

                case "connect-usb":
                case "usb":
                    return "connect-usb";

                case "connect-wifi":
                case "connect":
                    {
                        var addr = Arg(a, 1);
                        return addr == null ? "connect-wifi" : "connect-wifi " + addr;
                    }

                case "devices":
                    return "devices";

                case "video":
                case "screen":
                    return "video " + (Arg(a, 1) ?? "toggle");

                case "mic":
                case "monitor-mic":
                    return "monitor-mic " + (Arg(a, 1) ?? "toggle");

                case "share-mic":
                    return "share-mic " + (Arg(a, 1) ?? "toggle");

                case "clipboard":
                    return "clipboard " + (Arg(a, 1) ?? "toggle");

                case "multi":
                    return "multi " + (Arg(a, 1) ?? "toggle");

                case "topmost":
                case "top":
                    return "topmost " + (Arg(a, 1) ?? "toggle");

                case "notify":
                    return "notify " + (Arg(a, 1) ?? "toggle");

                case "volume-phone":
                case "vol-phone":
                    return "volume-phone " + (Arg(a, 1) ?? "50");

                case "volume-pc":
                case "vol-pc":
                    return "volume-pc " + (Arg(a, 1) ?? "50");

                case "push":
                    {
                        var path = Arg(a, 1);
                        if (path == null) return "ERR-push";
                        return "push " + path;
                    }

                case "pull":
                    return "pull";
                case "files":
                    return "files";
                case "ps":
                    return "ps";

                case "stop":
                    return "stop";
                case "stop-all":
                    return "stop-all";
                case "exit":
                    return "exit";

                case "help":
                case "h":
                case "?":
                    return "help";

                default:
                    return null;
            }
        }
    }

    /// <summary>实际执行指令（在 UI 线程里跑，可以直接操作菜单和窗口）</summary>
    internal static class Commands
    {
        public static string Execute(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "ERR 空指令";

            var parts = line.Trim().Split(new[] { ' ' }, 2);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : null;

            // 兼容 --video / --help 这种写法（调试窗口里就是直接敲参数）
            if (cmd.StartsWith("-")) cmd = cmd.TrimStart('-');
            switch (cmd)
            {
                case "usb": cmd = "connect-usb"; break;
                case "connect": cmd = "connect-wifi"; break;
                case "screen": cmd = "video"; break;
                case "top": cmd = "topmost"; break;
                case "vol-phone": cmd = "volume-phone"; break;
                case "vol-pc": cmd = "volume-pc"; break;
                case "h": case "?": cmd = "help"; break;
            }

            try
            {
                switch (cmd)
                {
                    case "status":
                        return Program.StatusText();

                    case "connect-usb":
                        {
                            var usb = Program.ListDevices().Where(d => !d.Contains(":")).ToList();
                            if (usb.Count == 0) return "ERR 没有 USB 设备（检查数据线和「允许 USB 调试」）";
                            Program.StartSession(usb[0], "USB");
                            return "OK 已连接 " + usb[0];
                        }

                    case "connect-wifi":
                        if (!string.IsNullOrEmpty(arg))
                        {
                            string detail;
                            if (Program.TryConnect(arg, out detail))
                            {
                                Program.StartSession(arg, "WiFi");
                                return "OK 已连接 " + arg;
                            }
                            return "ERR 连接失败：" + detail.Trim();
                        }
                        Program.ShowConnectDialog(true);
                        return "OK 已打开连接窗口";

                    case "devices":
                        {
                            var list = new List<string>();
                            foreach (var d in Program.ListDevices())
                            {
                                var name = Program.QueryDeviceName(d);
                                list.Add(d + "  (" + name + ")");
                            }
                            var wifi = Program.DiscoverWifi();
                            var sb = new StringBuilder();
                            sb.AppendLine("USB/已连接设备：" + (list.Count == 0 ? "（无）" : ""));
                            foreach (var l in list) sb.AppendLine("  " + l);
                            sb.AppendLine("WiFi 可发现：" + (wifi.Count == 0 ? "（无）" : ""));
                            foreach (var w in wifi) sb.AppendLine("  " + w);
                            return sb.ToString().TrimEnd();
                        }

                    case "video":
                        {
                            var want = Decide(arg, Program.ShowVideo);
                            if (want == Program.ShowVideo) return "OK 画面已经是" + (want ? "开" : "关");
                            Program.SetShowVideo(want);
                            if (!want) return "OK 画面已关闭，切换为仅音频";
                            return "OK 画面已开启" + (Program.NeedMicSession() ? "（含麦克风）" : "");
                        }

                    case "monitor-mic":
                        {
                            var want = Decide(arg, Program.MonitorMic);
                            Program.SetMonitorMic(want);
                            return "OK 监听手机麦克风：" + (want ? "开" : "关");
                        }

                    case "share-mic":
                        {
                            var want = Decide(arg, Program.ShareMic);
                            Program.SetShareMic(want);
                            return "OK 共享麦克风：" + (want ? "开（开画面时生效）" : "关");
                        }

                    case "clipboard":
                        {
                            var want = Decide(arg, Program.ClipboardSync);
                            Program.SetClipboardSync(want);
                            return "OK 剪贴板同步已" + (want ? "开启" : "关闭") + "（重启当前连接后生效）";
                        }

                    case "multi":
                        {
                            var want = Decide(arg, Program.AllowMultiple);
                            Program.SetAllowMultiple(want);
                            return "OK 允许多路音频：" + (want ? "开" : "关");
                        }

                    case "topmost":
                        {
                            var want = Decide(arg, Program.WindowTopMost);
                            Program.SetWindowTopMost(want);
                            return "OK 画面窗口置顶：" + (want ? "开" : "关");
                        }

                    case "notify":
                        {
                            var want = Decide(arg, Toast.Enabled);
                            Toast.Enabled = want;
                            return "OK Windows 通知：" + (want ? "开" : "关");
                        }

                    case "volume-phone":
                        {
                            int v;
                            if (!int.TryParse(arg, out v)) return "ERR 音量要 0-100 的数字";
                            Program.SetPhoneVolumePct(v);
                            return "OK 手机音量 → " + v + "%";
                        }

                    case "volume-pc":
                        {
                            int v;
                            if (!int.TryParse(arg, out v)) return "ERR 音量要 0-100 的数字";
                            Program.SetPcVolume(Math.Max(0, Math.Min(100, v)) / 100f);
                            return "OK 电脑侧音量 → " + v + "%";
                        }

                    case "push":
                        {
                            var path = arg;
                            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                                return "ERR 找不到文件：" + (path ?? "（没给路径）");
                            return Program.CliPush(path);
                        }

                    case "pull":
                        Program.OpenFileBrowser();
                        return "OK 已打开「从手机取文件」窗口";

                    case "files":
                        Program.OpenFileBrowser();
                        return "OK 已打开文件浏览窗口";

                    case "ps":
                        Program.OpenProcessTree();
                        return "OK 已打开进程树窗口";

                    case "stop":
                        Program.StopLatest();
                        return "OK 已停止当前";

                    case "stop-all":
                        Program.StopAllSessions();
                        return "OK 已停止全部";

                    case "exit":
                        Program.ShutdownAll();
                        Application.Exit();
                        return "OK 已退出";

                    case "help":
                        return Program.CommandHelp();

                    default:
                        return "ERR 不认识的指令：" + cmd + "（用 --help 看列表）";
                }
            }
            catch (Exception ex)
            {
                return "ERR " + ex.Message;
            }
        }

        private static bool Decide(string arg, bool current)
        {
            if (string.IsNullOrEmpty(arg)) return !current;           // 省略 = toggle
            var a = arg.ToLowerInvariant();
            if (a == "on" || a == "1" || a == "true" || a == "开") return true;
            if (a == "off" || a == "0" || a == "false" || a == "关") return false;
            return !current;
        }
    }
}
