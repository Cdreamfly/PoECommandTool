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
    /// 「串口读写」页的采样与曲线绘制。
    ///
    /// 线程约定：读线程（SerialSession 的回调）只往队列里塞东西；所有对曲线缓冲、
    /// 图例控件、日志框的操作都发生在两个 DispatcherTimer 的 Tick 里，也就是 UI 线程上。
    ///
    /// 图例与曲线管理拆到了 MainWindow.Legend.cs，日志拆到了 MainWindow.SerialLog.cs。
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
            Plot.InvalidateLayout();
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
            Plot.InvalidateLayout();
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            Plot.ZoomValue(ChartPlotElement.ZoomStep);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            Plot.ZoomValue(1.0 / ChartPlotElement.ZoomStep);
        }

        /// <summary>「自动」= 纵横两个缩放都恢复默认。</summary>
        private void ZoomAuto_Click(object sender, RoutedEventArgs e)
        {
            // 纵横两个缩放一起恢复：按钮写着「自动」，只回其中一个会让人以为另一个坏了
            Plot.ResetValueZoom();
            Plot.ResetTimeZoom();
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

            Plot.InvalidateLayout();
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

            // 自动状态就不显示文字了——旁边那个「自动」按钮已经说明了，写两遍反而乱。
            // 两个缩放各自标注，免得看到一个「×2」不知道说的是哪一根轴。
            var parts = new List<string>();
            if (Math.Abs(Plot.ValueZoom - 1.0) > 1e-9)
                parts.Add("纵轴 ×" + Plot.ValueZoom.ToString("0.##"));
            if (Math.Abs(Plot.TimeZoom - 1.0) > 1e-9)
                parts.Add("时间轴 ×" + Plot.TimeZoom.ToString("0.##"));

            ZoomText.Text = parts.Count == 0 ? string.Empty : string.Join("　", parts.ToArray());
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
            Plot.InvalidateLayout();
            AppendLog("已清空曲线。");
        }

        /// <summary>
        /// 暂停刷新曲线。**采样不停**——数据照常进缓冲，只是画面不动，
        /// 否则"暂停"会把暂停期间的数据一起丢掉，那是个陷阱不是暂停。
        /// </summary>
        private bool _chartPaused;

        private void PauseChart_Click(object sender, RoutedEventArgs e)
        {
            _chartPaused = !_chartPaused;

            if (PauseChartButton != null)
                PauseChartButton.Content = _chartPaused ? "继续" : "暂停";

            if (_chartPaused)
            {
                AppendLog("曲线已暂停刷新（采样继续，数据仍在记录）。");
                return;
            }

            // 继续时立刻把积压的数据画出来，不用等下一个 tick
            _hasNewSamples = true;
            AppendLog("曲线已继续刷新。");
        }

        private void ChartTimer_Tick(object sender, EventArgs e)
        {
            if (!_hasNewSamples)
                return;

            _hasNewSamples = false;

            // 暂停期间把「有新样本」的标记照常消化掉，但不重画：数据已经在缓冲里，
            // 继续时一次全画出来。图例上的数值也一并冻住，否则会出现
            // 「数字在跳、曲线不动」的怪状态。
            if (_chartPaused)
                return;

            UpdateLegendValues();
            Plot.InvalidateLayout();
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

                // 意外断开（拔线 / 链路出错）：如果这个端口再回来，自动重连
                _reconnectWanted = true;
            }));
        }
    }
}
