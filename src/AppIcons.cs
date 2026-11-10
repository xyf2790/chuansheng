// AppIcons.cs — 应用图标（从 APK 抽真实图标，取不到就画首字母头像）+ 常见包名中文名
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;

namespace PhoneAudioTray
{
    internal static class AppIcons
    {
        public static readonly string IconDir = Path.Combine(Program.BaseDir, "icons");
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Image> Cache = new Dictionary<string, Image>();

        // 常见包名 / 进程名 → 中文名（包名机器友好，人看不懂）
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ===== 常见应用（通用）=====
            { "com.tencent.mm", "微信" },
            { "com.tencent.mobileqq", "QQ" },
            { "com.tencent.tim", "TIM" },
            { "com.eg.android.AlipayGphone", "支付宝" },
            { "com.taobao.taobao", "淘宝" },
            { "com.jingdong.app.mall", "京东" },
            { "com.xunmeng.pinduoduo", "拼多多" },
            { "com.ss.android.ugc.aweme", "抖音" },
            { "com.ss.android.ugc.aweme.lite", "抖音极速版" },
            { "com.smile.gifmaker", "快手" },
            { "com.xingin.xhs", "小红书" },
            { "tv.danmaku.bili", "哔哩哔哩" },
            { "com.netease.cloudmusic", "网易云音乐" },
            { "com.tencent.qqmusic", "QQ音乐" },
            { "com.kugou.android", "酷狗音乐" },
            { "com.baidu.BaiduMap", "百度地图" },
            { "com.autonavi.minimap", "高德地图" },
            { "com.sina.weibo", "微博" },
            { "com.zhihu.android", "知乎" },
            { "com.baidu.tieba", "贴吧" },
            { "com.UCMobile", "UC浏览器" },
            { "com.quark.browser", "夸克" },
            { "com.alibaba.android.rimet", "钉钉" },
            { "com.tencent.wework", "企业微信" },
            { "com.ss.android.lark", "飞书" },
            { "com.tencent.docs", "腾讯文档" },
            { "com.netease.nim", "网易云信" },
            { "com.hunantv.imgo.activity", "芒果TV" },
            { "com.qiyi.video", "爱奇艺" },
            { "com.tencent.qqlive", "腾讯视频" },
            { "com.youku.phone", "优酷" },
            { "com.alibaba.wireless", "阿里巴巴" },
            { "com.achievo.vipshop", "唯品会" },
            { "com.sankuai.meituan", "美团" },
            { "me.ele", "饿了么" },
            { "com.dianping.v1", "大众点评" },
            { "ctrip.android.view", "携程" },
            { "com.MobileTicket", "铁路12306" },
            { "com.cmbchina.ccd.pluto.cmbActivity", "招商银行" },
            { "com.icbc", "工商银行" },
            { "com.unionpay", "云闪付" },
            { "com.alipay.mobile.antui", "支付宝组件" },
            { "com.wudaokou.hippo", "盒马" },
            { "com.taobao.idlefish", "闲鱼" },
            { "com.taobao.taoavatar", "淘宝组件" },

            // ===== 国际应用 =====
            { "com.android.chrome", "Chrome" },
            { "com.google.android.googlequicksearchbox", "Google" },
            { "com.google.android.gms", "Google 服务" },
            { "com.google.android.gms.unstable", "Google 服务(测试)" },
            { "com.google.android.gsf", "Google 服务框架" },
            { "com.android.vending", "Google Play" },
            { "com.google.android.youtube", "YouTube" },
            { "com.google.android.gm", "Gmail" },
            { "com.google.android.apps.maps", "Google 地图" },
            { "com.google.android.apps.photos", "Google 相册" },
            { "com.google.android.dialer", "电话(Google)" },
            { "com.google.android.apps.messaging", "信息(Google)" },
            { "com.instagram.android", "Instagram" },
            { "com.whatsapp", "WhatsApp" },
            { "org.telegram.messenger", "Telegram" },
            { "com.facebook.katana", "Facebook" },
            { "com.twitter.android", "X (Twitter)" },
            { "com.microsoft.office.outlook", "Outlook" },
            { "com.android.kiwi", "Kiwi 浏览器" },

            // ===== vivo / iQOO =====
            { "com.bbk.launcher2", "vivo 桌面" },
            { "com.vivo.pushservice", "vivo 推送" },
            { "com.vivo.globalsearch", "vivo 全局搜索" },
            { "com.vivo.connbase", "vivo 连接服务" },
            { "com.vivo.magazine", "vivo 负一屏" },
            { "com.vivo.email", "vivo 邮件" },
            { "com.vivo.assistant", "Jovi 助手" },
            { "com.vivo.camera", "vivo 相机" },
            { "com.vivo.gallery", "vivo 相册" },
            { "com.vivo.music", "vivo 音乐" },
            { "com.vivo.video", "vivo 视频" },
            { "com.vivo.weather", "vivo 天气" },
            { "com.vivo.appstore", "vivo 应用商店" },
            { "com.vivo.browser", "vivo 浏览器" },
            { "com.android.bbkcalculator", "计算器" },
            { "com.vivo.notes", "vivo 便签" },
            { "com.iqoo.secure", "i管家" },
            { "com.bbk.account", "vivo 账号" },
            { "com.bbk.updater", "系统更新" },
            { "com.bbk.iqoo.logsystem", "日志系统" },
            { "com.bbk.cloud", "vivo 云服务" },
            { "com.vivo.appfilter", "应用过滤" },
            { "com.vivo.safecenter", "vivo 安全中心" },

            // ===== 小米 / Redmi（MIUI / HyperOS）=====
            { "com.miui.home", "小米桌面" },
            { "com.miui.securitycenter", "安全中心" },
            { "com.miui.securitycore", "安全核心" },
            { "com.miui.gallery", "小米相册" },
            { "com.miui.camera", "小米相机" },
            { "com.android.camera", "相机" },
            { "com.miui.player", "小米音乐" },
            { "com.miui.video", "小米视频" },
            { "com.miui.notes", "小米便签" },
            { "com.miui.weather2", "小米天气" },
            { "com.miui.calculator", "计算器" },
            { "com.miui.powerkeeper", "电量管理" },
            { "com.miui.packageinstaller", "应用安装器" },
            { "com.miui.permcenter", "权限管理" },
            { "com.miui.voiceassist", "小爱同学" },
            { "com.miui.cloudservice", "小米云服务" },
            { "com.miui.analytics", "统计分析" },
            { "com.miui.notification", "通知管理" },
            { "com.miui.aod", "息屏显示" },
            { "com.miui.tsmclient", "小米钱包" },
            { "com.miui.fm", "收音机" },
            { "com.xiaomi.market", "小米应用商店" },
            { "com.xiaomi.account", "小米账号" },
            { "com.xiaomi.mipicks", "GetApps" },
            { "com.miui.mishare.connectivity", "小米互传" },
            { "com.miui.screenrecorder", "屏幕录制" },
            { "com.android.fileexplorer", "文件管理" },

            // ===== 华为 / 荣耀（EMUI / HarmonyOS 4）=====
            { "com.huawei.android.launcher", "华为桌面" },
            { "com.huawei.camera", "华为相机" },
            { "com.huawei.gallery", "华为图库" },
            { "com.huawei.music", "华为音乐" },
            { "com.huawei.video", "华为视频" },
            { "com.huawei.systemmanager", "手机管家" },
            { "com.huawei.android.hwouc", "系统更新" },
            { "com.huawei.appmarket", "华为应用市场" },
            { "com.huawei.hwid", "华为账号" },
            { "com.huawei.android.pushagent", "华为推送服务" },
            { "com.huawei.hms", "HMS Core" },
            { "com.huawei.android.totemweather", "华为天气" },
            { "com.huawei.notepad", "备忘录" },
            { "com.huawei.android.thememanager", "主题" },
            { "com.huawei.android.findmyphone", "查找设备" },
            { "com.huawei.android.mirrorshare", "投屏" },
            { "com.huawei.contacts", "联系人(华为)" },
            { "com.hihonor.android.launcher", "荣耀桌面" },
            { "com.hihonor.camera", "荣耀相机" },
            { "com.hihonor.photos", "荣耀图库" },
            { "com.hihonor.systemmanager", "荣耀管家" },
            { "com.hihonor.appmarket", "荣耀应用市场" },
            { "com.hihonor.id", "荣耀账号" },

            // ===== OPPO / realme / 一加（ColorOS）=====
            { "com.oppo.launcher", "OPPO 桌面" },
            { "com.oppo.camera", "OPPO 相机" },
            { "com.coloros.gallery3d", "OPPO 相册" },
            { "com.oppo.music", "OPPO 音乐" },
            { "com.coloros.video", "OPPO 视频" },
            { "com.coloros.weather2", "OPPO 天气" },
            { "com.coloros.calculator", "计算器" },
            { "com.coloros.note", "OPPO 便签" },
            { "com.coloros.safecenter", "安全中心" },
            { "com.coloros.filemanager", "文件管理" },
            { "com.coloros.assistantscreen", "负一屏" },
            { "com.oppo.market", "OPPO 软件商店" },
            { "com.heytap.market", "欢太商城" },
            { "com.heytap.browser", "OPPO 浏览器" },
            { "com.heytap.themestore", "主题商店" },
            { "com.oplus.launcher", "OPLUS 桌面" },
            { "com.oplus.camera", "OPLUS 相机" },
            { "com.redteamobile.roaming", "国际漫游" },

            // ===== 三星 =====
            { "com.sec.android.app.launcher", "三星桌面" },
            { "com.sec.android.app.camera", "三星相机" },
            { "com.sec.android.gallery3d", "三星相册" },
            { "com.samsung.android.dialer", "三星电话" },
            { "com.samsung.android.messaging", "三星信息" },
            { "com.sec.android.app.samsungapps", "三星应用商店" },
            { "com.samsung.android.voc", "Samsung Members" },
            { "com.samsung.android.app.spage", "三星负一屏" },
            { "com.samsung.android.scloud", "三星云" },
            { "com.sec.android.app.myfiles", "我的文件" },

            // ===== AOSP / 通用系统 =====
            { "com.android.systemui", "系统界面" },
            { "com.android.settings", "设置" },
            { "com.android.phone", "电话" },
            { "com.android.mms", "信息" },
            { "com.android.contacts", "联系人" },
            { "com.android.deskclock", "时钟" },
            { "com.android.calendar", "日历" },
            { "com.android.calculator2", "计算器" },
            { "com.android.launcher3", "启动器" },
            { "com.android.launcher", "启动器" },
            { "com.android.filemanager", "文件管理" },
            { "com.android.documentsui", "文件选择器" },
            { "com.android.gallery3d", "图库" },
            { "com.android.music", "音乐" },
            { "com.android.bluetooth", "蓝牙" },
            { "com.android.nfc", "NFC" },
            { "com.android.se", "安全元件" },
            { "com.android.shell", "Shell" },
            { "com.android.wallpaper", "壁纸" },
            { "com.android.inputmethod.latin", "Android 输入法" },
            { "com.android.providers.telephony", "电话存储" },
            { "com.android.providers.contacts", "联系人存储" },
            { "com.android.providers.downloads", "下载管理" },
            { "com.android.providers.settings", "设置存储" },
            { "com.android.providers.media.module", "媒体存储模块" },
            { "com.android.providers.calendar", "日历存储" },
            { "com.android.permissioncontroller", "权限管理" },
            { "com.android.server.telecom", "通讯服务" },
            { "com.android.cellbroadcastreceiver", "小区广播" },
            { "com.android.storagemanager", "存储管理" },
            { "com.android.traceur", "系统跟踪" },
            { "com.android.emergency", "紧急信息" },
            { "com.android.keychain", "密钥链" },
            { "com.android.mtp", "MTP" },
            { "com.android.adservices", "广告服务" },
            { "com.android.ext.services", "系统扩展服务" },
            { "org.codeaurora.ims", "通话服务(QTI)" },
            { "com.qualcomm.location", "定位服务(Qualcomm)" },
            { "com.qualcomm.qti.workloadclassifier", "负载调度(Qualcomm)" },
            { "com.qti.phone", "电话(QTI)" },
            { "com.qti.diagservices", "诊断服务(QTI)" },
            { "com.mediatek.ims", "通话服务(MTK)" },
            { "android.ext.services", "系统扩展服务" },
            { "android.process.acore", "联系人存储" },
            { "android.process.media", "媒体存储" },

            // ===== 系统级进程（没有包结构）=====
            { "init", "系统初始化" },
            { "zygote", "应用孵化器" },
            { "zygote64", "应用孵化器(64)" },
            { "system_server", "系统服务" },
            { "servicemanager", "服务管理" },
            { "logd", "日志守护" },
            { "surfaceflinger", "图形合成" },
            { "vold", "存储管理" },
            { "netd", "网络守护" },
            { "media", "媒体服务" },
            { "mediaserver", "媒体服务" },
            { "audioserver", "音频服务" },
            { "cameraserver", "相机服务" },
            { "keystore", "密钥库" },
            { "keystore2", "密钥库" },
            { "installd", "应用安装" },
            { "statsd", "统计守护" },
            { "lmkd", "内存回收" },
            { "hwservicemanager", "硬件服务管理" },
            { "android.hardware.audio.service", "音频硬件服务" },
            { "wpa_supplicant", "WiFi 守护" },
            { "net.hostapd", "热点守护" },
            { "sh", "Shell 进程" },
            { "app_process", "应用进程宿主" },
            { "adbd", "ADB 守护" },
            { "thermald", "温控守护" },
            { "dumpsys", "系统信息" }
        };

        /// <summary>包名 → 给人看的名字</summary>
        public static string DisplayName(string pkg)
        {
            if (string.IsNullOrEmpty(pkg)) return "";
            string name;
            if (Known.TryGetValue(pkg, out name)) return name;
            return "";   // 让它回落到包名
        }

        public static string ShortPackage(string pkg)
        {
            if (string.IsNullOrEmpty(pkg)) return "";
            var idx = pkg.LastIndexOf('.');
            return idx >= 0 && idx + 1 < pkg.Length ? pkg.Substring(idx + 1) : pkg;
        }

        public static Image Get(string pkg)
        {
            lock (Gate)
            {
                Image img;
                if (Cache.TryGetValue(pkg, out img)) return img;
            }

            var file = CacheFile(pkg);
            if (File.Exists(file))
            {
                try
                {
                    var img = Image.FromFile(file);
                    lock (Gate) Cache[pkg] = img;
                    return img;
                }
                catch { }
            }
            return null;
        }

        public static Image GetOrAvatar(string pkg)
        {
            var img = Get(pkg);
            if (img != null) return img;
            return MakeAvatar(pkg);   // 不写进 Cache，免得把真图标挡住
        }

        private static string CacheFile(string pkg)
        {
            var safe = pkg.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
            return Path.Combine(IconDir, safe + ".png");
        }

        /// <summary>画一个灰色底色的标签图标（系统进程/内核线程用）</summary>
        public static Image MakeBadge(string text, Color back)
        {
            const int size = 32;
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(back))
                    g.FillEllipse(b, 1, 1, size - 2, size - 2);
                using (var f = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.White))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString(text, f, b, new RectangleF(0, 0, size, size), sf);
                }
            }
            return bmp;
        }

        /// <summary>首字母头像：彩色圆 + 1~2 个字</summary>
        public static Image MakeAvatar(string pkg)
        {
            const int size = 32;
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                int hash = 0;
                foreach (var c in pkg ?? "") hash = hash * 31 + c;
                var hue = Math.Abs(hash) % 360;
                var back = ColorFromHsv(hue, 0.55, 0.85);

                using (var b = new SolidBrush(back))
                    g.FillEllipse(b, 1, 1, size - 2, size - 2);

                var label = DisplayName(pkg);
                var text = label.Length > 0
                    ? label.Substring(0, Math.Min(2, label.Length))
                    : ShortPackage(pkg).ToUpperInvariant();
                if (text.Length > 2) text = text.Substring(0, 2);

                using (var f = new Font("Microsoft YaHei UI", text.Length > 1 ? 10f : 13f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.White))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString(text, f, b, new RectangleF(0, 0, size, size), sf);
                }
            }
            return bmp;
        }

        private static Color ColorFromHsv(double h, double s, double v)
        {
            int hi = (int)(h / 60) % 6;
            double f = h / 60 - Math.Floor(h / 60);
            int p = (int)(v * (1 - s) * 255);
            int q = (int)(v * (1 - f * s) * 255);
            int t = (int)(v * (1 - (1 - f) * s) * 255);
            int vv = (int)(v * 255);
            switch (hi)
            {
                case 0: return Color.FromArgb(vv, t, p);
                case 1: return Color.FromArgb(q, vv, p);
                case 2: return Color.FromArgb(p, vv, t);
                case 3: return Color.FromArgb(p, q, vv);
                case 4: return Color.FromArgb(t, p, vv);
                default: return Color.FromArgb(vv, p, q);
            }
        }

        /// <summary>后台尝试从 APK 抽取真实图标；每次拿到一个就回调</summary>
        public static void FetchAll(string serial, IEnumerable<string> packages, Action<string> onReady, Action done)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Directory.CreateDirectory(IconDir);

                    // 有的机型没有 unzip（老 Android / 精简 ROM），那就全部用头像，别白跑 100 次
                    var hasUnzip = Program.RunAdb("-s " + serial + " shell which unzip", 10000).Trim();
                    if (hasUnzip.Length == 0) return;

                    Program.RunAdb("-s " + serial + " shell mkdir -p /sdcard/.PhoneAudioTrayIcons", 15000);

                    foreach (var pkg in packages)
                    {
                        if (File.Exists(CacheFile(pkg))) { onReady(pkg); continue; }

                        var apkPath = Program.RunAdb("-s " + serial + " shell pm path " + pkg, 15000)
                            .Replace("package:", "").Split('\n')[0].Trim();
                        if (apkPath.Length == 0 || !apkPath.EndsWith(".apk")) continue;

                        var entry = FindIconEntry(serial, apkPath);
                        if (entry == null) continue;

                        var remote = "/sdcard/.PhoneAudioTrayIcons/" + pkg.Replace(':', '_') + ".png";
                        Program.RunAdb("-s " + serial + " shell \"unzip -p '" + apkPath + "' '" + entry + "' > " + remote + "\"", 30000);

                        var bytes = Program.RunAdbBinary("-s " + serial + " exec-out cat " + remote);
                        if (bytes != null && bytes.Length > 200 && (IsPng(bytes) || IsWebp(bytes)))
                        {
                            File.WriteAllBytes(CacheFile(pkg), bytes);
                            onReady(pkg);
                        }
                        Program.RunAdb("-s " + serial + " shell rm -f " + remote, 10000);
                    }
                }
                catch { }
                finally
                {
                    if (done != null) done();
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        private static string FindIconEntry(string serial, string apkPath)
        {
            var listing = Program.RunAdb("-s " + serial + " shell \"unzip -l '" + apkPath
                + "' 2>/dev/null | grep -iE 'res/.*(ic_launcher|ic_app|app_icon|launcher_icon).*\\.(png|webp)$'\"", 30000);
            if (string.IsNullOrWhiteSpace(listing)) return null;

            string best = null;
            long bestSize = -1;
            foreach (var raw in listing.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;

                long size;
                if (!long.TryParse(parts[0], out size)) continue;
                var name = parts[parts.Length - 1];
                if (!name.Contains("/")) continue;

                // 优先高密度目录
                long weight = size;
                if (name.Contains("xxxhdpi")) weight += 1 << 20;
                else if (name.Contains("xxhdpi")) weight += 1 << 19;

                if (weight > bestSize) { bestSize = weight; best = name; }
            }
            return best;
        }

        private static bool IsPng(byte[] b) { return b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47; }
        private static bool IsWebp(byte[] b) { return b.Length > 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'; }
    }
}
