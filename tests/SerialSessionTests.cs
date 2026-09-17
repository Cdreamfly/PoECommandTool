using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1;
using WpfApp1.Serial;

namespace Rtl8239Verify
{
    /// <summary>不依赖硬件的串口传输层：字节排队等着被读，写入进日志。</summary>
    internal sealed class FakeSerialTransport : ISerialTransport
    {
        private const int ReadPollMs = 10;   // 模拟真实串口的读超时节奏，避免读循环空转

        private readonly object _sync = new object();
        private readonly Queue<byte> _incoming = new Queue<byte>();
        private readonly List<byte> _written = new List<byte>();

        public bool IsOpen { get; private set; }
        public bool ThrowOnRead { get; set; }
        public bool ThrowOnWrite { get; set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }

        /// <summary>收到一次写入后要回什么（裸字节）；返回 null 表示不回应。</summary>
        public Func<byte[], byte[]> AutoReply { get; set; }

        public byte[] WrittenBytes
        {
            get { lock (_sync) return _written.ToArray(); }
        }

        public void Open(SerialPortSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            IsOpen = true;
        }

        public void Close()
        {
            IsOpen = false;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (_sync)
                {
                    // 关闭优先于一切：真实传输层（SystemSerialTransport）在端口关闭时是
                    // **立刻**返回负数的，不走下面的轮询节奏。放在这里而不是函数开头，
                    // 是为了能模拟「读循环正阻塞在 Read 里时端口被关掉」——那正是竞态的现场。
                    if (!IsOpen)
                        return -1;

                    if (ThrowOnRead)
                        throw new System.IO.IOException("模拟：设备已断开");

                    if (_incoming.Count > 0)
                    {
                        int n = Math.Min(count, _incoming.Count);
                        for (int i = 0; i < n; i++)
                            buffer[offset + i] = _incoming.Dequeue();
                        return n;
                    }
                }

                if (sw.ElapsedMilliseconds >= ReadPollMs)
                    return 0;        // 读超时

                Thread.Sleep(1);
            }
        }

        public void Write(byte[] data, int offset, int count)
        {
            WriteCalls++;
            byte[] echo = null;
            lock (_sync)
            {
                if (ThrowOnWrite)
                    throw new System.IO.IOException("模拟：写失败");
                for (int i = 0; i < count; i++)
                    _written.Add(data[offset + i]);

                if (AutoReply != null)
                {
                    var copy = new byte[count];
                    Array.Copy(data, offset, copy, 0, count);
                    echo = AutoReply(copy);
                }
            }
            if (echo != null)
                Feed(echo);
        }

        public string[] GetPortNames()
        {
            return new[] { "COM_TEST" };
        }

        /// <summary>把设备将要输出的字节排进缓冲区。</summary>
        public void Feed(byte[] bytes)
        {
            lock (_sync)
            {
                for (int i = 0; i < bytes.Length; i++)
                    _incoming.Enqueue(bytes[i]);
            }
        }

        public void FeedText(string text)
        {
            Feed(Encoding.ASCII.GetBytes(text));
        }

        public void Dispose() { }
    }

    internal static partial class Program
    {
        private static volatile int _sessionFrames;
        private static volatile FrameEvent _sessionLastFrame;

        private static SerialPortSettings TestSettings()
        {
            return new SerialPortSettings { PortName = "COM_TEST", BaudRate = 115200 };
        }

        private static bool WaitFor(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;
                Thread.Sleep(5);
            }
            return condition();
        }

        private static async Task<Exception> CaughtAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static string Describe(Exception ex)
        {
            return ex == null ? "<没有异常>" : ex.GetType().Name + ": " + ex.Message;
        }

        private static async Task SessionTests()
        {
            Console.WriteLine("SerialSession");

            byte[] request = Frame("42 01 00 FF FF FF FF FF FF FF FF");   // "42 01 00 FF … 3B"

            // --- 发送：文本模板 / 裸帧 ---
            var fake = new FakeSerialTransport();
            using (var session = new SerialSession(fake))
            {
                session.Open(TestSettings());

                await session.SendAsync(
                    SendRequest.Text(request, CommandTemplate.DefaultTemplate, LineEnding.CrLf),
                    CancellationToken.None);
                CheckEq(Encoding.ASCII.GetString(fake.WrittenBytes),
                    "uart_test 42 01 00 FF FF FF FF FF FF FF FF 3B 12 500000\r\n",
                    "文本模式：写出 ASCII(模板展开) + CRLF");

                var raw = new FakeSerialTransport();
                using (var rawSession = new SerialSession(raw))
                {
                    rawSession.Open(TestSettings());
                    await rawSession.SendAsync(SendRequest.Raw(request), CancellationToken.None);
                    CheckEq(Rtl8239CommandBuilder.ToHex(raw.WrittenBytes),
                        "42 01 00 FF FF FF FF FF FF FF FF 3B",
                        "裸帧模式：恰好写出 12 个原始字节");
                }

                // --- 分包投喂只解析一次 ---
                _sessionFrames = 0;
                _sessionLastFrame = null;
                session.FrameReceived += delegate(FrameEvent fe) { _sessionFrames++; _sessionLastFrame = fe; };

                fake.FeedText("[UART_TEST] received 12 bytes: 42 01 00 01");
                fake.FeedText(" 06 00 00 00 00 00 00 4A\r\n");
                Check(WaitFor(delegate { return _sessionFrames == 1; }, 2000), "分两次投喂仍能凑成一行");
                CheckEq(_sessionFrames, 1, "只触发一次帧事件");
                Check(_sessionLastFrame != null && _sessionLastFrame.Parsed != null, "解析成功");
                Check(_sessionLastFrame != null && _sessionLastFrame.CommandId == 0x42, "命令 ID 正确");
                Check(_sessionLastFrame != null && _sessionLastFrame.Sequence == 0x01, "序列号正确");

                // --- 回显行不进解析 ---
                _sessionFrames = 0;
                fake.FeedText("[UART_TEST] sending 12 bytes: 42 01 00 FF FF FF FF FF FF FF FF 3B\r\n");
                Thread.Sleep(200);
                CheckEq(_sessionFrames, 0, "设备回显行不会被当成响应");

                // --- 正常等待 ---
                Task<FrameEvent> waiting = session.WaitForFrameAsync(0x42, 0x01, TimeSpan.FromSeconds(2), CancellationToken.None);
                Thread.Sleep(50);   // 确保等待者先注册，走等待队列而不是缓存
                fake.FeedText("[UART_TEST] received 12 bytes: 42 01 00 01 06 00 00 00 00 00 00 4A\r\n");
                FrameEvent matched = await waiting;
                Check(matched != null && matched.IsParsed, "等待者收到匹配帧并解析成功");

                // --- 序列号不匹配的帧不兑现 ---
                Task<FrameEvent> waiting2 = session.WaitForFrameAsync(0x42, 0x07, TimeSpan.FromMilliseconds(250), CancellationToken.None);
                Thread.Sleep(50);
                fake.FeedText("[UART_TEST] received 12 bytes: 42 01 00 01 06 00 00 00 00 00 00 4A\r\n");
                Exception mismatch = await CaughtAsync(delegate { return waiting2; });
                Check(mismatch is TimeoutException, "序列号不匹配的帧不会兑现等待者：" + Describe(mismatch));

                // --- 超时 ---
                Exception timeout = await CaughtAsync(delegate
                {
                    return session.WaitForFrameAsync(0x42, 0x08, TimeSpan.FromMilliseconds(80), CancellationToken.None);
                });
                Check(timeout is TimeoutException, "超时抛 TimeoutException：" + Describe(timeout));

                // --- 取消（必须与超时可区分）---
                var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                Exception cancel = await CaughtAsync(delegate
                {
                    return session.WaitForFrameAsync(0x42, 0x08, TimeSpan.FromSeconds(5), cancelled.Token);
                });
                Check(cancel is OperationCanceledException && !(cancel is TimeoutException),
                    "取消抛 OperationCanceledException：" + Describe(cancel));

                // --- 回包比等待者先到 ---
                _sessionFrames = 0;
                fake.FeedText("[UART_TEST] received 12 bytes: 42 01 00 01 06 00 00 00 00 00 00 4A\r\n");
                Check(WaitFor(delegate { return _sessionFrames == 1; }, 2000), "回包先被读循环收到");
                FrameEvent late = await session.WaitForFrameAsync(0x42, 0x01, TimeSpan.FromMilliseconds(300), CancellationToken.None);
                Check(late != null, "回包早于等待者到达时立刻兑现（不误报超时）");

                // --- 没有换行符结尾的残行 ---
                _sessionFrames = 0;
                fake.FeedText("no-newline received 12 bytes: 42 01 00 01 06 00 00 00 00 00 00 4A");
                Check(WaitFor(delegate { return _sessionFrames == 1; }, 3000), "设备最后一行没有换行符时也会被吐出");
            }

            // --- 未打开就发送 ---
            var closed = new FakeSerialTransport();
            using (var session = new SerialSession(closed))
            {
                Exception notOpen = await CaughtAsync(delegate
                {
                    return session.SendAsync(SendRequest.Raw(Frame("42 01 00 FF FF FF FF FF FF FF FF")), CancellationToken.None);
                });
                Check(notOpen is InvalidOperationException, "未打开串口时发送被拒绝：" + Describe(notOpen));
            }

            // --- 设备断开 ---
            var broken = new FakeSerialTransport();
            var faults = new List<SerialFault>();
            using (var session = new SerialSession(broken))
            {
                session.Fault += delegate(SerialFault f) { lock (faults) faults.Add(f); };
                session.Open(TestSettings());
                broken.ThrowOnRead = true;
                Check(WaitFor(delegate { lock (faults) return faults.Count > 0; }, 2000), "读出错时上报故障");
                lock (faults)
                {
                    CheckEq(faults[0].Kind, SerialFaultKind.DeviceRemoved, "故障类型：设备断开");
                    Check(faults[0].Message.Contains("断开"), "故障提示可读：" + faults[0].Message);
                }
                Check(!session.IsOpen, "故障后会话自己标记为已关闭");
            }

            // --- 关串口后读循环要及时退出 ---
            var closing = new FakeSerialTransport();
            var closingSession = new SerialSession(closing);
            closingSession.Open(TestSettings());
            Thread.Sleep(50);
            closingSession.Close();
            Task finished = await Task.WhenAny(closingSession.ReadLoopTask, Task.Delay(1500));
            Check(finished == closingSession.ReadLoopTask, "关串口后读循环 1.5 秒内退出");
            closingSession.Dispose();

            // --- 帧配对规则 ---
            Check(SerialSession.IsSequenceCorrelatable(0x44), "0x44 可以按序列号配对");
            Check(!SerialSession.IsSequenceCorrelatable(0x4B), "0x4B 的 Byte1 是 Bank ID，不按序列号配对");
            Check(SerialSession.Matches(new byte[] { 0x44, 0x07, 0x00 }, 0x44, 0x07), "命令 + 序列号都匹配");
            Check(!SerialSession.Matches(new byte[] { 0x44, 0x08, 0x00 }, 0x44, 0x07), "序列号不同不算匹配");
            Check(SerialSession.Matches(new byte[] { 0x4B, 0x03, 0x02 }, 0x4B, 0x07), "0x4B 只按命令 ID 配对");

            FrameEvent bad = SerialSession.Inspect(new byte[] { 0x44, 0x01, 0x00 }, "line");
            Check(bad.Parsed == null && !string.IsNullOrEmpty(bad.ParseError), "残缺帧解析失败但不抛异常");

            Console.WriteLine();
        }
    }
}
