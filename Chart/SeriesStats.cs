using System;
using System.Globalization;

namespace WpfApp1.Chart
{
    /// <summary>一条曲线在指定时间窗内的统计量。</summary>
    public struct SeriesStatistics
    {
        public int Count;
        public double Min;
        public double Max;
        public double Average;
        public double StdDev;
        public double First;
        public double Last;
        public DateTime FirstTime;
        public DateTime LastTime;

        /// <summary>峰峰值（最大 − 最小）：这段里起伏有多大。</summary>
        public double Range
        {
            get { return Max - Min; }
        }

        /// <summary>净变化（末值 − 首值）：这段里涨了还是跌了。</summary>
        public double Change
        {
            get { return Last - First; }
        }

        public TimeSpan Span
        {
            get { return LastTime - FirstTime; }
        }

        /// <summary>平均采样间隔（秒）；点数不足时返回 0。</summary>
        public double AverageIntervalSeconds
        {
            get { return Count > 1 ? Span.TotalSeconds / (Count - 1) : 0; }
        }
    }

    /// <summary>
    /// 按时间窗算一条曲线的统计量，并格式化成可读文本。
    /// 单位换算（mV→V 等）与图表保持一致，免得图上是 52.98 V、详情里是 52977.9 mV。
    /// </summary>
    public static class SeriesStats
    {
        /// <summary>算指定时间窗内的统计量；窗口内没有点就返回 false。</summary>
        public static bool TryCompute(SeriesBuffer buffer, DateTime from, DateTime to, out SeriesStatistics stats)
        {
            stats = new SeriesStatistics();
            if (buffer == null)
                return false;

            int count = 0;
            double sum = 0;
            double sumSquares = 0;
            double min = double.MaxValue;
            double max = double.MinValue;
            double first = 0;
            double last = 0;
            DateTime firstTime = DateTime.MinValue;
            DateTime lastTime = DateTime.MinValue;

            for (int i = 0; i < buffer.Count; i++)
            {
                SeriesSample sample = buffer[i];
                if (sample.Time < from)
                    continue;
                if (sample.Time > to)
                    break;

                if (count == 0)
                {
                    first = sample.Value;
                    firstTime = sample.Time;
                }

                last = sample.Value;
                lastTime = sample.Time;

                if (sample.Value < min) min = sample.Value;
                if (sample.Value > max) max = sample.Value;
                sum += sample.Value;
                sumSquares += sample.Value * sample.Value;
                count++;
            }

            if (count == 0)
                return false;

            stats.Count = count;
            stats.Min = min;
            stats.Max = max;
            stats.First = first;
            stats.Last = last;
            stats.FirstTime = firstTime;
            stats.LastTime = lastTime;
            stats.Average = sum / count;

            if (count > 1)
            {
                double variance = (sumSquares - sum * sum / count) / (count - 1);
                stats.StdDev = variance > 0 ? Math.Sqrt(variance) : 0;
            }

            return true;
        }

        /// <summary>一行紧凑摘要，放图例里用。</summary>
        public static string FormatSummary(SeriesStatistics stats, string unit)
        {
            double scale;
            string displayUnit;
            Scale(stats, unit, out scale, out displayUnit);

            return string.Format(CultureInfo.InvariantCulture,
                "最小 {0} / 最大 {1} / 平均 {2} / 差 {3} {4}",
                Format(stats.Min * scale), Format(stats.Max * scale),
                Format(stats.Average * scale), Format(stats.Range * scale), displayUnit);
        }

        /// <summary>多行详情，弹窗里用。</summary>
        public static string FormatDetail(string title, SeriesStatistics stats, string unit)
        {
            double scale;
            string displayUnit;
            Scale(stats, unit, out scale, out displayUnit);

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("曲线：" + (title ?? "(未命名)"));
            builder.AppendLine("时间范围：" + stats.FirstTime.ToString("HH:mm:ss.fff")
                + "  ~  " + stats.LastTime.ToString("HH:mm:ss.fff")
                + "   时长 " + Format(stats.Span.TotalSeconds) + " 秒");
            builder.AppendLine("采样点数：" + stats.Count
                + "   平均间隔 " + Format(stats.AverageIntervalSeconds) + " 秒");
            builder.AppendLine();
            builder.AppendLine("最小：" + Format(stats.Min * scale) + " " + displayUnit);
            builder.AppendLine("最大：" + Format(stats.Max * scale) + " " + displayUnit);
            builder.AppendLine("平均：" + Format(stats.Average * scale) + " " + displayUnit);
            builder.AppendLine("峰峰值（最大 − 最小）：" + Format(stats.Range * scale) + " " + displayUnit);
            builder.AppendLine("标准差：" + Format(stats.StdDev * scale) + " " + displayUnit
                + "   （越小越稳）");
            builder.AppendLine();
            builder.AppendLine("首值：" + Format(stats.First * scale) + " " + displayUnit);
            builder.AppendLine("末值：" + Format(stats.Last * scale) + " " + displayUnit);
            builder.AppendLine("净变化（末 − 首）：" + Format(stats.Change * scale) + " " + displayUnit);

            if (stats.Min != 0)
            {
                double percent = stats.Range / Math.Abs(stats.Average == 0 ? stats.Min : stats.Average) * 100.0;
                builder.AppendLine("相对波动（峰峰值 / 平均）：" + Format(percent) + " %");
            }

            return builder.ToString();
        }

        private static void Scale(SeriesStatistics stats, string unit, out double scale, out string displayUnit)
        {
            double magnitude = Math.Max(Math.Abs(stats.Min), Math.Abs(stats.Max));
            if (!ChartMath.TryScaleUnit(unit, magnitude, out scale, out displayUnit))
            {
                scale = 1.0;
                displayUnit = unit;
            }
        }

        private static string Format(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
