using System;
using System.Collections.Generic;
using PoECommandTool.Chart;

namespace Rtl8239Verify
{
    /// <summary>
    /// 曲线缓冲的按需增长。原先是一次性分配满 20000 条（每条 16 字节 = 320KB），
    /// 48 端口 × 4 参数就是 61MB 压在大对象堆上，而且一个采样点都还没有。
    ///
    /// 改成按需翻倍之后，环形语义必须一字不差：顺序、覆盖最旧的时机、
    /// 以及增长时按时间重排。
    /// </summary>
    internal static partial class Program
    {
        private static SeriesBuffer MakeBuffer(int capacity)
        {
            return new SeriesBuffer("k", "曲线", "W", capacity);
        }

        private static void SeriesGrowthTests()
        {
            Console.WriteLine("SeriesBuffer 按需增长");

            var buffer = MakeBuffer(8);
            CheckEq(buffer.Capacity, 8, "Capacity 报告的是配置上限，不是当前数组长度");
            CheckEq(buffer.Count, 0, "新建时是空的");

            // ---- 还没长到上限：一条不少，顺序正确 ----
            for (int i = 0; i < 5; i++)
                buffer.Add(new DateTime(2026, 9, 17, 12, 0, i), i);

            CheckEq(buffer.Count, 5, "加了 5 条就是 5 条");
            CheckEq(buffer[0].Value, 0.0, "第 0 条是最旧的");
            CheckEq(buffer[4].Value, 4.0, "第 4 条是最新的");

            // ---- 越过初始数组长度 → 触发增长，顺序必须保持 ----
            for (int i = 5; i < 8; i++)
                buffer.Add(new DateTime(2026, 9, 17, 12, 0, i), i);

            CheckEq(buffer.Count, 8, "长到上限是 8 条");
            for (int i = 0; i < 8; i++)
                CheckEq(buffer[i].Value, (double)i, "增长之后第 " + i + " 条仍是按时间排的");

            // ---- 到上限之后：覆盖最旧的 ----
            for (int i = 8; i < 12; i++)
                buffer.Add(new DateTime(2026, 9, 17, 12, 0, i), i);

            CheckEq(buffer.Count, 8, "到上限后不再增长");
            CheckEq(buffer[0].Value, 4.0, "最旧的一条被覆盖掉了");
            CheckEq(buffer[7].Value, 11.0, "最新的一条在末尾");

            // ---- 增长之后窗口拷贝仍然按时间有序 ----
            var big = MakeBuffer(100);
            for (int i = 0; i < 50; i++)
                big.Add(new DateTime(2026, 9, 17, 12, 0, 0).AddMilliseconds(i), i);

            var window = new List<SeriesSample>();
            big.CopyRange(new DateTime(2026, 9, 17, 12, 0, 0).AddMilliseconds(10),
                new DateTime(2026, 9, 17, 12, 0, 0).AddMilliseconds(14), window);

            CheckEq(window.Count, 5, "窗口 [10,14] 取到 5 条");
            CheckEq(window[0].Value, 10.0, "窗口内第一条是 10");
            CheckEq(window[4].Value, 14.0, "窗口内最后一条是 14");

            // ---- 时间回拨仍然夹紧到上一条（与增长无关的既有语义，别被这次改动带跑）----
            var clamp = MakeBuffer(4);
            clamp.Add(new DateTime(2026, 9, 17, 12, 0, 5), 1.0);
            clamp.Add(new DateTime(2026, 9, 17, 12, 0, 1), 2.0);        // 比上一条早
            CheckEq(clamp[1].Time, new DateTime(2026, 9, 17, 12, 0, 5), "回拨的时间被夹紧到上一条");

            // ---- 默认容量下的初始分配要小得多（这是这次改动的意义所在）----
            var dflt = new SeriesBuffer("k", "曲线", "W");
            CheckEq(dflt.Capacity, SeriesBuffer.DefaultCapacity, "默认上限仍是 20000");
            Check(SeriesBuffer.InitialCapacity * 16 * 4 < SeriesBuffer.DefaultCapacity * 16,
                "初始只分配 InitialCapacity 条，一条曲线的开局占用降到 1/20 量级");
        }
    }
}
