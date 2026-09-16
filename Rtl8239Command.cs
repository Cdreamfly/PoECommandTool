using System;
using System.Text;

namespace WpfApp1
{
    // =====================================================================
    //  RTL8239 PoE 控制器主机命令组装库
    //  依据 Realtek《RTL8239 PoE Controller Host Command Guide》Rev.2.2
    //
    //  帧结构（App 命令，固定 12 字节）：
    //    Byte0        命令 ID
    //    Byte1        序列号（部分命令为 Bank ID / 子命令）
    //    Byte2~Byte10 数据
    //    Byte11       校验和 = (Byte0 + ... + Byte10) & 0xFF
    //
    //  约定：
    //    - App 命令中未使用的字节一律填 0xFF
    //    - 多字节字段（功率、偏移、寄存器地址/值等）一律采用小端序（LSB 在前）
    //    - 逻辑端口索引 0x00-0x2F 有效（端口 0-47）
    // =====================================================================

    #region 枚举

    /// <summary>端口功能模式（CMD 0x08 / 0x4D）</summary>
    public enum PortFunctionMode : byte
    {
        Auto = 0x00,
        SemiAuto = 0x01,
        Manual = 0x02,
    }

    /// <summary>端口浪涌（inrush）模式（CMD 0x0C）</summary>
    public enum InrushMode : byte
    {
        Ieee802_3af = 0x00,
        Ieee802_3afHighInrush = 0x01,
        PreIeee802_3at = 0x02,
        Ieee802_3at = 0x03,
        PreIeee802_3btType3 = 0x04,
        Ieee802_3btType3 = 0x05,
        Ieee802_3btType4 = 0x06,
        PreIeee802_3btType4 = 0x07,
        Ieee802_3atAltB = 0x09,
    }

    /// <summary>端口优先级（CMD 0x15）</summary>
    public enum PortPriority : byte
    {
        Low = 0x00,
        Medium = 0x01,
        High = 0x02,
        Critical = 0x03,
    }

    /// <summary>端口最大功率类型（CMD 0x12）</summary>
    public enum MaxPowerType : byte
    {
        Reserved = 0x00,
        ClassBased = 0x01,
        UserDefined = 0x02,
    }

    /// <summary>断连类型（CMD 0x0F）</summary>
    public enum DisconnectType : byte
    {
        DisableMps = 0x00,
        Reserved = 0x01,
        EnableMps = 0x02,
        EnableMpsAfter700ms = 0x03,
    }

    /// <summary>全局功率管理模式（CMD 0x10）</summary>
    public enum PowerManagementMode : byte
    {
        None = 0x00,
        StaticWithPriority = 0x01,
        DynamicWithPriority = 0x02,
        StaticWithoutPriority = 0x03,
        DynamicWithoutPriority = 0x04,
    }

    /// <summary>线缆类型（CMD 0x19，仅 4Pair 模式 RTL8239C）</summary>
    public enum CableType : byte
    {
        Normal = 0x00,
        Short = 0x01,
    }

    #endregion

    /// <summary>一个「端口 / 值」对，用于多条 (Port,VAL)×4 形式的命令。</summary>
    public struct PortValue
    {
        public byte Port;
        public byte Value;

        public PortValue(byte port, byte value)
        {
            Port = port;
            Value = value;
        }
    }

    /// <summary>RTL8239 命令帧组装器。</summary>
    public static class Rtl8239CommandBuilder
    {
        public const byte Rsvd = 0xFF;
        public const int AppFrameLength = 12;

        // -----------------------------------------------------------------
        //  核心：帧组装 / 校验和
        // -----------------------------------------------------------------

        /// <summary>计算 8 位累加校验和（对 [0, length-1] 求和，丢弃进位）。</summary>
        public static byte Checksum(byte[] frame, int length)
        {
            int sum = 0;
            for (int i = 0; i < length; i++) sum += frame[i];
            return (byte)(sum & 0xFF);
        }

        // 新建 12 字节帧：所有字节默认 0xFF，写入 cmd 与 byte1（序列号/子命令/Bank ID）。
        private static byte[] NewFrame(byte cmd, byte byte1)
        {
            var f = new byte[AppFrameLength];
            for (int i = 0; i < f.Length; i++) f[i] = Rsvd;
            f[0] = cmd;
            f[1] = byte1;
            return f;
        }

        // 计算校验和并写入 Byte11，返回帧。
        private static byte[] Finish(byte[] f)
        {
            f[AppFrameLength - 1] = Checksum(f, AppFrameLength - 1);
            return f;
        }

        // 填充 (Port,VAL)×4 到 Byte2..Byte9，未用对保持 0xFF。
        private static void FillPortValPairs(byte[] f, PortValue[] pairs)
        {
            for (int i = 0; i < 4; i++)
            {
                if (i < pairs.Length)
                {
                    f[2 + i * 2] = pairs[i].Port;
                    f[3 + i * 2] = pairs[i].Value;
                }
            }
        }

        private static void CheckPairs(PortValue[] pairs)
        {
            if (pairs.Length > 4)
                throw new ArgumentException("最多支持 4 组 (Port, VAL) 对。", "pairs");
        }

        private static void PutUInt16LE(byte[] f, int index, int value)
        {
            // 不静默截断：越界值会让设备收到一个「看起来合法」的错误配置
            // （例如功率字段 70000 → 0x1170 → 446.4W），必须在组装帧之前就暴露。
            if (value < 0 || value > 0xFFFF)
                throw new ArgumentOutOfRangeException("value",
                    string.Format("16 位字段的值 {0} 超出 0x0000-0xFFFF。", value));
            f[index] = (byte)(value & 0xFF);
            f[index + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void PutUInt32LE(byte[] f, int index, long value)
        {
            if (value < 0 || value > 0xFFFFFFFFL)
                throw new ArgumentOutOfRangeException("value",
                    string.Format("32 位字段的值 {0} 超出 0x00000000-0xFFFFFFFF。", value));
            f[index] = (byte)(value & 0xFF);
            f[index + 1] = (byte)((value >> 8) & 0xFF);
            f[index + 2] = (byte)((value >> 16) & 0xFF);
            f[index + 3] = (byte)((value >> 24) & 0xFF);
        }

        // -----------------------------------------------------------------
        //  配置设置命令（0x00-0x19）
        // -----------------------------------------------------------------

        /// <summary>0x00 Global Enable Set：使能/禁用所有端口。val: 0x00 禁用 / 0x01 使能。</summary>
        public static byte[] GlobalEnableSet(byte seq, byte val)
        {
            var f = NewFrame(0x00, seq);
            f[2] = val;
            return Finish(f);
        }

        /// <summary>0x01 Port Enable Set：使能/禁用指定端口（最多 4 组）。val: 0x00 禁用 / 0x01 使能 / 0x02 半自动强制上电。</summary>
        public static byte[] PortEnableSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x01, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x02 Global Reset Set：复位整个 PoE 子系统。val: 0x00 不复位 / 0x01 复位。</summary>
        public static byte[] GlobalResetSet(byte seq, byte val)
        {
            var f = NewFrame(0x02, seq);
            f[2] = val;
            return Finish(f);
        }

        /// <summary>0x03 Port Reset Set：复位指定端口（最多 4 组）。val: 0x00 不复位 / 0x01 复位。</summary>
        public static byte[] PortResetSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x03, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x04 Global Power Source Set：设置功率 bank 的总额与保留功率（单位 0.1W/LSB）。</summary>
        public static byte[] GlobalPowerSourceSet(byte seq, byte bankId, int totalPower, int reservedPower)
        {
            var f = NewFrame(0x04, seq);
            f[2] = bankId;
            PutUInt16LE(f, 3, totalPower);
            PutUInt16LE(f, 5, reservedPower);
            return Finish(f);
        }

        /// <summary>0x05 Port Mapping Enable Set：端口映射开始/结束。val: 0x00 开始 / 0x01 结束。</summary>
        public static byte[] PortMappingEnableSet(byte seq, byte val, byte maxPort)
        {
            var f = NewFrame(0x05, seq);
            f[2] = val;
            f[3] = maxPort;
            return Finish(f);
        }

        /// <summary>0x06 Port Pair Mapping Set：配置逻辑端口的设备/通道映射。addr 非 0xFF 时表示芯片 I2C 地址（非连续地址场景）。</summary>
        public static byte[] PortPairMappingSet(byte seq, byte port, byte fourPairEnable,
            byte chipIndex, byte priChannel, byte secChannel, byte addr)
        {
            var f = NewFrame(0x06, seq);
            f[2] = port;
            f[3] = fourPairEnable;
            f[4] = chipIndex;
            f[5] = priChannel;
            f[6] = secChannel;
            f[10] = addr;
            return Finish(f);
        }

        /// <summary>0x08 Port Function Mode Set：端口功能模式（最多 4 组）。</summary>
        public static byte[] PortFunctionModeSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x08, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x09 Port Detection Type Set：端口检测类型（最多 4 组）。</summary>
        public static byte[] PortDetectionTypeSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x09, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x0A Port Detection Trigger Set（已废弃，仅 manual 模式）。seq: 0x00 Set / 0x01 Get。</summary>
        public static byte[] PortDetectionTriggerSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x0A, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x0B Port Class Trigger Set：强制分级（最多 4 组）。</summary>
        public static byte[] PortClassTriggerSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x0B, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x0C Port Inrush Mode Set：端口浪涌模式（最多 4 组）。</summary>
        public static byte[] PortInrushModeSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x0C, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x0D Port Force Inrush Set：端口浪涌提升（最多 4 组）。val: 0x00 禁用 / 0x01 使能。</summary>
        public static byte[] PortForceInrushSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x0D, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x0E Global Parameters Set：设置 UVLO/OVLO 阈值（原始 LSB 值，非电压）。</summary>
        public static byte[] GlobalParametersSet(byte seq, byte uvlo, byte ovlo)
        {
            var f = NewFrame(0x0E, seq);
            f[2] = uvlo;
            f[4] = ovlo;
            return Finish(f);
        }

        /// <summary>0x0F Port Disconnect Type Set：端口断连类型（最多 4 组）。</summary>
        public static byte[] PortDisconnectTypeSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x0F, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x10 Global Power Management Mode Set：全局功率管理模式。</summary>
        public static byte[] GlobalPowerManagementModeSet(byte seq, byte mode)
        {
            var f = NewFrame(0x10, seq);
            f[2] = mode;
            return Finish(f);
        }

        /// <summary>0x11 Global Power Management Mode Extended Set：系统预分配功能。val: 0x00 使能 / 其它 禁用。</summary>
        public static byte[] GlobalPowerManagementModeExtendedSet(byte seq, byte val)
        {
            var f = NewFrame(0x11, seq);
            f[2] = val;
            return Finish(f);
        }

        /// <summary>0x12 Port Max Power Type Set：端口最大功率类型（最多 4 组）。</summary>
        public static byte[] PortMaxPowerTypeSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x12, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x13 Port Max Power Value Set（最大 51W，0.2W/LSB，最多 4 组）。</summary>
        public static byte[] PortMaxPowerValueSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x13, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x14 Port Max Power Value Extended Set（最大 102W，0.4W/LSB，最多 4 组）。</summary>
        public static byte[] PortMaxPowerValueExtendedSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x14, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x15 Port Priority Set：端口优先级（最多 4 组）。</summary>
        public static byte[] PortPrioritySet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x15, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x16 Global Port Event Mask Set：全局端口事件掩码。</summary>
        public static byte[] GlobalPortEventMaskSet(byte seq, byte mask)
        {
            var f = NewFrame(0x16, seq);
            f[2] = mask;
            return Finish(f);
        }

        /// <summary>0x18 Port Trigger Det CLS PWR（仅 Manual 模式，最多 4 组）。val: 0x00 复位 / 0x03 强制上电。</summary>
        public static byte[] PortTriggerDetClsPwrSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x18, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        /// <summary>0x19 Port Cable Type Set（仅 4Pair 模式 RTL8239C，最多 4 组）。</summary>
        public static byte[] PortCableTypeSet(byte seq, params PortValue[] pairs)
        {
            CheckPairs(pairs);
            var f = NewFrame(0x19, seq);
            FillPortValPairs(f, pairs);
            return Finish(f);
        }

        // -----------------------------------------------------------------
        //  配置与状态获取命令（0x40-0x50）
        // -----------------------------------------------------------------

        /// <summary>0x40 Global Status Get：获取全局状态。</summary>
        public static byte[] GlobalStatusGet(byte seq) => Finish(NewFrame(0x40, seq));

        /// <summary>0x41 Global Power Status Get：获取系统功率状态。</summary>
        public static byte[] GlobalPowerStatusGet(byte seq) => Finish(NewFrame(0x41, seq));

        /// <summary>0x42 Port Status Get：获取指定端口状态。</summary>
        public static byte[] PortStatusGet(byte seq, byte port)
        {
            var f = NewFrame(0x42, seq);
            f[2] = port;
            return Finish(f);
        }

        /// <summary>0x43 Port Group Status Get：获取端口组状态（每组 4 端口）。</summary>
        public static byte[] PortGroupStatusGet(byte seq, byte group)
        {
            var f = NewFrame(0x43, seq);
            f[2] = group;
            return Finish(f);
        }

        /// <summary>0x44 Port Measurement Get：获取端口电压/电流/温度/功率。</summary>
        public static byte[] PortMeasurementGet(byte seq, byte port)
        {
            var f = NewFrame(0x44, seq);
            f[2] = port;
            return Finish(f);
        }

        /// <summary>0x45 Port Mib Counter Get：获取端口 MIB 计数器。resetFlag: 0x00 不复位 / 0x01 读后复位。</summary>
        public static byte[] PortMibCounterGet(byte seq, byte port, byte resetFlag)
        {
            var f = NewFrame(0x45, seq);
            f[2] = port;
            f[3] = resetFlag;
            return Finish(f);
        }

        /// <summary>0x46 Port Event Status Get：获取端口事件状态。clearFlag: 0x00 不清除 / 0x01 读后清除。</summary>
        public static byte[] PortEventStatusGet(byte seq, byte clearFlag)
        {
            var f = NewFrame(0x46, seq);
            f[2] = clearFlag;
            return Finish(f);
        }

        /// <summary>0x47 Global Reset Reason Get：获取复位原因。clearFlag: 0x00 不清除 / 其它 读后清除。</summary>
        public static byte[] GlobalResetReasonGet(byte seq, byte clearFlag)
        {
            var f = NewFrame(0x47, seq);
            f[2] = clearFlag;
            return Finish(f);
        }

        /// <summary>0x48 Port Basic Configuration Get：获取端口基本配置。</summary>
        public static byte[] PortBasicConfigurationGet(byte seq, byte port)
        {
            var f = NewFrame(0x48, seq);
            f[2] = port;
            return Finish(f);
        }

        /// <summary>0x49 Port Extended Configuration Get：获取端口扩展配置。</summary>
        public static byte[] PortExtendedConfigurationGet(byte seq, byte port)
        {
            var f = NewFrame(0x49, seq);
            f[2] = port;
            return Finish(f);
        }

        /// <summary>0x4A Global Parameters Get：获取全局参数。</summary>
        public static byte[] GlobalParametersGet(byte seq) => Finish(NewFrame(0x4A, seq));

        /// <summary>0x4B Global PM Configuration Get：获取功率管理配置。注意：Byte1 为 Bank ID（非序列号）。</summary>
        public static byte[] GlobalPMConfigurationGet(byte bankId) => Finish(NewFrame(0x4B, bankId));

        /// <summary>0x4C Global Device Address Get：获取芯片 I2C 地址（idx 每次读 8 颗）。</summary>
        public static byte[] GlobalDeviceAddressGet(byte seq, byte idx)
        {
            var f = NewFrame(0x4C, seq);
            f[2] = idx;
            return Finish(f);
        }

        /// <summary>0x4D Port Function Mode Get：获取端口功能模式（最多 4 个端口）。</summary>
        public static byte[] PortFunctionModeGet(byte seq, params byte[] ports)
        {
            if (ports.Length > 4)
                throw new ArgumentException("最多支持 4 个端口。", "ports");
            var f = NewFrame(0x4D, seq);
            for (int i = 0; i < ports.Length; i++)
                f[2 + i * 2] = ports[i];
            return Finish(f);
        }

        /// <summary>0x4E Channel Status Get：获取端口双通道状态。</summary>
        public static byte[] ChannelStatusGet(byte seq, byte port)
        {
            var f = NewFrame(0x4E, seq);
            f[2] = port;
            return Finish(f);
        }

        /// <summary>0x4F Port Channel Voltage Current Get：获取端口双通道电压/电流。</summary>
        public static byte[] PortChannelVoltageCurrentGet(byte seq, byte port)
        {
            var f = NewFrame(0x4F, seq);
            f[2] = port;
            return Finish(f);
        }

        /// <summary>0x50 System Chip Type Information Get：获取所有 PSE 芯片类型信息。</summary>
        public static byte[] SystemChipTypeInfoGet(byte seq) => Finish(NewFrame(0x50, seq));

        // -----------------------------------------------------------------
        //  杂项命令（0xC0，App 范围）
        // -----------------------------------------------------------------

        /// <summary>0xC0-00 Jump To Loader：跳转到 Loader（用于固件升级）。</summary>
        public static byte[] JumpToLoader() => Finish(NewFrame(0xC0, 0x00));

        /// <summary>0xC0-01 Configuration Information Save：保存配置到 flash（日期/版本仅 RTL8238C/8239C）。</summary>
        public static byte[] ConfigurationSave(byte year, byte month, byte day, byte highVersion, byte lowVersion)
        {
            var f = NewFrame(0xC0, 0x01);
            f[2] = year;
            f[3] = month;
            f[4] = day;
            f[5] = highVersion;
            f[6] = lowVersion;
            return Finish(f);
        }

        /// <summary>0xC0-02 Configuration Information Clear：清除 flash 中的配置。</summary>
        public static byte[] ConfigurationClear() => Finish(NewFrame(0xC0, 0x02));

        /// <summary>0xC0-03 Configuration Version Save：保存配置版本（仅 RTL8238B/8239）。</summary>
        public static byte[] ConfigurationVersionSave(byte year, byte month, byte day, byte highVersion, byte lowVersion)
        {
            var f = NewFrame(0xC0, 0x03);
            f[2] = year;
            f[3] = month;
            f[4] = day;
            f[5] = highVersion;
            f[6] = lowVersion;
            return Finish(f);
        }

        /// <summary>0xC0-04 Configuration Version Get：获取配置版本。</summary>
        public static byte[] ConfigurationVersionGet() => Finish(NewFrame(0xC0, 0x04));

        /// <summary>0xC0-05 Configuration Information Reset：复位配置为默认值。</summary>
        public static byte[] ConfigurationReset() => Finish(NewFrame(0xC0, 0x05));

        /// <summary>0xC0-06 Configuration Information Initial：检查配置有效性。</summary>
        public static byte[] ConfigurationInitial() => Finish(NewFrame(0xC0, 0x06));

        /// <summary>0xC0-40 Jump To App：跳转到 App（Loader 命令）。</summary>
        public static byte[] JumpToApp() => Finish(NewFrame(0xC0, 0x40));

        // -----------------------------------------------------------------
        //  固件/应用下载命令（Loader 范围，变长，无校验和）
        // -----------------------------------------------------------------

        /// <summary>0xC0-80~83 App Download：下载应用镜像（变长帧，无校验和）。sub: 0x80-0x83 对应 64K 块。</summary>
        public static byte[] AppDownload(byte sub, ushort imageOffset, byte[] data)
        {
            if (sub < 0x80 || sub > 0x83)
                throw new ArgumentOutOfRangeException("sub", "sub 必须为 0x80-0x83。");
            if (data == null || data.Length < 4 || data.Length > 32 || data.Length % 4 != 0)
                throw new ArgumentException("data 长度须为 4~32 字节且 4 字节对齐。", "data");

            var f = new byte[4 + data.Length];
            f[0] = 0xC0;
            f[1] = sub;
            PutUInt16LE(f, 2, imageOffset);
            Array.Copy(data, 0, f, 4, data.Length);
            return f;
        }

        /// <summary>0xCA Firmware Download：下载固件到外部 flash（变长帧，无校验和）。</summary>
        public static byte[] FirmwareDownload(byte seq, ushort imageOffset, byte[] data)
        {
            if (data == null || data.Length != 4 && data.Length != 8 && data.Length != 16 && data.Length != 32)
                throw new ArgumentException("data 长度须为 4 / 8 / 16 / 32 字节。", "data");

            var f = new byte[4 + data.Length];
            f[0] = 0xCA;
            f[1] = seq;
            PutUInt16LE(f, 2, imageOffset);
            Array.Copy(data, 0, f, 4, data.Length);
            return f;
        }

        // -----------------------------------------------------------------
        //  调试命令（0xF0 / 0xF1）
        // -----------------------------------------------------------------

        /// <summary>0xF0 Chip Register Set：直接写芯片寄存器。chipAddr: 0x20-0x37。</summary>
        public static byte[] ChipRegisterSet(byte seq, byte chipAddr, uint regAddr, uint regValue)
        {
            var f = NewFrame(0xF0, seq);
            f[2] = chipAddr;
            PutUInt32LE(f, 3, regAddr);
            PutUInt32LE(f, 7, regValue);
            return Finish(f);
        }

        /// <summary>0xF1 Chip Register Get：直接读芯片寄存器。chipAddr: 0x20-0x37。</summary>
        public static byte[] ChipRegisterGet(byte seq, byte chipAddr, uint regAddr)
        {
            var f = NewFrame(0xF1, seq);
            f[2] = chipAddr;
            PutUInt32LE(f, 3, regAddr);
            return Finish(f);
        }

        // -----------------------------------------------------------------
        //  辅助
        // -----------------------------------------------------------------

        /// <summary>将字节数组格式化为 "AA BB CC ..." 形式的十六进制字符串（用于显示/日志）。</summary>
        public static string ToHex(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            var sb = new StringBuilder(data.Length * 3);
            for (int i = 0; i < data.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>校验帧的校验和是否正确。</summary>
        public static bool IsChecksumValid(byte[] frame, int length)
        {
            if (frame == null || length < 1 || length > frame.Length) return false;
            return frame[length - 1] == Checksum(frame, length - 1);
        }

        /// <summary>校验响应帧：长度 ≥12、Byte1（序列号）匹配、校验和正确。</summary>
        public static bool IsResponseValid(byte[] response, byte expectedByte1)
        {
            if (response == null || response.Length < AppFrameLength) return false;
            if (response[1] != expectedByte1) return false;
            return IsChecksumValid(response, AppFrameLength);
        }
    }
}
