using System;
using System.Globalization;

namespace WpfApp1.Chart
{
    /// <summary>
    /// 给曲线分配颜色。
    ///
    /// 固定的 8 色循环在多端口下会撞色（比如 mV 子图里挤了 12 条电压曲线），
    /// 所以这里按**黄金角**推色相：相邻两条的色相差最大，取多少条都不会重复。
    /// 同一条曲线只在创建时分配一次颜色，之后隐藏/显示其它曲线都不会让它变色。
    /// </summary>
    public static class SeriesColor
    {
        /// <summary>色相步进（黄金角），保证任意条数下相邻色差最大。</summary>
        private const double GoldenAngle = 137.507764;

        /// <summary>起始色相：偏蓝，第一眼看过去不像报警色。</summary>
        private const double StartHue = 210.0;

        private const double Saturation = 0.78;
        private const double Value = 0.86;

        /// <summary>第 index 条曲线的颜色（"#RRGGBB"）。</summary>
        public static string For(int index)
        {
            if (index < 0)
                index = 0;

            double hue = (StartHue + index * GoldenAngle) % 360.0;
            if (hue < 0)
                hue += 360.0;

            return ToHex(HsvToRgb(hue, Saturation, Value));
        }

        private static int[] HsvToRgb(double hue, double saturation, double value)
        {
            double c = value * saturation;
            double h = hue / 60.0;
            double x = c * (1 - Math.Abs(h % 2 - 1));
            double m = value - c;

            double r = 0, g = 0, b = 0;
            if (h < 1) { r = c; g = x; }
            else if (h < 2) { r = x; g = c; }
            else if (h < 3) { g = c; b = x; }
            else if (h < 4) { g = x; b = c; }
            else if (h < 5) { r = x; b = c; }
            else { r = c; b = x; }

            return new[]
            {
                (int)Math.Round((r + m) * 255),
                (int)Math.Round((g + m) * 255),
                (int)Math.Round((b + m) * 255),
            };
        }

        private static string ToHex(int[] rgb)
        {
            return "#"
                + Clamp(rgb[0]).ToString("X2", CultureInfo.InvariantCulture)
                + Clamp(rgb[1]).ToString("X2", CultureInfo.InvariantCulture)
                + Clamp(rgb[2]).ToString("X2", CultureInfo.InvariantCulture);
        }

        private static int Clamp(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return value;
        }
    }
}
