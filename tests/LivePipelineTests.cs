using System;
using System.Collections.Generic;
using WpfApp1;
using WpfApp1.Chart;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        /// <summary>
        /// 完全照界面上的链路走一遍：采样时用 DateTime.Now 打时间戳 → 按「实时窗口」构建 →
        /// 每条曲线都必须有像素点落在绘图区里。专门防「图上有坐标轴却没有线」这类问题。
        /// </summary>
        private static void LivePipelineTests()
        {
            Console.WriteLine("实时链路（采样 → 实时窗口出图）");

            var buffers = new List<SeriesBuffer>();
            var byKey = new Dictionary<string, SeriesBuffer>();
            DateTime t0 = DateTime.Now;

            // 模拟 5 轮采样，每轮间隔 200ms（与真机轮询节奏接近）。
            // 时间戳必须落在「现在」之前——界面上的采样永远是这样打的。
            int count = 5;
            for (int i = 0; i < count; i++)
            {
                PortMeasurement parsed = Rtl8239ResponseParser.ParsePortMeasurement(
                    Frame("44 01 00 03 36 02 21 00 C5 01 21"));
                string tag = "0x44 端口0";

                foreach (SeriesCandidate c in TelemetryExtractor.Extract(parsed, tag))
                {
                    SeriesBuffer buffer;
                    if (!byKey.TryGetValue(c.Key, out buffer))
                    {
                        buffer = new SeriesBuffer(c.Key, c.Name, c.Unit);
                        byKey[c.Key] = buffer;
                        buffers.Add(buffer);
                    }
                    buffer.Add(t0.AddMilliseconds(-(count - 1 - i) * 200), c.Value);
                }
            }

            CheckEq(buffers.Count, 4, "采到 4 条曲线");
            foreach (SeriesBuffer buffer in buffers)
                CheckEq(buffer.Count, 5, buffer.Name + " 收下 5 个采样点");

            // 与 ChartPlotElement.OnRender 里完全相同的取窗口方式
            DateTime from;
            DateTime to;
            ChartMath.ResolveWindow(TimeSpan.FromMinutes(1), TimeSpan.Zero, DateTime.Now, DateTime.Now,
                out from, out to);

            Check(from < t0 && t0 < to, "刚采到的数据落在实时窗口内（窗口没跑偏）");

            // 诊断：看 5 个采样点是在哪一步掉成 1 个的
            var probe = new List<SeriesSample>();
            buffers[0].CopyRange(from, to, probe);
            Console.WriteLine("    诊断: buffer.Count=" + buffers[0].Count
                + " 窗口=" + from.ToString("HH:mm:ss.fff") + "~" + to.ToString("HH:mm:ss.fff")
                + " 采样时刻=" + t0.ToString("HH:mm:ss.fff")
                + " CopyRange=" + probe.Count);

            var reduced = new List<SeriesSample>();
            ChartMath.Downsample(probe, from, to, 400, reduced);
            Console.WriteLine("    诊断: Downsample=" + reduced.Count);

            ChartLayout layout = ChartMath.Build(buffers, from, to, 62, 8, 500, 300, 400);
            CheckEq(layout.Bands.Count, 4, "mV / mA / ℃ / W 四个子图");
            Check(layout.HasData, "布局报告有数据");

            int points = 0;
            bool allInside = true;
            for (int b = 0; b < layout.Bands.Count; b++)
            {
                ChartBand band = layout.Bands[b];
                for (int i = 0; i < band.Lines.Count; i++)
                {
                    ChartLine line = band.Lines[i];
                    if (!line.HasData || line.Xs.Length == 0)
                    {
                        Check(false, line.Name + " 没有像素点（就是「画不出来」）");
                        continue;
                    }

                    points += line.Xs.Length;

                    // 像素点必须落在子图的绘图区里（页眉那一行不算），否则等于画到外面去了
                    for (int k = 0; k < line.Xs.Length; k++)
                    {
                        double x = line.Xs[k];
                        double y = line.Ys[k];
                        if (x < 62 - 1 || x > 62 + 500 + 1) allInside = false;
                        if (y < band.PlotTop - 1 || y > band.PlotTop + band.PlotHeight + 1) allInside = false;
                    }
                }
            }

            // 页眉：文字（单位 + 变化量）只占这一行，绝不侵入绘图区去压曲线
            for (int b = 0; b < layout.Bands.Count; b++)
            {
                ChartBand band = layout.Bands[b];
                Check(band.PlotTop >= band.Top && band.PlotHeight > 0
                      && band.PlotTop + band.PlotHeight <= band.Top + band.Height + 1e-9,
                    band.Unit + " 子图的绘图区落在子图范围内");
                Check(band.Top + band.HeaderHeight <= band.PlotTop + 1e-9,
                    band.Unit + " 子图的页眉不侵入绘图区");
            }
            Check(layout.Bands[0].HeaderHeight > 0, "够高的子图有页眉（单位和变化量放页眉里）");

            // 挤的时候不留页眉，把高度全让给曲线
            ChartLayout crampedLayout = ChartMath.Build(buffers, from, to, 62, 8, 500, 130, 400);
            CheckEq(crampedLayout.Bands[0].HeaderHeight, 0.0, "子图矮时不留页眉");
            Check(crampedLayout.Bands[0].PlotHeight > 0, "矮子图仍有可画的绘图区");
            ChartLayout generousLayout = ChartMath.Build(buffers, from, to, 62, 8, 500, 400, 400);
            Check(generousLayout.Bands[0].HeaderHeight > 0, "子图高时留出页眉");

            CheckEq(points, 20, "4 条 × 5 点 = 20 个像素点");
            Check(allInside, "所有像素点都落在绘图区内（没有画到界外）");

            // 子图太矮时该标几个刻度：宁可少标，也不能叠成一团把曲线盖住
            Check(ChartMath.ShouldLabelTick(0, 5, 100) && ChartMath.ShouldLabelTick(2, 5, 100),
                "子图够高（100px）时每个刻度都标");
            Check(ChartMath.ShouldLabelTick(0, 5, 30) && ChartMath.ShouldLabelTick(4, 5, 30),
                "子图只有 30px 时标首尾");
            Check(!ChartMath.ShouldLabelTick(1, 5, 30) && !ChartMath.ShouldLabelTick(3, 5, 30),
                "子图只有 30px 时不标中间那些");
            Check(!ChartMath.ShouldLabelTick(0, 5, 20) && !ChartMath.ShouldLabelTick(4, 5, 20),
                "子图不足 26px 时一个刻度都不标（留给曲线）");
            Check(!ChartMath.ShouldLabelTick(0, 0, 100), "没有刻度时不标");
            Check(!ChartMath.ShouldLabelTick(9, 5, 100), "越界索引不标");

            Check(ChartMath.ShouldShowRangeText(100), "子图够高时显示量程与变化量");
            Check(!ChartMath.ShouldShowRangeText(30), "子图矮时不显示量程文本（它比曲线还占地方）");

            // 数据一直在更新时，窗口末端应跟着走
            DateTime laterFrom;
            DateTime laterTo;
            ChartMath.ResolveWindow(TimeSpan.FromMinutes(1), TimeSpan.Zero, DateTime.Now.AddSeconds(30),
                DateTime.Now, out laterFrom, out laterTo);
            Check(laterTo > to, "30 秒后窗口跟着前进（实时跟随没坏）");

            ChartLayout laterLayout = ChartMath.Build(buffers, laterFrom, laterTo, 62, 8, 500, 300, 400);
            Check(laterLayout.HasData, "30 秒后的数据仍在 1 分钟窗口内（时间窗行为正确）");

            // 窗口缩短到 1 秒，刚采的数据就该滑出去了
            DateTime shortFrom;
            DateTime shortTo;
            ChartMath.ResolveWindow(TimeSpan.FromSeconds(1), TimeSpan.Zero, DateTime.Now.AddSeconds(30),
                DateTime.Now, out shortFrom, out shortTo);
            ChartLayout shortLayout = ChartMath.Build(buffers, shortFrom, shortTo, 62, 8, 500, 300, 400);
            Check(!shortLayout.HasData, "窗口缩到 1 秒后旧数据滑出（确认取窗口没问题）");

            // 每条曲线的量程里必须包含自己的点（否则线会被裁掉）
            for (int b = 0; b < layout.Bands.Count; b++)
            {
                ChartBand band = layout.Bands[b];
                for (int i = 0; i < band.Lines.Count; i++)
                {
                    ChartLine line = band.Lines[i];
                    if (!line.HasData) continue;
                    if (line.Min < band.Min || line.Max > band.Max)
                    {
                        Check(false, line.Name + " 的点跑到量程外了");
                        break;
                    }
                }
            }
            Check(true, "每条曲线的点都在子图量程内");
            Console.WriteLine();
        }
    }
}
