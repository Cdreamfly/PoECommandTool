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

        /// <summary>读取的生命周期（忙标志 / 取消源 / 任务）。
        /// 三个面板共用同一套实现——见 <see cref="CancelableOperation"/>。</summary>
        private readonly CancelableOperation _deviceInfo = new CancelableOperation();

        private void InitializeDeviceInfo()
        {
            _deviceInfoReader = new DeviceInfoReader(_session);
            _deviceInfoReader.Progress = OnDeviceInfoProgress;
            UpdateDeviceInfoUi();
        }

        /// <summary>上一次读到的设备信息，供「复制全部」用——屏幕上的控件每次刷新都会重建。</summary>
        private DeviceInfoResult _lastDeviceInfo;
        private string _lastDeviceInfoTime;

        private void DeviceInfoCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_lastDeviceInfo == null)
                return;

            try
            {
                Clipboard.SetText(DeviceInfoReader.Describe(_lastDeviceInfo, _lastDeviceInfoTime));
                AppendLog("[工具] 设备信息已复制到剪贴板。");
            }
            catch (Exception ex)
            {
                // 剪贴板被别的进程占着时会抛，属于常见且无害的失败，如实说一声即可
                AppendLog("[工具] 复制设备信息失败（剪贴板可能被别的程序占用）：" + ex.Message);
            }
        }

        // =================================================================
        //  触发
        // =================================================================

        /// <summary>读取中这颗按钮是「取消」，空闲时是「更新」。</summary>
        private async void DeviceInfoUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_deviceInfo.IsBusy)
            {
                _deviceInfo.Cancel();
                return;
            }

            await StartDeviceInfoRead();
        }

        /// <summary>发起一次读取；已有读取在跑时直接返回那一笔，不并发两条。</summary>
        private Task StartDeviceInfoRead()
        {
            if (_deviceInfo.IsBusy && _deviceInfo.CurrentTask != null)
                return _deviceInfo.CurrentTask;

            _deviceInfo.CurrentTask = RefreshDeviceInfoAsync();
            return _deviceInfo.CurrentTask;
        }

        private async Task RefreshDeviceInfoAsync()
        {
            if (_deviceInfo.IsBusy)
                return;

            if (!CanSend())
            {
                AppendLog("请先打开串口。");
                UpdateDeviceInfoUi();
                return;
            }

            _deviceInfo.TryBegin();
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
                    _serialOptions, ResponseTimeoutMs(), _deviceInfo.Token);

                RenderDeviceInfo(result);
                ShowResult(result);
            }
            catch (OperationCanceledException)
            {
                ShowCancelled();
            }
            catch (Exception ex)
            {
                ShowError(ex);
            }
            finally
            {
                _deviceInfo.Finish();
                UpdateDeviceInfoUi();
            }
        }

        // =================================================================
        //  状态呈现
        // =================================================================

        private void ShowResult(DeviceInfoResult result)
        {
            bool allOk = result.OkCount == result.TotalCount;
            DeviceInfoStatusText.Foreground = allOk ? Brushes.Green : Brushes.Firebrick;

            string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            DeviceInfoStatusText.Text = result.Summary + "　最后更新 " + stamp;

            // 留给「复制全部」用：面板每次刷新都会重建，选中过的内容会丢，
            // 所以复制的来源不该是屏幕上的控件，而是这份模型。
            _lastDeviceInfo = result;
            _lastDeviceInfoTime = stamp;
            if (DeviceInfoCopyButton != null)
                DeviceInfoCopyButton.IsEnabled = true;

            AppendLog("[工具] 设备信息：" + result.Summary);
            for (int i = 0; i < result.Entries.Count; i++)
            {
                DeviceInfoEntry entry = result.Entries[i];
                if (!entry.Ok)
                    AppendLog("[工具] 设备信息 " + entry.CommandKey + "（" + entry.Title + "）：" + entry.Error);
            }
        }

        private void ShowCancelled()
        {
            DeviceInfoStatusText.Foreground = Gray;
            DeviceInfoStatusText.Text = "已取消";
        }

        private void ShowError(Exception ex)
        {
            DeviceInfoStatusText.Foreground = Brushes.Firebrick;
            DeviceInfoStatusText.Text = "读取失败：" + ex.Message;
            AppendLog("[工具] 设备信息读取失败：" + ex.Message);
        }

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
                // 同步的 Invoke，不是 BeginInvoke：读取收尾后调用方还要写最终文案，
                // 而 Invoke 会等委托跑完才放行——这个先后顺序就是「最终文案不会被打回
                // 『读取中』」的依据。换 BeginInvoke 就变成两个都在队列里、谁先谁后看运气。
                Dispatcher.Invoke(delegate
                {
                    if (!_deviceInfo.IsBusy)
                        return;     // 已经收尾，别再覆盖最终状态

                    DeviceInfoStatusText.Text = string.Format(
                        CultureInfo.InvariantCulture, "读取中 {0}/{1}：{2}", done + 1, total, title);
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

            if (_deviceInfo.IsBusy)
            {
                // 读取中，同一颗按钮变「取消」。这是唯一的逃生口：一次更新要连读六条命令
                // （0x4C 还要多读一块），超时设置大时能挂好几分钟——没有取消就只能关串口。
                DeviceInfoUpdateButton.Content = "取消";
                DeviceInfoUpdateButton.IsEnabled = true;
                UpdatePollStartButton();
                return;
            }

            bool open = CanSend();

            DeviceInfoUpdateButton.Content = "更新";
            // 轮询期间禁用：两边共用一把事务锁，一次更新要连读六条命令，
            // 插进去会把串口独占十几秒（超时设置越大越久），轮询只能干等。
            DeviceInfoUpdateButton.IsEnabled = open && !_pollingActive;
            UpdatePollStartButton();
        }

        // 「先同步发取消、改完状态、最后才等」，两件事被拆开用了，那个方法就没有调用者了。

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
