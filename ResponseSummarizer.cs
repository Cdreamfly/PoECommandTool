using System;
using System.Collections.Generic;

namespace WpfApp1
{
    /// <summary>
    /// 把一帧解析结果压成一行可读摘要，用于日志，例如：
    /// <c>0x44 端口0：电压 12890 mV / 电流 128 mA / 温度 25 ℃ / 功率 30 W</c>
    /// <c>0x42 端口0：检测中 / 有效PD / Class 4 / 2-pair</c>
    ///
    /// 两个来源：解析器已经算好的中文描述字段（<c>*Description</c>），
    /// 以及 <see cref="WpfApp1.Chart.TelemetryExtractor"/> 认出来的带单位数值。
    /// 两者都没有时返回 null（调用方就不打这一行）。
    /// </summary>
    public static class ResponseSummarizer
    {
        /// <summary>端口正常时解析器给的占位描述，摘要里不显示（免得每次轮询都刷「无故障」）。</summary>
        private const string NoFaultText = "无故障";

        /// <summary>按 STS1 分流后「本轮用不上」的那个字段，解析器会给这种说明；摘要里不显示。</summary>
        private const string NotApplicablePrefix = "不适用";

        /// <summary>把解析结果压成一行；没有可展示内容时返回 null。</summary>
        public static string Summarize(object parsed, string tag)
        {
            if (parsed == null)
                return null;

            var parts = new List<string>();

            System.Reflection.FieldInfo[] fields =
                parsed.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!fields[i].Name.EndsWith("Description", StringComparison.Ordinal))
                    continue;

                var text = fields[i].GetValue(parsed) as string;
                if (string.IsNullOrEmpty(text) || text == NoFaultText)
                    continue;
                if (text.StartsWith(NotApplicablePrefix, StringComparison.Ordinal))
                    continue;       // 0x42 故障时检测/分级字段的占位说明，摘要里没必要重复两遍
                parts.Add(text);
            }

            IList<WpfApp1.Chart.SeriesCandidate> numerics =
                WpfApp1.Chart.TelemetryExtractor.Extract(parsed, tag);
            for (int i = 0; i < numerics.Count; i++)
            {
                WpfApp1.Chart.SeriesCandidate candidate = numerics[i];

                // 量级大就换成更好读的单位（与图表/图例一致：52977.9 mV → 52.98 V）
                double scale = 1.0;
                string unit = candidate.Unit;
                WpfApp1.Chart.ChartMath.TryScaleUnit(candidate.Unit, candidate.Value, out scale, out unit);

                parts.Add(ShortName(candidate.Name, tag) + " "
                    + FormatValue(candidate.Value * scale) + " " + unit);
            }

            if (parts.Count == 0)
                return null;

            return (tag ?? string.Empty) + "：" + string.Join(" / ", parts.ToArray());
        }

        // 曲线名形如 "0x44 端口0 功率"，这里只要末尾的字段名
        private static string ShortName(string name, string tag)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            if (!string.IsNullOrEmpty(tag) && name.StartsWith(tag, StringComparison.Ordinal))
                return name.Substring(tag.Length).Trim();
            return name;
        }

        private static string FormatValue(double value)
        {
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
