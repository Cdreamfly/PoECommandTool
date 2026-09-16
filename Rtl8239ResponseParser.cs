using System;

namespace WpfApp1
{
    // =====================================================================
    //  RTL8239 PoE 控制器响应帧解析库
    //  依据 Realtek《RTL8239 PoE Controller Host Command Guide》Rev.2.2 第 5 节
    //
    //  响应帧结构：
    //    App 命令（0x40-0x50 / 0xC0 的 App 子命令 / 0xF1）固定 12 字节：
    //      Byte0        响应命令 ID（0xC0 命令此处恒为 0xC0，子命令在 Byte1）
    //      Byte1        序列号（0x4B 为 Bank ID，0xC0 为子命令）
    //      Byte2~Byte10 数据
    //      Byte11       校验和 = (Byte0 + ... + Byte10) & 0xFF
    //    Loader 命令（0xC0-80~83 / 0xCA）应答固定 4 字节且无校验和：
    //      Byte0 命令 ID | Byte1 子命令/序列号 | Byte2-3 镜像偏移
    //
    //  约定：
    //    - 每个解析方法先校验帧长度、命令 ID 与校验和，非法帧抛出异常
    //    - 多字节字段一律按小端序解析
    //    - 物理量字段均已换算为带单位的值（电压 mV、电流 mA、功率 W、温度 ℃、电压 V）
    //    - 需按 STS1 分流的字段（0x42/0x43 的故障类型与检测/分级结果）解析为可空类型，
    //      不适用时为 null 且有对应文字说明，避免把错误类型值误当成检测/分级结果
    // =====================================================================

    /// <summary>
    /// 响应里多字节字段（电压/电流/温度/功率、设备 ID、功率值等）的字节序。
    ///
    /// 手册并没有明确规定字节序，最初的实现按小端写；但真机实测（0x44 回包）是大端：
    /// 回包 <c>44 01 00 03 36 02 21 00 C5 01 21 88</c> 按大端解出 52.98 V / 545 mA /
    /// 28.75 ℃ / 28.9 W，且 V×I 恰好等于功率；按小端解则得到 891 V / 8450 mA / −62765 ℃
    /// 这种毫无物理意义的数。所以默认取大端，界面提供切换。
    /// </summary>
    public enum ByteOrder
    {
        /// <summary>高字节在前（真机实测的顺序）。</summary>
        BigEndian = 0,

        /// <summary>低字节在前（最初按手册假设的顺序，界面可切换）。</summary>
        LittleEndian = 1,
    }

    #region 枚举

    /// <summary>端口电源状态 / 故障状态。</summary>
    public enum PortPowerState : byte
    {
        Disabled = 0x00,
        Searching = 0x01,
        DeliveringPower = 0x02,
        Reserved3 = 0x03,
        Fault = 0x04,
        Reserved5 = 0x05,
        RequestingPower = 0x06,
    }

    /// <summary>检测结果。</summary>
    public enum DetectionResult : byte
    {
        Unknown = 0x00,
        ShortCircuit = 0x01,
        HighCap = 0x02,
        Rlow = 0x03,
        ValidPd = 0x04,
        Rhigh = 0x05,
        OpenCircuit = 0x06,
        FetFailure = 0x07,
    }

    /// <summary>故障 / 错误类型。</summary>
    public enum FaultType : byte
    {
        Ovlo = 0x00,
        MpsAbsent = 0x01,
        Short = 0x02,
        Overload = 0x03,
        PowerDenied = 0x04,
        ThermalShutdown = 0x05,
        InrushFail = 0x06,
        Uvlo = 0x07,
        Gotp = 0x0E,
    }

    /// <summary>分级结果。</summary>
    public enum ClassificationResult : byte
    {
        Class0 = 0x00,
        Class1 = 0x01,
        Class2 = 0x02,
        Class3 = 0x03,
        Class4 = 0x04,
        Class5 = 0x05,
        Class6 = 0x06,
        Class7 = 0x07,
        Class8 = 0x08,
        TreatedAsClass0 = 0x0C,
        ClassMismatch = 0x0E,
        ClassOverCurrent = 0x0F,
    }

    /// <summary>连接检查结果（PD 类型）。</summary>
    public enum ConnectionCheckResult : byte
    {
        TwoPair = 0x00,
        SinglePd = 0x01,
        DualPd = 0x02,
        Unknown = 0x03,
    }

    /// <summary>MCU 类型。</summary>
    public enum McuType : byte
    {
        Gd32F310 = 0x00,
        Gd32E230 = 0x01,
        Gd32F303 = 0x02,
        Gd32F103 = 0x03,
        Gd32E103 = 0x04,
        NuvotonM0516 = 0x10,
        NuvotonM0564 = 0x11,
        NuvotonNuc029 = 0x12,
    }

    /// <summary>PSE 芯片类型。</summary>
    public enum PseChipType : byte
    {
        Bt8Channel = 0x01,
        At8Channel = 0x02,
        InfoGetFailure = 0x0E,
        Reserved = 0x0F,
    }

    /// <summary>复位原因。</summary>
    public enum ResetReason : byte
    {
        PowerOnReset = 0x01,
        NrstReset = 0x02,
        SoftwareReset = 0x03,
        OtherErrorReset = 0x04,
    }

    /// <summary>供电线对类型。</summary>
    public enum PowerPairType : byte
    {
        AlternativeA = 0x00,
        AlternativeB = 0x01,
    }

    #endregion

    #region 结果结构体

    /// <summary>0x40 全局状态。</summary>
    public struct GlobalStatus
    {
        public byte MaxPorts;
        public byte PortMap;
        public bool PortMapEnabled;
        public ushort DeviceId;
        public string DeviceIdDescription;
        public byte SwVerMajor;
        public byte SwVerMinor;
        public string SwVersion;
        public McuType McuType;
        public string McuTypeDescription;
        public byte ConfigStatus;
        public bool ConfigSaved;
        public bool SystemResetOccurred;
        public bool GlobalDisablePinHigh;
        public byte ExtVerMajor;
        public byte ExtVerMinor;
        public string ExtVersion;
    }

    /// <summary>0x41 全局功率状态。</summary>
    public struct GlobalPowerStatus
    {
        public ushort SystemAllocatedPowerRaw;
        public double SystemAllocatedPowerW;
        public ushort SystemAvailablePowerRaw;
        public double SystemAvailablePowerW;
        public byte BankId;
        public ushort SystemCurrentPowerRaw;
        public double SystemCurrentPowerW;
    }

    /// <summary>0x42 端口状态。</summary>
    /// <remarks>
    /// STS2（字节 4）是二义字段：STS1 = Fault 时它只表示错误类型，否则表示
    /// 「高半字节 = 分级结果 + 低半字节 = 检测结果」。因此 FaultType 与
    /// DetectionResult/ClassificationResult 两组字段按 STS1 互斥填充，
    /// 不适用的那一组为 null，对应 Description 会写明原因。
    /// </remarks>
    public struct PortStatus
    {
        public byte Port;
        public PortPowerState PowerState;
        public string PowerStateDescription;
        public bool IsFault;
        public byte Sts2Raw;
        public FaultType? FaultType;
        public string FaultTypeDescription;
        public DetectionResult? DetectionResult;
        public string DetectionResultDescription;
        public ClassificationResult? ClassificationResult;
        public string ClassificationDescription;
        public byte Sts3Raw;
        public ClassificationResult PriChannelClassification;
        public ClassificationResult SecChannelClassification;
        public ClassificationResult SingleChannelClassification;
        public ConnectionCheckResult ConnectionCheckResult;
        public string ConnectionCheckDescription;
    }

    /// <summary>0x43 组内单端口状态。</summary>
    /// <remarks>STS2 的高半字节仅在该端口处于 Fault 状态时才是错误类型，其余情况为 null。</remarks>
    public struct PortGroupPortStatus
    {
        public byte PortIndex;
        public byte AbsolutePort;
        public DetectionResult DetectionResult;
        public string DetectionResultDescription;
        public PortPowerState PowerState;
        public string PowerStateDescription;
        public FaultType? FaultType;
        public string FaultTypeDescription;
        public ClassificationResult ClassificationResult;
        public string ClassificationDescription;
    }

    /// <summary>0x43 端口组状态（每组 4 端口）。</summary>
    public struct PortGroupStatus
    {
        public byte Group;
        public PortGroupPortStatus[] Ports;
    }

    /// <summary>0x44 端口测量。</summary>
    public struct PortMeasurement
    {
        public byte Port;
        public ushort VoltageRaw;
        public double VoltageMv;
        public ushort CurrentRaw;
        public int CurrentMa;
        public ushort TemperatureRaw;
        public double TemperatureC;
        public ushort PowerRaw;
        public double PowerW;
    }

    /// <summary>0x45 端口 MIB 计数器。</summary>
    public struct PortMibCounters
    {
        public byte Port;
        public byte MpsAbsentCounter;
        public byte OverloadCounter;
        public byte ShortCounter;
        public byte PowerDeniedCounter;
        public byte InvalidSignatureCounter;
    }

    /// <summary>0x46 端口事件状态。</summary>
    public struct PortEventStatus
    {
        public byte EventMask;
        public bool DisconnectEventMaskEnabled;
        public bool FaultEventMaskEnabled;
        public byte EventStatus;
        public bool DisconnectEventOccurred;
        public bool FaultEventOccurred;
        public ulong PortEventBitmap;
    }

    /// <summary>0x47 中一对芯片的访问错误信息。</summary>
    public struct ChipPairError
    {
        public byte Raw;
        public byte Chip0Nibble;
        public byte Chip1Nibble;
        public string Chip0Description;
        public string Chip1Description;
    }

    /// <summary>0x47 全局复位原因。</summary>
    public struct GlobalResetReason
    {
        public byte ResetFlag;
        public bool AllInterfacesOk;
        public int FirstChipAddress;
        public bool ChipResetStatus;
        public byte ErrorAddr;
        public ResetReason Reason;
        public string ReasonDescription;
        public ChipPairError[] ErrorAddrPairs;
    }

    /// <summary>0x48 端口基本配置。</summary>
    public struct PortBasicConfiguration
    {
        public byte Port;
        public byte EnableStatus;
        public bool Enabled;
        public PortFunctionMode FunctionMode;
        public string FunctionModeDescription;
        public byte DetectionType;
        public string DetectionTypeDescription;
        public byte ClassificationType;
        public DisconnectType DisconnectType;
        public string DisconnectTypeDescription;
        public PowerPairType PairType;
        public string PairTypeDescription;
        public CableType CableType;
        public string CableTypeDescription;
    }

    /// <summary>0x49 端口扩展配置。</summary>
    public struct PortExtendedConfiguration
    {
        public byte Port;
        public InrushMode InrushMode;
        public string InrushModeDescription;
        public MaxPowerType LimitType;
        public string LimitTypeDescription;
        public byte MaxPowerRaw;
        public double MaxPowerW;
        public PortPriority Priority;
        public string PriorityDescription;
        public byte ChipAddr;
        public string ChipAddrHex;
        public byte PriChannel;
        public byte SecChannel;
    }

    /// <summary>0x4A 全局参数。</summary>
    public struct GlobalParameters
    {
        public byte UvloRaw;
        public double UvloVolt;
        public byte PreAlloc;
        public bool PreAllocEnabled;
        public byte OvloRaw;
        public double OvloVolt;
        public byte ChipNumber;
        public byte NotSupportedChip;
    }

    /// <summary>0x4B 全局功率管理配置。</summary>
    public struct GlobalPmConfiguration
    {
        public byte BankId;
        public PowerManagementMode PmMode;
        public string PmModeDescription;
        public ushort BankTotalPowerRaw;
        public double BankTotalPowerW;
        public ushort BankReservedPowerRaw;
        public double BankReservedPowerW;
        public ushort BankNextTotalPowerRaw;
        public double BankNextTotalPowerW;
        public ushort BankNextReservedPowerRaw;
        public double BankNextReservedPowerW;
    }

    /// <summary>0x4C 全局设备地址。</summary>
    public struct GlobalDeviceAddress
    {
        public byte Idx;
        public byte[] Addresses;
        public string AddressesHex;
        public int PresentCount;
    }

    /// <summary>0x4D 单个端口的功能模式。</summary>
    public struct PortFunctionModeEntry
    {
        public byte Port;
        public PortFunctionMode Mode;
        public string ModeDescription;
    }

    /// <summary>0x4D 端口功能模式（最多 4 个端口）。</summary>
    public struct PortFunctionModeInfo
    {
        public PortFunctionModeEntry[] Entries;
    }

    /// <summary>0x4E 单个通道状态。</summary>
    public struct ChannelStatusInfo
    {
        public DetectionResult DetectionResult;
        public string DetectionResultDescription;
        public ClassificationResult ClassificationResult;
        public string ClassificationDescription;
        public FaultType FaultType;
        public string FaultTypeDescription;
        public PortPowerState PowerState;
        public string PowerStateDescription;
    }

    /// <summary>0x4E 通道状态（主/次双通道）。</summary>
    public struct ChannelStatus
    {
        public byte Port;
        public ChannelStatusInfo Primary;
        public ChannelStatusInfo Secondary;
    }

    /// <summary>0x4F 端口通道电压电流。</summary>
    public struct PortChannelVoltageCurrent
    {
        public byte Port;
        public ushort PriVoltageRaw;
        public double PriVoltageMv;
        public ushort PriCurrentRaw;
        public int PriCurrentMa;
        public ushort SecVoltageRaw;
        public double SecVoltageMv;
        public ushort SecCurrentRaw;
        public int SecCurrentMa;
    }

    /// <summary>0x50 单颗 PSE 芯片类型。</summary>
    public struct PseChipTypeEntry
    {
        public byte ChipIndex;
        public PseChipType Type;
        public string TypeDescription;
    }

    /// <summary>0x50 系统芯片类型信息（最多 12 颗）。</summary>
    public struct SystemChipTypeInfo
    {
        public PseChipTypeEntry[] Chips;
    }

    /// <summary>0xC0-00/02/03/05/06 的通用 STS 响应。</summary>
    public struct ConfigurationCommandStatus
    {
        public byte SubCommand;
        public string SubCommandName;
        public byte Status;
        public bool Success;
        public string StatusDescription;
    }

    /// <summary>0xC0-01 配置信息保存响应。</summary>
    public struct ConfigurationSaveStatus
    {
        public byte SaveStatus;
        public bool SaveSuccess;
        public byte VersionStatus;
        public bool VersionSuccess;
        public string Description;
    }

    /// <summary>0xC0-04 配置版本响应。</summary>
    public struct ConfigurationVersionInfo
    {
        public byte Year;
        public byte Month;
        public byte Day;
        public byte HighVersion;
        public byte LowVersion;
        public string DateText;
        public string VersionText;
    }

    /// <summary>0xC0-40 Jump To App 响应。</summary>
    public struct JumpToAppStatus
    {
        public byte AppImageStatus;
        public bool AppImageValid;
        public byte FirmwareImageStatus;
        public bool FirmwareImageValid;
        public string Description;
    }

    /// <summary>0xF1 芯片寄存器读响应。</summary>
    public struct ChipRegisterValue
    {
        public byte ChipAddr;
        public string ChipAddrHex;
        public uint RegisterAddress;
        public uint RegisterValue;
        public string RegisterAddressHex;
        public string RegisterValueHex;
    }

    /// <summary>Loader 下载应答（0xC0-80~83 / 0xCA，4 字节、无校验和）。</summary>
    public struct DownloadAck
    {
        public byte CommandId;
        public byte SubCommandOrSequence;
        public ushort ImageOffset;
        public string Description;
    }

    #endregion

    /// <summary>RTL8239 响应帧解析器（对应 Get 命令 0x40-0x50）。</summary>
    public static class Rtl8239ResponseParser
    {
        public const int AppFrameLength = 12;

        /// <summary>Loader 命令（0xC0-80~83 / 0xCA）应答长度：4 字节、无校验和。</summary>
        public const int LoaderAckLength = 4;

        // -----------------------------------------------------------------
        //  核心校验
        // -----------------------------------------------------------------

        private static byte Checksum(byte[] frame, int length)
        {
            int sum = 0;
            for (int i = 0; i < length; i++) sum += frame[i];
            return (byte)(sum & 0xFF);
        }

        private static ushort ReadUInt16(byte[] frame, int index, ByteOrder order)
        {
            return order == ByteOrder.LittleEndian
                ? (ushort)(frame[index] | (frame[index + 1] << 8))
                : (ushort)((frame[index] << 8) | frame[index + 1]);
        }

        private static uint ReadUInt32(byte[] frame, int index, ByteOrder order)
        {
            if (order == ByteOrder.LittleEndian)
            {
                return (uint)(frame[index]
                    | (frame[index + 1] << 8)
                    | (frame[index + 2] << 16)
                    | (frame[index + 3] << 24));
            }

            return (uint)((frame[index] << 24)
                | (frame[index + 1] << 16)
                | (frame[index + 2] << 8)
                | frame[index + 3]);
        }

        private static byte HighNibble(byte value) => (byte)((value >> 4) & 0x0F);

        private static byte LowNibble(byte value) => (byte)(value & 0x0F);

        private static void ValidateResponse(byte[] response)
        {
            if (response == null || response.Length < AppFrameLength)
                throw new ArgumentException("响应帧为空或长度不足 12 字节。", "response");

            byte expected = Checksum(response, AppFrameLength - 1);
            if (response[AppFrameLength - 1] != expected)
                throw new InvalidOperationException(
                    string.Format("响应帧校验和错误：期望 0x{0:X2}，实际 0x{1:X2}。", expected, response[AppFrameLength - 1]));
        }

        /// <summary>在长度/校验和之外，额外确认响应帧的命令 ID 与当前解析方法一致。</summary>
        private static void ValidateResponse(byte[] response, byte expectedCommandId)
        {
            ValidateResponse(response);
            if (response[0] != expectedCommandId)
                throw new InvalidOperationException(string.Format(
                    "响应帧命令 ID 不匹配：期望 0x{0:X2}，实际 0x{1:X2}（请确认粘贴的是该命令的响应帧）。",
                    expectedCommandId, response[0]));
        }

        // -----------------------------------------------------------------
        //  枚举 → 中文描述
        // -----------------------------------------------------------------

        public static string DescribePowerState(PortPowerState state)
        {
            switch (state)
            {
                case PortPowerState.Disabled: return "禁用";
                case PortPowerState.Searching: return "检测中";
                case PortPowerState.DeliveringPower: return "供电中";
                case PortPowerState.Reserved3: return "保留";
                case PortPowerState.Fault: return "故障";
                case PortPowerState.Reserved5: return "保留";
                case PortPowerState.RequestingPower: return "请求供电";
                default: return "未知 (0x" + ((byte)state).ToString("X2") + ")";
            }
        }

        public static string DescribeDetectionResult(DetectionResult result)
        {
            switch (result)
            {
                case DetectionResult.Unknown: return "未知";
                case DetectionResult.ShortCircuit: return "短路";
                case DetectionResult.HighCap: return "高容性";
                case DetectionResult.Rlow: return "低阻";
                case DetectionResult.ValidPd: return "有效 PD";
                case DetectionResult.Rhigh: return "高阻";
                case DetectionResult.OpenCircuit: return "开路";
                case DetectionResult.FetFailure: return "FET 故障";
                default: return "保留 (0x" + ((byte)result).ToString("X2") + ")";
            }
        }

        public static string DescribeFaultType(FaultType fault)
        {
            switch (fault)
            {
                case FaultType.Ovlo: return "过压 (OVLO)";
                case FaultType.MpsAbsent: return "MPS 缺失";
                case FaultType.Short: return "短路";
                case FaultType.Overload: return "过载";
                case FaultType.PowerDenied: return "供电拒绝";
                case FaultType.ThermalShutdown: return "过温关断";
                case FaultType.InrushFail: return "浪涌失败";
                case FaultType.Uvlo: return "欠压 (UVLO)";
                case FaultType.Gotp: return "GOTP";
                default: return "未知 (0x" + ((byte)fault).ToString("X2") + ")";
            }
        }

        public static string DescribeClassification(ClassificationResult cls)
        {
            byte v = (byte)cls;
            if (v <= 0x08) return "Class " + v.ToString();
            switch (cls)
            {
                case ClassificationResult.TreatedAsClass0: return "按 Class 0 处理";
                case ClassificationResult.ClassMismatch: return "分级不匹配";
                case ClassificationResult.ClassOverCurrent: return "分级过流";
                default: return "保留 (0x" + v.ToString("X2") + ")";
            }
        }

        public static string DescribeConnectionCheck(ConnectionCheckResult result)
        {
            switch (result)
            {
                case ConnectionCheckResult.TwoPair: return "2-pair";
                case ConnectionCheckResult.SinglePd: return "单个 PD";
                case ConnectionCheckResult.DualPd: return "双 PD";
                case ConnectionCheckResult.Unknown: return "未知";
                default: return "未知 (0x" + ((byte)result).ToString("X2") + ")";
            }
        }

        public static string DescribeMcuType(McuType mcu)
        {
            switch (mcu)
            {
                case McuType.Gd32F310: return "GigaDevice GD32F310";
                case McuType.Gd32E230: return "GigaDevice GD32E230";
                case McuType.Gd32F303: return "GigaDevice GD32F303";
                case McuType.Gd32F103: return "GigaDevice GD32F103";
                case McuType.Gd32E103: return "GigaDevice GD32E103";
                case McuType.NuvotonM0516: return "Nuvoton M0516";
                case McuType.NuvotonM0564: return "Nuvoton M0564";
                case McuType.NuvotonNuc029: return "Nuvoton NUC029";
                default: return "未知 MCU (0x" + ((byte)mcu).ToString("X2") + ")";
            }
        }

        public static string DescribeChipType(PseChipType type)
        {
            switch (type)
            {
                case PseChipType.Bt8Channel: return "8 通道 BT 芯片";
                case PseChipType.At8Channel: return "8 通道 AT 芯片";
                case PseChipType.InfoGetFailure: return "信息获取失败";
                case PseChipType.Reserved: return "保留";
                default: return "未知 (0x" + ((byte)type).ToString("X2") + ")";
            }
        }

        public static string DescribeResetReason(ResetReason reason)
        {
            switch (reason)
            {
                case ResetReason.PowerOnReset: return "上电复位";
                case ResetReason.NrstReset: return "nRST 复位";
                case ResetReason.SoftwareReset: return "软件复位";
                case ResetReason.OtherErrorReset: return "其它错误复位";
                default: return "未知 (0x" + ((byte)reason).ToString("X2") + ")";
            }
        }

        public static string DescribeDeviceId(ushort deviceId)
        {
            switch (deviceId)
            {
                case 0x0138: return "RTL8238B";
                case 0x0238: return "RTL8238C";
                case 0x0039: return "RTL8239";
                case 0x0139: return "RTL8239C";
                default: return "未知 (0x" + deviceId.ToString("X4") + ")";
            }
        }

        public static string DescribeChipAccessNibble(byte nibble)
        {
            if (nibble == 0x0F) return "正常";
            int addr = nibble * 2 + 0x20;
            return "访问异常 (芯片地址 0x" + addr.ToString("X2") + ")";
        }

        public static string DescribeFunctionMode(PortFunctionMode mode)
        {
            switch (mode)
            {
                case PortFunctionMode.Auto: return "自动 (Auto)";
                case PortFunctionMode.SemiAuto: return "半自动 (Semi-auto)";
                case PortFunctionMode.Manual: return "手动 (Manual)";
                default: return "未知 (0x" + ((byte)mode).ToString("X2") + ")";
            }
        }

        public static string DescribeInrushMode(InrushMode mode)
        {
            switch (mode)
            {
                case InrushMode.Ieee802_3af: return "IEEE 802.3af";
                case InrushMode.Ieee802_3afHighInrush: return "IEEE 802.3af 高浪涌";
                case InrushMode.PreIeee802_3at: return "Pre-IEEE 802.3at";
                case InrushMode.Ieee802_3at: return "IEEE 802.3at";
                case InrushMode.PreIeee802_3btType3: return "Pre-IEEE 802.3bt Type3";
                case InrushMode.Ieee802_3btType3: return "IEEE 802.3bt Type3";
                case InrushMode.Ieee802_3btType4: return "IEEE 802.3bt Type4";
                case InrushMode.PreIeee802_3btType4: return "Pre-IEEE 802.3bt Type4";
                case InrushMode.Ieee802_3atAltB: return "IEEE 802.3at ALT B";
                default: return "未知 (0x" + ((byte)mode).ToString("X2") + ")";
            }
        }

        public static string DescribePriority(PortPriority priority)
        {
            switch (priority)
            {
                case PortPriority.Low: return "低";
                case PortPriority.Medium: return "中";
                case PortPriority.High: return "高";
                case PortPriority.Critical: return "关键";
                default: return "未知 (0x" + ((byte)priority).ToString("X2") + ")";
            }
        }

        public static string DescribeDisconnectType(DisconnectType type)
        {
            switch (type)
            {
                case DisconnectType.DisableMps: return "禁用 MPS";
                case DisconnectType.Reserved: return "保留";
                case DisconnectType.EnableMps: return "使能 MPS";
                case DisconnectType.EnableMpsAfter700ms: return "使能 MPS (上电 700ms 后生效)";
                default: return "未知 (0x" + ((byte)type).ToString("X2") + ")";
            }
        }

        public static string DescribeLimitType(MaxPowerType type)
        {
            switch (type)
            {
                case MaxPowerType.Reserved: return "保留";
                case MaxPowerType.ClassBased: return "按分级 (Class based)";
                case MaxPowerType.UserDefined: return "用户自定义 (User defined)";
                default: return "未知 (0x" + ((byte)type).ToString("X2") + ")";
            }
        }

        public static string DescribePowerManagementMode(PowerManagementMode mode)
        {
            switch (mode)
            {
                case PowerManagementMode.None: return "无";
                case PowerManagementMode.StaticWithPriority: return "静态模式-带优先级";
                case PowerManagementMode.DynamicWithPriority: return "动态模式-带优先级";
                case PowerManagementMode.StaticWithoutPriority: return "静态模式-不带优先级";
                case PowerManagementMode.DynamicWithoutPriority: return "动态模式-不带优先级";
                default: return "未知 (0x" + ((byte)mode).ToString("X2") + ")";
            }
        }

        public static string DescribePairType(PowerPairType type)
        {
            switch (type)
            {
                case PowerPairType.AlternativeA: return "Alternative A";
                case PowerPairType.AlternativeB: return "Alternative B";
                default: return "未知 (0x" + ((byte)type).ToString("X2") + ")";
            }
        }

        public static string DescribeCableType(CableType type)
        {
            switch (type)
            {
                case CableType.Normal: return "普通线缆";
                case CableType.Short: return "短线缆";
                default: return "未知 (0x" + ((byte)type).ToString("X2") + ")";
            }
        }

        public static string DescribeDetectionType(byte detType)
        {
            switch (detType)
            {
                case 0x00:
                case 0x02:
                case 0x04:
                    return "仅对标准 PD 分级";
                case 0x01:
                case 0x03:
                case 0x05:
                    return "对标准及 Legacy PD 分级";
                case 0x06:
                    return "对过流 PD 分级";
                default:
                    return "未知 (0x" + detType.ToString("X2") + ")";
            }
        }

        /// <summary>0xC0 命令的子命令名称。</summary>
        public static string DescribeSubCommand(byte subCommand)
        {
            switch (subCommand)
            {
                case 0x00: return "0xC0-00 跳转到 Loader";
                case 0x01: return "0xC0-01 配置信息保存";
                case 0x02: return "0xC0-02 配置信息清除";
                case 0x03: return "0xC0-03 配置版本保存";
                case 0x04: return "0xC0-04 配置版本获取";
                case 0x05: return "0xC0-05 配置信息复位";
                case 0x06: return "0xC0-06 配置信息初始化";
                case 0x40: return "0xC0-40 跳转到 App";
                case 0x80: return "0xC0-80 App 下载（0~64K 块）";
                case 0x81: return "0xC0-81 App 下载（64K~128K 块）";
                case 0x82: return "0xC0-82 App 下载（128K~192K 块）";
                case 0x83: return "0xC0-83 App 下载（192K~256K 块）";
                default: return "未知子命令 (0x" + subCommand.ToString("X2") + ")";
            }
        }

        /// <summary>通用 STS 字段：0x00 = 请求成功 / 0x01 = 请求失败。</summary>
        public static string DescribeStatus(byte status)
        {
            switch (status)
            {
                case 0x00: return "成功";
                case 0x01: return "失败";
                default: return "未知 (0x" + status.ToString("X2") + ")";
            }
        }

        // -----------------------------------------------------------------
        //  解析方法（0x40-0x50）
        // -----------------------------------------------------------------

        public static GlobalStatus ParseGlobalStatus(byte[] response, ByteOrder order = ByteOrder.BigEndian)
        {
            ValidateResponse(response, 0x40);
            var r = new GlobalStatus();
            r.MaxPorts = response[3];
            r.PortMap = response[4];
            r.PortMapEnabled = response[4] == 0x01;
            r.DeviceId = ReadUInt16(response, 5, order);
            r.DeviceIdDescription = DescribeDeviceId(r.DeviceId);
            byte swVer = response[7];
            r.SwVerMajor = HighNibble(swVer);
            r.SwVerMinor = LowNibble(swVer);
            r.SwVersion = r.SwVerMajor + "." + r.SwVerMinor;
            r.McuType = (McuType)response[8];
            r.McuTypeDescription = DescribeMcuType(r.McuType);
            r.ConfigStatus = response[9];
            r.ConfigSaved = (response[9] & 0x01) != 0;
            r.SystemResetOccurred = (response[9] & 0x02) != 0;
            r.GlobalDisablePinHigh = (response[9] & 0x04) != 0;
            byte extVer = response[10];
            r.ExtVerMajor = HighNibble(extVer);
            r.ExtVerMinor = LowNibble(extVer);
            r.ExtVersion = r.ExtVerMajor + "." + r.ExtVerMinor;
            return r;
        }

        public static GlobalPowerStatus ParseGlobalPowerStatus(byte[] response, ByteOrder order = ByteOrder.BigEndian)
        {
            ValidateResponse(response, 0x41);
            var r = new GlobalPowerStatus();
            r.SystemAllocatedPowerRaw = ReadUInt16(response, 2, order);
            r.SystemAllocatedPowerW = r.SystemAllocatedPowerRaw * 0.1;
            r.SystemAvailablePowerRaw = ReadUInt16(response, 4, order);
            r.SystemAvailablePowerW = r.SystemAvailablePowerRaw * 0.1;
            r.BankId = response[6];
            r.SystemCurrentPowerRaw = ReadUInt16(response, 7, order);
            r.SystemCurrentPowerW = r.SystemCurrentPowerRaw * 0.1;
            return r;
        }

        public static PortStatus ParsePortStatus(byte[] response)
        {
            ValidateResponse(response, 0x42);
            var r = new PortStatus();
            r.Port = response[2];
            r.PowerState = (PortPowerState)response[3];
            r.PowerStateDescription = DescribePowerState(r.PowerState);
            r.IsFault = r.PowerState == PortPowerState.Fault;
            r.Sts2Raw = response[4];

            // STS2 是二义字段：STS1 = Fault 时它只表示错误类型，
            // 否则表示「高半字节 = 分级结果 + 低半字节 = 检测结果」，两者互斥。
            if (r.IsFault)
            {
                r.FaultType = (FaultType)response[4];
                r.FaultTypeDescription = DescribeFaultType(r.FaultType.Value);
                r.DetectionResultDescription = "不适用（端口处于故障状态，STS2 只表示错误类型）";
                r.ClassificationDescription = "不适用（端口处于故障状态，STS2 只表示错误类型）";
            }
            else
            {
                r.FaultTypeDescription = "无故障";
                r.DetectionResult = (DetectionResult)LowNibble(response[4]);
                r.DetectionResultDescription = DescribeDetectionResult(r.DetectionResult.Value);
                r.ClassificationResult = (ClassificationResult)HighNibble(response[4]);
                r.ClassificationDescription = DescribeClassification(r.ClassificationResult.Value);
            }

            r.Sts3Raw = response[5];
            r.PriChannelClassification = (ClassificationResult)LowNibble(response[5]);
            r.SecChannelClassification = (ClassificationResult)HighNibble(response[5]);
            r.SingleChannelClassification = (ClassificationResult)response[5];
            r.ConnectionCheckResult = (ConnectionCheckResult)response[7];
            r.ConnectionCheckDescription = DescribeConnectionCheck(r.ConnectionCheckResult);
            return r;
        }

        public static PortGroupStatus ParsePortGroupStatus(byte[] response)
        {
            ValidateResponse(response, 0x43);
            var r = new PortGroupStatus();
            r.Group = response[2];
            r.Ports = new PortGroupPortStatus[4];
            for (int i = 0; i < 4; i++)
            {
                byte stsDetPow = response[3 + i * 2];
                byte stsFltCls = response[4 + i * 2];
                var p = new PortGroupPortStatus();
                p.PortIndex = (byte)i;
                p.AbsolutePort = (byte)(r.Group * 4 + i);
                p.DetectionResult = (DetectionResult)HighNibble(stsDetPow);
                p.DetectionResultDescription = DescribeDetectionResult(p.DetectionResult);
                p.PowerState = (PortPowerState)LowNibble(stsDetPow);
                p.PowerStateDescription = DescribePowerState(p.PowerState);

                // STS2 高半字节只有在该端口处于 Fault 状态时才是错误类型。
                if (p.PowerState == PortPowerState.Fault)
                {
                    p.FaultType = (FaultType)HighNibble(stsFltCls);
                    p.FaultTypeDescription = DescribeFaultType(p.FaultType.Value);
                }
                else
                {
                    p.FaultTypeDescription = "无故障";
                }

                // 低半字节恒为分级结果；手册 5.4 注明双 PD 时它是两通道结果之和。
                p.ClassificationResult = (ClassificationResult)LowNibble(stsFltCls);
                p.ClassificationDescription = DescribeClassification(p.ClassificationResult);
                if (p.ClassificationResult > ClassificationResult.Class8)
                    p.ClassificationDescription += "（若为双 PD，则该值为两通道分级结果之和）";
                r.Ports[i] = p;
            }
            return r;
        }

        public static PortMeasurement ParsePortMeasurement(byte[] response, ByteOrder order = ByteOrder.BigEndian)
        {
            ValidateResponse(response, 0x44);
            var r = new PortMeasurement();
            r.Port = response[2];
            r.VoltageRaw = ReadUInt16(response, 3, order);
            r.VoltageMv = r.VoltageRaw * 64.45;
            r.CurrentRaw = ReadUInt16(response, 5, order);
            r.CurrentMa = r.CurrentRaw;
            r.TemperatureRaw = ReadUInt16(response, 7, order);
            r.TemperatureC = (r.TemperatureRaw - 120) * (-1.25) + 125;
            r.PowerRaw = ReadUInt16(response, 9, order);
            r.PowerW = r.PowerRaw * 0.1;
            return r;
        }

        public static PortMibCounters ParsePortMibCounters(byte[] response)
        {
            ValidateResponse(response, 0x45);
            var r = new PortMibCounters();
            r.Port = response[2];
            r.MpsAbsentCounter = response[3];
            r.OverloadCounter = response[4];
            r.ShortCounter = response[5];
            r.PowerDeniedCounter = response[6];
            r.InvalidSignatureCounter = response[7];
            return r;
        }

        public static PortEventStatus ParsePortEventStatus(byte[] response)
        {
            ValidateResponse(response, 0x46);
            var r = new PortEventStatus();
            r.EventMask = response[2];
            r.DisconnectEventMaskEnabled = (response[2] & 0x02) != 0;
            r.FaultEventMaskEnabled = (response[2] & 0x04) != 0;
            r.EventStatus = response[3];
            r.DisconnectEventOccurred = (response[3] & 0x02) != 0;
            r.FaultEventOccurred = (response[3] & 0x04) != 0;
            ulong bitmap = 0;
            for (int i = 0; i < 6; i++)
                bitmap |= (ulong)response[4 + i] << (i * 8);
            r.PortEventBitmap = bitmap;
            return r;
        }

        public static bool IsPortEventSet(ulong bitmap, int port)
        {
            if (port < 0 || port > 47) return false;
            return (bitmap & (1UL << port)) != 0;
        }

        public static GlobalResetReason ParseGlobalResetReason(byte[] response)
        {
            ValidateResponse(response, 0x47);
            var r = new GlobalResetReason();
            r.ResetFlag = response[2];
            r.AllInterfacesOk = response[2] == 0x00;
            r.FirstChipAddress = response[2] & 0x3F;
            r.ChipResetStatus = (response[2] & 0x40) != 0;
            r.ErrorAddr = response[3];
            r.Reason = (ResetReason)response[4];
            r.ReasonDescription = DescribeResetReason(r.Reason);
            r.ErrorAddrPairs = new ChipPairError[6];
            for (int i = 0; i < 6; i++)
            {
                byte raw = response[5 + i];
                var p = new ChipPairError();
                p.Raw = raw;
                p.Chip0Nibble = HighNibble(raw);
                p.Chip1Nibble = LowNibble(raw);
                p.Chip0Description = DescribeChipAccessNibble(p.Chip0Nibble);
                p.Chip1Description = DescribeChipAccessNibble(p.Chip1Nibble);
                r.ErrorAddrPairs[i] = p;
            }
            return r;
        }

        public static PortBasicConfiguration ParsePortBasicConfiguration(byte[] response)
        {
            ValidateResponse(response, 0x48);
            var r = new PortBasicConfiguration();
            r.Port = response[2];
            r.EnableStatus = response[3];
            r.Enabled = response[3] == 0x01;
            r.FunctionMode = (PortFunctionMode)response[4];
            r.FunctionModeDescription = DescribeFunctionMode(r.FunctionMode);
            r.DetectionType = response[5];
            r.DetectionTypeDescription = DescribeDetectionType(r.DetectionType);
            r.ClassificationType = response[6];
            r.DisconnectType = (DisconnectType)response[7];
            r.DisconnectTypeDescription = DescribeDisconnectType(r.DisconnectType);
            r.PairType = (PowerPairType)response[8];
            r.PairTypeDescription = DescribePairType(r.PairType);
            r.CableType = (CableType)response[10];
            r.CableTypeDescription = DescribeCableType(r.CableType);
            return r;
        }

        public static PortExtendedConfiguration ParsePortExtendedConfiguration(byte[] response)
        {
            ValidateResponse(response, 0x49);
            var r = new PortExtendedConfiguration();
            r.Port = response[2];
            r.InrushMode = (InrushMode)response[3];
            r.InrushModeDescription = DescribeInrushMode(r.InrushMode);
            r.LimitType = (MaxPowerType)response[4];
            r.LimitTypeDescription = DescribeLimitType(r.LimitType);
            r.MaxPowerRaw = response[5];
            r.MaxPowerW = r.MaxPowerRaw * 0.4;
            r.Priority = (PortPriority)response[6];
            r.PriorityDescription = DescribePriority(r.Priority);
            r.ChipAddr = response[7];
            r.ChipAddrHex = "0x" + r.ChipAddr.ToString("X2");
            r.PriChannel = response[8];
            r.SecChannel = response[9];
            return r;
        }

        public static GlobalParameters ParseGlobalParameters(byte[] response)
        {
            ValidateResponse(response, 0x4A);
            var r = new GlobalParameters();
            r.UvloRaw = response[2];
            r.UvloVolt = 33.0 + r.UvloRaw * 0.06445;
            r.PreAlloc = response[3];
            r.PreAllocEnabled = response[3] == 0x01;
            r.OvloRaw = response[7];
            r.OvloVolt = 57.0 + r.OvloRaw * 0.06445;
            r.ChipNumber = response[8];
            r.NotSupportedChip = response[9];
            return r;
        }

        public static GlobalPmConfiguration ParseGlobalPmConfiguration(byte[] response, ByteOrder order = ByteOrder.BigEndian)
        {
            ValidateResponse(response, 0x4B);
            var r = new GlobalPmConfiguration();
            r.BankId = response[1];
            r.PmMode = (PowerManagementMode)response[2];
            r.PmModeDescription = DescribePowerManagementMode(r.PmMode);
            r.BankTotalPowerRaw = ReadUInt16(response, 3, order);
            r.BankTotalPowerW = r.BankTotalPowerRaw * 0.1;
            r.BankReservedPowerRaw = ReadUInt16(response, 5, order);
            r.BankReservedPowerW = r.BankReservedPowerRaw * 0.1;
            r.BankNextTotalPowerRaw = ReadUInt16(response, 7, order);
            r.BankNextTotalPowerW = r.BankNextTotalPowerRaw * 0.1;
            r.BankNextReservedPowerRaw = ReadUInt16(response, 9, order);
            r.BankNextReservedPowerW = r.BankNextReservedPowerRaw * 0.1;
            return r;
        }

        public static GlobalDeviceAddress ParseGlobalDeviceAddress(byte[] response)
        {
            ValidateResponse(response, 0x4C);
            var r = new GlobalDeviceAddress();
            r.Idx = response[2];
            r.Addresses = new byte[8];
            int present = 0;
            for (int i = 0; i < 8; i++)
            {
                r.Addresses[i] = response[3 + i];
                if (r.Addresses[i] != 0xFF) present++;
            }
            r.PresentCount = present;
            r.AddressesHex = Rtl8239CommandBuilder.ToHex(r.Addresses);
            return r;
        }

        public static PortFunctionModeInfo ParsePortFunctionMode(byte[] response)
        {
            ValidateResponse(response, 0x4D);
            var r = new PortFunctionModeInfo();
            r.Entries = new PortFunctionModeEntry[4];
            for (int i = 0; i < 4; i++)
            {
                var e = new PortFunctionModeEntry();
                e.Port = response[2 + i * 2];
                e.Mode = (PortFunctionMode)response[3 + i * 2];
                // 请求中未指定的槽位在响应里保持 0xFF，不属于查询结果。
                e.ModeDescription = e.Port == 0xFF
                    ? "未使用（请求中未指定该槽位）"
                    : DescribeFunctionMode(e.Mode);
                r.Entries[i] = e;
            }
            return r;
        }

        public static ChannelStatus ParseChannelStatus(byte[] response)
        {
            ValidateResponse(response, 0x4E);
            var r = new ChannelStatus();
            r.Port = response[2];
            r.Primary = ParseChannelInfo(response, 3);
            r.Secondary = ParseChannelInfo(response, 7);
            return r;
        }

        private static ChannelStatusInfo ParseChannelInfo(byte[] response, int baseIndex)
        {
            var c = new ChannelStatusInfo();
            c.DetectionResult = (DetectionResult)response[baseIndex + 0];
            c.DetectionResultDescription = DescribeDetectionResult(c.DetectionResult);
            c.ClassificationResult = (ClassificationResult)response[baseIndex + 1];
            c.ClassificationDescription = DescribeClassification(c.ClassificationResult);
            c.FaultType = (FaultType)response[baseIndex + 2];
            c.FaultTypeDescription = DescribeFaultType(c.FaultType);
            c.PowerState = (PortPowerState)response[baseIndex + 3];
            c.PowerStateDescription = DescribePowerState(c.PowerState);
            return c;
        }

        public static PortChannelVoltageCurrent ParsePortChannelVoltageCurrent(byte[] response, ByteOrder order = ByteOrder.BigEndian)
        {
            ValidateResponse(response, 0x4F);
            var r = new PortChannelVoltageCurrent();
            r.Port = response[2];
            r.PriVoltageRaw = ReadUInt16(response, 3, order);
            r.PriVoltageMv = r.PriVoltageRaw * 64.45;
            r.PriCurrentRaw = ReadUInt16(response, 5, order);
            r.PriCurrentMa = r.PriCurrentRaw;
            r.SecVoltageRaw = ReadUInt16(response, 7, order);
            r.SecVoltageMv = r.SecVoltageRaw * 64.45;
            r.SecCurrentRaw = ReadUInt16(response, 9, order);
            r.SecCurrentMa = r.SecCurrentRaw;
            return r;
        }

        public static SystemChipTypeInfo ParseSystemChipTypeInfo(byte[] response)
        {
            ValidateResponse(response, 0x50);
            var r = new SystemChipTypeInfo();
            r.Chips = new PseChipTypeEntry[12];
            for (int chip = 0; chip < 12; chip++)
            {
                int stsIndex = chip / 2;
                byte raw = response[2 + stsIndex];
                byte nibble = (chip % 2 == 0) ? HighNibble(raw) : LowNibble(raw);
                var e = new PseChipTypeEntry();
                e.ChipIndex = (byte)chip;
                e.Type = (PseChipType)nibble;
                e.TypeDescription = DescribeChipType(e.Type);
                r.Chips[chip] = e;
            }
            return r;
        }

        // -----------------------------------------------------------------
        //  统一分发（0x40-0x50 / 0xC0-xx / 0xCA / 0xF1）
        // -----------------------------------------------------------------

        /// <summary>
        /// 按响应帧 Byte0 分发到对应解析方法；0xC0 命令再按 Byte1 的子命令细分。
        /// </summary>
        /// <exception cref="NotSupportedException">该命令的响应暂不支持解析。</exception>
        public static object Parse(byte[] response, ByteOrder order = ByteOrder.BigEndian)
        {
            if (response == null || response.Length == 0)
                throw new ArgumentException("响应帧为空。", "response");

            switch (response[0])
            {
                case 0x40: return ParseGlobalStatus(response, order);
                case 0x41: return ParseGlobalPowerStatus(response, order);
                case 0x42: return ParsePortStatus(response);
                case 0x43: return ParsePortGroupStatus(response);
                case 0x44: return ParsePortMeasurement(response, order);
                case 0x45: return ParsePortMibCounters(response);
                case 0x46: return ParsePortEventStatus(response);
                case 0x47: return ParseGlobalResetReason(response);
                case 0x48: return ParsePortBasicConfiguration(response);
                case 0x49: return ParsePortExtendedConfiguration(response);
                case 0x4A: return ParseGlobalParameters(response);
                case 0x4B: return ParseGlobalPmConfiguration(response, order);
                case 0x4C: return ParseGlobalDeviceAddress(response);
                case 0x4D: return ParsePortFunctionMode(response);
                case 0x4E: return ParseChannelStatus(response);
                case 0x4F: return ParsePortChannelVoltageCurrent(response, order);
                case 0x50: return ParseSystemChipTypeInfo(response);
                case 0xC0: return ParseConfigurationResponse(response, order);
                case 0xCA: return ParseDownloadAck(response, order);
                case 0xF1: return ParseChipRegisterValue(response, order);
                default:
                    throw new NotSupportedException(
                        "不支持解析命令 0x" + response[0].ToString("X2") + " 的响应。");
            }
        }

        // 0xC0 系命令：App 子命令返回 12 字节带校验和的帧，
        // Loader 子命令（0x80-0x83）只返回 4 字节、无校验和的应答。
        private static object ParseConfigurationResponse(byte[] response, ByteOrder order)
        {
            if (response.Length < AppFrameLength)
                return ParseDownloadAck(response, order);

            switch (response[1])
            {
                case 0x00:
                case 0x02:
                case 0x03:
                case 0x05:
                case 0x06:
                    return ParseConfigurationCommandStatus(response);
                case 0x01:
                    return ParseConfigurationSaveStatus(response);
                case 0x04:
                    return ParseConfigurationVersion(response);
                case 0x40:
                    return ParseJumpToAppStatus(response);
                case 0x80:
                case 0x81:
                case 0x82:
                case 0x83:
                    return ParseDownloadAck(response, order);
                default:
                    throw new NotSupportedException(
                        "不支持解析子命令 0xC0-" + response[1].ToString("X2") + " 的响应。");
            }
        }

        /// <summary>0xC0-00/02/03/05/06 的通用 STS 响应。</summary>
        public static ConfigurationCommandStatus ParseConfigurationCommandStatus(byte[] response)
        {
            ValidateResponse(response, 0xC0);
            var r = new ConfigurationCommandStatus();
            r.SubCommand = response[1];
            r.SubCommandName = DescribeSubCommand(r.SubCommand);
            r.Status = response[2];
            r.Success = r.Status == 0x00;
            r.StatusDescription = DescribeStatus(r.Status);
            return r;
        }

        /// <summary>0xC0-01 配置信息保存响应（Version STS 仅 RTL8238C/8239C 有效）。</summary>
        public static ConfigurationSaveStatus ParseConfigurationSaveStatus(byte[] response)
        {
            ValidateResponse(response, 0xC0);
            var r = new ConfigurationSaveStatus();
            r.SaveStatus = response[2];
            r.SaveSuccess = r.SaveStatus == 0x00;
            r.VersionStatus = response[3];
            r.VersionSuccess = r.VersionStatus == 0x00;
            r.Description = "保存：" + DescribeStatus(r.SaveStatus)
                + "；版本保存：" + DescribeStatus(r.VersionStatus);
            return r;
        }

        /// <summary>0xC0-04 配置版本响应。</summary>
        public static ConfigurationVersionInfo ParseConfigurationVersion(byte[] response)
        {
            ValidateResponse(response, 0xC0);
            var r = new ConfigurationVersionInfo();
            r.Year = response[2];
            r.Month = response[3];
            r.Day = response[4];
            r.HighVersion = response[5];
            r.LowVersion = response[6];
            r.DateText = string.Format("{0:D2}/{1:D2}/{2:D2}", r.Year, r.Month, r.Day);
            r.VersionText = r.HighVersion + "." + r.LowVersion;
            return r;
        }

        /// <summary>0xC0-40 Jump To App 响应。</summary>
        public static JumpToAppStatus ParseJumpToAppStatus(byte[] response)
        {
            ValidateResponse(response, 0xC0);
            var r = new JumpToAppStatus();
            r.AppImageStatus = response[2];
            r.AppImageValid = r.AppImageStatus == 0x00;
            r.FirmwareImageStatus = response[3];
            r.FirmwareImageValid = r.FirmwareImageStatus == 0x00;
            r.Description = "App 镜像校验：" + (r.AppImageValid ? "通过" : "不通过（App 区将被擦除）")
                + "；Firmware 镜像校验：" + (r.FirmwareImageValid ? "通过" : "不通过（Firmware 区将被擦除）");
            return r;
        }

        /// <summary>0xF1 芯片寄存器读响应。</summary>
        public static ChipRegisterValue ParseChipRegisterValue(byte[] response, ByteOrder order = ByteOrder.BigEndian)
        {
            ValidateResponse(response, 0xF1);
            var r = new ChipRegisterValue();
            r.ChipAddr = response[2];
            r.ChipAddrHex = "0x" + r.ChipAddr.ToString("X2");
            r.RegisterAddress = ReadUInt32(response, 3, order);
            r.RegisterValue = ReadUInt32(response, 7, order);
            r.RegisterAddressHex = "0x" + r.RegisterAddress.ToString("X8");
            r.RegisterValueHex = "0x" + r.RegisterValue.ToString("X8");
            return r;
        }

        /// <summary>
        /// Loader 下载应答（0xC0-80~83 / 0xCA）：固定 4 字节、无校验和，
        /// 仅含 Byte0 命令 ID、Byte1 子命令（0xCA 为序列号）与 Byte2-3 镜像偏移。
        /// </summary>
        public static DownloadAck ParseDownloadAck(byte[] response, ByteOrder order = ByteOrder.BigEndian)
        {
            if (response == null || response.Length < LoaderAckLength)
                throw new ArgumentException("下载应答帧长度不足 4 字节。", "response");

            var r = new DownloadAck();
            r.CommandId = response[0];
            r.SubCommandOrSequence = response[1];
            r.ImageOffset = ReadUInt16(response, 2, order);
            r.Description = (r.CommandId == 0xCA ? "Firmware 下载" : "App 下载")
                + "帧已接收，镜像偏移 0x" + r.ImageOffset.ToString("X4");
            return r;
        }
    }
}
