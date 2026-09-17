using System;
using System.Collections.Generic;
using System.Globalization;

namespace WpfApp1.Chart
{
    /// <summary>时间轴上的一个事件标记。</summary>
    public sealed class ChartMarker
    {
        public DateTime Time { get; private set; }
        public string Label { get; private set; }

        public ChartMarker(DateTime time, string label)
        {
            Time = time;
            Label = label;
        }
    }

    /// <summary>
    /// 把解析出来的事件类响应变成时间轴上的标记。
    ///
    /// 为什么要有它：`0x46` 端口事件早就解析出来了，但**从来没画到图上**——
    /// 端口断开、故障这些事件在日志里一闪而过，事后对不上曲线上的拐点。
    /// 标到时间轴上，才能一眼看出「这条曲线在这里掉下去，是因为那个端口出事了」。
    ///
    /// 做成纯逻辑是为了能断言位映射——位序搞反了只是「标记画错位置」，
    /// 在图上不容易发现。
    /// </summary>
    public static class EventMarkers
    {
        /// <summary>一个响应最多能带多少个端口事件位（0x46 的位图是 48 位）。</summary>
        public const int MaxPortBits = 48;

        /// <summary>
        /// 从一次解析结果里取出该记的标记；不是事件类响应就返回空表。
        ///
        /// <paramref name="portOffset"/>：这台设备的端口号从多少开始算（位图的 bit i 对应哪个端口）。
        /// </summary>
        public static List<ChartMarker> From(object parsed, DateTime time, int portOffset = 0)
        {
            var markers = new List<ChartMarker>();
            if (!(parsed is PortEventStatus))
                return markers;

            var status = (PortEventStatus)parsed;

            if (status.DisconnectEventOccurred)
                markers.Add(new ChartMarker(time, "断连事件"));
            if (status.FaultEventOccurred)
                markers.Add(new ChartMarker(time, "故障事件"));

            ulong bitmap = status.PortEventBitmap;
            for (int bit = 0; bit < MaxPortBits; bit++)
            {
                if ((bitmap & (1UL << bit)) == 0)
                    continue;

                markers.Add(new ChartMarker(time,
                    "端口 " + (bit + portOffset).ToString(CultureInfo.InvariantCulture) + " 事件"));
            }

            return markers;
        }
    }
}
