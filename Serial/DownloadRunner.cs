using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PoECommandTool.Serial
{
    /// <summary>一次下载的进度快照。</summary>
    public sealed class DownloadProgress
    {
        public int Confirmed { get; set; }      // 已被设备确认的帧数
        public int Total { get; set; }
        public int ImageOffset { get; set; }    // 刚确认的镜像偏移
        public string Message { get; set; }
    }

    /// <summary>一次下载的结果。</summary>
    public sealed class DownloadResult
    {
        public bool Ok { get; set; }
        public int Confirmed { get; set; }
        public int Total { get; set; }
        public string Error { get; set; }

        /// <summary>失败时停在哪一帧的镜像偏移；没失败为 -1。</summary>
        public int FailedOffset { get; set; }
    }

    /// <summary>
    /// 固件下载的发送循环：逐帧发 → 等应答 → 核对回显偏移 → 重试。
    ///
    /// **为什么核对偏移是这里唯一的校验手段**：Loader 应答是 4 字节、**没有校验和**
    /// （手册与 <see cref="Rtl8239ResponseParser.ParseDownloadAck"/> 都写明），
    /// 它能提供的信息只有「命令 ID + 子命令/序列号 + 镜像偏移」。而后两者里，
    /// 偏移正是我们刚发出去的那一帧的偏移——对得上才算这一帧被设备收下了。
    /// 不核对的话，一个迟到的、属于上一帧的应答会被当成当前帧的成功。
    ///
    /// **失败即停**：继续往下发会把后续数据写到错误的偏移上，那比停下来糟得多。
    ///
    /// 下载期间 <see cref="SerialSession.LoaderAckMode"/> 置为 true，**无论成败都在
    /// finally 里置回**——留在 true 的话，之后的正常通信会被当成 4 字节应答拆散。
    /// </summary>
    public sealed class DownloadRunner
    {
        private readonly SerialSession _session;

        /// <summary>进度回调。**在哪个线程上触发不保证**（本类内部一路 ConfigureAwait(false)）。</summary>
        public Action<DownloadProgress> Progress { get; set; }

        public DownloadRunner(SerialSession session)
        {
            if (session == null) throw new ArgumentNullException("session");
            _session = session;
        }

        public async Task<DownloadResult> RunAsync(IList<DownloadFrame> frames, SendOptions options,
            int timeoutMs, int maxRetries, CancellationToken cancellationToken)
        {
            if (frames == null) throw new ArgumentNullException("frames");
            if (options == null) throw new ArgumentNullException("options");
            if (maxRetries < 0) maxRetries = 0;

            var result = new DownloadResult();
            result.Total = frames.Count;
            result.FailedOffset = -1;

            if (frames.Count == 0)
            {
                result.Ok = true;
                return result;
            }

            bool ackModeWas = _session.LoaderAckMode;
            _session.LoaderAckMode = true;

            try
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    DownloadFrame frame = frames[i];

                    string failure = await SendWithRetriesAsync(frame, options, timeoutMs, maxRetries,
                        cancellationToken).ConfigureAwait(false);

                    if (failure != null)
                    {
                        result.Ok = false;
                        result.Confirmed = i;
                        result.FailedOffset = frame.ImageOffset;
                        result.Error = string.Format(CultureInfo.InvariantCulture,
                            "第 {0}/{1} 帧（镜像偏移 0x{2:X4}）失败：{3}",
                            i + 1, frames.Count, frame.ImageOffset, failure);
                        return result;
                    }

                    result.Confirmed = i + 1;
                    ReportProgress(i + 1, frames.Count, frame.ImageOffset);
                }

                result.Ok = true;
                return result;
            }
            finally
            {
                _session.LoaderAckMode = ackModeWas;
            }
        }

        /// <summary>发一帧并等它的应答，失败就重发；成功返回 null，否则返回最后一次的失败原因。</summary>
        private async Task<string> SendWithRetriesAsync(DownloadFrame frame, SendOptions options,
            int timeoutMs, int maxRetries, CancellationToken cancellationToken)
        {
            string failure = null;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // 事务锁包住「发送 + 等待」——只包发送等于没包
                    using (SerialTransaction transaction =
                        await _session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await _session.SendAsync(options.For(frame.Bytes), cancellationToken).ConfigureAwait(false);

                        FrameEvent frameEvent = await _session.WaitForFrameAsync(
                            frame.Bytes[0], frame.Bytes[1],
                            TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false);

                        string mismatch = DescribeAckMismatch(frame, frameEvent);
                        if (mismatch == null)
                            return null;

                        failure = mismatch;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    failure = "等待应答超时";
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }
            }

            return failure;
        }

        /// <summary>
        /// 应答是不是真属于这一帧。返回 null 表示对上了。
        ///
        /// 核对三项：命令 ID、子命令/序列号、**回显的镜像偏移**。前两项只能挡住串到别的
        /// 命令的应答；只有偏移能挡住「上一帧的应答迟到」——那是最容易发生、也最要命的
        /// 一种错配（会让这一帧的失败被记成成功）。
        /// </summary>
        private static string DescribeAckMismatch(DownloadFrame frame, FrameEvent frameEvent)
        {
            if (frameEvent == null || frameEvent.Raw == null || frameEvent.Raw.Length < 4)
                return "应答帧不足 4 字节";

            byte[] raw = frameEvent.Raw;

            if (raw[0] != frame.Bytes[0])
                return string.Format(CultureInfo.InvariantCulture,
                    "应答命令不符：期望 0x{0:X2}，实际 0x{1:X2}", frame.Bytes[0], raw[0]);

            if (raw[1] != frame.Bytes[1])
                return string.Format(CultureInfo.InvariantCulture,
                    "应答子命令/序列号不符：期望 0x{0:X2}，实际 0x{1:X2}", frame.Bytes[1], raw[1]);

            // 请求里的偏移是小端（见 PutUInt16LE），应答里同样是
            int echoed = raw[2] | (raw[3] << 8);
            if (echoed != frame.ImageOffset)
                return string.Format(CultureInfo.InvariantCulture,
                    "应答回显的镜像偏移不符：期望 0x{0:X4}，实际 0x{1:X4}（多半是上一帧的应答迟到了）",
                    frame.ImageOffset, echoed);

            return null;
        }

        private void ReportProgress(int confirmed, int total, int imageOffset)
        {
            Action<DownloadProgress> handler = Progress;
            if (handler == null)
                return;

            var report = new DownloadProgress();
            report.Confirmed = confirmed;
            report.Total = total;
            report.ImageOffset = imageOffset;
            report.Message = string.Format(CultureInfo.InvariantCulture,
                "已确认 {0}/{1} 帧（镜像偏移 0x{2:X4}）", confirmed, total, imageOffset);
            handler(report);
        }
    }
}
