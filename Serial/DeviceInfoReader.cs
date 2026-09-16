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

        /// <summary>
        /// 解析出来的原始结果对象（<see cref="Rtl8239ResponseParser"/> 的某个结构体）。
        /// 只用来在组之间传递事实（例如「有几颗芯片」），不参与显示。
        /// </summary>
        public object Parsed { get; set; }

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

        /// <summary>0x47 里表示「该芯片访问正常」的半字节，正常的不逐颗列出来，免得 12 行噪音盖住异常项。</summary>
        private const byte ChipAccessNormalNibble = 0x0F;

        /// <summary>协议里表示「该槽位没有芯片」的填充值。它是内部约定，不该原样给人看。</summary>
        public const string AbsentPlaceholder = "—";

        private sealed class InfoCommand
        {
            public string Key;          // 目录里的键
            public byte CommandId;      // 回包 Byte0，用于请求-响应配对
            public string Title;
        }

        /// <summary>要读的命令，顺序即显示顺序。</summary>
        private static readonly InfoCommand[] Commands =
        {
            new InfoCommand { Key = "0x40",    CommandId = 0x40, Title = "设备身份" },
            new InfoCommand { Key = "0xC0-04", CommandId = 0xC0, Title = "配置版本" },
            new InfoCommand { Key = "0x50",    CommandId = 0x50, Title = "芯片类型" },
            new InfoCommand { Key = "0x4C",    CommandId = 0x4C, Title = "芯片地址" },
            new InfoCommand { Key = "0x4A",    CommandId = 0x4A, Title = "全局参数" },
            new InfoCommand { Key = "0x47",    CommandId = 0x47, Title = "复位原因" },
        };

        /// <summary>
        /// 本面板是否接管了这条命令。轮询列表要用它排除——同一条命令出现在两个列表里、
        /// 各自刷新、各自显示，屏幕上就会有两份时机不同的「同一个值」。
        ///
        /// 直接扫 <see cref="Commands"/>，不另抄一份键名清单：两份手维护的同一事实早晚会漂移，
        /// 而漂移的后果正好就是这里要防的那件事。
        /// </summary>
        public static bool IsCovered(string key)
        {
            for (int i = 0; i < Commands.Length; i++)
                if (string.Equals(Commands[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private readonly SerialSession _session;
        private byte _sequence;

        /// <summary>
        /// 进度回调：(已开始第几组, 总组数, 组名)。<paramref name="title"/> 为 null 表示全部读完。
        ///
        /// ⚠️ **在哪个线程上触发是不保证的**：本类内部一路 ConfigureAwait(false)，
        /// 所以除了第一次（还在调用者的线程上），其余都在线程池线程上触发。
        /// 回调里要碰界面控件的话，必须自己封送到 UI 线程。
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
        /// </summary>
        public async Task<DeviceInfoResult> ReadAllAsync(SendOptions options, int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (options == null) throw new ArgumentNullException("options");

            var entries = new List<DeviceInfoEntry>();

            // 0x4C 要读几块，取决于 0x50 报出来有几颗芯片——而 0x50 排在它前面，
            // 所以边读边把事实带过去（见 ReadDeviceAddressesAsync 里那段说明）。
            int? chipCount = null;

            for (int i = 0; i < Commands.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(i, Commands[i].Title);

                InfoCommand cmd = Commands[i];
                DeviceInfoEntry entry;

                if (cmd.CommandId == 0x50)
                {
                    entry = await ReadOneAsync(cmd, options, timeoutMs, cancellationToken).ConfigureAwait(false);
                    if (entry.Ok && entry.Parsed is SystemChipTypeInfo)
                        chipCount = ChipCountOf((SystemChipTypeInfo)entry.Parsed);
                }
                else if (cmd.CommandId == 0x4C)
                {
                    entry = await ReadDeviceAddressesAsync(cmd, options, timeoutMs, chipCount, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    entry = await ReadOneAsync(cmd, options, timeoutMs, cancellationToken).ConfigureAwait(false);
                }

                entries.Add(entry);
            }

            ReportProgress(Commands.Length, null);
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

        private async Task<DeviceInfoEntry> ReadOneAsync(InfoCommand cmd, SendOptions options,
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
                    return Failed(cmd, "0x47 的参数定义异常，为安全起见不发送"
                        + "（清除标志必须显式为 0x00，否则会抹掉设备上的复位记录）");

                values[0] = ResetReasonClearFlag;
            }

            QueryOutcome outcome = await QueryAsync(cmd, options, timeoutMs, values, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Ok)
                return Failed(cmd, outcome.Error + EmptyResponseHint(cmd.CommandId, outcome.TimedOut));

            return Succeeded(cmd, outcome.Parsed);
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
            var blocks = new List<GlobalDeviceAddress>();
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

                var block = (GlobalDeviceAddress)outcome.Parsed;

                // 与 0x50 报的数量对不上：跳过这一块，但继续看后面的
                if (block.PresentCount == 0)
                    continue;

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

        private static DeviceInfoEntry Failed(InfoCommand cmd, string error)
        {
            var entry = new DeviceInfoEntry { CommandKey = cmd.Key, Title = cmd.Title, Ok = false, Error = error };
            entry.Fields.Add(new DeviceInfoField(cmd.Title, AbsentPlaceholder));
            return entry;
        }

        private static DeviceInfoEntry Succeeded(InfoCommand cmd, object parsed)
        {
            var entry = new DeviceInfoEntry { CommandKey = cmd.Key, Title = cmd.Title, Ok = true, Parsed = parsed };

            if (parsed is GlobalStatus)
                AppendGlobalStatus(entry, (GlobalStatus)parsed);
            else if (parsed is ConfigurationVersionInfo)
                AppendConfigurationVersion(entry, (ConfigurationVersionInfo)parsed);
            else if (parsed is SystemChipTypeInfo)
                AppendChipTypes(entry, (SystemChipTypeInfo)parsed);
            else if (parsed is GlobalParameters)
                AppendGlobalParameters(entry, (GlobalParameters)parsed);
            else if (parsed is GlobalResetReason)
                AppendResetReason(entry, (GlobalResetReason)parsed);
            else
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
        /// 0x4C：芯片的 I2C 地址。索引 idx 的那一块覆盖芯片 #(idx*8) 到 #(idx*8+7)，
        /// 用全局芯片编号做标签，才能和 0x50 的「芯片 #N」对得上。
        /// </summary>
        private static void AppendChipAddresses(DeviceInfoEntry entry, List<GlobalDeviceAddress> blocks, int? chipCount)
        {
            if (chipCount.HasValue && chipCount.Value == 0)
            {
                entry.Fields.Add(new DeviceInfoField("芯片地址", AbsentPlaceholder + "（0x50 报告没有芯片）"));
                return;
            }

            int present = 0;
            for (int b = 0; b < blocks.Count; b++)
            {
                GlobalDeviceAddress block = blocks[b];
                byte[] addresses = block.Addresses ?? new byte[0];

                for (int i = 0; i < addresses.Length; i++)
                {
                    byte address = addresses[i];
                    bool hasChip = address != 0xFF;
                    if (hasChip)
                        present++;

                    entry.Fields.Add(new DeviceInfoField(
                        "芯片 #" + Number(block.Idx * AddressesPerBlock + i),
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
            if (blocks.Count > 0 && blocks[blocks.Count - 1].PresentCount == AddressesPerBlock)
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
