using System;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 把轮询调度器接到串口会话上：发帧 → 等回包 → 把结果抛成事件。
    ///
    /// 这是轮询链路里唯一含 await 的地方；所有调度决策都在 <see cref="PollingScheduler"/> 里，
    /// 那个类不碰 IO，可以单独用假时钟测。
    /// </summary>
    public sealed class PollingRunner
    {
        private readonly SerialSession _session;
        private readonly Func<DateTime> _clock;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        public PollingRunner(SerialSession session)
            : this(session, null, null)
        {
        }

        public PollingRunner(SerialSession session, Func<DateTime> clock,
            Func<TimeSpan, CancellationToken, Task> delay)
        {
            if (session == null) throw new ArgumentNullException("session");

            _session = session;
            _clock = clock ?? (() => DateTime.UtcNow);
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
        }

        /// <summary>某一帧已经写进串口。</summary>
        public event Action<PollRequest> RequestSent;

        /// <summary>收到了匹配的响应（<see cref="FrameEvent.IsParsed"/> 为 false 表示解析失败）。</summary>
        public event Action<PollRequest, FrameEvent> ResponseMatched;

        /// <summary>本次尝试失败（超时 / 发送失败）。</summary>
        public event Action<PollRequest, string> RequestFailed;

        /// <summary>需要提示用户的说明（跟不上间隔、连发无响应被跳过、响应解析失败等）。</summary>
        public event Action<string> Notice;

        /// <summary>
        /// 按计划轮询，直到取消或计划里没有可轮询的命令。
        ///
        /// 注意：这里**刻意不写 ConfigureAwait(false)**。事件是在调用方的同步上下文上抛出去的，
        /// 界面就是靠这一点直接在事件处理器里改控件的（改成 ConfigureAwait(false) 会让续体跑到
        /// 线程池上，事件处理器一碰控件就抛跨线程异常）。
        /// </summary>
        public async Task RunAsync(PollingPlan plan, SendOptions options, CancellationToken cancellationToken)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (options == null) throw new ArgumentNullException("options");

            var scheduler = new PollingScheduler(plan, _clock);

            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                PollStep step = scheduler.Next();
                if (step.Kind == PollStepKind.Finished)
                    return;

                if (step.Kind == PollStepKind.Wait)
                {
                    TimeSpan wait = step.DueAt - _clock();
                    if (wait > TimeSpan.Zero)
                        await _delay(wait, cancellationToken);
                    continue;
                }

                PollRequest request = step.Request;
                try
                {
                    await _session.SendAsync(options.For(request.Frame), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    RaiseNotice(scheduler.OnAttemptFailed(_clock()));
                    RaiseRequestFailed(request, "发送失败：" + ex.Message);
                    continue;
                }

                RaiseRequestSent(request);

                try
                {
                    FrameEvent frameEvent = await _session.WaitForFrameAsync(
                        request.CommandId,
                        request.Sequence,
                        TimeSpan.FromMilliseconds(plan.ResponseTimeoutMs),
                        cancellationToken);

                    scheduler.OnResponse(_clock());
                    RaiseResponseMatched(request, frameEvent);

                    if (!frameEvent.IsParsed)
                    {
                        RaiseNotice(string.Format("0x{0:X2} 的响应解析失败：{1}",
                            request.CommandId, frameEvent.ParseError));
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (TimeoutException)
                {
                    RaiseNotice(scheduler.OnAttemptFailed(_clock()));
                    RaiseRequestFailed(request, "等待响应超时");
                }
                catch (Exception ex)
                {
                    RaiseNotice(scheduler.OnAttemptFailed(_clock()));
                    RaiseRequestFailed(request, ex.Message);
                }
            }
        }

        private void RaiseRequestSent(PollRequest request)
        {
            Action<PollRequest> handler = RequestSent;
            if (handler != null)
                handler(request);
        }

        private void RaiseResponseMatched(PollRequest request, FrameEvent frameEvent)
        {
            Action<PollRequest, FrameEvent> handler = ResponseMatched;
            if (handler != null)
                handler(request, frameEvent);
        }

        private void RaiseRequestFailed(PollRequest request, string reason)
        {
            Action<PollRequest, string> handler = RequestFailed;
            if (handler != null)
                handler(request, reason);
        }

        private void RaiseNotice(string notice)
        {
            if (string.IsNullOrEmpty(notice))
                return;

            Action<string> handler = Notice;
            if (handler != null)
                handler(notice);
        }
    }
}
