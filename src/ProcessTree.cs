// ProcessTree.cs — 手机任务管理器（平铺进程与实时占用）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal sealed class ProcInfo
    {
        public int Pid;
        public int PPid;
        public string User;
        public string Name;
        public string Started;
        public long RssKb = -1;
        public double? Cpu;
        public bool IsApp { get { return Regex.IsMatch(User ?? "", @"^u\d+_[ai]\d+$"); } }
        public bool IsKernelThread { get { return (Name ?? "").StartsWith("["); } }
        public string Package { get { return (Name ?? "").Split(':')[0]; } }
        public string DisplayName { get { var n = AppIcons.DisplayName(Package); return string.IsNullOrEmpty(n) ? Package : n; } }
        public bool CanStop { get { return Regex.IsMatch(User ?? "", @"^u\d+_a\d+$") && Regex.IsMatch(Package, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$"); } }
    }

    internal sealed class ProcessTreeDialog : Form
    {
        private readonly string serial;
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        private readonly ListView list;
        private readonly TextBox filter;
        private readonly CheckBox appsOnly;
        private readonly CheckBox autoRefresh;
        private readonly Button refresh;
        private readonly Button stop;
        private readonly Label status;
        private List<ProcInfo> allProcs = new List<ProcInfo>();
        private int sortColumn = 4;
        private bool descending = true;
        private bool busy;
        private bool closed;
        private string updated = "";
        private string refreshError = "";

        public ProcessTreeDialog(string serial)
        {
            this.serial = serial;
            Text = "手机任务管理器（" + serial + "）";
            ClientSize = new Size(960, 650);
            MinimumSize = new Size(840, 440);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            refresh = new Button { Left = 12, Top = 12, Width = 76, Height = 28, Text = "刷新" };
            Controls.Add(refresh);
            Controls.Add(new Label { Left = 102, Top = 18, Width = 44, Text = "搜索" });
            filter = new TextBox { Left = 146, Top = 15, Width = 280 };
            Controls.Add(filter);
            appsOnly = new CheckBox { Left = 444, Top = 16, Width = 124, Text = "只看应用进程", Checked = true };
            autoRefresh = new CheckBox { Left = 590, Top = 16, Width = 160, Text = "每 5 秒自动刷新", Checked = true };
            Controls.Add(appsOnly);
            Controls.Add(autoRefresh);
            list = new ListView {
                Left = 12, Top = 52, Width = 936, Height = 494,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
                GridLines = true, ShowItemToolTips = true
            };
            list.Columns.Add("应用名称", 200);
            list.Columns.Add("进程名 / 包名", 410);
            list.Columns.Add("PID", 80, HorizontalAlignment.Right);
            list.Columns.Add("CPU %", 100, HorizontalAlignment.Right);
            list.Columns.Add("内存 MB ▼", 120, HorizontalAlignment.Right);
            Controls.Add(list);
            status = new Label { Left = 12, Top = 555, Width = 936, Height = 38,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, ForeColor = Color.DimGray };
            Controls.Add(status);
            Controls.Add(new Label { Left = 12, Top = 604, Width = 680, Height = 32,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = "CPU：单核满载为 100%，多核可超过 100%；内存：RSS，含共享内存。" });
            stop = new Button { Left = 718, Top = 604, Width = 120, Height = 30,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Text = "强行停止应用", Enabled = false };
            var close = new Button { Left = 850, Top = 604, Width = 98, Height = 30,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Text = "关闭", DialogResult = DialogResult.Cancel };
            Controls.Add(stop);
            Controls.Add(close);
            CancelButton = close;
            refresh.Click += (s, e) => LoadProcs();
            filter.TextChanged += (s, e) => BuildList();
            appsOnly.CheckedChanged += (s, e) => BuildList();
            autoRefresh.CheckedChanged += (s, e) => refreshTimer.Enabled = autoRefresh.Checked;
            list.SelectedIndexChanged += (s, e) => UpdateStopButton();
            list.ColumnClick += (s, e) => {
                if (sortColumn == e.Column) descending = !descending;
                else { sortColumn = e.Column; descending = e.Column >= 2; }
                BuildList();
            };
            stop.Click += (s, e) => StopSelected();
            refreshTimer.Tick += (s, e) => LoadProcs();
            Shown += (s, e) => { refreshTimer.Start(); LoadProcs(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !closed) {
                closed = true;
                closing.Cancel();
                refreshTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void SetBusy(bool value)
        {
            busy = value;
            refresh.Enabled = !value;
            refresh.Text = value ? "读取中…" : "刷新";
            UpdateStopButton();
        }

        private ProcInfo Selected()
        {
            return list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as ProcInfo;
        }

        private void UpdateStopButton()
        {
            var p = Selected();
            stop.Enabled = !busy && p != null && p.CanStop;
        }

        private async void LoadProcs()
        {
            if (busy || closed) return;
            SetBusy(true);
            status.Text = "正在读取手机进程与 CPU 采样…";
            try {
                var procs = await Task.Run(() => ReadSnapshot(true));
                if (closed) return;
                if (procs.Count == 0) throw new InvalidOperationException("未能读取进程列表，请检查设备连接或系统权限。");
                allProcs = procs;
                updated = DateTime.Now.ToString("HH:mm:ss");
                refreshError = "";
                BuildList(true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) {
                if (!closed) {
                    refreshError = "刷新失败：" + ex.Message + "（保留上次数据）";
                    status.Text = refreshError;
                }
            }
            finally { if (!closed) SetBusy(false); }
        }

        private List<ProcInfo> ReadSnapshot(bool withCpu)
        {
            // 旧 Android 不支持自定义列时，由 shell 回退；最终失败仍保留退出码。
            var procs = ParsePs(RunAdb("ps -A -o PID,PPID,USER,RSS,STIME,NAME 2>/dev/null || " +
                "ps -A -o PID,PPID,USER,NAME,RSS 2>/dev/null || ps -A 2>/dev/null || ps"));
            if (withCpu && procs.Count > 0) {
                try { ApplyCpu(procs, RunAdb("top -b -n 2 -d 1 -o PID,%CPU,NAME")); }
                catch (OperationCanceledException) { throw; }
                catch { /* CPU 不可读时保留空值，不把未知显示为 0。 */ }
            }
            return procs;
        }

        // 与文件操作共用退出码检查、超时和取消清理，避免两套子进程实现产生差异。
        private string RunAdb(string command)
        {
            return Transfer.RunFileCommand("-s " + Transfer.Arg(serial) + " shell " + Transfer.Arg(command), 12000, closing.Token);
        }
        internal static List<ProcInfo> ParsePs(string output)
        {
            var result = new Dictionary<int, ProcInfo>();
            string[] header = null;
            foreach (var raw in (output ?? "").Split('\n')) {
                var parts = Regex.Split(raw.Trim(), @"\s+");
                if (parts.Length < 4) continue;
                if ((parts[0] == "PID" || parts[0] == "USER") && parts.Contains("PID") && parts.Contains("USER")) { header = parts; continue; }
                if (header == null) continue;
                int pidIndex = Array.IndexOf(header, "PID"), userIndex = Array.IndexOf(header, "USER");
                int parentIndex = Array.IndexOf(header, "PPID"), rssIndex = Array.IndexOf(header, "RSS");
                int nameIndex = Array.IndexOf(header, "NAME");
                if (nameIndex < 0) nameIndex = Array.IndexOf(header, "CMD");
                if (nameIndex < 0) nameIndex = Array.IndexOf(header, "COMMAND");
                if (nameIndex < 0 || parts.Length < header.Length) continue;
                int pid, parent = 0;
                if (!int.TryParse(parts[pidIndex], out pid) || pid <= 0) continue;
                if (parentIndex >= 0) int.TryParse(parts[parentIndex], out parent);
                long rss = -1, parsedRss;
                // NAME 可含空格；它后面的字段从行末定位。
                if (rssIndex >= 0) {
                    var actualRss = rssIndex > nameIndex ? parts.Length - (header.Length - rssIndex) : rssIndex;
                    if (long.TryParse(parts[actualRss], out parsedRss) && parsedRss >= 0) rss = parsedRss;
                }
                int startIndex = Array.IndexOf(header, "STIME");
                result[pid] = new ProcInfo { Pid = pid, PPid = parent, User = parts[userIndex], RssKb = rss,
                    Started = startIndex < 0 ? null : parts[startIndex],
                    Name = string.Join(" ", parts, nameIndex, parts.Length - header.Length + 1) };
            }
            return result.Values.ToList();
        }

        internal static void ApplyCpu(List<ProcInfo> procs, string output)
        {
            var readings = new Dictionary<int, KeyValuePair<string, double>>();
            int frames = 0;
            foreach (var raw in (output ?? "").Split('\n')) {
                var parts = Regex.Split(raw.Trim(), @"\s+");
                if (Regex.IsMatch(raw.Replace('[', ' ').Replace(']', ' ').Trim(), @"^PID\s+%CPU\s+NAME$")) {
                    frames++;
                    readings.Clear();
                    continue;
                }
                int pid; double value;
                if (frames == 0 || parts.Length < 3 || !int.TryParse(parts[0], out pid) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                    double.IsNaN(value) || double.IsInfinity(value) || value < 0) continue;
                readings[pid] = new KeyValuePair<string, double>(string.Join(" ", parts, 2, parts.Length - 2), value);
            }
            foreach (var p in procs) {
                KeyValuePair<string, double> value;
                p.Cpu = frames >= 2 && readings.TryGetValue(p.Pid, out value) && value.Key == p.Name ? (double?)value.Value : null;
            }
        }

        internal static List<ProcInfo> VisibleProcs(IEnumerable<ProcInfo> procs, string keyword, bool onlyApps, int column, bool desc)
        {
            var result = procs.Where(p => (!onlyApps || p.IsApp) &&
                (p.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 p.DisplayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 p.Pid.ToString().Contains(keyword))).ToList();
            result.Sort((a, b) => {
                int c;
                // 缺失数据始终排在末尾。
                if (column == 3 && a.Cpu.HasValue != b.Cpu.HasValue) return a.Cpu.HasValue ? -1 : 1;
                if (column == 4 && (a.RssKb >= 0) != (b.RssKb >= 0)) return a.RssKb >= 0 ? -1 : 1;
                switch (column) {
                    case 0: c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase); break;
                    case 1: c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                    case 2: c = a.Pid.CompareTo(b.Pid); break;
                    case 3: c = Nullable.Compare(a.Cpu, b.Cpu); break;
                    default: c = a.RssKb.CompareTo(b.RssKb); break;
                }
                return c == 0 ? a.Pid.CompareTo(b.Pid) : (desc ? -c : c);
            });
            return result;
        }

        private void BuildList(bool preserveScroll = false)
        {
            var selected = Selected();
            var top = !preserveScroll || list.TopItem == null ? null : list.TopItem.Tag as ProcInfo;
            var visible = VisibleProcs(allProcs, filter.Text.Trim(), appsOnly.Checked, sortColumn, descending);
            var titles = new[] { "应用名称", "进程名 / 包名", "PID", "CPU %", "内存 MB" };
            for (int i = 0; i < titles.Length; i++) list.Columns[i].Text = titles[i] + (i == sortColumn ? (descending ? " ▼" : " ▲") : "");
            list.BeginUpdate();
            try {
                list.Items.Clear();
                foreach (var p in visible) {
                    var item = new ListViewItem(new[] { p.DisplayName, p.Name, p.Pid.ToString(),
                        p.Cpu.HasValue ? p.Cpu.Value.ToString("0.0") : "—",
                        p.RssKb < 0 ? "—" : (p.RssKb / 1024.0).ToString("0.0") }) {
                        Tag = p, ToolTipText = p.Name + " · " + p.User,
                        ForeColor = p.IsApp ? Color.FromArgb(0, 85, 160) : Color.DimGray
                    };
                    list.Items.Add(item);
                    if (selected != null && SameProcess(selected, p)) { item.Selected = true; item.Focused = true; }
                }
            }
            finally { list.EndUpdate(); }
            if (top != null) {
                var topItem = list.Items.Cast<ListViewItem>().FirstOrDefault(item => SameProcess(top, (ProcInfo)item.Tag));
                if (topItem != null) list.TopItem = topItem;
            }
            status.Text = refreshError.Length > 0 ? refreshError : string.Format(
                "显示 {0} / {1} 个进程 · 所列 RSS 合计 {2:0.0} MB（共享部分会重复计算） · 更新 {3}\r\n{4}",
                visible.Count, allProcs.Count, visible.Where(p => p.RssKb >= 0).Sum(p => p.RssKb) / 1024.0, updated,
                allProcs.Any(p => p.Cpu.HasValue) ? "点击 CPU 或内存列标题排序；搜索支持中文应用名、包名和 PID。" : "CPU 暂不可读，以 — 显示；点击内存列标题排序。");
            UpdateStopButton();
        }

        internal static bool SameProcess(ProcInfo a, ProcInfo b)
        {
            return a.Pid == b.Pid && a.Name == b.Name && a.User == b.User && a.Started == b.Started;
        }

        internal static bool PackageMatchesUser(ProcInfo process, string packageOutput)
        {
            var user = Regex.Match(process.User ?? "", @"^u(\d+)_a(\d+)$");
            long profile, app;
            if (!process.CanStop || !user.Success ||
                !long.TryParse(user.Groups[1].Value, out profile) || profile > 21474 ||
                !long.TryParse(user.Groups[2].Value, out app) || app > 89999) return false;
            long expected = profile * 100000 + 10000 + app;
            foreach (var line in (packageOutput ?? "").Split('\n')) {
                var package = Regex.Match(line.Trim(), @"^package:" + Regex.Escape(process.Package) + @"\s+uid:(\d+)$");
                long actual;
                if (package.Success && long.TryParse(package.Groups[1].Value, out actual) && actual == expected) return true;
            }
            return false;
        }
        private async void StopSelected()
        {
            var selected = Selected();
            if (busy || closed || selected == null || !selected.CanStop) return;
            // 确认期间暂停刷新，固定用户正在确认的应用身份。
            SetBusy(true);
            try {
                if (MessageBox.Show(this, "强行停止「" + selected.DisplayName + "」？\r\n" + selected.Package +
                    "\r\n\r\n该应用的所有进程和服务都会停止，未保存的内容可能丢失。", "强行停止应用",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                var message = await Task.Run(() => {
                    var current = ReadSnapshot(false).FirstOrDefault(p => p.Pid == selected.Pid);
                    if (current == null || !SameProcess(selected, current)) return "进程已退出或身份已变化，请刷新后重新选择。";
                    // 自定义进程名不一定是所属包名，必须再以包管理器的精确包名和 UID 验证。
                    var profile = selected.User.Substring(1, selected.User.IndexOf('_' ) - 1);
                    if (!PackageMatchesUser(selected, RunAdb("pm list packages -U --user " + profile + " " + selected.Package)))
                        return "无法确认该进程属于此应用包，已取消停止操作。";
                    // 只对验证后的原始包名操作；绝不对可能复用的 PID 发送 kill。
                    var response = RunAdb("am force-stop --user " + selected.User.Substring(1, selected.User.IndexOf('_') - 1) + " " + selected.Package);
                    return string.IsNullOrWhiteSpace(response) ? "已发送强行停止命令。" : "手机返回：\r\n" + response.Trim();
                });
                if (!closed) MessageBox.Show(this, message, "强行停止应用", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closed) MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { if (!closed) SetBusy(false); }
            if (!closed) LoadProcs();
        }
    }
}
