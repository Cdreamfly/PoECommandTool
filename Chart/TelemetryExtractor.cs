using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PoECommandTool.Chart
{
    /// <summary>从解析结果里抽出来的一条待画曲线（还没绑定缓冲）。</summary>
    public struct SeriesCandidate
    {
        public string Key;
        public string Name;
        public string Unit;
        public double Value;
    }

    /// <summary>
    /// 从一次解析结果里抽出「带名字的数值序列」。
    ///
    /// 规则是**白名单**：只有字段名以单位后缀结尾的数值字段才算曲线。
    /// 不用「所有数值字段」是因为那样会把 <c>Port</c>（端口号）、<c>IsFault</c>（bool 也是数值）、
    /// 各种 <c>*Raw</c>（换算前的原始 LSB）一起画成没有物理意义的假曲线。
    /// 现成的解析结果命名本来就带单位（见 Rtl8239ResponseParser 文件头说明），所以这条规则
    /// 既准确又自带文档。
    ///
    /// 注意：0x42「端口状态获取」本来就没有测量量，这里抽出来是**零条**——这是正确行为，
    /// 不是缺陷；界面上应当提示「该命令不含可绘制的数值量，电压/电流/功率请用 0x44 / 0x4F」。
    /// </summary>
    public static class TelemetryExtractor
    {
        /// <summary>抽出所有可画的序列。<paramref name="tag"/> 是显示前缀，例如 "0x44 端口0"。</summary>
        public static IList<SeriesCandidate> Extract(object parsed, string tag)
        {
            var result = new List<SeriesCandidate>();
            if (parsed == null)
                return result;

            FieldInfo[] fields = parsed.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];

                string unit;
                if (!TryGetUnit(field.Name, out unit))
                    continue;

                object raw = field.GetValue(parsed);
                if (raw == null || !IsNumeric(raw.GetType()))
                    continue;

                var candidate = new SeriesCandidate();
                candidate.Key = tag + "." + field.Name;
                candidate.Name = tag + " " + LabelFor(field.Name);
                candidate.Unit = unit;
                candidate.Value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                result.Add(candidate);
            }
            return result;
        }

        /// <summary>某条命令 + 端口会产生哪些曲线（图例用，不需要先有数据）。</summary>
        public static IList<string> DescribeUnits(string commandKey)
        {
            switch (commandKey)
            {
                case "0x41": return new List<string>(new[] { "W" });
                case "0x44": return new List<string>(new[] { "mV", "mA", "℃", "W" });
                case "0x4F": return new List<string>(new[] { "mV", "mA" });
                case "0x4A": return new List<string>(new[] { "V" });
                case "0x49": return new List<string>(new[] { "W" });
                default: return new List<string>();
            }
        }

        /// <summary>
        /// 某条命令会产生哪些曲线——**不需要数据**，图例可以在开始轮询时就先列出来。
        /// 值的部分无意义（用的是默认实例），只取 key / 名字 / 单位。
        /// </summary>
        public static IList<SeriesCandidate> DescribeSeries(byte commandId, string tag)
        {
            Type type = TypeFor(commandId);
            if (type == null)
                return new List<SeriesCandidate>();

            object blank = Activator.CreateInstance(type);
            return Extract(blank, tag);
        }

        /// <summary>命令号 → 解析结果类型。</summary>
        public static Type TypeFor(byte commandId)
        {
            switch (commandId)
            {
                case 0x40: return typeof(GlobalStatus);
                case 0x41: return typeof(GlobalPowerStatus);
                case 0x42: return typeof(PortStatus);
                case 0x43: return typeof(PortGroupStatus);
                case 0x44: return typeof(PortMeasurement);
                case 0x45: return typeof(PortMibCounters);
                case 0x46: return typeof(PortEventStatus);
                case 0x47: return typeof(GlobalResetReason);
                case 0x48: return typeof(PortBasicConfiguration);
                case 0x49: return typeof(PortExtendedConfiguration);
                case 0x4A: return typeof(GlobalParameters);
                case 0x4B: return typeof(GlobalPmConfiguration);
                case 0x4C: return typeof(GlobalDeviceAddress);
                case 0x4D: return typeof(PortFunctionModeInfo);
                case 0x4E: return typeof(ChannelStatus);
                case 0x4F: return typeof(PortChannelVoltageCurrent);
                case 0x50: return typeof(SystemChipTypeInfo);
                default: return null;
            }
        }

        /// <summary>该命令是否有可画的数值量。</summary>
        public static bool HasNumericSeries(string commandKey)
        {
            return DescribeUnits(commandKey).Count > 0;
        }

        /// <summary>
        /// 解析结果里回显的端口号（结构体有 Port 字段时）。没有则返回 -1。
        /// 曲线名要带上端口，否则多端口轮询时所有端口会挤进同一条曲线。
        /// </summary>
        public static int TryGetPort(object parsed)
        {
            if (parsed == null)
                return -1;

            FieldInfo field = parsed.GetType().GetField("Port");
            if (field == null || field.FieldType != typeof(byte))
                return -1;

            return (byte)field.GetValue(parsed);
        }

        // 单位后缀白名单（区分大小写，所以 Sts2Raw 这类不会误判）
        private static bool TryGetUnit(string fieldName, out string unit)
        {
            if (fieldName.EndsWith("Volt", StringComparison.Ordinal)) { unit = "V"; return true; }
            if (fieldName.EndsWith("Mv", StringComparison.Ordinal)) { unit = "mV"; return true; }
            if (fieldName.EndsWith("Ma", StringComparison.Ordinal)) { unit = "mA"; return true; }
            if (fieldName.EndsWith("W", StringComparison.Ordinal)) { unit = "W"; return true; }
            if (fieldName.EndsWith("C", StringComparison.Ordinal)) { unit = "℃"; return true; }
            unit = null;
            return false;
        }

        private static string LabelFor(string fieldName)
        {
            switch (fieldName)
            {
                case "VoltageMv": return "电压";
                case "CurrentMa": return "电流";
                case "TemperatureC": return "温度";
                case "PowerW": return "功率";
                case "PriVoltageMv": return "主通道电压";
                case "PriCurrentMa": return "主通道电流";
                case "SecVoltageMv": return "副通道电压";
                case "SecCurrentMa": return "副通道电流";
                case "SystemAllocatedPowerW": return "系统已分配功率";
                case "SystemAvailablePowerW": return "系统可用功率";
                case "SystemCurrentPowerW": return "系统当前功率";
                case "UvloVolt": return "UVLO 阈值";
                case "OvloVolt": return "OVLO 阈值";
                case "MaxPowerW": return "端口最大功率";
                default: return fieldName;
            }
        }

        private static bool IsNumeric(Type type)
        {
            return type == typeof(byte) || type == typeof(sbyte)
                || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint)
                || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double);
        }
    }
}
