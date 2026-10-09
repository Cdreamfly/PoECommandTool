using System;
using System.Collections.Generic;
using PoECommandTool;
using PoECommandTool.Chart;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        private static void SeriesColorTests()
        {
            Console.WriteLine("SeriesColor（多端口不撞色）");

            var seen = new HashSet<string>();
            bool distinct = true;
            for (int i = 0; i < 32; i++)
            {
                if (!seen.Add(SeriesColor.For(i)))
                    distinct = false;
            }
            Check(distinct, "前 32 条曲线颜色互不相同（原来的固定 8 色循环会撞色）");
            CheckEq(seen.Count, 32, "32 个序号 → 32 种颜色");

            CheckEq(SeriesColor.For(0), SeriesColor.For(0), "同一序号永远给同一颜色（曲线隐藏/显示不会变色）");
            CheckEq(SeriesColor.For(-1), SeriesColor.For(0), "负数按 0 处理");
            CheckEq(SeriesColor.For(100), SeriesColor.For(100), "序号很大也稳定");
            Check(SeriesColor.For(100) != SeriesColor.For(101), "序号很大也互不相同");

            string hex = SeriesColor.For(3);
            Check(hex.StartsWith("#") && hex.Length == 7, "颜色是 #RRGGBB 形式：" + hex);
            Check(hex.Substring(1, 6) == hex.Substring(1, 6).ToUpperInvariant(), "十六进制大写：" + hex);
            Console.WriteLine();
        }

        private static void SeriesStatsTests()
        {
            Console.WriteLine("SeriesStats（曲线统计）");

            var from = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var buffer = new SeriesBuffer("k", "0x44 端口0 功率", "W", 100);
            double[] values = { 10, 20, 30, 40, 50 };
            for (int i = 0; i < values.Length; i++)
                buffer.Add(from.AddSeconds(i), values[i]);

            SeriesStatistics stats;
            Check(SeriesStats.TryCompute(buffer, from.AddSeconds(-1), from.AddSeconds(10), out stats), "能算出统计");
            CheckEq(stats.Count, 5, "采样点数");
            CheckNear(stats.Min, 10, 1e-9, "最小值");
            CheckNear(stats.Max, 50, 1e-9, "最大值");
            CheckNear(stats.Average, 30, 1e-9, "平均值");
            CheckNear(stats.Range, 40, 1e-9, "峰峰值 = 最大 − 最小");
            CheckNear(stats.Change, 40, 1e-9, "净变化 = 末 − 首");
            CheckNear(stats.First, 10, 1e-9, "首值");
            CheckNear(stats.Last, 50, 1e-9, "末值");
            CheckNear(stats.StdDev, Math.Sqrt(250), 1e-6, "样本标准差");
            CheckNear(stats.Span.TotalSeconds, 4, 1e-9, "时间跨度");
            CheckNear(stats.AverageIntervalSeconds, 1.0, 1e-9, "平均采样间隔");

            // 只统计时间窗内的点
            SeriesStatistics windowed;
            Check(SeriesStats.TryCompute(buffer, from.AddSeconds(1), from.AddSeconds(3), out windowed),
                "窗口内有数据");
            CheckEq(windowed.Count, 3, "窗口内 3 个点（20/30/40）");
            CheckNear(windowed.Min, 20, 1e-9, "窗口内最小值");
            CheckNear(windowed.Max, 40, 1e-9, "窗口内最大值");
            CheckNear(windowed.Average, 30, 1e-9, "窗口内平均值");
            CheckNear(windowed.Range, 20, 1e-9, "窗口内峰峰值");

            SeriesStatistics empty;
            Check(!SeriesStats.TryCompute(buffer, from.AddSeconds(30), from.AddSeconds(60), out empty),
                "窗口内没有数据时返回 false");
            Check(!SeriesStats.TryCompute(null, from, from.AddSeconds(1), out empty), "null 缓冲安全");

            // 单个点：标准差与间隔按 0 处理，不能出现除零
            var single = new SeriesBuffer("s", "单点", "W", 10);
            single.Add(from, 42);
            SeriesStatistics one;
            Check(SeriesStats.TryCompute(single, from.AddSeconds(-1), from.AddSeconds(1), out one), "单点也能算");
            CheckEq(one.Count, 1, "单点统计的采样点数");
            CheckNear(one.StdDev, 0, 1e-9, "单点标准差为 0（不除零）");
            CheckNear(one.AverageIntervalSeconds, 0, 1e-9, "单点平均间隔为 0");

            // 文本格式：单位换算与图表一致（52900 mV 显示成 52.9 V）
            var voltage = new SeriesBuffer("v", "0x44 端口0 电压", "mV", 10);
            voltage.Add(from, 52900);
            voltage.Add(from.AddSeconds(1), 53100);
            SeriesStatistics voltageStats;
            SeriesStats.TryCompute(voltage, from.AddSeconds(-1), from.AddSeconds(5), out voltageStats);

            string summary = SeriesStats.FormatSummary(voltageStats, "mV");
            Check(summary.Contains("最小 52.9") && summary.Contains("最大 53.1"),
                "摘要按 V 显示（与图表一致）：" + summary);
            Check(summary.Contains("差 0.2 V"), "摘要含差值：" + summary);

            string detail = SeriesStats.FormatDetail("0x44 端口0 电压", voltageStats, "mV");
            Check(detail.Contains("峰峰值"), "详情含峰峰值");
            Check(detail.Contains("标准差"), "详情含标准差");
            Check(detail.Contains("净变化"), "详情含净变化");
            Check(detail.Contains("采样点数：2"), "详情含采样点数");
            Check(detail.Contains("52.9 V"), "详情里的数值也换算成 V");
            Console.WriteLine();
        }
    }
}
