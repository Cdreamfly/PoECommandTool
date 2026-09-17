using System;
using System.Collections.Generic;
using PoECommandTool;
using PoECommandTool.Chart;

namespace Rtl8239Verify
{
    /// <summary>
    /// 把 0x46 端口事件变成时间轴标记。位序搞反了只是「标记画在错误的端口上」，
    /// 在图上不容易发现，所以逐个位钉住。
    /// </summary>
    internal static partial class Program
    {
        private static PortEventStatus EventFrame(byte mask, byte status, ulong bitmap)
        {
            var e = new PortEventStatus();
            e.EventMask = mask;
            e.EventStatus = status;
            e.PortEventBitmap = bitmap;
            return e;
        }

        private static void EventMarkerTests()
        {
            Console.WriteLine("EventMarkers 事件标记");

            var when = new DateTime(2026, 9, 17, 15, 30, 0);

            // 不是事件类响应 → 不产生标记（别的响应不该在时间轴上留痕）
            CheckEq(EventMarkers.From(new object(), when).Count, 0, "非事件响应不产生标记");
            CheckEq(EventMarkers.From(null, when).Count, 0, "null 不产生标记");

            // 位图全零、也没有事件标志 → 没有标记
            CheckEq(EventMarkers.From(EventFrame(0, 0, 0), when).Count, 0, "没有事件时不产生标记");

            // 单个端口事件：bit 0 = 端口 0
            List<ChartMarker> one = EventMarkers.From(EventFrame(0, 0, 1UL << 0), when);
            CheckEq(one.Count, 1, "一个端口事件 → 一条标记");
            Check(one[0].Label.Contains("端口 0"), "bit0 对应端口 0：" + one[0].Label);
            CheckEq(one[0].Time, when, "时间就是传进来的那一刻");

            // 位序：bit 5 = 端口 5（不是「第 5 个标记」）
            List<ChartMarker> bit5 = EventMarkers.From(EventFrame(0, 0, 1UL << 5), when);
            CheckEq(bit5.Count, 1, "只有 bit5 时只有一条");
            Check(bit5[0].Label.Contains("端口 5"), "bit5 对应端口 5：" + bit5[0].Label);

            // 多位同时置起
            List<ChartMarker> multi = EventMarkers.From(EventFrame(0, 0, (1UL << 0) | (1UL << 3) | (1UL << 11)), when);
            CheckEq(multi.Count, 3, "三位置起 → 三条标记");
            Check(multi[0].Label.Contains("端口 0") && multi[1].Label.Contains("端口 3")
                  && multi[2].Label.Contains("端口 11"), "按位序从小到大");

            // 端口偏移（不同设备端口号起点不同）
            List<ChartMarker> offset = EventMarkers.From(EventFrame(0, 0, 1UL << 2), when, 8);
            Check(offset[0].Label.Contains("端口 10"), "带偏移时 bit2 → 端口 10：" + offset[0].Label);

            // 高位（48 位里的末位）
            List<ChartMarker> last = EventMarkers.From(EventFrame(0, 0, 1UL << 47), when);
            CheckEq(last.Count, 1, "bit47 也算得到");
            Check(last[0].Label.Contains("端口 47"), "bit47 对应端口 47：" + last[0].Label);

            // 全局事件标志
            var flags = EventFrame(0, 0, 0);
            flags.DisconnectEventOccurred = true;
            flags.FaultEventOccurred = true;
            List<ChartMarker> global = EventMarkers.From(flags, when);
            CheckEq(global.Count, 2, "断连 + 故障两个全局标志 → 两条");
            Check(global[0].Label.Contains("断连"), "第一条是断连");
            Check(global[1].Label.Contains("故障"), "第二条是故障");

            // 全局标志与端口位同时出现
            var both = EventFrame(0, 0, 1UL << 1);
            both.FaultEventOccurred = true;
            CheckEq(EventMarkers.From(both, when).Count, 2, "全局标志与端口位可以同时出现");

            CheckEq(EventMarkers.MaxPortBits, 48, "位图是 48 位");
        }
    }
}
