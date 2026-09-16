using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfApp1.Serial;

namespace WpfApp1
{
    /// <summary>
    /// 「串口读写」页的设备信息面板（身份/诊断类查询）。
    ///
    /// 只做「接线 + 封送到 UI 线程」——读什么、怎么分页、怎么格式化都在
    /// <see cref="DeviceInfoReader"/> 里（纯 C#，能在 Linux 上编译并断言）。
    ///
    /// 这一块**不轮询**：只在「串口刚打开」和「点更新」两个时机各读一次。
    /// </summary>
    public partial class MainWindow
    {
        private DeviceInfoReader _deviceInfoReader;
        private CancellationTokenSource _deviceInfoCts;
        private Task _deviceInfoTask;
        private bool _deviceInfoBusy;

        private void InitializeDeviceInfo()
        {
            _deviceInfoReader = new DeviceInfoReader(_session);
            _deviceInfoReader.Progress = OnDeviceInfoProgress;
            UpdateDeviceInfoUi();
        }

        // =================================================================
        //  触发
        // =================================================================

        private async void DeviceInfoUpdate_Click(object sender, RoutedEventArgs e)
        {
            await StartDeviceInfoRead();
        }

        /// <summary>发起一次读取；已有读取在跑时直接返回那一笔，不并发两条。</summary>
        private Task StartDeviceInfoRead()
        {
            if (_deviceInfoBusy && _deviceInfoTask != null)
                return _deviceInfoTask;

            _deviceInfoTask = RefreshDeviceInfoAsync();
            return _deviceInfoTask;
        }

        private async Task RefreshDeviceInfoAsync()
        {
            if (_deviceInfoBusy)
                return;

            if (_session == null || !_session.IsOpen)
            {
                AppendLog("请先打开串口。");
                UpdateDeviceInfoUi();
                return;
            }

            _deviceInfoBusy = true;
            _deviceInfoCts = new CancellationTokenSource();
            UpdateDeviceInfoUi();
            DeviceInfoStatusText.Foreground = Gray;
            DeviceInfoStatusText.Text = "读取中…";

            try
            {
                // 发送方式跟着本页当前的设置走：调试控制台走文本模板，串口直连走裸帧
                _serialOptions.Mode = IsRawMode() ? SendMode.RawFrame : SendMode.TextCommand;
                _serialOptions.Template = TemplateBox.Text;
                _serialOptions.LineEnding = SelectedLineEnding();

                DeviceInfoResult result = await _deviceInfoReader.ReadAllAsync(
                    _serialOptions, DeviceInfoTimeoutMs(), _deviceInfoCts.Token);

                RenderDeviceInfo(result);

                bool allOk = result.OkCount == result.TotalCount;
                DeviceInfoStatusText.Foreground = allOk ? Brushes.Green : Brushes.Firebrick;
                DeviceInfoStatusText.Text = result.Summary + "　最后更新 "
                    + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

                AppendLog("[工具] 设备信息：" + result.Summary);
                LogDeviceInfoFailures(result);
            }
            catch (OperationCanceledException)
            {
                DeviceInfoStatusText.Foreground = Gray;
                DeviceInfoStatusText.Text = "已取消";
            }
            catch (Exception ex)
            {
                DeviceInfoStatusText.Foreground = Brushes.Firebrick;
                DeviceInfoStatusText.Text = "读取失败：" + ex.Message;
                AppendLog("[工具] 设备信息读取失败：" + ex.Message);
            }
            finally
            {
                _deviceInfoBusy = false;

                if (_deviceInfoCts != null)
                {
                    _deviceInfoCts.Dispose();
                    _deviceInfoCts = null;
                }

                UpdateDeviceInfoUi();
            }
        }

        /// <summary>该等多久：跟轮询用同一个超时设置，用户调一处即可。</summary>
        private int DeviceInfoTimeoutMs()
        {
            return (int)ParseLong(TimeoutBox.Text, 1500, 20, 60000);
        }

        private void LogDeviceInfoFailures(DeviceInfoResult result)
        {
            for (int i = 0; i < result.Entries.Count; i++)
            {
                DeviceInfoEntry entry = result.Entries[i];
                if (!entry.Ok)
                    AppendLog("[工具] 设备信息 " + entry.CommandKey + "（" + entry.Title + "）：" + entry.Error);
            }
        }

        // =================================================================
        //  进度与状态
        // =================================================================

        /// <summary>
        /// 进度在读取线程上回调，而读取线程**不是** UI 线程（<see cref="DeviceInfoReader"/>
        /// 内部一路 ConfigureAwait(false)），所以这里必须自己封送过去，不能直接控件。
        /// </summary>
        private void OnDeviceInfoProgress(int done, int total, string title)
        {
            if (Dispatcher.HasShutdownStarted)
                return;

            try
            {
                // 这里必须用同步的 Invoke，**不能**图省事改成 BeginInvoke：
                // 读取收尾时还会再报一次进度（title == null），而「本次更新：n/n 成功」是
                // ReadAllAsync 返回之后才写上去的。Invoke 会等委托跑完才放行，这个先后
                // 顺序才是「最终文案不会被打回『读取中』」的依据；换 BeginInvoke 就变成
                // 两个都在队列里、谁先谁后看运气。
                Dispatcher.Invoke(delegate
                {
                    // 已经收尾（_deviceInfoCts 被清空）就别再覆盖最终状态
                    if (_deviceInfoCts == null)
                        return;

                    DeviceInfoStatusText.Text = title == null
                        ? "读取中…"
                        : string.Format("读取中 {0}/{1}：{2}", done + 1, total, title);
                });
            }
            catch (Exception)
            {
                // 窗口已在关闭途中：进度显示不了了，不影响读取本身
            }
        }

        private void UpdateDeviceInfoUi()
        {
            if (DeviceInfoUpdateButton == null)
                return;

            bool open = _session != null && _session.IsOpen;

            // 轮询期间禁用：两边共用一把事务锁，一次更新要连读六条命令，
            // 插进去会把串口独占十几秒（超时设置越大越久），轮询只能干等。
            DeviceInfoUpdateButton.IsEnabled = open && !_deviceInfoBusy && !_pollingActive;
        }

        /// <summary>通知读取停下（不等待）。要先于关串口发出，否则它会在已关闭的端口上一直等到超时。</summary>
        private void CancelDeviceInfo()
        {
            CancellationTokenSource cts = _deviceInfoCts;
            if (cts != null)
                cts.Cancel();
        }

        /// <summary>等正在跑的读取退干净（异常不外抛）。</summary>
        private async Task AwaitDeviceInfoStoppedAsync()
        {
            Task task = _deviceInfoTask;
            if (task == null)
                return;

            try
            {
                await task;
            }
            catch (Exception)
            {
                // 关闭流程里不往外抛
            }
        }

        /// <summary>关窗口时用：先取消，再等它退干净。</summary>
        private async Task ShutdownDeviceInfoAsync()
        {
            CancelDeviceInfo();
            await AwaitDeviceInfoStoppedAsync();
        }

        // =================================================================
        //  渲染
        // =================================================================

        private void RenderDeviceInfo(DeviceInfoResult result)
        {
            DeviceInfoPanel.Children.Clear();

            for (int i = 0; i < result.Entries.Count; i++)
            {
                DeviceInfoEntry entry = result.Entries[i];

                DeviceInfoPanel.Children.Add(new TextBlock
                {
                    Text = entry.CommandKey + "  " + entry.Title + (entry.Ok ? string.Empty : "（读取失败）"),
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 2),
                    Foreground = entry.Ok ? Brushes.Black : Brushes.Firebrick,
                });

                var wrap = new WrapPanel();
                for (int f = 0; f < entry.Fields.Count; f++)
                    wrap.Children.Add(BuildInfoField(entry.Fields[f]));
                DeviceInfoPanel.Children.Add(wrap);

                if (!entry.Ok && !string.IsNullOrEmpty(entry.Error))
                {
                    DeviceInfoPanel.Children.Add(new TextBlock
                    {
                        Text = entry.Error,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Firebrick,
                        Margin = new Thickness(0, 2, 0, 0),
                    });
                }
            }
        }

        /// <summary>
        /// 一个「标签 值」。值用只读 TextBox 而不是 TextBlock——现场排查时能把值选中复制
        /// 贴进工单，这一点比「看起来像标签」重要。
        /// </summary>
        private static FrameworkElement BuildInfoField(DeviceInfoField field)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 14, 0),
            };

            panel.Children.Add(new TextBlock
            {
                Text = field.Label,
                Foreground = Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });

            panel.Children.Add(new TextBox
            {
                Text = field.Value ?? string.Empty,
                IsReadOnly = true,
                IsTabStop = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                FontFamily = Mono,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.IBeam,
            });

            return panel;
        }
    }
}
