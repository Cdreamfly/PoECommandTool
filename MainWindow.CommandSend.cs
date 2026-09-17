using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WpfApp1.Serial;

namespace WpfApp1
{
    /// <summary>
    /// 「命令组装」页的在线半边：串口打开时，把当前选中的命令按「串口读写」页的方式发出去，
    /// 并把回包自动解析出来。
    ///
    /// 串口关着时这一页的行为与从前**逐字节一致**——「发送并解析」那颗按钮是置灰的，
    /// 而「生成命令」走的是同一个 <c>BuildFramePlan</c>。
    ///
    /// 放在单独一个 partial 里，是为了让 MainWindow.xaml.cs 保持「纯离线」：
    /// 那个文件里不该出现 _session / _serialOptions。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>
        /// 一按下去就不可逆、会改变设备状态的命令。这些原本要靠「手工把十六进制抄进串口页」
        /// 才发得出去，现在填完参数就能一键发出，所以必须先问一句。
        /// </summary>
        private static readonly string[] DestructiveSendKeys =
        {
            "0xC0-00",      // 跳转到 Loader——会擦除固件信息
            "0xC0-02",      // 配置信息清除
            "0xC0-05",      // 配置信息复位
            "0x02",         // 全局复位
        };

        private CancellationTokenSource _sendParseCts;
        private Task _sendParseTask;
        private bool _sendParseBusy;

        // =================================================================
        //  触发
        // =================================================================

        /// <summary>发送中这颗按钮是「取消」，空闲时是「发送并解析」。</summary>
        private async void SendParse_Click(object sender, RoutedEventArgs e)
        {
            if (_sendParseBusy)
            {
                CancelCommandSend();
                return;
            }

            await StartCommandSend();
        }

        private Task StartCommandSend()
        {
            if (_sendParseBusy && _sendParseTask != null)
                return _sendParseTask;

            _sendParseTask = RunSendParseAsync();
            return _sendParseTask;
        }

        private async Task RunSendParseAsync()
        {
            if (_sendParseBusy)
                return;

            if (_current == null)
            {
                SendRespBox.Text = "请先在左侧选择一个命令。";
                return;
            }

            if (_session == null || !_session.IsOpen)
            {
                SendRespBox.Text = "串口未打开——「发送并解析」需要先打开串口。"
                    + "串口关着时请用「生成命令」，它只拼帧、不发出去。";
                UpdateCommandSendUi();
                return;
            }

            List<PlannedFrame> plan;
            byte firstSequence;
            try
            {
                firstSequence = Rtl8239Catalog.ParseByte(SeqText.Text, "序列号");
                // 多帧时序列号必须逐帧递增：回包只按「命令号 + Byte1」配对，
                // 同号的话第 3 帧迟到的回包会被第 5 帧的等待认领，把别人的数据显示成自己的。
                plan = BuildFramePlan(firstSequence, true);
            }
            catch (Exception ex)
            {
                SendRespBox.Text = "参数解析失败：" + ex.Message;
                return;
            }

            if (!ConfirmDestructiveSend(plan))
                return;

            _sendParseBusy = true;
            _sendParseCts = new CancellationTokenSource();
            UpdateCommandSendUi();

            int timeoutMs = ResponseTimeoutMs();
            var log = new StringBuilder();
            log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "准备依次发送 {0} 条，每条最多等 {1} ms（最坏约 {2} 秒）；发送过程中随时可以点「取消」。",
                plan.Count, timeoutMs, (long)plan.Count * timeoutMs / 1000));
            if (plan.Count > 1)
                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "多条时序列号自 0x{0:X2} 起逐条递增（否则迟到的回包会被后一条认领）。", firstSequence));
            log.AppendLine();

            int sent = 0;
            int ok = 0;
            try
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    if (_session == null || !_session.IsOpen)
                    {
                        log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "串口已关闭，余下 {0} 条不再发送。", plan.Count - i));
                        break;
                    }

                    PlannedFrame item = plan[i];
                    SendStatusText.Text = string.Format(CultureInfo.InvariantCulture,
                        "发送中 {0}/{1}（等待串口空闲…）", i + 1, plan.Count);

                    AppendLine("[发送] " + DescribeOutgoing(item.Frame));

                    ExchangeResult result = await ExchangeOneAsync(item, timeoutMs);

                    sent++;
                    if (result.Ok)
                        ok++;

                    AppendExchange(log, item, result, plan.Count == 1);
                    SendRespBox.Text = log.ToString();
                    SendRespBox.ScrollToEnd();
                }

                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "—— 共发送 {0} 条，成功 {1} ——", sent, ok));
                SendRespBox.Text = log.ToString();
                AppendLog(string.Format(CultureInfo.InvariantCulture,
                    "[工具] 命令组装：「{0}」发送 {1} 条，成功 {2}。", _current.Name, sent, ok));
            }
            catch (OperationCanceledException)
            {
                log.AppendLine();
                log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "—— 已取消（已发 {0} 条，成功 {1}）——", sent, ok));
                SendRespBox.Text = log.ToString();
                AppendLog("[工具] 命令组装：发送已取消。");
            }
            catch (Exception ex)
            {
                log.AppendLine();
                log.AppendLine("—— 中断：" + ex.Message + " ——");
                SendRespBox.Text = log.ToString();
                AppendLog("[工具] 命令组装：发送中断——" + ex.Message);
            }
            finally
            {
                _sendParseBusy = false;

                if (_sendParseCts != null)
                {
                    _sendParseCts.Dispose();
                    _sendParseCts = null;
                }

                SendStatusText.Text = string.Empty;
                UpdateCommandSendUi();
            }
        }

        /// <summary>
        /// 发一帧并等它的响应。发送方式跟着「串口读写」页当前的设置走（文本模板 / 裸帧直连），
        /// 与手工发送、轮询用的是同一套。
        /// </summary>
        private async Task<ExchangeResult> ExchangeOneAsync(PlannedFrame item, int timeoutMs)
        {
            _serialOptions.Mode = IsRawMode() ? SendMode.RawFrame : SendMode.TextCommand;
            _serialOptions.Template = TemplateBox.Text;
            _serialOptions.LineEnding = SelectedLineEnding();

            CommandKey target = CommandKey.Parse(_current.Key);
            return await CommandExchange.SendAsync(_session, item.Frame, target, item.Sequence,
                _serialOptions, timeoutMs, _sendParseCts.Token);
        }

        // =================================================================
        //  渲染
        // =================================================================

        /// <summary>
        /// 一条往返的呈现。<paramref name="fullDump"/> 为 false 时只给一行摘要——
        /// 48 个端口逐帧铺开全文会刷成一面墙，那时摘要 + 日志里的 [解析] 更合用。
        /// </summary>
        private void AppendExchange(StringBuilder log, PlannedFrame item, ExchangeResult result, bool fullDump)
        {
            log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "端口 0x{0:X2}  序列号 0x{1:X2}  用时 {2} ms",
                item.Port, item.Sequence, result.ElapsedMs));
            log.AppendLine("发送：" + Rtl8239CommandBuilder.ToHex(item.Frame));

            if (result.Frame != null && result.Frame.Raw != null)
                log.AppendLine("识别：" + Rtl8239CommandBuilder.ToHex(result.Frame.Raw));

            if (!result.Ok)
            {
                log.AppendLine("结果：" + result.Error);
                log.AppendLine();
                return;
            }

            string tag = string.Format(CultureInfo.InvariantCulture, "0x{0:X2}", result.Frame.CommandId)
                + (item.HasPort ? " 端口" + item.Port : string.Empty);

            string summary = ResponseSummarizer.Summarize(result.Parsed, tag);
            if (summary != null)
                log.AppendLine(summary);

            string echo = CheckSendEcho(result.Frame, item);
            if (echo != null)
                log.AppendLine(echo);

            if (fullDump)
                log.AppendLine(FormatObject(result.Parsed));

            log.AppendLine();
        }

        /// <summary>
        /// 核对回包是不是真属于我们刚发出去的那一帧。
        ///
        /// 比「响应解析」页那两个手填的期望值框更硬：期望的序列号与端口都是从**实际发出的帧**来的。
        /// 规则在 <see cref="ResponseEchoCheck"/> 里——「响应解析」页走的是同一份，不该各写一套。
        /// </summary>
        private static string CheckSendEcho(FrameEvent frame, PlannedFrame item)
        {
            if (frame == null)
                return null;

            string notes = ResponseEchoCheck.Describe(ResponseEchoCheck.NotesForSent(
                frame.Raw, item.Sequence, item.HasPort ? (int?)item.Port : null));

            return notes.Length == 0 ? null : notes;
        }

        // =================================================================
        //  破坏性命令确认
        // =================================================================

        private bool ConfirmDestructiveSend(List<PlannedFrame> plan)
        {
            if (_current == null || !IsDestructiveSend(_current.Key))
                return true;

            var frames = new StringBuilder();
            int shown = plan.Count < 3 ? plan.Count : 3;
            for (int i = 0; i < shown; i++)
                frames.AppendLine("    " + Rtl8239CommandBuilder.ToHex(plan[i].Frame));
            if (plan.Count > shown)
                frames.AppendLine(string.Format(CultureInfo.InvariantCulture, "    ……另有 {0} 条", plan.Count - shown));

            string message = string.Format(CultureInfo.InvariantCulture,
                "「{0}」（{1}）会改变设备状态，且不可撤销。\n\n将要发送 {2} 条：\n{3}\n确定发送吗？",
                _current.Name, _current.Key, plan.Count, frames);

            return MessageBox.Show(this, message, "确认发送", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK;
        }

        private static bool IsDestructiveSend(string key)
        {
            for (int i = 0; i < DestructiveSendKeys.Length; i++)
                if (string.Equals(DestructiveSendKeys[i], key, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        // =================================================================
        //  状态与收尾
        // =================================================================

        private void UpdateCommandSendUi()
        {
            if (SendParseButton == null)
                return;

            if (_sendParseBusy)
            {
                SendParseButton.Content = "取消";
                SendParseButton.IsEnabled = true;
                UpdatePollStartButton();
                return;
            }

            bool open = _session != null && _session.IsOpen;
            SendParseButton.Content = "发送并解析";
            // 轮询中**不**禁用：单条发送很短，而轮询每条命令之间有空隙，很快能插进去，
            // 排队期间状态栏会写着「等待串口空闲…」。
            // 设备信息读取中则禁用——它要连读六条命令，插进去要干等很久。
            SendParseButton.IsEnabled = open && !_deviceInfoBusy;
            UpdatePollStartButton();
        }

        /// <summary>通知发送停下（不等待）。要先于关串口发出，否则它会在已关闭的端口上等到超时。</summary>
        private void CancelCommandSend()
        {
            CancellationTokenSource cts = _sendParseCts;
            if (cts != null)
                cts.Cancel();
        }

        /// <summary>等发送退干净（异常不外抛），带超时。</summary>
        private async Task AwaitCommandSendStoppedAsync()
        {
            Task task = _sendParseTask;
            if (task == null)
                return;

            try
            {
                await Task.WhenAny(task, Task.Delay(DeviceInfoStopTimeoutMs));
            }
            catch (Exception)
            {
                // 关闭流程里不往外抛
            }
        }
    }
}
