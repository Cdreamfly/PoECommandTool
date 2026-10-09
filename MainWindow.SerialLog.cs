using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PoECommandTool.Chart;
using PoECommandTool.Serial;

namespace PoECommandTool
{
    /// <summary>
    /// 「通讯日志」页签：日志存储的接线、按类别筛选、复制 / 存盘 / 清空，
    /// 以及串口页底部那个日志框的增量渲染。
    ///
    /// 从 MainWindow.SerialChart.cs 里拆出来的。线程约定不变：读线程只往队列里塞，
    /// 所有渲染都发生在 100ms 的日志定时器里，也就是 UI 线程上。
    /// </summary>
    public partial class MainWindow
    {
        // =================================================================
        //  日志与采样（UI 线程定时器）
        // =================================================================

        private void LogTimer_Tick(object sender, EventArgs e)
        {
            var lines = new List<string>();
            var frames = new List<FrameEvent>();

            lock (_pendingSync)
            {
                while (_pendingLines.Count > 0)
                    lines.Add(_pendingLines.Dequeue());
                while (_pendingFrames.Count > 0)
                    frames.Add(_pendingFrames.Dequeue());
            }

            for (int i = 0; i < frames.Count; i++)
                SampleFrame(frames[i]);

            // 本 tick 里冒出新曲线的话，图例在这里统一重建一次。
            // 放在循环外是必须的，理由见 SampleFrame 里那段说明。
            if (_legendDirty)
            {
                _legendDirty = false;
                BuildLegend();
            }

            for (int i = 0; i < lines.Count; i++)
                AppendLogCore(lines[i]);

            FlushLogText();
            SyncCommLogTab();
            UpdateDropStats();
        }

        /// <summary>
        /// 把三处「丢弃」计数显示出来。原先它们存在但**没有任何读者**，
        /// 于是同一个症状（「曲线有缺口」）有好几种成因，而诊断方向完全相反：
        ///
        /// * 帧队列溢出 → **工具**来不及处理（绘制卡住 / 设备刷太快），要减负载或加缓冲；
        /// * 裸帧丢字节 → 一直对不上 12 字节帧，多半是**链路**问题（波特率、接线）；
        /// * 行溢出     → 设备刷得太快，或某"行"长到不合理；
        /// * 自回显     → 设备把请求原样回了，这是**正常**的，不是故障。
        ///
        /// 全为零时不占地方。
        /// </summary>
        private void UpdateDropStats()
        {
            if (DropStatsText == null)
                return;

            int frames = _droppedFrames;
            int lines = _session == null ? 0 : _session.DroppedLines;
            int bytes = _session == null ? 0 : _session.DroppedBytes;
            int echo = _session == null ? 0 : _session.SelfEchoIgnored;

            if (frames == 0 && lines == 0 && bytes == 0 && echo == 0)
            {
                if (DropStatsText.Text.Length != 0)
                    DropStatsText.Text = string.Empty;
                return;
            }

            DropStatsText.Text = string.Format(CultureInfo.InvariantCulture,
                "丢弃：帧 {0} · 行 {1} · 字节 {2} · 自回显 {3}", frames, lines, bytes, echo);

            if (DropStatsText.ToolTip == null)
                DropStatsText.ToolTip =
                    "帧队列溢出：界面来不及处理（绘制卡住，或设备刷得太快）——是工具侧的负载问题。\n"
                    + "裸帧丢字节：一直对不上 12 字节帧的同步——多半是链路问题（波特率 / 接线）。\n"
                    + "行溢出：设备刷得太快，或某一行长到不合理。\n"
                    + "自回显：设备把请求原样回了，工具正确地丢掉了它——这是正常的，不是故障。";
        }

        // =================================================================
        //  日志页签
        // =================================================================

        /// <summary>「通讯日志」页签已经显示到哪一行（绝对行号）。与串口页那个框各记各的。</summary>
        private long _commLogTabShownUpTo;

        /// <summary>
        /// 日志增量追加到串口页底部那个框。真正的增量逻辑在 <see cref="RenderLogInto"/> 里，
        /// 通讯日志页签走同一套，只是各记各的行号（并且可以按类别筛选）。
        /// </summary>
        private void FlushLogText()
        {
            RenderLogInto(SerialLogBox, ref _logShownUpTo, true, null);
        }

        /// <summary>
        /// 把日志的增量渲染进某一个视图（串口页的框、通讯日志页签的框）。
        ///
        /// 平时只把新增的行 AppendText 上去，只有该视图落后到**已被丢弃**的部分之前、
        /// 或者日志被清空过，才整体重建。每来一行就把整串重新赋给 TextBox 会让轮询期间的
        /// 界面明显卡顿——满 1000 行之后旧写法正是每 100ms 重建一次全量。
        ///
        /// <paramref name="tagFilter"/> 为 null 表示不筛选。注意行号记的是"检查到哪一行"，
        /// 不是"显示了哪一行"——被筛掉的行同样推进行号，否则切换筛选时会把它们重复放进来。
        /// </summary>
        private void RenderLogInto(TextBox box, ref long shown, bool autoScroll, ICollection<string> tagFilter)
        {
            if (box == null)
                return;

            bool rebuild;
            long from;
            _log.GetView(shown, out rebuild, out from);

            if (rebuild)
            {
                string text = JoinLogLines(from, _log.NextIndex, tagFilter);
                if (box.Text != text)
                    box.Text = text;
            }
            else if (from < _log.NextIndex)
            {
                var builder = new StringBuilder();
                for (long i = from; i < _log.NextIndex; i++)
                {
                    string line = _log.LineAt(i);
                    if (line == null || !LogTag.ShouldShow(line, tagFilter))
                        continue;

                    builder.Append(Environment.NewLine).Append(line);
                }

                if (builder.Length > 0)
                    box.AppendText(builder.ToString());
            }
            else
            {
                return;     // 没有新行
            }

            shown = _log.NextIndex;

            if (autoScroll)
                box.ScrollToEnd();
        }

        private string JoinLogLines(long from, long to, ICollection<string> tagFilter)
        {
            var rows = new List<string>();
            for (long i = from; i < to; i++)
            {
                string line = _log.LineAt(i);
                if (line != null && LogTag.ShouldShow(line, tagFilter))
                    rows.Add(line);
            }

            return string.Join(Environment.NewLine, rows.ToArray());
        }

        // ---- 类别筛选与整份操作（#11 / #12）----

        /// <summary>被用户取消勾选的类别。空集合表示「全都看」，此时不筛选。</summary>
        private readonly HashSet<string> _disabledLogTags = new HashSet<string>();

        /// <summary>
        /// 按 <see cref="LogTag.All"/> 生成筛选复选框。
        ///
        /// 标签清单由纯逻辑层给出，界面不再抄一份——两处各写一份的话，
        /// 加一个新标签时界面会静默地筛不到它。
        /// </summary>
        private void BuildLogFilter()
        {
            LogFilterPanel.Children.Clear();

            for (int i = 0; i < LogTag.All.Length; i++)
            {
                string tag = LogTag.All[i];
                var box = new CheckBox
                {
                    Content = tag,
                    IsChecked = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0),
                };
                box.Checked += LogFilter_Changed;
                box.Unchecked += LogFilter_Changed;
                LogFilterPanel.Children.Add(box);
            }
        }

        /// <summary>当前要显示的类别；全都勾选时返回 null（表示不筛选，省掉每行的判断）。</summary>
        private ICollection<string> EnabledLogTags()
        {
            if (_disabledLogTags.Count == 0)
                return null;

            var enabled = new List<string>();
            for (int i = 0; i < LogTag.All.Length; i++)
                if (!_disabledLogTags.Contains(LogTag.All[i]))
                    enabled.Add(LogTag.All[i]);

            return enabled;
        }

        private void LogFilter_Changed(object sender, RoutedEventArgs e)
        {
            _disabledLogTags.Clear();
            foreach (UIElement child in LogFilterPanel.Children)
            {
                var box = child as CheckBox;
                if (box != null && box.IsChecked != true && box.Content != null)
                    _disabledLogTags.Add(box.Content.ToString());
            }

            // 行号归零 → GetView 会要求整体重建，筛选立刻生效
            _commLogTabShownUpTo = 0;
            SyncCommLogTab();
        }

        /// <summary>当前筛选下看得见的日志正文（复制与保存共用）。</summary>
        private string VisibleLogText()
        {
            return JoinLogLines(_log.FirstIndex, _log.NextIndex, EnabledLogTags());
        }

        private void LogCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(VisibleLogText());
                AppendLog("[工具] 日志已复制到剪贴板。");
            }
            catch (Exception ex)
            {
                // 剪贴板被别的进程占着时会抛，属于常见且无害的失败
                AppendLog("[工具] 复制日志失败（剪贴板可能被别的程序占用）：" + ex.Message);
            }
        }

        private void LogSave_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog();
            dialog.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            dialog.FileName = "commlog-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                // UTF-8 带 BOM：Windows 上的记事本 / Excel 才不会把中文认成乱码
                File.WriteAllText(dialog.FileName, VisibleLogText(), new UTF8Encoding(true));
                AppendLog("[工具] 日志已保存：" + dialog.FileName);
            }
            catch (Exception ex)
            {
                AppendLog("[工具] 保存日志失败：" + ex.Message);
            }
        }

        private void LogClear_Click(object sender, RoutedEventArgs e)
        {
            _log.Clear();

            // 「一次性提示」的去重集合也跟着清，否则清空之后那类提示再也不会出现
            _loggedOnce.Clear();

            // 两个视图的行号都归零 → 下次渲染走整体重建（重建一个空范围 == 清空显示）
            _logShownUpTo = 0;
            _commLogTabShownUpTo = 0;
            FlushLogText();
            SyncCommLogTab();

            AppendLog("日志已清空。");
            FlushLogText();
        }

        private void SyncCommLogTab()
        {
            // 页签没在前台就不渲染：视图自己记着行号，等切回来时 GetView 会一次补齐。
            // 也正因为这样，不需要额外的「切页签」事件——定时器每 100ms 就会补上。
            if (CommLogTabBox == null || CommLogTabItem == null || !CommLogTabItem.IsSelected)
                return;

            RenderLogInto(CommLogTabBox, ref _commLogTabShownUpTo,
                CommLogAutoScrollCheck.IsChecked == true, EnabledLogTags());
        }


        /// <summary>把一帧解析结果变成曲线采样。只允许在 UI 线程调用。</summary>
        private void SampleFrame(FrameEvent frame)
        {
            if (frame == null || !frame.IsParsed)
                return;

            int port = TelemetryExtractor.TryGetPort(frame.Parsed);
            string tag = string.Format("0x{0:X2}", frame.CommandId) + (port >= 0 ? " 端口" + port : string.Empty);

            // 把识别出来的内容打出来（解析器给的中文描述 + 带单位的数值）
            // 先把「识别出的是哪一帧」打出来，再打解析结果：
            // 数值不对时一眼能看出是识别错了还是解析错了
            AppendLogCore("[识别] " + Rtl8239CommandBuilder.ToHex(frame.Raw));

            string summary = ResponseSummarizer.Summarize(frame.Parsed, tag);
            if (summary != null)
                AppendLogCore("[解析] " + summary);

            // 事件类响应（0x46）没有数值曲线，但要标到时间轴上——必须放在下面那个
            // 「只有状态」的提前返回**之前**，否则刚好被它挡掉。
            RecordEventMarkers(frame.Parsed);

            IList<SeriesCandidate> candidates = TelemetryExtractor.Extract(frame.Parsed, tag);

            if (candidates.Count == 0 && !TelemetryExtractor.HasNumericSeries(string.Format("0x{0:X2}", frame.CommandId)))
            {
                // 例如 0x42：本来就没有测量量。提示一次，别让用户以为工具坏了。
                AppendLogOnce(string.Format(
                    "0x{0:X2} 只有状态，没有可绘制的数值量；电压/电流/功率请用 0x44 或 0x4F。",
                    frame.CommandId));
                return;
            }

            DateTime now = DateTime.Now;
            for (int i = 0; i < candidates.Count; i++)
            {
                SeriesCandidate candidate = candidates[i];
                SeriesBuffer buffer;
                if (!_seriesByKey.TryGetValue(candidate.Key, out buffer))
                {
                    buffer = CreateSeries(candidate);

                    // 只做个记号，**不在这里重建图例**：BuildLegend 是清空 + 逐行重建，
                    // 而它在逐候选循环里——48 端口 × 4 个参数逐个冒出来的话，
                    // 第 k 次重建要造 k 行，合计 1+2+…+192 ≈ 18,500 次行构造
                    //（约 55k 个控件、74k 次事件接线），表现为采样时一次多秒的界面冻结。
                    // 攒到本 tick 结束再重建一次即可。
                    _legendDirty = true;
                }

                if (buffer.Add(now, candidate.Value))
                    _hasNewSamples = true;
            }
        }

        /// <summary>时间轴上的事件标记。有上限，长时间跑不会无限涨。</summary>
        private readonly List<ChartMarker> _markers = new List<ChartMarker>();

        /// <summary>标记最多留这么多条：再多也看不清，还白占内存。</summary>
        private const int MaxMarkers = 400;

        /// <summary>
        /// 把事件类响应标到时间轴上。
        ///
        /// 0x46 早就解析出来了，但**从来没画到图上**——端口断开、故障这些在日志里一闪而过，
        /// 事后对不上曲线上的拐点。标上去才能一眼看出「这条曲线在这里掉下去，
        /// 是因为那个端口出事了」。
        /// </summary>
        private void RecordEventMarkers(object parsed)
        {
            List<ChartMarker> markers = EventMarkers.From(parsed, DateTime.Now);
            if (markers.Count == 0)
                return;

            for (int i = 0; i < markers.Count; i++)
                _markers.Add(markers[i]);

            while (_markers.Count > MaxMarkers)
                _markers.RemoveAt(0);

            if (Plot != null)
                Plot.Markers = _markers;

            for (int i = 0; i < markers.Count; i++)
                AppendLogCore("[事件] " + markers[i].Label
                    + " @ " + markers[i].Time.ToString("HH:mm:ss.fff"));
        }

        private void AppendLogOnce(string text)
        {
            if (_loggedOnce.Contains(text))
                return;

            _loggedOnce.Add(text);
            AppendLogCore("[工具] " + text);
        }

        /// <summary>工具自己的消息（开关串口、故障、轮询提示…）统一带 [工具] 前缀。</summary>
        private void AppendLog(string text)
        {
            AppendLine("[工具] " + text);
        }

        private void AppendLogCore(string text)
        {
            // 时间戳是呈现层的事，不进 CommLog——那样纯逻辑层才能在 Linux 上按行断言
            _log.Append(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text);
        }
    }
}
