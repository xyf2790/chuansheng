// FileBrowser.cs — 手机文件浏览（目录树 + 排序 + 搜索 + 快捷入口 + 删除 + 回收站）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        private Button btnPreview;
        private bool suppressTreeEvent;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Dictionary<TreeNode, CancellationTokenSource> treeLoads = new Dictionary<TreeNode, CancellationTokenSource>();
        private CancellationTokenSource directoryLoad;
        private CancellationTokenSource renderLoad;
        private bool closing;
        private bool loading;
        private int directoryVersion;
        private int renderVersion;
        private List<RemoteEntry> shownEntries = new List<RemoteEntry>();
        private bool showParent;
        private string resultWarning = "";

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
            txtSearch.TextChanged += (s, e) => { if (subtreeResultMode) LoadDir(); else Render(); };
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
            tree.AfterCollapse += (s, e) =>
            {
                CancellationTokenSource pending;
                if (treeLoads.TryGetValue(e.Node, out pending))
                {
                    treeLoads.Remove(e.Node);
                    pending.Cancel();
                    e.Node.Nodes.Clear();
                    e.Node.Nodes.Add(new TreeNode("…"));
                }
            };
            tree.AfterSelect += Tree_AfterSelect;
            Controls.Add(tree);

            // ---- 右：列表 ----
            list = new ListView
            {
                Left = 244, Top = 72, Width = 750, Height = 486,
                View = View.Details, FullRowSelect = true, GridLines = true,
                MultiSelect = true, SmallImageList = icons, HideSelection = false, VirtualMode = true
            };
            list.Columns.Add("名称", 300);
            list.Columns.Add("类型", 140);
            list.Columns.Add("修改时间", 130);
            list.Columns.Add("大小", 120);
            list.ColumnClick += List_ColumnClick;
            list.DoubleClick += (s, e) => OpenSelected();
            list.KeyDown += List_KeyDown;
            list.RetrieveVirtualItem += RetrieveItem;
            list.SelectedIndexChanged += (s, e) => UpdatePreviewButton();
            list.VirtualItemsSelectionRangeChanged += (s, e) => UpdatePreviewButton();
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

            btnPreview = new Button { Left = 568, Top = 568, Width = 110, Height = 32, Text = "预览图片/视频", Enabled = false };
            btnPreview.Click += (s, e) => PreviewSelected();
            Controls.Add(btnPreview);

            var hint = new Label
            {
                Left = 244, Top = 610, Width = 600,
                ForeColor = Color.DimGray,
                Text = "双击图片/视频预览，文件夹进入；空格预览；Delete 移到回收站；Backspace 上一级。"
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
            FormClosed += (s, e) => CancelPending();
        }

        private void CancelPending()
        {
            if (closing) return;
            closing = true;
            lifetime.Cancel();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelPending();
            }
            base.Dispose(disposing);
            if (disposing && icons != null) { icons.Dispose(); icons = null; }
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

        private async void Tree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (closing) return;
            var node = e.Node;
            if (node.Nodes.Count == 1 && node.Nodes[0].Text == "…")
            {
                var path = node.Tag as string;
                if (string.IsNullOrEmpty(path)) return;
                var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                treeLoads[node] = request;
                node.Nodes[0].Text = "正在加载…";
                try
                {
                    var dirs = await Task.Run(() => Transfer.ListSubDirs(serial, path, request.Token), request.Token);
                    if (closing || request.IsCancellationRequested || node.TreeView != tree) return;
                    tree.BeginUpdate();
                    node.Nodes.Clear();
                    foreach (var d in dirs)
                    {
                        var child = new TreeNode(d) { Tag = path.TrimEnd('/') + "/" + d };
                        child.Nodes.Add(new TreeNode("…"));
                        node.Nodes.Add(child);
                    }
                    tree.EndUpdate();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (!closing && !request.IsCancellationRequested && node.TreeView == tree)
                    {
                        node.Nodes.Clear();
                        node.Nodes.Add(new TreeNode("…"));
                        lblPath.Text = "目录树加载失败：" + ex.Message;
                    }
                }
                finally
                {
                    CancellationTokenSource active;
                    if (treeLoads.TryGetValue(node, out active) && active == request) treeLoads.Remove(node);
                    request.Dispose();
                }
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

        private static int CompareEntries(RemoteEntry a, RemoteEntry b, int column, bool ascending)
        {
            int r = b.IsDir.CompareTo(a.IsDir);   // 文件夹永远在前
            if (r != 0) return r;

            switch (column)
            {
                case 0: r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case 1: r = string.Compare(a.TypeText, b.TypeText, StringComparison.Ordinal); break;
                case 2: r = string.Compare(a.Modified ?? "", b.Modified ?? "", StringComparison.Ordinal); break;
                case 3: r = a.Size.CompareTo(b.Size); break;
            }
            if (r == 0) r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return ascending ? r : -r;
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
            var path = currentPath;
            StartLoad(token => Transfer.ListRemote(serial, path, token), false, "");
        }

        private async void StartLoad(Func<CancellationToken, List<RemoteEntry>> load, bool search, string keyword, Func<string> warning = null)
        {
            if (closing) return;
            if (directoryLoad != null) directoryLoad.Cancel();
            if (renderLoad != null) renderLoad.Cancel();
            var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            directoryLoad = request;
            var version = ++directoryVersion;
            ++renderVersion;
            loading = true;
            subtreeResultMode = search;
            resultWarning = "";
            currentEntries = new List<RemoteEntry>();
            SetRows(new List<RemoteEntry>(), false);
            lblPath.Text = (search ? "正在搜索：" + keyword + "（" + currentPath + " 及子目录）" : "正在加载：" + currentPath);
            try
            {
                var entries = await Task.Run(() => load(request.Token), request.Token);
                if (closing || request.IsCancellationRequested || version != directoryVersion) return;
                currentEntries = entries;
                resultWarning = warning == null ? "" : warning();
                loading = false;
                Render();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!closing && !request.IsCancellationRequested && version == directoryVersion)
                {
                    loading = false;
                    lblPath.Text = "加载失败：" + ex.Message;
                }
            }
            finally
            {
                if (directoryLoad == request) directoryLoad = null;
                request.Dispose();
            }
        }

        private async void Render()
        {
            if (closing || loading) return;
            if (renderLoad != null) renderLoad.Cancel();
            var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            renderLoad = request;
            var version = ++renderVersion;
            var entries = currentEntries;
            var keyword = (txtSearch.Text ?? "").Trim();
            var search = subtreeResultMode;
            var path = currentPath;
            var column = sortColumn;
            var ascending = sortAsc;
            UpdateHeaders();
            try
            {
                var rows = await Task.Run(() =>
                {
                    var shown = new List<RemoteEntry>(entries.Count);
                    foreach (var entry in entries)
                    {
                        request.Token.ThrowIfCancellationRequested();
                        if (search || keyword.Length == 0 || entry.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                            shown.Add(entry);
                    }
                    shown.Sort((a, b) => CompareEntries(a, b, column, ascending));
                    request.Token.ThrowIfCancellationRequested();
                    return shown;
                }, request.Token);
                if (closing || request.IsCancellationRequested || version != renderVersion) return;
                SetRows(rows, !search && path != "/");
                lblPath.Text = (resultWarning.Length == 0 ? "" : resultWarning + "；")
                    + (search ? "搜索结果：" + keyword + "（" + path + " 及子目录）" : path)
                    + " — " + rows.Count + " 个项目";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!closing && !request.IsCancellationRequested && version == renderVersion)
                    lblPath.Text = "显示失败：" + ex.Message;
            }
            finally
            {
                if (renderLoad == request) renderLoad = null;
                request.Dispose();
            }
        }

        private void SetRows(List<RemoteEntry> rows, bool parent)
        {
            list.BeginUpdate();
            try
            {
                list.SelectedIndices.Clear();
                list.VirtualListSize = 0;
                shownEntries = rows;
                showParent = parent;
                list.VirtualListSize = rows.Count + (parent ? 1 : 0);
                list.Invalidate();
            }
            finally { list.EndUpdate(); }
            UpdatePreviewButton();
        }

        private RemoteEntry EntryAt(int index)
        {
            index -= showParent ? 1 : 0;
            return index >= 0 && index < shownEntries.Count ? shownEntries[index] : null;
        }

        private void RetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var entry = EntryAt(e.ItemIndex);
            e.Item = entry == null
                ? new ListViewItem(new[] { "..", "文件夹", "", "" }) { ImageIndex = 0 }
                : new ListViewItem(new[] { entry.Name, entry.TypeText, entry.Modified ?? "", entry.SizeText })
                    { ImageIndex = entry.IsDir ? 0 : 1 };
        }

        private void UpdatePreviewButton()
        {
            if (btnPreview == null) return;
            // Count first: Ctrl+A must not allocate/scan a list on every selection event.
            var entry = list.SelectedIndices.Count == 1 ? EntryAt(list.SelectedIndices[0]) : null;
            btnPreview.Enabled = entry != null && !entry.IsDir && !entry.IsLink && !entry.IsUnavailable && MediaPreview.CanPreview(entry.FullPath);
        }

        // ---------- 搜索 ----------
        private void DoSearch()
        {
            var keyword = (txtSearch.Text ?? "").Trim();
            if (keyword.Length == 0 || (!chkSub.Checked && subtreeResultMode)) { LoadDir(); return; }
            if (!chkSub.Checked) { Render(); return; }
            var path = currentPath;
            string warning = "";
            StartLoad(token => Transfer.SearchRemote(serial, path, keyword, token, out warning), true, keyword, () => warning);
        }

        private void OpenSelected()
        {
            if (list.SelectedIndices.Count == 0) return;
            var index = list.SelectedIndices[0];
            if (showParent && index == 0) { GoUp(); return; }
            var entry = EntryAt(index);
            if (entry == null) return;

            if (!entry.IsDir && MediaPreview.CanPreview(entry.FullPath)) { PreviewSelected(); return; }

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
            else if (e.KeyCode == Keys.Space) { e.SuppressKeyPress = true; PreviewSelected(); }
        }

        private void PreviewSelected()
        {
            var selected = SelectedEntries();
            if (selected.Count != 1 || selected[0].IsDir || selected[0].IsLink || selected[0].IsUnavailable || !MediaPreview.CanPreview(selected[0].FullPath)) return;
            using (var dialog = new MediaPreviewDialog(serial, selected[0])) dialog.ShowDialog(this);
        }

        private List<RemoteEntry> SelectedEntries()
        {
            var items = new List<RemoteEntry>();
            foreach (int index in list.SelectedIndices)
            {
                var entry = EntryAt(index);
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

            using (var operation = new FileOperationDialog(permanent ? "彻底删除" : "移到回收站", items.Select(entry => entry.Name).ToArray(),
                (index, token) =>
                {
                    string message;
                    bool done = permanent ? Transfer.DeletePermanent(serial, items[index], out message, token)
                        : Transfer.MoveToTrash(serial, items[index], out message, token);
                    if (!done) throw new IOException(message);
                }))
            {
                operation.ShowDialog(this);
                LoadDir();
                operation.ShowResult("");
            }
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
            using (var operation = new FileOperationDialog("取文件", targets.Select(entry => entry.Name).ToArray(),
                (index, token) => Transfer.PullEntry(serial, targets[index], targetDir, token)))
            {
                operation.ShowDialog(this);
                operation.ShowResult("保存到：" + targetDir);
            }
        }
    }

    // ---------- 回收站 ----------
    internal sealed class TrashDialog : Form
    {
        private readonly string serial;
        private ListView list;
        private List<Transfer.TrashItem> items = new List<Transfer.TrashItem>();
        private CancellationTokenSource load;
        private bool closed;
        private Label status;

        public TrashDialog(string serial)
        {
            this.serial = serial;

            Text = "手机回收站（" + Transfer.TrashRoot + "）";
            Size = new Size(880, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            status = new Label { Left = 12, Top = 12, Width = 840, Height = 20 };
            Controls.Add(status);
            list = new ListView
            {
                Left = 12, Top = 36, Width = 840, Height = 346,
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

        protected override void Dispose(bool disposing)
        {
            closed = true;
            if (disposing && load != null) load.Cancel();
            base.Dispose(disposing);
        }

        private async void LoadAll()
        {
            if (closed) return;
            if (load != null) load.Cancel();
            var request = new CancellationTokenSource();
            load = request;
            list.Enabled = false;
            status.Text = "正在加载回收站…";
            try
            {
                var result = await Task.Run(() => Transfer.ListTrash(serial, request.Token), request.Token);
                if (closed || request.IsCancellationRequested || load != request) return;
                items = result;
                list.BeginUpdate();
                try
                {
                    list.Items.Clear();
                    foreach (var item in items)
                    {
                        var size = new RemoteEntry { Size = item.Size, IsDir = item.IsDir }.SizeText;
                        list.Items.Add(new ListViewItem(new[] { item.Name, item.Source, item.IsDir ? "文件夹" : size, item.DeletedAt }) { Tag = item });
                    }
                }
                finally { list.EndUpdate(); }
                status.Text = items.Count + " 个项目";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!closed && load == request) status.Text = "加载失败：" + ex.Message;
            }
            finally
            {
                if (load == request)
                {
                    load = null;
                    if (!closed) list.Enabled = true;
                }
                request.Dispose();
            }
        }

        private void DoRestore() { RunSelected(false); }
        private void DoPurge() { RunSelected(true); }

        private void RunSelected(bool purge)
        {
            if (closed || load != null || list.SelectedItems.Count == 0) return;
            if (purge && MessageBox.Show("把选中的项目从回收站彻底删除？不可恢复。", "彻底删除",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(item => (Transfer.TrashItem)item.Tag).ToList();
            using (var operation = new FileOperationDialog(purge ? "清理回收站" : "还原文件", selected.Select(item => item.Name).ToArray(),
                (index, token) =>
                {
                    string message;
                    bool done = purge ? Transfer.PurgeTrash(serial, selected[index], out message, token)
                        : Transfer.RestoreTrash(serial, selected[index], out message, token);
                    if (!done) throw new IOException(message);
                }))
            {
                operation.ShowDialog(this);
                LoadAll();
                operation.ShowResult("");
            }
        }
    }
}
