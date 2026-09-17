using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WpfApp1.Chart;
using WpfApp1.Serial;

namespace WpfApp1
{
    /// <summary>
    /// 「串口读写」页的串口配置与发送部分。
    ///
    /// 这里只做「接线 + 封送到 UI 线程」——协议、调度、采样、绘图计算都在 Serial/ 与 Chart/
    /// 的纯 C# 类里（那些能在 Linux 上编译并断言）。轮询见 MainWindow.SerialPoll.cs，
    /// 采样/曲线/日志见 MainWindow.SerialChart.cs。
    /// </summary>
    public partial class MainWindow
    {
        private SerialSession _session;
        private SendOptions _serialOptions;
        private SerialPortSettings _portSettings;
        private bool _closing;

        // =================================================================
        //  初始化
        // =================================================================

        private void InitializeSerialTab()
        {
            _serialOptions = new SendOptions();
            _portSettings = new SerialPortSettings();
            _session = new SerialSession(new SystemSerialTransport());
            _session.Fault += OnSerialFault;
            _session.LineReceived += OnSerialLineReceived;
            _session.LineRejected += OnSerialLineRejected;
            _session.FrameReceived += OnSerialFrameReceived;

            BuildSerialOptionLists();
            BuildPollList();
            BuildLegend();
            BuildLogFilter();
            InitializeDeviceInfo();

            TemplateBox.Text = _serialOptions.Template;
            PatternBox.Text = _session.Extractor.Pattern;
            EndingCombo.SelectedIndex = 3;      // CRLF
            ManualFrameBox.Text = Rtl8239CommandBuilder.ToHex(
                Rtl8239CommandBuilder.PortMeasurementGet(0x01, 0x00));
            Plot.Series = _legend;
            Plot.ValueZoomChanged += Plot_ValueZoomChanged;
            Plot.SelectionChanged += Plot_SelectionChanged;
            Plot.CurveActivated += Plot_CurveActivated;
            UpdatePlotWindow();
            Plot_ValueZoomChanged();
            HistoryText.Text = "现在";

            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _logTimer.Tick += LogTimer_Tick;
            _logTimer.Start();

            _chartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _chartTimer.Tick += ChartTimer_Tick;
            _chartTimer.Start();

            StartPortWatcher();
            Closing += SerialTab_Closing;
            UpdateSerialUi();

            AppendLog(AppVersion.Title + " 已启动（版本号见标题栏与「帮助 → 关于」）。");
        }

        private void BuildSerialOptionLists()
        {
            RefreshPortList();
            _knownPorts = SafePortNames();

            var baudRates = new[] { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };
            BaudCombo.ItemsSource = baudRates;
            BaudCombo.Text = "115200";

            DataBitsCombo.ItemsSource = new[] { 5, 6, 7, 8 };
            DataBitsCombo.SelectedIndex = 3;

            ParityCombo.ItemsSource = new[] { "None", "Odd", "Even", "Mark", "Space" };
            ParityCombo.SelectedIndex = 0;

            StopBitsCombo.ItemsSource = new[] { "1", "1.5", "2" };
            StopBitsCombo.SelectedIndex = 0;

            EndingCombo.ItemsSource = new[] { "无", "CR", "LF", "CRLF" };

            ByteOrderCombo.ItemsSource = ByteOrderItems;
            ByteOrderCombo.SelectedIndex = 0;
            RespByteOrderCombo.ItemsSource = ByteOrderItems;     // 「响应解析」页那一份
            RespByteOrderCombo.SelectedIndex = 0;

            WindowCombo.ItemsSource = WindowLabels;
            WindowCombo.SelectedIndex = 2;      // 1 分钟
        }

        private void RefreshPortList()
        {
            string[] ports;
            try
            {
                ports = new SystemSerialTransport().GetPortNames();
            }
            catch (Exception)
            {
                ports = new string[0];
            }

            string current = PortCombo.SelectedItem as string;
            PortCombo.ItemsSource = ports;
            if (ports.Length == 0)
                return;

            PortCombo.SelectedIndex = 0;
            if (current == null)
                return;

            for (int i = 0; i < ports.Length; i++)
            {
                if (ports[i] == current)
                {
                    PortCombo.SelectedIndex = i;
                    break;
                }
            }
        }

        // =================================================================
        //  串口开关
        // =================================================================

        private void RefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RefreshPortList();
                AppendLog("已刷新串口列表，共 " + PortCombo.Items.Count + " 个。");
            }
            catch (Exception ex)
            {
                AppendLog("刷新串口列表失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 打开串口（按界面上的设置）。抽出来是为了让「自动重连」走同一条路径——
        /// 重连要是自己另写一遍开串口流程，早晚会和这里的差异越拉越大。
        /// </summary>
        private async Task OpenSerialAsync()
        {
            _portSettings = ReadPortSettings();
            ReadReceiveSettings();
            _session.Open(_portSettings);

            _lastOpenedPort = _portSettings.PortName;
            _reconnectWanted = false;

            AppendLog("已打开 " + _portSettings.Describe());
            UpdateSerialUi();

            // 刚连上先读一次设备信息：这不是轮询，只有一次往返，但能立刻暴露
            // 「设备不认这些命令」这类问题，省得用户以为是按钮坏了。
            await StartDeviceInfoRead();
        }

        // =================================================================
        //  串口热插拔与自动重连
        // =================================================================

        /// <summary>上一次见到的串口列表，用来发现插拔。</summary>
        private string[] _knownPorts = new string[0];

        /// <summary>上一次成功打开的端口名；自动重连要认准同一个。</summary>
        private string _lastOpenedPort;

        /// <summary>链路意外断了，等着同一个端口重新出现。</summary>
        private bool _reconnectWanted;

        private DispatcherTimer _portWatchTimer;

        private void StartPortWatcher()
        {
            _portWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _portWatchTimer.Tick += PortWatch_Tick;
            _portWatchTimer.Start();
        }

        /// <summary>
        /// 每两秒看一次串口列表。
        ///
        /// 原先端口列表只在启动时枚举一次，之后要靠手点「刷新」——启动之后才插上的
        /// USB 转串口根本不会出现。拔线之后的恢复也要手动两步（刷新 + 打开）。
        /// </summary>
        private async void PortWatch_Tick(object sender, EventArgs e)
        {
            try
            {
                string[] ports = SafePortNames();
                if (!SamePorts(ports, _knownPorts))
                {
                    string[] added = Difference(ports, _knownPorts);
                    string[] removed = Difference(_knownPorts, ports);
                    _knownPorts = ports;

                    RefreshPortList();      // 会尽量保住当前选择
                    AppendLog("串口列表有变化：" + DescribePortDiff(added, removed));
                }

                await TryReconnectAsync(ports);
            }
            catch (Exception ex)
            {
                // 定时器的回调里不能往外抛
                AppendLog("检测串口列表时出错：" + ex.Message);
            }
        }

        private async Task TryReconnectAsync(string[] ports)
        {
            if (!_reconnectWanted || _session == null || _session.IsOpen)
                return;

            if (AutoReconnectCheck == null || AutoReconnectCheck.IsChecked != true)
            {
                _reconnectWanted = false;
                return;
            }

            if (_lastOpenedPort == null || Array.IndexOf(ports, _lastOpenedPort) < 0)
                return;     // 还没回来，继续等

            _reconnectWanted = false;
            AppendLog(string.Format("检测到 {0} 重新出现，正在自动重连…", _lastOpenedPort));

            try
            {
                SelectPort(_lastOpenedPort);
                await OpenSerialAsync();
            }
            catch (Exception ex)
            {
                AppendLog("自动重连失败：" + ex.Message + "（设备可能还没就绪，可手点「打开串口」重试）");
            }
        }

        private void SelectPort(string portName)
        {
            if (PortCombo == null || portName == null)
                return;

            for (int i = 0; i < PortCombo.Items.Count; i++)
            {
                if (string.Equals(PortCombo.Items[i] as string, portName, StringComparison.OrdinalIgnoreCase))
                {
                    PortCombo.SelectedIndex = i;
                    return;
                }
            }

            PortCombo.Text = portName;
        }

        private static string[] SafePortNames()
        {
            try
            {
                return new SystemSerialTransport().GetPortNames();
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        private static bool SamePorts(string[] left, string[] right)
        {
            if (left == null || right == null)
                return false;
            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < right.Length && !found; j++)
                    found = string.Equals(left[i], right[j], StringComparison.OrdinalIgnoreCase);
                if (!found)
                    return false;
            }

            return true;
        }

        private static string[] Difference(string[] from, string[] minus)
        {
            var result = new List<string>();
            if (from == null)
                return result.ToArray();

            for (int i = 0; i < from.Length; i++)
            {
                bool present = false;
                if (minus != null)
                    for (int j = 0; j < minus.Length && !present; j++)
                        present = string.Equals(from[i], minus[j], StringComparison.OrdinalIgnoreCase);

                if (!present)
                    result.Add(from[i]);
            }

            return result.ToArray();
        }

        private static string DescribePortDiff(string[] added, string[] removed)
        {
            var parts = new List<string>();
            if (added != null && added.Length > 0)
                parts.Add("新增 " + string.Join("、", added));
            if (removed != null && removed.Length > 0)
                parts.Add("移除 " + string.Join("、", removed));

            return parts.Count == 0 ? "（无）" : string.Join("；", parts.ToArray());
        }

        private async void OpenClose_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_session.IsOpen)
                {
                    // ① 先同步发取消：不等它，但必须发在关串口之前——否则读取/发送会在
                    //    已经关掉的端口上一直等到超时。
                    CancelDeviceInfo();
                    CancelCommandSend();
                    CancelDownload();
                    _reconnectWanted = false;   // 是用户主动关的，别自动连回来

                    // ② 状态同步改完再 await。这个处理器现在是 async 的，await 期间按钮还能点，
                    //    那时 IsOpen 若还是 true，下一次点击会被误判成「再关一次」而被吞掉。
                    StopPolling("串口已关闭，轮询已停止。");
                    _session.Close();
                    AppendLog("已关闭 " + _portSettings.Describe());
                    UpdateSerialUi();

                    // ③ 最后才等它们退干净：它们的收尾会把「已取消」写进状态栏，
                    //    不能让它落到下一次连接上。取消早已发出，不会拖满超时。
                    await AwaitDeviceInfoStoppedAsync();
                    await AwaitCommandSendStoppedAsync();
                    return;
                }

                await OpenSerialAsync();
            }
            catch (Exception ex)
            {
                // 开关两个分支共用：Close() 在旧读循环没能按时退出时会抛（拒绝信号），
                // 所以这里不能写死「打开失败」。
                AppendLog("串口操作失败：" + ex.Message);
                UpdateSerialUi();
            }
        }

        private SerialPortSettings ReadPortSettings()
        {
            var settings = new SerialPortSettings();
            settings.PortName = (PortCombo.SelectedItem as string) ?? (PortCombo.Text ?? string.Empty);
            settings.BaudRate = (int)ParseLong(BaudText(), 115200, 300, 4000000);
            settings.DataBits = (int)ParseLong(DataBitsCombo.Text, 8, 5, 8);

            int parity = ParityCombo.SelectedIndex;
            settings.Parity = parity >= 0 && parity <= 4 ? (SerialParity)parity : SerialParity.None;

            switch (StopBitsCombo.SelectedIndex)
            {
                case 1: settings.StopBits = SerialStopBits.OnePointFive; break;
                case 2: settings.StopBits = SerialStopBits.Two; break;
                default: settings.StopBits = SerialStopBits.One; break;
            }

            return settings;
        }

        private string BaudText()
        {
            if (!string.IsNullOrEmpty(BaudCombo.Text))
                return BaudCombo.Text;

            return BaudCombo.SelectedItem == null ? "115200" : BaudCombo.SelectedItem.ToString();
        }

        /// <summary>字节序下拉框的选项，串口页与响应解析页共用。</summary>
        private static readonly string[] ByteOrderItems = { "大端（实测）", "小端（手册假设）" };

        /// <summary>读下拉框选中的字节序；0 = 大端（默认）。</summary>
        private static ByteOrder SelectedByteOrder(ComboBox combo)
        {
            return combo != null && combo.SelectedIndex == 1 ? ByteOrder.LittleEndian : ByteOrder.BigEndian;
        }

        private void ReadReceiveSettings()
        {
            _session.ReceiveMode = IsRawMode() ? ReceiveMode.RawFrames : ReceiveMode.TextLines;
            _session.Extractor.Pattern = PatternBox.Text;
            _session.Extractor.AllowFallbackScan = LooseScanCheck.IsChecked == true;
            _session.ByteOrder = SelectedByteOrder(ByteOrderCombo);
        }

        private bool IsRawMode()
        {
            // 注意：XAML 解析到 ModeTextRadio 的 IsChecked="True" 时就会触发 SendMode_Checked，
            // 那一刻 ModeRawRadio 还没被创建出来，所以这里必须判空。
            return ModeRawRadio != null && ModeRawRadio.IsChecked == true;
        }

        /// <summary>
        /// 单条命令等回包的时长。跟轮询用同一个设置，用户调一处即可——
        /// 设备信息面板与命令组装页的「发送并解析」都用它。
        /// </summary>
        private int ResponseTimeoutMs()
        {
            return (int)ParseLong(TimeoutBox.Text, 1500, 20, 60000);
        }

        private void UpdateSerialUi()
        {
            bool open = _session != null && _session.IsOpen;
            OpenCloseButton.Content = open ? "关闭串口" : "打开串口";
            SerialStatusText.Text = open ? ("已打开：" + _portSettings.Describe()) : "未打开";
            SerialStatusText.Foreground = open ? Brushes.Green : Gray;

            bool canEdit = !open;
            PortCombo.IsEnabled = canEdit;
            BaudCombo.IsEnabled = canEdit;
            DataBitsCombo.IsEnabled = canEdit;
            ParityCombo.IsEnabled = canEdit;
            StopBitsCombo.IsEnabled = canEdit;
            RefreshPortsButton.IsEnabled = canEdit;
            UpdateDeviceInfoUi();
            UpdateCommandSendUi();
            UpdateDownloadUi();
        }

        // =================================================================
        //  发送
        // =================================================================

        private void SendMode_Checked(object sender, RoutedEventArgs e)
        {
            bool raw = IsRawMode();
            if (TemplateBox != null)
                TemplateBox.IsEnabled = !raw;
            if (EndingCombo != null)
                EndingCombo.IsEnabled = !raw;
            if (_session != null && _session.IsOpen)
                ReadReceiveSettings();

            UpdateSendModeHint();
        }

        private void UpdateSendModeHint()
        {
            if (PatternHint == null)
                return;

            string error;
            var probe = new ResponseLineExtractor { Pattern = PatternBox == null ? null : PatternBox.Text };
            if (!probe.TryValidatePattern(out error))
            {
                PatternHint.Text = error;
                PatternHint.Foreground = Brushes.Firebrick;
                return;
            }

            PatternHint.Text = IsRawMode()
                ? "裸帧直连：设备直接回 12 字节二进制帧，不按行解析。"
                : "调试控制台：从文本行里按规则抠出帧。";
            PatternHint.Foreground = Gray;
        }

        private void PatternBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateSendModeHint();
        }

        private async void SendOnce_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_session.IsOpen)
                {
                    AppendLog("请先打开串口。");
                    return;
                }

                byte[] frame = HexUtil.ParseBytes(ManualFrameBox.Text);
                if (frame.Length != 12)
                    throw new Exception("命令帧必须是 12 字节，当前是 " + frame.Length + " 字节。");

                _serialOptions.Mode = IsRawMode() ? SendMode.RawFrame : SendMode.TextCommand;
                _serialOptions.Template = TemplateBox.Text;
                _serialOptions.LineEnding = SelectedLineEnding();

                string outgoing = DescribeOutgoing(frame);
                SendRequest request = _serialOptions.For(frame);

                // 先记日志再取锁：取锁可能要等另一笔事务——一次设备信息更新要连着读六条命令，
                // 读超时设得大时那是好几分钟。这期间界面上什么都不显示的话，
                // 用户只会觉得按钮坏了；先出一行日志至少说明这次点击被收到了。
                AppendLine("[发送] " + outgoing);

                // 手工帧走事务锁，它的**写入**就不会插进别人（轮询 / 设备信息）的事务中间。
                //
                // 但这把锁保护的是写入，不是响应窗口：手工发送不等回包，所以理论上
                // 一条手工 0xC0 的响应仍可能落进面板的 0xC0-04 等待里。
                // 那一头由 DeviceInfoReader 按子命令复核兜住了——收到子命令对不上的帧
                // 会明确报失败，而不是把它显示成「配置版本」。
                using (SerialTransaction transaction =
                    await _session.BeginTransactionAsync(System.Threading.CancellationToken.None))
                {
                    await _session.SendAsync(request, System.Threading.CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                AppendLog("发送失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 日志里显示真正写进串口的内容：文本模式给整条命令（含模板与设备参数），
        /// 裸帧模式给十六进制帧。排查「设备说命令不认识」这类问题时，看到整条命令比只看到帧有用得多。
        /// </summary>
        private string DescribeOutgoing(byte[] frame)
        {
            return _serialOptions.Mode == SendMode.RawFrame
                ? Rtl8239CommandBuilder.ToHex(frame)
                : CommandTemplate.Fill(_serialOptions.Template, frame);
        }

        private LineEnding SelectedLineEnding()
        {
            switch (EndingCombo.SelectedIndex)
            {
                case 0: return LineEnding.None;
                case 1: return LineEnding.Cr;
                case 2: return LineEnding.Lf;
                default: return LineEnding.CrLf;
            }
        }

        // =================================================================
        //  关闭
        // =================================================================

        private async void SerialTab_Closing(object sender, CancelEventArgs e)
        {
            if (_closing)
                return;

            _closing = true;
            e.Cancel = true;      // 先把关闭拦下来，等清理完再真正关

            try
            {
                await ShutdownSerialTabAsync();
            }
            catch (Exception)
            {
                // 关闭流程里不再往外抛
            }

            Close();
        }

        private async Task ShutdownSerialTabAsync()
        {
            if (_logTimer != null)
                _logTimer.Stop();
            if (_chartTimer != null)
                _chartTimer.Stop();
            if (_portWatchTimer != null)
                _portWatchTimer.Stop();

            // 先掐设备信息读取与命令组装页的发送，再停轮询：它们可能正持着事务锁在等回包，
            // 倒过来的话轮询取消后会卡在这把锁上多等一个超时。
            CancelDeviceInfo();
            CancelCommandSend();
            await AwaitDeviceInfoStoppedAsync();
            await AwaitCommandSendStoppedAsync();
            await AwaitDownloadStoppedAsync();

            StopPolling(null);
            if (_pollTask != null)
            {
                try
                {
                    await _pollTask;
                }
                catch (Exception)
                {
                    // 关闭时的异常忽略
                }
            }

            if (_session == null)
                return;

            Task readLoop = _session.ReadLoopTask;
            SerialSession session = _session;

            // SerialPort.Close() 在读线程正阻塞于读操作时可能一起阻塞（某些驱动上远不止 200ms），
            // 所以放到线程池上做，并且等待带上超时——否则关窗会卡在 UI 线程上，
            // 后面那句 Task.Delay 保险根本保护不到它。
            Task closeTask = Task.Run(delegate
            {
                try
                {
                    session.Close();
                }
                catch (Exception)
                {
                    // 设备可能已经不在了
                }
            });

            Task closing = readLoop == null ? closeTask : Task.WhenAll(closeTask, readLoop);
            await Task.WhenAny(closing, Task.Delay(1500));

            await Task.Run(delegate
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception)
                {
                    // SerialPort.Close() 有时会抛，忽略
                }
            });
        }

        // =================================================================
        //  小工具
        // =================================================================

        private static long ParseLong(string text, long fallback, long min, long max)
        {
            long value;
            try
            {
                value = Rtl8239Catalog.ParseNumber(text);
            }
            catch (Exception)
            {
                return fallback;
            }

            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static Brush BrushFrom(string colorHex)
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            }
            catch (Exception)
            {
                return Brushes.SteelBlue;
            }
        }
    }
}
