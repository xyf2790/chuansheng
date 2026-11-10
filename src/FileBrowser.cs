// FileBrowser.cs — 手机文件浏览（目录树 + 排序 + 搜索 + 快捷入口 + 删除 + 回收站）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal sealed class FileBrowserDialog : Form
    {
        private readonly string serial;
        private string currentPath = Transfer.RemoteDir;

        private TreeView tree;
        private ListView list;
        private Label lblPath;
        private TextBox txtSearch;
        private CheckBox chkSub;
        private ImageList icons;
        private bool suppressTreeEvent;

        private List<RemoteEntry> currentEntries = new List<RemoteEntry>();
        private bool subtreeResultMode;
        private int sortColumn = 0;      // 0 名称 / 1 类型 / 2 时间 / 3 大小
        private bool sortAsc = true;

        private static readonly string[][] QuickDirs = new[]
        {
            new[] { "下载 Download", "/sdcard/Download" },
            new[] { "相机照片 DCIM/Camera", "/sdcard/DCIM/Camera" },
            new[] { "图片 Pictures", "/sdcard/Pictures" },
            new[] { "视频 Movies", "/sdcard/Movies" },
            new[] { "文档 Documents", "/sdcard/Documents" },
            new[] { "音乐 Music", "/sdcard/Music" },
            new[] { "截图 Pictures/Screenshots", "/sdcard/Pictures/Screenshots" },
            new[] { "根目录 /sdcard", "/sdcard" }
        };

        public FileBrowserDialog(string serial)
        {
            this.serial = serial;

            Text = "手机文件（浏览 / 搜索 / 取文件 / 删除）";
            Size = new Size(1020, 660);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            icons.Images.Add(DrawFolderIcon());
            icons.Images.Add(DrawFileIcon());

            // ---- 顶部：路径 ----
            lblPath = new Label { Left = 244, Top = 12, Width = 750, Height = 20, Text = currentPath, ForeColor = Color.DimGray };
            Controls.Add(lblPath);

            // ---- 第二行：导航 + 快捷目录 + 搜索 ----
            var btnUp = new Button { Left = 244, Top = 38, Width = 70, Height = 27, Text = "↑ 上一级" };
            btnUp.Click += (s, e) => GoUp();
            Controls.Add(btnUp);

            var btnRefresh = new Button { Left = 320, Top = 38, Width = 60, Height = 27, Text = "刷新" };
            btnRefresh.Click += (s, e) => LoadDir();
            Controls.Add(btnRefresh);

            var btnQuick = new Button { Left = 386, Top = 38, Width = 92, Height = 27, Text = "快捷目录 ▾" };
            var quickMenu = new ContextMenuStrip();
            foreach (var q in QuickDirs)
            {
                var path = q[1];
                quickMenu.Items.Add(q[0], null, (s, e) => { subtreeResultMode = false; txtSearch.Text = ""; Navigate(path); });
            }
            btnQuick.Click += (s, e) => quickMenu.Show(btnQuick, new Point(0, btnQuick.Height));
            Controls.Add(btnQuick);

            txtSearch = new TextBox { Left = 500, Top = 40, Width = 300 };
            txtSearch.TextChanged += (s, e) => { if (!subtreeResultMode) Render(); };
            Controls.Add(txtSearch);

            chkSub = new CheckBox { Left = 806, Top = 42, Width = 88, Text = "含子目录" };
            Controls.Add(chkSub);

            var btnSearch = new Button { Left = 896, Top = 38, Width = 60, Height = 27, Text = "搜索" };
            btnSearch.Click += (s, e) => DoSearch();
            Controls.Add(btnSearch);

            var btnClear = new Button { Left = 960, Top = 38, Width = 30, Height = 27, Text = "×" };
            btnClear.Click += (s, e) =>
            {
                subtreeResultMode = false;
                txtSearch.Text = "";
                LoadDir();
            };
            Controls.Add(btnClear);

            // ---- 左：目录树 ----
            tree = new TreeView
            {
                Left = 12, Top = 38, Width = 220, Height = 520,
                HideSelection = false, ShowLines = true, ShowPlusMinus = true
            };
            tree.BeforeExpand += Tree_BeforeExpand;
            tree.AfterSelect += Tree_AfterSelect;
            Controls.Add(tree);

            // ---- 右：列表 ----
            list = new ListView
            {
                Left = 244, Top = 72, Width = 750, Height = 486,
                View = View.Details, FullRowSelect = true, GridLines = true,
                MultiSelect = true, SmallImageList = icons, HideSelection = false
            };
            list.Columns.Add("名称", 360);
            list.Columns.Add("类型", 80);
            list.Columns.Add("修改时间", 130);
            list.Columns.Add("大小", 120);
            list.ColumnClick += List_ColumnClick;
            list.DoubleClick += (s, e) => OpenSelected();
            list.KeyDown += List_KeyDown;
            Controls.Add(list);

            // ---- 底部 ----
            var btnPull = new Button { Left = 244, Top = 568, Width = 110, Height = 32, Text = "取到电脑" };
            btnPull.Click += (s, e) => PullSelected();
            Controls.Add(btnPull);

            var btnDelete = new Button { Left = 362, Top = 568, Width = 100, Height = 32, Text = "删除 ▾" };
            var delMenu = new ContextMenuStrip();
            delMenu.Items.Add("删除到回收站（Delete）", null, (s, e) => DeleteSelected(false));
            delMenu.Items.Add("彻底删除（不可恢复）", null, (s, e) => DeleteSelected(true));
            btnDelete.Click += (s, e) => delMenu.Show(btnDelete, new Point(0, btnDelete.Height));
            Controls.Add(btnDelete);

            var btnTrash = new Button { Left = 470, Top = 568, Width = 90, Height = 32, Text = "回收站" };
            btnTrash.Click += (s, e) =>
            {
                using (var d = new TrashDialog(serial)) d.ShowDialog();
                LoadDir();
            };
            Controls.Add(btnTrash);

            var hint = new Label
            {
                Left = 244, Top = 610, Width = 600,
                ForeColor = Color.DimGray,
                Text = "点列头排序；双击文件夹进入；Delete = 移到回收站；Backspace = 上一级。"
            };
            Controls.Add(hint);

            var btnClose = new Button { Left = 894, Top = 568, Width = 100, Height = 32, Text = "关闭", DialogResult = DialogResult.Cancel };
            Controls.Add(btnClose);
            CancelButton = btnClose;

            Shown += (s, e) =>
            {
                BuildTree();
                LoadDir();
            };
        }

        // ---------- 图标 ----------
        private static Bitmap DrawFolderIcon()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(245, 196, 79)))
                using (var p = new Pen(Color.FromArgb(190, 145, 40)))
                {
                    g.FillRectangle(b, 1, 4, 14, 10);
                    g.DrawRectangle(p, 1, 4, 13, 9);
                    g.FillRectangle(b, 1, 2, 6, 3);
                    g.DrawRectangle(p, 1, 2, 5, 2);
                }
            }
            return bmp;
        }

        private static Bitmap DrawFileIcon()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.White))
                using (var p = new Pen(Color.FromArgb(120, 120, 120)))
                {
                    g.FillRectangle(b, 3, 1, 9, 13);
                    g.DrawRectangle(p, 3, 1, 8, 12);
                    g.DrawLine(p, 6, 5, 10, 5);
                    g.DrawLine(p, 6, 8, 10, 8);
                    g.DrawLine(p, 6, 11, 10, 11);
                }
            }
            return bmp;
        }

        // ---------- 目录树 ----------
        private void BuildTree()
        {
            suppressTreeEvent = true;
            tree.BeginUpdate();
            tree.Nodes.Clear();

            var root = new TreeNode("/sdcard") { Tag = "/sdcard" };
            root.Nodes.Add(new TreeNode("…"));
            tree.Nodes.Add(root);
            root.Expand();

            tree.EndUpdate();
            suppressTreeEvent = false;
        }

        private void Tree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;
            if (node.Nodes.Count == 1 && node.Nodes[0].Text == "…")
            {
                node.Nodes.Clear();
                var path = node.Tag as string;
                if (string.IsNullOrEmpty(path)) return;

                Cursor.Current = Cursors.WaitCursor;
                try
                {
                    foreach (var d in Transfer.ListSubDirs(serial, path))
                    {
                        var child = new TreeNode(d) { Tag = path.TrimEnd('/') + "/" + d };
                        child.Nodes.Add(new TreeNode("…"));
                        node.Nodes.Add(child);
                    }
                }
                finally { Cursor.Current = Cursors.Default; }
            }
        }

        private void Tree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (suppressTreeEvent) return;
            var path = e.Node.Tag as string;
            if (!string.IsNullOrEmpty(path))
            {
                subtreeResultMode = false;
                txtSearch.Text = "";
                Navigate(path);
            }
        }

        // ---------- 排序 ----------
        private void List_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == sortColumn) sortAsc = !sortAsc;
            else { sortColumn = e.Column; sortAsc = true; }
            Render();
        }

        private int CompareEntries(RemoteEntry a, RemoteEntry b)
        {
            int r = b.IsDir.CompareTo(a.IsDir);   // 文件夹永远在前
            if (r != 0) return r;

            switch (sortColumn)
            {
                case 0: r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case 1: r = string.Compare(a.IsDir ? "文件夹" : "文件", b.IsDir ? "文件夹" : "文件", StringComparison.Ordinal); break;
                case 2: r = string.Compare(a.Modified ?? "", b.Modified ?? "", StringComparison.Ordinal); break;
                case 3: r = a.Size.CompareTo(b.Size); break;
            }
            return sortAsc ? r : -r;
        }

        private void UpdateHeaders()
        {
            var names = new[] { "名称", "类型", "修改时间", "大小" };
            for (int i = 0; i < names.Length; i++)
                list.Columns[i].Text = names[i] + (i == sortColumn ? (sortAsc ? " ▲" : " ▼") : "");
        }

        // ---------- 列表 ----------
        private void Navigate(string path)
        {
            currentPath = path;
            subtreeResultMode = false;
            LoadDir();
        }

        private void GoUp()
        {
            if (currentPath == "/" || currentPath.Length == 0) return;
            var idx = currentPath.TrimEnd('/').LastIndexOf('/');
            Navigate(idx <= 0 ? "/" : currentPath.Substring(0, idx));
        }

        private void LoadDir()
        {
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                currentEntries = Transfer.ListRemote(serial, currentPath);
                subtreeResultMode = false;
                Render();
            }
            finally { Cursor.Current = Cursors.Default; }
        }

        private void Render()
        {
            lblPath.Text = subtreeResultMode
                ? "搜索结果：" + txtSearch.Text + "（" + currentPath + " 及子目录）"
                : currentPath;

            UpdateHeaders();

            var keyword = (txtSearch.Text ?? "").Trim();

            list.BeginUpdate();
            list.Items.Clear();

            if (!subtreeResultMode && currentPath != "/")
                list.Items.Add(new ListViewItem(new[] { "..", "文件夹", "", "" }) { ImageIndex = 0, Tag = ".." });

            var shown = currentEntries;
            if (!subtreeResultMode && keyword.Length > 0)
            {
                shown = currentEntries
                    .Where(e => e.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            shown = shown.OrderBy(e => e, Comparer<RemoteEntry>.Create(CompareEntries)).ToList();

            foreach (var e in shown)
            {
                list.Items.Add(new ListViewItem(new[]
                {
                    e.Name,
                    e.IsDir ? "文件夹" : "文件",
                    e.Modified ?? "",
                    e.SizeText
                })
                { ImageIndex = e.IsDir ? 0 : 1, Tag = e });
            }

            list.EndUpdate();
        }

        // ---------- 搜索 ----------
        private void DoSearch()
        {
            var keyword = (txtSearch.Text ?? "").Trim();
            if (keyword.Length == 0)
            {
                LoadDir();
                return;
            }

            if (!chkSub.Checked)
            {
                // 只过滤当前目录，纯本地筛选
                subtreeResultMode = false;
                Render();
                return;
            }

            Cursor.Current = Cursors.WaitCursor;
            try
            {
                var results = new List<RemoteEntry>();
                var kw = keyword.Replace("'", "");

                var dirs = Program.RunAdb("-s " + serial + " shell find " + Transfer.Q(currentPath)
                    + " -maxdepth 4 -type d -iname '*" + kw + "*'", 60000);
                foreach (var line in dirs.Split('\n'))
                {
                    var p = line.Trim();
                    if (p.Length == 0 || p.StartsWith("find:")) continue;
                    results.Add(new RemoteEntry
                    {
                        Name = p,
                        IsDir = true,
                        Modified = "",
                        Size = 0,
                        FullPath = p
                    });
                }

                var files = Program.RunAdb("-s " + serial + " shell find " + Transfer.Q(currentPath)
                    + " -maxdepth 4 -type f -iname '*" + kw + "*'", 60000);
                foreach (var line in files.Split('\n'))
                {
                    var p = line.Trim();
                    if (p.Length == 0 || p.StartsWith("find:")) continue;
                    results.Add(new RemoteEntry
                    {
                        Name = p,
                        IsDir = false,
                        Modified = "",
                        Size = 0,
                        FullPath = p
                    });
                }

                // find 不支持时（老 Android 的 toybox 没 -iname）退回逐层扫描
                if (results.Count == 0)
                    results = Transfer.SearchRecursive(serial, currentPath, keyword, 3, 300);

                currentEntries = results;
                subtreeResultMode = true;
                Render();

                if (results.Count == 0)
                    MessageBox.Show("没找到名称包含「" + keyword + "」的项目。", "搜索",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally { Cursor.Current = Cursors.Default; }
        }

        private void OpenSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            var tag = list.SelectedItems[0].Tag;
            if (tag is string && (string)tag == "..") { GoUp(); return; }

            var entry = tag as RemoteEntry;
            if (entry == null) return;

            if (subtreeResultMode)
            {
                // 搜索结果：跳到所在目录
                var parent = entry.FullPath.Contains("/")
                    ? entry.FullPath.Substring(0, entry.FullPath.LastIndexOf('/'))
                    : "/sdcard";
                Navigate(parent);
                return;
            }

            if (entry.IsDir) Navigate(entry.FullPath);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete) { e.Handled = true; DeleteSelected(false); }
            else if (e.KeyCode == Keys.F5) { e.Handled = true; LoadDir(); }
            else if (e.KeyCode == Keys.Back) { e.Handled = true; GoUp(); }
        }

        private List<RemoteEntry> SelectedEntries()
        {
            var items = new List<RemoteEntry>();
            foreach (ListViewItem item in list.SelectedItems)
            {
                var entry = item.Tag as RemoteEntry;
                if (entry != null) items.Add(entry);
            }
            return items;
        }

        private void DeleteSelected(bool permanent)
        {
            var items = SelectedEntries();
            if (items.Count == 0)
            {
                MessageBox.Show("先选中要删的文件或文件夹。", "删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var what = items.Count == 1 ? items[0].Name : (items.Count + " 个项目");
            var text = permanent
                ? "要【彻底删除】" + what + " 吗？\r\n\r\n此操作不可恢复。"
                : "要把 " + what + " 移到手机回收站吗？\r\n\r\n可在「回收站」里还原。";

            if (MessageBox.Show(text, permanent ? "彻底删除" : "移到回收站",
                    MessageBoxButtons.OKCancel, permanent ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK)
                return;

            Cursor.Current = Cursors.WaitCursor;
            int ok = 0, fail = 0;
            var errs = new StringBuilder();
            try
            {
                foreach (var entry in items)
                {
                    string msg;
                    var done = permanent
                        ? Transfer.DeletePermanent(serial, entry, out msg)
                        : Transfer.MoveToTrash(serial, entry, out msg);

                    if (done) ok++;
                    else { fail++; errs.AppendLine(entry.Name + "：" + msg); }
                }
            }
            finally { Cursor.Current = Cursors.Default; }

            LoadDir();
            if (fail > 0)
                MessageBox.Show("成功 " + ok + "，失败 " + fail + "\r\n\r\n" + errs, "删除结果",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void PullSelected()
        {
            var targets = SelectedEntries();
            if (targets.Count == 0)
            {
                MessageBox.Show("先选中文件或文件夹。", "取文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var targetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "手机文件");
            Directory.CreateDirectory(targetDir);

            var sb = new StringBuilder();
            int ok = 0, fail = 0;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                foreach (var f in targets)
                {
                    var output = Program.RunAdb(
                        "-s " + serial + " pull " + Transfer.Q(f.FullPath) + " " + Transfer.Q(targetDir), 600000);
                    if (output.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fail++;
                        sb.AppendLine("[失败] " + f.Name + "  " + output.Trim());
                    }
                    else { ok++; sb.AppendLine("[成功] " + f.Name + (f.IsDir ? "（整个文件夹）" : "")); }
                }
            }
            finally { Cursor.Current = Cursors.Default; }

            if (fail > 0)
                MessageBox.Show("保存到：" + targetDir + "\r\n\r\n成功 " + ok + "，失败 " + fail + "\r\n\r\n" + sb,
                    "取文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                Toast.Show("文件已取回", ok + " 个 → 下载\\手机文件");
        }
    }

    // ---------- 回收站 ----------
    internal sealed class TrashDialog : Form
    {
        private readonly string serial;
        private ListView list;
        private List<Transfer.TrashItem> items = new List<Transfer.TrashItem>();

        public TrashDialog(string serial)
        {
            this.serial = serial;

            Text = "手机回收站（" + Transfer.TrashRoot + "）";
            Size = new Size(880, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            list = new ListView
            {
                Left = 12, Top = 12, Width = 840, Height = 370,
                View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = true
            };
            list.Columns.Add("名称", 220);
            list.Columns.Add("原路径", 340);
            list.Columns.Add("大小", 90);
            list.Columns.Add("删除批次", 170);
            Controls.Add(list);

            var btnRestore = new Button { Left = 12, Top = 394, Width = 100, Height = 32, Text = "还原" };
            btnRestore.Click += (s, e) => DoRestore();
            Controls.Add(btnRestore);

            var btnPurge = new Button { Left = 120, Top = 394, Width = 120, Height = 32, Text = "彻底删除" };
            btnPurge.Click += (s, e) => DoPurge();
            Controls.Add(btnPurge);

            var btnRefresh = new Button { Left = 248, Top = 394, Width = 80, Height = 32, Text = "刷新" };
            btnRefresh.Click += (s, e) => LoadAll();
            Controls.Add(btnRefresh);

            var btnClose = new Button { Left = 772, Top = 394, Width = 80, Height = 32, Text = "关闭", DialogResult = DialogResult.Cancel };
            Controls.Add(btnClose);
            CancelButton = btnClose;

            Shown += (s, e) => LoadAll();
        }

        private void LoadAll()
        {
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                items = Transfer.ListTrash(serial);
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var t in items)
                {
                    var sizeText = t.IsDir
                        ? "文件夹"
                        : (t.Size >= 1024 * 1024
                            ? (t.Size / 1024.0 / 1024).ToString("0.0") + " MB"
                            : (t.Size / 1024.0).ToString("0") + " KB");
                    list.Items.Add(new ListViewItem(new[] { t.Name, t.Source, sizeText, t.DeletedAt }) { Tag = t });
                }
                list.EndUpdate();
            }
            finally { Cursor.Current = Cursors.Default; }
        }

        private void DoRestore()
        {
            if (list.SelectedItems.Count == 0) return;
            int ok = 0, fail = 0;
            var errs = new StringBuilder();
            foreach (ListViewItem item in list.SelectedItems)
            {
                var t = (Transfer.TrashItem)item.Tag;
                string msg;
                if (Transfer.RestoreTrash(serial, t, out msg)) ok++;
                else { fail++; errs.AppendLine(t.Name + "：" + msg); }
            }
            LoadAll();
            if (fail > 0)
                MessageBox.Show("还原成功 " + ok + "，失败 " + fail + "\r\n\r\n" + errs, "还原",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void DoPurge()
        {
            if (list.SelectedItems.Count == 0) return;
            if (MessageBox.Show("把选中的项目从回收站彻底删除？不可恢复。", "彻底删除",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            int ok = 0, fail = 0;
            var errs = new StringBuilder();
            foreach (ListViewItem item in list.SelectedItems)
            {
                var t = (Transfer.TrashItem)item.Tag;
                string msg;
                if (Transfer.PurgeTrash(serial, t, out msg)) ok++;
                else { fail++; errs.AppendLine(t.Name + "：" + msg); }
            }
            LoadAll();
            if (fail > 0)
                MessageBox.Show("删除成功 " + ok + "，失败 " + fail + "\r\n\r\n" + errs, "彻底删除",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
