using System;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PhoneAudioTray
{
    internal static class DesktopRegression
    {
        [STAThread]
        public static int Main()
        {
            var payloadCheck = typeof(Payload).GetMethod("IsComplete", BindingFlags.Static | BindingFlags.NonPublic);
            var temporary = System.IO.Path.GetTempFileName();
            try {
                if ((bool)payloadCheck.Invoke(null, new object[] { temporary })) throw new Exception("Empty runtime file accepted");
                System.IO.File.WriteAllText(temporary, "synthetic runtime");
                if (!(bool)payloadCheck.Invoke(null, new object[] { temporary })) throw new Exception("Nonempty runtime file rejected");
                using (var source = new System.IO.MemoryStream(new byte[] { 1, 2, 3, 4 })) Payload.ExtractResource(source, temporary);
                if (System.IO.File.ReadAllBytes(temporary).Length != 4) throw new Exception("Partial resource was not repaired");
                try { using (var source = new BrokenResource()) Payload.ExtractResource(source, temporary); throw new Exception("Broken extraction reported success"); }
                catch (System.IO.IOException) { }
                if (System.IO.File.ReadAllBytes(temporary).Length != 4) throw new Exception("Failed extraction replaced the valid resource");
                var concurrent = temporary + ".concurrent";
                try {
                    System.Threading.Tasks.Parallel.For(0, 16, i => {
                        using (var source = new System.IO.MemoryStream(new byte[] { 1, 2, 3, 4 })) Payload.ExtractResource(source, concurrent);
                    });
                    if (System.IO.File.ReadAllBytes(concurrent).Length != 4) throw new Exception("Concurrent extraction published partial content");
                } finally { System.IO.File.Delete(concurrent); }
            } finally { System.IO.File.Delete(temporary); }
            Console.WriteLine("PASS runtime completeness rejects zero-byte files");
            const string path = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string name = "PhoneAudioTray";
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                var original = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var kind = original == null ? RegistryValueKind.String : key.GetValueKind(name);
                try
                {
                    Shortcut.SetStartup(true);
                    if (!Shortcut.StartupEnabled || (string)key.GetValue(name) != "\"" + Application.ExecutablePath + "\"")
                        throw new Exception("Startup path mismatch");
                    Shortcut.SetStartup(false);
                    if (Shortcut.StartupEnabled) throw new Exception("Startup disable failed");
                    Console.WriteLine("PASS startup enable/disable, quoted executable path");
                }
                finally
                {
                    if (original == null) key.DeleteValue(name, false);
                    else key.SetValue(name, original, kind);
                }
            }
            Application.EnableVisualStyles();
            typeof(Program).GetMethod("BuildTray", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            try
            {
                foreach (ToolStripItem item in Program.Tray.ContextMenuStrip.Items)
                    if (item.Text == "停止当前" || item.Text == "停止全部" || item.Text == "使用说明…" || item.Text == "打开使用手册")
                        throw new Exception("Removed entry still appears in tray menu: " + item.Text);
                typeof(NotifyIcon).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(Program.Tray, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0) });
                Application.DoEvents();
                if (!Program.Tray.ContextMenuStrip.Visible) throw new Exception("Left click did not open menu");
                Console.WriteLine("PASS left mouse release opens tray menu");
                Program.Tray.ContextMenuStrip.Close();
            }
            finally { Program.Tray.Dispose(); }
            return 0;
        }

        private sealed class BrokenResource : System.IO.MemoryStream
        {
            private bool read;
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (read) throw new System.IO.IOException("Synthetic interrupted extraction");
                read = true;
                buffer[offset] = 9;
                return 1;
            }
        }
    }
}
