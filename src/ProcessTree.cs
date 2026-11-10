// ProcessTree.cs — 手机进程树（按父进程展开，可强停应用）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal sealed class ProcInfo
    {
        public int Pid;
        public int PPid;
        public string User;
        public string Name;
        public long RssKb;

        public bool IsApp { get { return User != null && User.StartsWith("u0_"); } }
        public bool IsKernelThread { get { return Name != null && Name.StartsWith("["); } }

        public string RssText
        {
            get
            {
                if (RssKb >= 1024 * 1024) return (RssKb / 1024.0 / 1024).ToString("0.00") + " GB";
                if (RssKb >= 1024) return (RssKb / 1024.0).ToString("0.0") + " MB";
                return RssKb + " KB";
            }
        }
    }

    internal sealed class ProcessTreeDialog : Form
    {
        private readonly string serial;
        private TreeView tree;
        private TextBox txtFilter;
        private CheckBox chkAppsOnly;
        private Label lblStatus;
        private List<ProcInfo> allProcs = new List<ProcInfo>();
        private ImageList iconList;
        private readonly Dictionary<string, List<TreeNode>> nodesByPkg = new Dictionary<string, List<TreeNode>>();
        private readonly Dictionary<string, int> iconIndexByPkg = new Dictionary<string, int>();

        private static readonly Color ColorApp = Color.FromArgb(0, 102, 204);      // 应用进程：蓝
        private static readonly Color ColorSystem = Color.FromArgb(150, 95, 0);    // 系统进程：棕
        private static readonly Color ColorKernel = Color.Gray;                     // 内核线程：灰
        private static readonly Color ColorGhost = Color.Silver;                    // 隐藏父进程：浅灰

        private static Color ColorOf(ProcInfo p)
        {
            if (p.IsKernelThread) return ColorKernel;
            if (p.IsApp) return ColorApp;
            return ColorSystem;
        }

        public ProcessTreeDialog(string serial)
        {
            this.serial = serial;

            Text = "手机进程树（" + serial + "）";
            Size = new Size(820, 740);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            var btnRefresh = new Button { Left = 12, Top = 12, Width = 78, Height = 28, Text = "刷新" };
            btnRefresh.Click += (s, e) => LoadProcs();
            Controls.Add(btnRefresh);

            var lbl = new Label { Left = 100, Top = 17, Width = 60, Text = "筛选：" };
            Controls.Add(lbl);

            txtFilter = new TextBox { Left = 166, Top = 14, Width = 220 };
            txtFilter.TextChanged += (s, e) => BuildTree();
            Controls.Add(txtFilter);

            chkAppsOnly = new CheckBox { Left = 400, Top = 16, Width = 190, Text = "只看应用进程（u0_*）", Checked = true };
            chkAppsOnly.CheckedChanged += (s, e) => BuildTree();
            Controls.Add(chkAppsOnly);

            iconList = new ImageList { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };
            tree = new TreeView
            {
                Left = 12, Top = 48, Width = 780, Height = 570,
                HideSelection = false, ShowLines = true, ShowPlusMinus = true,
                ShowNodeToolTips = true,
                ImageList = iconList,
                ItemHeight = 36
            };
            Controls.Add(tree);

            lblStatus = new Label
            {
                Left = 12, Top = 626, Width = 520,
                ForeColor = Color.DimGray,
                Text = ""
            };
            Controls.Add(lblStatus);

            // 颜色图例
            int lx = 12;
            foreach (var item in new[]
            {
                new object[] { "■ 应用进程", ColorApp },
                new object[] { "■ 系统进程", ColorSystem },
                new object[] { "■ 内核线程", ColorKernel },
                new object[] { "■ 已隐藏父进程", ColorGhost }
            })
            {
                var lab = new Label
                {
                    Left = lx,
                    Top = 650,
                    Width = 118,
                    Text = (string)item[0],
                    ForeColor = (Color)item[1]
                };
                Controls.Add(lab);
                lx += 118;
            }

            var btnKill = new Button { Left = 540, Top = 644, Width = 120, Height = 32, Text = "结束进程" };
            btnKill.Click += (s, e) => KillSelected();
            Controls.Add(btnKill);

            var btnClose = new Button { Left = 694, Top = 644, Width = 98, Height = 32, Text = "关闭", DialogResult = DialogResult.Cancel };
            Controls.Add(btnClose);
            CancelButton = btnClose;

            Shown += (s, e) => LoadProcs();
        }

        private void LoadProcs()
        {
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                // 优先 Android 8+ 的 toybox ps；老机型退回默认 ps 输出
                var procs = ParsePs(Program.RunAdb("-s " + serial + " shell ps -A -o PID,PPID,USER,NAME,RSS", 30000), true);

                if (procs.Count == 0)
                    procs = ParsePs(Program.RunAdb("-s " + serial + " shell ps -A", 30000), false);

                if (procs.Count == 0)
                    procs = ParsePs(Program.RunAdb("-s " + serial + " shell ps", 30000), false);

                allProcs = procs;
                BuildTree();
            }
            finally { Cursor.Current = Cursors.Default; }
        }

        /// <summary>解析 ps 输出。modern=true 表示 -o PID,PPID,USER,NAME,RSS；false 表示默认列</summary>
        private static List<ProcInfo> ParsePs(string output, bool modern)
        {
            var procs = new List<ProcInfo>();
            if (string.IsNullOrEmpty(output)) return procs;

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("PID") || line.StartsWith("USER")) continue;

                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;

                int pid, ppid;
                long rss = 0;
                string user, name;

                if (modern)
                {
                    // PID PPID USER NAME... RSS
                    if (!int.TryParse(parts[0], out pid)) continue;
                    if (!int.TryParse(parts[1], out ppid)) continue;
                    user = parts[2];
                    long.TryParse(parts[parts.Length - 1], out rss);
                    name = string.Join(" ", parts, 3, Math.Max(1, parts.Length - 4));
                }
                else
                {
                    // USER PID PPID VSZ RSS WCHAN ADDR S NAME...（列数不定，NAME 在最后）
                    if (!int.TryParse(parts[1], out pid)) continue;
                    if (!int.TryParse(parts[2], out ppid)) continue;
                    user = parts[0];
                    // 老格式里 RSS 通常在第 5 列
                    if (parts.Length > 4) long.TryParse(parts[4], out rss);
                    name = parts[parts.Length - 1];
                }

                if (pid <= 0) continue;
                procs.Add(new ProcInfo
                {
                    Pid = pid,
                    PPid = ppid,
                    User = user,
                    Name = name,
                    RssKb = rss
                });
            }
            return procs;
        }

        private void BuildTree()
        {
            var keyword = (txtFilter.Text ?? "").Trim();
            var appsOnly = chkAppsOnly.Checked;

            var byPid = allProcs.ToDictionary(p => p.Pid, p => p);

            // 先过筛选
            var visible = new List<ProcInfo>();
            foreach (var p in allProcs)
            {
                if (appsOnly && !p.IsApp) continue;
                if (!appsOnly && p.IsKernelThread) continue;   // 保留系统但去掉内核线程，避免上千项
                if (keyword.Length > 0 && p.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                visible.Add(p);
            }

            var visiblePids = new HashSet<int>(visible.Select(p => p.Pid));

            tree.BeginUpdate();
            tree.Nodes.Clear();

            var nodes = new Dictionary<int, TreeNode>();
            nodesByPkg.Clear();

            // 先建节点
            foreach (var p in visible.OrderByDescending(p => p.RssKb))
            {
                var pkg = PkgOf(p.Name);
                var display = AppIcons.DisplayName(pkg);
                var head = string.IsNullOrEmpty(display) ? p.Name : display + "  (" + pkg + ")";
                var text = head + "   pid " + p.Pid + " · " + p.RssText;

                var node = new TreeNode(text)
                {
                    Tag = p,
                    ForeColor = ColorOf(p),
                    ImageIndex = EnsureIcon(pkg, p),
                    ToolTipText = p.Name + "  ·  pid " + p.Pid + "  ·  " + p.User + "  ·  " + p.RssText
                };
                nodes[p.Pid] = node;

                if (p.IsApp)
                {
                    List<TreeNode> bucket;
                    if (!nodesByPkg.TryGetValue(pkg, out bucket))
                    {
                        bucket = new List<TreeNode>();
                        nodesByPkg[pkg] = bucket;
                    }
                    bucket.Add(node);
                }
            }

            // 再挂父子：找最近的"可见"祖先；没有就挂到被筛掉的父进程（灰节点）下，保住层级
            var visibleAncestors = new Dictionary<int, TreeNode>();

            foreach (var p in visible)
            {
                var node = nodes[p.Pid];

                int cur = p.PPid;
                int firstHidden = 0;
                int visibleAncestor = 0;

                while (cur > 0)
                {
                    if (visiblePids.Contains(cur)) { visibleAncestor = cur; break; }

                    ProcInfo pp;
                    if (!byPid.TryGetValue(cur, out pp)) break;   // 父进程已不存在
                    if (firstHidden == 0) firstHidden = cur;
                    cur = pp.PPid;
                }

                if (visibleAncestor != 0)
                {
                    nodes[visibleAncestor].Nodes.Add(node);
                }
                else if (firstHidden != 0)
                {
                    TreeNode ghost;
                    if (!visibleAncestors.TryGetValue(firstHidden, out ghost))
                    {
                        var pp = byPid[firstHidden];
                        ghost = new TreeNode("[已隐藏] " + pp.Name + "  (pid " + pp.Pid + ")")
                        {
                            ForeColor = ColorGhost,
                            ToolTipText = "父进程本身被筛掉了，这里只是为了保留层级"
                        };
                        visibleAncestors[firstHidden] = ghost;
                        tree.Nodes.Add(ghost);
                    }
                    ghost.Nodes.Add(node);
                }
                else
                {
                    tree.Nodes.Add(node);
                }
            }

            tree.EndUpdate();

            var totalRss = visible.Sum(p => p.RssKb);
            var appCount = visible.Count(p => p.IsApp && !p.IsKernelThread);
            var sysCount = visible.Count(p => !p.IsApp && !p.IsKernelThread);
            var kernCount = visible.Count(p => p.IsKernelThread);

            lblStatus.Text = string.Format(
                "共 {0} 个（应用 {1} · 系统 {2} · 内核 {3}）／全部 {4} 个 · RSS 合计 {5:0.0} MB（含共享内存，偏大）",
                visible.Count, appCount, sysCount, kernCount, allProcs.Count, totalRss / 1024.0);

            foreach (TreeNode n in tree.Nodes) n.Expand();
            StartIconFetch();
        }

        private static string PkgOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var i = name.IndexOf(':');
            return i > 0 ? name.Substring(0, i) : name;
        }

        /// <summary>取/分配图标索引：应用用真图标或首字母头像，系统/内核用灰色标签</summary>
        private int EnsureIcon(string pkg, ProcInfo p)
        {
            int idx;
            if (iconIndexByPkg.TryGetValue(pkg, out idx)) return idx;

            Image img;
            if (p.IsApp)
            {
                img = AppIcons.GetOrAvatar(pkg);
            }
            else if (p.IsKernelThread)
            {
                img = AppIcons.MakeBadge("核", Color.FromArgb(150, 150, 150));
            }
            else
            {
                img = AppIcons.MakeBadge("系", Color.FromArgb(160, 120, 60));
            }

            idx = iconList.Images.Count;
            iconList.Images.Add(img);
            iconIndexByPkg[pkg] = idx;
            return idx;
        }

        private readonly HashSet<string> iconRequested = new HashSet<string>();

        /// <summary>后台去 APK 里抠真实图标，抠到就替换头像</summary>
        private void StartIconFetch()
        {
            var pkgs = allProcs.Where(p => p.IsApp)
                               .Select(p => PkgOf(p.Name))
                               .Where(p => p.Length > 0)
                               .Distinct()
                               .Where(p => !iconRequested.Contains(p))
                               .ToList();

            if (pkgs.Count == 0) return;
            foreach (var p in pkgs) iconRequested.Add(p);

            AppIcons.FetchAll(serial, pkgs,
                pkg =>
                {
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            List<TreeNode> nodes;
                            if (!nodesByPkg.TryGetValue(pkg, out nodes)) return;

                            var img = AppIcons.Get(pkg);
                            if (img == null) return;

                            var nodesForPkg = nodes;
                            int idx = iconList.Images.Count;
                            iconList.Images.Add(img);
                            iconIndexByPkg[pkg] = idx;
                            foreach (var n in nodesForPkg) n.ImageIndex = idx;
                        }));
                    }
                    catch { }
                },
                () => { });
        }

        private void KillSelected()
        {
            if (tree.SelectedNode == null) return;
            var p = tree.SelectedNode.Tag as ProcInfo;
            if (p == null) return;

            var isApp = p.IsApp && p.Name != null && p.Name.Contains(".");
            var text = isApp
                ? "要强停应用 " + p.Name + " 吗？\r\n\r\n（am force-stop，等同于系统里强行停止）"
                : "要结束进程 " + p.Name + "（pid " + p.Pid + "）吗？\r\n\r\n注意：系统进程通常会拒绝。";

            if (MessageBox.Show(text, "结束进程", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            Cursor.Current = Cursors.WaitCursor;
            string result;
            try
            {
                result = isApp
                    ? Program.RunAdb("-s " + serial + " shell am force-stop " + p.Name, 30000)
                    : Program.RunAdb("-s " + serial + " shell kill -9 " + p.Pid, 15000);
            }
            finally { Cursor.Current = Cursors.Default; }

            MessageBox.Show("命令返回：\r\n" + (string.IsNullOrWhiteSpace(result) ? "（无输出，通常代表成功）" : result.Trim()),
                "结束进程", MessageBoxButtons.OK, MessageBoxIcon.Information);
            LoadProcs();
        }
    }
}
