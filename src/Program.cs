// PhoneAudioTray.cs — 传声 → 电脑耳机（托盘程序）
// 编译：csc /target:winexe /codepage:65001 /out:PhoneAudioTray.exe PhoneAudioTray.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal sealed class Session
    {
        public Process Proc;
        public string Serial;
        public string Mode;
        public bool IsVideo;        // 这一路是不是"画面+音频"模式
        public bool IsMic;          // 这一路是不是"手机麦克风"
        public bool KilledByUs;     // 是不是我们主动停的（区别于用户关窗口）
    }

    internal static class Program
    {
        public const string AppVersion = "1.1.0";

        public static string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        /// <summary>外部 scrcpy 目录（settings.txt 里的 scrcpyDir，或同级的 Scrcpy 文件夹）</summary>
        public static string ScrcpyDir = "";

        /// <summary>
        /// 找 scrcpy.exe：先看程序自己的目录（便携版就是这样），
        /// 再看 settings.txt 指定的目录，再看同级的 Scrcpy 文件夹。
        /// </summary>
        public static string ScrcpyExe
        {
            get
            {
                var local = Path.Combine(BaseDir, "scrcpy.exe");
                if (File.Exists(local)) return local;

                EnsureSettings();

                if (!string.IsNullOrEmpty(ScrcpyDir))
                {
                    var p1 = Path.Combine(ScrcpyDir, "scrcpy.exe");
                    if (File.Exists(p1)) return p1;
                }

                try
                {
                    var parent = Path.GetDirectoryName(BaseDir.TrimEnd('\\'));
                    if (!string.IsNullOrEmpty(parent))
                    {
                        var p2 = Path.Combine(parent, "Scrcpy", "scrcpy.exe");
                        if (File.Exists(p2)) return p2;
                    }
                }
                catch { }

                return local;
            }
        }
        public static string IconPath
        {
            get
            {
                var app = Path.Combine(BaseDir, "app.ico");
                return File.Exists(app) ? app : Path.Combine(BaseDir, "scrcpy.ico");
            }
        }

        private static string _adb;
        public static string AdbExe
        {
            get
            {
                if (_adb == null) _adb = ResolveAdb();
                return _adb;
            }
        }

        public static readonly List<Session> Sessions = new List<Session>();
        public static bool AllowMultiple = false;
        public static bool ShowVideo = false;          // false=只要音频（无窗口）；true=音视频同一进程（同步）
        public static bool ClipboardSync = true;       // 文本剪贴板双向同步（scrcpy 默认开）
        public static bool WindowTopMost = false;      // 画面窗口是否置顶
        public static bool ShareMic = false;           // 共享：无菜单项，只在开画面时自动跟随（也可命令行）
        public static bool MonitorMic = false;         // 监听：菜单开关，默认关，与画面独立
        public static string LastSerial;
        public static string LastMode;
        public static NotifyIcon Tray;
        public static readonly System.Collections.Concurrent.ConcurrentQueue<CommandRequest> PendingCommands =
            new System.Collections.Concurrent.ConcurrentQueue<CommandRequest>();
        public static readonly System.Collections.Concurrent.ConcurrentQueue<Action> UiActions =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();
        public static ToolStripMenuItem StatusItem;
        public static ToolStripMenuItem MultiItem;
        public static ToolStripMenuItem VideoItem;
        public static ToolStripMenuItem MicItem;
        public static ToolStripMenuItem TopItem;
        public static ToolStripMenuItem ClipItem;
        public static SynchronizationContext Ui;

        [STAThread]
        private static void Main(string[] args)
        {
            BaseDir = Payload.Ensure();

            // 命令行模式：有参数就把指令发给已经在跑的程序，然后退出
            if (Cli.TryRun(args)) return;

            if (args != null && args.Any(a => a.Equals("--test", StringComparison.OrdinalIgnoreCase)))
            {
                var log = new StringBuilder();
                log.AppendLine("adb: " + AdbExe + "  exists=" + File.Exists(AdbExe));
                log.AppendLine("scrcpy: " + ScrcpyExe + "  exists=" + File.Exists(ScrcpyExe));
                log.AppendLine("--- devices ---");
                foreach (var d in ListDevices()) log.AppendLine("  " + d);
                log.AppendLine("--- wifi (mdns) ---");
                foreach (var d in DiscoverWifi()) log.AppendLine("  " + d);
                File.WriteAllText(Path.Combine(BaseDir, "selftest.log"), log.ToString(), Encoding.UTF8);
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, @"Local\PhoneAudioTray_SingleInstance", out created))
            {
                if (!created)
                {
                    return;   // 已经有实例在跑：静默退出（弹模态框会卡住命令行/脚本调用）
                }

                Application.EnableVisualStyles();

                // 兜底：任何未处理异常都记日志 + 提示，不让程序直接消失
                Application.ThreadException += (s, e) => LogFatal(e.Exception, "UI");
                AppDomain.CurrentDomain.UnhandledException += (s, e) => LogFatal(e.ExceptionObject as Exception, "非UI");
                Application.SetCompatibleTextRenderingDefault(false);
                Ui = new SynchronizationContext();
                BuildTray();
                Ipc.StartServer();

                var ctx = new TrayContext();

                bool wantConnect = false;
                if (WelcomeForm.IsFirstRun)
                {
                    using (var w = new WelcomeForm(true))
                    {
                        w.ShowDialog();
                        WelcomeForm.MarkDone();
                        wantConnect = w.StartConnect;
                    }
                }

                if (wantConnect)
                {
                    var timer = new System.Windows.Forms.Timer { Interval = 300 };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        ShowConnectDialog(false);
                    };
                    timer.Start();
                }

                Application.Run(ctx);
                ShutdownAll();
            }
        }

        private static string ResolveAdb()
        {
            // 优先用户指定的 platform-tools，其次 PATH，最后 scrcpy 目录自带
            var known = @"D:\Apply\platform-tools\adb.exe";
            if (File.Exists(known)) return known;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(';'))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "adb.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            var local = Path.Combine(BaseDir, "adb.exe");
            if (File.Exists(local)) return local;

            return "adb.exe";
        }

        private static void BuildTray()
        {
            Tray = new NotifyIcon();
            try
            {
                Tray.Icon = File.Exists(IconPath) ? new Icon(IconPath) : SystemIcons.Application;
            }
            catch { Tray.Icon = SystemIcons.Application; }
            Tray.Text = "传声 · 待机";
            Tray.Visible = true;

            var menu = new ContextMenuStrip();

            StatusItem = new ToolStripMenuItem("状态：待机") { Enabled = false };
            menu.Items.Add(StatusItem);
            menu.Items.Add(new ToolStripSeparator());

            var connect = new ToolStripMenuItem("连接手机");
            connect.DropDownItems.Add("USB 连接（直接连）", null, (s, e) => ConnectUsb());
            connect.DropDownItems.Add("选择设备…", null, (s, e) => ShowConnectDialog(false));
            connect.DropDownItems.Add("WiFi 连接…", null, (s, e) => ShowConnectDialog(true));
            connect.DropDownItems.Add("配对新设备…（首次连某台手机用）", null, (s, e) => ShowPairDialog());
            menu.Items.Add(connect);

            var volume = new ToolStripMenuItem("音量");

            var phoneVol = new ToolStripMenuItem("手机音量");
            foreach (var pct in new[] { 0, 25, 50, 75, 100 })
            {
                int v = pct;
                phoneVol.DropDownItems.Add(pct + "%", null, (s, e) => SetPhoneVolumePct(v));
            }
            volume.DropDownItems.Add(phoneVol);

            var pcVol = new ToolStripMenuItem("电脑侧音量");
            foreach (var pct in new[] { 0, 25, 50, 75, 100 })
            {
                int v = pct;
                pcVol.DropDownItems.Add(pct + "%", null, (s, e) => SetPcVolume(v / 100.0f));
            }
            volume.DropDownItems.Add(pcVol);

            volume.DropDownItems.Add(new ToolStripSeparator());
            volume.DropDownItems.Add("音量调节…", null, (s, e) => ShowVolumeDialog());
            menu.Items.Add(volume);

            menu.Items.Add(new ToolStripSeparator());

            MultiItem = new ToolStripMenuItem("允许同时多路音频") { Checked = false };
            MultiItem.Click += (s, e) => SetAllowMultiple(!AllowMultiple);
            menu.Items.Add(MultiItem);

            // 画面：单进程同时出音视频 => scrcpy 内部同一时钟，天然音画同步
            VideoItem = new ToolStripMenuItem("显示手机画面（音视频同步）") { Checked = false };
            VideoItem.Click += (s, e) => SetShowVideo(!ShowVideo);
            menu.Items.Add(VideoItem);

            TopItem = new ToolStripMenuItem("画面窗口置顶") { Checked = false };
            var topItem = TopItem;
            topItem.Click += (s, e) =>
            {
                SetWindowTopMost(!WindowTopMost);
                topItem.Checked = WindowTopMost;
            };
            MicItem = new ToolStripMenuItem("监听手机麦克风") { Checked = false };
            var micItem = MicItem;
            micItem.Click += (s, e) =>
            {
                SetMonitorMic(!MonitorMic);
                micItem.Checked = MonitorMic;
            };
            menu.Items.Add(micItem);

            menu.Items.Add(topItem);

            // 文本剪贴板：scrcpy 默认就双向同步；这里给一个显式开关（切换会重启当前这一路）
            ClipItem = new ToolStripMenuItem("剪贴板同步（文本 · 双向）") { Checked = true };
            var clipItem = ClipItem;
            clipItem.Click += (s, e) =>
            {
                SetClipboardSync(!ClipboardSync);
                clipItem.Checked = ClipboardSync;
            };
            menu.Items.Add(clipItem);

            menu.Items.Add("推送文件到手机…", null, (s, e) => Transfer.PushFiles());
            menu.Items.Add("从手机取文件…", null, (s, e) => Transfer.PullFiles());
            menu.Items.Add("手机进程树…", null, (s, e) =>
            {
                var serial = PrimarySerial;
                if (string.IsNullOrEmpty(serial))
                {
                    MessageBox.Show("还没有连接手机。", "进程树", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                using (var f = new ProcessTreeDialog(serial)) f.ShowDialog();
            });

            menu.Items.Add("停止当前", null, (s, e) => StopLatest());
            menu.Items.Add("停止全部", null, (s, e) => { StopAllSessions(); UpdateStatus(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("调试窗口…", null, (s, e) =>
            {
                using (var f = new DebugForm(string.IsNullOrEmpty(LastSerial) ? PrimarySerial : LastSerial))
                    f.ShowDialog();
            });
            menu.Items.Add("使用说明…", null, (s, e) =>
            {
                using (var w = new WelcomeForm(false))
                {
                    w.ShowDialog();
                    if (w.StartConnect) ShowConnectDialog(false);
                }
            });
            var manualMenu = new ToolStripMenuItem("打开使用手册");
            manualMenu.DropDownItems.Add("用默认程序打开（Typora 等）", null, (s, e) => OpenManual(false));
            manualMenu.DropDownItems.Add("用记事本打开", null, (s, e) => OpenManual(true));
            manualMenu.DropDownItems.Add("在文件夹中显示", null, (s, e) => RevealManual());
            menu.Items.Add(manualMenu);
            menu.Items.Add("打开程序目录", null, (s, e) => Process.Start("explorer.exe", BaseDir));
            menu.Items.Add("退出", null, (s, e) => { Tray.Visible = false; Application.Exit(); });

            Tray.ContextMenuStrip = menu;
            Tray.DoubleClick += (s, e) => ShowConnectDialog(false);

            // 命令行指令队列：由 UI 线程定时取出执行（比 Control.Invoke 稳，不依赖句柄）
            var cmdTimer = new System.Windows.Forms.Timer { Interval = 120 };
            cmdTimer.Tick += (s, e) =>
            {
                Action act;
                while (UiActions.TryDequeue(out act))
                {
                    try { act(); } catch { }
                }

                CommandRequest req;
                while (PendingCommands.TryDequeue(out req))
                {
                    try { req.Result = Commands.Execute(req.Line); }
                    catch (Exception ex) { req.Result = "ERR " + ex.Message; }
                    finally { req.Done.Set(); }
                }
            };
            cmdTimer.Start();
        }

        // ---------- adb ----------
        public static string RunAdb(string args, int timeoutMs = 8000)
        {
            try
            {
                var psi = new ProcessStartInfo(AdbExe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    var stdout = p.StandardOutput.ReadToEnd();
                    var stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "";
                    }
                    return stdout + stderr;
                }
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        /// <summary>执行 adb 并取回原始字节（二进制安全，用于拉图标）</summary>
        public static byte[] RunAdbBinary(string args, int timeoutMs = 30000)
        {
            try
            {
                var psi = new ProcessStartInfo(AdbExe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    var ms = new MemoryStream();
                    p.StandardOutput.BaseStream.CopyTo(ms);
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        public static List<string> ListDevices()
        {
            var result = new List<string>();
            var output = RunAdb("devices");
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices")) continue;
                var m = Regex.Match(line, @"^(\S+)\s+device$");
                if (m.Success) result.Add(m.Groups[1].Value);
            }
            return result;
        }

        public static List<string> DiscoverWifi()
        {
            var found = new List<string>();
            var mdns = RunAdb("mdns services");
            foreach (Match m in Regex.Matches(mdns, @"(\d{1,3}(?:\.\d{1,3}){3}):(\d+)"))
            {
                var addr = m.Groups[1].Value + ":" + m.Groups[2].Value;
                if (!found.Contains(addr)) found.Add(addr);
            }
            return found;
        }

        // ---------- 设备名（品牌 + 型号），带缓存 ----------
        public sealed class DeviceInfo
        {
            public string Address;
            public string Serial;
            public string Name;
        }

        private static readonly Dictionary<string, string> NameCache = new Dictionary<string, string>();
        private static bool cacheLoaded;

        private static string CachePath { get { return Path.Combine(BaseDir, "devices.txt"); } }

        private static void EnsureCache()
        {
            if (cacheLoaded) return;
            cacheLoaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                foreach (var line in File.ReadAllLines(CachePath))
                {
                    var parts = line.Split('|');
                    if (parts.Length == 2 && parts[0].Trim().Length > 0)
                        NameCache[parts[0].Trim()] = parts[1].Trim();
                }
            }
            catch { }
        }

        private static void SaveCache()
        {
            try
            {
                var lines = NameCache.Select(kv => kv.Key + "|" + kv.Value).ToArray();
                File.WriteAllLines(CachePath, lines, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>查询 品牌+型号；查不到就用串号。结果按串号缓存。</summary>
        public static string QueryDeviceName(string serial)
        {
            EnsureCache();
            var key = serial ?? "";
            if (NameCache.ContainsKey(key)) return NameCache[key];

            var brand = RunAdb("-s " + serial + " shell getprop ro.product.brand", 6000).Trim();
            var model = RunAdb("-s " + serial + " shell getprop ro.product.model", 6000).Trim();
            var name = (brand + " " + model).Trim();
            if (name.Length == 0) name = serial;

            NameCache[key] = name;
            SaveCache();
            return name;
        }

        /// <summary>改名（按串号缓存；串号不变，换端口/重启都不会丢）。传空名字=恢复默认。</summary>
        public static void RenameDevice(string serial, string newName)
        {
            if (string.IsNullOrEmpty(serial)) return;
            EnsureCache();

            if (string.IsNullOrWhiteSpace(newName))
                NameCache.Remove(serial);
            else
                NameCache[serial] = newName.Trim();

            SaveCache();
        }

        /// <summary>WiFi 设备：从 mdns 实例名里抠出串号，再尽力查出型号名。</summary>
        public static List<DeviceInfo> DiscoverWifiDetailed()
        {
            var list = new List<DeviceInfo>();
            var output = RunAdb("mdns services");

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of")) continue;

                var m = Regex.Match(line, @"^adb-([^-\s]+)-[^\s]+\s+_adb-tls-connect\._tcp\s+(\d{1,3}(?:\.\d{1,3}){3}:\d+)");
                string serial = null, addr = null;

                if (m.Success)
                {
                    serial = m.Groups[1].Value;
                    addr = m.Groups[2].Value;
                }
                else
                {
                    var m2 = Regex.Match(line, @"(\d{1,3}(?:\.\d{1,3}){3}:\d+)");
                    if (!m2.Success) continue;
                    addr = m2.Groups[1].Value;
                }

                var name = "（未连接，点刷新可查型号）";
                if (serial != null)
                {
                    EnsureCache();
                    if (NameCache.ContainsKey(serial))
                    {
                        name = NameCache[serial];
                    }
                    else
                    {
                        // 先连上才能读属性；连不上就留空让用户自己认
                        RunAdb("connect " + addr, 8000);
                        Thread.Sleep(300);
                        var brand = RunAdb("-s " + addr + " shell getprop ro.product.brand", 6000).Trim();
                        var model = RunAdb("-s " + addr + " shell getprop ro.product.model", 6000).Trim();
                        var combined = (brand + " " + model).Trim();
                        if (combined.Length > 0)
                        {
                            name = combined;
                            NameCache[serial] = combined;
                            SaveCache();
                        }
                    }
                }

                list.Add(new DeviceInfo { Address = addr, Serial = serial ?? addr, Name = name });
            }
            return list;
        }

        /// <summary>手册文件路径（开发版与单文件版都在 BaseDir 下）</summary>
        public static string ManualPath
        {
            get { return Path.Combine(BaseDir, "使用手册.md"); }
        }

        /// <summary>读注册表字符串值（hive: CurrentUser / ClassesRoot / LocalMachine）</summary>
        private static string RegGet(Microsoft.Win32.RegistryKey root, string subPath, string valueName)
        {
            try
            {
                using (var key = root.OpenSubKey(subPath))
                {
                    if (key == null) return null;
                    var v = key.GetValue(valueName) as string;
                    return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                }
            }
            catch { return null; }
        }

        /// <summary>从 "C:\path\app.exe" "%1" 里抠出 exe 路径</summary>
        private static string ExeFromCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                if (end > 1) return command.Substring(1, end - 1);
            }
            var space = command.IndexOf(' ');
            return space > 0 ? command.Substring(0, space) : command;
        }

        /// <summary>ProgId → 打开命令</summary>
        private static string CommandForProgId(string progId)
        {
            if (string.IsNullOrWhiteSpace(progId)) return null;

            var cmd = RegGet(Microsoft.Win32.Registry.ClassesRoot, progId + @"\shell\open\command", null);
            if (cmd == null)
                cmd = RegGet(Microsoft.Win32.Registry.CurrentUser, @"Software\Classes\" + progId + @"\shell\open\command", null);

            var exe = ExeFromCommand(cmd);
            return (!string.IsNullOrEmpty(exe) && File.Exists(exe)) ? exe : null;
        }

        /// <summary>找一个能看 .md 的程序：系统默认关联 → OpenWithProgids → 常见编辑器</summary>
        public static string FindMarkdownViewer()
        {
            // 1) 用户设置过的默认程序
            var progId = RegGet(Microsoft.Win32.Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice", "ProgId");
            var exe = CommandForProgId(progId);
            if (exe != null) return exe;

            // 2) .md 的候选程序列表（你机器上这里就是 Typora.md）
            try
            {
                using (var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@".md\OpenWithProgids"))
                {
                    if (key != null)
                    {
                        foreach (var name in key.GetValueNames())
                        {
                            exe = CommandForProgId(name);
                            if (exe != null) return exe;
                        }
                    }
                }
            }
            catch { }

            // 3) 常见 Markdown 编辑器：App Paths
            var candidates = new[] { "Typora.exe", "Obsidian.exe", "Code.exe", "MarkText.exe", "notepad++.exe", "VNote.exe", "Zettlr.exe" };
            foreach (var c in candidates)
            {
                foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                {
                    var path = RegGet(hive, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + c, null);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
            }

            // 4) 常见安装目录
            var guesses = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Typora\Typora.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Obsidian\Obsidian.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"),
                @"C:\Program Files\Typora\Typora.exe",
                @"C:\Program Files\Notepad++\notepad++.exe"
            };
            foreach (var g in guesses)
                if (File.Exists(g)) return g;

            return null;
        }

        /// <summary>启动外部程序时，剔掉会改变 Electron 应用行为的继承环境变量</summary>
        public static ProcessStartInfo CleanStartInfo(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false
            };
            // 如果本程序是被 Electron 宿主（DeepSeek Harness / VS Code 等）拉起来的，
            // 环境里会带着 ELECTRON_RUN_AS_NODE=1；Typora / VS Code / Obsidian 这些
            // Electron 应用继承后会退化成 Node，把 .md 当成 JS 脚本执行 → 直接报错退出。
            try { psi.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE"); } catch { }
            return psi;
        }

        /// <summary>打开手册：找 .md 阅读器（剔除 Electron 环境变量）→ 系统关联 → 记事本</summary>
        public static void OpenManual(bool forceNotepad)
        {
            var manual = ManualPath;
            if (!File.Exists(manual))
            {
                MessageBox.Show("没找到 使用手册.md（应该和程序放在一起）。\r\n\r\n路径：" + manual, "使用手册",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!forceNotepad)
            {
                // 1) 主动找 Markdown 阅读器，并清掉 Electron 变量
                var viewer = FindMarkdownViewer();
                if (!string.IsNullOrEmpty(viewer))
                {
                    try
                    {
                        Process.Start(CleanStartInfo(viewer, "\"" + manual + "\""));
                        return;
                    }
                    catch { }
                }

                // 2) 退回系统关联（双击等价）
                try
                {
                    Process.Start(new ProcessStartInfo(manual) { UseShellExecute = true });
                    return;
                }
                catch { }
            }

            try { Process.Start("notepad.exe", "\"" + manual + "\""); }
            catch (Exception ex)
            {
                MessageBox.Show("打开失败：" + ex.Message + "\r\n\r\n可以手动打开：\r\n" + manual,
                    "使用手册", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public static void RevealManual()
        {
            var manual = ManualPath;
            try
            {
                if (File.Exists(manual))
                    Process.Start("explorer.exe", "/select,\"" + manual + "\"");
                else
                    Process.Start("explorer.exe", BaseDir);
            }
            catch { }
        }

        // ---------- 设置（推送目标目录等）----------
        public static string PushTarget = "/sdcard/Download";
        private static bool settingsLoaded;
        private static string SettingsPath { get { return Path.Combine(BaseDir, "settings.txt"); } }

        public static void EnsureSettings()
        {
            if (settingsLoaded) return;
            settingsLoaded = true;
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var p = line.Split('|');
                    if (p.Length != 2) continue;
                    if (p[0].Trim() == "pushTarget" && p[1].Trim().Length > 0) PushTarget = p[1].Trim();
                    if (p[0].Trim() == "scrcpyDir" && p[1].Trim().Length > 0) ScrcpyDir = p[1].Trim();
                }
            }
            catch { }
        }

        public static void SaveSettings()
        {
            try
            {
                var sbSettings = new StringBuilder();
                sbSettings.AppendLine("pushTarget|" + PushTarget);
                if (!string.IsNullOrEmpty(ScrcpyDir)) sbSettings.AppendLine("scrcpyDir|" + ScrcpyDir);
                File.WriteAllText(SettingsPath, sbSettings.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // ---------- 供命令行调用的封装（和菜单点一下等价）----------
        public static string StatusText()
        {
            int total, mic;
            string serial = null;
            lock (Sessions)
            {
                total = Sessions.Count;
                mic = Sessions.Count(x => x.IsMic);
                var main = Sessions.FirstOrDefault(x => !x.IsMic);
                if (main != null) serial = main.Serial;
                if (string.IsNullOrEmpty(serial)) serial = LastSerial;
            }
            if (total == 0) return "状态：待机";

            var mode = ShowVideo ? "画面+音频" : "仅音频";
            if (mic > 0 && total > mic) mode += "+麦克风";
            return string.IsNullOrEmpty(serial) ? "状态：" + mode : "状态：" + mode + " · " + serial;
        }

        public static void SetShowVideo(bool want)
        {
            if (ShowVideo == want) return;
            ShowVideo = want;
            if (VideoItem != null) VideoItem.Checked = want;

            var serial = LastSerial;
            var mode = LastMode;
            bool had;
            lock (Sessions) had = Sessions.Count > 0;

            if (had && !string.IsNullOrEmpty(serial))
            {
                StopAllSessions(false);
                StartSession(serial, mode ?? "手动", false);
            }
            SyncMicSession();
            UpdateStatus();
            Toast.Show(want ? "画面已开启" : "画面已关闭", want ? "音视频同步输出中" : "已切换为仅音频");
        }


        public static void SetClipboardSync(bool want)
        {
            ClipboardSync = want;
            if (ClipItem != null) ClipItem.Checked = want;
            if (PrimarySerial != null) RestartForModeChange();
        }

        public static void SetAllowMultiple(bool want)
        {
            AllowMultiple = want;
            if (MultiItem != null) MultiItem.Checked = want;
            UpdateStatus();
        }

        public static void SetWindowTopMost(bool want)
        {
            WindowTopMost = want;
            if (TopItem != null) TopItem.Checked = want;
            ApplyWindowTopMost(want);
        }

        public static void OpenFileBrowser()
        {
            var serial = PrimarySerial;
            if (string.IsNullOrEmpty(serial))
            {
                MessageBox.Show("还没有连接手机。", "手机文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var d = new FileBrowserDialog(serial)) d.ShowDialog();
        }

        public static void OpenProcessTree()
        {
            var serial = PrimarySerial;
            if (string.IsNullOrEmpty(serial))
            {
                MessageBox.Show("还没有连接手机。", "进程树", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var f = new ProcessTreeDialog(serial)) f.ShowDialog();
        }

        /// <summary>命令行推文件：走和菜单一样的逻辑，但不弹文件框</summary>
        public static string CliPush(string localPath)
        {
            var serial = PrimarySerial;
            if (string.IsNullOrEmpty(serial)) return "ERR 还没有连接手机";

            EnsureSettings();
            var target = string.IsNullOrEmpty(PushTarget) ? Transfer.RemoteDir : PushTarget;
            RunAdb("-s " + serial + " shell mkdir -p " + "'" + target + "'", 15000);

            var output = RunAdb("-s " + serial + " push \"" + localPath + "\" \"" + target + "/\"", 600000);
            if (output.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                return "ERR 推送失败：" + output.Trim();

            Toast.Show("推送完成", Path.GetFileName(localPath) + " → " + target);
            return "OK 已推送 " + Path.GetFileName(localPath) + " → " + target;
        }

        public static string CommandHelp()
        {
            return string.Join(Environment.NewLine, new[]
            {
                "传声 v" + AppVersion + " · 命令行用法",
                "",
                "连接",
                "  --connect-usb               连接 USB 设备（第一个）",
                "  --connect-wifi <ip:端口>    直连指定地址",
                "  --connect                   打开选设备窗口",
                "  --pair <ip:端口> <6位码>     首次连某台手机时配对",
                "  --devices                   列出设备与可发现的 WiFi 设备",
                "",
                "开关（on / off / 省略=切换）",
                "  --video on|off              画面窗口（开画面时自动带麦克风）",
                "  --monitor-mic on|off        监听手机麦克风（默认关，与画面独立）",
                "  --share-mic on|off          共享手机麦克风（默认关，开画面时自动跟随）",
                "  --clipboard on|off          剪贴板同步（重启当前连接生效）",
                "  --multi on|off              允许同时多路音频",
                "  --topmost on|off            画面窗口置顶",
                "  --notify on|off             Windows 通知",
                "",
                "音量",
                "  --volume-phone <0-100>      手机媒体音量",
                "  --volume-pc <0-100>         电脑侧音量",
                "",
                "文件与进程",
                "  --push <本地文件路径>       推到手机当前目标目录",
                "  --pull                      打开「从手机取文件」",
                "  --files                     打开文件浏览",
                "  --ps                        打开进程树",
                "",
                "其他",
                "  --stop                      停止当前这一路",
                "  --stop-all                  停止全部",
                "  --status                    查询状态",
                "  --exit                      退出程序",
                "  --help                      显示本帮助"
            });
        }
        // ---------- 日志（供调试窗口）----------
        private static readonly object LogLock = new object();
        private static readonly List<string> LogLines = new List<string>();
        public static event Action<string> LogAppended;

        public static void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (LogLock)
            {
                LogLines.Add(line);
                if (LogLines.Count > 3000) LogLines.RemoveRange(0, LogLines.Count - 3000);
            }
            var handler = LogAppended;
            if (handler != null)
            {
                try { handler(line); } catch { }
            }
        }

        public static string GetLogText()
        {
            lock (LogLock) return string.Join(Environment.NewLine, LogLines);
        }

        // ---------- 连接 ----------
        public static void ConnectUsb()
        {
            var devices = ListDevices();
            var usb = devices.Where(d => !d.Contains(":")).ToList();
            if (usb.Count == 0)
            {
                MessageBox.Show(
                    "没有检测到 USB 连接的手机。\r\n\r\n检查：\r\n  1) 数据线支持传输（不是只能充电）\r\n  2) 手机已开 USB 调试并在弹窗点「允许」",
                    "USB 连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            StartSession(usb[0], "USB");
        }

        /// <summary>从 mdns 里找正在等待配对的设备（手机开着「使用配对码配对设备」时才有）</summary>
        public static List<string> DiscoverPairing()
        {
            var list = new List<string>();
            var output = RunAdb("mdns services");
            foreach (var line in output.Split('\n'))
            {
                if (line.IndexOf("_adb-tls-pairing", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var m = Regex.Match(line, @"(\d{1,3}(?:\.\d{1,3}){3}:\d+)");
                if (m.Success && !list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
            }
            return list;
        }

        /// <summary>配对一台新手机：adb pair → 再自动找连接端口并连上</summary>
        public static void ShowPairDialog()
        {
            var suggested = "";
            var found = DiscoverPairing();
            if (found.Count > 0) suggested = found[0];

            using (var dlg = new PairDialog(suggested))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                var addr = dlg.Addr;
                var code = dlg.Code;

                Cursor.Current = Cursors.WaitCursor;
                string result;
                try { result = RunAdb("pair " + addr + " " + code, 30000); }
                finally { Cursor.Current = Cursors.Default; }

                if (result.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    MessageBox.Show("配对失败：\r\n\r\n" + result.Trim()
                        + "\r\n\r\n检查三点：\r\n"
                        + "  1) 填的是「IP:配对端口」，不是下面的连接端口\r\n"
                        + "  2) 配对码是 6 位数字\r\n"
                        + "  3) 手机和电脑在同一个 WiFi",
                        "配对新设备", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Toast.Show("配对成功", "正在查找可连接的地址…");

                var ip = addr.Contains(":") ? addr.Substring(0, addr.IndexOf(':')) : addr;
                string target = null;
                for (int i = 0; i < 12 && target == null; i++)
                {
                    target = DiscoverWifi().FirstOrDefault(a => a.StartsWith(ip + ":"));
                    if (target == null) Thread.Sleep(500);
                }

                if (target == null)
                {
                    MessageBox.Show("配对成功 ✅\r\n\r\n但这会儿还没扫描到可连接的地址。\r\n"
                        + "请让手机停在「无线调试」页面，然后点「WiFi 连接…」→ 刷新，选 " + ip + " 开头那一项。",
                        "配对新设备", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string detail;
                if (TryConnect(target, out detail))
                {
                    StartSession(target, "WiFi");
                }
                else
                {
                    MessageBox.Show("已配对，但连接 " + target + " 失败：\r\n\r\n" + detail.Trim(),
                        "配对新设备", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }
        public static void ShowConnectDialog(bool preferWifi)
        {
            using (var dlg = new ConnectDialog(preferWifi))
            {
                if (dlg.ShowDialog() != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedAddress)) return;

                var addr = dlg.SelectedAddress;

                // USB 设备（串号里没有冒号）：直接启动
                if (!addr.Contains(":"))
                {
                    StartSession(addr, "USB");
                    return;
                }

                // WiFi / tcpip 地址：先 adb connect
                string detail;
                if (TryConnect(addr, out detail)) { StartSession(addr, "WiFi"); return; }

                // 失败常见原因：mdns 缓存了旧端口 / 手机断网 / 无线调试被关
                // 自愈：重启 adb server，重新发现一次；只发现一个就直接连
                RunAdb("kill-server", 8000);
                Thread.Sleep(800);
                RunAdb("start-server", 8000);
                Thread.Sleep(600);

                var fresh = DiscoverWifi();
                if (fresh.Count == 1 && TryConnect(fresh[0], out detail))
                {
                    StartSession(fresh[0], "WiFi");
                    return;
                }

                var msg = "连接 " + addr + " 失败：\r\n\r\n" + detail.Trim() + "\r\n\r\n"
                        + (detail.Contains("10061")
                            ? "「目标计算机积极拒绝」= 这个地址上没有服务在监听，通常是：\r\n"
                              + "  · mdns 缓存了旧端口（无线调试的端口每次重启都会变）\r\n"
                              + "  · 手机断开了 WiFi，或关掉了「无线调试」\r\n\r\n"
                            : "")
                        + "刚重新扫描到：" + (fresh.Count == 0 ? "（没有设备）" : string.Join("，", fresh))
                        + "\r\n\r\n建议：\r\n"
                        + "  · 手机确认 WiFi 连着 + 无线调试是开着的，然后重新打开这个窗口刷新列表\r\n"
                        + "  · 如果这是【第一次】连这台手机，要先配对：连接手机 → 配对新设备…";

                MessageBox.Show(msg, "WiFi 连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public static bool TryConnect(string addr, out string detail)
        {
            detail = RunAdb("connect " + addr, 15000);
            Thread.Sleep(500);
            var devices = ListDevices();
            return devices.Contains(addr);
        }

        public static void StartSession(string serial, string mode, bool notifyConnect = true)
        {
            if (!File.Exists(ScrcpyExe))
            {
                MessageBox.Show("找不到 scrcpy.exe：\r\n" + ScrcpyExe, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var already = Sessions.FirstOrDefault(s => s.Serial == serial && !s.IsMic);
            if (already != null)
            {
                MessageBox.Show("这个设备已经在转发了：" + serial, "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!AllowMultiple && Sessions.Count > 0) StopAllSessions(false);

            try
            {
                string scrcpyArgs;
                if (ShowVideo)
                {
                    // 视频+音频同一个进程：scrcpy 内部共用时钟，音画天然同步
                    // 剪贴板同步是 scrcpy 默认行为（不传 --no-clipboard-autosync 即为开启）
                    scrcpyArgs = "-s " + serial
                        + " --window-title \"手机画面 · " + serial + "\""
                        + " --max-size=1280 --max-fps=60"
                        + " --window-x=120 --window-y=60";
                }
                else
                {
                    // 纯音频：无任何窗口
                    scrcpyArgs = "--no-video --no-window -s " + serial;
                }

                if (!ClipboardSync) scrcpyArgs += " --no-clipboard-autosync";

                var psi = new ProcessStartInfo(ScrcpyExe, scrcpyArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,           // 始终不要控制台窗口；画面由 scrcpy 自己的 SDL 窗口显示
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = BaseDir
                };
                var p = Process.Start(psi);
                p.OutputDataReceived += (s2, ev) => { if (ev.Data != null) AppendLog(ev.Data); };
                p.ErrorDataReceived += (s2, ev) => { if (ev.Data != null) AppendLog(ev.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                JobObject.Add(p);
                LastSerial = serial;
                LastMode = mode;
                var session = new Session { Proc = p, Serial = serial, Mode = mode, IsVideo = ShowVideo };
                p.EnableRaisingEvents = true;
                p.Exited += (s, e) =>
                {
                    Session gone;
                    lock (Sessions)
                    {
                        gone = Sessions.FirstOrDefault(x => x.Proc == p);
                        Sessions.RemoveAll(x => x.Proc == p);
                    }

                    var wasVideo = gone != null && gone.IsVideo;
                    var byUs = gone != null && gone.KilledByUs;

                    Post(() =>
                    {
                        UpdateStatus();
                        HandleSessionExit(serial, mode, wasVideo, byUs);
                    });
                };
                lock (Sessions) Sessions.Add(session);

                // 画面模式：等窗口出现后，按当前置顶设置应用一次
                if (ShowVideo)
                {
                    var proc = p;
                    var th = new Thread(() =>
                    {
                        for (int i = 0; i < 40; i++)
                        {
                            try { if (proc.HasExited) return; } catch { return; }
                            var hwnd = NativeWin.FindWindowByPid(proc.Id);
                            if (hwnd != IntPtr.Zero)
                            {
                                if (WindowTopMost) NativeWin.SetTopMost(hwnd, true);
                                return;
                            }
                            Thread.Sleep(200);
                        }
                    });
                    th.IsBackground = true;
                    th.Start();
                }

                ApplyPcVolume(PcVolume);

                // 画面开启时：共享麦克风自动跟随（默认关，但开画面就开）
                if (ShowVideo && ShareMic) StartMicSession(serial);

                UpdateStatus();
                if (notifyConnect) Toast.Show("传声 · 已连接", mode + " · " + serial);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动 scrcpy 失败：\r\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>把当前所有画面窗口设为置顶 / 取消置顶</summary>
        public static void ApplyWindowTopMost(bool topMost)
        {
            List<Session> copy;
            lock (Sessions) copy = Sessions.ToList();

            int done = 0;
            foreach (var s in copy)
            {
                try
                {
                    if (s.Proc == null || s.Proc.HasExited) continue;
                    var hwnd = NativeWin.FindWindowByPid(s.Proc.Id);
                    if (hwnd == IntPtr.Zero) continue;
                    NativeWin.SetTopMost(hwnd, topMost);
                    done++;
                }
                catch { }
            }
            LastTopMostDone = done;
        }

        public static int LastTopMostDone;
        /// <summary>模式类开关（画面/剪贴板）改动后，重启当前这一路使其生效。</summary>
        public static void RestartForModeChange()
        {
            var serial = LastSerial;
            var mode = LastMode;
            bool had;
            lock (Sessions) had = Sessions.Count > 0;

            if (had && !string.IsNullOrEmpty(serial))
            {
                StopAllSessions();
                StartSession(serial, mode ?? "手动");
            }
            UpdateStatus();
        }

        /// <summary>未处理异常：写日志，尽量提示用户，避免进程无声消失</summary>
        private static void LogFatal(Exception ex, string where)
        {
            try
            {
                var log = Path.Combine(BaseDir, "crash.log");
                File.AppendAllText(log,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  " + ex + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }

            try
            {
                MessageBox.Show("程序遇到一个错误，已记录到 crash.log：\r\n\r\n" + (ex != null ? ex.Message : "未知错误"),
                    "传声", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        /// <summary>把一段代码投递到 UI 线程执行（后台线程安全，不依赖窗口句柄）</summary>
        public static void Post(Action action)
        {
            if (action == null) return;
            try { UiActions.Enqueue(action); } catch { }
        }

        /// <summary>当前是否需要有麦克风这一路：监听勾上，或（画面开着且共享允许）</summary>
        public static bool NeedMicSession()
        {
            if (MonitorMic) return true;
            if (ShowVideo && ShareMic) return true;
            return false;
        }

        /// <summary>按当前状态增删麦克风这一路</summary>
        public static void SyncMicSession()
        {
            var serial = PrimarySerial;
            if (string.IsNullOrEmpty(serial))
            {
                // 还没连上任何一路时，退而用 adb 里第一个在线的设备
                try { serial = ListDevices().FirstOrDefault(); } catch { }
            }

            if (NeedMicSession() && !string.IsNullOrEmpty(serial)) StartMicSession(serial);
            else StopMicSessions();
        }

        /// <summary>共享麦克风（无菜单项，默认关；开画面时自动跟随）</summary>
        /// <summary>共享麦克风（无菜单项，默认关；开画面时自动跟随）</summary>
        public static void SetShareMic(bool want)
        {
            ShareMic = want;
            SyncMicSession();
            UpdateStatus();
        }

        /// <summary>监听手机麦克风（菜单开关，默认关，与画面独立）</summary>
        public static void SetMonitorMic(bool want)
        {
            MonitorMic = want;
            if (MicItem != null) MicItem.Checked = want;
            SyncMicSession();
            UpdateStatus();
        }

        /// <summary>单独开一路 scrcpy 抓手机麦克风（音频源 mic），与画面进程互不影响</summary>
        public static void StartMicSession(string serial)
        {
            lock (Sessions) if (Sessions.Any(s => s.Serial == serial && s.IsMic)) return;

            try
            {
                var psi = new ProcessStartInfo(ScrcpyExe,
                    "--no-video --no-window --audio-source=mic -s " + serial)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = BaseDir
                };
                var p = Process.Start(psi);
                p.OutputDataReceived += (s2, ev) => { if (ev.Data != null) AppendLog("[mic] " + ev.Data); };
                p.ErrorDataReceived += (s2, ev) => { if (ev.Data != null) AppendLog("[mic] " + ev.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                JobObject.Add(p);

                var micSession = new Session { Proc = p, Serial = serial, Mode = "麦克风", IsMic = true };
                p.EnableRaisingEvents = true;
                p.Exited += (s2, e) =>
                {
                    lock (Sessions) Sessions.RemoveAll(x => x.Proc == p);
                    Post(UpdateStatus);
                };
                lock (Sessions) Sessions.Add(micSession);

                ApplyPcVolume(PcVolume);
            }
            catch { }
        }

        public static void StopMicSessions()
        {
            List<Session> mics;
            lock (Sessions) mics = Sessions.Where(s => s.IsMic).ToList();
            foreach (var m in mics) Kill(m);
        }
        // ---------- 停止 ----------
        public static void StopLatest()
        {
            Session last;
            lock (Sessions) last = Sessions.LastOrDefault();
            if (last == null) return;
            Kill(last);
            Toast.Show("已停止", "音频共享已停止");
        }

        public static void StopAllSessions(bool notify = true)
        {
            List<Session> copy;
            lock (Sessions) copy = Sessions.ToList();
            foreach (var s in copy) Kill(s);

            if (notify && copy.Count > 0)
                Toast.Show("已停止", copy.Count == 1 ? "音频共享已停止" : "已停止 " + copy.Count + " 路音频共享");
        }

        private static void Kill(Session s)
        {
            try { s.KilledByUs = true; } catch { }
            try { if (s.Proc != null && !s.Proc.HasExited) s.Proc.Kill(); } catch { }
            lock (Sessions) Sessions.Remove(s);
        }

        public static void ShutdownAll()
        {
            StopAllSessions(false);
            if (Tray != null) { Tray.Visible = false; Tray.Dispose(); }
        }

        private static DateTime _lastFallback = DateTime.MinValue;
        private static int _fallbackCount;

        /// <summary>
        /// 一路退出后的收尾：用户把画面窗口关掉时，scrcpy 进程会结束（窗口即进程），
        /// 这时自动退回「仅音频」，让连接和声音继续，而不是掉回待机。
        /// </summary>
        private static void HandleSessionExit(string serial, string mode, bool wasVideo, bool byUs)
        {
            if (!wasVideo || byUs) return;                 // 主动停止 / 本来就是纯音频
            lock (Sessions) if (Sessions.Count > 0) return; // 还有别的路在跑，不动

            bool online;
            try { online = ListDevices().Contains(serial); }
            catch { online = false; }

            // 设备都不在了：老老实实回待机，等用户重连
            if (!online) { UpdateStatus(); return; }

            // 防抖：1 分钟内连续崩 3 次就别再重启了
            if ((DateTime.Now - _lastFallback).TotalSeconds > 60) _fallbackCount = 0;
            _lastFallback = DateTime.Now;
            _fallbackCount++;

            ShowVideo = false;
            if (VideoItem != null) VideoItem.Checked = false;
            SyncMicSession();

            if (_fallbackCount > 3)
            {
                UpdateStatus();
                Toast.Show("画面反复退出", "已停止自动重试，请检查连接");
                return;
            }

            Toast.Show("画面窗口已关闭", "已切换为「仅音频」，声音继续");
            StartSession(serial, mode, false);
        }

        public static void UpdateStatus()
        {
            int n;
            string desc;
            lock (Sessions)
            {
                n = Sessions.Count;
                var mic = Sessions.Count(x => x.IsMic);
                if (n == 0)
                {
                    desc = "待机";
                }
                else if (mic > 0 && n > mic)
                {
                    desc = "画面+音频+麦克风 · " + Sessions.First(x => !x.IsMic).Serial;
                }
                else if (mic > 0)
                {
                    desc = "仅麦克风 · " + Sessions.First(x => x.IsMic).Serial;
                }
                else if (n == 1)
                {
                    desc = Sessions[0].Mode + (ShowVideo ? " · 画面+音频" : " · 仅音频") + " · " + Sessions[0].Serial;
                }
                else
                {
                    desc = n + " 路同时转发";
                }
            }

            StatusItem.Text = "状态：" + desc;
            var text = "传声 · " + desc;
            if (text.Length > 63) text = text.Substring(0, 63);
            Tray.Text = text;
        }

        // ---------- 音量 ----------
        // 说明：scrcpy 转发的是手机侧的音量级，电脑只能往下衰减、不能放大。
        // 所以这里分成两路：手机音量（真正的主音量）＋ 电脑侧（仅衰减）。
        public static float PcVolume = 1.0f;
        public static int PhoneVolumePct = -1;   // -1 = 尚未读取

        public static string PrimarySerial
        {
            get
            {
                lock (Sessions)
                {
                    if (Sessions.Count == 0) return null;
                    return Sessions[Sessions.Count - 1].Serial;
                }
            }
        }

        public static void SetPcVolume(float v)
        {
            PcVolume = v;
            ApplyPcVolume(v);
        }

        public static void ApplyPcVolume(float v)
        {
            List<uint> pids;
            lock (Sessions)
                pids = Sessions.Where(s => s.Proc != null && !s.Proc.HasExited)
                               .Select(s => (uint)s.Proc.Id).ToList();
            if (pids.Count == 0) return;

            try { AudioSession.SetVolumeForProcesses(pids, v); }
            catch (Exception ex)
            {
                MessageBox.Show("调节电脑侧音量失败：" + ex.Message, "音量", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ----- 手机音量（真正的主音量）-----
        public static int GetPhoneMaxVolume(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return 15;
            var output = RunAdb("-s " + serial + " shell cmd media_session volume --stream 3 --get");
            var m = Regex.Match(output, @"range \[0\.\.(\d+)\]");
            if (m.Success)
            {
                int max;
                if (int.TryParse(m.Groups[1].Value, out max) && max > 0) return max;
            }
            return 15;
        }

        public static int GetPhoneVolume(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return -1;
            var output = RunAdb("-s " + serial + " shell cmd media_session volume --stream 3 --get");
            var m = Regex.Match(output, @"volume is (\d+)");
            if (m.Success)
            {
                int v;
                if (int.TryParse(m.Groups[1].Value, out v)) return v;
            }
            return -1;
        }

        public static bool SetPhoneVolumePct(int pct)
        {
            var serial = PrimarySerial;
            if (string.IsNullOrEmpty(serial))
            {
                MessageBox.Show("还没有连接手机。先连接再调手机音量。", "手机音量",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            int max = GetPhoneMaxVolume(serial);
            int cur = GetPhoneVolume(serial);
            if (cur < 0) cur = 0;

            int target = (int)Math.Round((max * pct) / 100.0);
            if (target < 0) target = 0;
            if (target > max) target = max;

            int delta = target - cur;
            if (delta != 0)
            {
                // vivo 等机型会拦截 cmd media_session --set（只打日志不生效），
                // 但按键事件一定生效，所以用 KEYCODE_VOLUME_UP/DOWN 一步步推。
                string key = delta > 0 ? "24" : "25";
                var codes = new StringBuilder();
                for (int i = 0; i < Math.Abs(delta); i++) codes.Append(key).Append(' ');
                RunAdb("-s " + serial + " shell input keyevent " + codes.ToString().Trim(), 15000);
                Thread.Sleep(250);
            }

            int actual = GetPhoneVolume(serial);
            if (actual >= 0 && max > 0)
                PhoneVolumePct = (int)Math.Round(actual * 100.0 / max);
            else
                PhoneVolumePct = pct;
            return true;
        }

        public static void ShowVolumeDialog()
        {
            var serial = PrimarySerial;
            if (PhoneVolumePct < 0 && !string.IsNullOrEmpty(serial))
            {
                int max = GetPhoneMaxVolume(serial);
                int cur = GetPhoneVolume(serial);
                if (cur >= 0 && max > 0) PhoneVolumePct = (int)Math.Round(cur * 100.0 / max);
            }

            using (var dlg = new VolumeDialog(PcVolume, PhoneVolumePct < 0 ? 50 : PhoneVolumePct, serial != null))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;

                SetPcVolume(dlg.PcValue);
                if (dlg.PhoneValue >= 0) SetPhoneVolumePct(dlg.PhoneValue);
            }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        public TrayContext() { }
        protected override void ExitThreadCore()
        {
            Program.ShutdownAll();
            base.ExitThreadCore();
        }
    }
}
