using System;
using System.Threading;
using System.Threading.Tasks;
using WpfApp1;
using WpfApp1.Serial;

namespace Rtl8239Verify
{
    /// <summary>
    /// 单命令往返层。设备信息面板现在走的也是这里，所以 <c>DeviceInfoTests</c> 是它的
    /// 端到端回归；这里补的是它自己的契约（返回值、异常、以及那条防御）。
    /// </summary>
    internal static partial class Program
    {
        /// <summary>抛出来的**是不是**取消类异常。看的是类型层次而不是类型名——
        /// TaskCanceledException 也是 OperationCanceledException，契约成立。</summary>
        private static bool ThrewCancellation(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static async Task<ExchangeResult> Exchange(byte[] frame, CommandKey target, byte seq,
            int timeoutMs, CancellationToken ct, FakeSerialTransport fake)
        {
            using (var session = new SerialSession(fake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var options = new SendOptions { Mode = SendMode.RawFrame };
                return await CommandExchange.SendAsync(session, frame, target, seq, options, timeoutMs, ct);
            }
        }

        private static async Task CommandExchangeTests()
        {
            Console.WriteLine("CommandExchange 单命令往返");

            // --- 正常往返：帧、序列号、耗时、解析结果都要交代清楚 ---
            var fake = new FakeSerialTransport();
            fake.AutoReply = delegate(byte[] request)
            {
                return Sign(new byte[] { 0x44, request[1], request[2],
                    0x00, 0xC8, 0x00, 0x80, 0x00, 0xC8, 0x01, 0x2C });
            };

            byte[] frame = Rtl8239CommandBuilder.PortMeasurementGet(0x07, 0x03);
            ExchangeResult ok = await Exchange(frame, CommandKey.Parse("0x44"), 0x07, 1500,
                CancellationToken.None, fake);

            Check(ok.Ok, "正常往返标记成功（" + ok.Error + "）");
            CheckEq(ok.Sequence, (byte)0x07, "结果里带回发出的序列号");
            CheckEq(Rtl8239CommandBuilder.ToHex(ok.Request), Rtl8239CommandBuilder.ToHex(frame),
                "结果里带回发出的帧");
            Check(ok.Frame != null && ok.Frame.IsParsed, "结果里带回收到的帧事件");
            Check(ok.ElapsedMs >= 0, "带回耗时");
            Check(ok.Parsed is PortMeasurement, "解析出的是端口测量结构");
            CheckNear(((PortMeasurement)ok.Parsed).PowerW, 30.0, 1e-9, "数值正确（0x012C × 0.1 = 30 W）");

            // --- 不响应 → 超时，并且**超时的解释已经内置**（不必每个调用方各接一次）---
            var dead = new FakeSerialTransport();
            dead.AutoReply = null;

            ExchangeResult timeout = await Exchange(Rtl8239CommandBuilder.GlobalStatusGet(0x01),
                CommandKey.Parse("0x40"), 0x01, 120, CancellationToken.None, dead);

            Check(!timeout.Ok, "等不到响应时标记失败");
            Check(timeout.TimedOut, "并标出原因是超时");

            // --- 0xC0 系只按命令 ID 配对，子命令复核必须在这里生效 ---
            var wrongSub = new FakeSerialTransport();
            wrongSub.AutoReply = delegate(byte[] request)
            {
                return Sign(new byte[] { 0xC0, 0x01, 0x19, 0x08, 0x19, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF });
            };

            ExchangeResult mismatch = await Exchange(Rtl8239CommandBuilder.ConfigurationVersionGet(),
                CommandKey.Parse("0xC0-04"), 0x03, 300, CancellationToken.None, wrongSub);

            Check(!mismatch.Ok, "子命令对不上时标记失败");
            Check(mismatch.Error.IndexOf("子命令", StringComparison.Ordinal) >= 0,
                "失败原因点名子命令（实际：" + mismatch.Error + "）");

            // --- 防御：帧里的序列号与要等的序列号不一致 = 回包永远配不上号 ---
            byte[] wrongSeqFrame = Rtl8239CommandBuilder.PortMeasurementGet(0x09, 0x00);
            string thrown = ThrownBy(delegate
            {
                Exchange(wrongSeqFrame, CommandKey.Parse("0x44"), 0x07, 300, CancellationToken.None, fake)
                    .GetAwaiter().GetResult();
            });
            Check(thrown.StartsWith("ArgumentException", StringComparison.Ordinal),
                "帧里的序列号与要等的对不上时立刻抛（而不是静静地必然超时）：" + thrown);

            // 0xC0 系不能按序列号配对，所以不该被这条防御误伤
            var c0fake = new FakeSerialTransport();
            c0fake.AutoReply = delegate(byte[] request)
            {
                return Sign(new byte[] { 0xC0, 0x04, 0x19, 0x08, 0x19, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF });
            };
            ExchangeResult c0 = await Exchange(Rtl8239CommandBuilder.ConfigurationVersionGet(),
                CommandKey.Parse("0xC0-04"), 0x03, 300, CancellationToken.None, c0fake);
            Check(c0.Ok, "0xC0 系帧里没有序列号位置，不该被那条防御误伤");

            // --- 取消要往上抛，不能被当成「这一条失败」 ---
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Check(ThrewCancellation(delegate
                {
                    Exchange(frame, CommandKey.Parse("0x44"), 0x07, 1500, cts.Token, fake)
                        .GetAwaiter().GetResult();
                }), "取消向上抛取消类异常（OperationCanceledException 或其子类），整批要能被打断");
            }
        }
    }
}
