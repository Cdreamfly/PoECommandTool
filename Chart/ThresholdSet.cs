using System;
using System.Collections.Generic;
using System.Globalization;

namespace WpfApp1.Chart
{
    /// <summary>
    /// 曲线上的阈值参考线：按**单位**给一条水平线，例如「功率超过 30W 就画条虚线」。
    ///
    /// 按单位而不是按曲线，是因为图是按单位分带的（mV / mA / ℃ / W 各一条带），
    /// 一条阈值线落在对应那条带里，一眼能看出哪条曲线越过了它。
    ///
    /// 解析规则做成纯逻辑是为了能断言——输入框里的东西写错了不该只是「没反应」。
    /// </summary>
    public static class ThresholdSet
    {
        /// <summary>
        /// 解析 <c>"W=30, ℃=70"</c> 这样的文本；空文本返回空表（表示不画）。
        ///
        /// 分隔符宽容一些（逗号 / 分号 / 换行都认），因为现场多半是随手敲的。
        /// 单位大小写不敏感。**写错就抛**，让调用方把原因显示出来——
        /// 静默忽略的话，用户会以为阈值没生效是工具坏了。
        /// </summary>
        public static Dictionary<string, double> Parse(string text)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
                return result;

            string[] parts = text.Split(new[] { ',', '，', ';', '；', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0)
                    continue;

                int eq = part.IndexOf('=');
                if (eq <= 0 || eq == part.Length - 1)
                    throw new FormatException("「" + part + "」要写成 单位=数值，例如 W=30");

                string unit = part.Substring(0, eq).Trim();
                string valueText = part.Substring(eq + 1).Trim();

                double value;
                if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw new FormatException("「" + part + "」里的数值看不懂：" + valueText);

                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new FormatException("「" + part + "」里的数值不是有限数。");

                result[unit] = value;
            }

            return result;
        }

        /// <summary>取某个单位上的阈值；没设过返回 null。</summary>
        public static double? For(IDictionary<string, double> thresholds, string unit)
        {
            if (thresholds == null || string.IsNullOrEmpty(unit))
                return null;

            double value;
            return thresholds.TryGetValue(unit, out value) ? (double?)value : null;
        }

        /// <summary>回写成可编辑的形式，例如 <c>"W=30；℃=70"</c>。</summary>
        public static string Describe(IDictionary<string, double> thresholds)
        {
            if (thresholds == null || thresholds.Count == 0)
                return string.Empty;

            var parts = new List<string>();
            foreach (KeyValuePair<string, double> pair in thresholds)
                parts.Add(pair.Key + "=" + pair.Value.ToString("0.###", CultureInfo.InvariantCulture));

            return string.Join("；", parts.ToArray());
        }
    }
}
