using System;
using System.Collections.Generic;
using System.Linq;

namespace PoECommandTool
{
    // =====================================================================
    //  RTL8239 命令目录（供界面展示与参数输入）
    //  每个命令：Key(命令号)、Name(中文名)、Category(分类)、Description(介绍)、
    //           Fields(参数定义)、Build(按字段值组装帧的函数)
    // =====================================================================

    /// <summary>参数字段类型。</summary>
    public enum FieldKind
    {
        Byte,   // 单字节（可输十六进制 0x 或十进制）
        Word,   // 双字节（小端）
        Dword,  // 四字节（小端）
        Port,   // 端口（支持单端口 / 端口列表两种模式）
    }

    /// <summary>参数字段定义。</summary>
    public class FieldDef
    {
        public string Name;
        public FieldKind Kind;
        public long DefaultValue;

        /// <summary>
        /// 设备实际接受的范围。为 null 表示「该字段类型能表示的范围」（见 <see cref="Rtl8239Catalog.MaxFor"/>）。
        ///
        /// 为什么需要它：<see cref="FieldKind"/> 只说得清「几字节」，说不清「设备认不认」。
        /// Bank ID 是 Byte，但设备只认 0x00-0x07；不写在这里的话，填 0x50 会一路发到硬件。
        /// </summary>
        public long? MinValue;
        public long? MaxValue;
    }

    /// <summary>命令定义。</summary>
    public class CommandDef
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public FieldDef[] Fields;
        public Func<byte, long[], byte[]> Build;   // (序列号, 字段值) => 帧
    }

    public static class Rtl8239Catalog
    {
        public const string CatControl = "控制命令";
        public const string CatQuery = "查询命令";
        public const string CatMisc = "杂项命令";
        public const string CatDebug = "调试命令";

        // ---- 字段构造快捷方法 ----
        private static FieldDef B(string name, long def = 0) =>
            new FieldDef { Name = name, Kind = FieldKind.Byte, DefaultValue = def };

        private static FieldDef W(string name, long def = 0) =>
            new FieldDef { Name = name, Kind = FieldKind.Word, DefaultValue = def };

        private static FieldDef D(string name, long def = 0) =>
            new FieldDef { Name = name, Kind = FieldKind.Dword, DefaultValue = def };

        private static FieldDef P(string name = "端口（0x00-0x2F）") =>
            new FieldDef { Name = name, Kind = FieldKind.Port };

        /// <summary>
        /// 数值范围比字段类型更窄的字段。名字里通常已经写着范围了——
        /// 把它变成机器能校验的约束，而不是只给人看的一句话。
        /// </summary>
        private static FieldDef Ranged(string name, long def, long min, long max) =>
            new FieldDef
            {
                Name = name,
                Kind = FieldKind.Byte,
                DefaultValue = def,
                MinValue = min,
                MaxValue = max,
            };

        private static CommandDef Def(string key, string name, string cat, string desc,
            Func<byte, long[], byte[]> build, params FieldDef[] fields) =>
            new CommandDef { Key = key, Name = name, Category = cat, Description = desc, Fields = fields, Build = build };

        /// <summary>解析数字：0x 前缀或含 A-F 按十六进制，否则按十进制。</summary>
        public static long ParseNumber(string s)
        {
            s = (s ?? string.Empty).Trim();
            if (s.Length == 0) throw new FormatException("请输入数值。");
            bool hex = false;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                hex = true;
                s = s.Substring(2);
            }
            else if (s.IndexOfAny(new[] { 'A', 'B', 'C', 'D', 'E', 'F', 'a', 'b', 'c', 'd', 'e', 'f' }) >= 0)
            {
                hex = true;
            }
            return hex ? Convert.ToInt64(s, 16) : long.Parse(s);
        }

        /// <summary>
        /// 解析一个最终要放进单个字节的数值（序列号等）。越界抛 <see cref="FormatException"/>
        /// 而不是静默截断——被截断的序列号（300 → 0x2C）会让设备的回包永远对不上请求，
        /// 表现成轮询幽灵超时，而现场看不出是输入的问题。
        /// </summary>
        public static byte ParseByte(string text, string label)
        {
            long value = ParseNumber(text);
            if (value < 0 || value > MaxFor(FieldKind.Byte))
                throw new FormatException(string.Format("{0}须在 0x00-0xFF 范围内，当前是 {1}。", label, value));

            return (byte)value;
        }

        /// <summary>某类字段能表示的最大值（含）。</summary>
        public static long MaxFor(FieldKind kind)
        {
            switch (kind)
            {
                case FieldKind.Byte: return 0xFF;
                case FieldKind.Word: return 0xFFFF;
                case FieldKind.Dword: return 0xFFFFFFFFL;
                case FieldKind.Port: return 0x2F;   // 端口 0x00-0x2F（0-47）
                default: return 0xFF;
            }
        }

        /// <summary>
        /// 校验字段值在该字段类型能表示的范围内，越界抛 <see cref="ArgumentOutOfRangeException"/>。
        ///
        /// 必须做这一步：字段值最终会被 (byte)/(int) 转换并写进发给硬件的帧，越界值会被静默截断——
        /// 例如 300 变成 0x2C、70000（0.1W/LSB）变成 446.4W。这是配置 PoE 供电预算与
        /// UVLO/OVLO 阈值的工具，截断后的值会被真实写进给受电设备供电的硬件。
        /// 异常信息里带上字段名，现场才好定位是哪一个输入框。
        /// </summary>
        public static void ValidateFieldValue(FieldDef field, long value)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));

            // 先按设备实际接受的范围（如果这个字段声明了），否则按字段类型能表示的范围。
            // 前者更严：Bank ID 是 Byte，但设备只认 0x00-0x07。
            long min = field.MinValue.HasValue ? field.MinValue.Value : 0;
            long max = field.MaxValue.HasValue ? field.MaxValue.Value : MaxFor(field.Kind);

            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(field.Name,
                    string.Format("「{0}」的值 {1} 超出设备接受的范围（{2} - {3}）。",
                        field.Name, value, Describe(min), Describe(max)));
        }

        private static string Describe(long value)
        {
            return value > 9
                ? string.Format("0x{0:X}", value)
                : value.ToString();
        }

        /// <summary>
        /// 校验字段值后组装帧——**所有调用方都应走这里，而不是直接调 <see cref="CommandDef.Build"/>**。
        ///
        /// 为什么必须在 Build 之前校验：各命令的 Build lambda 里是 `(byte)v[0]` / `(int)v[1]`
        /// 这样的直接转换，字节字段的截断就发生在 lambda 内部，底层写入函数看不到。
        /// 这里是唯一能按 FieldKind 判断合法范围的地方。
        /// </summary>
        public static byte[] BuildChecked(CommandDef cmd, byte seq, long[] values)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            if (values == null) throw new ArgumentNullException(nameof(values));

            FieldDef[] fields = cmd.Fields ?? new FieldDef[0];
            if (values.Length != fields.Length)
                throw new ArgumentException(
                    string.Format("{0} 需要 {1} 个参数，实际传入 {2} 个。", cmd.Key, fields.Length, values.Length),
                    nameof(values));

            for (int i = 0; i < fields.Length; i++)
                ValidateFieldValue(fields[i], values[i]);

            return cmd.Build(seq, values);
        }

        public static readonly List<CommandDef> All = new List<CommandDef>
        {
            // =============================================================
            //  控制命令（配置设置 0x00-0x19）
            // =============================================================
            Def("0x00", "全局使能设置", CatControl,
                "使能/禁用所有端口。禁用：所有端口保持空闲，不做检测与分级；使能：正常检测分级并可上电。\n" +
                "参数：0x00 = 禁用，0x01 = 使能。响应在 1 秒内返回，响应前不得再发命令。",
                (seq, v) => Rtl8239CommandBuilder.GlobalEnableSet(seq, (byte)v[0]),
                B("使能值（0x00 禁用 / 0x01 使能）", 0x01)),

            Def("0x01", "端口使能设置", CatControl,
                "使能/禁用指定端口。\n值：0x00 = 禁用，0x01 = 使能，0x02 = 半自动模式下强制上电。",
                (seq, v) => Rtl8239CommandBuilder.PortEnableSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("值（0x00 禁用 / 0x01 使能 / 0x02 强制上电）", 0x01)),

            Def("0x02", "全局复位设置", CatControl,
                "复位整个 PoE 子系统。\n参数：0x00 = 不复位，0x01 = 复位。",
                (seq, v) => Rtl8239CommandBuilder.GlobalResetSet(seq, (byte)v[0]),
                B("复位值（0x00 不复位 / 0x01 复位）", 0x00)),

            Def("0x03", "端口复位设置", CatControl,
                "将指定端口状态机复位到空闲、配置值恢复默认。\n值：0x00 = 不复位，0x01 = 复位。",
                (seq, v) => Rtl8239CommandBuilder.PortResetSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("值（0x00 不复位 / 0x01 复位）", 0x01)),

            Def("0x04", "全局功率源设置", CatControl,
                "设置指定功率 bank 的总功率与保留功率，单位 0.1W/LSB。\n保留功率必须小于总功率。Bank ID 0x00-0x07 有效。",
                (seq, v) => Rtl8239CommandBuilder.GlobalPowerSourceSet(seq, (byte)v[0], (int)v[1], (int)v[2]),
                Ranged("Bank ID（0x00-0x07）", 0x00, 0x00, 0x07),
                W("总功率（0.1W/LSB）", 3000),
                W("保留功率（0.1W/LSB）", 300)),

            Def("0x05", "端口映射使能设置", CatControl,
                "端口映射开始/结束。开始(0x00)会复位运行时配置；结束(0x01)会把配置（含映射）保存到 MCU flash。\n" +
                "响应在 2 秒内返回，响应前不得再发命令。",
                (seq, v) => Rtl8239CommandBuilder.PortMappingEnableSet(seq, (byte)v[0], (byte)v[1]),
                B("映射（0x00 开始 / 0x01 结束）", 0x01),
                Ranged("最大端口数（0-48）", 48, 0, 48)),

            Def("0x06", "端口对映射设置", CatControl,
                "配置逻辑端口的芯片索引与主/副通道（单端口）。\n" +
                "4-Pair：0x00 = 2-pair 模式，0x01 = 4-pair 模式。芯片地址连续时「芯片地址」填 0xFF。",
                (seq, v) => Rtl8239CommandBuilder.PortPairMappingSet(seq, (byte)v[0], (byte)v[1],
                    (byte)v[2], (byte)v[3], (byte)v[4], (byte)v[5]),
                Ranged("端口（0x00-0x2F）", 0x00, 0x00, 0x2F),
                B("4-Pair（0x00 2pair / 0x01 4pair）", 0x00),
                Ranged("芯片索引（0-0xE）", 0x00, 0, 0x0E),
                Ranged("主通道（0-7）", 0x00, 0, 7),
                B("副通道（0-7）", 0xFF),   // 0xFF = 不使用副通道，所以不能一刀切按 0-7 卡
                B("芯片地址（0xFF = 连续）", 0xFF)),

            Def("0x08", "端口功能模式设置", CatControl,
                "设置端口功能模式。\n值：0x00 = Auto，0x01 = Semi-auto，0x02 = Manual。",
                (seq, v) => Rtl8239CommandBuilder.PortFunctionModeSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("模式（0=Auto / 1=Semi / 2=Manual）", 0x00)),

            Def("0x09", "端口检测类型设置", CatControl,
                "决定是否对 Legacy PD 分级、分级失败的 PD 是否可上电。\n" +
                "Sifos 测试时用 0/2/4，否则 det_range/det_cc 会失败。",
                (seq, v) => Rtl8239CommandBuilder.PortDetectionTypeSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("检测类型（Sifos 用 0/2/4）", 0x00)),

            Def("0x0A", "端口检测触发设置（已废弃）", CatControl,
                "强制端口在 manual 模式下执行一次检测。\n序列号兼作模式：0x00 = Set，0x01 = Get。",
                (seq, v) => Rtl8239CommandBuilder.PortDetectionTriggerSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("值（0x00 不触发 / 0x01 触发）", 0x01)),

            Def("0x0B", "端口分级触发设置", CatControl,
                "强制端口执行一次分级。\n值：0x00 = None，0x01 = 强制分级。Sifos 测试时勿用。",
                (seq, v) => Rtl8239CommandBuilder.PortClassTriggerSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("值（0x00 / 0x01 强制分级）", 0x01)),

            Def("0x0C", "端口浪涌模式设置", CatControl,
                "设置端口 inrush 限制。\n值：0=802.3af，1=af 高浪涌，2=预 at，3=at，4=预 bt3，5=bt3，6=bt4，7=预 bt4，9=at ALT B。",
                (seq, v) => Rtl8239CommandBuilder.PortInrushModeSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("inrush 模式（0=af / 3=at / 6=bt4）", 0x03)),

            Def("0x0D", "端口强制浪涌设置", CatControl,
                "端口 inrush 提升一级。\n值：0x00 = 禁用，0x01 = 使能。对 af 高浪涌/预 bt 模式无效。",
                (seq, v) => Rtl8239CommandBuilder.PortForceInrushSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("值（0x00 禁用 / 0x01 使能）", 0x00)),

            Def("0x0E", "全局参数设置", CatControl,
                "设置 UVLO / OVLO 阈值（原始 LSB 值）。\nUVLO = 33V + X×64.45mV；OVLO = 57V + X×64.45mV（X 为 0x00-0x2F）。",
                (seq, v) => Rtl8239CommandBuilder.GlobalParametersSet(seq, (byte)v[0], (byte)v[1]),
                Ranged("UVLO（原始值，0x00-0x2F）", 0x00, 0x00, 0x2F),
                B("OVLO（原始值）", 0x00)),

            Def("0x0F", "端口断连类型设置", CatControl,
                "设置端口断连类型。\n值：0x00 = 禁用 MPS（低电流不关断），0x02 = 使能 MPS，0x03 = 上电 700ms 后使能 MPS。",
                (seq, v) => Rtl8239CommandBuilder.PortDisconnectTypeSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("断连类型（0 / 2 / 3）", 0x02)),

            Def("0x10", "全局功率管理模式设置", CatControl,
                "设置全局功率管理模式。\n值：0 = 无，1 = 静态+优先级，2 = 动态+优先级，3 = 静态无优先级，4 = 动态无优先级。",
                (seq, v) => Rtl8239CommandBuilder.GlobalPowerManagementModeSet(seq, (byte)v[0]),
                Ranged("模式（0-4）", 0x00, 0, 4)),

            Def("0x11", "全局功率管理扩展设置", CatControl,
                "系统预分配功能。\n值：0x00 = 使能，其它 = 禁用。",
                (seq, v) => Rtl8239CommandBuilder.GlobalPowerManagementModeExtendedSet(seq, (byte)v[0]),
                B("预分配（0x00 使能）", 0x00)),

            Def("0x12", "端口最大功率类型设置", CatControl,
                "设置端口最大功率类型。\n值：0x01 = 按分级，0x02 = 用户定义（配 0x13/0x14）。",
                (seq, v) => Rtl8239CommandBuilder.PortMaxPowerTypeSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("类型（0x01 分级 / 0x02 用户定义）", 0x01)),

            Def("0x13", "端口最大功率值设置", CatControl,
                "设置端口最大功率值，单位 0.2W/LSB（最大 51W）。\n仅在用户定义模式(0x12=0x02)下生效。",
                (seq, v) => Rtl8239CommandBuilder.PortMaxPowerValueSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("功率值（0.2W/LSB，默认 0x96=30W）", 0x96)),

            Def("0x14", "端口最大功率值扩展设置", CatControl,
                "设置端口最大功率值，单位 0.4W/LSB（最大 102W）。\n仅在用户定义模式(0x12=0x02)下生效。",
                (seq, v) => Rtl8239CommandBuilder.PortMaxPowerValueExtendedSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("功率值（0.4W/LSB，默认 0x4B=30W）", 0x4B)),

            Def("0x15", "端口优先级设置", CatControl,
                "设置端口分配优先级。\n值：0x00 = 低，0x01 = 中，0x02 = 高，0x03 = 紧急。",
                (seq, v) => Rtl8239CommandBuilder.PortPrioritySet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("优先级（0/1/2/3）", 0x00)),

            Def("0x16", "全局端口事件掩码设置", CatControl,
                "设置全局端口事件掩码。\nBIT1 = 断连事件掩码，BIT2 = 故障事件掩码（short/OVLO/UVLO/过载），1 = 使能。",
                (seq, v) => Rtl8239CommandBuilder.GlobalPortEventMaskSet(seq, (byte)v[0]),
                B("掩码（BIT1 断连 / BIT2 故障）", 0x00)),

            Def("0x18", "端口强制检测分级上电", CatControl,
                "仅 manual 模式下强制端口上电。\n值：0x00 = 复位，0x03 = 强制上电。",
                (seq, v) => Rtl8239CommandBuilder.PortTriggerDetClsPwrSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("值（0x00 复位 / 0x03 强制上电）", 0x03)),

            Def("0x19", "端口线缆类型设置", CatControl,
                "设置 4Pair 模式下的线缆类型（仅 RTL8239C）。\n值：0x00 = 正常，0x01 = 短线。",
                (seq, v) => Rtl8239CommandBuilder.PortCableTypeSet(seq, new PortValue((byte)v[0], (byte)v[1])),
                P(), B("线缆（0x00 正常 / 0x01 短线）", 0x00)),

            // =============================================================
            //  查询命令（状态获取 0x40-0x50）
            // =============================================================
            Def("0x40", "全局状态获取", CatQuery,
                "获取 PoE 子系统基本状态：最大端口数、端口映射、设备 ID、软件版本、MCU 类型、配置状态、扩展版本。",
                (seq, v) => Rtl8239CommandBuilder.GlobalStatusGet(seq)),

            Def("0x41", "全局功率状态获取", CatQuery,
                "获取系统功率状态：已分配功率、可用功率、bank ID、当前功率（单位 0.1W/LSB）。",
                (seq, v) => Rtl8239CommandBuilder.GlobalPowerStatusGet(seq)),

            Def("0x42", "端口状态获取", CatQuery,
                "获取指定端口状态：电源状态、故障类型、检测/分级结果、PD 类型、连接检查结果。",
                (seq, v) => Rtl8239CommandBuilder.PortStatusGet(seq, (byte)v[0]),
                P()),

            Def("0x43", "端口组状态获取", CatQuery,
                "一次获取一组（4 个）端口的状态。\n组 0x00-0x0B 有效（8 口系统：组 0 = 端口 0-3）。",
                (seq, v) => Rtl8239CommandBuilder.PortGroupStatusGet(seq, (byte)v[0]),
                Ranged("组索引（0x00-0x0B）", 0x00, 0x00, 0x0B)),

            Def("0x44", "端口测量获取", CatQuery,
                "获取端口电压(64.45mV/LSB)、电流(1mA/LSB)、IC 中心温度、实际功率(0.1W/LSB)。",
                (seq, v) => Rtl8239CommandBuilder.PortMeasurementGet(seq, (byte)v[0]),
                P()),

            Def("0x45", "端口 MIB 计数器获取", CatQuery,
                "获取端口 MIB 计数：MPS 缺失、过载、短路、功率拒绝、无效签名。\n复位标志：0x00 不复位，0x01 读后复位。",
                (seq, v) => Rtl8239CommandBuilder.PortMibCounterGet(seq, (byte)v[0], (byte)v[1]),
                P(), B("复位标志（0x00 / 0x01）", 0x00)),

            Def("0x46", "端口事件状态获取", CatQuery,
                "获取事件掩码、系统事件状态、48 位端口事件位图。\n清除标志：0x00 不清除，0x01 读后清除。",
                (seq, v) => Rtl8239CommandBuilder.PortEventStatusGet(seq, (byte)v[0]),
                B("清除标志（0x00 / 0x01）", 0x00)),

            Def("0x47", "全局复位原因获取", CatQuery,
                "获取复位原因及异常芯片 I2C 地址。\n清除标志：0x00 不清除，其它 读后清除。",
                (seq, v) => Rtl8239CommandBuilder.GlobalResetReasonGet(seq, (byte)v[0]),
                B("清除标志（0x00 不清除）", 0x00)),

            Def("0x48", "端口基本配置获取", CatQuery,
                "获取端口基本配置：使能状态、功能模式、检测类型、断连类型、pair 类型、线缆类型。",
                (seq, v) => Rtl8239CommandBuilder.PortBasicConfigurationGet(seq, (byte)v[0]),
                P()),

            Def("0x49", "端口扩展配置获取", CatQuery,
                "获取端口扩展配置：inrush 模式、功率限制模式、最大功率(0.4W)、优先级、芯片地址、主/副通道。",
                (seq, v) => Rtl8239CommandBuilder.PortExtendedConfigurationGet(seq, (byte)v[0]),
                P()),

            Def("0x4A", "全局参数获取", CatQuery,
                "获取 UVLO/OVLO 阈值、预分配状态、PSE 芯片数量、SDK 不支持的芯片数量。",
                (seq, v) => Rtl8239CommandBuilder.GlobalParametersGet(seq)),

            Def("0x4B", "全局功率管理配置获取", CatQuery,
                "获取 PM 模式与各 bank 总/保留功率（0.1W/LSB）。\n注意：本命令 Byte1 为 Bank ID（非序列号）。",
                (seq, v) => Rtl8239CommandBuilder.GlobalPMConfigurationGet((byte)v[0]),
                Ranged("Bank ID（0x00-0x07）", 0x00, 0x00, 0x07)),

            Def("0x4C", "全局设备地址获取", CatQuery,
                "读取 PSE 芯片的 I2C 地址（每次 8 颗，空位填 0xFF）。\n索引 0x00-0x0B 有效。",
                (seq, v) => Rtl8239CommandBuilder.GlobalDeviceAddressGet(seq, (byte)v[0]),
                Ranged("索引（0x00-0x0B）", 0x00, 0x00, 0x0B)),

            Def("0x4D", "端口功能模式获取", CatQuery,
                "读取端口功能模式：auto / semi-auto / manual。",
                (seq, v) => Rtl8239CommandBuilder.PortFunctionModeGet(seq, (byte)v[0]),
                P()),

            Def("0x4E", "通道状态获取", CatQuery,
                "获取端口主/副通道的检测、分级、故障、电源状态。",
                (seq, v) => Rtl8239CommandBuilder.ChannelStatusGet(seq, (byte)v[0]),
                P()),

            Def("0x4F", "端口通道电压电流获取", CatQuery,
                "获取端口主/副通道的电压(64.45mV/LSB)与电流(1mA/LSB)。",
                (seq, v) => Rtl8239CommandBuilder.PortChannelVoltageCurrentGet(seq, (byte)v[0]),
                P()),

            Def("0x50", "系统芯片类型信息获取", CatQuery,
                "获取所有 PSE 芯片类型（bt/at），最多 12 颗芯片。",
                (seq, v) => Rtl8239CommandBuilder.SystemChipTypeInfoGet(seq)),

            // =============================================================
            //  杂项命令（0xC0）
            // =============================================================
            Def("0xC0-00", "跳转到 Loader", CatMisc,
                "跳转到 Loader 区用于固件升级，会擦除固件信息。\n响应后需等待（GD303 约 8 秒 / GD230 约 2 秒）再发下一条命令。",
                (seq, v) => Rtl8239CommandBuilder.JumpToLoader()),

            Def("0xC0-01", "配置信息保存", CatMisc,
                "保存配置到 flash（系统参数 + 每端口参数）。\n日期/版本仅支持 RTL8238C/8239C。",
                (seq, v) => Rtl8239CommandBuilder.ConfigurationSave((byte)v[0], (byte)v[1], (byte)v[2], (byte)v[3], (byte)v[4]),
                B("年", 25), B("月", 8), B("日", 25), B("高版本", 1), B("低版本", 0)),

            Def("0xC0-02", "配置信息清除", CatMisc,
                "从 flash 清除配置信息。",
                (seq, v) => Rtl8239CommandBuilder.ConfigurationClear()),

            Def("0xC0-03", "配置版本保存", CatMisc,
                "保存配置日期/版本到 ram 和 flash。仅支持 RTL8238B/8239。",
                (seq, v) => Rtl8239CommandBuilder.ConfigurationVersionSave((byte)v[0], (byte)v[1], (byte)v[2], (byte)v[3], (byte)v[4]),
                B("年", 25), B("月", 8), B("日", 25), B("高版本", 1), B("低版本", 0)),

            Def("0xC0-04", "配置版本获取", CatMisc,
                "读取配置的日期与版本。",
                (seq, v) => Rtl8239CommandBuilder.ConfigurationVersionGet()),

            Def("0xC0-05", "配置信息复位", CatMisc,
                "将配置信息复位为默认设置。",
                (seq, v) => Rtl8239CommandBuilder.ConfigurationReset()),

            Def("0xC0-06", "配置信息初始化", CatMisc,
                "检查配置信息有效性，判断是否需要复位。",
                (seq, v) => Rtl8239CommandBuilder.ConfigurationInitial()),

            Def("0xC0-40", "跳转到 App", CatMisc,
                "App 下载完成后校验并跳转到 App（Loader 命令）。校验失败会擦除 App/Firmware 区。",
                (seq, v) => Rtl8239CommandBuilder.JumpToApp()),

            // =============================================================
            //  调试命令（0xF0 / 0xF1）
            // =============================================================
            Def("0xF0", "芯片寄存器写", CatDebug,
                "直接写指定 PoE 芯片的 32 位寄存器。\n芯片地址 0x20-0x37 有效；寄存器地址与值为 32 位。",
                (seq, v) => Rtl8239CommandBuilder.ChipRegisterSet(seq, (byte)v[0], (uint)v[1], (uint)v[2]),
                Ranged("芯片地址（0x20-0x37）", 0x20, 0x20, 0x37),
                D("寄存器地址（32 位）", 0x00000000),
                D("寄存器值（32 位）", 0x00000000)),

            Def("0xF1", "芯片寄存器读", CatDebug,
                "直接读指定 PoE 芯片的 32 位寄存器。\n芯片地址 0x20-0x37 有效。",
                (seq, v) => Rtl8239CommandBuilder.ChipRegisterGet(seq, (byte)v[0], (uint)v[1]),
                Ranged("芯片地址（0x20-0x37）", 0x20, 0x20, 0x37),
                D("寄存器地址（32 位）", 0x00000000)),
        };
    }
}
