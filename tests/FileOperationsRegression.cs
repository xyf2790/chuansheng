using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class FileOperationsRegression
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "delayed") { Thread.Sleep(350); Console.Write("error.txt"); Console.Error.Write("diagnostic"); return 0; }
            if (args.Length > 0 && args[0] == "slow") { Thread.Sleep(5000); return 0; }
            if (args.Length > 0 && args[0] == "-s")
            {
                File.AppendAllText(Environment.GetEnvironmentVariable("FILE_OP_TEST_LOG"), args[3] + "\n");
                if (args[1] == "fail-mkdir" && args[3].Contains("mkdir")) { Console.Error.Write("Permission denied"); return 3; }
                if (args[1] == "fail-delete") { Console.Error.Write("Permission denied"); return 5; }
                if (args[1] == "names")
                {
                    if (!args[3].Contains("'/synthetic/'")) { Console.Error.Write("Directory links require a trailing slash"); return 1; }
                    Console.WriteLine("-rw-rw-r-- 1 0 0 12 2026-11-17 12:00 foo");
                    Console.WriteLine("-rw-rw-r-- 1 0 0 12 2026-11-17 12:00 \\ foo\\ ");
                }
                if (args[1] == "escaped")
                {
                    Console.WriteLine("-rw-rw-r-- 1 0 0 12 2026-11-17 12:00 first\\n-rw-rw-r--\\ 1\\ 0\\ 0\\ 12\\ 2026-11-17\\ 12:00\\ victim");
                    Console.WriteLine("lrwxrwxrwx 1 0 0 6 2026-11-17 12:00 link\\ ->\\ name -> target\\ ->\\ name");
                    Console.WriteLine("-rw-rw-r-- 1 0 0 12 2026-11-17 12:00 literal\\\\n");
                    Console.WriteLine("-rw-rw-r-- 1 0 0 12 2026-11-17 12:00 \\303\\251.txt");
                    Console.WriteLine("d????????? ? ? ? ? ? restricted");
                }
                if (args[1] == "malformed") Console.WriteLine("unsupported metadata format");
                return 0;
            }
            try
            {
                var exe = typeof(Program).GetField("_adb", BindingFlags.Static | BindingFlags.NonPublic);
                exe.SetValue(null, Assembly.GetExecutingAssembly().Location);
                var log = Path.Combine(Path.GetTempPath(), "Transmit-FileOps-" + Guid.NewGuid().ToString("N") + ".log");
                Environment.SetEnvironmentVariable("FILE_OP_TEST_LOG", log);
                var output = Transfer.RunFileCommand("delayed", Timeout.Infinite);
                Check(output == "error.txt", "Successful stderr must not corrupt command stdout");
                string message;
                Check(!Transfer.MoveToTrash("fail-mkdir", new RemoteEntry { Name = "test.txt", FullPath = "/synthetic/test.txt" }, out message), "Failed mkdir must stop trash operation");
                Check(!File.ReadAllText(log).Contains("mv "), "Trash must not attempt mv after failed mkdir");
                Check(!Transfer.DeletePermanent("fail-delete", new RemoteEntry { Name = "test.txt", FullPath = "/synthetic/test.txt" }, out message), "Nonzero deletion exit code must fail even without the word error");
                var names = Transfer.ListRemote("names", "/synthetic");
                Check(names.Count == 2 && names[0].FullPath == "/synthetic/foo" && names[1].FullPath == "/synthetic/ foo ", "Whitespace filenames must map to exact distinct paths, never the neighboring file");
                var escaped = Transfer.ListRemote("escaped", "/synthetic");
                Check(escaped.Count == 5 && escaped[0].Name == "first\n-rw-rw-r-- 1 0 0 12 2026-11-17 12:00 victim"
                    && escaped[1].Name == "link -> name" && escaped[2].Name == "literal\\n" && escaped[3].Name == "é.txt"
                    && escaped[4].IsUnavailable, "Escaped names, UTF8 bytes, permission errors and link delimiters must preserve exact targets");
                Check(escaped[1].IsLink && escaped[1].TypeText == "符号链接", "Symbolic links must be visibly identified");
                try { Transfer.PullEntry("fake", escaped[1], Path.GetTempPath(), CancellationToken.None); throw new Exception("Symbolic link pull followed an unselected target"); }
                catch (IOException) { }
                try { Transfer.PullEntry("fake", escaped[4], Path.GetTempPath(), CancellationToken.None); throw new Exception("Unreadable child was silently omitted from pull"); }
                catch (IOException) { }
                try { Transfer.ListRemote("malformed", "/synthetic"); throw new Exception("Malformed directory was accepted as empty"); }
                catch (IOException) { }
                var existing = Path.GetTempFileName();
                File.WriteAllText(existing, "must survive");
                try
                {
                    try { Transfer.PullEntry("fake", new RemoteEntry { FullPath = "/synthetic/" + Path.GetFileName(existing) }, Path.GetDirectoryName(existing), CancellationToken.None); throw new Exception("Pull overwrote existing file"); }
                    catch (IOException) { Check(File.ReadAllText(existing) == "must survive", "Existing local file changed"); }
                    foreach (var invalid in new[] { "../escape", "file:stream", "CON.txt", "trailing.", "trailing " })
                    {
                        try { Transfer.PullEntry("fake", new RemoteEntry { FullPath = "/synthetic/" + invalid }, Path.GetDirectoryName(existing), CancellationToken.None); throw new Exception("Unsafe local filename accepted: " + invalid); }
                        catch (IOException) { }
                    }
                }
                finally { File.Delete(existing); }
                Console.WriteLine("PASS infinite timeout, stdout separation, mkdir failure, deletion exit code");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                bool reportedComplete = false;
                int ticks = 0;
                using (var operation = new FileOperationDialog("Synthetic cancellation", new[] { "synthetic file" }, (index, token) =>
                {
                    Transfer.RunFileCommand("slow", Timeout.Infinite, token);
                    reportedComplete = true;
                }))
                using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
                {
                    operation.StartPosition = FormStartPosition.Manual;
                    operation.Location = new System.Drawing.Point(-10000, -10000);
                    operation.ShowInTaskbar = false;
                    timer.Tick += (sender, e) => { if (++ticks == 6) operation.Close(); };
                    var clock = Stopwatch.StartNew();
                    timer.Start();
                    operation.ShowDialog();
                    Check(ticks >= 6 && clock.ElapsedMilliseconds < 2000, "Progress dialog must remain responsive and cancel its child process promptly");
                    Check(operation.WasCancelled && !reportedComplete, "Closing a transfer must not report completion");
                }
                Console.WriteLine("PASS responsive transfer progress and close cancellation without false completion");
                File.Delete(log);
                if (args.Length == 2)
                {
                    exe.SetValue(null, args[0]);
                    TestDevice(args[1]);
                }
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

        private static void TestDevice(string serial)
        {
            var rootEntries = Transfer.ListRemote(serial, "/sdcard");
            var slashEntries = Transfer.ListRemote(serial, "/sdcard/");
            Check(rootEntries.Count > 0 && rootEntries.Count == slashEntries.Count, "sdcard link must list its contents, with or without a trailing slash");
            Check(rootEntries.Exists(entry => entry.IsDir && entry.Name == "Download"), "sdcard must contain the Download directory");
            Check(Transfer.ListSubDirs(serial, "/sdcard").Contains("Download"), "Directory tree must expand the sdcard link");
            Check(Transfer.ListRemote(serial, "/").Count > 0, "Filesystem root must remain browsable");
            var id = "Transmit-FileSafety-" + Guid.NewGuid().ToString("N");
            var local = Path.Combine(Path.GetTempPath(), id, "中文下载");
            var remote = "/sdcard/Download/" + id;
            Directory.CreateDirectory(local);
            string message;
            foreach (var name in new[] { "中文合成资料 (Example).pdf", "日本語の画像 123456.png", "error special '$ & (1).txt", ".source" })
            {
                var file = Path.Combine(local, name);
                File.WriteAllText(file, "Synthetic original content: " + id);
                Transfer.PushFile(serial, file, remote);
                try { Transfer.PushFile(serial, file, remote); throw new Exception("Push overwrote existing remote file"); }
                catch (IOException) { }
                var entry = new RemoteEntry { Name = name, FullPath = remote + "/" + name };
                var pull = Path.Combine(local, "pull-initial");
                Transfer.PullEntry(serial, entry, pull, CancellationToken.None);
                Check(File.ReadAllText(Path.Combine(pull, name)) == File.ReadAllText(file), "Special filename pull changed content");
                Check(Transfer.MoveToTrash(serial, entry, out message), "Synthetic trash failed: " + message);
                var trash = Transfer.ListTrash(serial).Find(item => item.Source == entry.FullPath);
                Check(trash != null && trash.Name == name, "Exact provenance/name missing, including .source filename");
                File.WriteAllText(file, "Synthetic replacement must survive");
                Transfer.PushFile(serial, file, remote);
                Check(!Transfer.RestoreTrash(serial, trash, out message), "Restore must refuse to overwrite existing file");
                var replacement = Path.Combine(local, "pull-replacement");
                Transfer.PullEntry(serial, entry, replacement, CancellationToken.None);
                Check(File.ReadAllText(Path.Combine(replacement, name)) == "Synthetic replacement must survive", "Restore overwrote replacement");
                Check(Transfer.DeletePermanent(serial, entry, out message), "Cannot delete synthetic replacement: " + message);
                Check(Transfer.RestoreTrash(serial, trash, out message), "Synthetic restore failed: " + message);
                var restored = Path.Combine(local, "pull-restored");
                Transfer.PullEntry(serial, entry, restored, CancellationToken.None);
                Check(File.ReadAllText(Path.Combine(restored, name)) == "Synthetic original content: " + id, "Restore changed original bytes");
                Check(Transfer.MoveToTrash(serial, entry, out message), "Second synthetic trash failed: " + message);
                trash = Transfer.ListTrash(serial).Find(item => item.Source == entry.FullPath);
                Check(trash != null && Transfer.PurgeTrash(serial, trash, out message), "Synthetic purge failed");
            }
            var nestedName = "子目录";
            var nestedFile = Path.Combine(local, "日本語の画像.png");
            File.WriteAllText(nestedFile, "Synthetic nested Unicode file");
            Transfer.PushFile(serial, nestedFile, remote + "/" + nestedName);
            string warning;
            var found = Transfer.SearchRemote(serial, "/sdcard", id, CancellationToken.None, out warning);
            Check(found.Exists(entry => entry.IsDir && entry.FullPath == remote), "Search must enter the sdcard link");
            var nestedTarget = Path.Combine(local, "文件夹取回");
            Transfer.PullEntry(serial, new RemoteEntry { Name = nestedName, FullPath = remote + "/" + nestedName, IsDir = true }, nestedTarget, CancellationToken.None);
            Check(File.ReadAllText(Path.Combine(nestedTarget, nestedName, Path.GetFileName(nestedFile))) == "Synthetic nested Unicode file", "Nested Unicode pull changed name/content");
            var alias = "/data/local/tmp/" + id;
            var prefix = "-s " + Transfer.Arg(serial) + " shell ";
            Transfer.RunFileCommand(prefix + Transfer.Arg("ln -s " + Transfer.ShellQ(remote) + " " + Transfer.ShellQ(alias)), 5000);
            try
            {
                Check(Transfer.ListSubDirs(serial, alias).Contains(nestedName), "Arbitrary directory links must expand");
                found = Transfer.SearchRemote(serial, alias, "日本語", CancellationToken.None, out warning);
                Check(found.Exists(entry => entry.FullPath == alias + "/" + nestedName + "/" + Path.GetFileName(nestedFile)), "Search must enter an arbitrary directory link");
                // /data/local/tmp links can be readable by shell yet rejected by the
                // device's ADB sync service. Pull is tested via /sdcard above.
            }
            finally { Transfer.RunFileCommand(prefix + Transfer.Arg("rm -- " + Transfer.ShellQ(alias)), 5000); }
            var unusualRoot = "/data/local/tmp/" + id + "-names";
            var newlineName = "first\n-rw-rw-r-- 1 0 0 12 2026-11-17 12:00 victim";
            Transfer.RunFileCommand(prefix + Transfer.Arg("mkdir -- " + Transfer.ShellQ(unusualRoot)
                + " && touch -- " + Transfer.ShellQ(unusualRoot + "/" + newlineName)
                + " " + Transfer.ShellQ(unusualRoot + "/victim")
                + " " + Transfer.ShellQ(unusualRoot + "/literal\\n")
                + " && ln -s " + Transfer.ShellQ("victim") + " " + Transfer.ShellQ(unusualRoot + "/link -> name")), 5000);
            try
            {
                var unusual = Transfer.ListRemote(serial, unusualRoot);
                Check(unusual.Count == 4 && unusual.Exists(entry => entry.Name == newlineName)
                    && unusual.Exists(entry => entry.Name == "literal\\n"), "Control-character names created forged or truncated entries");
                var link = unusual.Find(entry => entry.Name == "link -> name");
                Check(link != null && link.IsLink, "Arrow in symlink name was truncated");
                Check(Transfer.DeletePermanent(serial, link, out message), "Synthetic symlink delete failed: " + message);
                unusual = Transfer.ListRemote(serial, unusualRoot);
                Check(unusual.Count == 3 && unusual.Exists(entry => entry.Name == "victim"), "Deleting the link touched its target");
                found = Transfer.SearchRemote(serial, unusualRoot, "victim", CancellationToken.None, out warning);
                Check(found.Count == 1 && found[0].FullPath == unusualRoot + "/victim" && warning.Length > 0,
                    "Search must retain ordinary matches and explicitly skip unsupported newline names, never invent paths");
                Check(!Transfer.DeletePermanent(serial, unusual.Find(entry => entry.Name == newlineName), out message), "Unsupported newline mutation must fail explicitly");
            }
            finally { Transfer.RunFileCommand(prefix + Transfer.Arg("rm -rf -- " + Transfer.ShellQ(unusualRoot)), 5000); }
            // Only this harness's randomly named synthetic directory is removed.
            Check(Transfer.DeletePermanent(serial, new RemoteEntry { Name = id, FullPath = remote, IsDir = true }, out message), "Synthetic directory cleanup failed");
            Console.WriteLine("PASS device: sdcard/root browsing, tree, search, arbitrary directory links, Unicode files/folders, conflict protection and trash roundtrip");
        }
    }
}
