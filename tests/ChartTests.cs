using System;
using System.Collections.Generic;
using PoECommandTool;
using PoECommandTool.Chart;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        private static SeriesBuffer MakeSeries(string key, string name, string unit, params double[] values)
        {
            var buffer = new SeriesBuffer(key, name, unit, 1000);
            var t = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < values.Length; i++)
                buffer.Add(t.AddMilliseconds(i * 100), values[i]);
            return buffer;
        }

        // ---------------- 从解析结果里抽曲线 ----------------
        private static void TelemetryExtractorTests()
        {
            Console.WriteLine("TelemetryExtractor");

            PortMeasurement m = Rtl8239ResponseParser.ParsePortMeasurement(
                Frame("44 01 03 00 C8 00 80 00 C8 01 2C"));
            IList<SeriesCandidate> candidates = TelemetryExtractor.Extract(m, "0x44 端口3");
            CheckEq(candidates.Count, 4, "0x44 抽出 4 条曲线（电压/电流/温度/功率）");

            var byUnit = new Dictionary<string, double>();
            var keys = new List<string>();
            foreach (SeriesCandidate c in candidates)
            {
                byUnit[c.Unit] = c.Value;
                keys.Add(c.Key);
            }
            CheckEq(byUnit["mV"], 12890.0, "电压 12890 mV");
            CheckEq(byUnit["mA"], 128.0, "电流 128 mA");
            CheckEq(byUnit["W"], 30.0, "功率 30 W");
            CheckEq(byUnit["℃"], 25.0, "温度 25 ℃");
            Check(keys.Contains("0x44 端口3.PowerW"), "key 带上端口，不会与别的端口串台");
            Check(keys.Contains("0x44 端口3.VoltageMv"), "电压 key 正确");

            var names = new List<string>();
            foreach (SeriesCandidate c in candidates) names.Add(c.Name);
            Check(names.Contains("0x44 端口3 功率"), "图例名可读：" + string.Join(" / ", names.ToArray()));

            IList<SeriesCandidate> power = TelemetryExtractor.Extract(
                Rtl8239ResponseParser.ParseGlobalPowerStatus(Frame("41 01 01 2C 01 2C 00 01 2C FF FF")), "0x41");
            CheckEq(power.Count, 3, "0x41 抽出 3 条系统功率");
            foreach (SeriesCandidate c in power)
                CheckEq(c.Unit, "W", "0x41 单位都是 W：" + c.Key);

            IList<SeriesCandidate> channels = TelemetryExtractor.Extract(
                Rtl8239ResponseParser.ParsePortChannelVoltageCurrent(Frame("4F 01 07 00 C8 00 80 00 C8 00 80")), "0x4F 端口7");
            CheckEq(channels.Count, 4, "0x4F 抽出主/副各电压电流，共 4 条");

            // 这是最关键的一条：0x42 本来就没有测量量
            PortStatus status = Rtl8239ResponseParser.ParsePortStatus(Frame("42 01 05 02 44 FF FF 00 FF FF FF"));
            CheckEq(TelemetryExtractor.Extract(status, "0x42 端口5").Count, 0,
                "0x42 抽不出任何曲线（端口状态里没有电压/电流/功率），界面要给出提示而不是静默不画");
            Check(!TelemetryExtractor.HasNumericSeries("0x42"), "0x42 被标记为「无可用数值量」");
            Check(TelemetryExtractor.HasNumericSeries("0x44"), "0x44 被标记为「有数值量」");

            IList<SeriesCandidate> params4a = TelemetryExtractor.Extract(
                Rtl8239ResponseParser.ParseGlobalParameters(Frame("4A 01 10 00 FF FF FF 20 FF FF FF")), "0x4A");
            CheckEq(params4a.Count, 2, "0x4A 抽出 UVLO/OVLO 两条阈值");
            CheckEq(params4a[0].Unit, "V", "阈值单位是 V");

            CheckEq(TelemetryExtractor.Extract(null, "无").Count, 0, "null 输入返回空，不抛");
            CheckEq(TelemetryExtractor.Extract(new object(), "无").Count, 0, "没有数值字段的对象返回空");

            CheckEq(TelemetryExtractor.TryGetPort(m), 3, "从 0x44 结果里读出回显端口");
            CheckEq(TelemetryExtractor.TryGetPort(status), 5, "从 0x42 结果里读出回显端口");
            CheckEq(TelemetryExtractor.TryGetPort(new object()), -1, "没有 Port 字段时返回 -1");
            CheckEq(TelemetryExtractor.TryGetPort(null), -1, "null 返回 -1");
            Console.WriteLine();
        }

        // ---------------- 环形缓冲 ----------------
        private static void SeriesBufferTests()
        {
            Console.WriteLine("SeriesBuffer");

            var clockBase = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var small = new SeriesBuffer("k", "n", "W", 5);
            for (int i = 0; i < 8; i++)
                small.Add(clockBase.AddSeconds(i), i * 10.0);

            CheckEq(small.Count, 5, "容量满后 Count = 容量");
            SeriesSample oldest;
            small.TryGetLast(out oldest);
            CheckEq(oldest.Value, 70.0, "最后一个样本是最新的");
            CheckEq(small[0].Value, 30.0, "最旧的三个已被覆盖");

            Check(small.Add(clockBase, 1.0), "时钟回拨时仍然接受采样（夹紧时间戳而不是丢数据）");
            CheckEq(small[small.Count - 1].Time, clockBase.AddSeconds(7), "回拨样本的时间被夹紧到上一个采样点");
            CheckEq(small[small.Count - 1].Value, 1.0, "值仍然记录下来了");

            Check(!small.Add(clockBase.AddSeconds(9), double.NaN), "NaN 被拒绝");
            Check(!small.Add(clockBase.AddSeconds(9), double.PositiveInfinity), "无穷大被拒绝");
            Check(small.Add(clockBase.AddSeconds(9), 88.0), "正常样本被接受");

            var window = new List<SeriesSample>();
            small.CopyRange(clockBase.AddSeconds(5), clockBase.AddSeconds(9), window);
            CheckEq(window.Count, 5, "按时间窗口取样本");
            Check(window[0].Time <= window[window.Count - 1].Time, "窗口内保持时间顺序");

            var trim = new SeriesBuffer("k2", "n2", "W", 100);
            for (int i = 0; i < 10; i++)
                trim.Add(clockBase.AddSeconds(i), i);
            CheckEq(trim.Trim(clockBase.AddSeconds(6)), 6, "Trim 丢掉过期样本");
            CheckEq(trim.Count, 4, "Trim 后剩下的条数");
            CheckEq(trim[0].Value, 6.0, "Trim 后最旧的是 6");

            trim.Clear();
            CheckEq(trim.Count, 0, "Clear 清空");
            SeriesSample none;
            Check(!trim.TryGetLast(out none), "空缓冲没有最后样本");
            Console.WriteLine();
        }

        // ---------------- 图表计算 ----------------
        private static void ChartMathTests()
        {
            Console.WriteLine("ChartMath");

            // 刻度步长必须是 1/2/5 × 10ⁿ
            double[] ranges = { 0.03, 0.7, 3.3, 12.0, 55.0, 320.0, 12890.0, 1234567.0 };
            bool familyOk = true;
            foreach (double range in ranges)
            {
                double step = ChartMath.NiceStep(range, 5);
                double magnitude = Math.Pow(10, Math.Floor(Math.Log10(step)));
                double mantissa = step / magnitude;
                if (Math.Abs(mantissa - 1) > 1e-9 && Math.Abs(mantissa - 2) > 1e-9 && Math.Abs(mantissa - 5) > 1e-9)
                    familyOk = false;
            }
            Check(familyOk, "刻度步长恒为 1/2/5 × 10ⁿ");

            bool countOk = true;
            foreach (double range in ranges)
            {
                double min = 0;
                double max = range;
                double step = ChartMath.NiceStep(range, 5);
                int n = ChartMath.BuildTicks(min, max, step).Count;
                if (n < 3 || n > 12) countOk = false;
            }
            Check(countOk, "刻度条数落在合理区间");

            CheckEq(ChartMath.BuildTicks(0, 0, 1).Count, 1, "退化成单点时给出一个刻度");
            CheckEq(ChartMath.BuildTicks(0, 10, 0).Count, 0, "步长为 0 时没有刻度");
            CheckEq(ChartMath.BuildTicks(5, 1, 1).Count, 0, "min > max 时没有刻度");

            double lo = 10, hi = 10;
            ChartMath.ExpandRange(ref lo, ref hi);
            Check(lo < 10 && hi > 10, "min == max 时只往两侧撑开");
            lo = 10; hi = 30;
            ChartMath.ExpandRange(ref lo, ref hi);
            Check(lo < 10 && hi > 30, "普通量程上下留边距");
            lo = double.NaN; hi = double.NaN;
            ChartMath.ExpandRange(ref lo, ref hi);
            CheckEq(lo, 0.0, "NaN 量程退化成 [0,1]");
            CheckEq(hi, 1.0, "NaN 量程退化成 [0,1]");

            CheckEq(ChartMath.FormatTick(0.30000000000000004, 0.1), "0.3", "小步长不显示浮点噪声");
            CheckEq(ChartMath.FormatTick(12345.6, 1), "12346", "大数值按步长取整");
            CheckEq(ChartMath.FormatTick(-0.0000001, 1), "0", "不显示 -0");
            CheckEq(ChartMath.FormatTick(12890.0, 5000), "12890", "整数值不带小数");

            CheckEq(ChartMath.MapY(100, 0, 100, 0, 200), 0.0, "最大值映射到顶边");
            CheckEq(ChartMath.MapY(0, 0, 100, 0, 200), 200.0, "最小值映射到底边");
            CheckEq(ChartMath.MapY(50, 0, 100, 0, 200), 100.0, "中点映射到中线");

            var from = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var to = from.AddSeconds(60);
            CheckEq(ChartMath.MapX(from, from, to, 30, 400), 30.0, "窗口起点映射到左边");
            CheckEq(ChartMath.MapX(to, from, to, 30, 400), 430.0, "窗口终点映射到右边");

            double stepSeconds;
            string format;
            ChartMath.ChooseTimeStep(2, out stepSeconds, out format);
            CheckEq(stepSeconds, 1.0, "2 秒窗口步长 1 秒（免得只剩一个刻度）");
            CheckEq(format, "HH:mm:ss", "短窗口用秒级标签");
            ChartMath.ChooseTimeStep(10, out stepSeconds, out format);
            CheckEq(format, "HH:mm:ss", "10 秒窗口用秒级标签");
            CheckEq(stepSeconds, 2.0, "10 秒窗口步长 2 秒（约 5 个刻度）");
            ChartMath.ChooseTimeStep(300, out stepSeconds, out format);
            CheckEq(stepSeconds, 60.0, "5 分钟窗口步长 1 分钟");
            ChartMath.ChooseTimeStep(7200, out stepSeconds, out format);
            CheckEq(format, "HH:mm:ss", "2 小时窗口仍可带秒");
            CheckEq(stepSeconds, 1800.0, "2 小时窗口步长 30 分钟");
            ChartMath.ChooseTimeStep(86400, out stepSeconds, out format);
            CheckEq(format, "HH:mm", "超过 6 小时用分钟级标签");
            CheckEq(stepSeconds, 3600.0, "一天窗口步长 1 小时");

            // 降采样
            var dense = new List<SeriesSample>();
            for (int i = 0; i < 5000; i++)
                dense.Add(new SeriesSample(from.AddMilliseconds(i * 20), 10.0));
            dense[2500] = new SeriesSample(from.AddMilliseconds(2500 * 20), 999.0);   // 一个尖峰

            var reduced = new List<SeriesSample>();
            ChartMath.Downsample(dense, from, from.AddSeconds(100), 100, reduced);
            Check(reduced.Count <= 200, "降采样后点数不超过 2×maxPoints：" + reduced.Count);

            double peak = 0;
            bool monotonic = true;
            DateTime previous = DateTime.MinValue;
            foreach (SeriesSample s in reduced)
            {
                if (s.Value > peak) peak = s.Value;
                if (s.Time < previous) monotonic = false;
                previous = s.Time;
            }
            CheckEq(peak, 999.0, "尖峰在降采样后仍然保留");
            Check(monotonic, "降采样后时间单调不减");

            var few = new List<SeriesSample>();
            ChartMath.Downsample(MakeSamples(20), from, from.AddSeconds(100), 100, few);
            CheckEq(few.Count, 20, "点数不多时原样输出");
            ChartMath.Downsample(new List<SeriesSample>(), from, to, 100, few);
            CheckEq(few.Count, 0, "空输入输出空，不抛");

            // 整图布局
            SeriesBuffer power = MakeSeries("p", "0x44 端口0 功率", "W", 12, 18, 25, 30, 22, 8);
            SeriesBuffer current = MakeSeries("c", "0x44 端口0 电流", "mA", 100, 300, 700, 900, 500, 200);
            var series = new List<SeriesBuffer> { power, current };

            ChartLayout layout = ChartMath.Build(series, from, from.AddSeconds(2), 40, 0, 400, 200, 200);
            CheckEq(layout.Bands.Count, 2, "两种单位 → 两个子图");
            CheckEq(layout.Bands[0].Unit, "W", "先出现的单位在前");
            CheckEq(layout.Bands[1].Unit, "mA", "后出现的单位在后");
            Check(layout.Bands[0].Max < 100, "W 子图的量程只由 W 曲线决定：" + layout.Bands[0].Max);
            Check(layout.Bands[1].Max > 500, "mA 子图的量程只由 mA 曲线决定：" + layout.Bands[1].Max);
            Check(layout.Bands[0].Top >= 0 && layout.Bands[1].Top > layout.Bands[0].Top, "子图自上而下排布");
            Check(layout.Bands[1].Top + layout.Bands[1].Height <= 200.0001, "最后一个子图不超出绘图区");
            CheckEq(layout.Bands[0].TickValues.Length, layout.Bands[0].TickLabels.Length, "刻度值与标签一一对应");
            Check(layout.TimeTickLabels.Length >= 2, "时间轴有刻度：" + string.Join(" ", layout.TimeTickLabels));
            Check(layout.HasData, "布局报告有数据");

            ChartLine line = layout.Bands[0].Lines[0];
            CheckEq(line.Xs.Length, line.Ys.Length, "X/Y 数组等长");
            CheckEq(line.LastValue, 8.0, "图例显示的当前值是最新值");
            Check(line.Min <= 8.0 && line.Max >= 30.0, "曲线自己的量程正确");

            power.Visible = false;
            ChartLayout onlyCurrent = ChartMath.Build(series, from, from.AddSeconds(2), 40, 0, 400, 200, 200);
            CheckEq(onlyCurrent.Bands.Count, 1, "取消勾选后只剩一个子图");
            CheckEq(onlyCurrent.Bands[0].Unit, "mA", "剩下的是 mA");

            current.Visible = false;
            ChartLayout none = ChartMath.Build(series, from, from.AddSeconds(2), 40, 0, 400, 200, 200);
            CheckEq(none.Bands.Count, 0, "全部取消勾选时没有子图");
            Check(!none.HasData, "全隐藏时报告没有数据");

            // ---------------- 时间回看窗口 ----------------
            var stamp = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            DateTime visibleFrom;
            DateTime visibleTo;

            ChartMath.ResolveWindow(TimeSpan.FromMinutes(1), TimeSpan.Zero, stamp, stamp.AddMinutes(-5),
                out visibleFrom, out visibleTo);
            CheckEq(visibleTo, stamp, "不带偏移时跟着最新数据走");
            CheckEq(visibleFrom, stamp.AddMinutes(-1), "窗口宽度等于设定的时间窗");

            ChartMath.ResolveWindow(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), stamp, stamp,
                out visibleFrom, out visibleTo);
            CheckEq(visibleTo, stamp.AddMinutes(-3), "偏移 3 分钟：窗口末端是 3 分钟前");
            CheckEq(visibleFrom, stamp.AddMinutes(-4), "回看时窗口宽度不变");

            // 关键：过了很久，回看位置仍钉在原处，不会被时间推着滑走
            ChartMath.ResolveWindow(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), stamp.AddMinutes(10), stamp,
                out visibleFrom, out visibleTo);
            CheckEq(visibleTo, stamp.AddMinutes(-3), "时间流逝后回看位置仍钉在原处（画面不会自己滑走）");

            // ---------------- 十字准线读数 ----------------
            SeriesBuffer readout = MakeSeries("ro", "0x44 端口0 功率", "W", 10, 20, 30);
            ChartLayout readoutLayout = ChartMath.Build(new List<SeriesBuffer> { readout },
                from, from.AddSeconds(2), 40, 0, 400, 200, 200);
            ChartLine readoutLine = readoutLayout.Bands[0].Lines[0];

            CheckEq(readoutLine.Values.Length, readoutLine.Xs.Length, "数值数组与像素 X 一一对应");
            CheckEq(readoutLine.Values.Length, readoutLine.Ys.Length, "数值数组与像素 Y 一一对应");
            CheckEq(readoutLine.Values[0], 10.0, "第 1 个点的数值");
            CheckEq(readoutLine.Values[2], 30.0, "第 3 个点的数值");
            Check(readoutLine.Values[1] > readoutLine.Values[0], "读数按时间顺序排列（用于找最近点）");

            Check(ChartMath.StepOf(readoutLayout.Bands[0].TickValues) > 0, "能从刻度推回步长（决定读数位数）");
            CheckEq(ChartMath.StepOf(new double[0]), 1.0, "没有刻度时步长回退为 1");
            CheckEq(ChartMath.StepOf(null), 1.0, "null 安全");

            // 显示单位换算：552977.9 mV 这种大数读起来费劲
            double unitScale;
            string displayUnit;
            Check(ChartMath.TryScaleUnit("mV", 52977.9, out unitScale, out displayUnit)
                  && displayUnit == "V" && Math.Abs(unitScale - 0.001) < 1e-12,
                "mV 到 1000 以上换算成 V");
            Check(!ChartMath.TryScaleUnit("mV", 500, out unitScale, out displayUnit) && displayUnit == "mV",
                "mV 不到 1000 保持原样（500 mV 就是 500 mV）");
            Check(ChartMath.TryScaleUnit("mA", 1500, out unitScale, out displayUnit) && displayUnit == "A",
                "mA 到 1000 以上换算成 A");
            Check(!ChartMath.TryScaleUnit("W", 50, out unitScale, out displayUnit) && displayUnit == "W",
                "W 不换算");
            Check(!ChartMath.TryScaleUnit("℃", 30, out unitScale, out displayUnit), "温度不换算");
            Check(!ChartMath.TryScaleUnit(null, 1000, out unitScale, out displayUnit), "null 单位安全");
            Check(ChartMath.TryScaleUnit("mV", -2000, out unitScale, out displayUnit) && displayUnit == "V",
                "负值也按绝对值判断");

            // ---------------- 纵轴缩放 ----------------
            SeriesBuffer zoomPower = MakeSeries("zp", "0x44 端口0 功率", "W", 10, 20, 30);
            var zoomSeries = new List<SeriesBuffer> { zoomPower };

            ChartLayout zoomAuto = ChartMath.Build(zoomSeries, from, from.AddSeconds(2), 40, 0, 400, 200, 200);
            ChartLayout zoomIn = ChartMath.Build(zoomSeries, from, from.AddSeconds(2), 40, 0, 400, 200, 200, 2.0);
            ChartLayout zoomOut = ChartMath.Build(zoomSeries, from, from.AddSeconds(2), 40, 0, 400, 200, 200, 0.5);

            double autoSpan = zoomAuto.Bands[0].Max - zoomAuto.Bands[0].Min;
            double inSpan = zoomIn.Bands[0].Max - zoomIn.Bands[0].Min;
            double outSpan = zoomOut.Bands[0].Max - zoomOut.Bands[0].Min;

            CheckNear(inSpan, autoSpan / 2, 1e-9, "纵轴放大 ×2：量程跨度减半");
            CheckNear(outSpan, autoSpan * 2, 1e-9, "纵轴缩小 ×0.5：量程跨度翻倍");
            CheckNear((zoomIn.Bands[0].Max + zoomIn.Bands[0].Min) / 2,
                (zoomAuto.Bands[0].Max + zoomAuto.Bands[0].Min) / 2, 1e-9,
                "缩放围绕量程中心，不会跑偏");

            ChartLayout zoomBad = ChartMath.Build(zoomSeries, from, from.AddSeconds(2), 40, 0, 400, 200, 200, double.NaN);
            CheckEq(zoomBad.Bands[0].Min, zoomAuto.Bands[0].Min, "非法缩放倍数按「自动」处理");
            CheckEq(ChartMath.Build(zoomSeries, from, from.AddSeconds(2), 40, 0, 400, 200, 200, 0).Bands[0].Min,
                zoomAuto.Bands[0].Min, "缩放倍数 0 也按自动处理");

            // 多子图时各自围绕自己的中心缩放
            SeriesBuffer zoomCurrent = MakeSeries("zc", "0x44 端口0 电流", "mA", 100, 500, 900);
            var mixedZoom = new List<SeriesBuffer> { zoomPower, zoomCurrent };
            ChartLayout mixedAuto = ChartMath.Build(mixedZoom, from, from.AddSeconds(2), 40, 0, 400, 200, 200);
            ChartLayout mixedIn = ChartMath.Build(mixedZoom, from, from.AddSeconds(2), 40, 0, 400, 200, 200, 2.0);
            CheckEq(mixedIn.Bands.Count, 2, "两个单位两个子图");
            CheckNear(mixedIn.Bands[0].Max - mixedIn.Bands[0].Min,
                (mixedAuto.Bands[0].Max - mixedAuto.Bands[0].Min) / 2, 1e-9, "W 子图按 W 自己的中心缩放");
            CheckNear(mixedIn.Bands[1].Max - mixedIn.Bands[1].Min,
                (mixedAuto.Bands[1].Max - mixedAuto.Bands[1].Min) / 2, 1e-9, "mA 子图按 mA 自己的中心缩放");
            CheckNear((mixedIn.Bands[1].Max + mixedIn.Bands[1].Min) / 2, 500.0, 0.001,
                "mA 子图缩放中心仍是数据中点 500");

            // 缩放后像素映射跟着变：量程中心落在子图垂直中点
            double bandMid = mixedIn.Bands[0].Top + mixedIn.Bands[0].Height / 2;
            CheckNear(ChartMath.MapY((mixedIn.Bands[0].Max + mixedIn.Bands[0].Min) / 2,
                mixedIn.Bands[0].Min, mixedIn.Bands[0].Max, mixedIn.Bands[0].Top, mixedIn.Bands[0].Height),
                bandMid, 1e-9, "缩放后量程中心映射到子图中线");
            Console.WriteLine();
        }

        /// <summary>图例分组：多端口时同一个参数要能归到一组。</summary>
        private static void SeriesLabelTests()
        {
            Console.WriteLine("SeriesLabel（图例分组）");

            CheckEq(SeriesLabel.Source("0x44 端口0.PowerW"), "0x44 端口0", "从 key 取出来源");
            CheckEq(SeriesLabel.Source("0x41.SystemCurrentPowerW"), "0x41", "无端口命令的来源");
            CheckEq(SeriesLabel.Source("plain"), "plain", "没有点号时原样返回");
            CheckEq(SeriesLabel.Source(null), "", "null 安全");

            CheckEq(SeriesLabel.Parameter("0x44 端口0.PowerW", "0x44 端口0 功率", "W"), "功率", "从名取参数");
            CheckEq(SeriesLabel.Parameter("0x41.SystemAllocatedPowerW", "0x41 系统已分配功率", "W"),
                "系统已分配功率", "无端口命令的参数");
            CheckEq(SeriesLabel.Parameter("k", "完全不同的名字", "W"), "完全不同的名字", "名不含来源时回退到整个名");
            CheckEq(SeriesLabel.Parameter("k", "", "W"), "k", "名为空时回退到 key");

            // 真正关心的场景：4 个端口 × 0x44 → 每个参数一组、每组 4 条
            var groups = new Dictionary<string, List<string>>();
            for (byte port = 0; port < 4; port++)
            {
                PortMeasurement parsed = Rtl8239ResponseParser.ParsePortMeasurement(
                    Frame(string.Format("44 01 {0:X2} 00 C8 00 80 00 C8 01 2C", port)));
                string tag = "0x44 端口" + port;

                IList<SeriesCandidate> candidates = TelemetryExtractor.Extract(parsed, tag);
                foreach (SeriesCandidate c in candidates)
                {
                    string group = SeriesLabel.Parameter(c.Key, c.Name, c.Unit);
                    List<string> bucket;
                    if (!groups.TryGetValue(group, out bucket))
                    {
                        bucket = new List<string>();
                        groups[group] = bucket;
                    }
                    bucket.Add(SeriesLabel.Source(c.Key));
                }
            }

            CheckEq(groups.Count, 4, "4 个参数 → 4 个组（电压/电流/温度/功率）");
            CheckEq(groups["功率"].Count, 4, "「功率」组里收了 4 个端口");
            CheckEq(groups["功率"][2], "0x44 端口2", "组内按端口列出");
            CheckEq(groups["电压"].Count, 4, "「电压」组里也有 4 个");
            CheckEq(groups["温度"][0], "0x44 端口0", "组内第一项是端口 0");
            Console.WriteLine();
        }

        private static List<SeriesSample> MakeSamples(int count)
        {
            var list = new List<SeriesSample>();
            var start = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < count; i++)
                list.Add(new SeriesSample(start.AddMilliseconds(i * 100), i));
            return list;
        }
    }
}
