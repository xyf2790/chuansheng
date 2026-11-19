using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class TaskManagerRegression
    {
        private static int checks;
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            checks++;
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try {
                var procs = ProcessTreeDialog.ParsePs("PID PPID USER RSS STIME NAME\n11 1 u0_a12 2048 08:54:39 com.tencent.mm:push\n12 1 system 300 08:54:39 system_server\n13 1 u10_a14 10000 08:54:39 com.example.work\n14 1 root 0 08:54:39 [worker with spaces]\nerror: device offline");
                Check(procs.Count == 4, "modern ps rows");
                Check(procs[0].Package == "com.tencent.mm" && procs[0].DisplayName == "微信", "service package and Chinese label");
                Check(procs[0].CanStop && !procs[1].CanStop, "only app packages may be stopped");
                Check(procs[2].IsApp, "work-profile app UID");
                Check(procs[3].Name == "[worker with spaces]", "multiword process name");
                var alternate = ProcessTreeDialog.ParsePs("PID PPID USER NAME RSS\n42 1 root worker with spaces 1024");
                Check(alternate.Count == 1 && alternate[0].Name == "worker with spaces" && alternate[0].RssKb == 1024, "trailing RSS parser");
                var legacy = ProcessTreeDialog.ParsePs("USER PID PPID VSZ RSS WCHAN ADDR S NAME\nu0_a12 44 1 99999 1500 x 0 S com.example.old");
                Check(legacy.Count == 1 && legacy[0].RssKb == 1500, "legacy RSS header");
                var noRss = ProcessTreeDialog.ParsePs("USER PID PPID VSIZE WCHAN PC NAME\nu0_a12 45 1 99999 x 0 com.example.old");
                Check(noRss.Count == 1 && noRss[0].RssKb == -1, "missing RSS is unknown");
                Check(ProcessTreeDialog.ParsePs("error: device offline").Count == 0, "adb errors are not process rows");
                ProcessTreeDialog.ApplyCpu(procs, "PID %CPU[NAME]\n11 99.0 com.tencent.mm:push\n12 88 system_server\nPID %CPU[NAME]\n11 12.5 com.tencent.mm:push\n13 135.4 com.example.work\n14 NaN [worker with spaces]");
                Check(procs[0].Cpu == 12.5 && procs[2].Cpu == 135.4, "second CPU frame and >100 percent");
                Check(!procs[1].Cpu.HasValue && !procs[3].Cpu.HasValue, "departed and invalid CPU stay unknown");
                Check(ProcessTreeDialog.VisibleProcs(procs, "", true, 4, true)[0].Pid == 13, "memory numeric descending");
                Check(ProcessTreeDialog.VisibleProcs(procs, "", false, 3, false).Last().Cpu == null, "unknown CPU remains last ascending");
                Check(ProcessTreeDialog.VisibleProcs(procs, "微信", true, 4, true).Count == 1, "Chinese name search");
                Check(ProcessTreeDialog.VisibleProcs(procs, "TENCENT", true, 4, true).Count == 1, "case insensitive package search");
                Check(ProcessTreeDialog.VisibleProcs(procs, "system", true, 4, true).Count == 0, "apps-only filter");
                Check(ProcessTreeDialog.VisibleProcs(procs, "system", false, 4, true).Count == 1, "system process visibility");
                var replacement = new ProcInfo { Pid = 11, User = "u0_a12", Name = "com.tencent.mm:push", Started = "09:00:00" };
                Check(!ProcessTreeDialog.SameProcess(procs[0], replacement), "reused PID with different start time rejected");
                replacement.Started = procs[0].Started;
                replacement.Name = "com.example.other";
                Check(!ProcessTreeDialog.SameProcess(procs[0], replacement), "reused PID with different identity rejected");
                Check(!new ProcInfo { User = "u0_a12", Name = "com.example;bad" }.CanStop, "package injection rejected");
                ProcessTreeDialog.ApplyCpu(procs, "PID %CPU NAME\n11 90 com.tencent.mm:push");
                Check(procs.All(p => !p.Cpu.HasValue), "one frame is not an interval sample");
                ProcessTreeDialog.ApplyCpu(procs, "PID %CPU NAME\n11 90 com.tencent.mm:push\nPID %CPU NAME\n11 10 com.example.other");
                Check(!procs[0].Cpu.HasValue, "CPU identity mismatch rejected");

                Check(ProcessTreeDialog.PackageMatchesUser(procs[0], "package:com.tencent.mm uid:10012"), "exact package UID matches app process");
                Check(!ProcessTreeDialog.PackageMatchesUser(procs[0], "package:com.tencent.mm uid:10013"), "custom process name cannot stop another UID's package");
                Check(!ProcessTreeDialog.PackageMatchesUser(procs[0], "package:com.tencent.mm.other uid:10012"), "package prefix is not exact ownership");
                Check(ProcessTreeDialog.PackageMatchesUser(procs[2], "package:com.example.work uid:1010014"), "work-profile package UID");
                Check(!ProcessTreeDialog.PackageMatchesUser(procs[2], "package:com.example.work uid:10014"), "different profile rejected");
                Check(!new ProcInfo { User = "u0_i12", Name = "com.example.isolated" }.CanStop, "isolated UID cannot establish package ownership");
                ProcessTreeDialog.ApplyCpu(procs, "PID [%CPU] NAME\n11 99 com.tencent.mm:push\nPID [%CPU] NAME\n11 7.5 com.tencent.mm:push");
                Check(procs[0].Cpu == 7.5, "CPU-selected toybox header parsed");
                ProcessTreeDialog.ApplyCpu(procs, "[PID] %CPU NAME\n11 99 com.tencent.mm:push\n[PID] %CPU NAME\n11 6.5 com.tencent.mm:push");
                Check(procs[0].Cpu == 6.5, "PID-selected toybox header parsed");
                var namedHeaders = ProcessTreeDialog.ParsePs("PID PPID USER NAME RSS\n55 1 root PID USER 123\n56 1 root later_process 456");
                Check(namedHeaders.Count == 2 && namedHeaders[0].Name == "PID USER" && namedHeaders[1].Pid == 56, "process names cannot replace the ps header");
                if (args.Length == 2) {
                    Program.BaseDir = args[0];
                    var read = typeof(ProcessTreeDialog).GetMethod("ReadSnapshot", BindingFlags.NonPublic | BindingFlags.Instance);
                    using (var dialog = new ProcessTreeDialog(args[1])) {
                        var live = (List<ProcInfo>)read.Invoke(dialog, new object[] { true });
                        Check(live.Count > 0, "live phone process snapshot");
                        Check(live.Any(p => p.Cpu.HasValue), "live phone second-frame CPU");
                        Check(live.Any(p => p.IsApp && p.RssKb > 0), "live app RSS");
                        Console.WriteLine("LIVE read-only snapshot: " + live.Count + " processes; " + live.Count(p => p.Cpu.HasValue) + " CPU readings");
                    }
                    var cancelledDialog = new ProcessTreeDialog(args[1]);
                    var pending = Task.Run(() => {
                        try { read.Invoke(cancelledDialog, new object[] { true }); return false; }
                        catch (TargetInvocationException ex) { return ex.InnerException is OperationCanceledException; }
                    });
                    System.Threading.Thread.Sleep(100);
                    cancelledDialog.Dispose();
                    Check(pending.Wait(3000) && pending.Result, "closing cancels in-flight adb read");
                }
                Console.WriteLine("PASS: " + checks + " task manager checks");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
