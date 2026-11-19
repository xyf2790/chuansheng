using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class FileBrowserPerformance
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "-s") return FakeAdb(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            int result = 0;
            using (var host = new Form { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-10000, -10000) })
            using (var watchdog = new System.Threading.Timer(s => { Console.WriteLine("FAIL UI watchdog: message loop did not finish within 30 seconds"); Environment.Exit(1); }, null, 30000, Timeout.Infinite))
            {
                host.Shown += async (sender, e) =>
                {
                    try { await RunTests(args); }
                    catch (Exception ex) { Console.WriteLine("FAIL " + ex); result = 1; }
                    finally { host.Close(); }
                };
                Application.Run(host);
            }
            return result;
        }

        private static async Task RunTests(string[] args)
        {
            Check(new RemoteEntry { Name = "PHOTO.JPG" }.TypeText == "图片（JPG）", "Image extensions must be case-insensitive");
            Check(new RemoteEntry { Name = "archive.tar.gz" }.TypeText == "压缩包（GZ）", "Compound archive extension");
            Check(new RemoteEntry { Name = "song.flac" }.TypeText == "音频（FLAC）", "Common audio format");
            Check(new RemoteEntry { Name = "book.pdf" }.TypeText == "PDF 文档（PDF）", "PDF type");
            Check(new RemoteEntry { Name = "unknown.custom" }.TypeText == "文件（CUSTOM）", "Unknown extension must stay visible");
            Check(new RemoteEntry { Name = "plain" }.TypeText == "文件", "Extensionless fallback");
            Check(new RemoteEntry { Name = "folder.jpg", IsDir = true }.TypeText == "文件夹", "Directory takes precedence over extension");
                typeof(Program).GetField("_adb", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, Assembly.GetExecutingAssembly().Location);
                string warning;
                var partial = Transfer.SearchRemote("synthetic", "/partial", "video", CancellationToken.None, out warning);
                Check(partial.Count == 2 && warning.Length > 0, "Partial find results must survive permission errors with a visible warning");
                var empty = Transfer.SearchRemote("synthetic", "/empty", "video", CancellationToken.None, out warning);
                Check(empty.Count == 0 && warning.Length == 0, "Empty find result must not trigger recursive fallback");
                Console.WriteLine("PASS partial permission results and empty search");
                using (var dialog = new FileBrowserDialog("synthetic"))
                {
                    var list = (ListView)Field(dialog, "list");
                    if (!list.VirtualMode) throw new Exception("Large directories must use virtual rows, not eagerly create every ListViewItem.");
                    var clock = Stopwatch.StartNew();
                    dialog.StartPosition = FormStartPosition.Manual;
                        dialog.Location = new System.Drawing.Point(-10000, -10000);
                        dialog.ShowInTaskbar = false;
                        dialog.Show();
                    Check(clock.ElapsedMilliseconds < 800, "Showing the browser blocked on delayed ADB");
                    int ticks = 0;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 25 })
                    {
                        timer.Tick += (s, e) => ticks++;
                        timer.Start();
                        await PumpUntil(() => ticks >= 8, 1000);
                        Check((bool)Field(dialog, "loading"), "Synthetic delay did not exercise the loading state");
                        clock.Restart();
                        Call(dialog, "Navigate", "/fast");
                        Check(clock.ElapsedMilliseconds < 300, "Navigation blocked UI");
                        await PumpUntil(() => list.VirtualListSize == 20001, 10000);
                        Console.WriteLine("PASS 20,000 rows shown in " + clock.ElapsedMilliseconds + " ms; UI heartbeat " + ticks);
                        await PumpFor(1700);
                        var root = ((TreeView)Field(dialog, "tree")).Nodes[0];
                        Check(root.Nodes.Count == 1 && root.Nodes[0].Text == "Download", "Background directory tree failed");
                        typeof(FileBrowserDialog).GetField("suppressTreeEvent", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dialog, true);
                        root.Nodes[0].Expand();
                        await PumpFor(100);
                        root.Nodes[0].Collapse();
                        Check(root.Nodes[0].Nodes[0].Text == "…", "Collapsed tree request did not reset for retry");
                        await PumpFor(100);
                        typeof(FileBrowserDialog).GetField("suppressTreeEvent", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dialog, false);
                        Check(((Label)Field(dialog, "lblPath")).Text.StartsWith("/fast"), "Old directory result overwrote new path");
                        Check(list.Items[1].Text == "sample-00000.mp4", "Virtual retrieval or sorting is incorrect");
                        Check(list.Items[1].SubItems[1].Text == "视频（MP4）", "Virtual rows must show the detected file type");
                        Check(list.Items[20000].Text == "sample-19999.mp4", "Last virtual row is incorrect");
                        clock.Restart();
                        list.SelectedIndices.Add(1);
                        list.SelectedIndices.Add(15000);
                        var selection = (List<RemoteEntry>)Call(dialog, "SelectedEntries");
                        Check(selection.Count == 2 && selection[1].Name == "sample-14999.mp4", "Virtual multi-select maps to wrong files");
                        Check(clock.ElapsedMilliseconds < 500, "Selecting two rows scans all entries");
                        Check(!((Button)Field(dialog, "btnPreview")).Enabled, "Multi-selection should disable preview");
                        list.SelectedIndices.Clear();
                        list.SelectedIndices.Add(1);
                        Check(((Button)Field(dialog, "btnPreview")).Enabled, "Single video selection should enable preview");
                        var search = (TextBox)Field(dialog, "txtSearch");
                        search.Text = "sample-00001";
                        await PumpUntil(() => list.VirtualListSize == 2, 3000);
                        Check(list.Items[1].Text == "sample-00001.mp4", "Local filter maps to wrong virtual row");
                        search.Text = "sample-00002";
                        search.Text = "sample-";
                        await PumpUntil(() => list.VirtualListSize == 20001, 3000);
                        Call(dialog, "List_ColumnClick", list, new ColumnClickEventArgs(0));
                        await PumpUntil(() => list.VirtualListSize == 20001 && list.Items[1].Text == "sample-19999.mp4", 3000);
                        Console.WriteLine("PASS virtual selection, filtering and sorting");
                        ((CheckBox)Field(dialog, "chkSub")).Checked = true;
                        search.Text = "special '$ &";
                        Console.WriteLine("CHECK starting delayed recursive search");
                        Call(dialog, "DoSearch");
                        int before = ticks;
                        await PumpFor(250);
                        Check(ticks > before + 3, "Search blocks UI heartbeat");
                        Console.WriteLine("CHECK navigating away from recursive search");
                        Call(dialog, "Navigate", "/fast");
                        search.Text = "";
                        await PumpUntil(() => list.VirtualListSize == 20001, 4000);
                        Console.WriteLine("CHECK waiting for stale search");
                        await PumpFor(1800);
                        Check(!(bool)Field(dialog, "subtreeResultMode"), "Canceled search overwrote directory");
                        Console.WriteLine("CHECK starting slow directory");
                        Call(dialog, "Navigate", "/slow");
                        Console.WriteLine("CHECK pumping slow directory");
                        await PumpFor(150);
                        Console.WriteLine("CHECK closing during slow directory");
                        dialog.Close();
                        Console.WriteLine("CHECK waiting for canceled child processes");
                        await PumpUntil(() => CountHarnessProcesses() == 1, 2500);
                        Console.WriteLine("PASS delayed directory/tree/search stay responsive, stale requests ignored, close kills pending ADB");
                    }
                }
                if (args.Length == 3)
                {
                    typeof(Program).GetField("_adb", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, args[0]);
                    var clock = Stopwatch.StartNew();
                    var entries = Transfer.ListRemote(args[1], args[2]);
                    Console.WriteLine("PASS real directory: " + entries.Count + " entries in " + clock.ElapsedMilliseconds + " ms (names not logged)");
                    using (var dialog = new FileBrowserDialog(args[1]))
                    {
                        typeof(FileBrowserDialog).GetField("currentPath", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dialog, args[2]);
                        dialog.StartPosition = FormStartPosition.Manual;
                        dialog.Location = new System.Drawing.Point(-10000, -10000);
                        dialog.ShowInTaskbar = false;
                        dialog.Show();
                        var list = (ListView)Field(dialog, "list");
                        clock.Restart();
                        await PumpUntil(() => list.VirtualListSize == entries.Count + (args[2] == "/" ? 0 : 1), 15000);
                        Console.WriteLine("PASS real browser virtual display in " + clock.ElapsedMilliseconds + " ms");
                        dialog.Close();
                        await PumpFor(250);
                    }
                }
        }

        private static object Field(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        private static object Call(object target, string name, params object[] args)
        {
            return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        }

        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

        private static async Task PumpUntil(Func<bool> done, int timeout)
        {
            var clock = Stopwatch.StartNew();
            while (!done())
            {
                if (clock.ElapsedMilliseconds > timeout) throw new Exception("Timed out waiting for UI state");
                await Task.Delay(5);
            }
        }

        private static Task PumpFor(int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            return PumpUntil(() => clock.ElapsedMilliseconds >= milliseconds, milliseconds + 1000);
        }

        private static int CountHarnessProcesses()
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location));
            int count = processes.Length;
            foreach (var process in processes) process.Dispose();
            return count;
        }

        private static int FakeAdb(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            // The full Android shell command must arrive as one Windows argument.
            if (args.Length != 4 || args[1] != "synthetic" || args[2] != "shell") return 2;
            var command = args[3];
            // Fixtures use canonical names; production must pass directory operands with /.
            command = command.Replace("/'", "'");
            if (command.StartsWith("find "))
            {
                if (command.Contains("'/partial'"))
                {
                    Console.Write("/partial/video.mp4\0");
                    Console.Error.WriteLine("find: /partial/private: Permission denied");
                    return 1;
                }
                if (command.Contains("'/empty'")) return 0;
                Thread.Sleep(1500);
                Console.Write("/fake-search-result\0");
                return 0;
            }
            if (command.Contains("'/slow'")) Thread.Sleep(5000);
            if (command.Contains("'/sdcard/Download'")) Thread.Sleep(1500);
            if (command.Contains("'/sdcard'"))
            {
                Thread.Sleep(900);
                Console.WriteLine("drwxrwxr-x 2 0 0 4096 2026-11-17 12:00 Download");
                return 0;
            }
            for (int i = 19999; i >= 0; i--)
                Console.WriteLine("-rw-rw-r-- 1 0 0 1048576 2026-11-17 12:00 sample-" + i.ToString("D5") + ".mp4");
            return 0;
        }
    }
}
