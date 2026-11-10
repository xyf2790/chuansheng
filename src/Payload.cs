// Payload.cs — 单文件版的载荷解压：把内嵌的 scrcpy/adb 释放到 %LOCALAPPDATA%
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class Payload
    {
        public const string PayloadVersion = "4.1";

        private static readonly string[] Required =
        {
            "scrcpy.exe", "scrcpy-server", "scrcpy.ico", "app.ico", "adb.exe", "SDL3.dll",
            "使用手册.md"
        };

        /// <summary>开发版（exe 旁边就有 scrcpy）返回自身目录；单文件版解压到 LocalAppData 并返回该目录。</summary>
        public static string Ensure()
        {
            var localDir = AppDomain.CurrentDomain.BaseDirectory;
            if (Required.All(f => File.Exists(Path.Combine(localDir, f))))
                return localDir;

            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames();
            if (names == null || names.Length == 0)
                return localDir;

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhoneAudioTray", PayloadVersion);

            if (Required.All(f => File.Exists(Path.Combine(root, f))))
                return root;

            try
            {
                Directory.CreateDirectory(root);
                foreach (var name in names)
                {
                    if (!LooksLikePayload(name)) continue;

                    // 资源名用 ASCII，落地时还原成中文文件名
                    var destName = name.Equals("manual.md", StringComparison.OrdinalIgnoreCase)
                        ? "使用手册.md"
                        : name;
                    var dest = Path.Combine(root, destName);
                    if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;

                    using (var src = asm.GetManifestResourceStream(name))
                    {
                        if (src == null) continue;
                        using (var fs = File.Create(dest)) src.CopyTo(fs);
                    }
                }
                return root;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "首次运行解压失败：\r\n" + ex.Message + "\r\n\r\n目标目录：\r\n" + root,
                    "传声", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return localDir;
            }
        }

        private static bool LooksLikePayload(string name)
        {
            if (name.Equals("scrcpy-server", StringComparison.OrdinalIgnoreCase)) return true;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".exe" || ext == ".dll" || ext == ".ico" || ext == ".md";
        }
    }
}
