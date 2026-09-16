using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1.Serial
{
    /// <summary>设备信息面板里的一行「标签 : 值」。</summary>
    public sealed class DeviceInfoField
    {
        public string Label { get; set; }
        public string Value { get; set; }

        public DeviceInfoField(string label, string value)
        {
            Label = label;
            Value = value;
        }
    }

    /// <summary>一组设备信息的读取结果，对应一条查询命令。</summary>
    public sealed class DeviceInfoEntry
    {
        public string CommandKey { get; set; }
        public string Title { get; set; }

        /// <summary>本组是否完整读回。<see cref="Error"/> 非空时这里为 false，但 Fields 里仍可能有部分结果。</summary>
        public bool Ok { get; set; }
        public string Error { get; set; }
        public List<DeviceInfoField> Fields { get; set; }

        public DeviceInfoEntry()
        {
            Fields = new List<DeviceInfoField>();
        }
    }

    /// <summary>一次「更新」的完整结果。</summary>
    public sealed class DeviceInfoResult
    {
        public IList<DeviceInfoEntry> Entries { get; private set; }
        public int OkCount { get; private set; }
        public int TotalCount { get; private set; }

        /// <summary>如「本次更新：5/6 成功」。逐条独立结算，不因为一条失败就否定整块。</summary>
        public string Summary { get; private set; }

        public DeviceInfoResult(IList<DeviceInfoEntry> entries)
        {
            Entries = entries ?? new List<DeviceInfoEntry>();
            TotalCount = Entries.Count;

            int ok = 0;
            for (int i = 0; i < Entries.Count; i++)
                if (Entries[i].Ok)
                    ok++;

            OkCount = ok;
            Summary = string.Format(CultureInfo.InvariantCulture,
                "本次更新：{0}/{1} 成功", ok, Entries.Count);
        }
    }

    /// <summary>
    /// 一次性地读一批「身份 / 诊断」类查询命令，并把结果整理成可显示的「标签 : 值」。
    ///
    /// 这些命令**不进轮询**：它们回答的是「这台设备是什么、上次为什么重启」这类不变或低频的问题，
    /// 周期性重读只会白占串口带宽。因此这里只提供 <see cref="ReadAllAsync"/>，由界面上的
    /// 「更新」按钮或「串口刚打开」这两个时机各调一次。
    ///
    /// 这里一行业都不知道 WPF 的存在：结果模型与格式化都是纯 C#，可以在 Linux 上编译并断言。
    /// 依赖 <see cref="SerialSession"/> 而不是 System.IO.Ports，所以整条链路可以用假传输层测试。
    /// </summary>
    public sealed class DeviceInfoReader
    {
        /// <summary>0x4C 的索引上限（手册：有效 0x00-0x0B）。</summary>
        public const int MaxAddressIndex = 0x0B;

        /// <summary>0x4C 每个索引返回的地址个数。</summary>
        public const int AddressesPerBlock = 8;

        /// <summary>0x47 的清除标志：固定 0x00 = 读后**不**清除。</summary>
        private const long ResetReasonClearFlag = 0x00;

        /// <summary>0x4C 的命令号，用来把它从第一趟读取里摘出来单独处理。</summary>
        private const byte AddressCommandId = 0x4C;

        /// <summary>0x47 里表示「该芯片访问正常」的半字节，正常的不逐颗列出来，免得 12 行噪音盖住异常项。</summary>
        private const byte ChipAccessNormalNibble = 0x0F;

        /// <summary>协议里表示「该槽位没有芯片」的填充值。它是内部约定，不该原样给人看。</summary>
        public const string AbsentPlaceholder = "—";

        private sealed class InfoCommand
        {
            public string Key;
            public string Title;

            /// <summary>回包 Byte0，用于请求-响应配对。</summary>
            public byte CommandId;

            /// <summary>0xC0 系的子命令（回包 Byte1）；其它命令为 0，表示「无子命令」。</summary>
            public byte SubCommand;
        }

        /// <summary>
        /// 要读的命令，顺序即**显示**顺序。
        ///
        /// 身份信息（命令号、子命令）全部从 <see cref="CommandOwnership"/> 的键名推导，
        /// 不另写一份——两份手工维护的同一事实早晚会漂移，而漂移的后果是
        /// 「用一条命令造的帧、等另一条命令的回包」，表现成必然超时。
        ///
        /// 注意这个数组**不再**兼任执行顺序：0x4C 要读几块取决于 0x50，见 <see cref="ReadAllAsync"/>。
        /// </summary>
        private static readonly InfoCommand[] Commands = BuildCommands();

        private static InfoCommand[] BuildCommands()
        {
            CommandOwner[] owners = CommandOwnership.DeviceInfoPanel;
            var commands = new InfoCommand[owners.Length];

            for (int i = 0; i < owners.Length; i++)
            {
                string key = owners[i].Key;
                int dash = key.IndexOf('-');

                var command = new InfoCommand();
                command.Key = key;
                command.Title = owners[i].Title;
                command.CommandId = (byte)Rtl8239Catalog.ParseNumber(dash < 0 ? key : key.Substring(0, dash));
                command.SubCommand = dash < 0 ? (byte)0 : (byte)Rtl8239Catalog.ParseNumber(key.Substring(dash + 1));
                commands[i] = command;
            }

            return commands;
        }

        /// <summary>把命令键名交给调用方（轮询列表按它排除这些命令）。</summary>
        public static bool IsCovered(string key)
        {
            return CommandOwnership.OwnedByDeviceInfoPanel(key);
        }

        private readonly SerialSession _session;
        private readonly SemaphoreSlim _readGate = new SemaphoreSlim(1, 1);
        private byte _sequence;

        /// <summary>
        /// 进度回调：(已开始第几组, 总组数, 组名)。
        ///
        /// ⚠️ **在哪个线程上触发是不保证的**：本类内部一路 ConfigureAwait(false)，
        /// 所以除了第一次（还在调用者的线程上），其余都在线程池线程上触发。
        /// 回调里要碰界面控件的话，必须自己封送到 UI 线程。
        ///
        /// 本类**不承诺**在返回前额外报一次「已完成」——调用方拿到的 Task 完成本身就是那个信号，
        /// 别在这里再补一次（那会让最后写入的状态依赖两个线程的先后顺序）。
        /// </summary>
        public Action<int, int, string> Progress { get; set; }

        public DeviceInfoReader(SerialSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            _session = session;
        }

        /// <summary>
        /// 依次读完全部命令。单条失败不中断整块——已经在手的其它结果照常返回，
        /// 「这条命令在设备上读不通」本身就是一个有用的诊断结论，不该被抹平成一句「读取失败」。
        /// 只有取消会中断整块（向上抛 <see cref="OperationCanceledException"/>）。
        ///
        /// 本方法不是可重入的（内部有个无锁的序列号计数器），所以自己用信号量守住单飞，
        /// 而不是把这个约束留给调用方——约束是这里产生的，就该在这里兑现。
        /// 并发调用会排队，不会出错。
        /// </summary>
        public async Task<DeviceInfoResult> ReadAllAsync(SendOptions options, int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException("options");

            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ReadAllCoreAsync(options, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _readGate.Release();
            }
        }

        private async Task<DeviceInfoResult> ReadAllCoreAsync(SendOptions options, int timeoutMs,
            CancellationToken cancellationToken)
        {
            // 结果先按显示位置落座，最后一趟扫的时候读取顺序就和显示顺序无关了
            var slots = new DeviceInfoEntry[Commands.Length];

            // 第一趟：除 0x4C 以外的全部命令。
            //
            // 0x4C 被摘出去，是因为它要读几块取决于 0x50 报出来的芯片占用情况。
            // 这条依赖**不能**靠「它在数组里排在 0x50 后面」来维系：谁要是觉得
            // 「按命令号排个序更整齐」，0x4C 就会静默地只读到第一块——而且那时的提示
            // 还会把「还没跑」诊断成「跑失败了」。写成两趟，顺序就变不坏了。
            int addressSlot = -1;
            int? chipCount = null;

            for (int i = 0; i < Commands.Length; i++)
            {
                InfoCommand command = Commands[i];
                if (command.CommandId == AddressCommandId)
                {
                    addressSlot = i;
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(i, command.Title);

                SingleRead read = await ReadOneAsync(command, options, timeoutMs, cancellationToken)
                    .ConfigureAwait(false);

                if (read.Parsed is SystemChipTypeInfo)
                    chipCount = ChipCountOf((SystemChipTypeInfo)read.Parsed);

                slots[i] = read.Entry;
            }

            // 第二趟：0x4C，用第一趟拿到的芯片数推算要读几块
            if (addressSlot >= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(addressSlot, Commands[addressSlot].Title);

                slots[addressSlot] = await ReadDeviceAddressesAsync(
                    Commands[addressSlot], options, timeoutMs, chipCount, cancellationToken).ConfigureAwait(false);
            }

            var entries = new List<DeviceInfoEntry>(Commands.Length);
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != null)
                    entries.Add(slots[i]);

            return new DeviceInfoResult(entries);
        }

        private void ReportProgress(int done, string title)
        {
            Action<int, int, string> handler = Progress;
            if (handler != null)
                handler(done, Commands.Length, title);
        }

        // =================================================================
        //  单条命令
        // =================================================================

        /// <summary>一次单命令读取：给调用方看的条目，以及只在内部分发的原始结果对象。</summary>
        private struct SingleRead
        {
            public DeviceInfoEntry Entry;
            public object Parsed;
        }

        private async Task<SingleRead> ReadOneAsync(InfoCommand cmd, SendOptions options,
            int timeoutMs, CancellationToken cancellationToken)
        {
            long[] values = FieldValues(cmd.Key);

            // 0x47 的 Byte1 是清除标志，非零会**抹掉设备上的复位记录**。
            // 本面板是只读展示，这里固定发 0x00（不清除）——整个文件唯一一处
            // 不能照抄目录默认值的地方，改之前先想清楚会毁掉什么。
            //
            // 字段定义缺失时**显式失败**而不是放过去：放过去就等于拿着空参数数组去调
            // GlobalResetReasonGet(seq, v[0])，清除标志最后是什么值全看目录怎么越界，
            // 而那个值会被真的写进设备。这里宁可什么都不发。
            if (cmd.CommandId == 0x47)
            {
                if (values.Length != 1)
                    return Failure(cmd, "0x47 的参数定义异常，为安全起见不发送"
                        + "（清除标志必须显式为 0x00，否则会抹掉设备上的复位记录）");

                values[0] = ResetReasonClearFlag;
            }

            QueryOutcome outcome = await QueryAsync(cmd, options, timeoutMs, values, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Ok)
                return Failure(cmd, outcome.Error + EmptyResponseHint(cmd.CommandId, outcome.TimedOut));

            var read = new SingleRead();
            read.Parsed = outcome.Parsed;
            read.Entry = Succeeded(cmd, outcome.Parsed);
            return read;
        }

        private static SingleRead Failure(InfoCommand cmd, string error)
        {
            var entry = new DeviceInfoEntry { CommandKey = cmd.Key, Title = cmd.Title, Ok = false, Error = error };
            entry.Fields.Add(new DeviceInfoField(cmd.Title, AbsentPlaceholder));

            var read = new SingleRead();
            read.Entry = entry;
            return read;
        }

        /// <summary>
        /// 0x50 里最后一颗有内容的芯片之后，就都是没插芯片的槽位了，据此得出芯片总数。
        /// </summary>
        private static int ChipCountOf(SystemChipTypeInfo info)
        {
            PseChipTypeEntry[] chips = info.Chips ?? new PseChipTypeEntry[0];

            int last = -1;
            for (int i = 0; i < chips.Length; i++)
                if (chips[i].Type != PseChipType.Reserved)
                    last = i;

            return last + 1;
        }

        /// <summary>
        /// 0x4C 一共要读几块。<paramref name="chipCount"/> 为 null 表示 0x50 没读成。
        /// </summary>
        private static int BlockCountFor(int? chipCount)
        {
            if (chipCount == null)
                return 1;       // 不知道有几颗：先读第一块，至少给出一半答案

            if (chipCount.Value <= 0)
                return 0;       // 0x50 说一颗芯片都没有，再问地址就是白问

            int blocks = (chipCount.Value + AddressesPerBlock - 1) / AddressesPerBlock;
            return blocks > MaxAddressIndex + 1 ? MaxAddressIndex + 1 : blocks;
        }

        /// <summary>一块已读回的地址，连同**请求时用的索引**。</summary>
        private struct AddressBlock
        {
            public int RequestedIndex;
            public GlobalDeviceAddress Data;
        }

        /// <summary>
        /// 0x4C 是索引式的：每个索引回 8 个芯片地址。
        ///
        /// **不能**用「一直读，读到一整块全是 0xFF 为止」来找终点：手册规定空缺位填 0xFF，
        /// 而请求的 RSVD 位也是 0xFF、SEQ 与 Idx 又都原样回显，于是**空块的响应与我们的请求
        /// 逐字节相同**，会被 <see cref="SerialSession"/> 的自回显抑制当成回显丢掉，表现为超时。
        /// 也就是说「读到空块为止」在真机上根本走不通——每台芯片数不是 8 的整数倍的设备
        /// 都会在最后一步超时。
        ///
        /// 所以改成**不问已知是空的块**：用 0x50 读到的芯片占用情况反推需要读几块，
        /// 正常路径上一次空块都不会问到。
        /// </summary>
        private async Task<DeviceInfoEntry> ReadDeviceAddressesAsync(InfoCommand cmd, SendOptions options,
            int timeoutMs, int? chipCount, CancellationToken cancellationToken)
        {
            var blocks = new List<AddressBlock>();
            string error = null;

            int blocksNeeded = BlockCountFor(chipCount);
            for (int idx = 0; idx < blocksNeeded; idx++)
            {
                long[] values = FieldValues(cmd.Key);
                if (values.Length > 0)
                    values[0] = idx;

                QueryOutcome outcome = await QueryAsync(cmd, options, timeoutMs, values, cancellationToken)
                    .ConfigureAwait(false);

                if (!outcome.Ok)
                {
                    // 这一块没读成，但**不要就此收手**：芯片编号可能有断层（0x50 报第 8 槽有芯片、
                    // 0~7 槽是保留值），后面那块恰好是有数据的。块数已被 chipCount 封顶，接着读不会失控。
                    // 只留第一条错误——它是根因，后面的多半是它的连带。
                    if (error == null)
                        error = string.Format(CultureInfo.InvariantCulture,
                            "索引 0x{0:X2} 读取失败：{1}{2}", idx, outcome.Error, EmptyBlockHint(outcome.TimedOut));
                    continue;
                }

                var data = (GlobalDeviceAddress)outcome.Parsed;

                // 与 0x50 报的数量对不上：跳过这一块，但继续看后面的
                if (data.PresentCount == 0)
                    continue;

                var block = new AddressBlock();
                block.RequestedIndex = idx;
                block.Data = data;
                blocks.Add(block);
            }

            var entry = new DeviceInfoEntry { CommandKey = cmd.Key, Title = cmd.Title, Ok = error == null, Error = error };
            AppendChipAddresses(entry, blocks, chipCount);
            return entry;
        }

        private sealed class QueryOutcome
        {
            public bool Ok;
            public bool TimedOut;
            public object Parsed;
            public string Error;
        }

        /// <summary>
        /// 发一条命令并等它的响应。整对操作持有事务锁，所以不会和轮询、
        /// 或本面板自己的前一条命令在串口上交错。
        /// </summary>
        private async Task<QueryOutcome> QueryAsync(InfoCommand cmd, SendOptions options, int timeoutMs,
            long[] values, CancellationToken cancellationToken)
        {
            try
            {
                CommandDef definition = FindCommand(cmd.Key);
                if (definition == null)
                    return new QueryOutcome { Ok = false, Error = "命令目录里找不到 " + cmd.Key };

                _sequence = PollingScheduler.NextSequence(_sequence);
                byte sequence = _sequence;
                byte[] frame = Rtl8239Catalog.BuildChecked(definition, sequence, values);

                using (SerialTransaction transaction =
                    await _session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                {
                    await _session.SendAsync(options.For(frame), cancellationToken).ConfigureAwait(false);

                    FrameEvent frameEvent = await _session.WaitForFrameAsync(cmd.CommandId, sequence,
                        TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false);

                    string mismatch = DescribeSubCommandMismatch(cmd, frameEvent);
                    if (mismatch != null)
                        return new QueryOutcome { Ok = false, Error = mismatch };

                    if (!frameEvent.IsParsed)
                        return new QueryOutcome
                        {
                            Ok = false,
                            Error = "响应解析失败：" + (frameEvent.ParseError ?? "未知原因"),
                        };

                    return new QueryOutcome { Ok = true, Parsed = frameEvent.Parsed };
                }
            }
            catch (OperationCanceledException)
            {
                throw;      // 取消要中断整块，不能被当成「这一条失败」
            }
            catch (TimeoutException)
            {
                return new QueryOutcome { Ok = false, TimedOut = true, Error = "等待响应超时" };
            }
            catch (Exception ex)
            {
                return new QueryOutcome { Ok = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// 0xC0 系的命令只按命令 ID 配对（<see cref="SerialSession.IsSequenceCorrelatable"/> 为 false），
        /// 所以**任何** 0xC0 帧都能满足那个等待——手工帧的回包、上一次超时之后才到的回包，都可能被认领。
        /// 这里按子命令再挡一道：宁可这一条明确失败，也不能把别人的数据当成自己的结果显示出来，
        /// 那正是这块面板最不该犯的错。
        /// </summary>
        private static string DescribeSubCommandMismatch(InfoCommand cmd, FrameEvent frameEvent)
        {
            if (SerialSession.IsSequenceCorrelatable(cmd.CommandId))
                return null;        // 别的命令已经按序列号配过对了

            if (frameEvent.Raw == null || frameEvent.Raw.Length < 2 || frameEvent.Raw[1] == cmd.SubCommand)
                return null;

            return string.Format(CultureInfo.InvariantCulture,
                "收到的不是 {0} 的响应（子命令 0x{1:X2}，期望 0x{2:X2}）——未采纳，免得把别人的数据当成自己的",
                cmd.Key, frameEvent.Raw[1], cmd.SubCommand);
        }

        /// <summary>
        /// 请求帧里被填成 0xFF 的「保留位」、以及协议规定空缺处填 0xFF 的响应字段，
        /// 会让**某些「全空」的响应与请求逐字节相同**——SEQ 原样回显、校验和随之相同，
        /// 于是被 <see cref="SerialSession"/> 的自回显抑制当成回显丢掉，表现为超时。
        ///
        /// 后果是：这些命令的「设备上什么都没有」和「链路断了」**在观测上无法区分**。
        /// 所以超时时必须把这个歧义说出来，不能让用户以为一定是链路问题。
        /// </summary>
        private static string EmptyResponseHint(byte commandId, bool timedOut)
        {
            if (!timedOut)
                return string.Empty;

            switch (commandId)
            {
                case 0x50:
                    return "（12 个槽位都没有芯片应答时，响应与请求逐字节相同，会被当成回显丢掉——"
                         + "所以「超时」本身就可能是「一颗芯片都没有」，不一定是链路断了）";
                case 0xC0:
                    return "（设备从未保存过配置时，响应与请求逐字节相同，会被当成回显丢掉——"
                         + "所以「超时」本身就可能是「没有配置版本」，不一定是链路断了）";
                case 0x4C:
                    return "（该块可能整个是空的——空块的响应与请求逐字节相同，会被当成回显丢掉；"
                         + "拿 0x50 的芯片数核对一下）";
                default:
                    return string.Empty;
            }
        }

        private static string EmptyBlockHint(bool timedOut)
        {
            return EmptyResponseHint(0x4C, timedOut);
        }

        private static CommandDef FindCommand(string key)
        {
            List<CommandDef> all = Rtl8239Catalog.All;
            for (int i = 0; i < all.Count; i++)
                if (string.Equals(all[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return all[i];

            return null;
        }

        /// <summary>按目录里各字段的默认值取一份参数值；只有 0x4C 的索引会在调用处覆盖。</summary>
        private static long[] FieldValues(string key)
        {
            CommandDef definition = FindCommand(key);
            FieldDef[] fields = definition == null ? null : definition.Fields;
            if (fields == null)
                return new long[0];

            var values = new long[fields.Length];
            for (int i = 0; i < fields.Length; i++)
                values[i] = fields[i].DefaultValue;

            return values;
        }

        // =================================================================
        //  结果 → 可显示的「标签 : 值」
        // =================================================================

        private static DeviceInfoEntry Succeeded(InfoCommand cmd, object parsed)
        {
            var entry = new DeviceInfoEntry { CommandKey = cmd.Key, Title = cmd.Title, Ok = true };
            if (AppendParsed(entry, parsed))
                return entry;

            // 解析对象和命令对不上：**绝不报成功**。一条「看起来成功了、其实不是这条命令的结果」
            // 比一条明确的失败有害得多——这块面板存在的意义就是让人相信屏幕上的东西。
            return Failed(cmd, "响应类型与命令不符（得到 "
                + (parsed == null ? "null" : parsed.GetType().Name) + "）");
        }

        private static bool AppendParsed(DeviceInfoEntry entry, object parsed)
        {
            if (parsed is GlobalStatus)
            {
                AppendGlobalStatus(entry, (GlobalStatus)parsed);
                return true;
            }

            if (parsed is ConfigurationVersionInfo)
            {
                AppendConfigurationVersion(entry, (ConfigurationVersionInfo)parsed);
                return true;
            }

            if (parsed is SystemChipTypeInfo)
            {
                AppendChipTypes(entry, (SystemChipTypeInfo)parsed);
                return true;
            }

            if (parsed is GlobalParameters)
            {
                AppendGlobalParameters(entry, (GlobalParameters)parsed);
                return true;
            }

            if (parsed is GlobalResetReason)
            {
                AppendResetReason(entry, (GlobalResetReason)parsed);
                return true;
            }

            return false;
        }

        private static DeviceInfoEntry Failed(InfoCommand cmd, string error)
        {
            var entry = new DeviceInfoEntry { CommandKey = cmd.Key, Title = cmd.Title, Ok = false, Error = error };
            entry.Fields.Add(new DeviceInfoField(cmd.Title, AbsentPlaceholder));
            return entry;
        }

        /// <summary>0x40：设备 ID、版本、MCU、端口数与配置状态。</summary>
        private static void AppendGlobalStatus(DeviceInfoEntry entry, GlobalStatus status)
        {
            entry.Fields.Add(new DeviceInfoField("设备 ID",
                status.DeviceIdDescription + "  (0x" + status.DeviceId.ToString("X4", CultureInfo.InvariantCulture) + ")"));
            entry.Fields.Add(new DeviceInfoField("软件版本", status.SwVersion));
            entry.Fields.Add(new DeviceInfoField("扩展版本", status.ExtVersion));
            entry.Fields.Add(new DeviceInfoField("MCU 类型", status.McuTypeDescription));
            entry.Fields.Add(new DeviceInfoField("最大端口数", Number(status.MaxPorts)));
            entry.Fields.Add(new DeviceInfoField("端口映射",
                (status.PortMapEnabled ? "使能" : "未使能") + "  (" + Hex(status.PortMap) + ")"));

            var parts = new List<string>();
            parts.Add(status.ConfigSaved ? "配置已保存" : "配置未保存");
            if (status.SystemResetOccurred) parts.Add("发生过系统复位");
            if (status.GlobalDisablePinHigh) parts.Add("全局禁用脚为高");
            entry.Fields.Add(new DeviceInfoField("配置状态",
                string.Join("，", parts.ToArray()) + "  (" + Hex(status.ConfigStatus) + ")"));
        }

        /// <summary>0xC0-04：配置保存时写进去的日期与版本。</summary>
        private static void AppendConfigurationVersion(DeviceInfoEntry entry, ConfigurationVersionInfo info)
        {
            bool blank = info.Year == 0xFF && info.Month == 0xFF && info.Day == 0xFF;
            entry.Fields.Add(new DeviceInfoField("配置日期", blank ? AbsentPlaceholder : info.DateText));

            bool noVersion = info.HighVersion == 0xFF && info.LowVersion == 0xFF;
            entry.Fields.Add(new DeviceInfoField("配置版本", noVersion ? AbsentPlaceholder : info.VersionText));
        }

        /// <summary>
        /// 0x50：每颗芯片是 BT 还是 AT。响应固定回 12 个槽位，没插芯片的槽位填「保留」——
        /// 那是没装芯片的意思，不是查询结果，所以只显示到最后一颗有内容的芯片为止
        /// （中间的空槽仍然照实显示，免得看不出编号断层）。
        /// </summary>
        private static void AppendChipTypes(DeviceInfoEntry entry, SystemChipTypeInfo info)
        {
            PseChipTypeEntry[] chips = info.Chips ?? new PseChipTypeEntry[0];
            int count = ChipCountOf(info);

            if (count <= 0)
            {
                entry.Fields.Add(new DeviceInfoField("芯片", AbsentPlaceholder + "（12 个槽位都没有芯片应答）"));
                return;
            }

            for (int i = 0; i < count; i++)
                entry.Fields.Add(new DeviceInfoField("芯片 #" + Number(i), chips[i].TypeDescription));
        }

        /// <summary>
        /// 0x4C：芯片的 I2C 地址。用全局芯片编号做标签，才能和 0x50 的「芯片 #N」对得上。
        ///
        /// 编号按**请求时用的索引**算，不用设备回显的 Idx：回显值不在 0..0x0B 时
        /// （坏设备、或帧被张冠李戴），用它算出来的编号会离谱到没有意义。
        /// </summary>
        private static void AppendChipAddresses(DeviceInfoEntry entry, List<AddressBlock> blocks, int? chipCount)
        {
            if (chipCount.HasValue && chipCount.Value == 0)
            {
                entry.Fields.Add(new DeviceInfoField("芯片地址", AbsentPlaceholder + "（0x50 报告没有芯片）"));
                return;
            }

            int present = 0;
            for (int b = 0; b < blocks.Count; b++)
            {
                AddressBlock block = blocks[b];
                byte[] addresses = block.Data.Addresses ?? new byte[0];

                for (int i = 0; i < addresses.Length; i++)
                {
                    byte address = addresses[i];
                    bool hasChip = address != 0xFF;
                    if (hasChip)
                        present++;

                    entry.Fields.Add(new DeviceInfoField(
                        "芯片 #" + Number(block.RequestedIndex * AddressesPerBlock + i),
                        hasChip ? Hex(address) : AbsentPlaceholder));
                }
            }

            if (blocks.Count == 0)
                entry.Fields.Add(new DeviceInfoField("芯片地址", AbsentPlaceholder));
            else
                entry.Fields.Add(new DeviceInfoField("已检出芯片数", Number(present)));

            // 两边对不上就照实说出来，不替用户裁决谁对——这正是这块诊断面板存在的意义。
            // 注意这一句要在 blocks 为空时也能走到：一块都没读成、chipCount 却不为零，
            // 恰恰是最该说明的情况。
            if (chipCount.HasValue)
            {
                if (present != chipCount.Value)
                    entry.Fields.Add(new DeviceInfoField("注意", string.Format(CultureInfo.InvariantCulture,
                        "0x50 说有 {0} 颗芯片，这里读到 {1} 颗地址", chipCount.Value, present)));
                return;
            }

            // 0x50 没读成，所以只读了第一块；第一块是满的说明后面可能还有芯片，
            // 而再往后问的那一次正好会撞上「空块的响应与请求逐字节相同」那个坑，所以不追问。
            if (blocks.Count > 0 && blocks[blocks.Count - 1].Data.PresentCount == AddressesPerBlock)
                entry.Fields.Add(new DeviceInfoField("注意", "0x50 没读成，只读了第一块且已满，后面可能还有芯片"));
        }

        /// <summary>0x4A：UVLO/OVLO 门限与芯片数量。</summary>
        private static void AppendGlobalParameters(DeviceInfoEntry entry, GlobalParameters parameters)
        {
            entry.Fields.Add(new DeviceInfoField("UVLO 门限", Volt(parameters.UvloVolt, parameters.UvloRaw)));
            entry.Fields.Add(new DeviceInfoField("OVLO 门限", Volt(parameters.OvloVolt, parameters.OvloRaw)));
            entry.Fields.Add(new DeviceInfoField("预分配",
                (parameters.PreAllocEnabled ? "使能" : "未使能") + "  (" + Hex(parameters.PreAlloc) + ")"));
            entry.Fields.Add(new DeviceInfoField("PSE 芯片数", Number(parameters.ChipNumber)));
            entry.Fields.Add(new DeviceInfoField("不支持的芯片数", Number(parameters.NotSupportedChip)));
        }

        /// <summary>0x47：上次为什么复位，以及哪些芯片的 I2C 访问不正常。</summary>
        private static void AppendResetReason(DeviceInfoEntry entry, GlobalResetReason reason)
        {
            entry.Fields.Add(new DeviceInfoField("复位原因", reason.ReasonDescription));
            entry.Fields.Add(new DeviceInfoField("芯片接口", reason.AllInterfacesOk ? "全部正常" : "有异常"));
            entry.Fields.Add(new DeviceInfoField("芯片复位", reason.ChipResetStatus ? "发生过" : "未发生"));

            ChipPairError[] pairs = reason.ErrorAddrPairs ?? new ChipPairError[0];
            int abnormal = 0;
            for (int p = 0; p < pairs.Length; p++)
            {
                int first = p * 2;
                if (pairs[p].Chip0Nibble != ChipAccessNormalNibble)
                {
                    abnormal++;
                    entry.Fields.Add(new DeviceInfoField("芯片 #" + Number(first), pairs[p].Chip0Description));
                }

                if (pairs[p].Chip1Nibble != ChipAccessNormalNibble)
                {
                    abnormal++;
                    entry.Fields.Add(new DeviceInfoField("芯片 #" + Number(first + 1), pairs[p].Chip1Description));
                }
            }

            if (abnormal == 0)
                entry.Fields.Add(new DeviceInfoField("异常芯片", "无"));
        }

        // =================================================================
        //  格式化
        // =================================================================

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Hex(byte value)
        {
            return "0x" + value.ToString("X2", CultureInfo.InvariantCulture);
        }

        private static string Volt(double volts, byte raw)
        {
            return volts.ToString("F2", CultureInfo.InvariantCulture) + " V  (" + Hex(raw) + ")";
        }
    }
}
