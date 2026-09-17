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
            // 行为变更（清单 12）：没显示过的视图一律整体重建，**哪怕日志是空的**。
            // 这正是「清空」能生效的根据——重建一个空范围就等于把视图清空。
            // 调用方对空内容做一次赋值是无害的（RenderLogInto 里还比了一次，避免重复赋值）。
            Check(rebuild, "没显示过的视图一律整体重建");
            CheckEq(from, 0L, "从 FirstIndex 开始重建");

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
        private static void LogTagTests()
        {
            Console.WriteLine("LogTag 类别标签");

            CheckEq(LogTag.Extract("14:23:05.123  [设备] 收到一行"), "设备", "取出开头的类别");
            CheckEq(LogTag.Extract("14:23:05.123  [工具] 设备信息 0x40（设备身份）：..."), "工具",
                "行内后面还有括号也不影响——只认第一个方括号");
            CheckEq(LogTag.Extract("没有任何括号的一行"), null, "没有类别返回 null");
            CheckEq(LogTag.Extract("[未闭合"), null, "括号不闭合返回 null");
            CheckEq(LogTag.Extract(null), null, "null 不炸");

            // 全部显示
            Check(LogTag.ShouldShow("x [设备] y", null), "筛选集合为 null → 全显示");
            Check(LogTag.ShouldShow("x [设备] y", new List<string>()), "空集合 → 全显示");

            var onlyTools = new List<string> { "工具" };
            Check(LogTag.ShouldShow("x [工具] y", onlyTools), "命中的类别显示");
            Check(!LogTag.ShouldShow("x [设备] y", onlyTools), "没命中的类别隐藏");
            Check(LogTag.ShouldShow("没有类别的一行", onlyTools),
                "没有类别的行在筛选时仍然显示（它不属于任何一类，筛掉只会让日志变得莫名其妙）");

            CheckEq(LogTag.All.Length, 5, "五个类别");
            CheckEq(LogTag.All[0], "设备", "顺序：设备在最前");
            Check(LogTag.All[4] == "工具", "工具在最后");

            CheckEq(LogTag.Describe("x [发送] y"), "发送", "Describe 给出类别");
            CheckEq(LogTag.Describe("没有类别"), "(无类别)", "Describe 对无类别给出说明");
        }

        private static void CommLogClearTests()
        {
            Console.WriteLine("CommLog 清空");

            var log = new CommLog(100);
            long shown = 0;
            var view = new List<string>();

            log.Append("a");
            log.Append("b");
            Feed(log, view, ref shown);
            CheckEq(view.Count, 2, "先看到两行");

            log.Clear();

            // 清空后行号继续往前推，视图因此发现自己落后了 → 整体重建（内容为空）
            CheckEq(log.Count, 0, "清空后没有内容");
            CheckEq(log.NextIndex, 3L, "行号越过末尾继续推进（不归零，也不持平）");
            CheckEq(log.FirstIndex, 3L, "FirstIndex 与 NextIndex 一起走");

            Feed(log, view, ref shown);
            CheckEq(view.Count, 0, "视图被清空");

            log.Append("c");
            Feed(log, view, ref shown);
            CheckEq(view.Count, 1, "清空之后照常追加");
            CheckEq(view[0], "c", "新行接在清空时的行号之后继续编号");
        }
    }
}
