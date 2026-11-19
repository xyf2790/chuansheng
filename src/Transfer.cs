// Transfer.cs — 手机文件浏览：目录树 + 取文件 + 删除（回收站/彻底）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal sealed class RemoteEntry
    {
        public string Name;
        public bool IsDir;
        public bool IsLink;
        public bool IsUnavailable;
        public long Size;
        public string Modified;
        public string FullPath;

        public string TypeText
        {
            get
            {
                if (IsUnavailable) return "无法读取";
                if (IsDir) return "文件夹";
                if (IsLink) return "符号链接";
                var name = Name ?? "";
                var dot = name.LastIndexOf('.');
                if (dot <= 0 || dot == name.Length - 1) return "文件";
                var ext = name.Substring(dot + 1).ToLowerInvariant();
                string type;
                switch (ext)
                {
                    case "jpg": case "jpeg": case "png": case "gif": case "bmp": case "webp":
                    case "heic": case "heif": case "tif": case "tiff": case "svg": case "ico": case "avif": type = "图片"; break;
                    case "mp4": case "mkv": case "avi": case "mov": case "m4v": case "wmv":
                    case "flv": case "webm": case "3gp": case "mpeg": case "mpg": case "ts": case "m2ts": type = "视频"; break;
                    case "mp3": case "flac": case "wav": case "aac": case "m4a": case "ogg":
                    case "opus": case "wma": case "amr": case "ape": case "aiff": type = "音频"; break;
                    case "zip": case "rar": case "7z": case "tar": case "gz": case "bz2": case "xz": case "tgz": case "zst": type = "压缩包"; break;
                    case "pdf": type = "PDF 文档"; break;
                    case "doc": case "docx": case "odt": case "rtf": type = "文字文档"; break;
                    case "xls": case "xlsx": case "csv": case "ods": type = "表格"; break;
                    case "ppt": case "pptx": case "odp": type = "演示文稿"; break;
                    case "txt": case "md": case "log": type = "文本"; break;
                    case "epub": case "mobi": case "azw": case "azw3": type = "电子书"; break;
                    case "apk": case "apks": case "xapk": case "aab": type = "安卓安装包"; break;
                    case "exe": case "msi": type = "Windows 程序"; break;
                    case "srt": case "ass": case "ssa": case "vtt": case "sub": type = "字幕"; break;
                    case "html": case "htm": type = "网页"; break;
                    default: type = "文件"; break;
                }
                return type + "（" + ext.ToUpperInvariant() + "）";
            }
        }

        public string SizeText
        {
            get
            {
                if (IsDir || IsUnavailable) return "";
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

        // Windows 参数引用与 Android shell 引用是两层，不能混用。
        public static string Arg(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        public static string ShellQ(string value) { return "'" + value.Replace("'", "'\"'\"'") + "'"; }

        public static string RunFileCommand(string args, int timeoutMs, CancellationToken cancellation = default(CancellationToken))
        {
            if (timeoutMs < Timeout.Infinite) throw new ArgumentOutOfRangeException("timeoutMs");
            cancellation.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo(Program.AdbExe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(psi))
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    if (cancellation.IsCancellationRequested || (timeoutMs != Timeout.Infinite && clock.ElapsedMilliseconds >= timeoutMs))
                    {
                        try { process.Kill(); process.WaitForExit(5000); } catch { }
                        cancellation.ThrowIfCancellationRequested();
                        throw new IOException("文件传输超时，请检查手机连接后重试。");
                    }
                    if (process.WaitForExit(100))
                    {
                        if (stdout.IsCompleted && stderr.IsCompleted) break;
                        Thread.Sleep(10);
                    }
                }
                Task.WaitAll(stdout, stderr);
                cancellation.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    var output = (stdout.Result + "\n" + stderr.Result).Trim();
                    throw new IOException(output.Length == 0 ? "ADB 操作失败（退出码 " + process.ExitCode + "）" : output);
                }
                return stdout.Result.TrimEnd('\r', '\n');
            }
        }

        public static void PushFile(string serial, string localPath, string target, CancellationToken cancellation = default(CancellationToken))
        {
            if (!File.Exists(localPath)) throw new FileNotFoundException("找不到文件：" + localPath);
            target = (target ?? "").Trim().TrimEnd('/');
            if (target.Length == 0) target = "/";
            if (!target.StartsWith("/") || target.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new IOException("请输入有效的手机绝对目录路径。");
            var prefix = "-s " + Arg(serial) + " ";
            RunFileCommand(prefix + "shell " + Arg("mkdir -p -- " + ShellQ(target)), 15000, cancellation);
            var remote = target.TrimEnd('/') + "/" + Path.GetFileName(localPath);
            RunShell(serial, "if [ -e " + ShellQ(remote) + " ] || [ -L " + ShellQ(remote)
                + " ]; then printf '手机目标已有同名项目，未覆盖' >&2; exit 1; fi", cancellation);
            // 引号外的 / 会触发部分 Windows ADB 的路径转换，截掉扩展名。
            RunFileCommand(prefix + "push " + Arg(localPath) + " " + Arg(remote), Timeout.Infinite, cancellation);
            var size = RunFileCommand(prefix + "shell " + Arg("stat -c %s -- " + ShellQ(remote)), 15000, cancellation);
            long actual;
            if (!long.TryParse(size.Trim(), out actual) || actual != new FileInfo(localPath).Length)
                throw new IOException("手机端文件校验失败：" + remote + "，请重试。");
        }

        public static void PullEntry(string serial, RemoteEntry entry, string target, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ValidateRemotePath(entry.FullPath);
            if (entry.IsUnavailable) throw new IOException("手机未授予此项目的读取权限，取回未完成：" + entry.Name);
            if (entry.IsLink) throw new IOException("不自动取回符号链接，请进入实际目录后选择文件：" + entry.Name);
            var name = entry.FullPath.Substring(entry.FullPath.LastIndexOf('/') + 1);
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(" ") || name.EndsWith("."))
                throw new IOException("该手机文件名不适用于 Windows，未取回：" + name);
            var deviceName = name.Split('.')[0].ToUpperInvariant();
            if (deviceName == "CON" || deviceName == "PRN" || deviceName == "AUX" || deviceName == "NUL"
                || System.Text.RegularExpressions.Regex.IsMatch(deviceName, @"^(COM|LPT)[1-9]$"))
                throw new IOException("该手机文件名是 Windows 保留名称，未取回：" + name);
            var directory = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var local = Path.GetFullPath(Path.Combine(directory, name));
            if (!local.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) throw new IOException("无效的本地目标路径。");
            if (File.Exists(local) || Directory.Exists(local)) throw new IOException("电脑目标已有同名项目，未覆盖：" + local);
            Directory.CreateDirectory(directory);
            if (entry.IsDir)
            {
                // Create Unicode directories with .NET too: ADB's recursive local
                // directory creation has the same filename conversion problem.
                Directory.CreateDirectory(local);
                foreach (var child in ListRemote(serial, entry.FullPath, cancellation, true))
                    PullEntry(serial, child, local, cancellation);
                return;
            }
            // Give ADB the complete Windows destination; deriving a Unicode basename
            // from a remote path can corrupt it in the bundled Windows ADB.
            RunFileCommand("-s " + Arg(serial) + " pull " + Arg(entry.FullPath) + " " + Arg(local), Timeout.Infinite, cancellation);
        }

        private static string RunShell(string serial, string command, CancellationToken cancellation, int timeout = 60000)
        {
            return RunFileCommand("-s " + Arg(serial) + " shell " + Arg(command), timeout, cancellation);
        }

        private static void ValidateRemotePath(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("/", StringComparison.Ordinal) || path == "/"
                || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                || path.Split('/').Any(p => p == "." || p == ".."))
                throw new IOException("无效的手机文件路径。");
        }

        /// <summary>列出手机某个目录（解析 ls -l）</summary>
        private static readonly System.Text.RegularExpressions.Regex ListingLine = new System.Text.RegularExpressions.Regex(
            @"^([-dlbcps])([rwxStTs-]{9})\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2}) (.+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        public static List<RemoteEntry> ListRemote(string serial, string path, CancellationToken cancellation = default(CancellationToken), bool includeHidden = false)
        {
            var result = new List<RemoteEntry>();
            // A trailing slash enters command-line directory links such as /sdcard.
            // Do not follow every child link (-L), which could recurse outside the directory.
            var directory = DirectoryPath(path);
            var listing = RunFileCommand("-s " + Arg(serial) + " shell " + Arg((includeHidden ? "ls -lanb -- " : "ls -lnb -- ") + ShellQ(directory)), 60000, cancellation);

            using (var reader = new StringReader(listing))
            {
              string raw;
              while ((raw = reader.ReadLine()) != null)
              {
                cancellation.ThrowIfCancellationRequested();
                var line = raw;
                if (line.Length == 0 || line.StartsWith("total ")) continue;

                var m = ListingLine.Match(line);
                if (!m.Success)
                {
                    var denied = System.Text.RegularExpressions.Regex.Match(line, @"^([-dlbcps])\?{9}\s+\?\s+\?\s+\?\s+\?\s+\? (.+)$");
                    if (!denied.Success) throw new IOException("无法安全解析手机目录，已停止操作，避免遗漏或误认文件。");
                    var deniedName = denied.Groups[2].Value;
                    bool deniedLink = denied.Groups[1].Value == "l";
                    if (deniedLink && deniedName.Contains(" -> ")) deniedName = deniedName.Substring(0, deniedName.IndexOf(" -> ", StringComparison.Ordinal));
                    deniedName = DecodeListedName(deniedName);
                    result.Add(new RemoteEntry { Name = deniedName, FullPath = path.TrimEnd('/') + "/" + deniedName,
                        IsDir = denied.Groups[1].Value == "d", IsLink = deniedLink, IsUnavailable = true });
                    continue;
                }

                var name = m.Groups[6].Value;
                // -b escapes spaces inside names, so this unescaped separator belongs only to ls.
                if (m.Groups[1].Value == "l" && name.Contains(" -> ")) name = name.Substring(0, name.IndexOf(" -> ", StringComparison.Ordinal));
                name = DecodeListedName(name);
                if (name == "." || name == "..") continue;

                long size;
                long.TryParse(m.Groups[3].Value, out size);

                result.Add(new RemoteEntry
                {
                    Name = name,
                    IsDir = m.Groups[1].Value == "d",
                    IsLink = m.Groups[1].Value == "l",
                    Size = size,
                    Modified = m.Groups[4].Value + " " + m.Groups[5].Value,
                    FullPath = path.TrimEnd('/') + "/" + name
                });
              }
            }

            // The browser sorts once in the background according to its active column.
            return result;
        }

        private static string DecodeListedName(string value)
        {
            var bytes = new List<byte>();
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\')
                {
                    int start = i;
                    while (i + 1 < value.Length && value[i + 1] != '\\') i++;
                    bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(start, i - start + 1)));
                    continue;
                }
                if (++i >= value.Length) throw new IOException("手机文件名转义不完整，已停止操作。");
                char c = value[i];
                int escape = "abefnrtv".IndexOf(c);
                if (escape >= 0) bytes.Add(new byte[] { 7, 8, 27, 12, 10, 13, 9, 11 }[escape]);
                else if (c == '\\' || c == ' ') bytes.Add((byte)c);
                else if (c >= '0' && c <= '7' && i + 2 < value.Length
                    && value[i + 1] >= '0' && value[i + 1] <= '7' && value[i + 2] >= '0' && value[i + 2] <= '7')
                {
                    int code = (c - '0') * 64 + (value[i + 1] - '0') * 8 + value[i + 2] - '0';
                    if (code == 0 || code > 255) throw new IOException("手机文件名转义无效，已停止操作。");
                    bytes.Add((byte)code);
                    i += 2;
                }
                else throw new IOException("不支持的手机文件名转义，已停止操作。");
            }
            try { return new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
            catch (DecoderFallbackException) { throw new IOException("手机文件名不是有效 UTF-8，已停止操作。"); }
        }

        private static string DirectoryPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("/", StringComparison.Ordinal)
                || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
                || path.Split('/').Any(p => p == "." || p == ".."))
                throw new IOException("无效的手机目录路径。");
            return path.TrimEnd('/') + "/";
        }

        /// <summary>只列子目录（给树用）</summary>
        public static List<string> ListSubDirs(string serial, string path, CancellationToken cancellation = default(CancellationToken))
        {
            var dirs = new List<string>();
            foreach (var entry in ListRemote(serial, path, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.IsDir) dirs.Add(entry.Name);
            }
            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            return dirs;
        }

        // ---------- 删除 / 回收站 ----------
        public static bool MoveToTrash(string serial, RemoteEntry entry, out string message, CancellationToken cancellation = default(CancellationToken))
        {
            try
            {
                ValidateRemotePath(entry.FullPath);
                // Each item owns a bucket; payload and metadata cannot collide, even for a file named .source.
                var bucket = TrashRoot + "/" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".v2";
                RunShell(serial, "mkdir -p -- " + ShellQ(TrashRoot) + " && mkdir -- " + ShellQ(bucket)
                    + " && mkdir -- " + ShellQ(bucket + "/data"), cancellation);
                // Write provenance BEFORE moving, so an interrupted move never leaves an unrecorded payload.
                RunShell(serial, "printf '%s' " + ShellQ(entry.FullPath) + " > " + ShellQ(bucket + "/.source"), cancellation);
                RunShell(serial, "mv -- " + ShellQ(entry.FullPath) + " " + ShellQ(bucket + "/data/"), cancellation, Timeout.Infinite);
                message = "已移到手机回收站：" + bucket;
                return true;
            }
            catch (IOException ex) { message = ex.Message; return false; }
        }

        public static bool DeletePermanent(string serial, RemoteEntry entry, out string message, CancellationToken cancellation = default(CancellationToken))
        {
            try
            {
                ValidateRemotePath(entry.FullPath);
                RunShell(serial, "rm -rf -- " + ShellQ(entry.FullPath), cancellation, Timeout.Infinite);
                message = "已彻底删除：" + entry.FullPath;
                return true;
            }
            catch (IOException ex) { message = ex.Message; return false; }
        }

        internal sealed class TrashItem
        {
            public string Bucket;
            public string Name;
            public bool IsDir;
            public long Size;
            public string Source;
            public string DeletedAt;
            public string StoredPath;
        }

        public static List<TrashItem> ListTrash(string serial, CancellationToken cancellation = default(CancellationToken))
        {
            var items = new List<TrashItem>();
            // A missing recycle bin is empty; permission/connection failures still surface as errors.
            var exists = RunShell(serial, "if [ -d " + ShellQ(TrashRoot) + " ]; then printf yes; elif [ -e " + ShellQ(TrashRoot)
                + " ]; then printf '回收站路径不是目录' >&2; exit 1; fi", cancellation);
            if (exists != "yes") return items;
            foreach (var bucket in ListRemote(serial, TrashRoot, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!bucket.IsDir) continue;
                var source = RunShell(serial, "if [ -f " + ShellQ(bucket.FullPath + "/.source") + " ]; then cat -- "
                    + ShellQ(bucket.FullPath + "/.source") + "; fi", cancellation);
                bool version2 = bucket.Name.EndsWith(".v2", StringComparison.Ordinal);
                var directory = version2 ? bucket.FullPath + "/data" : bucket.FullPath;
                if (version2 && RunShell(serial, "if [ -d " + ShellQ(directory) + " ]; then printf yes; fi", cancellation) != "yes") continue;
                foreach (var entry in ListRemote(serial, directory, cancellation, true))
                {
                    if (!version2 && entry.Name == ".source") continue;
                    items.Add(new TrashItem { Bucket = bucket.FullPath, Name = entry.Name, IsDir = entry.IsDir,
                        Size = entry.Size, Source = source, DeletedAt = bucket.Name, StoredPath = entry.FullPath });
                }
            }
            return items.OrderByDescending(i => i.DeletedAt).ToList();
        }

        private static string TrashPath(TrashItem item)
        {
            ValidateRemotePath(item.Bucket);
            if (!item.Bucket.StartsWith(TrashRoot + "/", StringComparison.Ordinal)
                || item.Bucket.Substring(TrashRoot.Length + 1).Contains("/"))
                throw new IOException("无效的回收站批次路径。");
            var stored = string.IsNullOrEmpty(item.StoredPath) ? item.Bucket + "/" + item.Name : item.StoredPath;
            ValidateRemotePath(stored);
            if (!stored.StartsWith(item.Bucket + "/", StringComparison.Ordinal)) throw new IOException("无效的回收站项目路径。");
            return stored;
        }

        public static bool RestoreTrash(string serial, TrashItem item, out string message, CancellationToken cancellation = default(CancellationToken))
        {
            try
            {
                var stored = TrashPath(item);
                ValidateRemotePath(item.Source);
                var parent = item.Source.Substring(0, item.Source.LastIndexOf('/'));
                if (parent.Length == 0) parent = "/";
                RunShell(serial, "mkdir -p -- " + ShellQ(parent), cancellation);
                // mv -n avoids overwriting a newer file. Some Android versions return success when skipped.
                RunShell(serial, "mv -nT -- " + ShellQ(stored) + " " + ShellQ(item.Source)
                    + " && if [ -e " + ShellQ(stored) + " ] || [ -L " + ShellQ(stored)
                    + " ]; then printf '原路径已有同名项目，未覆盖' >&2; exit 1; fi", cancellation, Timeout.Infinite);
                CleanEmptyBucket(serial, item, cancellation);
                message = "已还原到：" + item.Source;
                return true;
            }
            catch (IOException ex) { message = ex.Message; return false; }
        }

        public static bool PurgeTrash(string serial, TrashItem item, out string message, CancellationToken cancellation = default(CancellationToken))
        {
            try
            {
                var stored = TrashPath(item);
                // Delete only the selected payload: legacy buckets may contain multiple items.
                RunShell(serial, "rm -rf -- " + ShellQ(stored), cancellation, Timeout.Infinite);
                CleanEmptyBucket(serial, item, cancellation);
                message = "已从回收站彻底删除";
                return true;
            }
            catch (IOException ex) { message = ex.Message; return false; }
        }

        private static void CleanEmptyBucket(string serial, TrashItem item, CancellationToken cancellation)
        {
            if (!item.Bucket.EndsWith(".v2", StringComparison.Ordinal)) return;
            // rmdir succeeds only when empty; never recursively remove a bucket that might contain another payload.
            RunShell(serial, "if rmdir -- " + ShellQ(item.Bucket + "/data") + " 2>/dev/null; then rm -f -- "
                + ShellQ(item.Bucket + "/.source") + " && rmdir -- " + ShellQ(item.Bucket) + "; fi", cancellation);
        }

        /// <summary>老机型没有 find -iname 时的兜底：逐层 ls 递归找（限深度和目录数）</summary>
        public static List<RemoteEntry> SearchRemote(string serial, string root, string keyword, CancellationToken cancellation, out string warning)
        {
            warning = "";
            var results = new List<RemoteEntry>();
            // Treat the user's keyword literally, just like local substring filtering.
            var pattern = "*" + keyword.Replace("\\", "\\\\").Replace("*", "\\*").Replace("?", "\\?").Replace("[", "\\[") + "*";
            try
            {
                foreach (var type in new[] { "d", "f" })
                {
                    var command = "find " + ShellQ(DirectoryPath(root)) + " -maxdepth 4 -type " + type + " -iname " + ShellQ(pattern) + " -print0";
                    string output;
                    bool partial = false;
                    try { output = RunFileCommand("-s " + Arg(serial) + " shell " + Arg(command), 60000, cancellation); }
                    catch (IOException ex)
                    {
                        var error = ex.Message.ToLowerInvariant();
                        if (!error.Contains("permission denied") && !error.Contains("operation not permitted")) throw;
                        // find emits usable matches before exiting nonzero for inaccessible subdirectories.
                        output = ex.Message;
                        partial = true;
                        warning = "部分目录无权访问，搜索结果可能不完整";
                    }
                    int start = 0;
                    while (start < output.Length)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int end = output.IndexOf('\0', start);
                        if (end < 0)
                        {
                            if (partial) break; // stderr follows the last complete, NUL-terminated match.
                            throw new IOException("手机搜索未返回安全的文件名分隔符，已停止显示结果。");
                        }
                        var path = output.Substring(start, end - start).TrimEnd('/');
                        start = end + 1;
                        if (!path.StartsWith("/", StringComparison.Ordinal)) continue;
                        // Windows ADB shell can rewrite LF inside a name to CRLF even with -T.
                        // These names are already unsupported by file mutations; never invent a path by normalizing them.
                        if (path.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                        {
                            const string skipped = "含换行控制字符的名称不支持操作，已从搜索结果跳过";
                            if (!warning.Contains(skipped)) warning = warning.Length == 0 ? skipped : warning + "；" + skipped;
                            continue;
                        }
                        results.Add(new RemoteEntry { Name = path, FullPath = path, IsDir = type == "d", Modified = "" });
                    }
                }
                return results;
            }
            catch (IOException ex)
            {
                var error = ex.Message.ToLowerInvariant();
                if (!error.Contains("unknown") && !error.Contains("unrecognized") && !error.Contains("bad option") && !error.Contains("invalid option")) throw;
                warning = "兼容搜索最多扫描 300 个目录、4 层，跳过无法读取的子目录，结果可能不完整";
                return SearchRecursive(serial, root, keyword, 3, 300, cancellation);
            }
        }

        public static List<RemoteEntry> SearchRecursive(string serial, string root, string keyword, int maxDepth, int maxDirs, CancellationToken cancellation = default(CancellationToken))
        {
            var results = new List<RemoteEntry>();
            var queue = new Queue<Tuple<string, int>>();
            queue.Enqueue(Tuple.Create(root, 0));
            int visited = 0;

            while (queue.Count > 0 && visited < maxDirs)
            {
                cancellation.ThrowIfCancellationRequested();
                var cur = queue.Dequeue();
                visited++;

                List<RemoteEntry> entries;
                try { entries = ListRemote(serial, cur.Item1, cancellation); }
                catch (IOException) { if (visited == 1) throw; else continue; }

                foreach (var e in entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (e.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        results.Add(e);

                    if (e.IsDir && cur.Item2 < maxDepth)
                        queue.Enqueue(Tuple.Create(e.FullPath, cur.Item2 + 1));
                }
            }

            return results;
        }

        // ---------- 入口 ----------
        private static bool pushing;
        public static void PushFiles()
        {
            if (pushing) return;
            pushing = true;
            try { PushFilesCore(); }
            finally { pushing = false; }
        }

        private static void PushFilesCore()
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
                target = (tdlg.Target ?? "").Trim();
            }
            if (target.Length == 0) target = RemoteDir;

            Program.PushTarget = target;
            Program.SaveSettings();

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择要推到手机的文件（目标：" + target + "）";
                dlg.Multiselect = true;
                dlg.Filter = "所有文件|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                using (var operation = new FileOperationDialog("推送文件", dlg.FileNames,
                    (index, token) => PushFile(serial, dlg.FileNames[index], target, token)))
                {
                    operation.ShowDialog();
                    operation.ShowResult("推送到：" + target);
                }
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

    // One modal progress window keeps file work off the UI thread and prevents repeated actions.
    internal sealed class FileOperationDialog : Form
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly string[] names;
        private readonly Action<int, CancellationToken> action;
        private readonly Label status;
        private readonly Button cancel;
        private bool running;
        private bool finished;
        private int completed;
        private int failed;
        private readonly StringBuilder errors = new StringBuilder();
        public bool WasCancelled { get; private set; }

        public FileOperationDialog(string title, string[] names, Action<int, CancellationToken> action)
        {
            this.names = names;
            this.action = action;
            Text = title;
            Size = new Size(570, 190);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            status = new Label { Left = 16, Top = 16, Width = 520, Height = 45, AutoEllipsis = true };
            var progress = new ProgressBar { Left = 16, Top = 68, Width = 520, Height = 16, Style = ProgressBarStyle.Marquee };
            cancel = new Button { Left = 440, Top = 102, Width = 96, Height = 28, Text = "取消" };
            cancel.Click += (sender, e) => RequestCancel();
            Controls.Add(status); Controls.Add(progress); Controls.Add(cancel);
            CancelButton = cancel;
            Shown += (sender, e) => Run();
            FormClosing += (sender, e) =>
            {
                if (finished) return;
                RequestCancel();
                if (running) e.Cancel = true;
            };
        }

        private void RequestCancel()
        {
            cancellation.Cancel();
            status.Text = "正在取消，等待当前操作停止…";
            cancel.Enabled = false;
        }

        private async void Run()
        {
            running = true;
            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    int index = i;
                    status.Text = (i + 1) + " / " + names.Length + "：" + names[i];
                    try
                    {
                        await Task.Run(() => action(index, cancellation.Token), cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        completed++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { failed++; errors.AppendLine(names[i] + "：" + ex.Message); }
                }
            }
            catch (OperationCanceledException) { WasCancelled = true; }
            finally
            {
                WasCancelled = WasCancelled || cancellation.IsCancellationRequested;
                running = false;
                finished = true;
                if (!IsDisposed)
                {
                    DialogResult = WasCancelled ? DialogResult.Cancel : DialogResult.OK;
                    Close();
                }
                else cancellation.Dispose();
            }
        }

        public void ShowResult(string destination)
        {
            if (WasCancelled)
                MessageBox.Show("已取消。已完成 " + completed + "，失败 " + failed + "。\r\n正在处理的项目可能已有部分更改，请核对。\r\n" + destination, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else if (failed > 0)
                MessageBox.Show("成功 " + completed + "，失败 " + failed + "\r\n" + destination + "\r\n\r\n" + errors, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                Toast.Show(Text + "完成", completed + " 个项目；" + destination);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !finished) cancellation.Cancel();
            base.Dispose(disposing);
            if (disposing && !running) cancellation.Dispose();
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
