using System;
using System.Collections.Generic;
using System.Globalization;

namespace WpfApp1.Chart
{
    /// <summary>一条曲线在像素坐标下的样子。</summary>
    public sealed class ChartLine
    {
        public ChartLine()
        {
            Xs = new double[0];
            Ys = new double[0];
            Values = new double[0];
        }

        public string Key { get; set; }
        public string Name { get; set; }
        public string Unit { get; set; }
        public string ColorHex { get; set; }
        public double[] Xs { get; set; }
        public double[] Ys { get; set; }
        /// <summary>与 Xs/Ys 一一对应的数值（十字准线读数要用）。</summary>
        public double[] Values { get; set; }
        public bool HasData { get; set; }
        public double LastValue { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
    }

    /// <summary>一个单位分组——一个子图。W 和 mA 差 1000 倍，画在同一条 Y 轴上没法看。</summary>
    public sealed class ChartBand
    {
        public ChartBand()
        {
            Lines = new List<ChartLine>();
            TickValues = new double[0];
            TickLabels = new string[0];
        }

        public string Unit { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Top { get; set; }
        public double Height { get; set; }

        /// <summary>顶部页眉（单位 + 变化量文本）占的高度；0 表示没有页眉。</summary>
        public double HeaderHeight { get; set; }

        /// <summary>绘图区（曲线、网格、刻度）的顶边 = Top + HeaderHeight。</summary>
        public double PlotTop { get; set; }

        /// <summary>绘图区高度 = Height − HeaderHeight。</summary>
        public double PlotHeight { get; set; }

        public double[] TickValues { get; set; }
        public string[] TickLabels { get; set; }
        public List<ChartLine> Lines { get; private set; }
    }

    /// <summary>整张图的布局结果（纯像素与文本，绘制层照着画就行）。</summary>
    public sealed class ChartLayout
    {
        public ChartLayout()
        {
            Bands = new List<ChartBand>();
            TimeTickXs = new double[0];
            TimeTickLabels = new string[0];
        }

        public List<ChartBand> Bands { get; private set; }
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public double[] TimeTickXs { get; set; }
        public string[] TimeTickLabels { get; set; }

        public bool HasData
        {
            get
            {
                for (int i = 0; i < Bands.Count; i++)
                    for (int j = 0; j < Bands[i].Lines.Count; j++)
                        if (Bands[i].Lines[j].HasData)
                            return true;
                return false;
            }
        }
    }

    /// <summary>
    /// 图表的全部计算：单位分组、量程、刻度、坐标映射、降采样。
    /// 全是纯函数，所以能完整测试；绘制层只负责把算好的像素画出来。
    /// </summary>
    public static class ChartMath
    {
        /// <summary>量程上下留白比例。</summary>
        public const double RangeMarginRatio = 0.05;

        /// <summary>每个子图期望的刻度条数。</summary>
        public const int TargetTicksPerBand = 5;

        /// <summary>子图低于这个高度就精简刻度与标注（否则文字会叠成一团，比曲线还显眼）。</summary>
        public const double CompactBandHeight = 46;

        /// <summary>再矮就只画曲线与单位，刻度标签和量程标注都省掉。</summary>
        public const double MinimalBandHeight = 26;

        /// <summary>子图顶部的页眉高度：单位与「变化」文本只占这条，绝不压住曲线。</summary>
        public const double BandHeaderHeight = 15;

        /// <summary>子图之间的像素间距。</summary>
        public const double BandGap = 10;

        /// <summary>
        /// 从 1/2/5×10ⁿ 里挑一个让刻度数最接近 targetTicks 的步长。
        /// </summary>
        public static double NiceStep(double range, int targetTicks)
        {
            if (double.IsNaN(range) || double.IsInfinity(range) || range <= 0)
                return 1;
            if (targetTicks < 2)
                targetTicks = 2;

            double rough = range / targetTicks;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
            double normalized = rough / magnitude;

            double step;
            if (normalized <= 1) step = 1;
            else if (normalized <= 2) step = 2;
            else if (normalized <= 5) step = 5;
            else step = 10;

            return step * magnitude;
        }

        /// <summary>把量程上下各撑开一点，避免曲线贴着边框；min == max 时也要撑开。</summary>
        public static void ExpandRange(ref double min, ref double max)
        {
            if (double.IsNaN(min) || double.IsNaN(max) || min > max)
            {
                min = 0;
                max = 1;
                return;
            }

            if (min == max)
            {
                double pad = min == 0 ? 1 : Math.Abs(min) * 0.1;
                min -= pad;
                max += pad;
                return;
            }

            double margin = (max - min) * RangeMarginRatio;
            min -= margin;
            max += margin;
        }

        /// <summary>按步长生成 [min, max] 内的刻度值。</summary>
        public static List<double> BuildTicks(double min, double max, double step)
        {
            var ticks = new List<double>();
            if (step <= 0 || double.IsNaN(step) || double.IsInfinity(step) || min > max)
                return ticks;

            double start = Math.Ceiling(min / step) * step;
            for (double v = start; v <= max + step * 1e-9 && ticks.Count < 64; v += step)
                ticks.Add(v);

            return ticks;
        }

        /// <summary>
        /// 该不该给第 <paramref name="index"/> 个刻度画标签。
        /// 子图够高就全标；矮了只标首尾（量程一眼可见）；再矮就一个都不标——宁可没数字，
        /// 也好过一坨叠在一起的字把曲线盖住。
        /// </summary>
        public static bool ShouldLabelTick(int index, int count, double bandHeight)
        {
            if (count <= 0 || index < 0 || index >= count)
                return false;
            if (bandHeight < MinimalBandHeight)
                return false;
            if (bandHeight < CompactBandHeight)
                return index == 0 || index == count - 1;
            return true;
        }

        /// <summary>子图矮到放不下量程标注时，就把它省掉（曲线比说明重要）。</summary>
        public static bool ShouldShowRangeText(double bandHeight)
        {
            return bandHeight >= CompactBandHeight;
        }

        /// <summary>从刻度值推出步长（用于决定读数显示几位小数）。</summary>
        public static double StepOf(double[] tickValues)
        {
            if (tickValues == null || tickValues.Length < 2)
                return 1.0;
            return tickValues[1] - tickValues[0];
        }

        /// <summary>
        /// 挑一个更好读的显示单位：mV 量级到 1000 以上就换算成 V，mA 同理换算成 A。
        /// 值本身不动，只影响显示（例如 52977.9 mV 显示成 52.98 V）。
        /// </summary>
        /// <returns>true 表示换了单位，<paramref name="scale"/> 是要乘的系数。</returns>
        public static bool TryScaleUnit(string unit, double magnitude, out double scale, out string displayUnit)
        {
            scale = 1.0;
            displayUnit = unit;

            if (string.IsNullOrEmpty(unit))
                return false;

            double abs = Math.Abs(magnitude);
            if (unit == "mV" && abs >= 1000)
            {
                scale = 0.001;
                displayUnit = "V";
                return true;
            }
            if (unit == "mA" && abs >= 1000)
            {
                scale = 0.001;
                displayUnit = "A";
                return true;
            }
            return false;
        }

        /// <summary>刻度文本：按步长决定小数位，避免 0.30000000000000004 这类浮点噪声。</summary>
        public static string FormatTick(double value, double step)
        {
            int decimals = 0;
            if (step > 0 && !double.IsNaN(step) && !double.IsInfinity(step))
            {
                double exponent = Math.Log10(step);
                if (exponent < 0)
                    decimals = (int)Math.Ceiling(-exponent);
                if (decimals > 6)
                    decimals = 6;
            }

            if (value != 0 && Math.Abs(value) < step / 2)
                value = 0;      // 别显示 -0

            return value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);
        }

        /// <summary>时间轴刻度的时间步长与标签格式（按时长选）。</summary>
        public static void ChooseTimeStep(double totalSeconds, out double stepSeconds, out string format)
        {
            format = totalSeconds <= 21600 ? "HH:mm:ss" : "HH:mm";

            if (totalSeconds <= 6) stepSeconds = 1;
            else if (totalSeconds <= 15) stepSeconds = 2;
            else if (totalSeconds <= 30) stepSeconds = 5;
            else if (totalSeconds <= 120) stepSeconds = 15;
            else if (totalSeconds <= 600) stepSeconds = 60;
            else if (totalSeconds <= 3600) stepSeconds = 300;
            else if (totalSeconds <= 21600) stepSeconds = 1800;
            else stepSeconds = 3600;

            // 超过 6 小时换成分钟级标签，秒位没有意义
            if (totalSeconds > 21600)
                format = "HH:mm";
        }

        /// <summary>
        /// 决定这一帧要画的 [from, to]。
        ///
        /// 偏移为 0 时跟着最新数据走（to = now）；偏移不为 0 时把窗口**钉在**锚点那一刻往回偏移的位置——
        /// 回看历史时画面必须停住，否则刚要看的那个尖峰会被时间推着滑出屏幕。
        /// </summary>
        public static void ResolveWindow(TimeSpan span, TimeSpan offset, DateTime now, DateTime anchor,
            out DateTime from, out DateTime to)
        {
            to = offset <= TimeSpan.Zero ? now : anchor - offset;
            from = to - span;
        }

        public static double MapX(DateTime time, DateTime from, DateTime to, double left, double width)        {
            double total = (to - from).Ticks;
            if (total <= 0)
                return left;

            double ratio = (time - from).Ticks / total;
            return left + ratio * width;
        }

        /// <summary>数值 → 像素。屏幕 Y 向下，所以最大值映射到 top。</summary>
        public static double MapY(double value, double min, double max, double top, double height)
        {
            if (max <= min)
                return top + height;

            double ratio = (value - min) / (max - min);
            return top + height - ratio * height;
        }

        /// <summary>
        /// 按像素桶取 min/max 降采样：输出点数不超过 2×maxPoints，且每个桶的极值都保留，
        /// 尖峰不会被抹平。X 轴基本单调（桶内按时间排序）。
        /// </summary>
        public static void Downsample(IList<SeriesSample> samples, DateTime from, DateTime to,
            int maxPoints, List<SeriesSample> destination)
        {
            if (destination == null) throw new ArgumentNullException("destination");

            destination.Clear();
            if (samples == null || samples.Count == 0)
                return;
            if (maxPoints < 1)
                maxPoints = 1;

            if (samples.Count <= maxPoints * 2 || (to - from).Ticks <= 0)
            {
                for (int i = 0; i < samples.Count; i++)
                    destination.Add(samples[i]);
                return;
            }

            int buckets = maxPoints;
            var has = new bool[buckets];
            var minValue = new double[buckets];
            var maxValue = new double[buckets];
            var minTime = new DateTime[buckets];
            var maxTime = new DateTime[buckets];
            double total = (to - from).Ticks;

            for (int i = 0; i < samples.Count; i++)
            {
                SeriesSample sample = samples[i];
                if (sample.Time < from || sample.Time > to)
                    continue;

                int bucket = (int)((sample.Time - from).Ticks / total * buckets);
                if (bucket < 0) bucket = 0;
                if (bucket >= buckets) bucket = buckets - 1;

                if (!has[bucket])
                {
                    has[bucket] = true;
                    minValue[bucket] = maxValue[bucket] = sample.Value;
                    minTime[bucket] = maxTime[bucket] = sample.Time;
                    continue;
                }

                if (sample.Value < minValue[bucket]) { minValue[bucket] = sample.Value; minTime[bucket] = sample.Time; }
                if (sample.Value > maxValue[bucket]) { maxValue[bucket] = sample.Value; maxTime[bucket] = sample.Time; }
            }

            for (int b = 0; b < buckets; b++)
            {
                if (!has[b])
                    continue;

                if (minValue[b] == maxValue[b])
                {
                    destination.Add(new SeriesSample(minTime[b], minValue[b]));
                    continue;
                }

                if (minTime[b] <= maxTime[b])
                {
                    destination.Add(new SeriesSample(minTime[b], minValue[b]));
                    destination.Add(new SeriesSample(maxTime[b], maxValue[b]));
                }
                else
                {
                    destination.Add(new SeriesSample(maxTime[b], maxValue[b]));
                    destination.Add(new SeriesSample(minTime[b], minValue[b]));
                }
            }
        }

        /// <summary>按单位把可见曲线分组（保持出现顺序）。</summary>
        public static List<string> DistinctUnits(IList<SeriesBuffer> series)
        {
            var units = new List<string>();
            if (series == null)
                return units;

            for (int i = 0; i < series.Count; i++)
            {
                if (!series[i].Visible)
                    continue;
                if (!units.Contains(series[i].Unit))
                    units.Add(series[i].Unit);
            }
            return units;
        }

        /// <summary>
        /// 算出整张图：每个单位一条子图，各自量程与刻度，曲线映射成像素。
        /// <paramref name="valueZoom"/> 是纵轴缩放倍数：1 = 按数据铺满，&gt;1 放大（看得清小波动），
        /// &lt;1 缩小（看整体）；缩放围绕该子图当前量程的中心进行。
        /// </summary>
        public static ChartLayout Build(IList<SeriesBuffer> series, DateTime from, DateTime to,
            double left, double top, double width, double height, int maxPointsPerLine,
            double valueZoom = 1.0)
        {
            var layout = new ChartLayout { From = from, To = to };
            if (series == null || width <= 0 || height <= 0 || to <= from)
                return layout;

            if (valueZoom <= 0 || double.IsNaN(valueZoom) || double.IsInfinity(valueZoom))
                valueZoom = 1.0;

            List<string> units = DistinctUnits(series);
            if (units.Count == 0)
                return layout;

            double gap = units.Count > 1 ? BandGap : 0;
            double bandHeight = (height - gap * (units.Count - 1)) / units.Count;
            var buffer = new List<SeriesSample>();

            for (int u = 0; u < units.Count; u++)
            {
                var band = new ChartBand();
                band.Unit = units[u];
                band.Top = top + u * (bandHeight + gap);
                band.Height = bandHeight;

                // 够高就留一条页眉给「单位 + 变化量」；挤的时候不留，把高度全让给曲线
                band.HeaderHeight = ShouldShowRangeText(bandHeight) ? BandHeaderHeight : 0;
                band.PlotTop = band.Top + band.HeaderHeight;
                band.PlotHeight = band.Height - band.HeaderHeight;

                // 先收样本、定总量程
                var collected = new List<KeyValuePair<SeriesBuffer, List<SeriesSample>>>();
                double min = double.MaxValue;
                double max = double.MinValue;
                bool hasData = false;

                for (int i = 0; i < series.Count; i++)
                {
                    SeriesBuffer source = series[i];
                    if (!source.Visible || source.Unit != band.Unit)
                        continue;

                    var samples = new List<SeriesSample>();
                    source.CopyRange(from, to, samples);
                    collected.Add(new KeyValuePair<SeriesBuffer, List<SeriesSample>>(source, samples));

                    for (int k = 0; k < samples.Count; k++)
                    {
                        if (samples[k].Value < min) min = samples[k].Value;
                        if (samples[k].Value > max) max = samples[k].Value;
                        hasData = true;
                    }
                }

                if (!hasData)
                {
                    min = 0;
                    max = 1;
                }
                else
                {
                    ExpandRange(ref min, ref max);
                    if (valueZoom != 1.0)
                    {
                        double center = (min + max) / 2;
                        double half = (max - min) / (2 * valueZoom);
                        min = center - half;
                        max = center + half;
                    }
                }
                band.Min = min;
                band.Max = max;

                double step = NiceStep(max - min, TargetTicksPerBand);
                List<double> ticks = BuildTicks(min, max, step);
                band.TickValues = ticks.ToArray();
                var labels = new string[ticks.Count];
                for (int i = 0; i < ticks.Count; i++)
                    labels[i] = FormatTick(ticks[i], step);
                band.TickLabels = labels;

                for (int i = 0; i < collected.Count; i++)
                {
                    SeriesBuffer source = collected[i].Key;
                    var line = new ChartLine();
                    line.Key = source.Key;
                    line.Name = source.Name;
                    line.Unit = band.Unit;
                    line.ColorHex = source.ColorHex;

                    Downsample(collected[i].Value, from, to, maxPointsPerLine, buffer);
                    line.HasData = buffer.Count > 0;
                    line.Xs = new double[buffer.Count];
                    line.Ys = new double[buffer.Count];
                    line.Values = new double[buffer.Count];

                    double lineMin = double.MaxValue;
                    double lineMax = double.MinValue;
                    for (int k = 0; k < buffer.Count; k++)
                    {
                        line.Xs[k] = MapX(buffer[k].Time, from, to, left, width);
                        line.Ys[k] = MapY(buffer[k].Value, min, max, band.PlotTop, band.PlotHeight);
                        line.Values[k] = buffer[k].Value;
                        if (buffer[k].Value < lineMin) lineMin = buffer[k].Value;
                        if (buffer[k].Value > lineMax) lineMax = buffer[k].Value;
                    }

                    if (line.HasData)
                    {
                        line.Min = lineMin;
                        line.Max = lineMax;
                        line.LastValue = buffer[buffer.Count - 1].Value;
                    }
                    band.Lines.Add(line);
                }

                layout.Bands.Add(band);
            }

            BuildTimeAxis(layout, left, width);
            return layout;
        }

        private static void BuildTimeAxis(ChartLayout layout, double left, double width)
        {
            double totalSeconds = (layout.To - layout.From).TotalSeconds;
            if (totalSeconds <= 0)
                return;

            double stepSeconds;
            string format;
            ChooseTimeStep(totalSeconds, out stepSeconds, out format);

            long stepTicks = (long)(stepSeconds * TimeSpan.TicksPerSecond);
            if (stepTicks <= 0)
                return;

            long firstTicks = ((layout.From.Ticks + stepTicks - 1) / stepTicks) * stepTicks;

            var xs = new List<double>();
            var labels = new List<string>();
            for (long ticks = firstTicks; ticks <= layout.To.Ticks && xs.Count < 16; ticks += stepTicks)
            {
                var time = new DateTime(ticks, layout.From.Kind);
                xs.Add(MapX(time, layout.From, layout.To, left, width));
                labels.Add(time.ToString(format, CultureInfo.InvariantCulture));
            }

            layout.TimeTickXs = xs.ToArray();
            layout.TimeTickLabels = labels.ToArray();
        }
    }
}
