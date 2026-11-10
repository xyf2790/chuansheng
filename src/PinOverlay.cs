// PinOverlay.cs — 浮在 scrcpy 画面窗口标题栏上的"置顶"图钉
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal static class NativeWin
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x00000008;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        /// <summary>找某个进程的主可见窗口</summary>
        public static IntPtr FindWindowByPid(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                uint windowPid;
                GetWindowThreadProcessId(h, out windowPid);
                if (windowPid != (uint)pid) return true;
                if (!IsWindowVisible(h)) return true;

                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                if (r.Right - r.Left < 80 || r.Bottom - r.Top < 80) return true;

                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static bool IsTopMost(IntPtr hWnd)
        {
            return (GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        }

        public static void SetTopMost(IntPtr hWnd, bool topMost)
        {
            SetWindowPos(hWnd, topMost ? HWND_TOPMOST : HWND_NOTOPMOST,
                0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    internal sealed class PinOverlay : Form
    {
        private readonly IntPtr target;
        private readonly Timer timer;
        private bool pinned;
        private bool hover;

        private static Bitmap _iconOff;
        private static Bitmap _iconOn;

        private const int Box = 30;        // 外框
        private const int IconPx = 22;     // 图标画多大

        public PinOverlay(IntPtr target)
        {
            this.target = target;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;                       // 始终浮在目标之上；目标失焦且未置顶时整只隐藏
            StartPosition = FormStartPosition.Manual;
            Size = new Size(Box, Box);
            TransparencyKey = Color.Magenta;
            BackColor = Color.Magenta;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;

            LoadIcons();
            pinned = NativeWin.IsTopMost(target);

            timer = new Timer { Interval = 200 };
            timer.Tick += (s, e) => Follow();
            timer.Start();

            Follow();
        }

        private static void LoadIcons()
        {
            if (_iconOff == null) _iconOff = LoadIcon("pin-off.png", false);
            if (_iconOn == null) _iconOn = LoadIcon("pin-on.png", true);
        }

        private static Bitmap LoadIcon(string fileName, bool white)
        {
            // 用 PNG 而不是 ICO：新版 ICO 的 48px 帧是 PNG 压缩的，
            // System.Drawing.Icon.ToBitmap() 会抛 ArgumentOutOfRangeException，这里绕开它。
            try
            {
                var path = Path.Combine(Program.BaseDir, fileName);
                if (!File.Exists(path)) return null;

                using (var src = Image.FromFile(path))
                {
                    var small = new Bitmap(IconPx, IconPx, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        g.DrawImage(src, 0, 0, IconPx, IconPx);
                    }

                    if (white)
                    {
                        // 生成白色剪影：保留 alpha，RGB 全白。
                        // （用反相矩阵会把深蓝变成米白，看着发灰）
                        for (int y = 0; y < small.Height; y++)
                        {
                            for (int x = 0; x < small.Width; x++)
                            {
                                var c = small.GetPixel(x, y);
                                if (c.A > 0) small.SetPixel(x, y, Color.FromArgb(c.A, 255, 255, 255));
                            }
                        }
                    }
                    return small;
                }
            }
            catch { return null; }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：点击不抢焦点
                return cp;
            }
        }

        private void Follow()
        {
            if (!NativeWin.IsWindow(target) || !NativeWin.IsWindowVisible(target))
            {
                Hide();
                return;
            }

            NativeWin.RECT r;
            if (!NativeWin.GetWindowRect(target, out r)) { Hide(); return; }

            // 状态以目标窗口的真实状态为准
            var targetTopMost = NativeWin.IsTopMost(target);
            if (targetTopMost != pinned)
            {
                pinned = targetTopMost;
                Invalidate();
            }

            // 什么时候显示图钉：目标置顶（一直显示）或目标在前台（可点）；否则隐藏，避免飞到别的程序上
            var foreground = NativeWin.GetForegroundWindow();
            var shouldShow = targetTopMost || foreground == target;
            if (!shouldShow)
            {
                if (Visible) Hide();
                return;
            }

            // 贴在标题栏右侧（留出三个系统按钮的位置）
            int x = r.Right - 3 * 46 - (Box + 6);
            int y = r.Top + 4;

            var minX = r.Left + 4;
            var maxX = r.Right - Box - 4;
            if (x < minX) x = minX;
            if (x > maxX) x = maxX;

            var minY = r.Top + 2;
            var maxY = r.Top + 12;
            if (y < minY) y = minY;
            if (y > maxY) y = maxY;

            if (Left != x || Top != y) Location = new Point(x, y);
            if (!Visible) Show();
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            pinned = !pinned;
            NativeWin.SetTopMost(target, pinned);
            pinned = NativeWin.IsTopMost(target);
            Invalidate();
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Magenta);   // 透明键

            // 底：浅色圆角，保证深色图标在深色标题栏上也看得清
            Color back, border;
            if (pinned)
            {
                back = Color.FromArgb(255, 37, 99, 235);      // 置顶：蓝底
                border = Color.FromArgb(255, 24, 78, 190);
            }
            else
            {
                back = Color.FromArgb(hover ? 255 : 235, 255, 255, 255);
                border = Color.FromArgb(hover ? 255 : 210, 150, 158, 175);
            }

            var rect = new Rectangle(1, 1, Box - 3, Box - 3);
            using (var path = Round(rect, 6))
            {
                using (var b = new SolidBrush(back)) g.FillPath(b, path);
                using (var p = new Pen(border, 1.2f)) g.DrawPath(p, path);
            }

            // 图标：普通态用原色深线；置顶态把深线“提亮”成白色，免得蓝底上看不清
            var img = pinned ? _iconOn : _iconOff;
            if (img != null)
            {
                var iconRect = new Rectangle((Box - IconPx) / 2, (Box - IconPx) / 2, IconPx, IconPx);
                g.DrawImage(img, iconRect);

            }
            else
            {
                DrawFallbackPin(g, pinned);
            }
        }

        /// <summary>把深色线条图标绘制成白色（用颜色矩阵反相亮度）</summary>
        private static void DrawIconWhite(Graphics g, Bitmap src, Rectangle dest)
        {
            var attrs = new ImageAttributes();
            // 亮度反相：白线黑底透明保留
            var mt = new ColorMatrix(new[]
            {
                new float[] { -1, 0, 0, 0, 0 },
                new float[] { 0, -1, 0, 0, 0 },
                new float[] { 0, 0, -1, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { 1, 1, 1, 0, 1 }
            });
            attrs.SetColorMatrix(mt, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            g.DrawImage(src, dest, 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            attrs.Dispose();
        }

        private static void DrawFallbackPin(Graphics g, bool solid)
        {
            var cx = Box / 2f;
            var cy = Box / 2f;
            using (var pen = new Pen(solid ? Color.White : Color.FromArgb(220, 30, 41, 59), 1.8f))
            using (var brush = new SolidBrush(solid ? Color.White : Color.Transparent))
            {
                var state = g.Save();
                if (!solid)
                {
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(-40);
                    g.TranslateTransform(-cx, -cy);
                }
                var head = new RectangleF(cx - 5.5f, cy - 6.5f, 11f, 5f);
                using (var p = RoundF(head, 2))
                {
                    g.FillPath(brush, p);
                    g.DrawPath(pen, p);
                }
                var body = new[]
                {
                    new PointF(cx - 3.2f, cy - 1.8f),
                    new PointF(cx + 3.2f, cy - 1.8f),
                    new PointF(cx + 1.4f, cy + 2.6f),
                    new PointF(cx - 1.4f, cy + 2.6f)
                };
                g.FillPolygon(brush, body);
                g.DrawPolygon(pen, body);
                g.DrawLine(pen, cx, cy + 2.6f, cx, cy + 7f);
                g.Restore(state);
            }
        }

        private static GraphicsPath Round(Rectangle rect, int radius)
        {
            return RoundF(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), radius);
        }

        private static GraphicsPath RoundF(RectangleF b, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            if (b.Width < d || b.Height < d)
            {
                p.AddRectangle(b);
                p.CloseFigure();
                return p;
            }
            p.AddArc(b.X, b.Y, d, d, 180, 90);
            p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
            p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && timer != null) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
