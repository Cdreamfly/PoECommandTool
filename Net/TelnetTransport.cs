using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using PoECommandTool.Serial;

namespace PoECommandTool.Net
{
    /// <summary>
    /// Telnet 传输层：把设备调试控制台通过 TCP 接进来，对上层就是一个字节管道。
    ///
    /// 只依赖 <c>System.Net.Sockets</c>（在 System.dll 里，csproj 早已引用），
    /// **不引入任何第三方包**——这也是把它排在 SSH 前面做的原因。
    ///
    /// 三件事值得说明：
    ///
    /// 1. **读契约**（见 <see cref="ITransport.Read"/>）：用 <c>Socket.Poll</c> 做有界轮询，
    ///    空闲返回 0、对端关闭返回负数。<c>Poll</c> 而不是让 <c>Receive</c> 抛超时异常，
    ///    是因为前者不涉及异常开销、也更直白。**绝不能**返回 0 表示「已关闭」——
    ///    那会让读循环满速空转（这个坑在串口那边踩过一次）。
    ///
    /// 2. **Close 必须能解除阻塞中的读**：先把字段置空（后续 Read 立刻返回 -1），
    ///    再 Shutdown + Close 套接字，让已经阻塞在 Poll 里的那次调用立刻醒来。
    ///    会话关闭时会等旧读循环退出，卡住的话链路就再也关不掉了。
    ///
    /// 3. **登录在 Open 里完成**：会话是在 <c>Open</c> 成功**之后**才起读循环的，
    ///    所以这里读掉的欢迎信息和登录提示都不会流进会话——**口令因此不会进通讯日志**。
    /// </summary>
    public sealed class TelnetTransport : ITransport
    {
        /// <summary>与 SystemSerialTransport.ReadTimeoutMs 同节拍：读循环检查取消的周期。</summary>
        private const int ReadTimeoutMs = 200;

        /// <summary>等登录提示的上限。等不到不算失败——设备可能根本不要登录。</summary>
        private const int LoginPromptTimeoutMs = 5000;

        /// <summary>登录成功后再读一会儿，把欢迎信息冲掉。</summary>
        private const int LoginSettleMs = 300;

        private readonly TelnetNegotiation _negotiation = new TelnetNegotiation();
        private readonly List<byte> _payload = new List<byte>();
        private readonly List<byte> _reply = new List<byte>();
        private readonly Queue<byte> _pending = new Queue<byte>();
        private readonly byte[] _scratch = new byte[4096];

        private TcpClient _client;

        public bool IsOpen
        {
            get { return _client != null; }
        }

        public void Open(TransportSettings settings)
        {
            var net = settings as NetworkSettings;
            if (net == null)
                throw new ArgumentException("Telnet 传输层需要 NetworkSettings。", "settings");
            if (net.Kind != TransportKind.Telnet)
                throw new ArgumentException("这份设置不是 Telnet 的。", "settings");
            if (string.IsNullOrEmpty(net.Host))
                throw new ArgumentException("没有填主机名或地址。", "settings");

            Close();

            var client = new TcpClient();
            try
            {
                client.Connect(net.Host, net.Port);
            }
            catch (Exception)
            {
                client.Close();
                throw;      // 连接失败由调用方提示（会话的 Open 会让异常抛给界面）
            }

            client.ReceiveTimeout = ReadTimeoutMs;
            client.NoDelay = true;
            _client = client;

            LoginIfNeeded(net);
        }

        public void Close()
        {
            TcpClient client = _client;
            _client = null;
            _pending.Clear();

            if (client == null)
                return;

            try
            {
                // Shutdown 让正阻塞在 Poll/Receive 里的那次调用立刻返回
                client.Client.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
                // 对端可能已经走了，关闭异常没有意义
            }

            try
            {
                client.Close();
            }
            catch (Exception)
            {
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");

            // 上次协商后剩下的净数据先交出去
            if (_pending.Count > 0)
                return DrainPending(buffer, offset, count);

            TcpClient client = _client;
            if (client == null)
                return -1;      // 已关闭 ≠ 超时，读循环必须停下来

            var deadline = Stopwatch.StartNew();
            while (true)
            {
                if (_client != client)
                    return -1;      // 期间被关掉了

                int remaining = ReadTimeoutMs - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0)
                    return 0;       // 本次读超时（无数据），不是错误

                int got = PumpOnce(client, remaining);
                if (got < 0)
                    return -1;      // 对端关闭
                if (got == 0)
                    return 0;       // 空闲超时

                if (_pending.Count > 0)
                    return DrainPending(buffer, offset, count);

                // 这一段全是协商字节，净数据为空：在剩余窗口内继续等真正的数据
            }
        }

        public void Write(byte[] data, int offset, int count)
        {
            TcpClient client = _client;
            if (client == null)
                throw new InvalidOperationException("Telnet 链路未打开。");

            SendRaw(client, data, offset, count);
        }

        public void Dispose()
        {
            Close();
        }

        // -----------------------------------------------------------------
        //  内部
        // -----------------------------------------------------------------

        /// <summary>
        /// 收一次数据、跑一遍协商，把**净数据**排进 <see cref="_pending"/>，并回写协商应答。
        /// 返回：&gt;0 = 本次收到的净数据字节数；0 = 空闲超时或只收到协商；负数 = 对端关闭。
        /// </summary>
        private int PumpOnce(TcpClient client, int timeoutMs)
        {
            try
            {
                // Poll 返回 true 表示「可读」：有数据、或对端已关闭
                if (!client.Client.Poll(timeoutMs * 1000, SelectMode.SelectRead))
                    return 0;

                int n = client.Client.Receive(_scratch, 0, _scratch.Length, SocketFlags.None);
                if (n <= 0)
                    return -1;      // 对端 FIN

                _payload.Clear();
                _reply.Clear();
                _negotiation.Process(_scratch, n, _payload, _reply);
                if (_reply.Count > 0)
                    SendRaw(client, _reply);   // 不经过 _pending：这是发给对端的协商应答

                for (int i = 0; i < _payload.Count; i++)
                    _pending.Enqueue(_payload[i]);

                return _payload.Count;
            }
            catch (SocketException)
            {
                // 被我们自己关掉时（Close 会 Shutdown）走到这里。对读循环来说这就是「已关闭」：
                // 返回负数让它停下，绝不能返回 0 —— 那会变成满速空转。
                return -1;
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
        }

        private int DrainPending(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _pending.Count);
            for (int i = 0; i < n; i++)
                buffer[offset + i] = _pending.Dequeue();
            return n;
        }

        private static void SendRaw(TcpClient client, IList<byte> bytes)
        {
            var arr = new byte[bytes.Count];
            for (int i = 0; i < bytes.Count; i++)
                arr[i] = bytes[i];
            SendRaw(client, arr, 0, arr.Length);
        }

        private static void SendRaw(TcpClient client, byte[] data, int offset, int count)
        {
            client.Client.Send(data, offset, count, SocketFlags.None);
        }

        /// <summary>
        /// 设备控制台可能先要登录。**没填账号就假定不要登录**，直接进管线。
        ///
        /// 这里读掉的字节不会到达会话（<c>Open</c> 返回之后读循环才起来），
        /// 所以**口令不会出现在通讯日志里**——这是有意的，不是巧合。
        /// </summary>
        private void LoginIfNeeded(NetworkSettings net)
        {
            if (string.IsNullOrEmpty(net.User))
                return;

            TcpClient client = _client;

            if (!WaitForText(client, "ogin", LoginPromptTimeoutMs))
                return;     // 没有登录提示：当作不需要登录，别把链路卡死

            SendLine(client, net.User);

            if (!WaitForText(client, "assword", LoginPromptTimeoutMs))
                return;

            SendLine(client, net.Password);

            Settle(client, LoginSettleMs);
        }

        private bool WaitForText(TcpClient client, string needle, int timeoutMs)
        {
            var deadline = Stopwatch.StartNew();
            var text = new StringBuilder();

            while (deadline.ElapsedMilliseconds < timeoutMs)
            {
                if (_client != client)
                    return false;

                int remaining = timeoutMs - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0)
                    break;

                int got = PumpOnce(client, Math.Min(remaining, ReadTimeoutMs));
                if (got < 0)
                    return false;
                if (got == 0)
                    continue;

                while (_pending.Count > 0)
                    text.Append((char)_pending.Dequeue());

                if (text.ToString().IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>读掉并丢弃一段数据（登录后的欢迎信息）。</summary>
        private void Settle(TcpClient client, int durationMs)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < durationMs)
            {
                if (_client != client)
                    return;

                int remaining = durationMs - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0)
                    return;

                PumpOnce(client, Math.Min(remaining, ReadTimeoutMs));
                _pending.Clear();       // 欢迎信息不进管线
            }
        }

        private void SendLine(TcpClient client, string text)
        {
            var bytes = new List<byte>(Encoding.ASCII.GetBytes(text ?? string.Empty));
            bytes.Add(0x0D);
            bytes.Add(0x0A);
            SendRaw(client, bytes);
        }
    }
}
