using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1.Serial
{
    /// <summary>一次发送请求。</summary>
    public sealed class SendRequest
    {
        public SendMode Mode { get; set; }
        public byte[] Frame { get; set; }
        public string Template { get; set; }
        public LineEnding LineEnding { get; set; }

        public static SendRequest Text(byte[] frame, string template, LineEnding ending)
        {
            return new SendRequest { Mode = SendMode.TextCommand, Frame = frame, Template = template, LineEnding = ending };
        }

        public static SendRequest Raw(byte[] frame)
        {
            return new SendRequest { Mode = SendMode.RawFrame, Frame = frame };
        }
    }

    /// <summary>
    /// 一次「发送 + 等响应」事务的持有凭证：用完 <see cref="Dispose"/> 即放锁。
    ///
    /// 为什么要这个：<see cref="SerialSession"/> 里的 _writeLock 只保证**字节**不交错，
    /// 挡不住两条并发的查询在**事务**层交错——A 的响应可能落进 B 的等待窗口。
    /// 响应配对（命令号 + 序列号）能挡住大部分误配，但那是「碰巧对」不是「设计对」，
    /// 尤其 0xC0 系命令只按命令号配对（见 IsSequenceCorrelatable）。
    /// </summary>
    public sealed class SerialTransaction : IDisposable
    {
        private SemaphoreSlim _gate;

        internal SerialTransaction(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            // 幂等：重复 Dispose 不能多放一次，否则信号量会被撑开、互斥失效。
            // 用 Exchange 而不是「读-判-写」，因为这是公开类型，无法假定只有一个线程会调它。
            SemaphoreSlim gate = Interlocked.Exchange(ref _gate, null);
            if (gate != null)
                gate.Release();
        }
    }

    /// <summary>从设备输出里提取到的一帧，附带解析结果（或解析失败原因）。</summary>
    public sealed class FrameEvent
    {
        public byte[] Raw { get; set; }
        public byte CommandId { get; set; }
        public byte Sequence { get; set; }
        /// <summary>Rtl8239ResponseParser.Parse 的结果；解析失败时为 null。</summary>
        public object Parsed { get; set; }
        /// <summary>解析失败原因；成功时为 null。</summary>
        public string ParseError { get; set; }
        /// <summary>帧来自哪一行原始文本。</summary>
        public string Line { get; set; }

        public bool IsParsed
        {
            get { return Parsed != null; }
        }
    }

    /// <summary>接收方式：调试控制台回文本行，还是串口直连时回二进制帧。</summary>
    public enum ReceiveMode
    {
        /// <summary>按行拆包，用识别规则从文本里抠帧（调试控制台）。</summary>
        TextLines = 0,

        /// <summary>在字节流里直接同步 12 字节帧（串口直连 PoE 控制器）。</summary>
        RawFrames = 1,
    }

    /// <summary>
    /// 串口会话：打开/读写、拆行、从设备输出里提取响应帧、自动解析、请求-响应配对。
    ///
    /// 这里一行业都不知道 WPF 的存在：所有事件都在读线程上触发，由界面层自己负责封送到 UI 线程。
    /// 依赖 <see cref="ISerialTransport"/> 而不是 System.IO.Ports，所以整条链路可以用假传输层测试。
    /// </summary>
    public sealed class SerialSession : IDisposable
    {
        private const int ReadBufferSize = 8192;

        /// <summary>连续多少次「读超时且无数据」之后，把没有换行符结尾的残行也当作一行吐出去。</summary>
        private const int IdleReadsBeforeFlush = 2;

        /// <summary>最近帧的小缓存条数——用于「回包比等待者先到」这种时序竞态。</summary>
        private const int RecentFrameCacheSize = 4;

        /// <summary>记住最近发出去的几帧，用来识别「设备把自己的请求原样回送」。</summary>
        private const int RecentSentCount = 4;

        /// <summary>
        /// 关串口时等旧读循环退出的上限。超时说明驱动卡住，此时宁可拒绝重开，
        /// 也不能让两条读循环去抢同一个端口——那会让每个字节被劈成两半。
        /// </summary>
        private const int ReadLoopJoinTimeoutMs = 1000;

        private sealed class Waiter
        {
            public byte CommandId;
            public byte Sequence;
            public TaskCompletionSource<FrameEvent> Completion;
        }

        private readonly ISerialTransport _transport;
        private readonly LineAssembler _assembler = new LineAssembler();
        private readonly RawFrameScanner _scanner = new RawFrameScanner();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _transactionLock = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private readonly object _assemblerSync = new object();
        private readonly List<Waiter> _waiters = new List<Waiter>();
        private readonly Queue<FrameEvent> _recentFrames = new Queue<FrameEvent>();
        private readonly List<byte[]> _recentSentFrames = new List<byte[]>();

        private CancellationTokenSource _readCts;
        private bool _isOpen;
        private bool _disposed;
        private int _selfEchoIgnored;

        public SerialSession(ISerialTransport transport)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            _transport = transport;
            Extractor = new ResponseLineExtractor();
        }

        /// <summary>响应识别规则；可在运行中修改。</summary>
        public ResponseLineExtractor Extractor { get; private set; }

        /// <summary>
        /// 接收方式。文本模式走「按行拆包 + 识别规则」，裸帧模式走字节流同步。
        /// 应当与发送方式配套：调试控制台（文本命令）配 <see cref="ReceiveMode.TextLines"/>，
        /// 串口直连（裸帧）配 <see cref="ReceiveMode.RawFrames"/>。
        /// </summary>
        public ReceiveMode ReceiveMode { get; set; }

        /// <summary>
        /// 响应里多字节字段的字节序。真机实测是大端（见 <see cref="ByteOrder"/> 的说明），
        /// 若换到按手册小端实现的设备，在界面上切一下即可。
        /// </summary>
        public ByteOrder ByteOrder { get; set; }

        /// <summary>
        /// 自己记开关状态：net48 上拔线之后 SerialPort.IsOpen 经常还是 true，不能信它。
        /// </summary>
        public bool IsOpen
        {
            get { return _isOpen; }
        }

        /// <summary>读循环任务；关窗口时可以用它配合超时等待线程退出。</summary>
        public Task ReadLoopTask { get; private set; }

        /// <summary>被识别为「自己的请求回显」而丢弃的帧数（设备本地回显 / 半双工适配器）。</summary>
        public int SelfEchoIgnored
        {
            get { return _selfEchoIgnored; }
        }

        /// <summary>每收到一行文本（已按 CR/LF 拆好）。</summary>
        public event Action<string> LineReceived;

        /// <summary>每收到一段原始字节。</summary>
        public event Action<byte[]> BytesReceived;

        /// <summary>每从某一行里提取到一帧（无论解析成功与否）。</summary>
        public event Action<FrameEvent> FrameReceived;

        /// <summary>某一行看着像响应、但识别规则没有采纳（第 1 个参数是原始行，第 2 个是原因）。</summary>
        public event Action<string, string> LineRejected;

        /// <summary>串口故障（打开失败、读写失败、设备断开）。</summary>
        public event Action<SerialFault> Fault;

        public void Open(SerialPortSettings settings)
        {
            if (_disposed) throw new ObjectDisposedException("SerialSession");
            if (_isOpen) throw new InvalidOperationException("串口已经打开了。");
            if (settings == null) throw new ArgumentNullException("settings");

            // 读故障之后 _isOpen 会被置 false，但取消源还留着。这里先统一收尾，
            // 否则反复「拔线—重开」会每次泄漏一个 CancellationTokenSource（内含内核句柄）。
            // Close() 会**等旧读循环真正退出**再返回，所以下面那句 _transport.Open 不会
            // 在旧循环还没死透时就重新打开端口。
            if (_readCts != null)
                Close();

            _transport.Open(settings);          // 失败时异常直接抛给调用方，由界面提示
            _isOpen = true;

            lock (_assemblerSync)
            {
                _assembler.Reset();
                _scanner.Reset();
            }
            lock (_sync)
            {
                _waiters.Clear();
                _recentFrames.Clear();
                _recentSentFrames.Clear();
            }

            // 令牌由循环自己捕获（作为参数传进去），循环体不再去读 _readCts 字段。
            // 否则「关—开」挨得很近时，旧循环会在循环顶部读到**新一代**的、还没取消的
            // 取消源，于是永不退出——两条循环抢同一个端口，字节被劈成两半。
            CancellationTokenSource cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            _readCts = cts;
            ReadLoopTask = Task.Factory.StartNew(
                delegate { ReadLoop(token); }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public void Close()
        {
            if (!_isOpen && _readCts == null)
                return;

            // 即使 StopReadLoop 因为旧循环没能按时退出而抛（拒绝信号），
            // 收尾也必须做完：否则在 WaitForFrameAsync 上等着的调用方会一直挂到自己的超时。
            try
            {
                StopReadLoop();
            }
            finally
            {
                lock (_assemblerSync)
                {
                    _assembler.Reset();
                    _scanner.Reset();
                }
                CancelPendingWaiters("串口已关闭。");
            }
        }

        /// <summary>
        /// 停掉读循环并关闭传输层，**等旧循环真正退出**才返回。
        ///
        /// 为什么要等：旧循环可能仍阻塞在 _transport.Read 里。不等它退出就重开串口的话，
        /// 它会一直活着并和新循环抢同一个端口——每个字节被两条循环各读走一半，
        /// 表现成半截行、重复的 FrameReceived、以及轮询的幽灵超时。
        ///
        /// 顺序也有讲究：先取消令牌，再关传输层。端口一关，阻塞中的 Read 立刻返回负数，
        /// 循环在下一个检查点看到**自己那一代**的令牌已取消，就干净退出了。
        /// </summary>
        private void StopReadLoop()
        {
            CancellationTokenSource cts = _readCts;
            Task loop = ReadLoopTask;

            _readCts = null;
            _isOpen = false;

            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            try
            {
                _transport.Close();
            }
            catch (Exception)
            {
                // 关闭时的异常没有意义（设备可能已经不在了），吞掉
            }

            if (loop != null && !loop.IsCompleted)
            {
                bool exited;
                try
                {
                    exited = loop.Wait(ReadLoopJoinTimeoutMs);
                }
                catch (AggregateException)
                {
                    // 旧循环是异常结束的（异常细节已由 Fault 事件报过）。在这里 Wait 一下
                    // 顺带观察掉它，免得变成 unobserved task exception。
                    exited = true;
                }

                if (!exited)
                    throw new InvalidOperationException(
                        "上一个读循环在 " + ReadLoopJoinTimeoutMs + " 毫秒内没有退出（串口驱动可能卡住了）。" +
                        "为避免两条读循环抢同一个端口导致收包错乱，本次操作已中止，请重试或重新插拔设备。");
            }
        }

        /// <summary>把请求写进串口。文本模式按模板拼装，裸帧模式直接写原始字节。</summary>
        public async Task SendAsync(SendRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (request.Frame == null || request.Frame.Length == 0)
                throw new ArgumentException("要发送的命令帧是空的。", "request");
            if (!_isOpen)
                throw new InvalidOperationException("串口未打开。");

            byte[] data = request.Mode == SendMode.RawFrame
                ? request.Frame
                : CommandTemplate.BuildTextCommand(request.Template, request.Frame, request.LineEnding);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ISerialTransport transport = _transport;
                await Task.Run(delegate { transport.Write(data, 0, data.Length); }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;      // 取消不算链路故障
            }
            catch (Exception ex)
            {
                // 写不进去说明链路已经不可用：把会话标记成关闭，免得轮询一直重试刷屏
                _isOpen = false;
                RaiseFault(SerialFaultKind.WriteFailed, "写串口失败：" + ex.Message, ex);
                throw;
            }
            finally
            {
                _writeLock.Release();
            }

            RememberSent(request.Frame);
        }

        /// <summary>
        /// 取事务锁：拿到之后，直到 <see cref="SerialTransaction.Dispose"/> 为止，
        /// 本条链路上的**发送与等待**不会被另一个事务插进来。
        ///
        /// 调用方必须把「发送 + 等响应」整对包在 using 里，而不是只包发送：
        /// 只包发送等于没包——真正要防的是响应落进别人的等待窗口。
        /// **不要**在这段持锁区间里再调用本会话的发送（会自己等自己，死锁）。
        /// </summary>
        public async Task<SerialTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            await _transactionLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            // 新事务不继承上一笔事务遗留的帧。_recentFrames 的本意只是容纳「回包比等待者先到」
            // ——发送返回与注册等待之间没有先后保证，而那个窗口只存在于一次事务内部。
            // 跨事务复用它的后果很具体：上一次**超时之后**才到的回包会留在缓存里，
            // 下一次读取一注册等待就立刻认领它，于是每次显示的都是上一次的数据，
            // 时间戳却是新的。0x4B / 0xC0 / 0xCA 只按命令 ID 配对，尤其容易命中。
            lock (_sync)
            {
                _recentFrames.Clear();
            }

            return new SerialTransaction(_transactionLock);
        }

        /// <summary>
        /// 等一帧与指定命令 / 序列号匹配的响应。
        /// 超时抛 <see cref="TimeoutException"/>，取消抛 <see cref="OperationCanceledException"/>（两者可区分）。
        /// </summary>
        public async Task<FrameEvent> WaitForFrameAsync(byte commandId, byte sequence,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<FrameEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_sync)
            {
                // 回包有可能比等待者先到（发送返回与读线程之间没有先后保证），先翻缓存
                FrameEvent late = TakeRecentLocked(commandId, sequence);
                if (late != null)
                    completion.TrySetResult(late);
                else
                    _waiters.Add(new Waiter { CommandId = commandId, Sequence = sequence, Completion = completion });
            }

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutCts.CancelAfter(timeout);
                using (timeoutCts.Token.Register(delegate { completion.TrySetCanceled(); }))
                {
                    try
                    {
                        return await completion.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException(cancellationToken);

                        throw new TimeoutException(string.Format(
                            "等待 0x{0:X2} 序列号 0x{1:X2} 的响应超时（{2:F0} ms）。",
                            commandId, sequence, timeout.TotalMilliseconds));
                    }
                    finally
                    {
                        // 无论成功、超时还是取消，都要把自己从等待者队列里摘掉。
                        // 否则超时的等待者会一直留着：既泄漏，又会在序列号回绕之后
                        // 「吃掉」本该交给当前请求的那一帧响应。
                        lock (_sync)
                        {
                            _waiters.RemoveAll(delegate(Waiter w) { return w.Completion == completion; });
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                Close();
            }
            catch (Exception)
            {
                // Close() 在旧读循环没能按时退出时会抛（那是给 Open 用的拒绝信号）；
                // Dispose 必须继续往下走，把底层句柄释放掉。
            }

            _writeLock.Dispose();
            _transactionLock.Dispose();
            _transport.Dispose();
        }

        // -----------------------------------------------------------------
        //  帧协议辅助
        // -----------------------------------------------------------------

        /// <summary>
        /// 该命令的响应能否用 Byte1 跟请求配对。
        ///
        /// 0x4B 的 Byte1 是 Bank ID、0xC0 是子命令、0xCA 是序列号但应答格式不同，
        /// 这几个只能按命令 ID 配对。
        /// </summary>
        public static bool IsSequenceCorrelatable(byte commandId)
        {
            return commandId != 0x4B && commandId != 0xC0 && commandId != 0xCA;
        }

        /// <summary>这一帧是不是指定命令（必要时还包括序列号）的响应。</summary>
        public static bool Matches(byte[] raw, byte commandId, byte sequence)
        {
            if (raw == null || raw.Length < 2 || raw[0] != commandId)
                return false;
            if (!IsSequenceCorrelatable(commandId))
                return true;
            return raw[1] == sequence;
        }

        /// <summary>把一帧原始字节变成带解析结果的 <see cref="FrameEvent"/>（解析失败不抛异常）。</summary>
        public static FrameEvent Inspect(byte[] raw, string line, ByteOrder order = ByteOrder.BigEndian)
        {
            var result = new FrameEvent();
            result.Raw = raw;
            result.Line = line;

            if (raw == null || raw.Length == 0)
            {
                result.ParseError = "空帧";
                return result;
            }

            result.CommandId = raw[0];
            result.Sequence = raw.Length > 1 ? raw[1] : (byte)0;

            try
            {
                result.Parsed = Rtl8239ResponseParser.Parse(raw, order);
            }
            catch (Exception ex)
            {
                // 解析失败属于「数据」而不是「异常」：记下原因继续跑
                result.ParseError = ex.Message;
            }
            return result;
        }

        // -----------------------------------------------------------------
        //  读循环
        // -----------------------------------------------------------------

        /// <summary>
        /// 读循环。令牌在 <see cref="Open"/> 里创建时就被捕获进来，循环体只认它自己这一代；
        /// 绝不去读 _readCts 字段——那正是「关—开」后旧循环赖着不走的成因。
        /// </summary>
        private void ReadLoop(CancellationToken token)
        {
            var buffer = new byte[ReadBufferSize];
            int idleReads = 0;

            while (true)
            {
                if (token.IsCancellationRequested)
                    return;

                int count;
                try
                {
                    count = _transport.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                        return;      // 我们自己关的串口，不算故障

                    _isOpen = false;
                    RaiseFault(SerialFaultKind.DeviceRemoved, "读串口失败（设备可能已断开）：" + ex.Message, ex);
                    return;
                }

                if (count < 0)
                {
                    // 传输层报告「端口已关闭」。这里**绝不能 continue**：端口关闭时的读取是
                    // 立刻返回的（不走超时），继续重试就是满核空转——那个 100% CPU 的来源。
                    if (token.IsCancellationRequested)
                        return;      // 我们自己关的，正常收尾

                    _isOpen = false;
                    RaiseFault(SerialFaultKind.DeviceRemoved, "串口已关闭（设备可能已断开）。", null);
                    return;
                }

                if (count == 0)
                {
                    idleReads++;
                    // 设备最后一行可能没有换行符：连续空闲之后把残行也吐出去
                    if (idleReads >= IdleReadsBeforeFlush)
                        FlushPendingLine();
                    continue;
                }

                idleReads = 0;
                var chunk = new byte[count];
                Buffer.BlockCopy(buffer, 0, chunk, 0, count);

                Action<byte[]> bytesHandler = BytesReceived;
                if (bytesHandler != null)
                    bytesHandler(chunk);

                if (ReceiveMode == ReceiveMode.RawFrames)
                {
                    IList<byte[]> frames = _scanner.Append(chunk, count);
                    for (int i = 0; i < frames.Count; i++)
                        HandleFrame(Inspect(frames[i], "(裸帧)", ByteOrder));
                    continue;
                }

                IList<string> lines;
                lock (_assemblerSync)
                {
                    lines = _assembler.Append(chunk, count);
                }

                for (int i = 0; i < lines.Count; i++)
                    HandleLine(lines[i]);
            }
        }

        private void FlushPendingLine()
        {
            if (ReceiveMode == ReceiveMode.RawFrames)
                return;

            string residue;
            lock (_assemblerSync)
            {
                residue = _assembler.Flush();
            }
            if (!string.IsNullOrEmpty(residue))
                HandleLine(residue);
        }

        private void HandleLine(string line)
        {
            Action<string> lineHandler = LineReceived;
            if (lineHandler != null)
                lineHandler(line);

            byte[] raw;
            string why;
            if (!Extractor.TryExtract(line, out raw, out why))
            {
                // why 非 null 说明这一行看着像响应但没被采纳——这种事值得说一声，
                // 否则识别规则写错时界面上一片安静，根本查不出来
                if (!string.IsNullOrEmpty(why))
                {
                    Action<string, string> rejected = LineRejected;
                    if (rejected != null)
                        rejected(line, why);
                }
                return;
            }

            HandleFrame(Inspect(raw, line, ByteOrder));
        }

        private void HandleFrame(FrameEvent frameEvent)
        {
            if (IsSelfEcho(frameEvent.Raw))
            {
                // 设备把刚发出去的请求原样回送（本地回显 / 半双工适配器）。它与我们的请求
                // 逐字节相同、校验和同样正确，命令 ID 和序列号也都对得上——绝不能当成响应。
                Interlocked.Increment(ref _selfEchoIgnored);
                return;
            }

            lock (_sync)
            {
                Waiter match = null;
                for (int i = 0; i < _waiters.Count; i++)
                {
                    if (Matches(frameEvent.Raw, _waiters[i].CommandId, _waiters[i].Sequence))
                    {
                        match = _waiters[i];
                        break;
                    }
                }

                if (match != null)
                {
                    _waiters.Remove(match);
                    // 等待者可能已经超时/取消：那时 TrySetResult 会失败，
                    // 这一帧不能就此丢掉，要按「没有等待者」处理，进缓存。
                    if (!match.Completion.TrySetResult(frameEvent))
                        match = null;
                }

                if (match == null)
                {
                    _recentFrames.Enqueue(frameEvent);
                    while (_recentFrames.Count > RecentFrameCacheSize)
                        _recentFrames.Dequeue();
                }
            }

            Action<FrameEvent> handler = FrameReceived;
            if (handler != null)
                handler(frameEvent);
        }

        private void RememberSent(byte[] frame)
        {
            if (frame == null || frame.Length == 0)
                return;

            var copy = (byte[])frame.Clone();
            lock (_sync)
            {
                if (_recentSentFrames.Count >= RecentSentCount)
                    _recentSentFrames.RemoveAt(0);
                _recentSentFrames.Add(copy);
            }
        }

        private bool IsSelfEcho(byte[] raw)
        {
            if (raw == null || raw.Length == 0)
                return false;

            lock (_sync)
            {
                for (int i = 0; i < _recentSentFrames.Count; i++)
                {
                    if (FramesEqual(_recentSentFrames[i], raw))
                        return true;
                }
            }
            return false;
        }

        private static bool FramesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }

        private FrameEvent TakeRecentLocked(byte commandId, byte sequence)
        {
            if (_recentFrames.Count == 0)
                return null;

            var kept = new Queue<FrameEvent>(_recentFrames.Count);
            FrameEvent found = null;
            while (_recentFrames.Count > 0)
            {
                FrameEvent candidate = _recentFrames.Dequeue();
                if (found == null && Matches(candidate.Raw, commandId, sequence))
                    found = candidate;
                else
                    kept.Enqueue(candidate);
            }
            while (kept.Count > 0)
                _recentFrames.Enqueue(kept.Dequeue());
            return found;
        }

        private void CancelPendingWaiters(string reason)
        {
            List<Waiter> pending;
            lock (_sync)
            {
                pending = new List<Waiter>(_waiters);
                _waiters.Clear();
                _recentFrames.Clear();
            }

            for (int i = 0; i < pending.Count; i++)
                pending[i].Completion.TrySetException(new InvalidOperationException(reason));
        }

        private void RaiseFault(SerialFaultKind kind, string message, Exception exception)
        {
            Action<SerialFault> handler = Fault;
            if (handler == null)
                return;

            var fault = new SerialFault { Kind = kind, Message = message, Exception = exception };
            handler(fault);
        }
    }
}
