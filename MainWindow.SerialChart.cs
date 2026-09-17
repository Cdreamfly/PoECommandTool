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
using WpfApp1.Chart;
using WpfApp1.Serial;

namespace WpfApp1
{
    /// <summary>
    /// 「串口读写」页的采样、曲线与日志部分。
    ///
    /// 线程约定：读线程（SerialSession 的回调）只往队列里塞东西；所有对曲线缓冲、图例控件、
    /// 日志框的操作都发生在两个 DispatcherTimer 的 Tick 里，也就是 UI 线程上。
    /// </summary>
    public partial class MainWindow
    {
        private const int MaxLogLines = 1000;
        private const int MaxPendingLines = 400;
        private const int MaxPendingFrames = 400;

        /// <summary>因 UI 线程来不及消费而丢掉的帧数（读线程累加，UI 线程读）。</summary>
        private int _droppedFrames;

        private readonly object _pendingSync = new object();
        private readonly Queue<string> _pendingLines = new Queue<string>();
        private readonly Queue<FrameEvent> _pendingFrames = new Queue<FrameEvent>();

        /// <summary>通讯日志。只允许在 UI 线程上碰（见 <see cref="CommLog"/> 的线程约定）。</summary>
        private readonly CommLog _log = new CommLog(MaxLogLines);

        /// <summary>页内那个日志框已经显示到哪一行（绝对行号）。</summary>
        private long _logShownUpTo;

        private readonly List<string> _loggedOnce = new List<string>();
        private readonly List<SeriesBuffer> _legend = new List<SeriesBuffer>();
        private readonly Dictionary<string, SeriesBuffer> _seriesByKey = new Dictionary<string, SeriesBuffer>();
        private readonly Dictionary<SeriesBuffer, TextBlock> _legendLabels = new Dictionary<SeriesBuffer, TextBlock>();
        private readonly Dictionary<SeriesBuffer, CheckBox> _legendBoxes = new Dictionary<SeriesBuffer, CheckBox>();
        private readonly List<CheckBox> _legendGroupBoxes = new List<CheckBox>();
        private readonly HashSet<string> _collapsedGroups = new HashSet<string>();
        private bool _updatingLegend;

        /// <summary>本 tick 里有新曲线冒出来，图例需要在循环外统一重建一次（见 SampleFrame）。</summary>
        private bool _legendDirty;

        private DispatcherTimer _logTimer;
        private DispatcherTimer _chartTimer;
        private bool _hasNewSamples;
        private bool _chartMaximized;    // 曲线是否处于最大化
        private GridLength[] _savedRowHeights;

        /// <summary>最大化时收起的行：串口(0) / 发送(1) / 手动帧(2) / 分隔条(5) / 日志(6)。</summary>
        private static readonly int[] ChartMaximizeCollapsedRows = { 0, 1, 2, 5, 6 };

        private void MaximizeChart_Click(object sender, RoutedEventArgs e)
        {
            SetChartMaximized(!_chartMaximized);
        }

        /// <summary>
        /// 最大化 / 还原曲线区：收起「串口 / 发送 / 手动帧 / 分隔条 / 日志」，
        /// 但**保留轮询设置那一行** —— 开始、停止、改间隔在最常用的场景下仍然够得着，不用来回切。
        /// </summary>
        private void SetChartMaximized(bool maximize)
        {
            if (SerialTabGrid == null || _chartMaximized == maximize)
                return;

            _chartMaximized = maximize;

            if (maximize)
            {
                // 每次最大化都重新记录行高：用户拖过分隔条之后再最大化也能正确还原
                _savedRowHeights = new GridLength[SerialTabGrid.RowDefinitions.Count];
                for (int i = 0; i < _savedRowHeights.Length; i++)
                    _savedRowHeights[i] = SerialTabGrid.RowDefinitions[i].Height;
            }

            for (int i = 0; i < SerialTabGrid.RowDefinitions.Count; i++)
            {
                if (maximize && IsCollapsedWhenMaximized(i))
                    SerialTabGrid.RowDefinitions[i].Height = new GridLength(0);
                else if (_savedRowHeights != null && i < _savedRowHeights.Length)
                    SerialTabGrid.RowDefinitions[i].Height = _savedRowHeights[i];
            }

            // 行高归零还不够：把里面的东西也收起来，免得残留边框或文字
            foreach (UIElement child in SerialTabGrid.Children)
            {
                if (!IsCollapsedWhenMaximized(Grid.GetRow(child)))
                    continue;
                child.Visibility = maximize ? Visibility.Collapsed : Visibility.Visible;
            }

            MaximizeChartButton.Content = maximize ? "还原" : "最大化";
            Plot.InvalidateVisual();
            AppendLog(maximize ? "曲线已最大化（轮询的启停仍保留在这一页）。" : "已还原布局。");
        }

        private static bool IsCollapsedWhenMaximized(int row)
        {
            for (int i = 0; i < ChartMaximizeCollapsedRows.Length; i++)
            {
                if (ChartMaximizeCollapsedRows[i] == row)
                    return true;
            }
            return false;
        }

        // =================================================================
        //  图例
        // =================================================================

        /// <summary>
        /// 重建图例。按「参数」分组（功率 / 电流 / 电压 / 温度 …）：多端口轮询时同一个参数会有好几条
        /// （每端口一条），分到一组里就能一键全显/全隐，也方便折叠起来。
        /// </summary>
        private void BuildLegend()
        {
            LegendPanel.Children.Clear();
            _legendLabels.Clear();
            _legendBoxes.Clear();
            _legendGroupBoxes.Clear();

            if (_legend.Count == 0)
            {
                LegendPanel.Children.Add(new TextBlock
                {
                    Text = "（还没有数据）",
                    Foreground = Gray,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            var groupOrder = new List<string>();
            var groups = new Dictionary<string, List<SeriesBuffer>>();
            for (int i = 0; i < _legend.Count; i++)
            {
                SeriesBuffer buffer = _legend[i];
                string label = ParameterLabel(buffer);
                List<SeriesBuffer> bucket;
                if (!groups.TryGetValue(label, out bucket))
                {
                    bucket = new List<SeriesBuffer>();
                    groups[label] = bucket;
                    groupOrder.Add(label);
                }
                bucket.Add(buffer);
            }

            for (int g = 0; g < groupOrder.Count; g++)
            {
                string label = groupOrder[g];
                List<SeriesBuffer> buffers = groups[label];

                var children = new StackPanel
                {
                    Margin = new Thickness(20, 1, 0, 2),
                    Visibility = _collapsedGroups.Contains(label) ? Visibility.Collapsed : Visibility.Visible,
                    Tag = label,        // 折叠按钮据此记住/恢复分组状态
                };

                var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

                var collapseButton = new Button
                {
                    Content = _collapsedGroups.Contains(label) ? "▸" : "▾",
                    Width = 18,
                    Height = 18,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = children,
                    ToolTip = "折叠 / 展开这一组",
                };
                collapseButton.Click += LegendCollapse_Click;
                header.Children.Add(collapseButton);

                var groupBox = new CheckBox
                {
                    IsThreeState = true,          // 部分选中时显示半选
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 4, 0),
                    Tag = buffers,
                    ToolTip = "全显 / 全隐这一组（点一下切换）",
                };
                // 用 Click 而不是 Checked/Unchecked：程序改状态时不会误触发，也不会被三态循环绊住
                groupBox.Click += LegendGroup_Click;
                header.Children.Add(groupBox);
                _legendGroupBoxes.Add(groupBox);

                header.Children.Add(new TextBlock
                {
                    Text = string.Format("{0} · {1} 条", label, buffers.Count),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 180,
                });
                LegendPanel.Children.Add(header);

                for (int i = 0; i < buffers.Count; i++)
                    children.Children.Add(BuildLegendRow(buffers[i]));

                LegendPanel.Children.Add(children);
            }

            UpdateLegendGroups();
        }

        private StackPanel BuildLegendRow(SeriesBuffer buffer)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

            var checkBox = new CheckBox
            {
                IsChecked = buffer.Visible,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = buffer,
            };
            checkBox.Checked += LegendCheckBox_Toggled;
            checkBox.Unchecked += LegendCheckBox_Toggled;
            row.Children.Add(checkBox);
            _legendBoxes[buffer] = checkBox;

            row.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(4, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = BrushFrom(buffer.ColorHex),
                BorderBrush = Gray,
                BorderThickness = new Thickness(1),
            });

            var label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 156,
                Tag = buffer,
                Cursor = Cursors.Hand,
                ToolTip = "单击选中（图上加粗、再点一次取消）；双击查看详细统计（最大/最小/平均/峰峰值等）",
            };
            label.MouseLeftButtonUp += LegendRow_Click;
            row.Children.Add(label);
            _legendLabels[buffer] = label;
            return row;
        }

        private void LegendCollapse_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
                return;

            var children = button.Tag as StackPanel;
            if (children == null)
                return;

            bool expanding = children.Visibility != Visibility.Visible;
            children.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
            button.Content = expanding ? "▾" : "▸";

            // 记住折叠状态：新曲线出现时会重建图例，别把用户折叠过的又展开
            string label = children.Tag as string;
            if (label == null)
                return;
            if (expanding)
                _collapsedGroups.Remove(label);
            else
                _collapsedGroups.Add(label);
        }

        /// <summary>按组内可见情况刷新组标题的三态复选框。</summary>
        private void UpdateLegendGroups()
        {
            _updatingLegend = true;
            try
            {
                for (int i = 0; i < _legendGroupBoxes.Count; i++)
                {
                    CheckBox box = _legendGroupBoxes[i];
                    var buffers = box.Tag as List<SeriesBuffer>;
                    if (buffers == null || buffers.Count == 0)
                        continue;

                    int visible = 0;
                    for (int k = 0; k < buffers.Count; k++)
                        if (buffers[k].Visible)
                            visible++;

                    box.IsChecked = visible == 0
                        ? (bool?)false
                        : (visible == buffers.Count ? (bool?)true : null);
                }
            }
            finally
            {
                _updatingLegend = false;
            }
        }

        /// <summary>点组标题：组内有隐藏的就全显，全是显示的就全隐。</summary>
        private void LegendGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingLegend)
                return;

            var box = sender as CheckBox;
            var buffers = box == null ? null : box.Tag as List<SeriesBuffer>;
            if (buffers == null || buffers.Count == 0)
                return;

            bool anyHidden = false;
            for (int i = 0; i < buffers.Count; i++)
                if (!buffers[i].Visible)
                    anyHidden = true;

            bool target = anyHidden;
            _updatingLegend = true;
            try
            {
                for (int i = 0; i < buffers.Count; i++)
                {
                    buffers[i].Visible = target;
                    CheckBox childBox;
                    if (_legendBoxes.TryGetValue(buffers[i], out childBox))
                        childBox.IsChecked = target;
                }
            }
            finally
            {
                _updatingLegend = false;
            }

            UpdateLegendGroups();
            Plot.InvalidateVisual();
        }

        /// <summary>新建一条曲线缓冲，并分配颜色。</summary>
        private SeriesBuffer CreateSeries(SeriesCandidate candidate)
        {
            var buffer = new SeriesBuffer(candidate.Key, candidate.Name, candidate.Unit)
            {
                ColorHex = NextColorFor(candidate.Unit),
            };
            _seriesByKey[candidate.Key] = buffer;
            _legend.Add(buffer);
            return buffer;
        }

        /// <summary>
        /// 取「同一个子图里的下一种颜色」。按单位而不是按全局序号分配：
        /// 不同子图里的同色不会混淆（它们本来就分格里画），同一个子图里的曲线则保证各不相同。
        /// </summary>
        private string NextColorFor(string unit)
        {
            int sameUnit = 0;
            for (int i = 0; i < _legend.Count; i++)
                if (_legend[i].Unit == unit)
                    sameUnit++;
            return SeriesColor.For(sameUnit);
        }

        /// <summary>按「本次要轮询的命令 × 端口」预建曲线条目：这样一开始轮询，图例里就有对应条目
        /// （值先显示 --），而不是等第一条数据到了才一条条冒出来。
        /// </summary>
        private void EnsureSeriesFor(IList<SeriesCandidate> wanted)
        {
            if (wanted == null || wanted.Count == 0)
                return;

            bool added = false;
            for (int i = 0; i < wanted.Count; i++)
            {
                SeriesCandidate candidate = wanted[i];
                if (_seriesByKey.ContainsKey(candidate.Key))
                    continue;

                CreateSeries(candidate);
                added = true;
            }

            if (added)
            {
                BuildLegend();
                UpdateLegendValues();
            }
        }

        /// <summary>
        /// 把图例同步成「本次勾选」：预建缺失的条目；本次用不到的曲线取消勾选（数据保留，想看再勾回来）。
        /// 本次仍然要画的曲线保留用户自己的勾选状态，不会被强行打开。
        /// </summary>
        private void SyncSeriesWithSelection(IList<SeriesCandidate> wanted)
        {
            EnsureSeriesFor(wanted);

            var keys = new HashSet<string>();
            for (int i = 0; i < wanted.Count; i++)
                keys.Add(wanted[i].Key);

            bool changed = false;
            for (int i = 0; i < _legend.Count; i++)
            {
                SeriesBuffer buffer = _legend[i];
                if (keys.Contains(buffer.Key) || !buffer.Visible)
                    continue;

                buffer.Visible = false;
                changed = true;
            }

            if (changed)
                BuildLegend();
            Plot.InvalidateVisual();
        }

        private void LegendCheckBox_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingLegend)
                return;

            var checkBox = sender as CheckBox;
            if (checkBox == null)
                return;

            var buffer = checkBox.Tag as SeriesBuffer;
            if (buffer == null)
                return;

            buffer.Visible = checkBox.IsChecked == true;
            UpdateLegendGroups();
            Plot.InvalidateVisual();
        }

        /// <summary>单击图例项：选中/取消选中该曲线；双击：弹出详细统计。</summary>
        private void LegendRow_Click(object sender, MouseButtonEventArgs e)
        {
            var label = sender as TextBlock;
            var buffer = label == null ? null : label.Tag as SeriesBuffer;
            if (buffer == null)
                return;

            if (e.ClickCount >= 2)
            {
                ShowSeriesDetail(buffer);
                return;
            }

            Plot.SelectCurve(Plot.SelectedKey == buffer.Key ? null : buffer.Key);
            e.Handled = true;
        }

        /// <summary>双击图上的曲线：选中它并弹出详情。</summary>
        private void Plot_CurveActivated(string key)
        {
            SeriesBuffer buffer;
            if (key == null || !_seriesByKey.TryGetValue(key, out buffer))
                return;

            Plot.SelectCurve(key);
            ShowSeriesDetail(buffer);
        }

        /// <summary>选中变化：图例字重同步 + 刷新统计块（曲线本身由绘图元素加粗）。</summary>
        private void Plot_SelectionChanged(string key)
        {
            UpdateSelectionStats();
        }

        /// <summary>弹出某条曲线在「当前时间窗」内的详细统计。</summary>
        private void ShowSeriesDetail(SeriesBuffer buffer)
        {
            DateTime from;
            DateTime to;
            Plot.ResolveVisibleWindow(out from, out to);

            SeriesStatistics stats;
            string text = SeriesStats.TryCompute(buffer, from, to, out stats)
                ? SeriesStats.FormatDetail(buffer.Name, stats, buffer.Unit)
                : "当前时间窗内没有这条曲线的数据。"
                  + Environment.NewLine
                  + "当前时间窗：" + from.ToString("HH:mm:ss") + " ~ " + to.ToString("HH:mm:ss")
                  + "（数据可能已滑出窗口，或是刚预建、还没采到点）";

            var dialog = new SeriesDetailWindow { Owner = this };
            dialog.SetContent("曲线详情 - " + buffer.Name, text);
            dialog.ShowDialog();
        }

        /// <summary>刷新「选中曲线」的统计块；没选中时整块隐藏，不占地方。</summary>
        private void UpdateSelectionStats()
        {
            if (Plot == null || SelectionStatsPanel == null || SelectionStatsText == null)
                return;

            string key = Plot.SelectedKey;

            // 选中的那条在图例里也加粗，和图上对应起来
            for (int i = 0; i < _legend.Count; i++)
            {
                TextBlock rowLabel;
                if (!_legendLabels.TryGetValue(_legend[i], out rowLabel))
                    continue;

                rowLabel.FontWeight = key != null && _legend[i].Key == key
                    ? FontWeights.Bold
                    : FontWeights.Normal;
            }

            SeriesBuffer selected;
            if (key == null || !_seriesByKey.TryGetValue(key, out selected))
            {
                SelectionStatsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            DateTime from;
            DateTime to;
            Plot.ResolveVisibleWindow(out from, out to);

            SeriesStatistics stats;
            SelectionStatsPanel.Visibility = Visibility.Visible;
            SelectionStatsText.Text = SeriesStats.TryCompute(selected, from, to, out stats)
                ? selected.Name + Environment.NewLine + SeriesStats.FormatSummary(stats, selected.Unit)
                : selected.Name + Environment.NewLine + "（当前时间窗内没有数据）";
        }

        /// <summary>曲线名 "0x44 端口0 功率" → 分组用的 "功率"。</summary>
        private static string ParameterLabel(SeriesBuffer buffer)
        {
            return SeriesLabel.Parameter(buffer.Key, buffer.Name, buffer.Unit);
        }

        /// <summary>曲线 key "0x44 端口0.PowerW" → 来源 "0x44 端口0"。</summary>
        private static string SourceLabel(SeriesBuffer buffer)
        {
            return SeriesLabel.Source(buffer.Key);
        }

        // =================================================================
        //  曲线
        // =================================================================

        private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePlotWindow();
        }

        /// <summary>时间窗预设。索引与 <see cref="WindowLabels"/> 一一对应。</summary>
        private static readonly TimeSpan[] WindowOptions =
        {
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1),
        };

        private static readonly string[] WindowLabels =
        {
            "10 秒", "30 秒", "1 分钟", "5 分钟", "10 分钟", "30 分钟", "1 小时",
        };

        private void UpdatePlotWindow()
        {
            if (Plot == null || WindowCombo == null)
                return;

            int index = WindowCombo.SelectedIndex;
            if (index < 0 || index >= WindowOptions.Length)
                index = 2;      // 1 分钟

            Plot.Window = WindowOptions[index];
            Plot.InvalidateVisual();
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            Plot.ZoomValue(ChartPlotElement.ZoomStep);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            Plot.ZoomValue(1.0 / ChartPlotElement.ZoomStep);
        }

        private void ZoomAuto_Click(object sender, RoutedEventArgs e)
        {
            Plot.ResetValueZoom();
        }

        /// <summary>时间回看：把画面钉在「现在 − N 分钟」的位置，不再跟着最新数据滑。</summary>
        private void HistorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Plot == null)
                return;

            double minutes = HistorySlider.Value;
            Plot.TimeOffset = TimeSpan.FromMinutes(minutes);

            if (HistoryText != null)
                HistoryText.Text = minutes < 0.01 ? "现在" : "−" + minutes.ToString("0.#") + " 分";

            Plot.InvalidateVisual();
        }

        private void HistoryLive_Click(object sender, RoutedEventArgs e)
        {
            if (HistorySlider == null)
                return;

            HistorySlider.Value = 0;     // 拖回最左端即恢复跟随最新数据
        }

        /// <summary>纵轴缩放变化时更新倍数显示（滚轮也会触发）。</summary>
        private void Plot_ValueZoomChanged()
        {
            if (ZoomText == null || Plot == null)
                return;

            // 自动状态就不显示文字了——旁边那个「自动」按钮已经说明了，写两遍反而乱
            ZoomText.Text = Math.Abs(Plot.ValueZoom - 1.0) < 1e-9
                ? string.Empty
                : "×" + Plot.ValueZoom.ToString("0.##");
        }

        private void ClearChart_Click(object sender, RoutedEventArgs e)
        {
            _legend.Clear();
            _seriesByKey.Clear();
            // 一次性提示的去重集合也要清掉，否则「0x42 只有状态…」那类提示
            // 整个进程生命周期只出现一次——用户清空重来之后再也看不到它了。
            _loggedOnce.Clear();
            Plot.SelectCurve(null);      // 选中的那条也没了
            BuildLegend();
            _hasNewSamples = true;
            Plot.InvalidateVisual();
            AppendLog("已清空曲线。");
        }

        private void ChartTimer_Tick(object sender, EventArgs e)
        {
            if (!_hasNewSamples)
                return;

            _hasNewSamples = false;
            UpdateLegendValues();
            Plot.InvalidateVisual();
        }

        private void UpdateLegendValues()
        {
            for (int i = 0; i < _legend.Count; i++)
            {
                SeriesBuffer buffer = _legend[i];
                TextBlock label;
                if (!_legendLabels.TryGetValue(buffer, out label))
                    continue;

                SeriesSample last;
                string valueText = "--";
                if (buffer.TryGetLast(out last))
                {
                    // 量级大就换成更好读的单位（同图表上的处理）
                    double scale = 1.0;
                    string unit = buffer.Unit;
                    ChartMath.TryScaleUnit(buffer.Unit, last.Value, out scale, out unit);
                    valueText = (last.Value * scale).ToString("0.###") + " " + unit;
                }

                // 子项只写「来源：当前值」，参数名已经在组标题上
                label.Text = SourceLabel(buffer) + "：" + valueText;
            }

            UpdateSelectionStats();      // 选中曲线的统计跟着一起刷
        }

        // =================================================================
        //  读线程回调：只入队，不碰界面
        // =================================================================

        private void OnSerialLineReceived(string line)
        {
            AppendLine("[设备] " + line);
        }

        /// <summary>看着像响应、但识别规则没采纳：把原因和原始行都记下来，方便排查「识别不对」。</summary>
        private void OnSerialLineRejected(string line, string reason)
        {
            AppendLine("[识别] 未采纳：" + reason);
        }

        private void OnSerialFrameReceived(FrameEvent frame)
        {
            lock (_pendingSync)
            {
                if (_pendingFrames.Count >= MaxPendingFrames)
                {
                    // 队列满说明 UI 线程来不及消费（绘制卡住、或者设备刷得太快）。
                    // 这里丢帧**必须计数**：不做的话，界面上的「曲线有缺口」就分不清是
                    // 设备没回、还是工具自己丢了——两者的排查方向完全相反。
                    _pendingFrames.Dequeue();
                    _droppedFrames++;
                }
                _pendingFrames.Enqueue(frame);
            }
        }

        private void Enqueue(Queue<string> queue, string text, int limit)
        {
            lock (_pendingSync)
            {
                if (queue.Count >= limit)
                    queue.Dequeue();
                queue.Enqueue(text);
            }
        }

        /// <summary>写一行自带类别前缀的日志（[设备] / [发送] / [识别] …）；读线程也可以调。</summary>
        private void AppendLine(string text)
        {
            Enqueue(_pendingLines, text, MaxPendingLines);
        }

        private void OnSerialFault(SerialFault fault)
        {
            AppendLog("串口故障：" + fault.Message);

            // 读失败、写失败都说明链路已经不可用（会话也会把自己标记成关闭）：
            // 回到 UI 线程停轮询、改状态，免得一直重试刷屏。
            if (fault.Kind != SerialFaultKind.DeviceRemoved
                && fault.Kind != SerialFaultKind.ReadFailed
                && fault.Kind != SerialFaultKind.WriteFailed)
                return;

            if (Dispatcher.HasShutdownStarted)
                return;

            // 读线程上不能碰控件，回到 UI 线程处理
            Dispatcher.BeginInvoke(new Action(delegate
            {
                StopPolling("串口链路出错，轮询已停止。");
                UpdateSerialUi();
            }));
        }

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
