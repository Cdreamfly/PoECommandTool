using System;
using System.Collections.Generic;

namespace WpfApp1.Serial
{
    /// <summary>一条待轮询的查询命令。</summary>
    public sealed class PollItem
    {
        public CommandDef Command { get; set; }

        /// <summary>命令带端口字段时使用；不带端口的命令忽略。</summary>
        public byte Port { get; set; }

        public bool Enabled { get; set; }

        public string DisplayName
        {
            get { return Command == null ? "(空)" : Command.Key + "  " + Command.Name; }
        }
    }

    /// <summary>
    /// 轮询候选清单。
    ///
    /// 候选 = 目录里的「查询命令」且参数只有端口（0 个或 1 个）。像 0x43（组索引）、
    /// 0x45（复位标志）、0x46/0x47（清除标志）、0x4B（Bank ID）、0x4C（索引）这些
    /// 需要端口之外的额外参数，不进候选——否则会把「组索引 0」错当成「端口 0」发出去。
    /// </summary>
    public static class PollPlan
    {
        /// <summary>默认勾选的命令：0x44 / 0x4F 才是出功率、电流的。</summary>
        public static readonly string[] DefaultEnabledKeys = { "0x41", "0x42", "0x44", "0x4F" };

        /// <summary>命令的参数里是否含端口字段。</summary>
        public static bool HasPortField(CommandDef cmd)
        {
            if (cmd == null || cmd.Fields == null)
                return false;

            for (int i = 0; i < cmd.Fields.Length; i++)
                if (cmd.Fields[i].Kind == FieldKind.Port)
                    return true;
            return false;
        }

        /// <summary>该命令能不能拿来轮询。</summary>
        public static bool IsPollable(CommandDef cmd)
        {
            if (cmd == null || cmd.Category != Rtl8239Catalog.CatQuery)
                return false;

            FieldDef[] fields = cmd.Fields;
            if (fields == null)
                return true;

            for (int i = 0; i < fields.Length; i++)
                if (fields[i].Kind != FieldKind.Port)
                    return false;      // 有端口之外的参数：需要额外输入，暂不支持轮询
            return true;
        }

        /// <summary>构造候选清单（默认勾选 <see cref="DefaultEnabledKeys"/> 里的命令）。</summary>
        public static List<PollItem> BuildCandidates()
        {
            var items = new List<PollItem>();
            List<CommandDef> all = Rtl8239Catalog.All;
            for (int i = 0; i < all.Count; i++)
            {
                CommandDef cmd = all[i];
                if (!IsPollable(cmd))
                    continue;

                var item = new PollItem();
                item.Command = cmd;
                item.Port = 0x00;
                item.Enabled = IsDefaultEnabled(cmd.Key);
                items.Add(item);
            }
            return items;
        }

        private static bool IsDefaultEnabled(string key)
        {
            for (int i = 0; i < DefaultEnabledKeys.Length; i++)
                if (string.Equals(DefaultEnabledKeys[i], key, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>命令号（目录里的 Key 是 "0x44" 这种）。轮询项本身只存 CommandDef，这里换算一次。</summary>
        public static byte CommandIdOf(CommandDef cmd)
        {
            if (cmd == null || string.IsNullOrEmpty(cmd.Key))
                return 0;

            try
            {
                long value = Rtl8239Catalog.ParseNumber(cmd.Key);
                return value >= 0 && value <= 0xFF ? (byte)value : (byte)0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 为某一项构造 12 字节请求帧。直接复用「命令组装」页用的 CommandDef.Build，
        /// 保证轮询发出去的帧和手工生成的帧逐字节一致。
        /// </summary>
        public static byte[] BuildFrame(PollItem item, byte seq)
        {
            if (item == null) throw new ArgumentNullException("item");
            if (item.Command == null) throw new ArgumentException("轮询项没有命令定义。", "item");

            FieldDef[] fields = item.Command.Fields ?? new FieldDef[0];
            var values = new long[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                values[i] = fields[i].Kind == FieldKind.Port
                    ? item.Port
                    : fields[i].DefaultValue;
            }

            // 与「命令组装」页共用同一个校验入口，否则轮询路径会与手工生成路径分叉，
            // 一条越界的默认值或端口会静默截断后才发到设备上。
            return Rtl8239Catalog.BuildChecked(item.Command, seq, values);
        }
    }
}
