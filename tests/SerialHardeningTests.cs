using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1;
using WpfApp1.Serial;

namespace Rtl8239Verify
{
    /// <summary>
    /// 单线程消息泵，模拟 WPF 的 SynchronizationContext。
    /// 用它验证「轮询事件必须回到调用方线程」——界面正是靠这一点直接在事件处理器里改控件的。
    /// </summary>
    internal sealed class SingleThreadPump
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;

        public SingleThreadPump()
        {
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        public int ThreadId
        {
            get { return _thread.ManagedThreadId; }
        }

        public void Post(Action action)
        {
            _queue.Add(action);
        }

        public void Stop()
        {
            _queue.CompleteAdding();
        }

        private void Loop()
        {
            SynchronizationContext.SetSynchronizationContext(new PumpContext(_queue));
            foreach (Action action in _queue.GetConsumingEnumerable())
                action();
        }

        private sealed class PumpContext : SynchronizationContext
        {
            private readonly BlockingCollection<Action> _queue;

            public PumpContext(BlockingCollection<Action> queue)
            {
                _queue = queue;
            }

            public override void Post(SendOrPostCallback callback, object state)
            {
                _queue.Add(delegate { callback(state); });
            }

            public override void Send(SendOrPostCallback callback, object state)
            {
                callback(state);
            }
        }
    }

    internal static partial class Program
    {
        /// <summary>
        /// 把一段异步代码放到线程泵上跑；返回它在泵上跑出来的异常（正常结束返回 null）。
        /// 注意不能在泵上同步阻塞等待它——那样续体没机会执行，会自己把自己锁死。
        /// </summary>
        private static Exception RunOnPump(Func<Task> action, int timeoutMs)
        {
            var pump = new SingleThreadPump();
            var done = new ManualResetEventSlim(false);
            Exception error = null;

            pump.Post(delegate
            {
                try
                {
                    Task task = action();
                    task.ContinueWith(delegate(Task finished)
                    {
                        if (finished.Exception != null)
                            error = finished.Exception.GetBaseException();
                        done.Set();
                    }, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    error = ex;
                    done.Set();
                }
            });

            bool finishedInTime = done.Wait(timeoutMs);
            pump.Stop();
            if (!finishedInTime && error == null)
                error = new TimeoutException("线程泵在 " + timeoutMs + " ms 内没有跑完。");

            return error;
        }

        /// <summary>按设备口吻生成一行响应日志（声明字节数 + 十六进制）。</summary>
        private static string ReceivedLine(byte[] frame)
        {
            return "[UART_TEST] received " + frame.Length + " bytes: "
                + Rtl8239CommandBuilder.ToHex(frame) + "\r\n";
        }

        private static async Task SerialHardeningTests()
        {
            Console.WriteLine("串口加固（按审查发现的问题逐条验证）");

            byte[] request = Frame("42 01 00 FF FF FF FF FF FF FF FF");

            // --- C1：轮询事件必须回到调用方线程（否则事件处理器一改控件就抛跨线程异常）---
            var offThreadEvents = new List<string>();
            var failures = new List<string>();
            Exception pumpError = RunOnPump(async delegate
            {
                var clock = new FakeClock();
                var fake = new FakeSerialTransport();
                fake.AutoReply = ReplyFor;

                using (var session = new SerialSession(fake))     // 注意：using 必须覆盖下面的 await
                {
                    session.ReceiveMode = ReceiveMode.RawFrames;
                    session.Open(TestSettings());

                    var runner = new PollingRunner(session, clock.Read,
                        delegate(TimeSpan span, CancellationToken token)
                        {
                            clock.Advance(span.TotalMilliseconds);
                            return Task.CompletedTask;
                        });

                    int pumpThread = Thread.CurrentThread.ManagedThreadId;
                    var cts = new CancellationTokenSource();
                    int matched = 0;

                    runner.RequestSent += delegate
                    {
                        if (Thread.CurrentThread.ManagedThreadId != pumpThread)
                            offThreadEvents.Add("RequestSent");
                    };
                    runner.ResponseMatched += delegate
                    {
                        if (Thread.CurrentThread.ManagedThreadId != pumpThread)
                            offThreadEvents.Add("ResponseMatched");
                        if (Interlocked.Increment(ref matched) >= 2)
                            cts.Cancel();
                    };
                    runner.RequestFailed += delegate(PollRequest failedRequest, string reason)
                    {
                        if (Thread.CurrentThread.ManagedThreadId != pumpThread)
                            offThreadEvents.Add("RequestFailed");
                        lock (failures) failures.Add(reason);
                    };

                    var plan = new PollingPlan { IntervalMs = 0, ResponseTimeoutMs = 200, InterCommandDelayMs = 0 };
                    plan.Items.Add(Item("0x42", 0x00));
                    await runner.RunAsync(plan, new SendOptions { Mode = SendMode.RawFrame }, cts.Token);
                }
            }, 8000);

            Check(pumpError == null, "带同步上下文的轮询能正常跑完：" + Describe(pumpError));
            lock (failures)
            {
                CheckEq(failures.Count, 0, "轮询过程中没有失败：" + (failures.Count > 0 ? failures[0] : "-"));
            }
            CheckEq(offThreadEvents.Count, 0,
                "轮询事件都回到了调用方线程（界面才能直接改控件）");

            // --- H1：超时的等待者必须被清理，否则序列号回绕后它会吃掉别人的响应 ---
            var timeoutFake = new FakeSerialTransport();
            using (var session = new SerialSession(timeoutFake))
            {
                session.Open(TestSettings());

                Exception timeout = await CaughtAsync(delegate
                {
                    return session.WaitForFrameAsync(0x42, 0x05, TimeSpan.FromMilliseconds(60), CancellationToken.None);
                });
                Check(timeout is TimeoutException, "第一次等待如约超时");

                // 超时之后，同一命令+序列号的帧必须还能被「新的等待者」拿到
                byte[] lateFrame = Sign(new byte[] { 0x42, 0x05, 0x00, 0x02, 0x44, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF });
                timeoutFake.FeedText(ReceivedLine(lateFrame));
                FrameEvent frame = await session.WaitForFrameAsync(0x42, 0x05,
                    TimeSpan.FromMilliseconds(800), CancellationToken.None);
                Check(frame != null && frame.CommandId == 0x42,
                    "超时的等待者已被清理，同序列号的响应不会被它吞掉");
            }

            // --- H2：设备把我们的请求原样回送时，不能当成响应 ---
            var echoFake = new FakeSerialTransport();
            using (var session = new SerialSession(echoFake))
            {
                session.Open(TestSettings());
                await session.SendAsync(SendRequest.Raw(request), CancellationToken.None);

                _sessionFrames = 0;
                session.FrameReceived += delegate { _sessionFrames++; };

                echoFake.FeedText(ReceivedLine(request));
                Thread.Sleep(200);

                CheckEq(_sessionFrames, 0, "与刚发出的请求逐字节相同的帧不会被当成响应（文本路径）");
                Check(session.SelfEchoIgnored >= 1, "自回显被计数，便于排查");

                byte[] realResponse = Sign(new byte[] { 0x42, 0x01, 0x00, 0x02, 0x44, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF });
                echoFake.FeedText(ReceivedLine(realResponse));
                Check(WaitFor(delegate { return _sessionFrames == 1; }, 2000), "真正的响应仍然正常收下");
            }

            // --- H3：裸帧模式同样拒绝自回显 ---
            var rawEchoFake = new FakeSerialTransport();
            using (var session = new SerialSession(rawEchoFake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());
                await session.SendAsync(SendRequest.Raw(request), CancellationToken.None);

                _sessionFrames = 0;
                session.FrameReceived += delegate { _sessionFrames++; };

                rawEchoFake.Feed(request);
                Thread.Sleep(200);
                CheckEq(_sessionFrames, 0, "裸帧路径也不会把自回显当成响应");

                rawEchoFake.Feed(ReplyFor(request));
                Check(WaitFor(delegate { return _sessionFrames == 1; }, 2000), "裸帧路径的真正响应正常收下");
            }

            // --- 识别「看着像但没采纳」的行要能被界面看到（排查「识别不对」）---
            var rejectFake = new FakeSerialTransport();
            using (var session = new SerialSession(rejectFake))
            {
                session.Open(TestSettings());
                var rejections = new List<string>();
                session.LineRejected += delegate(string line, string reason)
                {
                    lock (rejections) rejections.Add(reason);
                };

                rejectFake.FeedText("debug#\r\n");
                rejectFake.FeedText("Command not recognised.  Enter \"help\" to view a list of available commands.\r\n");
                Thread.Sleep(200);
                lock (rejections)
                    CheckEq(rejections.Count, 0, "控制台的普通输出不会报「未采纳」");

                rejectFake.FeedText("[UART_TEST] received 12 bytes: 42 01 00 01 06 00 00 00 00 00 00\r\n");  // 声明 12、实际 11
                Check(WaitFor(delegate { lock (rejections) return rejections.Count == 1; }, 2000),
                    "「声明 12 实际 11」这种行会报成未采纳");
                lock (rejections)
                    Check(rejections.Count > 0 && rejections[0].Contains("11"),
                        "未采纳的原因里有实际字节数：" + (rejections.Count > 0 ? rejections[0] : "-"));
            }

            // --- M1：写失败会把会话标记成关闭（否则轮询无限重试刷屏）---
            var writeFake = new FakeSerialTransport();
            var writeFaults = new List<SerialFault>();
            using (var session = new SerialSession(writeFake))
            {
                session.Fault += delegate(SerialFault f) { lock (writeFaults) writeFaults.Add(f); };
                session.Open(TestSettings());
                writeFake.ThrowOnWrite = true;

                Exception failed = await CaughtAsync(delegate
                {
                    return session.SendAsync(SendRequest.Raw(request), CancellationToken.None);
                });
                Check(failed != null, "写失败会抛给调用方");
                lock (writeFaults)
                {
                    CheckEq(writeFaults.Count, 1, "上报一次写故障");
                    CheckEq(writeFaults[0].Kind, SerialFaultKind.WriteFailed, "故障类型是写失败");
                }
                Check(!session.IsOpen, "写失败后会话标记为已关闭");
            }

            // --- M4：读故障之后再打开，要能正常工作（也不能泄漏取消源）---
            var reopenFake = new FakeSerialTransport();
            var reopenFaults = new List<SerialFault>();
            using (var session = new SerialSession(reopenFake))
            {
                session.Fault += delegate(SerialFault f) { lock (reopenFaults) reopenFaults.Add(f); };
                session.Open(TestSettings());
                reopenFake.ThrowOnRead = true;
                Check(WaitFor(delegate { lock (reopenFaults) return reopenFaults.Count > 0; }, 2000), "读故障已上报");
                Check(!session.IsOpen, "读故障后会话关闭");

                reopenFake.ThrowOnRead = false;
                session.Open(TestSettings());
                Check(session.IsOpen, "读故障之后可以重新打开");
                await session.SendAsync(SendRequest.Raw(request), CancellationToken.None);
                CheckEq(Rtl8239CommandBuilder.ToHex(reopenFake.WrittenBytes), Rtl8239CommandBuilder.ToHex(request),
                    "重开之后照常能发出命令");
                session.Close();
            }

            Console.WriteLine();
        }
    }
}
