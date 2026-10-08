using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using PoECommandTool.Net;

namespace Rtl8239Verify
{
    /// <summary>假壳通道：文本排队等着被取，可以模拟对端关闭或读取抛异常。</summary>
    internal sealed class FakeShellChannel : IShellChannel
    {
        private readonly Queue<string> _chunks = new Queue<string>();
        private readonly object _sync = new object();

        public volatile bool Open = true;
        public volatile Exception ThrowOnRead;

        public bool IsOpen
        {
            get { return Open; }
        }

        public string Read()
        {
            Exception ex = ThrowOnRead;
            if (ex != null)
                throw ex;

            lock (_sync)
                return _chunks.Count > 0 ? _chunks.Dequeue() : null;
        }

        public void Push(string text)
        {
            lock (_sync) _chunks.Enqueue(text);
        }

        public void Write(byte[] data, int offset, int count)
        {
        }

        public void Dispose()
        {
        }
    }

    internal static partial class Program
    {
        private static string BytesToHex(byte[] buffer, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(buffer[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 读取泵：把壳通道的输出搬进有界队列。
        ///
        /// 这一层存在的理由是把 SSH.NET 的 <c>ShellStream</c> 语义（2024.0.0 改过）
        /// 挡在传输层之外，所以它的行为必须有断言钉住——尤其是「空返回 0」和
        /// 「通道关了要能分辨出来」，这两条正是 ITransport.Read 契约的另一半。
        /// </summary>
        private static void ShellReadPumpTests()
        {
            Console.WriteLine("壳通道读取泵");

            // ---- 数据从通道搬进队列 ----
            using (var channel = new FakeShellChannel())
            using (var pump = new ShellReadPump(channel))
            {
                pump.Start();
                channel.Push("AB");

                Check(WaitFor(delegate { return pump.QueuedCount == 2; }, 2000), "数据被搬进队列");

                var buffer = new byte[16];
                int n = pump.TryTake(buffer, 0, buffer.Length);
                CheckEq(n, 2, "取出 2 个字节");
                CheckEq(BytesToHex(buffer, n), "41 42", "取出的就是推入的内容");

                CheckEq(pump.TryTake(buffer, 0, buffer.Length), 0, "队列空时 TryTake 返回 0");
            }

            // ---- Latin-1：一个字符一个字节（与 LineAssembler 的假设一致） ----
            using (var channel = new FakeShellChannel())
            using (var pump = new ShellReadPump(channel))
            {
                pump.Start();
                channel.Push("AÿB");     // 0xFF 是合法字节，1 是非法

                Check(WaitFor(delegate { return pump.QueuedCount == 3; }, 2000), "3 个字符搬进队列");

                var buffer = new byte[16];
                int n = pump.TryTake(buffer, 0, buffer.Length);
                CheckEq(BytesToHex(buffer, n), "41 FF 42", "码位 <= 0xFF 按低字节映射");
            }

            // ---- 通道关闭要能分辨 ----
            using (var channel = new FakeShellChannel())
            using (var pump = new ShellReadPump(channel))
            {
                pump.Start();
                Check(!pump.ChannelClosed, "刚开始没关");

                channel.Open = false;
                Check(WaitFor(delegate { return pump.ChannelClosed; }, 2000), "通道关闭后能被识别出来");
                Check(pump.Error == null, "正常关闭不算错误");
            }

            // ---- 通道抛异常要记下来 ----
            using (var channel = new FakeShellChannel())
            using (var pump = new ShellReadPump(channel))
            {
                pump.Start();
                channel.ThrowOnRead = new System.IO.IOException("模拟：通道断了");

                Check(WaitFor(delegate { return pump.Error != null; }, 2000), "通道异常被记录下来");
            }

            // ---- 队列有上限：满了丢最旧的，不涨内存 ----
            using (var channel = new FakeShellChannel())
            using (var pump = new ShellReadPump(channel, 4))
            {
                pump.Start();
                channel.Push("ABCDEFG");      // 7 个字节，上限 4

                Check(WaitFor(delegate { return pump.QueuedCount > 0; }, 2000), "有字节进队列");
                Thread.Sleep(200);            // 让剩余的也进完

                CheckEq(pump.QueuedCount, 4, "队列不超过上限");
                Check(pump.DroppedBytes >= 3, "丢掉的字节被计数（实际 " + pump.DroppedBytes + "）");

                var buffer = new byte[16];
                int n = pump.TryTake(buffer, 0, buffer.Length);
                CheckEq(BytesToHex(buffer, n), "44 45 46 47", "留下的是最新的那一段");
            }

            Console.WriteLine();
        }
    }
}
