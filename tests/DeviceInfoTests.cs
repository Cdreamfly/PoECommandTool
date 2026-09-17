using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1;
using WpfApp1.Serial;

namespace Rtl8239Verify
{
    /// <summary>
    /// 「设备信息」面板：0x4C 的分页终止、逐条失败隔离、以及格式化规则。
    ///
    /// 这些断言能跑起来，是因为整块逻辑（DeviceInfoReader）不碰 WPF——
    /// 界面上那层只负责把结果贴进控件，测不到也不值得测。
    /// </summary>
    internal static partial class Program
    {
        // ---------------- 造响应 ----------------

        /// <summary>0x4C 的第 idx 块要回什么：从 <paramref name="blocks"/> 里取一组地址，不足的补 0xFF。</summary>
        private static Func<byte, byte[]> _addressBlocks;

        /// <summary>0x50 报到第几颗芯片为止（含）；-1 表示 12 个槽位全是保留值。</summary>
        private static int _lastPopulatedChip = 2;

        /// <summary>
        /// 造一条 0x50 响应：芯片 #0.._lastPopulatedChip 有内容，其余槽位保留；#2 用 AT 其余用 BT。
        ///
        /// 未使用的字节按手册一律填 0xFF——这不是装饰：若填 0x00，那么 12 槽全保留时的响应
        /// 就与请求（0x50, seq, 0xFF×9）不同了，假传输层不会触发自回显抑制，
        /// 于是测出一个真机上根本不存在的「成功」。
        /// </summary>
        private static byte[] ChipTypeFrame(byte seq)
        {
            var frame = new byte[11];
            for (int i = 0; i < frame.Length; i++)
                frame[i] = 0xFF;

            frame[0] = 0x50;
            frame[1] = seq;      // 槽位 2..7 保持 0xFF = 全保留

            for (int chip = 0; chip <= _lastPopulatedChip && chip < 12; chip++)
            {
                byte value = chip == 2 ? (byte)0x02 : (byte)0x01;
                int index = 2 + chip / 2;
                frame[index] = chip % 2 == 0
                    ? (byte)((value << 4) | (frame[index] & 0x0F))
                    : (byte)((frame[index] & 0xF0) | value);
            }

            return Sign(frame);
        }

        /// <summary>按请求造一条合法响应帧；返回 null 表示「这条命令不回」（用来造超时）。</summary>
        private static byte[] InfoReply(byte[] request)
        {
            if (request.Length != 12)
                return null;

            byte seq = request[1];

            switch (request[0])
            {
                case 0x40:      // 最大端口数 8、端口映射使能、设备 ID 0x0139(RTL8239C)、SW 2.1、MCU GD32F310、扩展 1.0
                    // 未使用的 Byte2 按手册填 0xFF
                    return Sign(new byte[] { 0x40, seq, 0xFF, 0x08, 0x01, 0x01, 0x39, 0x21, 0x00, 0x01, 0x10 });

                case 0x50:
                    return ChipTypeFrame(seq);

                case 0x4C:
                    {
                        byte idx = request[2];
                        // 默认回「一块全空」（8 个 0xFF），正是会与请求撞车的那种响应
                        byte[] addresses = _addressBlocks == null ? new byte[0] : _addressBlocks(idx);
                        var frame = new byte[11];
                        frame[0] = 0x4C;
                        frame[1] = seq;
                        frame[2] = idx;
                        for (int i = 0; i < 8; i++)
                            frame[3 + i] = i < addresses.Length ? addresses[i] : (byte)0xFF;
                        return Sign(frame);
                    }

                case 0x4A:      // UVLO 原始 0x20、预分配使能、OVLO 原始 0x30、PSE 芯片数 6、不支持 0
                    // 未使用的 Byte4/5/6/10 按手册填 0xFF
                    return Sign(new byte[] { 0x4A, seq, 0x20, 0x01, 0xFF, 0xFF, 0xFF, 0x30, 0x06, 0x00, 0xFF });

                case 0x47:      // 接口全正常、上电复位（ResetReason.PowerOnReset = 0x01）、12 颗芯片访问都正常
                    return Sign(new byte[] { 0x47, seq, 0x00, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

                case 0xC0:
                    // 未使用的 Byte7..10 按手册填 0xFF
                    return Sign(new byte[] { 0xC0, 0x04, 0x19, 0x08, 0x19, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF });

                default:
                    return null;
            }
        }

        // ---------------- 驱动一次完整的读取 ----------------

        private static async Task<DeviceInfoResult> ReadDeviceInfoAsync(FakeSerialTransport fake, int timeoutMs,
            Action<int, int, string> progress)
        {
            using (var session = new SerialSession(fake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var reader = new DeviceInfoReader(session) { Progress = progress };
                var options = new SendOptions { Mode = SendMode.RawFrame };
                return await reader.ReadAllAsync(options, timeoutMs, CancellationToken.None);
            }
        }

        private static FakeSerialTransport InfoTransport()
        {
            var fake = new FakeSerialTransport();
            fake.AutoReply = InfoReply;
            return fake;
        }

        /// <summary>把失败的组连同原因拼进断言文案，失败时不用再猜。</summary>
        private static string DescribeFailures(DeviceInfoResult result)
        {
            var text = new System.Text.StringBuilder();
            foreach (DeviceInfoEntry entry in result.Entries)
                if (!entry.Ok)
                    text.Append("；").Append(entry.CommandKey).Append(" → ").Append(entry.Error);

            return text.Length == 0 ? string.Empty : "（失败原因：" + text + "）";
        }

        private static DeviceInfoEntry EntryOf(DeviceInfoResult result, string key)        {
            foreach (DeviceInfoEntry entry in result.Entries)
                if (entry.CommandKey == key)
                    return entry;

            throw new Exception("结果里没有 " + key);
        }

        private static string ValueOf(DeviceInfoEntry entry, string label)
        {
            foreach (DeviceInfoField field in entry.Fields)
                if (field.Label == label)
                    return field.Value;

            return "<没有这个标签>";
        }

        private static int FrameCount(FakeSerialTransport fake, byte commandId)
        {
            int n = 0;
            foreach (byte[] frame in SplitFrames(fake.WrittenBytes))
                if (frame.Length > 0 && frame[0] == commandId)
                    n++;

            return n;
        }

        // =================================================================

        private static void DeviceInfoCoverageTests()
        {
            Console.WriteLine("DeviceInfo 归属：谁来读这些命令");

            Check(DeviceInfoReader.IsCovered("0x40") && DeviceInfoReader.IsCovered("0xC0-04")
                  && DeviceInfoReader.IsCovered("0x50") && DeviceInfoReader.IsCovered("0x4C")
                  && DeviceInfoReader.IsCovered("0x4A") && DeviceInfoReader.IsCovered("0x47"),
                "六条信息命令都归面板接管");

            Check(!DeviceInfoReader.IsCovered("0x42") && !DeviceInfoReader.IsCovered("0x44")
                  && !DeviceInfoReader.IsCovered("0x4F"),
                "端口状态/测量类命令不归面板，仍可轮询");

            // 轮询列表与面板不能有两个入口：同一条命令两处显示、两处刷新，屏幕上就是两份「同一个值」
            var keys = new List<string>();
            foreach (PollItem item in PollPlan.BuildCandidates())
                keys.Add(item.Command.Key);

            Check(!keys.Contains("0x40") && !keys.Contains("0x4A") && !keys.Contains("0x50"),
                "无参数的信息命令（0x40/0x4A/0x50）从轮询候选中移除");
            Check(keys.Contains("0x41") && keys.Contains("0x42") && keys.Contains("0x44")
                  && keys.Contains("0x4E") && keys.Contains("0x4F"),
                "真正需要在时间上看变化的命令仍在候选里");

            // 整个分页策略的根因，单独钉死在这里：
            // 0x4C 请求的 RSVD 位是 0xFF，手册又规定空槽位回 0xFF，SEQ/Idx 原样回显、
            // 校验和随之相同——于是**空块的响应与请求逐字节相同**。
            // SerialSession 会把它当自回显丢掉，读它就必然超时。
            byte[] request = Rtl8239CommandBuilder.GlobalDeviceAddressGet(0x04, 0x02);
            byte[] emptyReply = Sign(new byte[] { 0x4C, 0x04, 0x02, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
            byte[] realReply = Sign(new byte[] { 0x4C, 0x04, 0x02, 0x20, 0x22, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

            CheckEq(Rtl8239CommandBuilder.ToHex(emptyReply), Rtl8239CommandBuilder.ToHex(request),
                "0x4C 空块的响应与请求逐字节相同（读空块必然被自回显抑制吞掉）");
            Check(Rtl8239CommandBuilder.ToHex(realReply) != Rtl8239CommandBuilder.ToHex(request),
                "有芯片的块响应与请求不同（所以只要不问空块就不会撞车）");

            // 同一个坑对 0x50 一样成立：请求是 (0x50, seq, 0xFF×9)，
            // 而「12 个槽位全是保留值」的响应也是 (0x50, seq, 0xFF×9)。
            int savedLastPopulated = _lastPopulatedChip;
            _lastPopulatedChip = -1;
            byte[] chipNoneReply = ChipTypeFrame(0x05);
            _lastPopulatedChip = savedLastPopulated;

            CheckEq(Rtl8239CommandBuilder.ToHex(chipNoneReply),
                Rtl8239CommandBuilder.ToHex(Rtl8239CommandBuilder.SystemChipTypeInfoGet(0x05)),
                "0x50 全保留的响应与请求逐字节相同（真机上 0x50 必然超时，且与链路断了无法区分）");

            // 还有 0xC0-04：设备从未保存过配置时，日期/版本字节与未使用字节全是 0xFF，
            // 整帧仍然等于请求。三处性质相同，所以 EmptyResponseHint 里那三条提示不是泛泛而谈。
            CheckEq(Rtl8239CommandBuilder.ToHex(Sign(new byte[]
                { 0xC0, 0x04, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })),
                Rtl8239CommandBuilder.ToHex(Rtl8239CommandBuilder.ConfigurationVersionGet()),
                "0xC0-04 从未保存配置时的响应与请求逐字节相同");
        }

        private static async Task DeviceInfoPagingTests()
        {
            Console.WriteLine("DeviceInfo 0x4C 分页");

            // --- 9 颗芯片：索引 0 满 8 颗、索引 1 还有 1 颗 → 只需要读 2 块 ---
            var fake = InfoTransport();
            _lastPopulatedChip = 8;
            _addressBlocks = delegate(byte idx)
            {
                if (idx == 0)
                    return new byte[] { 0x20, 0x22, 0x24, 0x26, 0x28, 0x2A, 0x2C, 0x2E };
                if (idx == 1)
                    return new byte[] { 0x30 };
                return new byte[0];
            };

            DeviceInfoResult result = await ReadDeviceInfoAsync(fake, 1500, null);

            // 这一条是整个分页策略的要害：第 3 块一定全空，而全空块的响应与请求逐字节相同，
            // 会被自回显抑制吞掉。所以正确的做法是根本不问它。
            CheckEq(FrameCount(fake, 0x4C), 2, "9 颗芯片只读 2 块——不去问那个必然全空的第 3 块");

            DeviceInfoEntry entry = EntryOf(result, "0x4C");
            Check(entry.Ok, "0x4C 读取成功" + (entry.Ok ? "" : "（实际失败：" + entry.Error + "）"));
            CheckEq(ValueOf(entry, "芯片 #0"), "0x20", "芯片 #0 的 I2C 地址");
            CheckEq(ValueOf(entry, "芯片 #7"), "0x2E", "索引 0 的第 8 个槽位按全局编号排在 #7");
            CheckEq(ValueOf(entry, "芯片 #8"), "0x30", "索引 1 的第一颗按全局编号排在 #8");
            CheckEq(ValueOf(entry, "芯片 #9"), DeviceInfoReader.AbsentPlaceholder,
                "空槽位显示为破折号，不是 0xFF（协议内部值不给人看）");
            CheckEq(ValueOf(entry, "已检出芯片数"), "9", "已检出芯片数 = 8 + 1");

            // --- 正好 8 颗：索引 0 满，绝不能再去问索引 1 ---
            var eightFake = InfoTransport();
            _lastPopulatedChip = 7;
            _addressBlocks = delegate(byte idx)
            {
                return idx == 0
                    ? new byte[] { 0x20, 0x22, 0x24, 0x26, 0x28, 0x2A, 0x2C, 0x2E }
                    : new byte[0];
            };

            DeviceInfoResult eight = await ReadDeviceInfoAsync(eightFake, 1500, null);

            CheckEq(FrameCount(eightFake, 0x4C), 1, "正好 8 颗时只读 1 块（索引 1 必然全空，问了就会超时）");
            CheckEq(ValueOf(EntryOf(eight, "0x4C"), "已检出芯片数"), "8", "8 颗都检出来了");

            // --- 0x50 报到 11 颗 → 2 块，且不能超出索引上限 ---
            var fullFake = InfoTransport();
            _lastPopulatedChip = 11;
            _addressBlocks = delegate(byte idx) { return new byte[] { 0x20 }; };

            DeviceInfoResult full = await ReadDeviceInfoAsync(fullFake, 1500, null);

            CheckEq(FrameCount(fullFake, 0x4C), 2, "12 颗芯片也只要 2 块（每块 8 个）");
            CheckEq(ValueOf(EntryOf(full, "0x4C"), "已检出芯片数"), "2", "两块各检出 1 颗");

            // --- 芯片编号有断层：0 槽没有芯片、第 8 槽有（0x50 报 9 颗）---
            // 索引 0（芯片 #0-#7）全空 → 响应与请求逐字节相同 → 超时。
            // 但**不能因此就不读索引 1**：真正有数据的芯片在那边。
            var gapFake = InfoTransport();
            _lastPopulatedChip = 8;
            _addressBlocks = delegate(byte idx)
            {
                return idx == 0 ? new byte[0] : new byte[] { 0x30 };
            };

            DeviceInfoResult gap = await ReadDeviceInfoAsync(gapFake, 120, null);

            CheckEq(FrameCount(gapFake, 0x4C), 2, "断层时仍要把后面的块读完，不能被第一个空块打断");
            CheckEq(ValueOf(EntryOf(gap, "0x4C"), "芯片 #8"), "0x30",
                "断层后面的芯片地址照样显示出来（修复前这颗会被整块丢掉）");
            Check(!EntryOf(gap, "0x4C").Ok, "同时如实报出索引 0x00 那一块没读成，不假装完整");

            // --- 12 个槽位全是保留值：按手册这时的响应与请求逐字节相同，会被当成回显丢掉 ---
            // 真机上 0x50 必然超时，而**超时是唯一能观察到的现象**：「一颗芯片都没有」
            // 和「链路断了」在观测上无法区分。断言的重点是提示文案必须把这个歧义说出来，
            // 而不是假装读到了一个「没有芯片」的确定结论。
            var noneFake = InfoTransport();
            _lastPopulatedChip = -1;
            _addressBlocks = delegate(byte idx) { return new byte[0]; };

            DeviceInfoResult none = await ReadDeviceInfoAsync(noneFake, 120, null);

            Check(!EntryOf(none, "0x50").Ok, "全保留时 0x50 超时（响应与请求逐字节相同，读不回来）");
            Check(EntryOf(none, "0x50").Error.IndexOf("回显", StringComparison.Ordinal) >= 0,
                "0x50 的超时提示点明「响应被当成回显丢掉了」，而不是笼统的链路超时");
            Check(EntryOf(none, "0x4C").Error.IndexOf("回显", StringComparison.Ordinal) >= 0,
                "0x50 读不到后 0x4C 降级，同样给出解释");
            Check(!EntryOf(none, "0x4C").Ok, "降级后第一块也是全空，如实报失败而不是假装「没有芯片」");

            // --- 0x50 读不到：退化成只读第一块，不能因此整组不读 ---
            var blindFake = new FakeSerialTransport();
            blindFake.AutoReply = delegate(byte[] request)
            {
                return request[0] == 0x50 ? null : InfoReply(request);
            };

            _lastPopulatedChip = 2;
            _addressBlocks = delegate(byte idx) { return new byte[] { 0x20, 0x22 }; };

            DeviceInfoResult blind = await ReadDeviceInfoAsync(blindFake, 120, null);

            CheckEq(FrameCount(blindFake, 0x4C), 1, "0x50 读不到时退化成只读第一块");
            Check(EntryOf(blind, "0x4C").Ok, "退化成一块仍然算读成功（0x50 那组的失败会单独显示）");
            CheckEq(ValueOf(EntryOf(blind, "0x4C"), "芯片 #0"), "0x20", "退化路径仍然给出真实地址");

            // --- 0x50 与 0x4C 数字对不上：照实说出来，不替用户裁决谁对 ---
            var clashFake = InfoTransport();
            _lastPopulatedChip = 8;                       // 0x50 说有 9 颗
            _addressBlocks = delegate(byte idx)
            {
                return idx == 0 ? new byte[] { 0x20, 0x22, 0x24 } : new byte[] { 0x30 };
            };

            DeviceInfoResult clash = await ReadDeviceInfoAsync(clashFake, 1500, null);
            string clashNote = ValueOf(EntryOf(clash, "0x4C"), "注意");

            Check(clashNote.IndexOf("9", StringComparison.Ordinal) >= 0
                  && clashNote.IndexOf("4", StringComparison.Ordinal) >= 0,
                "0x50 说 9 颗、实读 4 颗时给出对照提示（实际：" + clashNote + "）");

            // --- 0x50 读不到且第一块是满的：不能假装数完了 ---
            var blindFullFake = new FakeSerialTransport();
            blindFullFake.AutoReply = delegate(byte[] request)
            {
                return request[0] == 0x50 ? null : InfoReply(request);
            };
            _addressBlocks = delegate(byte idx)
            {
                return new byte[] { 0x20, 0x22, 0x24, 0x26, 0x28, 0x2A, 0x2C, 0x2E };
            };

            DeviceInfoResult blindFull = await ReadDeviceInfoAsync(blindFullFake, 120, null);

            Check(ValueOf(EntryOf(blindFull, "0x4C"), "注意").IndexOf("可能还有", StringComparison.Ordinal) >= 0,
                "0x50 读不到且第一块已满时提示「后面可能还有芯片」");
            CheckEq(ValueOf(EntryOf(blindFull, "0x4C"), "已检出芯片数"), "8", "已读到几颗就报几颗");

            _addressBlocks = null;
            _lastPopulatedChip = 2;
        }

        private static async Task DeviceInfoIsolationTests()
        {
            Console.WriteLine("DeviceInfo 逐条失败隔离");

            // 0x50 永远不回：它超时，其余五条照常出结果
            var fake = new FakeSerialTransport();
            fake.AutoReply = delegate(byte[] request)
            {
                return request[0] == 0x50 ? null : InfoReply(request);
            };

            // 0x50 读不到 → 0x4C 退化成只读第一块，所以第一块要有真实数据
            _addressBlocks = delegate(byte idx) { return new byte[] { 0x20, 0x22 }; };

            int lastDone = -1;
            int progressCalls = 0;
            bool noNullTitle = true;
            DeviceInfoResult result = await ReadDeviceInfoAsync(fake, 120,
                delegate(int done, int total, string title)
                {
                    lastDone = done;
                    progressCalls++;
                    if (title == null) noNullTitle = false;
                });

            _addressBlocks = null;

            CheckEq(result.TotalCount, 6, "一共六组");
            CheckEq(result.OkCount, 5, "一条超时，其余五条仍然成功");
            CheckEq(result.Summary, "本次更新：5/6 成功", "摘要把失败如实算进去");

            DeviceInfoEntry failed = EntryOf(result, "0x50");
            Check(!failed.Ok, "0x50 标记为失败");
            Check(failed.Error.StartsWith("等待响应超时", StringComparison.Ordinal),
                "失败原因写明是超时（实际：" + failed.Error + "）");
            Check(failed.Error.IndexOf("回显", StringComparison.Ordinal) >= 0,
                "并且点明「超时」与「一颗芯片都没有」在这个命令上无法区分");
            CheckEq(ValueOf(failed, "芯片类型"), DeviceInfoReader.AbsentPlaceholder, "失败的那组显示破折号");

            Check(EntryOf(result, "0x40").Ok && EntryOf(result, "0x47").Ok,
                "同一批里的其它组不受牵连（0x40 / 0x47 仍成功）");

            Check(lastDone >= 0 && lastDone < 6,
                "进度回调报的是组的下标（最后一组是索引 3 的 0x4C，它被排到第二趟读，实际 " + lastDone + "）");
            Check(progressCalls >= 6, "六组各报一次进度（实际 " + progressCalls + " 次）");
            Check(noNullTitle, "进度回调不再用 title == null 报「已完成」（那是要靠时序撑着的哨兵）");
        }

        private static async Task DeviceInfoFormatTests()
        {
            Console.WriteLine("DeviceInfo 展示内容");

            var fake = InfoTransport();
            _addressBlocks = delegate(byte idx)
            {
                return idx == 0
                    ? new byte[] { 0x20, 0x22 }
                    : new byte[0];
            };

            DeviceInfoResult result = await ReadDeviceInfoAsync(fake, 1500, null);
            _addressBlocks = null;

            DeviceInfoEntry identity = EntryOf(result, "0x40");
            CheckEq(ValueOf(identity, "设备 ID"), "RTL8239C  (0x0139)", "设备 ID 解成型号名并附原始值");
            CheckEq(ValueOf(identity, "软件版本"), "2.1", "SW 版本按低/高半字节拆开");
            CheckEq(ValueOf(identity, "扩展版本"), "1.0", "扩展版本");
            CheckEq(ValueOf(identity, "最大端口数"), "8", "最大端口数用十进制（不是寄存器值）");

            DeviceInfoEntry chips = EntryOf(result, "0x50");
            CheckEq(chips.Fields.Count, 3, "芯片表只列到最后一颗有内容的芯片，尾部保留槽位不列");
            CheckEq(ValueOf(chips, "芯片 #0"), "8 通道 BT 芯片", "芯片 #0 是 BT");
            CheckEq(ValueOf(chips, "芯片 #2"), "8 通道 AT 芯片", "芯片 #2 是 AT");

            DeviceInfoEntry version = EntryOf(result, "0xC0-04");
            CheckEq(ValueOf(version, "配置日期"), "25/08/25", "配置日期");
            CheckEq(ValueOf(version, "配置版本"), "1.0", "配置版本");

            DeviceInfoEntry globals = EntryOf(result, "0x4A");
            CheckEq(ValueOf(globals, "PSE 芯片数"), "6", "PSE 芯片数用十进制");
            Check(ValueOf(globals, "UVLO 门限").StartsWith("35.", StringComparison.Ordinal),
                "UVLO = 33.0 + 0x20 × 0.06445 ≈ 35.1 V（实测 " + ValueOf(globals, "UVLO 门限") + "）");

            DeviceInfoEntry reset = EntryOf(result, "0x47");
            CheckEq(ValueOf(reset, "复位原因"), "上电复位", "复位原因（原始 0x01）");
            CheckEq(ValueOf(reset, "异常芯片"), "无", "12 颗芯片访问都正常时不逐颗列出来");
        }

        private static async Task DeviceInfoClearFlagTests()
        {
            Console.WriteLine("DeviceInfo 0x47 只读，不清除设备记录");

            var fake = InfoTransport();
            _addressBlocks = delegate(byte idx) { return new byte[] { 0x20, 0x22 }; };

            await ReadDeviceInfoAsync(fake, 1500, null);
            _addressBlocks = null;

            byte[] resetFrame = null;
            foreach (byte[] frame in SplitFrames(fake.WrittenBytes))
                if (frame.Length > 0 && frame[0] == 0x47)
                    resetFrame = frame;

            Check(resetFrame != null, "确实发了 0x47");
            CheckEq(resetFrame == null ? (object)"<没发>" : (object)resetFrame[2], (object)(byte)0x00,
                "0x47 的清除标志固定为 0x00——发非零值会抹掉设备上的复位记录");
        }

        private static async Task DeviceInfoStaleFrameTests()
        {
            Console.WriteLine("DeviceInfo 不认领不属于自己的响应");

            // --- 上一次超时之后才到的 0xC0-04 回包，不能被下一次读取捡走 ---
            // _recentFrames 只是用来容纳「回包比等待者先到」的，那个窗口只在一次事务内部存在。
            // 跨事务复用它的后果是：每次显示的其实都是上一次的数据，时间戳却是新的。
            var stale = InfoTransport();
            stale.Feed(Sign(new byte[]
                { 0xC0, 0x04, 0x11, 0x01, 0x01, 0x09, 0x09, 0xFF, 0xFF, 0xFF, 0xFF }));   // 伪造的旧配置 11/01/01 版本 9.9

            await Task.Delay(250);      // 让读循环把它收进缓存（假传输层 10ms 一轮轮询）

            DeviceInfoResult fresh = await ReadDeviceInfoAsync(stale, 1500, null);

            CheckEq(ValueOf(EntryOf(fresh, "0xC0-04"), "配置版本"), "1.0",
                "读到的是这一次的回包（1.0），不是上一次遗留的 9.9");
            CheckEq(ValueOf(EntryOf(fresh, "0xC0-04"), "配置日期"), "25/08/25",
                "日期也来自这一次的回包");

            // --- 子命令对不上的 0xC0 帧，必须明确失败，不能显示成配置版本 ---
            // 0xC0 系只按命令 ID 配对，所以任何 0xC0 帧都能满足那个等待：
            // 手工帧的回包、上一次的回包，都可能被认领。
            var wrongSub = new FakeSerialTransport();
            wrongSub.AutoReply = delegate(byte[] request)
            {
                if (request[0] == 0xC0)     // 回一个 0xC0-01，而不是请求的 0xC0-04
                    return Sign(new byte[] { 0xC0, 0x01, 0x19, 0x08, 0x19, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF });
                return InfoReply(request);
            };

            _addressBlocks = delegate(byte idx) { return new byte[0]; };

            DeviceInfoResult mismatched = await ReadDeviceInfoAsync(wrongSub, 300, null);
            _addressBlocks = null;

            DeviceInfoEntry version = EntryOf(mismatched, "0xC0-04");
            Check(!version.Ok, "子命令对不上时明确报失败");
            Check(version.Error.IndexOf("子命令", StringComparison.Ordinal) >= 0,
                "失败原因点名是子命令不符（实际：" + version.Error + "）");
            CheckEq(ValueOf(version, "配置版本"), DeviceInfoReader.AbsentPlaceholder,
                "绝不把别人的数据显示成配置版本");
            Check(!version.Ok && mismatched.OkCount < mismatched.TotalCount,
                "并且不会被算进「n/n 成功」");

            // --- 显示顺序不受读取顺序影响 ---
            var ordered = InfoTransport();
            _addressBlocks = delegate(byte idx) { return new byte[] { 0x20, 0x22 }; };

            DeviceInfoResult order = await ReadDeviceInfoAsync(ordered, 1500, null);
            _addressBlocks = null;

            // 0x4C 被排到第二趟读（它要等 0x50 的芯片数），但显示时仍回到它自己的位置
            CheckEq(order.Entries[3].CommandKey, "0x4C",
                "0x4C 显示在数组里的第 4 位（读取顺序变了，显示顺序没变）");
            CheckEq(order.Entries[0].CommandKey, "0x40", "第一位仍是设备身份");
        }

        private static async Task TransactLockTests()
        {
            Console.WriteLine("SerialTransaction 事务锁");

            // 确定性断言：锁被持有期间，第二个持有者拿不到；
            // 必须等到第一个 Dispose 之后才轮到它。这是「手动读取不会插进轮询事务中间」的根据。
            var fake = InfoTransport();
            using (var session = new SerialSession(fake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var order = new List<string>();
                SerialTransaction first = await session.BeginTransactionAsync(CancellationToken.None);

                Task second = Task.Run(async delegate
                {
                    SerialTransaction t = await session.BeginTransactionAsync(CancellationToken.None);
                    lock (order) order.Add("第二个拿到");
                    t.Dispose();
                });

                await Task.Delay(80);
                lock (order) order.Add("第一个仍持有");

                CheckEq(order.Count, 1, "第一个持有期间，第二个拿不到锁");
                CheckEq(order[0], "第一个仍持有", "而且顺序是先「仍持有」");

                first.Dispose();
                await second;

                CheckEq(order.Count, 2, "第一个释放后第二个立刻拿到");
                CheckEq(order[1], "第二个拿到", "顺序正确");

                // 重复 Dispose 不能把锁撑开：多放一次会让互斥失效
                first.Dispose();
                SerialTransaction again = await session.BeginTransactionAsync(CancellationToken.None);
                Check(again != null, "重复 Dispose 之后锁仍然可用（没有多放一次）");
                again.Dispose();
            }
        }

        private static async Task DeviceInfoWithPollingTests()
        {
            Console.WriteLine("DeviceInfo 与轮询共存");

            // 手动读取期间轮询同时在发命令。两者共用一把事务锁，所以谁也不会把
            // 对方的请求-响应窗口撑开——结果是两边都拿到自己那份完整且正确的数据。
            var fake = new FakeSerialTransport();
            fake.AutoReply = delegate(byte[] request)
            {
                byte[] reply = InfoReply(request);
                if (reply != null)
                    return reply;

                if (request[0] == 0x42)
                    return Sign(new byte[] { 0x42, request[1], request[2], 0x02, 0x44, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF });

                return null;
            };

            _addressBlocks = delegate(byte idx) { return new byte[] { 0x20, 0x22 }; };

            using (var session = new SerialSession(fake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var options = new SendOptions { Mode = SendMode.RawFrame };
                var reader = new DeviceInfoReader(session);

                var plan = new PollingPlan
                {
                    IntervalMs = 0,
                    ResponseTimeoutMs = 1500,
                    MaxRetries = 0,
                    InterCommandDelayMs = 0,
                };
                plan.Items.Add(Item("0x42", 0x00));

                var clock = new FakeClock();
                var runner = new PollingRunner(session, clock.Read,
                    // 真让出一次时间片，否则轮询会空转把事务锁一直攥在手里
                    delegate(TimeSpan span, CancellationToken token) { return Task.Delay(2, token); });

                var cts = new CancellationTokenSource();
                int matched = 0;
                runner.ResponseMatched += delegate(PollRequest request, FrameEvent frameEvent)
                {
                    if (Interlocked.Increment(ref matched) >= 6) cts.Cancel();
                };

                Task pollTask = runner.RunAsync(plan, options, cts.Token);
                DeviceInfoResult infoResult = await reader.ReadAllAsync(options, 1500, CancellationToken.None);

                try { await pollTask; }
                catch (Exception) { }

                _addressBlocks = null;

                CheckEq(infoResult.OkCount, infoResult.TotalCount,
                    "轮询同时进行时，手动读取六组全部成功" + DescribeFailures(infoResult));
                CheckEq(infoResult.Summary, "本次更新：6/6 成功", "摘要是全成功");
                Check(matched >= 6, "轮询也照常收到响应（实际 " + matched + " 个）");
            }
        }
        private static async Task DeviceInfoDescribeTests()
        {
            Console.WriteLine("DeviceInfo 复制全部用的纯文本");

            var fake = InfoTransport();
            _lastPopulatedChip = 2;
            _addressBlocks = delegate(byte idx) { return new byte[] { 0x20, 0x22 }; };

            DeviceInfoResult result = await ReadDeviceInfoAsync(fake, 1500, null);
            _addressBlocks = null;

            string text = DeviceInfoReader.Describe(result, "14:23:05");

            Check(text.Contains("本次更新：6/6 成功"), "表头带成功计数");
            Check(text.Contains("最后更新 14:23:05"), "表头带时间戳");
            Check(text.Contains("0x40  设备身份"), "每条命令一段，带命令号与小标题");
            Check(text.Contains("  设备 ID: RTL8239C  (0x0139)"), "字段缩进两格、标签与值用冒号分隔");
            CheckEq(DeviceInfoReader.Describe(null, "x"), "", "没有结果时返回空串");

            // 失败的那一组也要出现，并写明原因——复制出去的内容应当和屏幕上一致
            var failedFake = new FakeSerialTransport();
            failedFake.AutoReply = delegate(byte[] request)
            {
                return request[0] == 0x50 ? null : InfoReply(request);
            };
            _addressBlocks = delegate(byte idx) { return new byte[] { 0x20 }; };

            DeviceInfoResult partial = await ReadDeviceInfoAsync(failedFake, 120, null);
            _addressBlocks = null;

            string partialText = DeviceInfoReader.Describe(partial, "14:24:00");
            Check(partialText.Contains("0x50  芯片类型（读取失败）"), "失败的那组标出「读取失败」");
            Check(partialText.Contains("  错误: 等待响应超时"), "并写明失败原因");
            Check(partialText.Contains("本次更新：5/6 成功"), "计数如实反映");
        }
    }
}
