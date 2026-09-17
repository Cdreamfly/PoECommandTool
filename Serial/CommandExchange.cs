using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PoECommandTool.Serial
{
    /// <summary>一次单命令往返的结果。</summary>
    public sealed class ExchangeResult
    {
        /// <summary>发出去并成功解析回了。</summary>
        public bool Ok { get; set; }

        /// <summary>失败原因是「等不到响应」。有些命令的超时是有歧义的，见 <see cref="CommandKey.EmptyResponseHint"/>。</summary>
        public bool TimedOut { get; set; }

        public string Error { get; set; }

        /// <summary>解析出来的结果对象；失败时为 null。</summary>
        public object Parsed { get; set; }

        /// <summary>收到的原始帧事件（含 Raw / ParseError / Line）；连回包都没收到时为 null。</summary>
        public FrameEvent Frame { get; set; }

        public byte Sequence { get; set; }
        public byte[] Request { get; set; }

        /// <summary>从取锁到拿到回包的耗时。现场排查时要看这个数。</summary>
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// 单命令往返：取事务锁 → 发送 → 等配对响应 → 子命令复核 → 解析。
    ///
    /// 抽出来是因为这套动作现在不止一处要讲，而它有几条规矩**错了不会报错，只会静默显示错值
    /// 或者必然超时**：
    ///
    /// * 事务的 using 必须**同时包住发送与等待**（<see cref="SerialSession.BeginTransactionAsync"/> 上写着
    ///   「只包发送等于没包」）；
    /// * 0xC0 系命令只按命令 ID 配对，必须再比一次子命令，否则会把别人的回包当成自己的；
    /// * 帧里的序列号必须和等响应时用的序列号是同一个，否则回包永远配不上号。
    ///
    /// 复制一份就是等着漂移，所以只留这一份。
    /// </summary>
    public static class CommandExchange
    {
        /// <summary>
        /// 发一帧并等它的响应。**不抛**超时与解析失败——那些是正常结果，装在返回值里。
        /// 只有取消会向上抛 <see cref="OperationCanceledException"/>（调用方要拿它中断整批）。
        /// </summary>
        public static async Task<ExchangeResult> SendAsync(SerialSession session, byte[] frame,
            CommandKey target, byte sequence, SendOptions options, int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (frame == null) throw new ArgumentNullException("frame");
            if (target == null) throw new ArgumentNullException("target");
            if (options == null) throw new ArgumentNullException("options");

            // 帧里的序列号和这里要等的序列号不一致 = 回包永远配不上号，而现场只会看到一句「超时」。
            // 与其让那种错误静默发生，不如立刻炸出来。
            if (target.SequenceCorrelatable && frame.Length > 1 && frame[1] != sequence)
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "帧里的序列号是 0x{0:X2}，但要等的是 0x{1:X2}——这样回包永远配不上号。",
                    frame[1], sequence), "sequence");

            var result = new ExchangeResult();
            result.Sequence = sequence;
            result.Request = frame;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using (SerialTransaction transaction =
                    await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
                {
                    await session.SendAsync(options.For(frame), cancellationToken).ConfigureAwait(false);

                    FrameEvent frameEvent = await session.WaitForFrameAsync(target.CommandId, sequence,
                        TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false);

                    result.Frame = frameEvent;

                    string mismatch = target.DescribeMismatch(frameEvent);
                    if (mismatch != null)
                    {
                        result.Error = mismatch;
                        return result;
                    }

                    if (!frameEvent.IsParsed)
                    {
                        result.Error = "响应解析失败：" + (frameEvent.ParseError ?? "未知原因");
                        return result;
                    }

                    result.Ok = true;
                    result.Parsed = frameEvent.Parsed;
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                throw;      // 取消要中断整批，不能被当成「这一条失败」
            }
            catch (TimeoutException)
            {
                result.TimedOut = true;
                result.Error = "等待响应超时" + CommandKey.EmptyResponseHint(target.CommandId, true);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                stopwatch.Stop();
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            }
        }
    }
}
