using System;
using System.Collections.Generic;
using PoECommandTool;
using PoECommandTool.Serial;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        // =================================================================
        //  字段范围校验（SEC-02 / 审查发现 C-1）
        //
        //  原实现把用户输入解析成 long 后直接 (byte)/(int) 转换，越界值被静默截断：
        //    300  → 0x2C        （字节字段）
        //    70000 → 0x1170 = 446.4W（0.1W/LSB 的功率字段）
        //    -1   → 0xFF
        //  这是一个配置 PoE 供电预算与 UVLO/OVLO 阈值的工具，截断后的值会被真实写进
        //  给受电设备供电的硬件。
        // =================================================================
        private static void FieldRangeTests()
        {
            Console.WriteLine("字段范围校验 FieldRange（SEC-02：硬件值不得静默截断）");

            // ---- 1. 底层写入不得静默截断（Word / Dword 字段）----
            // 0x04 GlobalPowerSourceSet(seq, maxPower(byte), totalPower(int), reservedPower(int))
            string e2 = ThrownBy(delegate { Rtl8239CommandBuilder.GlobalPowerSourceSet(0x01, 1, 70000, 0); });
            Check(e2.StartsWith("ArgumentOutOfRangeException"),
                "Word 字段 70000 被拒绝（不再静默变 446.4W）：" + e2);

            string e3 = ThrownBy(delegate { Rtl8239CommandBuilder.GlobalPowerSourceSet(0x01, 1, -1, 0); });
            Check(e3.StartsWith("ArgumentOutOfRangeException"), "Word 字段 -1 被拒绝：" + e3);

            // 边界内的值必须照常工作（防止修过头把正常用法也挡了）
            CheckEq(Rtl8239CommandBuilder.GlobalPowerSourceSet(0x01, 1, 0xFFFF, 0).Length, 12,
                "Word 上界 0xFFFF 正常生成 12 字节帧");
            CheckEq(Rtl8239CommandBuilder.GlobalParametersSet(0x01, 0xFF, 0x2F).Length, 12,
                "边界内值 0xFF / 0x2F 正常生成 12 字节帧");

            // ---- 2. 字节字段：截断发生在目录的 Build lambda 内部，必须靠 BuildChecked 拦 ----
            CommandDef cmdByte = null;
            foreach (CommandDef c in Rtl8239Catalog.All) { if (c.Key == "0x0E") cmdByte = c; }
            Check(cmdByte != null, "找到 0x0E（两个字节字段）");
            if (cmdByte != null)
            {
                // 直接调 Build 仍然是截断的（lambda 内部 (byte)v[0]）——这正是为什么所有调用方
                // 都必须走 BuildChecked，而不是各自调 Build。
                CheckEq(cmdByte.Build(0x01, new long[] { 300, 300 })[2], (byte)0x2C,
                    "记录现状：直接调 Build 仍会静默截断 300 → 0x2C（故调用方必须走 BuildChecked）");

                string eByte = ThrownBy(delegate { Rtl8239Catalog.BuildChecked(cmdByte, 0x01, new long[] { 300, 300 }); });
                Check(eByte.StartsWith("ArgumentOutOfRangeException"),
                    "BuildChecked 拒绝字节字段越界值 300：" + eByte);
                Check(eByte.Contains("UVLO"),
                    "拒绝信息点名了出问题的字段（便于现场定位输入框）：" + eByte);

                // 这两个字段是 UVLO 原始值（0x00-0x2F）与 OVLO 原始值。
                // 它们虽然各占一字节，设备只认到 0x2F——所以在命令级范围上线之后，
                // 0xFF 不再"合法"。这条断言原先写的是 0xFF，固化的正是那个错。
                CheckEq(Rtl8239Catalog.BuildChecked(cmdByte, 0x01, new long[] { 0x2F, 0x2F }).Length, 12,
                    "BuildChecked 放行命令级上界 0x2F / 0x2F");
                Check(ThrownBy(delegate { Rtl8239Catalog.BuildChecked(cmdByte, 0x01, new long[] { 0xFF, 0x2F }); })
                        .StartsWith("ArgumentOutOfRangeException"),
                    "UVLO 占一字节，但设备只认 0x00-0x2F，0xFF 被拒绝");
            }

            // 参数个数不符也要拒绝（防止字段与值错位后静默少写字节）
            string eArity = ThrownBy(delegate { Rtl8239Catalog.BuildChecked(cmdByte, 0x01, new long[] { 1 }); });
            Check(eArity.StartsWith("ArgumentException"), "参数个数不符被拒绝：" + eArity);

            // ---- 2. 目录级校验器 ----
            CheckEq(Rtl8239Catalog.MaxFor(FieldKind.Byte), 0xFFL, "Byte 上限 0xFF");
            CheckEq(Rtl8239Catalog.MaxFor(FieldKind.Word), 0xFFFFL, "Word 上限 0xFFFF");
            CheckEq(Rtl8239Catalog.MaxFor(FieldKind.Dword), 0xFFFFFFFFL, "Dword 上限 0xFFFFFFFF");
            CheckEq(Rtl8239Catalog.MaxFor(FieldKind.Port), 0x2FL, "Port 上限 0x2F（0-47）");

            var byteField = new FieldDef { Name = "使能值", Kind = FieldKind.Byte, DefaultValue = 0 };
            Check(ThrownBy(delegate { Rtl8239Catalog.ValidateFieldValue(byteField, 0x100); }).Contains("使能值"),
                "越界异常点名了出问题的字段");
            CheckEq(ThrownBy(delegate { Rtl8239Catalog.ValidateFieldValue(byteField, 0xFF); }),
                "<no exception>", "0xFF 合法");
            CheckEq(ThrownBy(delegate { Rtl8239Catalog.ValidateFieldValue(byteField, 0); }),
                "<no exception>", "0 合法");
            Check(ThrownBy(delegate { Rtl8239Catalog.ValidateFieldValue(byteField, -1); }).Length > 0,
                "-1 被拒绝");

            // ---- 3. 目录里每个命令的默认值都必须合法 ----
            // 防止将来新增命令时写了一条越界默认值，在轮询路径上静默出错。
            int checkedFields = 0;
            int badDefaults = 0;
            string firstBad = "";
            foreach (CommandDef cmd in Rtl8239Catalog.All)
            {
                if (cmd.Fields == null) continue;
                foreach (FieldDef fd in cmd.Fields)
                {
                    checkedFields++;
                    try { Rtl8239Catalog.ValidateFieldValue(fd, fd.DefaultValue); }
                    catch (Exception ex)
                    {
                        badDefaults++;
                        if (firstBad.Length == 0) firstBad = cmd.Key + " 的「" + fd.Name + "」= " + ex.Message;
                    }
                }
            }
            CheckEq(badDefaults, 0, "全部 " + checkedFields + " 个字段的默认值都在范围内"
                + (badDefaults > 0 ? "（首个越界：" + firstBad + "）" : ""));

            // ---- 4. 轮询路径共用同一个校验器 ----
            // PollPlan.BuildFrame 直接从目录取默认值，必须走同样的校验，否则两条路径会分叉。
            int pollBad = 0;
            string firstPollBad = "";
            foreach (CommandDef cmd in Rtl8239Catalog.All)
            {
                if (!PollPlan.IsPollable(cmd)) continue;
                foreach (byte port in new byte[] { 0x00, 0x2F, 0x30 })
                {
                    var item = new PollItem { Command = cmd, Port = port, Enabled = true };
                    try { PollPlan.BuildFrame(item, 0x01); }
                    catch (Exception ex)
                    {
                        if (port > 0x2F) continue;   // 越界端口本来就该被拒
                        pollBad++;
                        if (firstPollBad.Length == 0)
                            firstPollBad = cmd.Key + " 端口 0x" + port.ToString("X2") + "：" + ex.Message;
                    }
                }
            }
            CheckEq(pollBad, 0, "可轮询命令在合法端口下都能构造帧"
                + (pollBad > 0 ? "（首个失败：" + firstPollBad + "）" : ""));

            // ---- 5. 序列号（ParseByte）：单字节标量同样不得静默截断 ----
            // 序列号进的是帧的 Byte1。300 被截成 0x2C 之后，设备回包的序号与请求对不上，
            // 表现成轮询幽灵超时，而现场看不出是输入框里那个数的问题。
            CheckEq(Rtl8239Catalog.ParseByte("0", "序列号"), (byte)0x00, "ParseByte：下界 0");
            CheckEq(Rtl8239Catalog.ParseByte("0xFF", "序列号"), (byte)0xFF, "ParseByte：上界 0xFF");
            CheckEq(Rtl8239Catalog.ParseByte("255", "序列号"), (byte)0xFF, "ParseByte：十进制 255 = 0xFF");
            Check(ThrownBy(delegate { Rtl8239Catalog.ParseByte("300", "序列号"); })
                    .StartsWith("FormatException"),
                "ParseByte：300 被拒绝（不再静默变 0x2C）");
            Check(ThrownBy(delegate { Rtl8239Catalog.ParseByte("-1", "序列号"); })
                    .StartsWith("FormatException"),
                "ParseByte：-1 被拒绝（不再静默变 0xFF）");
            Check(ThrownBy(delegate { Rtl8239Catalog.ParseByte("0x100", "序列号"); })
                    .StartsWith("FormatException"),
                "ParseByte：0x100 被拒绝");
            Check(ThrownBy(delegate { Rtl8239Catalog.ParseByte("300", "序列号"); })
                    .Contains("序列号"),
                "ParseByte：异常信息点名了是哪个输入，便于现场定位");

            // ---- 6. 命令级范围：FieldKind 只说得清"几字节"，说不清"设备认不认" ----
            // Bank ID / 组索引 / 芯片地址这些字段都是 Byte，但设备只接受更窄的范围。
            // 这个范围原先只写在字段名里给人看，填 0x50 会一路发到硬件。
            var ranged = new List<string>();
            foreach (CommandDef c in Rtl8239Catalog.All)
            {
                if (c.Fields == null) continue;
                foreach (FieldDef f in c.Fields)
                    if (f.MinValue.HasValue || f.MaxValue.HasValue)
                        ranged.Add(c.Key + " " + f.Name);
            }
            Check(ranged.Count >= 12, "目录里声明了较窄范围的字段共 " + ranged.Count + " 个");

            CommandDef pmConfig = null;
            foreach (CommandDef c in Rtl8239Catalog.All) { if (c.Key == "0x4B") pmConfig = c; }
            Check(pmConfig != null && pmConfig.Fields[0].MaxValue == 0x07, "0x4B 的 Bank ID 上限是 0x07");

            string bankBad = ThrownBy(delegate
            {
                Rtl8239Catalog.BuildChecked(pmConfig, 0x01, new long[] { 0x50 });
            });
            Check(bankBad.StartsWith("ArgumentOutOfRangeException"),
                "Bank ID 填 0x50 被拒绝（Byte 的上界是 0xFF，但设备只认 0x00-0x07）：" + bankBad);
            Check(bankBad.Contains("Bank ID"), "异常信息点名了是哪个字段：" + bankBad);

            // 边界内的值必须照常工作（防止修过头把正常用法也挡了）
            CheckEq(Rtl8239Catalog.BuildChecked(pmConfig, 0x01, new long[] { 0x07 }).Length, 12,
                "Bank ID 上界 0x07 正常生成");

            CommandDef deviceAddr = null;
            foreach (CommandDef c in Rtl8239Catalog.All) { if (c.Key == "0x4C") deviceAddr = c; }
            Check(deviceAddr != null && deviceAddr.Fields[0].MinValue == 0x00
                  && deviceAddr.Fields[0].MaxValue == 0x0B,
                "0x4C 的索引范围是 0x00-0x0B");
            Check(ThrownBy(delegate { Rtl8239Catalog.BuildChecked(deviceAddr, 0x01, new long[] { 0x0C }); })
                    .StartsWith("ArgumentOutOfRangeException"),
                "索引 0x0C 被拒绝（手册写明 0x00-0x0B 有效）");

            CommandDef chipAddr = null;
            foreach (CommandDef c in Rtl8239Catalog.All) { if (c.Key == "0xF1") chipAddr = c; }
            Check(chipAddr != null && chipAddr.Fields[0].MinValue == 0x20
                  && chipAddr.Fields[0].MaxValue == 0x37,
                "0xF1 的芯片地址范围是 0x20-0x37");
            CheckEq(Rtl8239Catalog.BuildChecked(chipAddr, 0x01, new long[] { 0x20, 0 }).Length, 12,
                "芯片地址下界 0x20 正常生成");
            Check(ThrownBy(delegate { Rtl8239Catalog.BuildChecked(chipAddr, 0x01, new long[] { 0x1F, 0 }); })
                    .StartsWith("ArgumentOutOfRangeException"),
                "芯片地址 0x1F 被拒绝（低于下界）");
        }
    }
}
