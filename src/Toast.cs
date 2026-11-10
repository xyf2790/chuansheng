// Toast.cs — Windows 10/11 原生通知（通过 PowerShell 5.1 调 WinRT）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PhoneAudioTray
{
    internal static class Toast
    {
        public const string AppId = "PhoneAudioTray.App";
        public static bool Enabled = true;      // --notify off 可关掉全部通知

        private static readonly object Gate = new object();
        private static DateTime _last = DateTime.MinValue;
        private static bool _appIdReady;

        /// <summary>
        /// 在 HKCU 里登记 AppUserModelID，让通知显示成「传声」而不是「Windows PowerShell」。
        /// 只写 HKCU\Software\Classes\AppUserModelId\PhoneAudioTray.App，删掉这个键即可完全撤销。
        /// </summary>
        public static void EnsureAppId()
        {
            if (_appIdReady) return;
            _appIdReady = true;

            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\AppUserModelId\" + AppId))
                {
                    if (key != null)
                    {
                        key.SetValue("DisplayName", "传声");
                        var icon0 = Path.Combine(Program.BaseDir, "app.ico");
                        if (File.Exists(icon0)) key.SetValue("IconUri", icon0);
                    }
                }
            }
            catch { }

            // Windows 要求桌面程序至少有一条带 AppUserModelID 的开始菜单快捷方式，
            // 否则 ToastNotification.Show() 返回成功但什么都不会显示。
            try
            {
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                var icon = Path.Combine(Program.BaseDir, "app.ico");
                Shortcut.Ensure(exe, icon, AppId, "传声 · 手机声音投到电脑");
            }
            catch { }
        }

        /// <summary>弹一条 Windows 通知；失败静默（不打断用户操作）</summary>
        public static void Show(string title, string message)
        {
            if (!Enabled) return;
            try
            {
                lock (Gate)
                {
                    // 0.6 秒内只发一条，避免批量操作时刷屏
                    if ((DateTime.Now - _last).TotalMilliseconds < 600) return;
                    _last = DateTime.Now;
                }

                EnsureAppId();

                var script = BuildScript(title, message);
                var tmp = Path.Combine(Path.GetTempPath(), "pat-toast-" + Guid.NewGuid().ToString("N") + ".ps1");
                File.WriteAllText(tmp, script, new UTF8Encoding(true));

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + tmp + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                try { psi.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE"); } catch { }

                using (var p = Process.Start(psi))
                {
                    try { p.WaitForExit(8000); } catch { }
                }

                try { File.Delete(tmp); } catch { }
            }
            catch { }
        }

        private static string BuildScript(string title, string message)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null");
            sb.AppendLine("[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null");
            sb.AppendLine("$xml = New-Object Windows.Data.Xml.Dom.XmlDocument");
            sb.AppendLine("$xml.LoadXml(" + PsString(
                "<toast><visual><binding template=\"ToastGeneric\"><text>" + Xml(title) + "</text><text>"
                + Xml(message) + "</text></binding></visual></toast>") + ")");
            sb.AppendLine("$toast = New-Object Windows.UI.Notifications.ToastNotification $xml");
            sb.AppendLine("[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier(" + PsString(AppId) + ").Show($toast)");
            return sb.ToString();
        }

        private static string Xml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        /// <summary>PowerShell 单引号字符串（单引号加倍转义）</summary>
        private static string PsString(string s)
        {
            if (s == null) s = "";
            return "'" + s.Replace("'", "''") + "'";
        }
    }
}
