using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneAudioTray
{
    internal static class MediaPreviewRegression
    {
        private static int checks;
        private static void Check(bool pass, string message) { if (!pass) throw new Exception("FAIL: " + message); checks++; }
        private static int FakeAdb(string[] args)
        {
            var root = Environment.GetEnvironmentVariable("TRANSMIT_PREVIEW_TEST_ROOT");
            var mode = Environment.GetEnvironmentVariable("TRANSMIT_PREVIEW_TEST_MODE") ?? "";
            if (args.Contains("shell")) {
                var command = args[args.Length - 1];
                if (command == "exit-seven") return 7;
                if (command == "stderr-only") { Console.Error.WriteLine("diagnostic warning"); return 0; }
                if (command == "slow") { File.WriteAllText(Path.Combine(root, "child.pid"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); Thread.Sleep(30000); return 0; }
                if (command.StartsWith("stat ")) {
                    Console.WriteLine("6|" + (mode == "changed" && File.Exists(Path.Combine(root, "pulled")) ? "1001" : "1000"));
                    return 0;
                }
                return 8;
            }
            if (args.Contains("pull")) {
                File.WriteAllText(Path.Combine(root, "started"), "1");
                if (mode == "delay") Thread.Sleep(400);
                var target = args[args.Length - 1];
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (mode == "directory") {
                    Directory.CreateDirectory(target);
                    File.WriteAllText(Path.Combine(target, "synthetic-child.txt"), "unexpected directory payload");
                    return 0;
                }
                File.WriteAllText(target, "ABCDEF");
                File.WriteAllText(Path.Combine(root, "pulled"), "1");
                return 0;
            }
            return 9;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "-s") return FakeAdb(args);
            var root = Path.Combine(Path.GetTempPath(), "Transmit-preview-test-" + Guid.NewGuid().ToString("N"));
            var adb = typeof(Program).GetField("_adb", BindingFlags.NonPublic | BindingFlags.Static);
            var originalAdb = adb.GetValue(null);
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("TRANSMIT_PREVIEW_TEST_ROOT", root);
            adb.SetValue(null, Assembly.GetExecutingAssembly().Location);
            var entry = new RemoteEntry { Name = "synthetic.png", FullPath = "/synthetic.png" };
            string retained = null;
            try {
                var expected = MediaPreview.CachePath("fake", entry.FullPath, "6|1000");
                Directory.CreateDirectory(Path.GetDirectoryName(expected));
                File.WriteAllText(expected, "bad");
                var downloaded = MediaPreview.Download("fake", entry, CancellationToken.None);
                Check(downloaded == expected && File.ReadAllText(downloaded) == "ABCDEF", "corrupt cache replaced");
                Check(MediaPreview.Download("fake", entry, CancellationToken.None) == downloaded, "valid cache reused");
                MediaPreview.Cleanup();
                File.Delete(Path.Combine(root, "pulled"));
                Environment.SetEnvironmentVariable("TRANSMIT_PREVIEW_TEST_MODE", "changed");
                bool rejected = false;
                try { MediaPreview.Download("fake", entry, CancellationToken.None); } catch (IOException) { rejected = true; }
                Check(rejected && !File.Exists(expected), "changed source never published");
                Environment.SetEnvironmentVariable("TRANSMIT_PREVIEW_TEST_MODE", "directory");
                rejected = false;
                try { MediaPreview.Download("fake", entry, CancellationToken.None); } catch (IOException) { rejected = true; }
                Check(rejected && Directory.GetDirectories(Path.GetDirectoryName(expected), "*.part").Length == 0, "file replaced by directory leaves no partial directory");
                var abandoned = expected + "." + Guid.NewGuid().ToString("N") + ".part";
                Directory.CreateDirectory(abandoned);
                File.WriteAllText(Path.Combine(abandoned, "synthetic-child.txt"), "cleanup fixture");
                MediaPreview.Cleanup();
                Check(!Directory.Exists(abandoned), "cleanup removes abandoned partial directories");
                Environment.SetEnvironmentVariable("TRANSMIT_PREVIEW_TEST_MODE", "delay");
                File.Delete(Path.Combine(root, "started"));
                var pending = Task.Run(() => {
                    try { MediaPreview.Download("fake", entry, CancellationToken.None); return false; }
                    catch (OperationCanceledException) { return true; }
                });
                Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(root, "started")), 3000), "controlled download started");
                MediaPreview.Cleanup();
                Check(pending.Wait(3000) && pending.Result && !File.Exists(expected), "cleanup prevents stale cache publication");
                Check(!Directory.Exists(Path.GetDirectoryName(expected)) || Directory.GetFiles(Path.GetDirectoryName(expected), "*.part").Length == 0, "partial files removed after cleanup race");
                Environment.SetEnvironmentVariable("TRANSMIT_PREVIEW_TEST_MODE", "");
                var first = Task.Run(() => MediaPreview.Download("fake", entry, CancellationToken.None));
                var second = Task.Run(() => MediaPreview.Download("fake", entry, CancellationToken.None));
                Task.WaitAll(first, second);
                Check(first.Result == second.Result && File.ReadAllText(first.Result) == "ABCDEF", "concurrent downloads converge on verified cache");
                using (var cancellation = new CancellationTokenSource()) {
                    cancellation.Cancel();
                    rejected = false;
                    try { MediaPreview.Download("fake", entry, cancellation.Token); } catch (OperationCanceledException) { rejected = true; }
                    Check(rejected, "cancelled cache request rejected");
                }
                retained = first.Result;
                MediaPreview.KeepForExternalApp(retained);
                var disposable = Path.Combine(Path.GetDirectoryName(retained), "unused.tmp");
                File.WriteAllText(disposable, "x");
                MediaPreview.Cleanup();
                Check(File.Exists(retained) && !File.Exists(disposable), "external playback retains its file while other cache is cleaned");
                using (var dialog = new ProcessTreeDialog("fake serial")) {
                    var run = typeof(ProcessTreeDialog).GetMethod("RunAdb", BindingFlags.Instance | BindingFlags.NonPublic);
                    rejected = false;
                    try { run.Invoke(dialog, new object[] { "exit-seven" }); }
                    catch (TargetInvocationException ex) { rejected = ex.InnerException is IOException; }
                    Check(rejected, "task manager rejects silent nonzero adb exit");
                    Check((string)run.Invoke(dialog, new object[] { "stderr-only" }) == "", "stderr cannot contaminate process parser");
                    var reading = Task.Run(() => {
                        try { run.Invoke(dialog, new object[] { "slow" }); return false; }
                        catch (TargetInvocationException ex) { return ex.InnerException is OperationCanceledException; }
                    });
                    Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(root, "child.pid")), 3000), "controlled adb process started");
                    int childPid = int.Parse(File.ReadAllText(Path.Combine(root, "child.pid")));
                    dialog.Dispose();
                    Check(reading.Wait(3000) && reading.Result, "closing cancels task manager command");
                    bool running;
                    try { using (var child = System.Diagnostics.Process.GetProcessById(childPid)) running = !child.HasExited; }
                    catch (ArgumentException) { running = false; }
                    Check(!running, "cancelled adb child has exited");
                }
                Console.WriteLine("PASS: " + checks + " controlled media/command regression checks; no phone writes or real process stops");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally {
                adb.SetValue(null, originalAdb);
                if (retained != null) { try { File.Delete(retained); } catch { } }
                MediaPreview.Cleanup();
                Environment.SetEnvironmentVariable("TRANSMIT_PREVIEW_TEST_ROOT", null);
                Environment.SetEnvironmentVariable("TRANSMIT_PREVIEW_TEST_MODE", null);
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
