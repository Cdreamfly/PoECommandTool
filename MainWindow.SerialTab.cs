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

            Closing += SerialTab_Closing;
            UpdateSerialUi();

            AppendLog(AppVersion.Title + " 已启动（版本号见标题栏与「帮助 → 关于」）。");
        }

        private void BuildSerialOptionLists()
        {
            RefreshPortList();

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

        private void OpenClose_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_session.IsOpen)
                {
                    StopPolling("串口已关闭，轮询已停止。");
                    _session.Close();
                    AppendLog("已关闭 " + _portSettings.Describe());
                    UpdateSerialUi();
                    return;
                }

                _portSettings = ReadPortSettings();
                ReadReceiveSettings();
                _session.Open(_portSettings);
                AppendLog("已打开 " + _portSettings.Describe());
                UpdateSerialUi();
            }
            catch (Exception ex)
            {
                AppendLog("打开串口失败：" + ex.Message);
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

        private void SendOnce_Click(object sender, RoutedEventArgs e)
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

                SendRequest request = _serialOptions.For(frame);
                _session.SendAsync(request, System.Threading.CancellationToken.None)
                    .ContinueWith(delegate(Task task)
                    {
                        if (task.IsFaulted && task.Exception != null)
                            AppendLog("发送失败：" + task.Exception.GetBaseException().Message);
                    });

                AppendLine("[发送] " + DescribeOutgoing(frame));
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
