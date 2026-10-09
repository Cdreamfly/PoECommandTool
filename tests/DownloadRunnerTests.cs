using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PoECommandTool;
using PoECommandTool.Serial;

namespace Rtl8239Verify
{
    /// <summary>
    /// 固件下载的发送循环。
    ///
    /// 这里最要命的一条：Loader 应答**没有校验和**，4 个字节里唯一能证明「这应答是我的」
    /// 就是回显的镜像偏移。不核对的话，一个迟到的、属于上一帧的应答会被记成当前帧成功——
    /// 而后续数据会写到错误的偏移上，那是能写坏 flash 的。
    /// </summary>
    internal static partial class Program
    {
        /// <summary>造一个把请求前 4 字节原样回应的设备——那正是 ACK 的格式。</summary>
        private static byte[] AckFor(byte[] request)
        {
            return new byte[] { request[0], request[1], request[2], request[3] };
        }

        private static IList<DownloadFrame> DownloadFrames(int count)
        {
            var data = new byte[count * 32];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(i & 0xFF);

            return Rtl8239DownloadPlan.Build(data, 0x01, DownloadMode.Firmware);
        }

        private static async Task DownloadRunnerTests()
        {
            Console.WriteLine("DownloadRunner 固件下载发送循环");

            // ---- 正常路径：每帧的应答都回显自己的偏移 ----
            var fake = new FakeSerialTransport();
            fake.AutoReply = AckFor;

            using (var session = new SerialSession(fake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                IList<DownloadFrame> frames = DownloadFrames(3);
                CheckEq(frames.Count, 3, "3 帧测试数据");

                var runner = new DownloadRunner(session);
                var options = new SendOptions { Mode = SendMode.RawFrame };

                int lastConfirmed = 0;
                runner.Progress = delegate(DownloadProgress p) { lastConfirmed = p.Confirmed; };

                DownloadResult result = await runner.RunAsync(frames, options, 500, 0, CancellationToken.None);

                Check(result.Ok, "全部确认时成功（" + result.Error + "）");
                CheckEq(result.Confirmed, 3, "确认了 3 帧");
                CheckEq(lastConfirmed, 3, "进度回调走到最后一帧");
                CheckEq(result.FailedOffset, -1, "成功时没有失败偏移");
            }

            // ---- 应答回显的偏移对不上 → 必须判失败（这是唯一的校验手段）----
            var wrong = new FakeSerialTransport();
            wrong.AutoReply = delegate(byte[] request)
            {
                // 回一个属于**上一帧**的偏移——这正是「迟到的应答」的样子
                return new byte[] { request[0], request[1], (byte)(request[2] + 0x20), request[3] };
            };

            using (var session = new SerialSession(wrong))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                IList<DownloadFrame> frames = DownloadFrames(3);
                var runner = new DownloadRunner(session);
                var options = new SendOptions { Mode = SendMode.RawFrame };

                DownloadResult result = await runner.RunAsync(frames, options, 300, 0, CancellationToken.None);

                Check(!result.Ok, "回显偏移对不上时判失败");
                Check(result.Error != null && result.Error.Contains("镜像偏移不符"),
                    "失败原因点名是偏移不符：" + result.Error);
                CheckEq(result.Confirmed, 0, "第一帧就没通过");
                CheckEq(result.FailedOffset, (int)frames[0].ImageOffset, "报出失败在哪一帧的偏移");
            }

            // ---- 子命令/序列号对不上也要判失败 ----
            var wrongSub = new FakeSerialTransport();
            wrongSub.AutoReply = delegate(byte[] request)
            {
                return new byte[] { request[0], (byte)(request[1] + 1), request[2], request[3] };
            };

            using (var session = new SerialSession(wrongSub))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var runner = new DownloadRunner(session);
                var options = new SendOptions { Mode = SendMode.RawFrame };
                DownloadResult result = await runner.RunAsync(DownloadFrames(2), options, 300, 0, CancellationToken.None);

                Check(!result.Ok && result.Error.Contains("子命令/序列号不符"),
                    "子命令/序列号不符也判失败：" + result.Error);
            }

            // ---- 重试：前两次不回，第三次回 ----
            var flaky = new FakeSerialTransport();
            int attempts = 0;
            flaky.AutoReply = delegate(byte[] request)
            {
                attempts++;
                return attempts < 3 ? null : AckFor(request);
            };

            using (var session = new SerialSession(flaky))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var runner = new DownloadRunner(session);
                var options = new SendOptions { Mode = SendMode.RawFrame };
                DownloadResult result = await runner.RunAsync(DownloadFrames(1), options, 150, 3, CancellationToken.None);

                Check(result.Ok, "重试之后成功（实际尝试 " + attempts + " 次）");
                CheckEq(attempts, 3, "前两次超时、第三次成功");
            }

            // ---- 失败即停：第 2 帧永远不回，第 3 帧不该被发出去 ----
            var stuck = new FakeSerialTransport();
            stuck.AutoReply = delegate(byte[] request)
            {
                return request[2] == 0x20 ? null : AckFor(request);   // 偏移 0x0020 的帧不回
            };

            using (var session = new SerialSession(stuck))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                IList<DownloadFrame> frames = DownloadFrames(3);
                var runner = new DownloadRunner(session);
                var options = new SendOptions { Mode = SendMode.RawFrame };

                DownloadResult result = await runner.RunAsync(frames, options, 120, 0, CancellationToken.None);

                Check(!result.Ok, "有一帧确认不了 → 整体失败");
                CheckEq(result.Confirmed, 1, "只确认了第 1 帧");
                CheckEq(result.FailedOffset, (int)frames[1].ImageOffset, "报出停在第 2 帧");
                Check(result.Error.Contains("第 2/3 帧"), "错误里写明是第几帧：" + result.Error);
                CheckEq(FrameCount(stuck, 0xCA), 2, "第 3 帧没有被发出去（失败即停）");
            }

            // ---- 下载模式用完必须还原，而且取消时也要还原 ----
            var modeFake = new FakeSerialTransport();
            modeFake.AutoReply = AckFor;

            using (var session = new SerialSession(modeFake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                Check(!session.LoaderAckMode, "默认不是下载模式");

                var runner = new DownloadRunner(session);
                var options = new SendOptions { Mode = SendMode.RawFrame };
                await runner.RunAsync(DownloadFrames(1), options, 500, 0, CancellationToken.None);
                Check(!session.LoaderAckMode, "正常结束后还原成非下载模式");

                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    try
                    {
                        await runner.RunAsync(DownloadFrames(2), options, 500, 0, cts.Token);
                        Check(false, "取消应当抛出");
                    }
                    catch (OperationCanceledException)
                    {
                        Check(true, "取消向上抛 OperationCanceledException");
                    }
                }

                Check(!session.LoaderAckMode, "取消之后同样还原成非下载模式");
            }

            // ---- 空列表：直接成功，不发任何帧 ----
            var emptyFake = new FakeSerialTransport();
            using (var session = new SerialSession(emptyFake))
            {
                session.ReceiveMode = ReceiveMode.RawFrames;
                session.Open(TestSettings());

                var runner = new DownloadRunner(session);
                var options = new SendOptions { Mode = SendMode.RawFrame };
                DownloadResult result = await runner.RunAsync(new List<DownloadFrame>(), options, 500, 0, CancellationToken.None);

                Check(result.Ok && result.Total == 0, "空列表直接成功");
                CheckEq(emptyFake.WriteCalls, 0, "一帧都没发");
            }
        }
    }
}
