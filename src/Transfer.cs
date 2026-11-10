// Transfer.cs — 手机文件浏览：目录树 + 取文件 + 删除（回收站/彻底）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal sealed class RemoteEntry
    {
        public string Name;
        public bool IsDir;
        public long Size;
        public string Modified;
        public string FullPath;

        public string SizeText
        {
            get
            {
                if (IsDir) return "";
                if (Size >= 1024L * 1024 * 1024) return (Size / 1024.0 / 1024 / 1024).ToString("0.0") + " GB";
                if (Size >= 1024 * 1024) return (Size / 1024.0 / 1024).ToString("0.0") + " MB";
                if (Size >= 1024) return (Size / 1024.0).ToString("0") + " KB";
                return Size + " B";
            }
        }
    }

    internal static class Transfer
    {
        public const string RemoteDir = "/sdcard/Download";
        public const string TrashRoot = "/sdcard/.PhoneAudioTrayTrash";

        public static string Q(string s) { return "\"" + s + "\""; }

        /// <summary>列出手机某个目录（解析 ls -l）</summary>
        public static List<RemoteEntry> ListRemote(string serial, string path)
        {
            var result = new List<RemoteEntry>();
            var listing = Program.RunAdb("-s " + serial + " shell ls -l " + Q(path), 20000);

            foreach (var raw in listing.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("total ")) continue;

                var m = System.Text.RegularExpressions.Regex.Match(
                    line, @"^([-dlbcps])([rwxStTs-]{9})\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})\s+(.+)$");
                if (!m.Success) continue;

                var name = m.Groups[6].Value.Trim();
                if (name == "." || name == "..") continue;

                long size;
                long.TryParse(m.Groups[3].Value, out size);

                result.Add(new RemoteEntry
                {
                    Name = name,
                    IsDir = m.Groups[1].Value == "d",
                    Size = size,
                    Modified = m.Groups[4].Value + " " + m.Groups[5].Value,
                    FullPath = path.TrimEnd('/') + "/" + name
                });
            }

            return result
                .OrderByDescending(e => e.IsDir)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>只列子目录（给树用）</summary>
        public static List<string> ListSubDirs(string serial, string path)
        {
            var dirs = new List<string>();
            var listing = Program.RunAdb("-s " + serial + " shell ls -l " + Q(path), 20000);
            foreach (var raw in listing.Split('\n'))
            {
                var line = raw.Trim();
                var m = System.Text.RegularExpressions.Regex.Match(
                    line, @"^d[rwxStTs-]{9}\s+\d+\s+\S+\s+\S+\s+\d+\s+\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}\s+(.+)$");
                if (!m.Success) continue;
                var name = m.Groups[1].Value.Trim();
                if (name == "." || name == "..") continue;
                dirs.Add(name);
            }
            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            return dirs;
        }

        // ---------- 删除 ----------
        public static bool MoveToTrash(string serial, RemoteEntry entry, out string message)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var bucket = TrashRoot + "/" + stamp;
            Program.RunAdb("-s " + serial + " shell mkdir -p " + Q(bucket), 15000);

            var mv = Program.RunAdb("-s " + serial + " shell mv " + Q(entry.FullPath) + " " + Q(bucket + "/"), 60000);
            if (mv.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || mv.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0
                || mv.IndexOf("Permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = mv.Trim();
                return false;
            }

            // 记录原路径，便于还原
            Program.RunAdb("-s " + serial + " shell \"echo " + entry.FullPath.Replace(" ", "\\ ") + " > " + bucket + "/.source\"", 15000);
            message = "已移到手机回收站：" + bucket;
            return true;
        }

        public static bool DeletePermanent(string serial, RemoteEntry entry, out string message)
        {
            var rm = Program.RunAdb("-s " + serial + " shell rm -rf " + Q(entry.FullPath), 60000);
            if (rm.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || rm.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = rm.Trim();
                return false;
            }
            message = "已彻底删除：" + entry.FullPath;
            return true;
        }

        // ---------- 回收站 ----------
        internal sealed class TrashItem
        {
            public string Bucket;      // 回收站里的那层目录
            public string Name;        // 原文件名
            public bool IsDir;
            public long Size;
            public string Source;      // 原路径
            public string DeletedAt;
        }

        public static List<TrashItem> ListTrash(string serial)
        {
            var items = new List<TrashItem>();
            var buckets = Program.RunAdb("-s " + serial + " shell ls -l " + Q(TrashRoot), 20000);

            foreach (var raw in buckets.Split('\n'))
            {
                var line = raw.Trim();
                var m = System.Text.RegularExpressions.Regex.Match(
                    line, @"^d[rwxStTs-]{9}\s+\d+\s+\S+\s+\S+\s+\d+\s+\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}\s+(\S+)$");
                if (!m.Success) continue;

                var bucket = TrashRoot + "/" + m.Groups[1].Value;
                var inner = Program.RunAdb("-s " + serial + " shell ls -l " + Q(bucket), 20000);

                foreach (var raw2 in inner.Split('\n'))
                {
                    var l2 = raw2.Trim();
                    if (l2.Length == 0 || l2.Contains(".source")) continue;
                    var m2 = System.Text.RegularExpressions.Regex.Match(
                        l2, @"^([-dlbcps])[rwxStTs-]{9}\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})\s+(.+)$");
                    if (!m2.Success) continue;

                    long size;
                    long.TryParse(m2.Groups[2].Value, out size);
                    var name = m2.Groups[5].Value.Trim();

                    var src = Program.RunAdb("-s " + serial + " shell cat " + Q(bucket + "/.source"), 10000).Trim();

                    items.Add(new TrashItem
                    {
                        Bucket = bucket,
                        Name = name,
                        IsDir = m2.Groups[1].Value == "d",
                        Size = size,
                        Source = string.IsNullOrEmpty(src) ? "" : src,
                        DeletedAt = m.Groups[1].Value
                    });
                }
            }

            return items.OrderByDescending(i => i.DeletedAt).ToList();
        }

        public static bool RestoreTrash(string serial, TrashItem item, out string message)
        {
            if (string.IsNullOrEmpty(item.Source))
            {
                message = "找不到原始路径记录（.source 缺失），只能手动处理";
                return false;
            }

            var parent = item.Source.Substring(0, Math.Max(0, item.Source.LastIndexOf('/')));
            if (parent.Length == 0) parent = "/sdcard";

            Program.RunAdb("-s " + serial + " shell mkdir -p " + Q(parent), 15000);
            var mv = Program.RunAdb("-s " + serial + " shell mv " + Q(item.Bucket + "/" + item.Name) + " " + Q(item.Source), 60000);

            if (mv.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = mv.Trim();
                return false;
            }
            message = "已还原到：" + item.Source;
            return true;
        }

        public static bool PurgeTrash(string serial, TrashItem item, out string message)
        {
            var rm = Program.RunAdb("-s " + serial + " shell rm -rf " + Q(item.Bucket), 60000);
            if (rm.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = rm.Trim();
                return false;
            }
            message = "已从回收站彻底删除";
            return true;
        }

        /// <summary>老机型没有 find -iname 时的兜底：逐层 ls 递归找（限深度和目录数）</summary>
        public static List<RemoteEntry> SearchRecursive(string serial, string root, string keyword, int maxDepth, int maxDirs)
        {
            var results = new List<RemoteEntry>();
            var queue = new Queue<Tuple<string, int>>();
            queue.Enqueue(Tuple.Create(root, 0));
            int visited = 0;

            while (queue.Count > 0 && visited < maxDirs)
            {
                var cur = queue.Dequeue();
                visited++;

                List<RemoteEntry> entries;
                try { entries = ListRemote(serial, cur.Item1); }
                catch { break; }

                foreach (var e in entries)
                {
                    if (e.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        results.Add(e);

                    if (e.IsDir && cur.Item2 < maxDepth)
                        queue.Enqueue(Tuple.Create(e.FullPath, cur.Item2 + 1));
                }
            }

            return results
                .OrderByDescending(e => e.IsDir)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ---------- 入口 ----------
        public static void PushFiles()
        {
            var serial = Program.PrimarySerial;
            if (string.IsNullOrEmpty(serial))
            {
                MessageBox.Show("还没有连接手机。", "推送文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Program.EnsureSettings();

            string target;
            using (var tdlg = new TargetDialog(Program.PushTarget))
            {
                if (tdlg.ShowDialog() != DialogResult.OK) return;
                target = (tdlg.Target ?? "").Trim().TrimEnd('/');
            }
            if (target.Length == 0) target = RemoteDir;

            Program.PushTarget = target;
            Program.SaveSettings();

            Program.RunAdb("-s " + serial + " shell mkdir -p " + Q(target), 15000);

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要推到手机的文件（目标：" + target + "）";
                dlg.Multiselect = true;
                dlg.Filter = "所有文件|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                var sb = new StringBuilder();
                int ok = 0, fail = 0;
                foreach (var file in dlg.FileNames)
                {
                    var output = Program.RunAdb("-s " + serial + " push " + Q(file) + " " + Q(target) + "/", 600000);
                    if (output.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fail++;
                        sb.AppendLine("[失败] " + Path.GetFileName(file) + "  " + output.Trim());
                    }
                    else { ok++; sb.AppendLine("[成功] " + Path.GetFileName(file)); }
                }

                if (fail > 0)
                    MessageBox.Show("推送到 " + target + "：成功 " + ok + "，失败 " + fail + "\r\n\r\n" + sb,
                        "推送文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    Toast.Show("推送完成", ok + " 个文件 → " + target);
            }
        }

        public static void PullFiles()
        {
            var serial = Program.PrimarySerial;
            if (string.IsNullOrEmpty(serial))
            {
                MessageBox.Show("还没有连接手机。", "取文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new FileBrowserDialog(serial))
                dlg.ShowDialog();
        }
    }

    // ---------- 选择推送目标目录 ----------
    internal sealed class TargetDialog : Form
    {
        private ComboBox box;
        public string Target { get; private set; }

        public TargetDialog(string current)
        {
            Text = "选择推送位置";
            Size = new Size(560, 210);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);

            var lbl = new Label { Left = 16, Top = 14, Width = 520, Text = "把文件推到手机的哪个目录？（可直接输入路径）" };
            Controls.Add(lbl);

            box = new ComboBox { Left = 16, Top = 40, Width = 510, DropDownStyle = ComboBoxStyle.DropDown };
            foreach (var d in new[]
            {
                "/sdcard/Download",
                "/sdcard/DCIM/Camera",
                "/sdcard/Pictures",
                "/sdcard/Movies",
                "/sdcard/Documents",
                "/sdcard/Music",
                "/sdcard/Pictures/Screenshots",
                "/sdcard"
            })
                box.Items.Add(d);

            box.Text = string.IsNullOrEmpty(current) ? "/sdcard/Download" : current;
            Controls.Add(box);

            var hint = new Label
            {
                Left = 16, Top = 74, Width = 510,
                ForeColor = Color.DimGray,
                Text = "目录不存在会自动创建；这个选择会记住，下次直接用。"
            };
            Controls.Add(hint);

            var ok = new Button { Left = 350, Top = 120, Width = 84, Height = 30, Text = "确定" };
            var cancel = new Button { Left = 442, Top = 120, Width = 84, Height = 30, Text = "取消", DialogResult = DialogResult.Cancel };
            Controls.Add(ok);
            Controls.Add(cancel);
            CancelButton = cancel;

            ok.Click += (s, e) =>
            {
                var t = (box.Text ?? "").Trim();
                if (t.Length == 0)
                {
                    MessageBox.Show("请填一个手机上的目录路径，例如 /sdcard/Download", "推送位置",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (!t.StartsWith("/"))
                {
                    MessageBox.Show("路径要以 / 开头（手机上的绝对路径）", "推送位置",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Target = t;
                DialogResult = DialogResult.OK;
            };
        }
    }
}