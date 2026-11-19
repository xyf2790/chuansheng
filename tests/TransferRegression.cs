using System;
using System.IO;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class TransferRegression
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Program.BaseDir = args[0];
                var serial = args[1];
                var root = args[2];
                var remote = args[3];
                Directory.CreateDirectory(root);
                if (args.Length > 4)
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    if (args[4] == "browser")
                        using (var browser = new FileBrowserDialog(serial))
                        {
                            typeof(FileBrowserDialog).GetField("currentPath", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(browser, remote);
                            browser.ShowDialog();
                        }
                    else
                    {
                        var name = args[4] == "video" ? "预览测试.avi" : "预览测试.png";
                        using (var dialog = new MediaPreviewDialog(serial, new RemoteEntry { Name = name, FullPath = remote + "/" + name })) dialog.ShowDialog();
                    }
                    MediaPreview.Cleanup();
                    return 0;
                }
                var method = typeof(Transfer).GetMethod("PushFile", BindingFlags.Public | BindingFlags.Static);
                if (method == null) throw new Exception("FAIL: shared filename-preserving push is missing");
                foreach (var name in new[] { "后缀 回归.tar.gz", "picture.jpg", "plain", "special '$ & (1).txt", "error.txt" })
                {
                    var file = Path.Combine(root, name);
                    File.WriteAllText(file, "Synthetic transfer regression: " + name);
                    method.Invoke(null, new object[] { serial, file, remote, CancellationToken.None });
                    var entries = Transfer.ListRemote(serial, remote);
                    if (!entries.Exists(e => e.Name == name && e.Size == new FileInfo(file).Length))
                        throw new Exception("FAIL: exact remote name/size missing: " + name);
                    Console.WriteLine("PASS push: " + name);
                }
                try
                {
                    method.Invoke(null, new object[] { serial, Path.Combine(root, "missing.txt"), remote, CancellationToken.None });
                    throw new Exception("FAIL: missing source reported success");
                }
                catch (TargetInvocationException ex)
                {
                    if (!(ex.InnerException is IOException)) throw;
                    Console.WriteLine("PASS missing source rejected");
                }
                var imageFile = Path.Combine(root, "预览测试.png");
                using (var bitmap = new Bitmap(640, 360))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var font = new Font("Microsoft YaHei UI", 28))
                {
                    graphics.Clear(Color.FromArgb(25, 90, 130));
                    graphics.FillEllipse(Brushes.Gold, 430, 60, 110, 110);
                    graphics.DrawString("Transmit 图片预览\n640 × 360", font, Brushes.White, 35, 110);
                    bitmap.Save(imageFile, System.Drawing.Imaging.ImageFormat.Png);
                }
                var videoFile = Path.Combine(root, "预览测试.avi");
                CreateVideo(videoFile);
                foreach (var file in new[] { imageFile, videoFile })
                {
                    Transfer.PushFile(serial, file, remote);
                    var entry = new RemoteEntry { Name = Path.GetFileName(file), FullPath = remote + "/" + Path.GetFileName(file) };
                    if (!MediaPreview.CanPreview(entry.FullPath)) throw new Exception("FAIL: media not recognized");
                    var cache = MediaPreview.Download(serial, entry, CancellationToken.None);
                    if (Convert.ToBase64String(File.ReadAllBytes(cache)) != Convert.ToBase64String(File.ReadAllBytes(file)))
                        throw new Exception("FAIL: preview cache content differs");
                    if (cache != MediaPreview.Download(serial, entry, CancellationToken.None)) throw new Exception("FAIL: cache not reused");
                    Console.WriteLine("PASS preview pull/cache: " + entry.Name);
                }
                if (MediaPreview.CanPreview("plain") || MediaPreview.CanPreview("report.pdf") || !MediaPreview.IsVideo("VIDEO.MP4"))
                    throw new Exception("FAIL: media type detection");
                if (!MediaPreview.CanPreview("/sdcard/a|b/<test>/photo\".jpg") || MediaPreview.CanPreview("/sdcard/folder.jpg/no-extension"))
                    throw new Exception("FAIL: Android filename detection");
                if (MediaPreview.CachePath(serial, "/a/image.png", "1") == MediaPreview.CachePath(serial, "/b/image.png", "1"))
                    throw new Exception("FAIL: cache paths collide");
                if (MediaPreview.CachePath(serial, "/a/image.png", "1") == MediaPreview.CachePath(serial, "/a/image.png", "2"))
                    throw new Exception("FAIL: cache revision ignored");
                var concurrent = new RemoteEntry { Name = "预览测试.avi", FullPath = remote + "/预览测试.avi" };
                MediaPreview.Cleanup();
                var first = Task.Run(() => MediaPreview.Download(serial, concurrent, CancellationToken.None));
                var second = Task.Run(() => MediaPreview.Download(serial, concurrent, CancellationToken.None));
                Task.WaitAll(first, second);
                if (first.Result != second.Result || !File.Exists(first.Result)) throw new Exception("FAIL: concurrent preview cache");
                Console.WriteLine("PASS concurrent preview cache");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try { MediaPreview.Download(serial, concurrent, cancelled.Token); throw new Exception("FAIL: cancellation ignored"); }
                    catch (OperationCanceledException) { Console.WriteLine("PASS cancelled preview stops"); }
                }
                try
                {
                    Transfer.RunFileCommand("-s " + Transfer.Arg(serial) + " shell " + Transfer.Arg("exit 7"), 5000);
                    throw new Exception("FAIL: nonzero exit reported success");
                }
                catch (IOException) { Console.WriteLine("PASS nonzero exit rejected"); }
                try
                {
                    Transfer.RunFileCommand("-s " + Transfer.Arg(serial) + " shell " + Transfer.Arg("sleep 2"), 100);
                    throw new Exception("FAIL: timeout ignored");
                }
                catch (IOException) { Console.WriteLine("PASS timeout rejected"); }
                MediaPreview.Cleanup();
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        }

                // 无依赖的合成 AVI：300 帧彩色测试图，仅用于播放器验证。
        private static void CreateVideo(string path)
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                var riff = Begin(writer, "RIFF"); FourCC(writer, "AVI ");
                var header = Begin(writer, "LIST"); FourCC(writer, "hdrl");
                var avih = Begin(writer, "avih");
                foreach (int value in new[] { 100000, 30720, 0, 0, 300, 0, 1, 3072, 32, 32, 0, 0, 0, 0 }) writer.Write(value);
                End(writer, avih);
                var stream = Begin(writer, "LIST"); FourCC(writer, "strl");
                var strh = Begin(writer, "strh"); FourCC(writer, "vids"); FourCC(writer, "DIB ");
                writer.Write(0); writer.Write((short)0); writer.Write((short)0);
                foreach (int value in new[] { 0, 1, 10, 0, 300, 3072, -1, 0 }) writer.Write(value);
                foreach (short value in new short[] { 0, 0, 32, 32 }) writer.Write(value);
                End(writer, strh);
                var strf = Begin(writer, "strf");
                writer.Write(40); writer.Write(32); writer.Write(32); writer.Write((short)1); writer.Write((short)24);
                foreach (int value in new[] { 0, 3072, 0, 0, 0, 0 }) writer.Write(value);
                End(writer, strf); End(writer, stream); End(writer, header);
                var movie = Begin(writer, "LIST"); FourCC(writer, "movi");
                for (int frame = 0; frame < 300; frame++)
                {
                    var chunk = Begin(writer, "00db");
                    for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
                    { writer.Write((byte)(frame * 12)); writer.Write((byte)(x * 8)); writer.Write((byte)(y * 8)); }
                    End(writer, chunk);
                }
                End(writer, movie); End(writer, riff);
            }
        }

        private static void FourCC(BinaryWriter writer, string value) { writer.Write(Encoding.ASCII.GetBytes(value)); }
        private static long Begin(BinaryWriter writer, string id) { FourCC(writer, id); writer.Write(0); return writer.BaseStream.Position; }
        private static void End(BinaryWriter writer, long start)
        {
            var end = writer.BaseStream.Position;
            writer.BaseStream.Position = start - 4; writer.Write((int)(end - start)); writer.BaseStream.Position = end;
        }
    }
}
