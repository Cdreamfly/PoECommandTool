using System;
using System.Collections.Generic;
using WpfApp1.Serial;

namespace Rtl8239Verify
{
    /// <summary>
    /// 通讯日志的增量取行。两个视图（页内框、独立窗口）靠它的行号落位。
    ///
    /// 这里钉的是两个真实存在过的毛病：首次渲染多一个空行；满上限后每次追加都全量重建。
    /// </summary>
    internal static partial class Program
    {
        /// <summary>
        /// 模拟一个视图消费一次增量：<paramref name="view"/> 是它已经在显示的内容，
        /// 整体重建时清空重填，否则只追加新行。这跟两个真实视图（页内框 / 独立窗口）的做法一致。
        /// </summary>
        private static void Feed(CommLog log, List<string> view, ref long shown)
        {
            bool rebuild;
            long from;
            log.GetView(shown, out rebuild, out from);

            if (rebuild)
            {
                view.Clear();
                for (long i = log.FirstIndex; i < log.NextIndex; i++)
                    view.Add(log.LineAt(i));
            }
            else
            {
                for (long i = from; i < log.NextIndex; i++)
                    view.Add(log.LineAt(i));
            }

            shown = log.NextIndex;
        }

        private static void CommLogTests()
        {
            Console.WriteLine("CommLog 增量取行");

            var log = new CommLog(5);

            // --- 空日志：什么都不用做，不能凭空冒出一个空行 ---
            bool rebuild;
            long from;
            log.GetView(0, out rebuild, out from);
            Check(!rebuild, "空日志时不需要重建");
            CheckEq(from, 0L, "空日志时 from == NextIndex，调用方什么都不做");

            // --- 首次渲染走整体重建（这正是「开头不多一个空行」的根据）---
            log.Append("a");
            log.Append("b");
            log.GetView(0, out rebuild, out from);
            Check(rebuild, "视图还没显示过 → 整体重建");
            CheckEq(from, 0L, "重建从第一行开始");

            long shown = 0;
            var view = new List<string>();
            Feed(log, view, ref shown);
            CheckEq(view.Count, 2, "首次渲染拿到两行");
            CheckEq(view[0], "a", "第一行就是 a（前面没有多余的空行）");

            // --- 之后是增量，而且只有新行 ---
            log.Append("c");
            log.GetView(shown, out rebuild, out from);
            Check(!rebuild, "已经在跟上的视图走增量，不重建");
            CheckEq(from, 2L, "增量从第 2 行开始");

            Feed(log, view, ref shown);
            CheckEq(view.Count, 3, "累计三行");
            CheckEq(view[2], "c", "新增的是 c");

            // --- 没有新行时什么都不做（旧写法在满上限后会每次全量重建）---
            log.GetView(shown, out rebuild, out from);
            Check(!rebuild && from == log.NextIndex, "没有新行 → 调用方什么都不做");

            // --- 超出上限：丢最旧的，行号继续往前 ---
            for (int i = 0; i < 10; i++)
                log.Append("x" + i);

            // a/b/c 三行 + x0..x9 十行 = 累计 13 行；上限 5，所以只留最后 5 行
            CheckEq(log.Count, 5, "上限 5 行");
            CheckEq(log.FirstIndex, 8L, "累计 13 行、只留 5 行 → FirstIndex 推进到 8");
            CheckEq(log.NextIndex, 13L, "NextIndex 是累计写入过的行数");
            CheckEq(log.LineAt(log.FirstIndex), "x5", "留下来的最旧一行是 x5");

            // --- 视图落后太多（它没看到的行已经被丢掉）→ 必须整体重建，内容才自洽 ---
            long stale = 3;
            var staleView = new List<string>();
            log.GetView(stale, out rebuild, out from);
            Check(rebuild, "视图落后到被丢弃的部分之前 → 整体重建");

            Feed(log, staleView, ref stale);
            CheckEq(staleView.Count, 5, "重建后正好是上限行数");
            CheckEq(staleView[0], "x5", "重建内容从当前第一行开始");

            // --- 两个视图各记各的行号，互不影响 ---
            var two = new CommLog(100);
            long viewA = 0;
            long viewB = 0;
            var aLines = new List<string>();
            var bLines = new List<string>();

            two.Append("1");
            two.Append("2");
            Feed(two, aLines, ref viewA);
            two.Append("3");
            Feed(two, bLines, ref viewB);

            CheckEq(aLines.Count, 2, "A 视图先看到两行");
            CheckEq(bLines.Count, 3, "B 视图晚一步打开，拿到完整历史（首次一律重建）");
            CheckEq(viewA, 2L, "A 记着自己显示到第 2 行");
            CheckEq(viewB, 3L, "B 记着第 3 行");

            Feed(two, aLines, ref viewA);
            CheckEq(aLines.Count, 3, "A 追上了，拿到第三行");

            // --- 越界取行返回 null，视图可以据此跳过 ---
            CheckEq(log.LineAt(log.NextIndex), null, "取到 NextIndex 之外返回 null");
            CheckEq(log.LineAt(log.FirstIndex - 1), null, "取到 FirstIndex 之前返回 null");

            // --- 非法上限立刻炸 ---
            Check(ThrownBy(delegate { new CommLog(0); }).StartsWith("ArgumentOutOfRangeException", StringComparison.Ordinal),
                "上限为 0 抛 ArgumentOutOfRangeException");
        }
    }
}
