using System;
using System.Collections.Generic;
using System.Threading;

namespace PoECommandTool.Net
{
    /// <summary>
    /// 把一条壳通道的输出抽到一个有界的字节队列里，供上层按 <c>ITransport.Read</c> 的契约取用。
    ///
    /// 为什么要有它（而不是直接在 <c>ShellStream</c> 上实现 Read）：
    /// SSH.NET 的 <c>ShellStream</c> 在 2024.0.0 被重写过，<c>Read</c> 返回 0 的时点、
    /// 以及 <c>DataAvailable</c> 的语义都变了（后者可能永远为 true）。把这些语义
    /// **隔离在一个后台线程里**，上层的读取就只剩「从队列里有界地取」——与 SSH.NET 的版本无关，
    /// 也就不会被它某次升级悄悄改掉行为。
    ///
    /// 顺带满足一条硬要求：<c>Close()</c> 必须能在 1 秒内让阻塞中的读返回。因为上层从不直接
    /// 等这个线程，即使它卡在通道里，上层也能立刻看到「已关闭」。
    /// </summary>
    public sealed class ShellReadPump : IDisposable
    {
        /// <summary>没有数据时的轮询间隔。</summary>
        private const int IdleSleepMs = 20;

        /// <summary>队列的字节上限。对端刷屏时丢最旧的，而不是把内存吃光。</summary>
        private const int DefaultQueueLimit = 64 * 1024;

        private readonly IShellChannel _channel;
        private readonly int _queueLimit;
        private readonly object _sync = new object();
        private readonly Queue<byte> _queue = new Queue<byte>();

        private Thread _thread;
        private volatile bool _stopping;
        private Exception _error;
        private bool _channelClosed;

        public ShellReadPump(IShellChannel channel)
            : this(channel, DefaultQueueLimit)
        {
        }

        public ShellReadPump(IShellChannel channel, int queueLimit)
        {
            if (channel == null) throw new ArgumentNullException("channel");
            if (queueLimit < 1) throw new ArgumentOutOfRangeException("queueLimit");

            _channel = channel;
            _queueLimit = queueLimit;
        }

        /// <summary>泵线程里发生的异常；非空表示这条链路已经不可用了。</summary>
        public Exception Error
        {
            get { lock (_sync) return _error; }
        }

        /// <summary>通道自己报告已关闭（对端把 shell 关掉了）。</summary>
        public bool ChannelClosed
        {
            get { lock (_sync) return _channelClosed; }
        }

        public int QueuedCount
        {
            get { lock (_sync) return _queue.Count; }
        }

        /// <summary>队列里丢掉的字节数（上限溢出时增长）。</summary>
        public int DroppedBytes { get; private set; }

        public void Start()
        {
            if (_thread != null)
                return;

            _thread = new Thread(Loop);
            _thread.IsBackground = true;    // 进程退出时不要被它拖住
            _thread.Start();
        }

        /// <summary>取最多 <paramref name="count"/> 个字节；队列为空返回 0。**不阻塞。**</summary>
        public int TryTake(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");

            lock (_sync)
            {
                int n = Math.Min(count, _queue.Count);
                for (int i = 0; i < n; i++)
                    buffer[offset + i] = _queue.Dequeue();
                return n;
            }
        }

        public void Stop()
        {
            _stopping = true;
        }

        /// <summary>等泵线程退出。**只是尽力而为**：正确性不依赖它，所以超时就放弃。</summary>
        public void Join(int timeoutMs)
        {
            Thread thread = _thread;
            if (thread != null)
                thread.Join(timeoutMs);
        }

        public void Dispose()
        {
            Stop();
            _channel.Dispose();
        }

        private void Loop()
        {
            while (!_stopping)
            {
                string chunk;
                try
                {
                    chunk = _channel.Read();
                }
                catch (Exception ex)
                {
                    lock (_sync) _error = ex;
                    return;
                }

                if (chunk == null || chunk.Length == 0)
                {
                    if (!_channel.IsOpen)
                    {
                        lock (_sync) _channelClosed = true;
                        return;
                    }

                    Thread.Sleep(IdleSleepMs);
                    continue;
                }

                byte[] bytes = Latin1(chunk);
                lock (_sync)
                {
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        if (_queue.Count >= _queueLimit)
                        {
                            _queue.Dequeue();
                            DroppedBytes++;
                        }
                        _queue.Enqueue(bytes[i]);
                    }
                }
            }
        }

        /// <summary>
        /// 文本按 Latin-1 映射成字节：**一个字符一个字节**。
        ///
        /// 这不是随手选的：<c>Serial/LineAssembler</c> 就是把每个字节当成一个字符来切行的
        /// （`(char)data[i]`），两边的假设必须一致，否则非 ASCII 字节会被拆错位。
        /// 手写映射而不是用 Encoding：net48 上没有 Encoding.Latin1，
        /// 而按码位取低字节就正是 Latin-1 的定义。
        /// </summary>
        private static byte[] Latin1(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
                bytes[i] = text[i] > 0xFF ? (byte)'?' : (byte)text[i];
            return bytes;
        }
    }
}
