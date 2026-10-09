using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using PoECommandTool.Serial;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PoECommandTool.Net
{
    /// <summary>首次连接时要给用户看的主机密钥信息（指纹是 OpenSSH 的 SHA256:base64 形式）。</summary>
    public sealed class SshHostKey
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string KeyName { get; set; }
        public string FingerPrint { get; set; }
    }

    /// <summary>
    /// SSH 传输层：登录设备的调试控制台，对上层就是一个字节管道。
    ///
    /// 这是本项目**唯一**引入第三方依赖的地方（SSH.NET）——真加密与密钥交换不是能自己写的东西。
    ///
    /// 几处刻意的选择：
    ///
    /// 1. **用持久 shell 通道，不是每命令一次 exec**。轮询是 1 Hz 起，每条命令单开一次
    ///    SSH 会话会把时间全花在握手上，而且控制台的会话状态也断了。
    /// 2. **读取走 <see cref="ShellReadPump"/>**，不直接用 <c>ShellStream.Read</c>：
    ///    它的阻塞语义在 SSH.NET 2024.0.0 被改过，隔离在泵线程里就不受版本影响。
    /// 3. **主机密钥默认拒绝**。没装 <see cref="HostKeyPrompt"/> 就直接拒连——
    ///    静默信任等于把中间人攻击的门留着；宁可连不上，让人看见原因。
    ///    指纹只记在**进程内**（本程序没有任何设置持久化，这一版也不打算为它开这个头）。
    /// 4. **口令只在这里用掉**：认证由 SSH.NET 内部完成，口令不经过会话、不进通讯日志。
    /// </summary>
    public sealed class SshTransport : ITransport
    {
        /// <summary>读等待窗口。与串口、Telnet 同一个节拍（读循环检查取消的周期）。</summary>
        private const int ReadWaitMs = 200;

        /// <summary>登录后的欢迎信息读掉不要：那不是给这条管线用的数据。</summary>
        private const int SettleMs = 400;

        private const int ConnectTimeoutSeconds = 10;

        /// <summary>
        /// 本次运行内已信任的主机密钥指纹。
        ///
        /// **必须是静态的**：每点一次「连接」都会新建一个 <see cref="SshTransport"/> 实例，
        /// 若做成实例字段，每次连接都会再弹一次确认框——而设计意图是「本次运行内记住」。
        /// （2026-10-08 真机上就是被这个咬到的：断开后重连，确认框又冒出来，
        /// 而界面那边整段连接在后台线程上等着它，看起来就像「点了连接没反应」。）
        /// </summary>
        private static readonly HashSet<string> TrustedFingerPrints =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly object TrustedLock = new object();

        private SshClient _client;
        private ShellStream _stream;
        private ShellReadPump _pump;

        /// <summary>本次连接的目标，供主机密钥回调显示（回调触发时连接信息还没回来）。</summary>
        private string _pendingHost;
        private int _pendingPort;

        /// <summary>
        /// 主机密钥确认回调。返回 false 即中止连接。
        /// **没装这个回调时一律拒绝**——安全默认，界面必须显式接上。
        ///
        /// ⚠️ **这个回调不在调用 <see cref="Open"/> 的那条线程上执行**
        /// （2026-10-08 实测：SSH.NET 在它自己的协商线程上抛 <c>HostKeyReceived</c>）。
        /// 界面的实现必须先派发回 UI 线程再弹框；而调用方也必须**不要把 <see cref="Open"/>
        /// 放在 UI 线程上跑**——否则那个派发会等一个正卡在 <c>Connect()</c> 里的线程，死锁。
        /// </summary>
        public Func<SshHostKey, bool> HostKeyPrompt { get; set; }

        public bool IsOpen
        {
            get { return _client != null && _client.IsConnected; }
        }

        public void Open(TransportSettings settings)
        {
            var net = settings as NetworkSettings;
            if (net == null)
                throw new ArgumentException("SSH 传输层需要 NetworkSettings。", "settings");
            if (net.Kind != TransportKind.Ssh)
                throw new ArgumentException("这份设置不是 SSH 的。", "settings");
            if (string.IsNullOrEmpty(net.Host))
                throw new ArgumentException("没有填主机名或地址。", "settings");
            if (string.IsNullOrEmpty(net.User))
                throw new ArgumentException("SSH 需要用户名。", "settings");

            // 口令为空时**当场说清楚**，别等一趟网络往返回来只丢一句英文的
            // "Permission denied (password)." ——用户根本看不出来是口令框空了
            // （曾经在断开后清空口令框，于是重连必然踩这个坑）。
            if (!net.UsesKeyFile && string.IsNullOrEmpty(net.Password))
                throw new ArgumentException("SSH 需要口令或私钥文件——口令框现在是空的。", "settings");

            Close();

            var methods = new List<AuthenticationMethod>();
            if (net.UsesKeyFile)
                methods.Add(new PrivateKeyAuthenticationMethod(net.User, new PrivateKeyFile(net.KeyFile)));
            else
                methods.Add(new PasswordAuthenticationMethod(net.User, net.Password ?? string.Empty));

            // 有些 sshd 只提供键盘交互，补一条不会有害；SSH.NET 会按顺序试。
            methods.Add(new KeyboardInteractiveAuthenticationMethod(net.User));

            var info = new ConnectionInfo(net.Host, net.Port, net.User, methods.ToArray());
            info.Timeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds);

            // 主机密钥回调在 Connect() 里触发，那时连接信息还没回来——先记下来供它用。
            _pendingHost = net.Host;
            _pendingPort = net.Port;

            var client = new SshClient(info);
            client.HostKeyReceived += OnHostKeyReceived;

            try
            {
                client.Connect();
            }
            catch (Exception)
            {
                client.Dispose();
                throw;      // 认证失败 / 连不上：异常直接抛给界面显示
            }

            _client = client;

            ShellStream stream = client.CreateShellStream("xterm", 120, 40, 800, 600, 8192);
            _stream = stream;

            var pump = new ShellReadPump(new ShellStreamChannel(stream));
            _pump = pump;
            pump.Start();

            Settle();
        }

        public void Close()
        {
            // 顺序要紧：先把泵字段置空，正在 Read 的调用者下一次检查就会拿到「已关闭」，
            // 不必等下面这些 Dispose 把阻塞中的通道读解开。
            ShellReadPump pump = _pump;
            ShellStream stream = _stream;
            SshClient client = _client;
            _pump = null;
            _stream = null;
            _client = null;

            if (pump != null)
                pump.Stop();

            try
            {
                if (stream != null) stream.Dispose();
            }
            catch (Exception)
            {
                // 对端可能已经走了
            }

            try
            {
                if (client != null)
                {
                    if (client.IsConnected) client.Disconnect();
                    client.Dispose();
                }
            }
            catch (Exception)
            {
            }

            // 泵线程大概率还卡在通道读里。Join 只是尽力而为——上层的关闭义务不依赖它，
            // 因为上层从不直接等这个线程。
            if (pump != null)
                pump.Join(500);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");

            ShellReadPump pump = _pump;
            if (pump == null)
                return -1;      // 已关闭 ≠ 超时，读循环必须停下来

            var deadline = Stopwatch.StartNew();
            while (true)
            {
                if (_pump != pump)
                    return -1;      // 期间被关掉了

                int got = pump.TryTake(buffer, offset, count);
                if (got > 0)
                    return got;

                Exception error = pump.Error;
                if (error != null)
                    throw new IOException("SSH 链路读取失败：" + error.Message, error);

                if (pump.ChannelClosed)
                    return -1;

                int remaining = ReadWaitMs - (int)deadline.ElapsedMilliseconds;
                if (remaining <= 0)
                    return 0;       // 本次读超时（无数据），不是错误

                Thread.Sleep(Math.Min(remaining, 20));
            }
        }

        public void Write(byte[] data, int offset, int count)
        {
            ShellStream stream = _stream;
            if (stream == null)
                throw new InvalidOperationException("SSH 链路未打开。");

            stream.Write(data, offset, count);
            stream.Flush();
        }

        public void Dispose()
        {
            Close();
        }

        // -----------------------------------------------------------------
        //  内部
        // -----------------------------------------------------------------

        /// <summary>把登录后的欢迎信息读掉，别让它混进通讯日志。</summary>
        private void Settle()
        {
            var deadline = Stopwatch.StartNew();
            var buffer = new byte[4096];

            while (deadline.ElapsedMilliseconds < SettleMs)
            {
                ShellReadPump pump = _pump;
                if (pump == null)
                    return;

                if (pump.TryTake(buffer, 0, buffer.Length) == 0)
                    Thread.Sleep(20);
            }
        }

        private void OnHostKeyReceived(object sender, HostKeyEventArgs e)
        {
            string fingerprint = FingerprintOf(e);

            lock (TrustedLock)
            {
                if (TrustedFingerPrints.Contains(fingerprint))
                {
                    e.CanTrust = true;
                    return;
                }
            }

            Func<SshHostKey, bool> prompt = HostKeyPrompt;
            if (prompt == null)
            {
                // 没装确认回调：拒绝。安全默认——静默信任就是给中间人留门。
                e.CanTrust = false;
                return;
            }

            var info = new SshHostKey
            {
                Host = _pendingHost,
                Port = _pendingPort,
                KeyName = e.HostKeyName,
                FingerPrint = fingerprint,
            };

            bool trust = prompt(info);
            e.CanTrust = trust;
            if (trust)
            {
                lock (TrustedLock)
                    TrustedFingerPrints.Add(fingerprint);
            }
        }

        /// <summary>
        /// 主机密钥指纹。优先用库给出的 SHA256 形式，拿不到时自己按 OpenSSH 的算法算一遍。
        ///
        /// **一定要补上 "SHA256:" 前缀**：SSH.NET 的 <c>FingerPrintSHA256</c> 只给 base64 那一截
        /// （2026-10-08 在真机上实测），而 <c>ssh-keygen -lf</c> / <c>ssh-keyscan</c> 的输出是
        /// <c>SHA256:&lt;base64&gt;</c>。少了前缀，用户就没法拿两边做逐字比对——
        /// 而这个弹框的全部意义就是让他做这个比对。
        /// </summary>
        private static string FingerprintOf(HostKeyEventArgs e)
        {
            string value = e.FingerPrintSHA256;

            if (string.IsNullOrEmpty(value))
            {
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(e.HostKey);
                    value = Convert.ToBase64String(hash).TrimEnd('=');
                }
            }
            else if (value.StartsWith("SHA256:", StringComparison.Ordinal))
            {
                return value;
            }

            return "SHA256:" + value;
        }

        /// <summary>
        /// 把 <c>ShellStream</c> 包成 <see cref="IShellChannel"/>。
        /// <c>Read()</c> 取回当前已缓冲的文本、不阻塞等新数据。
        /// </summary>
        private sealed class ShellStreamChannel : IShellChannel
        {
            private readonly ShellStream _stream;

            public ShellStreamChannel(ShellStream stream)
            {
                _stream = stream;
            }

            public bool IsOpen
            {
                get { return _stream.CanRead; }
            }

            public string Read()
            {
                return _stream.Read();
            }

            public void Write(byte[] data, int offset, int count)
            {
                _stream.Write(data, offset, count);
                _stream.Flush();
            }

            public void Dispose()
            {
                _stream.Dispose();
            }
        }
    }
}
