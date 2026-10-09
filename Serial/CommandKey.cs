using System;
using System.Globalization;

namespace PoECommandTool.Serial
{
    /// <summary>
    /// 命令键名（"0x44" / "0xC0-04"）的解析结果，连同两条跟着命令走、不该散落各处的规则：
    /// ① 响应的子命令要和请求对得上；② 某些「全空」响应的超时该给出什么解释。
    ///
    /// 单独成类是因为它们会被不止一处用到，而这两条规则都有同一个脾气：**错了会静默显示错值**
    /// （把别人的数据当成自己的、或对正确响应报错）。复制一份就意味着迟早漂移。
    /// </summary>
    public sealed class CommandKey
    {
        public string Key { get; private set; }
        public byte CommandId { get; private set; }

        /// <summary>
        /// 键名里带 '-' 才有子命令（如 "0xC0-04"）。
        /// 注意不能拿 <see cref="SerialSession.IsSequenceCorrelatable"/> 当判据——0x4B 的 Byte1
        /// 是 **Bank ID**（目录里自己写着），它同样不可用序列号配对，但绝不是子命令。
        /// </summary>
        public bool HasSubCommand { get; private set; }

        /// <summary>仅当 <see cref="HasSubCommand"/> 为真时有意义。</summary>
        public byte SubCommand { get; private set; }

        public bool SequenceCorrelatable
        {
            get { return SerialSession.IsSequenceCorrelatable(CommandId); }
        }

        private CommandKey()
        {
        }

        /// <summary>解析 "0x44" 或 "0xC0-04" 这样的键名。解析不出来抛 <see cref="FormatException"/>。</summary>
        public static CommandKey Parse(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("命令键名是空的。", "key");

            var parsed = new CommandKey();
            parsed.Key = key;

            int dash = key.IndexOf('-');
            string head = dash < 0 ? key : key.Substring(0, dash);
            parsed.CommandId = (byte)ParseHexField("命令号", head);

            if (dash >= 0)
            {
                parsed.HasSubCommand = true;
                parsed.SubCommand = (byte)ParseHexField("子命令", key.Substring(dash + 1));
            }

            return parsed;
        }

        /// <summary>
        /// 键名里的每一段都按**十六进制**解析。
        ///
        /// 这里不能用 <see cref="Rtl8239Catalog.ParseNumber"/>：它对既无 "0x" 前缀、又不含 A-F 的串
        /// 按**十进制**解析，于是 "0xC0-40" 的后缀 "40" 会变成 0x28，而构造器写进去的是 0x40。
        /// 后果是对一个完全正确的响应报「子命令不符」。（"0xC0-04" 的 "04"→4 只是碰巧正确。）
        /// </summary>
        private static long ParseHexField(string label, string text)
        {
            string s = (text ?? string.Empty).Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);

            if (s.Length == 0)
                throw new FormatException(string.Format(CultureInfo.InvariantCulture,
                    "命令键名里的{0}是空的。", label));

            long value;
            try
            {
                value = Convert.ToInt64(s, 16);
            }
            catch (Exception ex)
            {
                throw new FormatException(string.Format(CultureInfo.InvariantCulture,
                    "命令键名里的{0}「{1}」不是合法的十六进制数。", label, text), ex);
            }

            if (value > 0xFF)
                throw new FormatException(string.Format(CultureInfo.InvariantCulture,
                    "命令键名里的{0}「{1}」超出 0x00-0xFF。", label, text));

            return value;
        }

        /// <summary>
        /// 这一帧是不是「不属于这条命令」。返回 null 表示没问题，否则返回给人看的说明。
        ///
        /// 只对**键名里确实带子命令**的命令比对 Byte1：序列号可配对的那些，
        /// <see cref="SerialSession.WaitForFrameAsync"/> 已经比过了；而 0x4B 之类的 Byte1
        /// 既不是子命令也不是序列号，拿它比对会把正确的响应当成错的。
        ///
        /// 为什么值得单独挡一道：0xC0 系命令只按命令 ID 配对，**任何** 0xC0 帧都能满足那个等待——
        /// 手工帧的回包、上一次超时之后才到的回包，都可能被认领。宁可这条明确失败，
        /// 也不能把别人的数据显示成自己的。
        /// </summary>
        public string DescribeMismatch(FrameEvent frame)
        {
            if (!HasSubCommand || frame == null || frame.Raw == null || frame.Raw.Length < 2)
                return null;

            if (frame.Raw[1] == SubCommand)
                return null;

            return string.Format(CultureInfo.InvariantCulture,
                "收到的不是 {0} 的响应（子命令 0x{1:X2}，期望 0x{2:X2}）——未采纳，免得把别人的数据当成自己的",
                Key, frame.Raw[1], SubCommand);
        }

        /// <summary>
        /// 请求帧里被填成 0xFF 的「保留位」、以及协议规定空缺处填 0xFF 的响应字段，
        /// 会让**某些「全空」的响应与请求逐字节相同**——SEQ 原样回显、校验和随之相同，
        /// 于是被 <see cref="SerialSession"/> 的自回显抑制当成回显丢掉，表现为超时。
        ///
        /// 后果是：这些命令的「设备上什么都没有」和「链路断了」**在观测上无法区分**。
        /// 所以超时时必须把这个歧义说出来，不能让用户以为一定是链路问题。
        /// </summary>
        public static string EmptyResponseHint(byte commandId, bool timedOut)
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
    }
}
