using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1;
using WpfApp1.Serial;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        private static void CheckNear(double actual, double expected, double tolerance, string name)
        {
            if (Math.Abs(actual - expected) <= tolerance) { _passed++; Console.WriteLine("  PASS  " + name); }
            else { _failed++; Console.WriteLine("  FAIL  " + name + "  expected≈[" + expected + "] actual=[" + actual + "]"); }
        }

        // ---------------- 假时钟 ----------------
        private sealed class FakeClock
        {
            public DateTime Now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            public DateTime Read() { return Now; }
            public void Advance(double ms) { Now = Now.AddMilliseconds(ms); }
        }

        private static PollItem Item(string key, byte port)
        {
            foreach (CommandDef cmd in Rtl8239Catalog.All)
            {
                if (cmd.Key == key)
                    return new PollItem { Command = cmd, Port = port, Enabled = true };
            }
            throw new Exception("目录里没有命令 " + key);
        }

        // ---------------- 候选清单 ----------------
        private static void PollPlanTests()
        {
            Console.WriteLine("PollPlan");

            List<PollItem> items = PollPlan.BuildCandidates();
            CheckEq(items.Count, 8, "可轮询的查询命令共 8 条（信息类 0x40/0x47/0x4A/0x4C/0x50 归「设备信息」面板）");

            var keys = new List<string>();
            int defaultOn = 0;
            foreach (PollItem item in items)
            {
                keys.Add(item.Command.Key);
                if (item.Enabled) defaultOn++;
            }
            CheckEq(defaultOn, PollPlan.DefaultEnabledKeys.Length, "默认勾选数 = 声明的默认集合");
            Check(keys.Contains("0x42") && keys.Contains("0x44") && keys.Contains("0x4F") && keys.Contains("0x41"),
                "0x41/0x42/0x44/0x4F 都在候选里");
            Check(!keys.Contains("0x43") && !keys.Contains("0x45") && !keys.Contains("0x46")
                  && !keys.Contains("0x47") && !keys.Contains("0x4B") && !keys.Contains("0x4C"),
                "需要额外参数的命令不进候选（免得把组索引/标志当成端口发出去）");
            Check(!keys.Contains("0x40") && !keys.Contains("0x4A") && !keys.Contains("0x50"),
                "信息类命令不进候选（改由「设备信息」面板手工读取）");
            Check(!keys.Contains("0x0C") && !keys.Contains("0x01"), "配置写命令永远不会被轮询");

            // 轮询帧必须与「命令组装」页手工生成的帧逐字节一致
            PollItem measurement = Item("0x44", 0x03);
            CheckEq(Rtl8239CommandBuilder.ToHex(PollPlan.BuildFrame(measurement, 0x07)),
                Rtl8239CommandBuilder.ToHex(Rtl8239CommandBuilder.PortMeasurementGet(0x07, 0x03)),
                "0x44 @ 端口3 的帧与手工生成一致");

            PollItem power = Item("0x41", 0x00);
            CheckEq(Rtl8239CommandBuilder.ToHex(PollPlan.BuildFrame(power, 0x09)),
                Rtl8239CommandBuilder.ToHex(Rtl8239CommandBuilder.GlobalPowerStatusGet(0x09)),
                "0x41（无端口）的帧与手工生成一致");

            foreach (PollItem item in items)
            {
                byte[] frame = PollPlan.BuildFrame(item, 0x01);
                if (frame.Length != 12 || !Rtl8239CommandBuilder.IsChecksumValid(frame, 12))
                {
                    Check(false, item.Command.Key + " 生成的帧不合法");
                    break;
                }
            }
            Check(true, "所有候选命令生成的帧都是 12 字节且校验和正确");
            Console.WriteLine();
        }

        // ---------------- 调度状态机 ----------------
        private static void PollingSchedulerTests()
        {
            Console.WriteLine("PollingScheduler");
            var clock = new FakeClock();

            // --- 顺序 / 序列号 / 轮次 ---
            var plan = new PollingPlan { IntervalMs = 1000, InterCommandDelayMs = 30, MaxRetries = 2 };
            plan.Items.Add(Item("0x42", 0x00));
            plan.Items.Add(Item("0x44", 0x00));
            var sch = new PollingScheduler(plan, clock.Read);

            PollStep s1 = sch.Next();
            CheckEq(s1.Kind, PollStepKind.Send, "第一步是发送");
            CheckEq(s1.Request.CommandId, (byte)0x42, "先发第一条");
            CheckEq(s1.Request.Sequence, (byte)0x01, "序列号从 1 开始");
            CheckEq(s1.Request.Attempt, 1, "第 1 次尝试");
            sch.OnResponse(clock.Read());

            PollStep s2 = sch.Next();
            CheckEq(s2.Kind, PollStepKind.Wait, "条间间隔未到要等");
            clock.Advance(30);
            s2 = sch.Next();
            CheckEq(s2.Kind, PollStepKind.Send, "间隔到点后继续");
            CheckEq(s2.Request.CommandId, (byte)0x44, "第二条");
            CheckEq(s2.Request.Sequence, (byte)0x02, "序列号递增");
            sch.OnResponse(clock.Read());

            PollStep s3 = sch.Next();
            CheckEq(s3.Kind, PollStepKind.Wait, "一轮结束要等轮次间隔");
            CheckEq(sch.RoundsCompleted, 1, "已跑完 1 轮");
            clock.Advance(1000);
            s3 = sch.Next();
            CheckEq(s3.Kind, PollStepKind.Send, "轮次间隔到点后开新一轮");
            CheckEq(s3.Request.CommandId, (byte)0x42, "回到第一条");
            CheckEq(s3.Request.Sequence, (byte)0x03, "序列号继续递增");

            // --- 在途未结算时禁止再取 ---
            Check(ThrownBy(delegate { sch.Next(); }).Contains("结算"), "在途未结算时取下一步会被拒绝");
            sch.OnResponse(clock.Read());

            // --- 序列号回绕，且永远不为 0 ---
            var wrapPlan = new PollingPlan { IntervalMs = 0, InterCommandDelayMs = 0 };
            wrapPlan.Items.Add(Item("0x42", 0x00));
            var wrapSch = new PollingScheduler(wrapPlan, clock.Read);
            bool sawZero = false;
            byte last = 0;
            bool sawWrap = false;
            for (int i = 0; i < 300; i++)
            {
                PollStep step = wrapSch.Next();
                if (step.Kind != PollStepKind.Send) { clock.Advance(1); continue; }
                if (step.Request.Sequence == 0) sawZero = true;
                if (last == 0xFF && step.Request.Sequence == 0x01) sawWrap = true;
                last = step.Request.Sequence;
                wrapSch.OnResponse(clock.Read());
            }
            Check(!sawZero, "序列号不会出现 0");
            Check(sawWrap, "序列号到 0xFF 后回绕到 1");

            // --- 超时重发：换新序列号，用尽后跳过 ---
            var retryPlan = new PollingPlan { IntervalMs = 0, InterCommandDelayMs = 0, MaxRetries = 2 };
            retryPlan.Items.Add(Item("0x42", 0x00));
            retryPlan.Items.Add(Item("0x44", 0x00));
            var retrySch = new PollingScheduler(retryPlan, clock.Read);

            PollStep r1 = retrySch.Next();
            CheckEq(r1.Request.CommandId, (byte)0x42, "第一条开始发");
            retrySch.OnAttemptFailed(clock.Read());
            PollStep r2 = retrySch.Next();
            CheckEq(r2.Request.CommandId, (byte)0x42, "失败后重发同一条");
            Check(r2.Request.Sequence != r1.Request.Sequence, "重发换了新的序列号（防陈旧回包）");
            CheckEq(r2.Request.Attempt, 2, "第 2 次尝试");
            retrySch.OnAttemptFailed(clock.Read());
            PollStep r3 = retrySch.Next();
            CheckEq(r3.Request.Attempt, 3, "第 3 次尝试");
            Check(r3.Request.Sequence != r2.Request.Sequence, "第三次尝试又是新的序列号");

            string notice = retrySch.OnAttemptFailed(clock.Read());   // 第 3 次失败 > MaxRetries(2)
            Check(notice != null && notice.Contains("跳过"), "连发无响应后给出跳过提示：" + notice);

            PollStep r4 = retrySch.Next();
            CheckEq(r4.Kind, PollStepKind.Send, "跳过没响应的命令，继续下一条");
            CheckEq(r4.Request.CommandId, (byte)0x44, "后面那条没受影响");
            CheckEq(r4.Request.Attempt, 1, "下一条从第 1 次尝试开始");

            // --- 空计划 ---
            var emptySch = new PollingScheduler(new PollingPlan(), clock.Read);
            CheckEq(emptySch.Next().Kind, PollStepKind.Finished, "没有勾选任何命令时立即结束");
            Console.WriteLine();
        }

        // ---------------- 调度器 + 会话（假传输） ----------------
        private static byte[] Sign(byte[] head11)
        {
            var frame = new byte[12];
            Array.Copy(head11, frame, 11);
            frame[11] = Rtl8239CommandBuilder.Checksum(frame, 11);
            return frame;
        }

        // 按请求命令造一个合法的响应帧（裸帧直连场景）
        private static byte[] ReplyFor(byte[] request)
        {
            if (request.Length != 12)
                return null;

            if (request[0] == 0x42)   // 端口状态：供电中 + Class4 + 有效 PD + 2-pair
                return Sign(new byte[] { 0x42, request[1], request[2], 0x02, 0x44, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF });

            if (request[0] == 0x44)   // 端口测量：电压 0x00C8(200)、电流 0x0080(128)、温度 0x00C8(200)、功率 0x012C(300)
                return Sign(new byte[] { 0x44, request[1], request[2], 0x00, 0xC8, 0x00, 0x80, 0x00, 0xC8, 0x01, 0x2C });

            return null;
        }

        private static PollingPlan TwoCommandPlan(int intervalMs, int timeoutMs, int maxRetries)
        {
            var plan = new PollingPlan
            {
                IntervalMs = intervalMs,
                ResponseTimeoutMs = timeoutMs,
                MaxRetries = maxRetries,
                InterCommandDelayMs = 0,
            };
            plan.Items.Add(Item("0x42", 0x00));
            plan.Items.Add(Item("0x44", 0x00));
            return plan;
        }

        private static List<byte[]> SplitFrames(byte[] written)
        {
            var frames = new List<byte[]>();
            for (int i = 0; i + 12 <= written.Length; i += 12)
            {
                var frame = new byte[12];
                Array.Copy(written, i, frame, 0, 12);
                frames.Add(frame);
            }
            return frames;
        }

        private static async Task PollingRunnerTests()
        {
            Console.WriteLine("PollingRunner");

            // --- 2 条命令 × 3 轮：断言写出的帧序列精确无误 ---
            var clock = new FakeClock();
            var fake = new FakeSerialTransport();
            fake.AutoReply = ReplyFor;

            using (var session = new SerialSession(fake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var options = new SendOptions { Mode = SendMode.RawFrame };
                var runner = new PollingRunner(session, clock.Read,
                    delegate(TimeSpan span, CancellationToken token) { clock.Advance(span.TotalMilliseconds); return Task.CompletedTask; });

                var cts = new CancellationTokenSource();
                int matched = 0;
                int parsed = 0;
                runner.ResponseMatched += delegate(PollRequest request, FrameEvent frameEvent)
                {
                    if (frameEvent.IsParsed) Interlocked.Increment(ref parsed);
                    if (Interlocked.Increment(ref matched) >= 6) cts.Cancel();
                };

                await runner.RunAsync(TwoCommandPlan(0, 200, 2), options, cts.Token);

                CheckEq(matched, 6, "2 条命令 × 3 轮共收到 6 个响应");
                CheckEq(parsed, 6, "6 个响应都解析成功");

                List<byte[]> frames = SplitFrames(fake.WrittenBytes);
                CheckEq(frames.Count, 6, "恰好写出 6 帧");
                var expected = new[]
                {
                    new byte[] { 0x42, 0x01 }, new byte[] { 0x44, 0x02 },
                    new byte[] { 0x42, 0x03 }, new byte[] { 0x44, 0x04 },
                    new byte[] { 0x42, 0x05 }, new byte[] { 0x44, 0x06 },
                };
                bool orderOk = frames.Count == expected.Length;
                for (int i = 0; orderOk && i < expected.Length; i++)
                    orderOk = frames[i][0] == expected[i][0] && frames[i][1] == expected[i][1];
                Check(orderOk, "按轮次依次发送，序列号连续不重复");

                // 解析出来的确实是测量值
                var measurement = Rtl8239ResponseParser.ParsePortMeasurement(Sign(
                    new byte[] { 0x44, 0x01, 0x00, 0x00, 0xC8, 0x00, 0x80, 0x00, 0xC8, 0x01, 0x2C }));
                CheckNear(measurement.PowerW, 30.0, 1e-9, "0x44 功率 = 30.0 W（原始 0x012C × 0.1）");
                CheckEq(measurement.CurrentMa, 128, "0x44 电流 = 128 mA（原始 0x0080，1mA/LSB）");
                CheckNear(measurement.VoltageMv, 12890.0, 1e-6, "0x44 电压 = 12890 mV（原始 200 × 64.45）");
                CheckNear(measurement.TemperatureC, 25.0, 1e-9, "0x44 温度 = 25 ℃（(200-120)×(-1.25)+125）");
            }

            // --- 一条始终不响应：重试后跳过，另一条不受影响 ---
            var deadClock = new FakeClock();
            var deadFake = new FakeSerialTransport();
            deadFake.AutoReply = delegate(byte[] request)
            {
                return request[0] == 0x44 ? null : ReplyFor(request);   // 0x44 永远不回
            };

            using (var session = new SerialSession(deadFake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var notices = new List<string>();
                var options = new SendOptions { Mode = SendMode.RawFrame };
                var runner = new PollingRunner(session, deadClock.Read,
                    delegate(TimeSpan span, CancellationToken token) { deadClock.Advance(span.TotalMilliseconds); return Task.CompletedTask; });
                runner.Notice += delegate(string n) { lock (notices) notices.Add(n); };

                var cts = new CancellationTokenSource();
                int statusResponses = 0;
                runner.ResponseMatched += delegate(PollRequest request, FrameEvent frameEvent)
                {
                    if (request.CommandId == 0x42 && Interlocked.Increment(ref statusResponses) >= 3) cts.Cancel();
                };

                await runner.RunAsync(TwoCommandPlan(0, 60, 1), options, cts.Token);   // MaxRetries=1 -> 每条最多 2 次

                List<byte[]> frames = SplitFrames(deadFake.WrittenBytes);
                int count42 = 0, count44 = 0;
                foreach (byte[] f in frames)
                {
                    if (f[0] == 0x42) count42++;
                    if (f[0] == 0x44) count44++;
                }
                CheckEq(count42, 3, "能响应的 0x42 每轮只发一次（没被别人的重试拖累）");
                CheckEq(statusResponses, 3, "0x42 每轮都收到响应");

                // 前两轮是完整的：每轮 0x42 一次 + 0x44 重试一次后才跳过
                var roundPattern = new byte[] { 0x42, 0x44, 0x44, 0x42, 0x44, 0x44 };
                bool patternOk = frames.Count >= 6;
                for (int i = 0; patternOk && i < roundPattern.Length; i++)
                    patternOk = frames[i][0] == roundPattern[i];
                Check(patternOk, "发送模式：能响应的发一次，没响应的重试一次后跳过");
                CheckEq(count44, 4, "第三轮在发出 0x44 之前就被取消了，所以 0x44 共 4 次");
                lock (notices)
                {
                    bool skipNotice = false;
                    foreach (string n in notices) if (n.Contains("跳过")) skipNotice = true;
                    Check(skipNotice, "连续无响应时给出「跳过」提示");
                }
            }

            // --- 取消后立刻停止，不再写入 ---
            var stopClock = new FakeClock();
            var stopFake = new FakeSerialTransport();
            stopFake.AutoReply = ReplyFor;
            using (var session = new SerialSession(stopFake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var options = new SendOptions { Mode = SendMode.RawFrame };
                var runner = new PollingRunner(session, stopClock.Read,
                    delegate(TimeSpan span, CancellationToken token) { stopClock.Advance(span.TotalMilliseconds); return Task.CompletedTask; });

                var cts = new CancellationTokenSource();
                cts.CancelAfter(120);
                await runner.RunAsync(TwoCommandPlan(0, 150, 2), options, cts.Token);

                int writesAtStop = stopFake.WriteCalls;
                await Task.Delay(150);
                CheckEq(stopFake.WriteCalls, writesAtStop, "取消后不再有任何写入");
            }

            // --- 裸帧模式：帧被拆在两次读之间也能同步出来 ---
            var scannerFake = new FakeSerialTransport();
            using (var session = new SerialSession(scannerFake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                _sessionFrames = 0;
                _sessionLastFrame = null;
                session.FrameReceived += delegate(FrameEvent fe) { _sessionFrames++; _sessionLastFrame = fe; };

                byte[] reply = ReplyFor(Frame("42 07 00 FF FF FF FF FF FF FF FF"));

                scannerFake.Feed(new byte[] { 0xAA, 0x55 });     // 前面混入垃圾字节
                var head = new byte[5];
                Array.Copy(reply, 0, head, 0, head.Length);
                scannerFake.Feed(head);                          // 帧的前半截
                var tail = new byte[7];
                Array.Copy(reply, head.Length, tail, 0, tail.Length);
                scannerFake.Feed(tail);                          // 帧的后半截

                Check(WaitFor(delegate { return _sessionFrames >= 1; }, 2000), "裸帧跨两次读也能同步出来");
                Check(_sessionLastFrame != null && _sessionLastFrame.CommandId == 0x42, "同步出的帧命令正确");
                Check(_sessionLastFrame != null && _sessionLastFrame.IsParsed, "裸帧也自动解析了");
            }

            Console.WriteLine();
        }
    }
}
