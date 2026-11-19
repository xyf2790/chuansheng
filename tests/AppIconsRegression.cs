using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace PhoneAudioTray
{
    internal static class AppIconsRegression
    {
        public static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "Transmit-icons-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Program.BaseDir = root;
            try { return Run(root); }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int Run(string root)
        {
            if (!AppIcons.IconDir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Fixture must stay inside its unique temp directory");
            var key = "test.synthetic." + Guid.NewGuid().ToString("N");
            var file = Path.Combine(AppIcons.IconDir, key + ".png");
            Directory.CreateDirectory(AppIcons.IconDir);
            using (var bitmap = new Bitmap(4, 4)) bitmap.Save(file, ImageFormat.Png);
            var requests = Enumerable.Range(0, 20).Select(i => Task.Run(() => AppIcons.Get(key))).ToArray();
            Task.WaitAll(requests);
            var image = requests[0].Result;
            if (image == null || requests.Any(t => !object.ReferenceEquals(image, t.Result))) throw new Exception("Concurrent reads must reuse one owned image");
            using (var unlocked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(file);
            if (image.Width != 4) throw new Exception("Deleting cache file must not invalidate loaded image");
            int hash = 0;
            foreach (char c in "polygenelubricants") hash = unchecked(hash * 31 + c);
            if (hash != int.MinValue) throw new Exception("Wrong boundary fixture");
            using (var avatar = AppIcons.MakeAvatar("polygenelubricants"))
                if (avatar.Width != 32) throw new Exception("Minimum hash avatar failed");
            if (AppIcons.Get(null) != null) throw new Exception("Null package lookup must be empty");
            var cache = (Dictionary<string, Image>)typeof(AppIcons).GetField("Cache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            cache.Remove(key);
            image.Dispose();
            Console.WriteLine("PASS: 5 icon checks (concurrent ownership, file unlock, detached image, min-value hash, null lookup)");
            return 0;
        }
    }
}
