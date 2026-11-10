// Welcome.cs — 首次启动引导
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PhoneAudioTray
{
    internal sealed class WelcomeForm : Form
    {
        private CheckBox chkNoMore;
        public bool StartConnect { get; private set; }

        public WelcomeForm(bool firstRun)
        {
            Text = firstRun ? "欢迎使用 · 传声" : "使用说明 · 传声";
            Size = new Size(660, 560);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.White;

            // 顶部渐变横幅
            var banner = new Panel { Left = 0, Top = 0, Width = 660, Height = 96 };
            banner.Paint += (s, e) =>
            {
                using (var brush = new LinearGradientBrush(banner.ClientRectangle,
                    Color.FromArgb(30, 58, 138), Color.FromArgb(124, 58, 237), LinearGradientMode.Horizontal))
                    e.Graphics.FillRectangle(brush, banner.ClientRectangle);

                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                using (var f = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
                using (var b = new SolidBrush(Color.White))
                    e.Graphics.DrawString("传声 → 电脑耳机", f, b, 24, 22);

                using (var f = new Font("Microsoft YaHei UI", 9f))
                using (var b = new SolidBrush(Color.FromArgb(220, Color.White)))
                    e.Graphics.DrawString("投屏 · 音频 · 剪贴板 · 文件 · 进程", f, b, 26, 56);
            };
            Controls.Add(banner);

            int y = 112;

            var lblWhat = new Label
            {
                Left = 24, Top = y, Width = 610, Height = 44,
                Text = "把手机的声音和画面弄到电脑上，顺便管理手机里的文件。全部走局域网 / USB，"
                     + "不经过任何服务器，不需要账号。"
            };
            Controls.Add(lblWhat);
            y += 52;

            var lblSteps = new Label
            {
                Left = 24, Top = y, Width = 610, Height = 22,
                Text = "三步就能用：",
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
            };
            Controls.Add(lblSteps);
            y += 28;

            AddStep(ref y, "1", "打开手机的开发者选项", "设置 → 关于手机 → 连点「版本号」7 次；再进「开发者选项」打开「USB 调试」。");
            AddStep(ref y, "2", "选一种方式连电脑", "插 USB 线（最稳）；或打开「无线调试」让手机和电脑连同一个 WiFi。");
            AddStep(ref y, "3", "双击托盘图标连手机", "连上后右键托盘菜单，可以开画面、调音量、传文件、看进程。");

            y += 8;
            var lblNote = new Label
            {
                Left = 24, Top = y, Width = 610, Height = 40,
                ForeColor = Color.FromArgb(150, 90, 0),
                Text = "注意：音频转发需要 Android 11 及以上。Android 10 及更早只能看画面，没有声音。"
            };
            Controls.Add(lblNote);

            chkNoMore = new CheckBox
            {
                Left = 24, Top = 470, Width = 220,
                Text = firstRun ? "以后启动不再显示" : "下次不再显示",
                Checked = false
            };
            Controls.Add(chkNoMore);

            var btnConnect = new Button
            {
                Left = 396, Top = 464, Width = 118, Height = 34,
                Text = "连接手机…",
                BackColor = Color.FromArgb(0, 102, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnConnect.FlatAppearance.BorderSize = 0;
            btnConnect.Click += (s, e) => { StartConnect = true; Close(); };
            Controls.Add(btnConnect);

            var btnOk = new Button { Left = 522, Top = 464, Width = 112, Height = 34, Text = "知道了", DialogResult = DialogResult.OK };
            Controls.Add(btnOk);
            AcceptButton = btnOk;
        }

        private void AddStep(ref int y, string no, string title, string detail)
        {
            var num = new Label
            {
                Left = 24, Top = y, Width = 26, Height = 26,
                Text = no,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(235, 240, 250),
                ForeColor = Color.FromArgb(30, 58, 138),
                Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold)
            };
            Controls.Add(num);

            var t = new Label
            {
                Left = 60, Top = y - 2, Width = 570, Height = 20,
                Text = title,
                Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold)
            };
            Controls.Add(t);

            var d = new Label
            {
                Left = 60, Top = y + 20, Width = 570, Height = 34,
                ForeColor = Color.DimGray,
                Text = detail
            };
            Controls.Add(d);

            y += 62;
        }

        /// <summary>首次启动标记文件</summary>
        public static string MarkerPath
        {
            get { return Path.Combine(Program.BaseDir, ".firstrun-done"); }
        }

        public static bool IsFirstRun
        {
            get { try { return !File.Exists(MarkerPath); } catch { return false; } }
        }

        public static void MarkDone()
        {
            try { File.WriteAllText(MarkerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); } catch { }
        }
    }
}
