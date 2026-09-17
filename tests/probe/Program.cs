using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfApp1;
using WpfApp1.Chart;

namespace ChartProbe
{
    /// <summary>
    /// 把 ChartPlotElement 离屏渲染成 PNG，这样不用真机也能看清「图上到底画出来了什么」。
    /// </summary>
    internal static class Program
    {
        private const int Width = 660;
        private const int Height = 380;

        [STAThread]
        private static void Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outDir);

            // 真机 0x44 回包（大端）：52.98 V / 545 mA / 28.75 ℃ / 28.9 W
            byte[] frame = ParseHex("44 01 00 03 36 02 21 00 C5 01 21 88");

            Render(outDir, "live.png", BuildSeries(frame, 5, 200, false), TimeSpan.FromMinutes(1), 1.0);
            Render(outDir, "many.png", BuildSeries(frame, 300, 200, true), TimeSpan.FromMinutes(1), 1.0);
            Render(outDir, "zoomed.png", BuildSeries(frame, 300, 200, true), TimeSpan.FromMinutes(1), 5.0);
            Render(outDir, "multiport.png", BuildMultiPort(), TimeSpan.FromMinutes(1), 1.0);
            Render(outDir, "zoom_extreme.png", BuildMultiPort(), TimeSpan.FromMinutes(1), 20.0);

            // 阈值参考线：W 带画在 30W、mA 带画在 550mA —— 两条线应当落在各自的带里
            var thresholds = new Dictionary<string, double> { { "W", 30 }, { "mA", 550 } };
            RenderFull(outDir, "threshold.png", BuildMultiPort(), 1.0, Width, Height, null, thresholds);

            // 事件标记：几条竖直虚线应当落在时间轴上、且不遮挡曲线
            var marks = new List<ChartMarker>();
            DateTime baseTime = DateTime.Now;
            for (int i = 0; i < 3; i++)
                marks.Add(new ChartMarker(baseTime.AddSeconds(-40 + i * 12), "端口 " + i + " 事件"));
            RenderFull(outDir, "markers.png", BuildMultiPort(), 1.0, Width, Height, null, null, marks);

            // 实际界面里曲线那一行只有一百多像素高——这才是用户看到的样子
            RenderSized(outDir, "cramped130.png", BuildSeries(frame, 300, 200, true), 1.0, 660, 130);
            RenderSized(outDir, "cramped100.png", BuildSeries(frame, 300, 200, true), 1.0, 660, 100);
            RenderSized(outDir, "cramped_mp.png", BuildMultiPort(), 1.0, 660, 130);
            RenderSized(outDir, "tall.png", BuildMultiPort(), 1.0, 660, 320);

            // 选中高亮：某条曲线加粗（选中状态在绘图元素上，不在缓冲上）
            RenderSized(outDir, "selected.png", BuildMultiPort(), 1.0, 660, 320, "0x44 端口2.PowerW");

            Console.WriteLine("已生成 PNG 到 " + outDir);
        }

        private static byte[] ParseHex(string text)
        {
            string[] parts = text.Split(' ');
            var bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                bytes[i] = Convert.ToByte(parts[i], 16);
            return bytes;
        }

        /// <summary>照界面上的做法采样：用 DateTime.Now 往前的时刻打时间戳。</summary>
        private static List<SeriesBuffer> BuildSeries(byte[] frame, int count, int stepMs, bool vary)
        {
            var buffers = new List<SeriesBuffer>();
            var byKey = new Dictionary<string, SeriesBuffer>();
            DateTime end = DateTime.Now;

            for (int i = 0; i < count; i++)
            {
                byte[] copy = (byte[])frame.Clone();
                if (vary)
                {
                    // 让功率/电流在小范围里漂，模拟真实负载波动（[9-10] 功率、[5-6] 电流、[11] 校验和）
                    int power = 289 + (int)(12 * Math.Sin(i * 0.35));
                    copy[9] = (byte)(power >> 8);
                    copy[10] = (byte)(power & 0xFF);

                    int current = 545 + (int)(40 * Math.Sin(i * 0.7));
                    copy[5] = (byte)(current >> 8);
                    copy[6] = (byte)(current & 0xFF);

                    copy[11] = Rtl8239CommandBuilder.Checksum(copy, 11);
                }

                PortMeasurement parsed = Rtl8239ResponseParser.ParsePortMeasurement(copy, ByteOrder.BigEndian);
                DateTime time = end.AddMilliseconds(-(count - 1 - i) * stepMs);

                foreach (SeriesCandidate candidate in TelemetryExtractor.Extract(parsed, "0x44 端口0"))
                {
                    SeriesBuffer buffer;
                    if (!byKey.TryGetValue(candidate.Key, out buffer))
                    {
                        buffer = new SeriesBuffer(candidate.Key, candidate.Name, candidate.Unit);
                        byKey[candidate.Key] = buffer;
                        buffers.Add(buffer);
                    }
                    buffer.Add(time, candidate.Value);
                }
            }

            int color = 0;
            foreach (SeriesBuffer buffer in buffers)
                buffer.ColorHex = SeriesColor.For(color++);

            return buffers;
        }

        private static List<SeriesBuffer> BuildMultiPort()
        {
            var buffers = new List<SeriesBuffer>();
            var byKey = new Dictionary<string, SeriesBuffer>();
            DateTime end = DateTime.Now;
            int count = 200;

            for (byte port = 0; port < 4; port++)
            {
                var frame = new byte[12];
                frame[0] = 0x44;
                frame[1] = 0x01;
                frame[2] = port;
                frame[3] = 0x03;
                frame[4] = (byte)(0x36 + port);
                frame[5] = 0x02;
                frame[6] = 0x21;
                frame[7] = 0x00;
                frame[8] = 0xC5;
                frame[9] = 0x01;
                frame[10] = 0x21;
                frame[11] = Rtl8239CommandBuilder.Checksum(frame, 11);

                for (int i = 0; i < count; i++)
                {
                    int power = 200 + port * 40 + (int)(15 * Math.Sin(i * 0.3 + port));
                    frame[9] = (byte)(power >> 8);
                    frame[10] = (byte)(power & 0xFF);
                    frame[11] = Rtl8239CommandBuilder.Checksum(frame, 11);

                    PortMeasurement parsed = Rtl8239ResponseParser.ParsePortMeasurement(frame, ByteOrder.BigEndian);
                    DateTime time = end.AddMilliseconds(-(count - 1 - i) * 300);

                    foreach (SeriesCandidate candidate in TelemetryExtractor.Extract(parsed, "0x44 端口" + port))
                    {
                        SeriesBuffer buffer;
                        if (!byKey.TryGetValue(candidate.Key, out buffer))
                        {
                            buffer = new SeriesBuffer(candidate.Key, candidate.Name, candidate.Unit);
                            byKey[candidate.Key] = buffer;
                            buffers.Add(buffer);
                        }
                        buffer.Add(time, candidate.Value);
                    }
                }
            }

            var perUnit = new Dictionary<string, int>();
            for (int i = 0; i < buffers.Count; i++)
            {
                int used;
                perUnit.TryGetValue(buffers[i].Unit, out used);
                buffers[i].ColorHex = SeriesColor.For(used);
                perUnit[buffers[i].Unit] = used + 1;
            }

            return buffers;
        }

        private static void Render(string outDir, string fileName, List<SeriesBuffer> series,
            TimeSpan window, double zoom)
        {
            RenderSized(outDir, fileName, series, zoom, Width, Height);
        }

        private static void RenderSized(string outDir, string fileName, List<SeriesBuffer> series,
            double zoom, int width, int height)
        {
            RenderSized(outDir, fileName, series, zoom, width, height, null);
        }

        private static void RenderSized(string outDir, string fileName, List<SeriesBuffer> series,
            double zoom, int width, int height, string selectedKey)
        {
            RenderFull(outDir, fileName, series, zoom, width, height, selectedKey, null);
        }

        private static void RenderFull(string outDir, string fileName, List<SeriesBuffer> series,
            double zoom, int width, int height, string selectedKey, Dictionary<string, double> thresholds)
        {
            RenderFull(outDir, fileName, series, zoom, width, height, selectedKey, thresholds, null);
        }

        private static void RenderFull(string outDir, string fileName, List<SeriesBuffer> series,
            double zoom, int width, int height, string selectedKey, Dictionary<string, double> thresholds,
            List<ChartMarker> markers)
        {
            var plot = new ChartPlotElement
            {
                Series = series,
                Window = TimeSpan.FromMinutes(1),
                Thresholds = thresholds,
                Markers = markers,
            };
            plot.ZoomValue(zoom);
            if (selectedKey != null)
                plot.SelectCurve(selectedKey);

            plot.Measure(new Size(width, height));
            plot.Arrange(new Rect(0, 0, width, height));
            plot.UpdateLayout();

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(plot);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(outDir, fileName)))
                encoder.Save(stream);

            Console.WriteLine("  " + fileName + "  " + width + "x" + height + "  曲线 " + series.Count + " 条");
        }
    }
}
