using System;
using System.Reflection;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class UsabilityPreview
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Program.BaseDir = args[0];
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args[2] == "process")
                using (var dialog = new ProcessTreeDialog(args[1])) dialog.ShowDialog();
            else
                using (var dialog = new FileBrowserDialog(args[1]))
                {
                    typeof(FileBrowserDialog).GetField("currentPath", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(dialog, args[3]);
                    dialog.ShowDialog();
                }
        }
    }
}
