using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PoECommandTool;
using PoECommandTool.Net;
using PoECommandTool.Serial;

namespace Rtl8239Verify
{
    /// <summary>
    /// 回环上的假 Telnet 设备：本机起一个监听，收下客户端发来的字节，也能往客户端推字节。
    /// </summary>
    internal sealed class FakeTelnetServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<byte> _received = new List<byte>();
        private readonly object _sync = new object();
        private volatile TcpClient _client;

        public int Port { get; private set; }

        /// <summary>收到一次写入后要回什么（用来模拟设备控制台）；返回 null 表示不回应。</summary>
        public Func<byte[], byte[]> AutoReply { get; set; }

        public FakeTelnetServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            Task.Run(delegate
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    _client = client;

                    var buffer = new byte[4096];
                    while (true)
                    {
                        int n = client.GetStream().Read(buffer, 0, buffer.Length);
                        if (n <= 0) break;
                        lock (_sync)
                            for (int i = 0; i < n; i++) _received.Add(buffer[i]);

                        Func<byte[], byte[]> reply = AutoReply;
                        if (reply != null)
                        {
                            var written = new byte[n];
                            Array.Copy(buffer, 0, written, 0, n);
                            byte[] outgoing = reply(written);
                            if (outgoing != null && outgoing.Length > 0)
                            {
                                client.GetStream().Write(outgoing, 0, outgoing.Length);
                                client.GetStream().Flush();
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // 测试结束时的正常关闭
                }
            });
        }

        public bool WaitForClient(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (_client != null) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        public void Send(string text)
        {
            Send(Encoding.ASCII.GetBytes(text));
        }

        public void Send(byte[] bytes)
        {
            TcpClient client = _client;
            if (client == null) throw new InvalidOperationException("还没有客户端连上来。");
            client.GetStream().Write(bytes, 0, bytes.Length);
            client.GetStream().Flush();
        }

        public string ReceivedText()
        {
            lock (_sync) return Encoding.ASCII.GetString(_received.ToArray());
        }

        public byte[] ReceivedBytes()
        {
            lock (_sync) return _received.ToArray();
        }

        public void CloseClient()
        {
            TcpClient client = _client;
            if (client != null) client.Close();
        }

        public void Dispose()
        {
            try { if (_client != null) _client.Close(); } catch (Exception) { }
            try { _listener.Stop(); } catch (Exception) { }
        }
    }

    internal static partial class Program
    {
        private static NetworkSettings TelnetTo(int port)
        {
            return new NetworkSettings(TransportKind.Telnet) { Host = "127.0.0.1", Port = port };
        }

        /// <summary>在给定时间内反复读，把读到的字节拼成字符串（遇到负数即停）。</summary>
        private static string ReadFor(ITransport transport, int ms)
        {
            var buffer = new byte[256];
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                int n = transport.Read(buffer, 0, buffer.Length);
                if (n < 0) break;
                for (int i = 0; i < n; i++) sb.Append((char)buffer[i]);
            }
            return sb.ToString();
        }

        private static void TelnetTransportTests()
        {
            Console.WriteLine("Telnet 传输层");

            // ---- 未打开时读返回 -1（终止条件，不是超时） ----
            using (var idle = new TelnetTransport())
            {
                CheckEq(idle.Read(new byte[16], 0, 16), -1, "未打开时 Read 返回 -1（终止，不是 0）");
                Check(!idle.IsOpen, "未打开时 IsOpen 为 false");
            }

            // ---- 连上、收文本、发文本 ----
            using (var server = new FakeTelnetServer())
            using (var transport = new TelnetTransport())
            {
                transport.Open(TelnetTo(server.Port));
                Check(transport.IsOpen, "连上之后 IsOpen");

                if (!server.WaitForClient(2000))
                    Check(false, "假设备接受了连接");
                else
                {
                    Check(true, "假设备接受了连接");

                    server.Send("debug#");
                    CheckEq(ReadFor(transport, 1500), "debug#", "读到设备发来的文本");

                    transport.Write(Encoding.ASCII.GetBytes("uart_test\r\n"), 0, 11);
                    var sw = Stopwatch.StartNew();
                    while (server.ReceivedText().IndexOf("uart_test", StringComparison.Ordinal) < 0
                           && sw.ElapsedMilliseconds < 1500)
                        Thread.Sleep(10);
                    CheckEq(server.ReceivedText(), "uart_test\r\n", "写出的字节到达设备");
                }
            }

            // ---- 空闲返回 0（不是负数的「已关闭」） ----
            using (var server = new FakeTelnetServer())
            using (var transport = new TelnetTransport())
            {
                transport.Open(TelnetTo(server.Port));
                server.WaitForClient(2000);

                int r = transport.Read(new byte[16], 0, 16);
                CheckEq(r, 0, "没有数据时 Read 返回 0（空闲超时）");
            }

            // ---- 协商字节被剥掉、应答被写回 ----
            using (var server = new FakeTelnetServer())
            using (var transport = new TelnetTransport())
            {
                transport.Open(TelnetTo(server.Port));
                server.WaitForClient(2000);

                // 41 FF FD 01 42：夹在数据中间的「对方 DO ECHO」
                server.Send(Bytes("41 FF FD 01 42"));
                CheckEq(ReadFor(transport, 1500), "AB", "协商字节不进数据流");

                var sw = Stopwatch.StartNew();
                while (server.ReceivedBytes().Length < 3 && sw.ElapsedMilliseconds < 1500)
                    Thread.Sleep(10);
                CheckEq(HexOf(server.ReceivedBytes()), "FF FC 01", "协商应答回写给了设备");
            }

            // ---- 对端关闭 -> 读返回负数 ----
            using (var server = new FakeTelnetServer())
            using (var transport = new TelnetTransport())
            {
                transport.Open(TelnetTo(server.Port));
                server.WaitForClient(2000);

                server.CloseClient();

                var sw = Stopwatch.StartNew();
                int r = 0;
                while (sw.ElapsedMilliseconds < 2000)
                {
                    r = transport.Read(new byte[16], 0, 16);
                    if (r < 0) break;
                }
                Check(r < 0, "对端关闭后 Read 返回负数（读循环会停下来）");
            }

            // ---- Close 必须在 1 秒内解除阻塞中的读 ----
            // 这是会话里 ReadLoopJoinTimeoutMs 的硬要求：超时会让链路再也关不掉、
            // 或者重开后两条读循环抢同一个链路、把每个字节劈成两半。
            using (var server = new FakeTelnetServer())
            using (var transport = new TelnetTransport())
            {
                transport.Open(TelnetTo(server.Port));
                server.WaitForClient(2000);

                var done = new TaskCompletionSource<int>();
                Task.Run(delegate
                {
                    var buffer = new byte[16];
                    int r;
                    do { r = transport.Read(buffer, 0, buffer.Length); } while (r >= 0);
                    done.TrySetResult(r);
                });

                Thread.Sleep(200);          // 让它真的读起来
                var sw = Stopwatch.StartNew();
                transport.Close();

                bool finished = done.Task.Wait(1000);
                Check(finished && done.Task.Result < 0,
                    "Close 后读循环在 1 秒内拿到负数（用时 " + sw.ElapsedMilliseconds + " ms）");
                Check(!transport.IsOpen, "Close 之后 IsOpen 为 false");
            }
        }

        /// <summary>
        /// 端到端：完整会话架在 Telnet 上跑一遍
        /// 「模板填充 → 控制台 → 回显 + 回包行 → 提取 → 解析」。
        ///
        /// 这条断言的意义是证明**整条回包管线确实与传输层无关**——控制台仿真
        /// （<see cref="ConsoleDeviceWriteHandler"/>）是现成的，这里只是把下面那层从
        /// 假串口换成了真的回环套接字。
        /// </summary>
        private static async Task TelnetConsoleEndToEndTests()
        {
            Console.WriteLine("端到端（Telnet → 设备调试控制台）");

            using (var server = new FakeTelnetServer())
            using (var link = new SwitchableTransport())
            using (var session = new SerialSession(link))
            {
                server.AutoReply = ConsoleDeviceWriteHandler;
                link.Install(new TelnetTransport());

                session.ReceiveMode = ReceiveMode.TextLines;   // 控制台链路只有文本
                session.Open(TelnetTo(server.Port));

                byte[] frame = Rtl8239CommandBuilder.PortMeasurementGet(0x01, 0x00);
                var options = new SendOptions
                {
                    Mode = SendMode.TextCommand,
                    Template = CommandTemplate.DefaultTemplate,
                    LineEnding = LineEnding.CrLf,
                };

                Task<FrameEvent> wait = session.WaitForFrameAsync(
                    0x44, 0x01, TimeSpan.FromSeconds(5), CancellationToken.None);
                await session.SendAsync(options.For(frame), CancellationToken.None);
                FrameEvent ev = await wait;

                Check(ev.IsParsed, "0x44 的响应解析成功（整条管线与传输无关）");
                if (ev.IsParsed)
                {
                    var measurement = (PortMeasurement)ev.Parsed;
                    CheckNear(measurement.VoltageMv, 12890.0, 1e-6, "电压 = 12890 mV");
                    // 设备会先回显命令行，那行里也有一个校验和正确的帧：
                    // 一旦被当成响应，功率会解成 0xFFFF×0.1 = 6553.5 W。
                    CheckNear(measurement.PowerW, 30.0, 1e-9, "功率 = 30 W（回显没有被当成响应）");
                }

                string sent = server.ReceivedText();
                Check(sent.StartsWith("uart_test 44 01 00", StringComparison.Ordinal),
                    "Telnet 上发出的是控制台文本命令：" + sent.Split('\r')[0]);
            }

            Console.WriteLine();
        }
    }
}
