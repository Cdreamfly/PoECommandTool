using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PoECommandTool.Chart;
using PoECommandTool.Net;
using PoECommandTool.Serial;

namespace PoECommandTool
{
    /// <summary>
    /// 「连接与读写」页的串口配置与发送部分。
    ///
    /// 这里只做「接线 + 封送到 UI 线程」——协议、调度、采样、绘图计算都在 Serial/ 与 Chart/
    /// 的纯 C# 类里（那些能在 Linux 上编译并断言）。轮询见 MainWindow.SerialPoll.cs，
    /// 采样/曲线/日志见 MainWindow.SerialChart.cs。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// 等后台操作退出的上限。与 SerialSession 等读循环退出用的 1000ms 是同一个思路：
        /// 卡住的驱动写入是 Cancel 掐不断的，不能无限等下去。
        /// </summary>
        private const int BackgroundStopTimeoutMs = 1500;

        private SerialSession _session;
        private SwitchableTransport _link;
        private SendOptions _serialOptions;
        private SerialPortSettings _portSettings;
        private bool _closing;

        /// <summary>
        /// 正在建立连接。连接现在是异步的（见 <see cref="OpenSerialAsync"/>），
        /// 握手期间 <c>_session.IsOpen</c> 还是 false，不挡住的话连点两下会开两次。
        /// </summary>
        private bool _connecting;

        /// <summary>当前选中的链路类型。</summary>
        private TransportKind _transportKind = TransportKind.Serial;

        /// <summary>
        /// 网络链路的端点与账号。主机/端口/用户名在内存里记住，**口令不落盘**、
        /// 每次连接时现输，断开时清掉。
        /// </summary>
        private NetworkSettings _netSettings;

        /// <summary>连接方式下拉框的选项。**只列真的实现了的方式**——列了却不能连比不列更糟。</summary>
        private static readonly string[] TransportLabels = { "串口", "Telnet", "SSH" };
        private static readonly TransportKind[] TransportKinds =
        {
            TransportKind.Serial, TransportKind.Telnet, TransportKind.Ssh,
        };

        /// <summary>当前链路在界面文案里的说法。</summary>
        private string LinkWord
        {
            get
            {
                switch (_transportKind)
                {
                    case TransportKind.Telnet: return "Telnet";
                    case TransportKind.Ssh: return "SSH";
                    default: return "串口";
                }
            }
        }

        /// <summary>
        /// 「还没连接，请用户先做什么」的统一说法。
        ///
        /// 收敛到一处：这句话原先在四个文件里各写了一遍「请先打开串口。」，
        /// 加了网络链路之后如果还各处手写，早晚会出现某一处忘了改。
        /// </summary>
        private string ConnectPrompt
        {
            get
            {
                return _transportKind == TransportKind.Serial
                    ? "请先打开串口。"
                    : "请先连接 " + LinkWord + "。";
            }
        }

        // =================================================================
        //  初始化
        // =================================================================

        private void InitializeSerialTab()
        {
            _serialOptions = new SendOptions();
            _portSettings = new SerialPortSettings();
            // 会话终身持有这一个转发器；真正用哪条链路（串口 / Telnet / SSH）在连接时装进去。
            // 见 Serial/SwitchableTransport.cs。
            _link = new SwitchableTransport();
            _link.Install(new SystemSerialTransport());
            _session = new SerialSession(_link);
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

            // 连接方式下拉框要放在**初始化最后**：设置 SelectedIndex 会触发
            // SelectionChanged → UpdateTransportPanels → UpdateSerialUi，而后者会
            // 顺带刷新设备信息 / 发送 / 下载那几块，得等它们都建好。
            TransportCombo.ItemsSource = TransportLabels;
            TransportCombo.SelectedIndex = 0;
            UpdateTransportPanels();

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

        /// <summary>
        /// 串口能不能用：会话已建好、且端口开着。
        ///
        /// 单独一个方法而不是到处写 `_session == null || !_session.IsOpen`：
        /// 那句话原先在 5 个文件里出现 9 次，改一次「什么叫打开」要动五个地方。
        /// 注意 <see cref="SerialSession.IsOpen"/> 本身是会话自己记的——net48 上拔线之后
        /// `SerialPort.IsOpen` 经常还是 true，不能信它。
        /// </summary>
        private bool CanSend()
        {
            return _session != null && _session.IsOpen;
        }

        private void RefreshPortList()
        {
            string[] ports;
            try
            {
                ports = SystemSerialTransport.GetPortNames();
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
            if (_connecting)
                return;
            _connecting = true;
            UpdateSerialUi();

            try
            {
                TransportSettings settings = ReadTransportSettings();
                ReadReceiveSettings();

                // 调试控制台只回文本；裸帧是直连 UART 才有的概念。
                if (_transportKind != TransportKind.Serial)
                    _session.ReceiveMode = ReceiveMode.TextLines;

                // 整段「收尾旧循环 → 换芯 → 建立连接」都放到后台线程。两个理由：
                //  1) 网络方式的连接是同步阻塞的（TCP 握手 + SSH 协商，超时上限 10 秒），
                //     占着 UI 线程会让窗口假死；
                //  2) SSH 的主机密钥确认回调是在**别的线程**上抛的（2026-10-08 实测：
                //     回调线程 ≠ 调用线程），它必须派发回 UI 线程才能弹框——而如果 UI 线程
                //     正卡在 Connect() 里，那个派发会**死等**（是死锁，不是报错）。
                await Task.Run(delegate
                {
                    // 先把旧读循环收干净（它还阻塞在**旧**传输层上），再换芯。
                    // 顺序反过来的话，旧循环会从刚装上的新传输层读，并且把「还没打开」报成一次链路故障。
                    _session.Close();
                    _link.Install(BuildTransport());

                    _session.Open(settings);
                });

                var serial = settings as SerialPortSettings;
                _lastOpenedPort = serial != null ? serial.PortName : null;
                _reconnectWanted = false;

                AppendLog("已连接 " + settings.Describe());
            }
            finally
            {
                // 到这儿链路已经算连上了：_connecting 必须马上复位，否则按钮会一直灰着、
                // 状态一直显示「连接中…」，而下面那次设备信息读取在设备不回话时要等好几个超时。
                _connecting = false;
                UpdateSerialUi();
            }

            // 刚连上顺手读一次设备信息：这不是轮询，只有一次往返，但能立刻暴露
            // 「设备不认这些命令」这类问题，省得用户以为是按钮坏了。
            // 它失败不该影响「已经连上」这个事实，所以单独兜异常。
            try
            {
                await StartDeviceInfoRead();
            }
            catch (Exception ex)
            {
                AppendLog("读取设备信息失败：" + ex.Message);
            }
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
            // 热插拔只对串口有意义：网络链路没有「端口列表」可看。
            if (_transportKind != TransportKind.Serial)
                return;

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
                return SystemSerialTransport.GetPortNames();
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
                    _deviceInfo.Cancel();
                    _sendParse.Cancel();
                    _download.Cancel();
                    _reconnectWanted = false;   // 是用户主动关的，别自动连回来

                    // ② 状态同步改完再 await。这个处理器现在是 async 的，await 期间按钮还能点，
                    //    那时 IsOpen 若还是 true，下一次点击会被误判成「再关一次」而被吞掉。
                    StopPolling(LinkWord + "已断开，轮询已停止。");
                    _session.Close();
                    AppendLog("已断开 " + CurrentLinkDescription());
                    UpdateSerialUi();

                    // ③ 最后才等它们退干净：它们的收尾会把「已取消」写进状态栏，
                    //    不能让它落到下一次连接上。取消早已发出，不会拖满超时。
                    await _deviceInfo.AwaitStoppedAsync(BackgroundStopTimeoutMs);
                    await _sendParse.AwaitStoppedAsync(BackgroundStopTimeoutMs);
                    await _download.AwaitStoppedAsync(BackgroundStopTimeoutMs);
                    return;
                }

                await OpenSerialAsync();
            }
            catch (Exception ex)
            {
                // 开关两个分支共用：Close() 在旧读循环没能按时退出时会抛（拒绝信号），
                // 所以这里不能写死「打开失败」。
                AppendLog("连接操作失败：" + ex.Message);
                UpdateSerialUi();
            }
        }

        // =================================================================
        //  连接方式（串口 / Telnet / SSH）
        // =================================================================

        private void TransportCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = TransportCombo.SelectedIndex;
            _transportKind = index >= 0 && index < TransportKinds.Length
                ? TransportKinds[index]
                : TransportKind.Serial;

            UpdateTransportPanels();
        }

        /// <summary>按当前方式显示对应的一排参数控件。</summary>
        private void UpdateTransportPanels()
        {
            if (SerialOptionsPanel == null || NetworkOptionsPanel == null)
                return;

            bool serial = _transportKind == TransportKind.Serial;
            SerialOptionsPanel.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
            NetworkOptionsPanel.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;

            // 网络链路只有文本：控制台的输出是一行行文本，没有裸帧这回事。
            if (!serial && ModeRawRadio != null)
            {
                if (ModeRawRadio.IsChecked == true && ModeTextRadio != null)
                    ModeTextRadio.IsChecked = true;     // 从串口切过来时把裸帧收回去
                ModeRawRadio.IsEnabled = false;
            }
            else if (ModeRawRadio != null)
            {
                ModeRawRadio.IsEnabled = true;
            }

            if (SshKeyPanel != null)
                SshKeyPanel.Visibility = _transportKind == TransportKind.Ssh
                    ? Visibility.Visible : Visibility.Collapsed;

            if (NetHintText != null)
            {
                switch (_transportKind)
                {
                    case TransportKind.Telnet:
                        NetHintText.Text = "Telnet 不加密：账号与数据以明文经过网络，仅限可信内网使用。";
                        break;
                    case TransportKind.Ssh:
                        NetHintText.Text = "首次连接会要求确认主机密钥指纹。";
                        break;
                    default:
                        NetHintText.Text = string.Empty;
                        break;
                }
            }

            if (NetPortBox != null && _netSettings == null)
            {
                NetPortBox.Text = NetworkSettings.DefaultPortFor(_transportKind)
                    .ToString(CultureInfo.InvariantCulture);
            }

            UpdateSerialUi();
        }

        /// <summary>
        /// 按当前方式把界面上的设置收成一个 <see cref="TransportSettings"/>。
        ///
        /// 网络方式的**口令只从 PasswordInput 现取、只放进这个对象里**：不落盘、不进日志，
        /// 连上之后由传输层自己用掉（见 Net/TelnetTransport.cs 的 LoginIfNeeded）。
        /// </summary>
        private TransportSettings ReadTransportSettings()
        {
            if (_transportKind == TransportKind.Serial)
            {
                _portSettings = ReadPortSettings();
                return _portSettings;
            }

            if (_netSettings == null || _netSettings.Kind != _transportKind)
                _netSettings = new NetworkSettings(_transportKind);

            _netSettings.Host = (HostBox.Text ?? string.Empty).Trim();
            _netSettings.Port = (int)ParseLong(NetPortBox.Text,
                NetworkSettings.DefaultPortFor(_transportKind), 1, 65535);
            _netSettings.User = (UserBox.Text ?? string.Empty).Trim();
            _netSettings.Password = PasswordInput.Password ?? string.Empty;
            _netSettings.KeyFile = (KeyBox.Text ?? string.Empty).Trim();

            return _netSettings;
        }

        /// <summary>按当前方式造一个传输层，交给 <see cref="SwitchableTransport"/> 当芯。</summary>
        private ITransport BuildTransport()
        {
            switch (_transportKind)
            {
                case TransportKind.Telnet:
                    return new TelnetTransport();

                case TransportKind.Ssh:
                    var ssh = new SshTransport();
                    // 主机密钥确认必须由界面给：传输层在 Net/ 里，不碰 WPF。
                    // 不接这个回调的话 SshTransport 会**拒绝**一切主机密钥（安全默认）。
                    ssh.HostKeyPrompt = ConfirmSshHostKey;
                    return ssh;

                default:
                    return new SystemSerialTransport();
            }
        }

        /// <summary>
        /// 首次见到某台设备的主机密钥时问一句。指纹只在本次运行内记住；换了指纹会再问一次
        /// （也就是说，指纹变了要么是真的换了机器，要么是有人在中间——两种情况都该让人看见）。
        /// </summary>
        private bool ConfirmSshHostKey(SshHostKey key)
        {
            // 这个回调**不在 UI 线程上**（SSH.NET 在它自己的协商线程上抛事件，实测如此），
            // 而 MessageBox 要碰窗口对象——不派发的话就是
            // 「调用线程无法访问此对象，因为另一个线程拥有该对象」。
            //
            // 这里敢用阻塞式 Invoke，是因为调用方已经把这整段连接放到了后台线程，
            // UI 线程是空闲的。（若哪天有人把 Open 挪回 UI 线程，这里会死锁。）
            if (!Dispatcher.CheckAccess())
                return (bool)Dispatcher.Invoke(new Func<bool>(delegate { return ConfirmSshHostKey(key); }));

            string text =
                "第一次连接到 " + key.Host + ":" + key.Port + "。\n\n" +
                "主机密钥类型：" + key.KeyName + "\n" +
                "指纹：" + key.FingerPrint + "\n\n" +
                "确认这是你要连的那台设备吗？\n" +
                "（指纹与设备上 ssh-keyscan / ssh-keygen -lf 的结果应当一致）";

            return MessageBox.Show(this, text, "确认主机密钥",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        private void KeyBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog();
            dialog.Title = "选择 SSH 私钥文件";
            dialog.Filter = "私钥文件 (*.*)|*.*";
            if (dialog.ShowDialog(this) == true)
                KeyBox.Text = dialog.FileName;
        }

        /// <summary>状态栏上描述当前链路的那一行。</summary>
        private string CurrentLinkDescription()
        {
            if (_transportKind == TransportKind.Serial)
                return _portSettings.Describe();

            return _netSettings != null ? _netSettings.Describe() : (LinkWord + " 未连接");
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
            // 网络链路只有文本：调试控制台回的是行文本，没有裸帧这回事。
            if (_transportKind != TransportKind.Serial)
                return false;

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
            bool open = CanSend();
            OpenCloseButton.Content = open ? "断开" : "连接";
            OpenCloseButton.IsEnabled = !_connecting;
            SerialStatusText.Text = _connecting
                ? "连接中…"
                : (open ? ("已连接：" + CurrentLinkDescription()) : "未连接");
            SerialStatusText.Foreground = open ? Brushes.Green : Gray;

            bool canEdit = !open;
            TransportCombo.IsEnabled = canEdit;
            PortCombo.IsEnabled = canEdit;
            BaudCombo.IsEnabled = canEdit;
            DataBitsCombo.IsEnabled = canEdit;
            ParityCombo.IsEnabled = canEdit;
            StopBitsCombo.IsEnabled = canEdit;
            RefreshPortsButton.IsEnabled = canEdit;
            HostBox.IsEnabled = canEdit;
            NetPortBox.IsEnabled = canEdit;
            UserBox.IsEnabled = canEdit;
            PasswordInput.IsEnabled = canEdit;
            KeyBox.IsEnabled = canEdit;
            KeyBrowseButton.IsEnabled = canEdit;
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
            if (CanSend())
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
                    AppendLog(ConnectPrompt);
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
            _deviceInfo.Cancel();
            _sendParse.Cancel();
            _download.Cancel();
            await _deviceInfo.AwaitStoppedAsync(BackgroundStopTimeoutMs);
            await _sendParse.AwaitStoppedAsync(BackgroundStopTimeoutMs);
            await _download.AwaitStoppedAsync(BackgroundStopTimeoutMs);

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
