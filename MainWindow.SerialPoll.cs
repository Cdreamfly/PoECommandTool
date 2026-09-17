using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfApp1.Chart;
using WpfApp1.Serial;

namespace WpfApp1
{
    /// <summary>
    /// 「串口读写」页的轮询部分：可轮询命令的勾选列表、启停、状态显示。
    /// 调度与配对逻辑在 PollingScheduler / PollingRunner / SerialSession 里，这里只负责接线。
    /// </summary>
    public partial class MainWindow
    {
        private readonly List<PollItem> _pollItems = new List<PollItem>();
        private readonly List<CheckBox> _pollCheckBoxes = new List<CheckBox>();
        private readonly List<TextBox> _pollPortBoxes = new List<TextBox>();

        private CancellationTokenSource _pollCts;
        private Task _pollTask;

        /// <summary>轮询是否正在进行。与 <see cref="SetPollingUiState"/> 同步维护。</summary>
        private bool _pollingActive;
        private PollingRunner _runner;
        private int _pollsSent;
        private int _pollsOk;

        // =================================================================
        //  轮询命令列表
        // =================================================================

        private void BuildPollList()
        {
            PollListPanel.Children.Clear();
            _pollItems.Clear();
            _pollCheckBoxes.Clear();
            _pollPortBoxes.Clear();

            List<PollItem> candidates = PollPlan.BuildCandidates();
            for (int i = 0; i < candidates.Count; i++)
            {
                PollItem item = candidates[i];
                _pollItems.Add(item);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                var checkBox = new CheckBox
                {
                    IsChecked = item.Enabled,
                    Content = item.DisplayName,
                    Width = 186,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(checkBox);

                if (PollPlan.HasPortField(item.Command))
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = "端口",
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0, 0, 0),
                    });
                    var portBox = new TextBox
                    {
                        Text = "0",
                        Width = 96,
                        FontFamily = Mono,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        ToolTip = "可以填多个端口，会展开成多条轮询：0,1,2  或  0-3  或  0x00-0x03  或混用 0,2-4,7",
                    };
                    row.Children.Add(portBox);
                    _pollPortBoxes.Add(portBox);
                }
                else
                {
                    _pollPortBoxes.Add(null);
                }

                _pollCheckBoxes.Add(checkBox);
                PollListPanel.Children.Add(row);
            }

            // 勾选变化时预建对应曲线：这样「勾了什么就准备画什么」，不用等数据到了才知道
            for (int i = 0; i < _pollCheckBoxes.Count; i++)
            {
                _pollCheckBoxes[i].Checked += PollItem_Toggled;
                _pollCheckBoxes[i].Unchecked += PollItem_Toggled;
            }

            // 需要额外参数的查询命令：列出来但置灰，免得用户以为工具漏了它们。
            // 已被「设备信息」面板接管的（0x47 / 0x4C 等）跳过——它们在那边有正经入口，
            // 这里再挂一行「暂不支持」会自相矛盾。
            List<CommandDef> all = Rtl8239Catalog.All;
            for (int i = 0; i < all.Count; i++)
            {
                CommandDef cmd = all[i];
                if (cmd.Category != Rtl8239Catalog.CatQuery || PollPlan.IsPollable(cmd))
                    continue;
                if (CommandOwnership.OwnedByDeviceInfoPanel(cmd.Key))
                    continue;

                var disabled = new CheckBox
                {
                    IsEnabled = false,
                    Content = cmd.Key + "  " + cmd.Name + "（需额外参数，暂不支持轮询）",
                    Foreground = Gray,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                PollListPanel.Children.Add(disabled);
            }
        }

        // =================================================================
        //  轮询启停
        // =================================================================

        private void StartPoll_Click(object sender, RoutedEventArgs e)
        {
            StartPolling();
        }

        private async void StartPolling()
        {
            try
            {
                if (!_session.IsOpen)
                {
                    AppendLog("请先打开串口。");
                    return;
                }

                string problem;
                PollingPlan plan = CollectPlan(out problem);
                if (plan == null)
                {
                    AppendLog(problem);
                    return;
                }

                _serialOptions.Mode = IsRawMode() ? SendMode.RawFrame : SendMode.TextCommand;
                _serialOptions.Template = TemplateBox.Text;
                _serialOptions.LineEnding = SelectedLineEnding();

                // 图例跟着本次勾选走：预建条目，并把这次不画的曲线先取消勾选（数据留着）
                SyncSeriesWithSelection(CollectWantedSeries());

                _pollsSent = 0;
                _pollsOk = 0;
                _hasNewSamples = true;

                _pollCts = new CancellationTokenSource();
                _runner = new PollingRunner(_session);
                _runner.RequestSent += OnPollRequestSent;
                _runner.ResponseMatched += OnPollResponseMatched;
                _runner.RequestFailed += OnPollRequestFailed;
                _runner.Notice += OnPollNotice;

                SetPollingUiState(true);
                // 把「一轮实际要多久」摆出来。间隔设置只是目标值：一轮要走完所有勾选的命令，
                // 每条之间还要留命令间隔，所以条目一多，实际轮次就由命令间隔说了算，
                // 那个「间隔(ms)」会静默失效。不说清楚的话，用户会把曲线的斜率误读成物理速率。
                long roundMs = (long)CountEnabled(plan) * plan.InterCommandDelayMs;
                string ceiling = roundMs > plan.IntervalMs
                    ? string.Format("；一轮至少 {0:F1} 秒（{1} 条 × 命令间隔 {2} ms），"
                        + "已超过间隔设置，此时间隔不生效",
                        roundMs / 1000.0, CountEnabled(plan), plan.InterCommandDelayMs)
                    : string.Empty;

                AppendLog(string.Format("开始轮询：{0} 条命令，轮次间隔 {1} ms，命令间隔 {2} ms，超时 {3} ms，重试 {4} 次{5}。",
                    CountEnabled(plan), plan.IntervalMs, plan.InterCommandDelayMs,
                    plan.ResponseTimeoutMs, plan.MaxRetries, ceiling));

                _pollTask = _runner.RunAsync(plan, _serialOptions, _pollCts.Token);
                await _pollTask;
            }
            catch (OperationCanceledException)
            {
                // 用户停止，正常路径
            }
            catch (Exception ex)
            {
                AppendLog("轮询结束：" + ex.Message);
            }
            finally
            {
                SetPollingUiState(false);
                _pollTask = null;
                if (_pollCts != null)
                {
                    _pollCts.Dispose();
                    _pollCts = null;
                }
            }
        }

        private void StopPoll_Click(object sender, RoutedEventArgs e)
        {
            StopPolling("已手动停止轮询。");
        }

        private void StopPolling(string reason)
        {
            if (_pollCts == null)
                return;

            try
            {
                _pollCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!string.IsNullOrEmpty(reason))
                AppendLog(reason);
        }

        private PollingPlan CollectPlan(out string problem)
        {
            problem = null;
            var plan = new PollingPlan();
            plan.IntervalMs = (int)ParseLong(IntervalBox.Text, 1000, 50, 600000);
            plan.ResponseTimeoutMs = (int)ParseLong(TimeoutBox.Text, 1500, 20, 60000);
            plan.MaxRetries = (int)ParseLong(RetryBox.Text, 2, 0, 20);
            plan.InterCommandDelayMs = (int)ParseLong(GapBox.Text, 500, 0, 60000);

            int enabledCount = 0;
            for (int i = 0; i < _pollItems.Count; i++)
            {
                PollItem item = _pollItems[i];
                bool on = _pollCheckBoxes[i].IsChecked == true;
                item.Enabled = on;
                if (!on)
                    continue;

                TextBox portBox = _pollPortBoxes[i];
                if (portBox == null)
                {
                    plan.Items.Add(item);       // 不带端口字段的命令（如 0x41）
                    enabledCount++;
                    continue;
                }

                // 一条命令可以填多个端口：展开成多条轮询项，调度器那边就不用改了
                byte[] ports;
                string portError;
                if (!PortListParser.TryParse(portBox.Text, out ports, out portError))
                {
                    problem = item.DisplayName + "：" + portError;
                    return null;
                }

                for (int p = 0; p < ports.Length; p++)
                {
                    plan.Items.Add(new PollItem { Command = item.Command, Port = ports[p], Enabled = true });
                    enabledCount++;
                }
            }

            if (enabledCount == 0)
            {
                problem = "请至少勾选一条要轮询的命令。";
                return null;
            }
            return plan;
        }

        private static int CountEnabled(PollingPlan plan)
        {
            int count = 0;
            for (int i = 0; i < plan.Items.Count; i++)
                if (plan.Items[i].Enabled)
                    count++;
            return count;
        }

        /// <summary>
        /// 把界面上勾选的命令 + 端口换算成「本次会产生哪些曲线」，用于预先建好图例条目。
        /// 端口没填对就先跳过（开始轮询时会统一校验并报错）。
        /// </summary>
        private List<SeriesCandidate> CollectWantedSeries()
        {
            var wanted = new List<SeriesCandidate>();
            if (_pollItems == null || _pollCheckBoxes == null)
                return wanted;

            for (int i = 0; i < _pollItems.Count && i < _pollCheckBoxes.Count; i++)
            {
                if (_pollCheckBoxes[i].IsChecked != true)
                    continue;

                PollItem item = _pollItems[i];
                TextBox portBox = i < _pollPortBoxes.Count ? _pollPortBoxes[i] : null;
                if (portBox == null)
                {
                    AddWantedSeries(wanted, item, -1);
                    continue;
                }

                byte[] ports;
                string error;
                if (!PortListParser.TryParse(portBox.Text, out ports, out error))
                    continue;

                for (int p = 0; p < ports.Length; p++)
                    AddWantedSeries(wanted, item, ports[p]);
            }
            return wanted;
        }

        private static void AddWantedSeries(List<SeriesCandidate> wanted, PollItem item, int port)
        {
            byte commandId = PollPlan.CommandIdOf(item.Command);
            if (commandId == 0)
                return;

            string tag = string.Format("0x{0:X2}", commandId)
                + (port >= 0 ? " 端口" + port : string.Empty);

            IList<SeriesCandidate> described = TelemetryExtractor.DescribeSeries(commandId, tag);
            for (int i = 0; i < described.Count; i++)
                wanted.Add(described[i]);
        }

        private void PollItem_Toggled(object sender, RoutedEventArgs e)
        {
            EnsureSeriesFor(CollectWantedSeries());
        }

        private void SetPollingUiState(bool polling)
        {
            _pollingActive = polling;
            UpdatePollStartButton();
            StopPollButton.IsEnabled = polling;
            PollStatusText.Text = polling ? "轮询中…" : string.Empty;
            PollStatusText.Foreground = polling ? Brushes.Green : Gray;

            // 让「设备信息」的「更新」跟着轮询一起禁用：两边共用一把事务锁，
            // 一次更新要连着读六条命令，插进轮询里会把串口占住十几秒甚至更久。
            UpdateDeviceInfoUi();
        }

        /// <summary>
        /// 「开始轮询」的可用性。互锁必须是**双向**的：只挡住「轮询时点更新」还不够，
        /// 反过来的「读取/发送时点开始轮询」会让轮询一上来就卡在事务锁上，
        /// 界面停在「轮询中… 已发 0 / 成功 0」，既不报错也看不出在等什么。
        /// </summary>
        private void UpdatePollStartButton()
        {
            if (StartPollButton == null)
                return;

            StartPollButton.IsEnabled = !_pollingActive && !_deviceInfo.IsBusy && !_sendParse.IsBusy;
        }

        private void OnPollRequestSent(PollRequest request)
        {
            _pollsSent++;
            UpdatePollStatus();

            // 只记第一条：轮询期间每条都记会刷屏，但第一条能让人一眼确认命令格式对不对
            if (_pollsSent == 1)
                AppendLine("[发送] " + DescribeOutgoing(request.Frame));
        }

        private void OnPollResponseMatched(PollRequest request, FrameEvent frame)
        {
            _pollsOk++;
            UpdatePollStatus();

            if (!frame.IsParsed)
                AppendLog(string.Format("{0}（第 {1} 次尝试）的响应解析失败：{2}",
                    DescribeRequest(request), request.Attempt, frame.ParseError));
        }

        private void OnPollRequestFailed(PollRequest request, string reason)
        {
            AppendLog(string.Format("{0}（第 {1} 次尝试）失败：{2}",
                DescribeRequest(request), request.Attempt, reason));
        }

        /// <summary>形如 "0x44 端口3"，用于日志里区分是哪一条在发。</summary>
        private static string DescribeRequest(PollRequest request)
        {
            string text = string.Format("0x{0:X2}", request.CommandId);
            if (request.Item != null && PollPlan.HasPortField(request.Item.Command))
                text += " 端口" + request.Item.Port;
            return text;
        }

        private void OnPollNotice(string notice)
        {
            AppendLog(notice);
        }

        private void UpdatePollStatus()
        {
            PollStatusText.Text = string.Format("轮询中… 已发 {0} / 成功 {1}", _pollsSent, _pollsOk);
        }
    }
}
