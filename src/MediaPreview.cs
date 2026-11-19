// MediaPreview.cs — 本地临时缓存 + 图片预览 / Windows Media Player 视频预览
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class MediaPreview
    {
        private const string Images = "|.jpg|.jpeg|.png|.bmp|.gif|.tif|.tiff|.webp|.heic|.heif|";
        private const string Videos = "|.mp4|.m4v|.mov|.avi|.mkv|.wmv|.webm|.3gp|.mpeg|.mpg|.ts|";
        private static readonly string CacheRoot = Path.Combine(Path.GetTempPath(), "PhoneAudioTray-preview", Guid.NewGuid().ToString("N"));

        private static readonly object CacheGate = new object();
        private static readonly HashSet<string> ExternalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int cacheGeneration;

        public static bool IsVideo(string path) { return HasExtension(Videos, path); }
        public static bool CanPreview(string path) { return IsVideo(path) || HasExtension(Images, path); }
        private static bool HasExtension(string types, string path)
        {
            var extension = Extension(path);
            return extension.Length > 1 && types.Contains("|" + extension + "|");
        }

        private static string Extension(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var dot = path.LastIndexOf('.');
            return dot > path.LastIndexOf('/') ? path.Substring(dot).ToLowerInvariant() : "";
        }

        public static string CachePath(string serial, string path, string revision)
        {
            string key;
            using (var sha = SHA256.Create())
                key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(serial + "\n" + path + "\n" + revision))).Replace("-", "");
            // 不使用远端名称作为本地目录或命令，文件扩展名只用于选择解码器。
            var extension = Extension(path);
            return Path.Combine(CacheRoot, key + (CanPreview(path) ? extension : ""));
        }

        private static string ReadRevision(string prefix, string path, CancellationToken cancellation, out long size)
        {
            var revision = Transfer.RunFileCommand(prefix + "shell " + Transfer.Arg("stat -c '%s|%Y' -- " + Transfer.ShellQ(path)), 15000, cancellation).Trim();
            var parts = revision.Split('|');
            long modified;
            if (parts.Length != 2 || !long.TryParse(parts[0], out size) || size < 0 || !long.TryParse(parts[1], out modified))
                throw new IOException("无法读取文件大小和修改时间，请刷新目录后重试。");
            return revision;
        }

        public static string Download(string serial, RemoteEntry entry, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            int generation;
            lock (CacheGate) generation = cacheGeneration;
            var prefix = "-s " + Transfer.Arg(serial) + " ";
            long size;
            var revision = ReadRevision(prefix, entry.FullPath, cancellation, out size);
            var cache = CachePath(serial, entry.FullPath, revision);
            lock (CacheGate) {
                cancellation.ThrowIfCancellationRequested();
                if (generation != cacheGeneration) throw new OperationCanceledException("预览缓存已清理。");
                if (File.Exists(cache) && new FileInfo(cache).Length == size) return cache;
                Directory.CreateDirectory(CacheRoot);
            }
            var partial = cache + "." + Guid.NewGuid().ToString("N") + ".part";
            try {
                Transfer.RunFileCommand(prefix + "pull " + Transfer.Arg(entry.FullPath) + " " + Transfer.Arg(partial), 600000, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!File.Exists(partial) || new FileInfo(partial).Length != size)
                    throw new IOException("预览文件下载不完整，请重试。");
                long currentSize;
                if (ReadRevision(prefix, entry.FullPath, cancellation, out currentSize) != revision)
                    throw new IOException("文件在下载期间发生变化，请重新打开预览。");
                lock (CacheGate) {
                    cancellation.ThrowIfCancellationRequested();
                    if (generation != cacheGeneration) throw new OperationCanceledException("预览缓存已清理。");
                    if (File.Exists(cache)) {
                        if (new FileInfo(cache).Length == size) return cache;
                        File.Delete(cache); // 损坏的旧缓存不能阻止所有后续下载。
                    }
                    File.Move(partial, cache);
                }
                return cache;
            }
            finally { DeletePartial(partial); }
        }

        private static void DeletePartial(string partial)
        {
            // A remote file can become a directory between stat and pull. Clean only our own
            // generated partial target, including directories, never a caller-supplied path.
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(partial)), CacheRoot, StringComparison.OrdinalIgnoreCase)
                || !partial.EndsWith(".part", StringComparison.Ordinal)) return;
            try {
                if (Directory.Exists(partial)) Directory.Delete(partial, true);
                else if (File.Exists(partial)) File.Delete(partial);
            }
            catch { }
        }

        internal static void KeepForExternalApp(string path)
        {
            // ShellExecute 可能复用已有播放器且不返回进程；不猜测其退出时间或终止用户播放器。
            // 此类文件留给系统临时文件清理，避免主程序退出时删掉播放器仍需读取的媒体。
            lock (CacheGate) ExternalFiles.Add(path);
        }

        public static void Cleanup()
        {
            lock (CacheGate) {
                cacheGeneration++;
                if (!Directory.Exists(CacheRoot)) return;
                string[] files;
                try { files = Directory.GetFiles(CacheRoot); } catch { return; }
                foreach (var path in files) {
                    if (ExternalFiles.Contains(path)) continue;
                    try { File.Delete(path); } catch { }
                }
                try { foreach (var partial in Directory.GetDirectories(CacheRoot, "*.part")) DeletePartial(partial); } catch { }
                try { Directory.Delete(CacheRoot, false); } catch { }
            }
        }
    }
    internal sealed class MediaPreviewDialog : Form
    {
        private readonly string serial;
        private readonly RemoteEntry entry;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Panel surface;
        private readonly Label status;
        private readonly Button open;
        private PictureBox picture;
        private VideoPlayer player;
        private string localPath;
        private bool closed;

        public MediaPreviewDialog(string serial, RemoteEntry entry)
        {
            this.serial = serial;
            this.entry = entry;
            Text = "媒体预览 · " + entry.Name;
            Size = new Size(920, 680);
            MinimumSize = new Size(600, 420);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            var header = new Label
            {
                Dock = DockStyle.Top, Height = 52, Padding = new Padding(14, 8, 14, 0),
                Text = entry.Name + "\r\n" + entry.FullPath, AutoEllipsis = true
            };
            surface = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 30, 34) };
            var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 48, ColumnCount = 3, Padding = new Padding(10, 7, 10, 7) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Text = "正在从手机加载预览… 视频需缓存完成后播放。" };
            open = new Button { Text = "用系统应用打开", Dock = DockStyle.Fill, Enabled = false };
            open.Click += (s, e) => OpenExternal();
            var close = new Button { Text = "关闭", Dock = DockStyle.Fill, DialogResult = DialogResult.Cancel };
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(open, 1, 0);
            footer.Controls.Add(close, 2, 0);
            Controls.Add(surface);
            Controls.Add(header);
            Controls.Add(footer);
            CancelButton = close;
            Shown += (s, e) => LoadPreview();
            FormClosing += (s, e) => { closed = true; cancellation.Cancel(); };
        }

        private async void LoadPreview()
        {
            try
            {
                localPath = await Task.Run(() => MediaPreview.Download(serial, entry, cancellation.Token));
                if (closed) return;
                open.Enabled = true;
                if (MediaPreview.IsVideo(entry.FullPath))
                {
                    player = new VideoPlayer { Dock = DockStyle.Fill };
                    surface.Controls.Add(player);
                    player.Play(localPath);
                    status.Text = "视频预览 · 如无画面或声音，可用系统应用打开。";
                }
                else
                {
                    var image = Image.FromFile(localPath);
                    picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = image };
                    surface.Controls.Add(picture);
                    status.Text = image.Width + " × " + image.Height + " · 按窗口大小适配";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (closed) return;
                if (player != null) { surface.Controls.Remove(player); player.Dispose(); player = null; }
                var message = localPath == null
                    ? "加载失败：" + ex.Message + "\r\n关闭后重试，并确认手机仍然连接。"
                    : "此格式暂时无法在窗口内预览。\r\n请点击「用系统应用打开」。";
                surface.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.WhiteSmoke, TextAlign = ContentAlignment.MiddleCenter, Text = message });
                status.Text = localPath == null ? "预览加载失败" : "已缓存，等待系统应用打开";
            }
        }

        private void OpenExternal()
        {
            try {
                using (var launched = Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true })) { }
                MediaPreview.KeepForExternalApp(localPath);
                if (player != null) player.StopPlayback();
            }
            catch (Exception ex) { MessageBox.Show(this, "无法打开文件：" + ex.Message, "媒体预览", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closed = true;
                cancellation.Cancel();
                if (picture != null && picture.Image != null) { picture.Image.Dispose(); picture.Image = null; }
            }
            base.Dispose(disposing);
        }
    }

    // 系统自带的播放器控件；没有安装或编码不支持时保留外部播放器入口。
    internal sealed class VideoPlayer : AxHost
    {
        public VideoPlayer() : base("6BF52A52-394A-11D3-B153-00C04F79FAA6") { }
        public void StopPlayback()
        {
            try {
                var control = GetOcx();
                if (control != null) control.GetType().InvokeMember("close", BindingFlags.InvokeMethod, null, control, null);
            }
            catch { } // 未安装控件或已销毁时仍须允许窗口关闭。
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) StopPlayback();
            base.Dispose(disposing);
        }
        public void Play(string path)
        {
            CreateControl();
            var control = GetOcx();
            control.GetType().InvokeMember("uiMode", BindingFlags.SetProperty, null, control, new object[] { "full" });
            control.GetType().InvokeMember("stretchToFit", BindingFlags.SetProperty, null, control, new object[] { true });
            control.GetType().InvokeMember("URL", BindingFlags.SetProperty, null, control, new object[] { path });
        }
    }
}
