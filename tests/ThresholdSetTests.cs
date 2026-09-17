using System;
using System.Collections.Generic;
using WpfApp1.Chart;

namespace Rtl8239Verify
{
    /// <summary>
    /// 阈值参考线的解析。写错要**抛**而不是静默忽略——静默的话用户会以为阈值功能坏了。
    /// </summary>
    internal static partial class Program
    {
        private static void ThresholdSetTests()
        {
            Console.WriteLine("ThresholdSet 阈值解析");

            CheckEq(ThresholdSet.Parse("").Count, 0, "空文本 → 空表（不画线）");
            CheckEq(ThresholdSet.Parse(null).Count, 0, "null → 空表");
            CheckEq(ThresholdSet.Parse("   ").Count, 0, "全空白 → 空表");

            var one = ThresholdSet.Parse("W=30");
            CheckEq(one.Count, 1, "一条");
            CheckEq(ThresholdSet.For(one, "W"), (double?)30.0, "W = 30");

            var many = ThresholdSet.Parse("W=30, ℃=70");
            CheckEq(many.Count, 2, "逗号分隔两条");
            CheckEq(ThresholdSet.For(many, "℃"), (double?)70.0, "℃ = 70");

            CheckEq(ThresholdSet.Parse("W=30；℃=70").Count, 2, "中文分号也认");
            CheckEq(ThresholdSet.Parse("W=30\n℃=70").Count, 2, "换行也认");
            CheckEq(ThresholdSet.Parse(" W = 30 ").Count, 1, "两侧空白容忍");

            CheckEq(ThresholdSet.Parse("w=30").Count, 1, "单位大小写不敏感");
            CheckEq(ThresholdSet.For(ThresholdSet.Parse("w=30"), "W"), (double?)30.0, "w 与 W 是同一个");

            CheckEq(ThresholdSet.For(one, "mA"), null, "没设过的单位返回 null");
            CheckEq(ThresholdSet.For(null, "W"), null, "空表不炸");

            // 小数与负数（温度有可能是负的）
            CheckEq(ThresholdSet.For(ThresholdSet.Parse("℃=-10.5"), "℃"), (double?)(-10.5), "负数阈值");

            // 写错了要抛，且说清是哪儿不对
            string noEq = ThrownBy(delegate { ThresholdSet.Parse("W30"); });
            Check(noEq.StartsWith("FormatException") && noEq.Contains("单位=数值"),
                "缺等号报错并说明格式：" + noEq);

            string badValue = ThrownBy(delegate { ThresholdSet.Parse("W=abc"); });
            Check(badValue.StartsWith("FormatException"), "数值不是数报错：" + badValue);

            string noValue = ThrownBy(delegate { ThresholdSet.Parse("W="); });
            Check(noValue.StartsWith("FormatException"), "只有单位没数值报错：" + noValue);

            // 回写
            Check(ThresholdSet.Describe(one).Contains("W=30"), "回写一条：" + ThresholdSet.Describe(one));
            CheckEq(ThresholdSet.Describe(null), "", "空表回写成空串");
        }
    }
}
