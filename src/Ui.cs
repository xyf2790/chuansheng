// Ui.cs — 连接对话框 / 音量对话框 / 音频会话 COM 互操作
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    // ---------- 连接对话框 ----------
    internal sealed class ConnectDialog : Form
    {
        private RadioButton rbUsb;
        private RadioButton rbWifi;
        private ListView list;
        private Button btnRefresh;
        private Button btnOk;
        private Button btnCancel;
        private TextBox txtManual;
        private Button btnManual;
        private Label lblHint;

        public string SelectedAddress { get; private set; }

        public ConnectDialog() : this(false) { }

        public ConnectDialog(bool preferWifi)
        {
            Text = "连接手机";
            Size = new Size(520, 420);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            rbUsb = new RadioButton { Text = "USB 连接", Left = 18, Top = 14, Width = 110, Checked = !preferWifi };
            rbWifi = new RadioButton { Text = "WiFi 连接", Left = 140, Top = 14, Width = 110, Checked = preferWifi };
            Controls.Add(rbUsb);
            Controls.Add(rbWifi);

            btnRefresh = new Button { Text = "刷新", Left = 400, Top = 10, Width = 96, Height = 28 };
            btnRefresh.Click += (s, e) => RefreshList();
            Controls.Add(btnRefresh);

            var btnRename = new Button { Text = "重命名…", Left = 300, Top = 10, Width = 92, Height = 28 };
            btnRename.Click += (s, e) => RenameSelected();
            Controls.Add(btnRename);

            list = new ListView
            {
                Left = 18,
                Top = 48,
                Width = 478,
                Height = 232,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false
            };
            list.Columns.Add("设备", 220);
            list.Columns.Add("地址 / 串号", 180);
            list.Columns.Add("类型", 70);
            list.DoubleClick += (s, e) => { if (list.SelectedItems.Count > 0) AcceptSelected(); };
            Controls.Add(list);

            lblHint = new Label
            {
                Left = 18,
                Top = 288,
                Width = 478,
                Height = 34,
                ForeColor = Color.DimGray,
                Text = "WiFi 需要手机开启「无线调试」（或先 USB 执行 adb tcpip 5555）。"
            };
            Controls.Add(lblHint);

            txtManual = new TextBox { Left = 18, Top = 326, Width = 250, Text = "" };
            Controls.Add(txtManual);

            btnManual = new Button { Text = "手动 IP:端口 连接", Left = 276, Top = 324, Width = 130, Height = 28 };
            btnManual.Click += (s, e) =>
            {
                var addr = txtManual.Text.Trim();
                if (addr.Length == 0) return;
                SelectedAddress = addr;
                DialogResult = DialogResult.OK;
            };
            Controls.Add(btnManual);

            btnOk = new Button { Text = "连接", Left = 296, Top = 366, Width = 96, Height = 30, DialogResult = DialogResult.None };
            btnOk.Click += (s, e) => AcceptSelected();
            Controls.Add(btnOk);

            btnCancel = new Button { Text = "取消", Left = 400, Top = 366, Width = 96, Height = 30, DialogResult = DialogResult.Cancel };
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            rbUsb.CheckedChanged += (s, e) => RefreshList();
            rbWifi.CheckedChanged += (s, e) => RefreshList();

            Shown += (s, e) => RefreshList();
        }

        private void AcceptSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            var info = list.SelectedItems[0].Tag as Program.DeviceInfo;
            if (info != null) SelectedAddress = info.Address;
            DialogResult = DialogResult.OK;
        }

        private void RenameSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            var info = list.SelectedItems[0].Tag as Program.DeviceInfo;
            if (info == null) return;

            using (var dlg = new InputForm("重命名设备", "给「" + info.Name + "」起个名字（按串号保存，换端口不会丢）：", info.Name))
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                Program.RenameDevice(info.Serial, dlg.Value);
                info.Name = string.IsNullOrWhiteSpace(dlg.Value) ? info.Name : dlg.Value.Trim();
                RefreshList();
            }
        }

        private void RefreshList()
        {
            list.Items.Clear();
            Cursor = Cursors.WaitCursor;
            try
            {
                if (rbUsb.Checked)
                {
                    var devices = Program.ListDevices().Where(d => !d.Contains(":")).ToList();
                    foreach (var d in devices)
                    {
                        var name = Program.QueryDeviceName(d);
                        var info = new Program.DeviceInfo { Address = d, Serial = d, Name = name };
                        list.Items.Add(new ListViewItem(new[] { name, d, "USB" }) { Tag = info });
                    }
                    lblHint.Text = devices.Count == 0
                        ? "没发现 USB 设备。检查数据线与「允许 USB 调试」弹窗。"
                        : "双击设备即可连接；「重命名…」可给设备起别名（按串号保存）。";
                }
                else
                {
                    var devices = Program.DiscoverWifiDetailed();
                    foreach (var d in devices)
                    {
                        var info = new Program.DeviceInfo { Address = d.Address, Serial = d.Serial, Name = d.Name };
                        list.Items.Add(new ListViewItem(new[] { d.Name, d.Address, "WiFi" }) { Tag = info });
                    }
                    lblHint.Text = devices.Count == 0
                        ? "没发现无线设备。手机需开「无线调试」并与电脑同一 WiFi（或先 USB 执行 adb tcpip 5555）。"
                        : "双击即可连接；「重命名…」按串号保存，换端口不会丢。";
                }
                if (list.Items.Count > 0) list.Items[0].Selected = true;
            }
            finally { Cursor = Cursors.Default; }
        }
    }

    // ---------- 音量对话框（手机 + 电脑侧）----------
    internal sealed class VolumeDialog : Form
    {
        private TrackBar phoneBar;
        private TrackBar pcBar;
        private Label phoneValue;
        private Label pcValue;

        public float PcValue { get; private set; }
        public int PhoneValue { get; private set; }

        public VolumeDialog(float pcVolume, int phonePct, bool phoneAvailable)
        {
            Text = "音量";
            Size = new Size(470, 320);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            PcValue = pcVolume;
            PhoneValue = phonePct;

            var lbl1 = new Label
            {
                Left = 18, Top = 16, Width = 400,
                Text = "手机音量（主音量，真正决定声音大小）"
            };
            Controls.Add(lbl1);

            phoneBar = new TrackBar
            {
                Left = 18, Top = 38, Width = 360, Minimum = 0, Maximum = 100,
                TickFrequency = 10, Value = Math.Max(0, Math.Min(100, phonePct))
            };
            phoneBar.Enabled = phoneAvailable;
            Controls.Add(phoneBar);

            phoneValue = new Label { Left = 388, Top = 48, Width = 60, Text = phonePct + "%" };
            Controls.Add(phoneValue);

            var hint = new Label
            {
                Left = 18, Top = 92, Width = 430, Height = 30,
                ForeColor = Color.DimGray,
                Text = phoneAvailable
                    ? "改这里会直接改手机的媒体音量（adb 下发）。"
                    : "还没连接手机，手机音量不可调。"
            };
            Controls.Add(hint);

            var lbl2 = new Label
            {
                Left = 18, Top = 128, Width = 400,
                Text = "电脑侧音量（只能往下衰减，不能放大）"
            };
            Controls.Add(lbl2);

            pcBar = new TrackBar
            {
                Left = 18, Top = 150, Width = 360, Minimum = 0, Maximum = 100,
                TickFrequency = 10, Value = (int)Math.Round(pcVolume * 100)
            };
            Controls.Add(pcBar);

            pcValue = new Label { Left = 388, Top = 160, Width = 60, Text = ((int)Math.Round(pcVolume * 100)) + "%" };
            Controls.Add(pcValue);

            var btnApply = new Button { Text = "应用并关闭", Left = 250, Top = 234, Width = 110, Height = 32, DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Left = 370, Top = 234, Width = 80, Height = 32, DialogResult = DialogResult.Cancel };
            Controls.Add(btnApply);
            Controls.Add(btnCancel);
            AcceptButton = btnApply;
            CancelButton = btnCancel;

            phoneBar.ValueChanged += (s, e) =>
            {
                phoneValue.Text = phoneBar.Value + "%";
            };
            pcBar.ValueChanged += (s, e) =>
            {
                pcValue.Text = pcBar.Value + "%";
                Program.SetPcVolume(pcBar.Value / 100f);
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (DialogResult == DialogResult.OK)
            {
                PcValue = pcBar.Value / 100f;
                PhoneValue = phoneBar.Enabled ? phoneBar.Value : -1;
            }
        }
    }

    // ---------- 通用输入框 ----------
    internal sealed class InputForm : Form
    {
        private TextBox box;
        public string Value { get; private set; }

        public InputForm(string title, string prompt, string initial)
        {
            Text = title;
            Size = new Size(460, 190);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            var lbl = new Label { Left = 16, Top = 14, Width = 420, Height = 34, Text = prompt };
            Controls.Add(lbl);

            box = new TextBox { Left = 16, Top = 52, Width = 420, Text = initial ?? "" };
            Controls.Add(box);

            var ok = new Button { Left = 260, Top = 96, Width = 84, Height = 30, Text = "确定", DialogResult = DialogResult.OK };
            var cancel = new Button { Left = 352, Top = 96, Width = 84, Height = 30, Text = "取消", DialogResult = DialogResult.Cancel };
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            Load += (s, e) => { box.Focus(); box.SelectAll(); };
            FormClosing += (s, e) => { Value = box.Text; };
        }
    }

    // ---------- 配对对话框（Android 11+ 无线调试首次连接需要）----------
    internal sealed class PairDialog : Form
    {
        private TextBox txtAddr;
        private TextBox txtCode;
        public string Addr { get; private set; }
        public string Code { get; private set; }

        public PairDialog(string suggestedAddr)
        {
            Text = "配对新设备";
            Size = new Size(520, 300);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            var tip = new Label
            {
                Left = 16, Top = 12, Width = 470, Height = 76,
                Text = "手机上传第一次连这台电脑，需要先配对：\r\n"
                     + "手机：开发者选项 → 无线调试 → 「使用配对码配对设备」\r\n"
                     + "把弹窗里的「IP 地址和端口」「配对码」填到下面。"
            };
            Controls.Add(tip);

            var l1 = new Label { Left = 16, Top = 96, Width = 110, Text = "IP:配对端口" };
            Controls.Add(l1);
            txtAddr = new TextBox { Left = 132, Top = 93, Width = 350, Text = suggestedAddr ?? "" };
            Controls.Add(txtAddr);

            var l2 = new Label { Left = 16, Top = 132, Width = 110, Text = "6 位配对码" };
            Controls.Add(l2);
            txtCode = new TextBox { Left = 132, Top = 129, Width = 160 };
            Controls.Add(txtCode);

            var hint = new Label
            {
                Left = 16, Top = 166, Width = 470, Height = 34,
                ForeColor = Color.DimGray,
                Text = "配对端口和下面的连接端口不是同一个；配对成功后程序会自动去找连接端口。"
            };
            Controls.Add(hint);

            var ok = new Button { Left = 300, Top = 212, Width = 88, Height = 32, Text = "配对" };
            var cancel = new Button { Left = 396, Top = 212, Width = 88, Height = 32, Text = "取消", DialogResult = DialogResult.Cancel };
            Controls.Add(ok);
            Controls.Add(cancel);
            CancelButton = cancel;

            ok.Click += (s, e) =>
            {
                var a = (txtAddr.Text ?? "").Trim();
                var c = (txtCode.Text ?? "").Trim();
                if (!a.Contains(":") || c.Length == 0)
                {
                    MessageBox.Show("请把「IP:端口」和「6 位配对码」都填上。", "配对",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Addr = a;
                Code = c;
                DialogResult = DialogResult.OK;
            };
        }
    }
    // ---------- 调试窗口：scrcpy 日志 + 可执行 adb 命令 ----------
    internal sealed class DebugForm : Form
    {
        private TextBox logBox;
        private TextBox cmdBox;
        private ComboBox cboTarget;
        private string serial;
        private Action<string> handler;

        public DebugForm(string serial)
        {
            this.serial = serial;

            Text = "调试窗口 — scrcpy 日志 / adb 命令";
            Size = new Size(900, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            logBox = new TextBox
            {
                Left = 12, Top = 12, Width = 860, Height = 470,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                Text = Program.GetLogText()
            };
            Controls.Add(logBox);

            var lbl = new Label { Left = 12, Top = 492, Width = 56, Text = "执行：" };
            Controls.Add(lbl);

            cboTarget = new ComboBox { Left = 70, Top = 489, Width = 138, DropDownStyle = ComboBoxStyle.DropDownList };
            cboTarget.Items.Add("手机（adb shell）");
            cboTarget.Items.Add("本机（程序命令）");
            cboTarget.SelectedIndex = 0;
            Controls.Add(cboTarget);

            cmdBox = new TextBox { Left = 216, Top = 489, Width = 504, Text = "cmd media_session volume --stream 3 --get" };
            Controls.Add(cmdBox);

            var btnRun = new Button { Left = 730, Top = 487, Width = 60, Height = 26, Text = "执行" };
            btnRun.Click += (s, e) => RunCommand();
            Controls.Add(btnRun);

            var btnClear = new Button { Left = 798, Top = 487, Width = 60, Height = 26, Text = "清空" };
            btnClear.Click += (s, e) => logBox.Clear();
            Controls.Add(btnClear);

            var hint = new Label
            {
                Left = 12, Top = 522, Width = 860,
                ForeColor = Color.DimGray,
                Text = "上面是 scrcpy 实时日志。下面：手机模式跑手机的 adb shell 命令；本机模式跑本程序命令（如 --status、--video on）。"
            };
            Controls.Add(hint);

            var btnClose = new Button { Left = 776, Top = 546, Width = 82, Height = 28, Text = "关闭", DialogResult = DialogResult.Cancel };
            Controls.Add(btnClose);
            CancelButton = btnClose;

            handler = line =>
            {
                if (logBox.IsDisposed) return;
                try { logBox.AppendText(Environment.NewLine + line); }
                catch { }
            };
            Program.LogAppended += handler;

            cmdBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RunCommand(); }
            };
        }

        /// <summary>判断这行是不是本程序命令（而不是手机命令）</summary>
        private static bool LooksLikeLocalCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;

            var s = cmd.Trim();
            if (s.StartsWith("--")) return true;                       // 纯参数

            var unquoted = s.TrimStart('"');
            if (unquoted.Length > 2 && char.IsLetter(unquoted[0]) && unquoted[1] == ':' && unquoted[2] == '\\')
                return true;                                            // D:\... 这种 Windows 路径

            if (cmd.IndexOf("PhoneAudioTray", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (cmd.IndexOf("pat.cmd", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        /// <summary>去掉命令前面的程序路径，只留参数</summary>
        private static string StripProgramPath(string cmd)
        {
            var s = (cmd ?? "").Trim();
            if (s.StartsWith("--")) return s;

            if (s.StartsWith("\""))
            {
                var end = s.IndexOf('"', 1);
                if (end > 0) return s.Substring(end + 1).Trim();
            }

            var sp = s.IndexOf(' ');
            if (sp > 0 && s.Substring(0, sp).IndexOf('\\') > 0) return s.Substring(sp + 1).Trim();
            return s;
        }

        private void RunCommand()
        {
            var cmd = cmdBox.Text.Trim();
            if (cmd.Length == 0) return;

            // 本机模式：跑程序自己的命令行指令（--status / --video on …）
            if (cboTarget.SelectedIndex == 1)
            {
                cmd = StripProgramPath(cmd);
                logBox.AppendText(Environment.NewLine + "> [本机] " + cmd);
                Cursor = Cursors.WaitCursor;
                try
                {
                    var r = Commands.Execute(cmd);
                    logBox.AppendText(Environment.NewLine + r);
                }
                finally { Cursor = Cursors.Default; }
                return;
            }

            // 手机模式：如果其实是本机命令（带盘符路径 / pat.cmd / --参数），直接在本机跑
            if (LooksLikeLocalCommand(cmd))
            {
                var localCmd = StripProgramPath(cmd);
                logBox.AppendText(Environment.NewLine + "[提示] 这是本机命令，已自动切到「本机」模式执行。");
                cboTarget.SelectedIndex = 1;
                logBox.AppendText(Environment.NewLine + "> [本机] " + localCmd);
                Cursor = Cursors.WaitCursor;
                try { logBox.AppendText(Environment.NewLine + Commands.Execute(localCmd)); }
                finally { Cursor = Cursors.Default; }
                return;
            }

            if (string.IsNullOrEmpty(serial))
            {
                logBox.AppendText(Environment.NewLine + "[错误] 还没有连接设备");
                return;
            }

            logBox.AppendText(Environment.NewLine + "> adb -s " + serial + " shell " + cmd);
            Cursor = Cursors.WaitCursor;
            try
            {
                var output = Program.RunAdb("-s " + serial + " shell " + cmd, 15000);
                logBox.AppendText(Environment.NewLine + output.TrimEnd());
            }
            finally { Cursor = Cursors.Default; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Program.LogAppended -= handler;
            base.OnFormClosed(e);
        }
    }

    // ---------- CoreAudio 互操作：按进程设置音量 ----------
    internal static class AudioSession
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags, out IntPtr sessionControl);
            int GetSimpleAudioVolume(IntPtr audioSessionGuid, int streamFlags, out ISimpleAudioVolume audioVolume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        }

        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int sessionCount);
            int GetSession(int sessionCount, out IAudioSessionControl2 session);
        }

        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            int GetState(out int state);
            int GetDisplayName(out IntPtr name);
            int SetDisplayName(string name, IntPtr eventContext);
            int GetIconPath(out IntPtr path);
            int SetIconPath(string path, IntPtr eventContext);
            int GetGroupingParam(out Guid groupingParam);
            int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
            int RegisterAudioSessionNotification(IntPtr newNotifications);
            int UnregisterAudioSessionNotification(IntPtr newNotifications);
            // IAudioSessionControl2
            int GetSessionIdentifier(out IntPtr retVal);
            int GetSessionInstanceIdentifier(out IntPtr retVal);
            int GetProcessId(out uint retVal);
            int IsSystemSoundsSession();
            int SetDuckingPreference(bool optOut);
        }

        [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float level, ref Guid eventContext);
            int GetMasterVolume(out float level);
            int SetMute(bool mute, ref Guid eventContext);
            int GetMute(out bool mute);
        }

        private const int eRender = 0;
        private const int eConsole = 0;

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr instance);

        public static void SetVolumeForProcesses(IEnumerable<uint> pids, float level)
        {
            var pidSet = new HashSet<uint>(pids);
            if (pidSet.Count == 0) return;

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out device) != 0) return;

            var iidManager = typeof(IAudioSessionManager2).GUID;
            object raw;
            if (device.Activate(ref iidManager, 0, IntPtr.Zero, out raw) != 0) return;
            var manager = raw as IAudioSessionManager2;
            if (manager == null) return;

            IAudioSessionEnumerator sessions;
            if (manager.GetSessionEnumerator(out sessions) != 0) return;

            int count;
            if (sessions.GetCount(out count) != 0) return;

            var ctx = Guid.Empty;
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2 control2;
                if (sessions.GetSession(i, out control2) != 0) continue;
                if (control2 == null) continue;

                uint pid;
                if (control2.GetProcessId(out pid) != 0) continue;
                if (!pidSet.Contains(pid)) continue;

                var simple = control2 as ISimpleAudioVolume;
                if (simple != null) simple.SetMasterVolume(level, ref ctx);
            }
        }
    }
}
