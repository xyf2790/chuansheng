// GhPush.cs — 一键推送：绕过被干扰的 GitHub IP
// 用法：ghpush.exe [git push 的额外参数]
// 原理：本机起一个 CONNECT 转发，把 github.com:443 指向一个实测可用的 IP，
//       再让 git 走这个本地代理；成功后自动关闭转发。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal static class GhPush
{
    private const int LocalPort = 7899;

    // 按顺序尝试这些 IP，第一个能推成功就用它
    private static readonly string[] Candidates =
    {
        "140.82.121.4",
        "140.82.112.3",
        "20.27.177.113",
        "20.205.243.166"
    };

    private static TcpListener _listener;
    private static volatile bool _running;

    private static int Main(string[] args)
    {
        var repo = AppDomain.CurrentDomain.BaseDirectory;
        // 允许从 tools\ 里运行：仓库根目录是上一级
        if (repo.TrimEnd('\\').EndsWith("tools", StringComparison.OrdinalIgnoreCase))
            repo = System.IO.Path.GetDirectoryName(repo.TrimEnd('\\'));

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 传声 · 一键推送 ===");
        Console.WriteLine("仓库: " + repo);

        foreach (var ip in Candidates)
        {
            Console.WriteLine();
            Console.WriteLine("尝试 IP " + ip + " …");

            if (!StartRelay(ip))
            {
                Console.WriteLine("  转发启动失败，换下一个");
                continue;
            }

            try
            {
                var code = RunGit(repo, args);
                if (code == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("✅ 推送成功（走 " + ip + "）");
                    return 0;
                }
                Console.WriteLine("  这次没成功（git 退出码 " + code + "），换下一个 IP");
            }
            finally
            {
                StopRelay();
            }
        }

        Console.WriteLine();
        Console.WriteLine("❌ 所有候选 IP 都失败了。");
        Console.WriteLine("   可能整个网络都不通，或者 GitHub 账号需要重新登录。");
        Console.WriteLine("   可以试试直接跑： git push （看原始报错）");
        return 1;
    }

    private static bool StartRelay(string upstreamIp)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, LocalPort);
            _listener.Start();
            _running = true;
            var upstream = IPAddress.Parse(upstreamIp);

            var t = new Thread(() =>
            {
                while (_running)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch { return; }

                    var c = client;
                    var th = new Thread(() => HandleClient(c, upstream)) { IsBackground = true };
                    th.Start();
                }
            });
            t.IsBackground = true;
            t.Start();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  " + ex.Message);
            return false;
        }
    }

    private static void HandleClient(TcpClient client, IPAddress upstream)
    {
        try
        {
            var stream = client.GetStream();
            var head = new List<byte>();
            var buf = new byte[1];
            while (head.Count < 8192)
            {
                int n = stream.Read(buf, 0, 1);
                if (n <= 0) { client.Close(); return; }
                head.Add(buf[0]);
                int cnt = head.Count;
                if (cnt >= 4 &&
                    head[cnt - 4] == 13 && head[cnt - 3] == 10 && head[cnt - 2] == 13 && head[cnt - 1] == 10)
                    break;
            }

            var text = Encoding.ASCII.GetString(head.ToArray());
            if (!text.StartsWith("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                var no = Encoding.ASCII.GetBytes("HTTP/1.1 405 Only CONNECT\r\n\r\n");
                stream.Write(no, 0, no.Length);
                client.Close();
                return;
            }

            var up = new TcpClient();
            up.Connect(upstream, 443);
            var upStream = up.GetStream();

            var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
            stream.Write(ok, 0, ok.Length);
            stream.Flush();

            var t1 = new Thread(() => Pump(stream, upStream)) { IsBackground = true };
            t1.Start();
            Pump(upStream, stream);
            up.Close();
            client.Close();
        }
        catch
        {
            try { client.Close(); } catch { }
        }
    }

    private static void Pump(NetworkStream from, NetworkStream to)
    {
        var buf = new byte[65536];
        try
        {
            while (true)
            {
                int n = from.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                to.Write(buf, 0, n);
                to.Flush();
            }
        }
        catch { }
        finally
        {
            try { to.Close(); } catch { }
        }
    }

    private static void StopRelay()
    {
        _running = false;
        try { if (_listener != null) _listener.Stop(); } catch { }
        _listener = null;
        Thread.Sleep(150);
    }

    private static int RunGit(string repo, string[] extra)
    {
        var gitArgs = new StringBuilder();
        gitArgs.Append("-c http.proxy=http://127.0.0.1:").Append(LocalPort).Append(" push");
        foreach (var a in extra) gitArgs.Append(' ').Append(a);

        var psi = new ProcessStartInfo("git", gitArgs.ToString())
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEnd();
            var outText = p.StandardOutput.ReadToEnd();
            p.WaitForExit(180000);

            if (!string.IsNullOrWhiteSpace(outText)) Console.Write(outText);
            if (!string.IsNullOrWhiteSpace(err)) Console.Write(err);
            return p.HasExited ? p.ExitCode : 1;
        }
    }
}
