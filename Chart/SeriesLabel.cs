using System;

namespace PoECommandTool.Chart
{
    /// <summary>
    /// 曲线名与键的拆解。
    ///
    /// <see cref="TelemetryExtractor"/> 产出的键与名是这样构成的：
    /// 键 <c>"0x44 端口0.PowerW"</c>、名 <c>"0x44 端口0 功率"</c> —— 前半段是「来源」
    /// （哪条命令的哪个端口），后半段是「参数」。图例按参数分组，靠的就是这里的拆解。
    /// </summary>
    public static class SeriesLabel
    {
        /// <summary>键 "0x44 端口0.PowerW" → 来源 "0x44 端口0"。</summary>
        public static string Source(string key)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            int dot = key.LastIndexOf('.');
            return dot > 0 ? key.Substring(0, dot) : key;
        }

        /// <summary>名 "0x44 端口0 功率" → 参数 "功率"（用于分组）。</summary>
        public static string Parameter(string key, string name, string unitFallback)
        {
            if (string.IsNullOrEmpty(name))
                return string.IsNullOrEmpty(key) ? (unitFallback ?? string.Empty) : key;

            string source = Source(key);
            if (source.Length > 0 && name.StartsWith(source, StringComparison.Ordinal))
            {
                string tail = name.Substring(source.Length).Trim();
                if (tail.Length > 0)
                    return tail;
            }
            return name.Trim();
        }
    }
}
