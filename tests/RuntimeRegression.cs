using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class RuntimeRegression
    {
        private static int checks;
        private static bool Start(CommandRequest request) { return (bool)typeof(CommandRequest).GetMethod("TryStart").Invoke(request, null); }
        private static void Check(bool pass, string message) { if (!pass) throw new Exception("FAIL: " + message); checks++; }
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0) {
                if (Path.GetFileName(Assembly.GetExecutingAssembly().Location) == "scrcpy.exe") {
                    if (Environment.GetEnvironmentVariable("PAT_RUNTIME_SCRCPY_DELAY") == "1") Thread.Sleep(5000);
                    return 0;
                }
                if (args[0] == "devices") { Console.WriteLine("List of devices attached\nsynthetic\tdevice"); return 0; }
                if (args[0] == "-s") {
                    var state = Environment.GetEnvironmentVariable("PAT_RUNTIME_STATE");
                    var mode = Environment.GetEnvironmentVariable("PAT_RUNTIME_VOLUME_MODE");
                    if (args.Contains("volume")) {
                        if (mode == "unreadable") { Console.Error.WriteLine("synthetic denied"); return 1; }
                        Console.WriteLine("volume is " + (File.Exists(state) ? File.ReadAllText(state) : "3") + " range [0..15]");
                    }
                    if (args.Contains("keyevent")) {
                        File.WriteAllText(state + ".called", "called");
                        if (mode == "success") {
                            int current = File.Exists(state) ? int.Parse(File.ReadAllText(state)) : 3;
                            foreach (var key in args.SkipWhile(x => x != "keyevent").Skip(1)) current += key == "24" ? 1 : -1;
                            File.WriteAllText(state, current.ToString());
                        }
                    }
                    return 0;
                }
                if (args[0] == "sleep") { Thread.Sleep(1500); return 0; }
                if (args[0] == "flood") { Console.Error.Write(new string('e', 200000)); Console.Write("payload"); return 0; }
                if (args[0] == "fail") { Console.Write("partial"); Console.Error.Write("synthetic failure"); return 3; }
                return 0; // Synthetic scrcpy exits immediately.
            }
            try {
                var self = Assembly.GetExecutingAssembly().Location;
                typeof(Program).GetField("_adb", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, self);
                var clock = Stopwatch.StartNew();
                var output = Program.RunAdb("sleep", 120);
                Check(clock.ElapsedMilliseconds < 1000 && output.StartsWith("ERROR:"), "text timeout must interrupt output reads");
                clock.Restart();
                Check(Program.RunAdbBinary("sleep", 120) == null && clock.ElapsedMilliseconds < 1000, "binary timeout must interrupt output reads");
                Check(Program.RunAdb("flood", 2000) == "payload", "text stdout and stderr must drain concurrently");
                Check(Encoding.UTF8.GetString(Program.RunAdbBinary("flood", 2000)) == "payload", "binary stdout and stderr must drain concurrently");
                Check(Program.RunAdbBinary("fail", 2000) == null, "binary nonzero exit must not return partial success");
                Console.WriteLine("PASS bounded text/binary subprocess reads and dual-pipe flood");

                var build = typeof(Cli).GetMethod("BuildCommand", BindingFlags.Static | BindingFlags.NonPublic);
                var command = (string)build.Invoke(null, new object[] { new[] { "--push", ".\\relative.jpg" } });
                Check(command == "push " + Path.GetFullPath(".\\relative.jpg"), "relative push path must resolve in CLI process");
                Console.WriteLine("PASS CLI relative push path");

                var wait = typeof(Ipc).GetMethod("WaitForCommand", BindingFlags.Static | BindingFlags.NonPublic);
                Check(wait != null, "request wait helper must cancel only queued commands");
                var queued = new CommandRequest { Line = "exit" };
                var response = (string)wait.Invoke(null, new object[] { queued, 40 });
                Check(response.Contains("未执行") && !Start(queued), "expired queued command must never execute later");
                var started = new CommandRequest { Line = "push x" };
                Check(Start(started), "queued command may start once");
                Task.Run(() => { Thread.Sleep(160); started.Result = "OK complete"; started.Done.Set(); });
                clock.Restart();
                response = (string)wait.Invoke(null, new object[] { started, 40 });
                Check(response == "OK complete" && clock.ElapsedMilliseconds >= 100, "started operation must keep its actual result after queue deadline");
                Ipc.PipeName = "PhoneAudioTray.Regression." + Guid.NewGuid().ToString("N");
                Ipc.StartServer();
                Check(Ipc.Send("ping", 1200) == "OK", "ping must respond without a UI message pump");
                var pending = Task.Run(() => Ipc.Send("status"));
                clock.Restart();
                while (Program.PendingCommands.IsEmpty && clock.ElapsedMilliseconds < 1500) Thread.Sleep(10);
                Check(Ipc.Send("ping", 1200) == "OK", "another active request must not block ping");
                CommandRequest request;
                Check(Program.PendingCommands.TryDequeue(out request), "server must queue operation");
                Check(Start(request), "server request can start");
                request.Result = "OK synthetic"; request.Done.Set();
                Check(pending.Wait(2000) && pending.Result == "OK synthetic", "IPC returns complete operation result");
                var pipe = Ipc.PipeName;
                Ipc.PipeName = pipe + ".deadline";
                using (var delayedServer = new NamedPipeServerStream(Ipc.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous)) {
                    var delayed = Task.Run(() => {
                        delayedServer.WaitForConnection();
                        using (var reader = new StreamReader(delayedServer, Encoding.UTF8, false, 1024, true)) reader.ReadLine();
                        Thread.Sleep(300);
                    });
                    clock.Restart();
                    Check(Ipc.Send("ping", 80).StartsWith("ERR") && clock.ElapsedMilliseconds < 500, "explicit response deadline includes output reading");
                    delayed.Wait(2000);
                }
                Ipc.PipeName = pipe;
                int actions = Program.UiActions.Count;
                Check(Commands.Execute("files").Contains("已请求") && Program.UiActions.Count == actions + 1, "CLI window command must acknowledge dispatch without waiting for dialog close");
                Console.WriteLine("PASS IPC queued cancellation, long operation result and independent ping");

                var temp = Path.Combine(Path.GetTempPath(), "pat-runtime-test-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try {
                    File.Copy(self, Path.Combine(temp, "scrcpy.exe"));
                    Program.BaseDir = temp;
                    Program.StatusItem = new ToolStripMenuItem();
                    Program.Tray = new NotifyIcon();
                    Program.MonitorMic = false;
                    for (int i = 0; i < 12; i++) {
                        Program.StartSession("synthetic", "test", false);
                        Thread.Sleep(70);
                        Program.StartMicSession("synthetic");
                        Thread.Sleep(70);
                    }
                    clock.Restart();
                    while (clock.ElapsedMilliseconds < 2500) {
                        lock (Program.Sessions) if (Program.Sessions.Count == 0) break;
                        Thread.Sleep(20);
                    }
                    lock (Program.Sessions) Check(Program.Sessions.Count == 0, "immediately exiting sessions must not leave connected entries");
                    File.WriteAllText(Path.Combine(temp, "settings.txt"), "pushTarget|/sdcard/a|b\n");
                    typeof(Program).GetField("settingsLoaded", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
                    Program.EnsureSettings();
                    Check(Program.PushTarget == "/sdcard/a|b", "setting separator belongs to value after first pipe");
                    File.WriteAllText(Path.Combine(temp, "devices.txt"), "synthetic|phone|alias\n");
                    typeof(Program).GetField("cacheLoaded", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
                    Check(Program.QueryDeviceName("synthetic") == "phone|alias", "device alias preserves pipe");
                    var pc = Program.PcVolume;
                    var notifications = Toast.Enabled;
                    Toast.Enabled = false;
                    Check(Commands.Execute("volume-pc -1").StartsWith("ERR") && Program.PcVolume == pc, "invalid PC volume must not change volume");
                    Check(Commands.Execute("volume-phone 101").StartsWith("ERR"), "invalid phone volume must be rejected");
                    Check(Commands.Execute("notify typo").StartsWith("ERR") && !Toast.Enabled, "invalid switch token must not toggle state");
                    Check(Commands.Execute("volume-phone 40").StartsWith("ERR"), "disconnected phone volume must fail without dialog");
                    File.Move(Path.Combine(temp, "scrcpy.exe"), Path.Combine(temp, "scrcpy-disabled.exe"));
                    try { Check(Commands.Execute("connect-usb").StartsWith("ERR"), "missing scrcpy must fail CLI connection without a modal dialog"); }
                    finally { File.Move(Path.Combine(temp, "scrcpy-disabled.exe"), Path.Combine(temp, "scrcpy.exe")); }
                    Program.PcVolume = .8f;
                    using (var volume = new VolumeDialog(.8f, 40, false)) {
                        ((TrackBar)typeof(VolumeDialog).GetField("pcBar", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(volume)).Value = 20;
                        volume.DialogResult = DialogResult.Cancel;
                        typeof(VolumeDialog).GetMethod("OnFormClosed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(volume, new object[] { new FormClosedEventArgs(CloseReason.UserClosing) });
                    }
                    Check(Math.Abs(Program.PcVolume - .8f) < .001, "cancel restores previewed PC volume");
                    using (var volume = new VolumeDialog(.8f, 40, true)) {
                        volume.DialogResult = DialogResult.OK;
                        typeof(VolumeDialog).GetMethod("OnFormClosed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(volume, new object[] { new FormClosedEventArgs(CloseReason.UserClosing) });
                        Check(volume.PhoneValue == -1, "unchanged phone slider must not overwrite external volume changes");
                    }
                    var state = Path.Combine(temp, "volume-state");
                    Environment.SetEnvironmentVariable("PAT_RUNTIME_STATE", state);
                    Environment.SetEnvironmentVariable("PAT_RUNTIME_VOLUME_MODE", "unreadable");
                    Program.Sessions.Add(new Session { Serial = "synthetic" });
                    Check(!Program.SetPhoneVolumePct(60) && !File.Exists(state + ".called"), "unreadable baseline must not send volume-up events");
                    Environment.SetEnvironmentVariable("PAT_RUNTIME_VOLUME_MODE", "fixed");
                    Check(!Program.SetPhoneVolumePct(60), "phone refusing target must not report success");
                    Environment.SetEnvironmentVariable("PAT_RUNTIME_VOLUME_MODE", "success");
                    Check(Program.SetPhoneVolumePct(60) && Program.PhoneVolumePct == 60, "phone volume succeeds only after verified target");
                    Program.Sessions.Clear();
                    Program.Sessions.Add(new Session { Serial = "current" });
                    Program.Sessions.Add(new Session { Serial = "other", IsMic = true });
                    Check(Program.PrimarySerial == "current", "mic side-channel must not redirect primary device operations");
                    Program.Sessions.Clear();
                    Program.Sessions.Add(new Session { Serial = "current" });
                    Program.Sessions.Add(new Session { Serial = "current", IsMic = true });
                    Program.StopLatest();
                    Check(Program.Sessions.Count == 0, "stop current must stop its main session and companion mic");
                    Program.Sessions.Add(new Session { Serial = "synthetic", IsMic = true });
                    Program.ShowVideo = true;
                    typeof(Program).GetMethod("HandleSessionExit", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { "synthetic", "test", true, false });
                    Check(!Program.ShowVideo, "companion mic must not prevent video-to-audio fallback");
                    Program.StopAllSessions(false);
                    Program.AllowMultiple = true;
                    Environment.SetEnvironmentVariable("PAT_RUNTIME_SCRCPY_DELAY", "1");
                    Program.StartSession("first", "test", false);
                    Program.StartSession("second", "test", false);
                    Program.SetClipboardSync(!Program.ClipboardSync);
                    lock (Program.Sessions) Check(Program.Sessions.Count(x => !x.IsMic) == 2, "mode restart preserves all multiple-device sessions");
                    Program.StopAllSessions(false);
                    Environment.SetEnvironmentVariable("PAT_RUNTIME_SCRCPY_DELAY", null);
                    Environment.SetEnvironmentVariable("PAT_RUNTIME_STATE", null);
                    Environment.SetEnvironmentVariable("PAT_RUNTIME_VOLUME_MODE", null);
                    Program.PcVolume = pc;
                    Toast.Enabled = notifications;
                    Console.WriteLine("PASS immediate session exits and settings/alias roundtrip");
                }
                finally {
                    Program.StopAllSessions(false);
                    Program.Tray.Dispose();
                    Directory.Delete(temp, true);
                }
                Console.WriteLine("PASS " + checks + " runtime checks");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
