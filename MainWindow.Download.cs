using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PoECommandTool.Serial;

namespace PoECommandTool
{
    /// <summary>
    /// 「固件下载」页的在线半边：把生成好的下载帧真的发给设备。
    ///
    /// ⚠️ **这条路径我无法在没有设备的情况下验证**。纯逻辑部分（分帧、应答核对、
    /// 重试、失败即停）在断言工程里有覆盖，但「真的能把固件写进设备」这句结论
    /// 必须真机跑过才算数。在这个分支上它是**未经验证**的。
    ///
    /// 两处刻意的保守设计：
    ///
    /// * **不自动跳转 Loader**。跳 Loader 会擦除固件信息，而擦除之后如果下载没走完，
    ///   设备就停在 Loader 里了。让用户自己决定什么时候发 `0xC0-00`。
    /// * 发送前**必须确认**。这不像别的按钮，按错了可能是一台砖。
    /// </summary>
    public partial class MainWindow
    {
        private DownloadRunner _downloadRunner;
        /// <summary>这一块的生命周期（忙标志 / 取消源 / 任务）。见 <see cref="CancelableOperation"/>。</summary>
        private readonly CancelableOperation _download = new CancelableOperation();

        // =================================================================
        //  触发
        // =================================================================

        private async void DownloadToDevice_Click(object sender, RoutedEventArgs e)
        {
            if (_download.IsBusy)
            {
                _download.Cancel();
                return;
            }

            await StartDownloadToDevice();
        }

        /// <summary>发起一次烧录；已有在下时直接返回那一笔。</summary>
        private Task StartDownloadToDevice()
        {
            if (_download.IsBusy && _download.CurrentTask != null)
                return _download.CurrentTask;

            _download.CurrentTask = RunDownloadAsync();
            return _download.CurrentTask;
        }

        private async Task RunDownloadAsync()
        {
            if (_download.IsBusy)
                return;

            if (!CanSend())
            {
                DlStatusText.Foreground = Brushes.Firebrick;
                DlStatusText.Text = "串口未打开——先到「串口读写」页打开串口。";
                return;
            }

            if (!IsRawMode())
            {
                // Loader 应答是 4 字节二进制，文本模式按行拆包根本认不出来
                DlStatusText.Foreground = Brushes.Firebrick;
                DlStatusText.Text = "请把「串口读写」页切到「裸帧直连」——Loader 应答是 4 字节二进制，文本模式认不出来。";
                return;
            }

            IList<DownloadFrame> frames;
            DownloadMode mode;
            try
            {
                frames = BuildDownloadFrames(out mode);
            }
            catch (Exception ex)
            {
                DlStatusText.Foreground = Brushes.Firebrick;
                DlStatusText.Text = "无法生成下载帧：" + ex.Message;
                return;
            }

            if (frames.Count == 0)
            {
                DlStatusText.Foreground = Brushes.Firebrick;
                DlStatusText.Text = "没有可发送的帧——先选文件并点「生成下载帧」。";
                return;
            }

            if (!ConfirmFlash(frames, mode))
                return;

            _download.TryBegin();
            UpdateDownloadUi();

            _downloadRunner = new DownloadRunner(_session);
            _downloadRunner.Progress = OnDownloadProgress;

            try
            {
                SendOptions options = _serialOptions;
                options.Mode = SendMode.RawFrame;

                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    "[工具] 开始向设备发送 {0} 帧（{1} 模式），失败即停；随时可以点「取消」。",
                    frames.Count, mode == DownloadMode.Firmware ? "Firmware" : "App"));

                DownloadResult result = await _downloadRunner.RunAsync(
                    frames, options, ResponseTimeoutMs(), 2, _download.Token);

                if (result.Ok)
                {
                    DlStatusText.Foreground = Brushes.Green;
                    DlStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                        "全部 {0} 帧已确认。", result.Total);
                    AppendLog("[工具] 固件帧已全部发送并确认（" + result.Total + " 帧）。");
                }
                else
                {
                    DlStatusText.Foreground = Brushes.Firebrick;
                    DlStatusText.Text = "下载中断：" + result.Error;
                    AppendLog("[工具] 固件下载中断：" + result.Error);
                }
            }
            catch (OperationCanceledException)
            {
                DlStatusText.Foreground = Gray;
                DlStatusText.Text = "已取消。";
                AppendLog("[工具] 固件下载已取消。");
            }
            catch (Exception ex)
            {
                DlStatusText.Foreground = Brushes.Firebrick;
                DlStatusText.Text = "下载失败：" + ex.Message;
                AppendLog("[工具] 固件下载失败：" + ex.Message);
            }
            finally
            {
                _download.Finish();

                UpdateDownloadUi();
            }
        }

        /// <summary>用界面上当前选的文件与模式生成帧（与「生成下载帧」走的是同一套）。</summary>
        private IList<DownloadFrame> BuildDownloadFrames(out DownloadMode mode)
        {
            mode = DownloadTypeFw != null && DownloadTypeFw.IsChecked == true
                ? DownloadMode.Firmware
                : DownloadMode.App;

            if (string.IsNullOrEmpty(_downloadFilePath))
                throw new InvalidOperationException("还没有选择文件。");

            byte[] data = System.IO.File.ReadAllBytes(_downloadFilePath);
            byte seq = Rtl8239Catalog.ParseByte(DlSeqText.Text, "序列号");

            return Rtl8239DownloadPlan.Build(data, seq, mode);
        }

        /// <summary>
        /// 烧录前的确认。这是全工具唯一一个「按错了可能是一台砖」的按钮，
        /// 所以把关键事实摆出来：模式、帧数、以及**它不会替你跳 Loader**。
        /// </summary>
        private bool ConfirmFlash(IList<DownloadFrame> frames, DownloadMode mode)
        {
            string message = string.Format(CultureInfo.InvariantCulture,
                "将要向设备发送 {0} 帧（{1} 模式）。\n\n"
                + "· 这是真的在写设备，不是生成文本。\n"
                + "· 途中失败会立即停止，不会继续往后写。\n"
                + "· 本工具不会自动发送「跳转到 Loader」——那一步要你自己决定什么时候发。\n\n"
                + "确定开始吗？",
                frames.Count,
                mode == DownloadMode.Firmware ? "Firmware (0xCA)" : "App (0xC0-80~83)");

            return MessageBox.Show(this, message, "确认烧录", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK;
        }

        private void OnDownloadProgress(DownloadProgress progress)
        {
            if (Dispatcher.HasShutdownStarted)
                return;

            try
            {
                Dispatcher.Invoke(delegate
                {
                    if (!_download.IsBusy)
                        return;

                    DlStatusText.Foreground = Gray;
                    DlStatusText.Text = progress.Message;
                });
            }
            catch (Exception)
            {
                // 窗口已在关闭途中：进度显示不了了，不影响下载本身
            }
        }

        private void UpdateDownloadUi()
        {
            if (DownloadToDeviceButton == null)
                return;

            if (_download.IsBusy)
            {
                DownloadToDeviceButton.Content = "取消";
                DownloadToDeviceButton.IsEnabled = true;
                return;
            }

            DownloadToDeviceButton.Content = "下载到设备";
            DownloadToDeviceButton.IsEnabled = CanSend();
        }

    }
}
