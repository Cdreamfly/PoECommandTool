using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PoECommandTool;
using PoECommandTool.Chart;
using PoECommandTool.Serial;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        private static double MaxOf(SeriesBuffer buffer)
        {
            double max = double.MinValue;
            for (int i = 0; i < buffer.Count; i++)
                if (buffer[i].Value > max)
                    max = buffer[i].Value;
            return max;
        }

        /// <summary>
        /// 模拟用户那台设备的调试控制台：收到 `debug#uart_test &lt;12 个 hex&gt; 500000` 这样的命令行后，
        /// 解析出命令帧、造一个响应帧，再回一行 `[UART_TEST] received 12 bytes: ...`。
        /// </summary>
        private static byte[] ConsoleDeviceWriteHandler(byte[] written)
        {
            string text = Encoding.ASCII.GetString(written);
            int at = text.IndexOf("uart_test", StringComparison.Ordinal);
            if (at < 0)
                return null;

            at += "uart_test".Length;
            int end = text.IndexOf('\r', at);
            if (end < 0) end = text.Length;
            string rest = text.Substring(at, end - at);

            // 只取紧跟命令后面的十六进制字段：尾部还有长度(12)与设备参数(500000)。
            // 长度那一段也长得像十六进制字节，所以这里按「至少要 12 个」判断，只取前 12 个。
            var tokens = new List<string>();
            string[] parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i];
                if (token.Length != 2 || HexUtil.HexValue(token[0]) < 0 || HexUtil.HexValue(token[1]) < 0)
                    break;
                tokens.Add(token);
            }

            if (tokens.Count < 12)
                return null;

            byte[] request = HexUtil.ParseBytes(string.Join(" ", tokens.GetRange(0, 12).ToArray()));
            byte[] reply = ReplyFor(request);
            if (reply == null)
                return null;

            // 真设备会先回显刚发出去的命令行，再回响应——回显那行里也含一个校验和正确的帧。
            // 如果这段回显被当成响应，0x44 的功率会解成 0xFFFF×0.1 = 6553.5 W，测试会当场发现。
            string echo = "[UART_TEST] sending 12 bytes: " + Rtl8239CommandBuilder.ToHex(request) + "\r\n";
            string received = "[UART_TEST] received 12 bytes: " + Rtl8239CommandBuilder.ToHex(reply) + "\r\n";
            return Encoding.ASCII.GetBytes(echo + received);
        }

        /// <summary>
        /// 端到端：轮询 → 模板填充 → 设备控制台 → 回包行 → 提取 → 解析 → 采样 → 绘图计算。
        /// 这是没有真机时能做到的最完整的验证。
        /// </summary>
        private static async Task ConsoleEndToEndTests()
        {
            Console.WriteLine("端到端（模拟调试控制台）");

            var clock = new FakeClock();
            var fake = new FakeSerialTransport();
            fake.AutoReply = ConsoleDeviceWriteHandler;

            var legend = new List<SeriesBuffer>();
            var byKey = new Dictionary<string, SeriesBuffer>();
            int parsedFrames = 0;

            using (var session = new SerialSession(fake))
            {
                session.ReceiveMode = ReceiveMode.TextLines;      // 调试控制台：按行解析
                session.Open(TestSettings());

                var options = new SendOptions
                {
                    Mode = SendMode.TextCommand,
                    Template = CommandTemplate.DefaultTemplate,
                    LineEnding = LineEnding.CrLf,
                };

                var runner = new PollingRunner(session, clock.Read,
                    delegate(TimeSpan span, CancellationToken token)
                    {
                        clock.Advance(span.TotalMilliseconds);
                        return Task.CompletedTask;
                    });

                // 与 MainWindow.SampleFrame 相同的采样路径
                runner.ResponseMatched += delegate(PollRequest request, FrameEvent frame)
                {
                    if (!frame.IsParsed)
                        return;

                    Interlocked.Increment(ref parsedFrames);
                    int port = TelemetryExtractor.TryGetPort(frame.Parsed);
                    string tag = string.Format("0x{0:X2}", frame.CommandId) + (port >= 0 ? " 端口" + port : string.Empty);

                    DateTime now = DateTime.Now;
                    IList<SeriesCandidate> candidates = TelemetryExtractor.Extract(frame.Parsed, tag);
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        SeriesCandidate candidate = candidates[i];
                        SeriesBuffer buffer;
                        if (!byKey.TryGetValue(candidate.Key, out buffer))
                        {
                            buffer = new SeriesBuffer(candidate.Key, candidate.Name, candidate.Unit);
                            byKey[candidate.Key] = buffer;
                            legend.Add(buffer);
                        }
                        buffer.Add(now, candidate.Value);
                    }
                };

                var plan = new PollingPlan
                {
                    IntervalMs = 200,
                    ResponseTimeoutMs = 300,
                    MaxRetries = 1,
                    InterCommandDelayMs = 30,
                };
                plan.Items.Add(Item("0x42", 0x00));    // 端口状态：不出曲线
                plan.Items.Add(Item("0x44", 0x00));    // 端口测量：出 4 条曲线

                var cts = new CancellationTokenSource();
                cts.CancelAfter(15000);      // 硬超时：夹具写错时也要能跑完，不能把整个套件挂住
                int responses = 0;
                runner.ResponseMatched += delegate { if (Interlocked.Increment(ref responses) >= 6) cts.Cancel(); };
                await runner.RunAsync(plan, options, cts.Token);

                CheckEq(parsedFrames, 6, "6 个响应全部解析成功（0x42 × 3 + 0x44 × 3）");

                // 写出去的确实是那条控制台命令，且尾部设备参数保留
                string firstLine = Encoding.ASCII.GetString(fake.WrittenBytes);
                Check(firstLine.StartsWith("uart_test 42 01 00 FF FF FF FF FF FF FF FF 3B 12 500000\r\n"),
                    "发送的是控制台命令（模板+长度+设备参数都在）：" + firstLine.Split('\r')[0]);

                // 采样：0x42 不出曲线，0x44 出 4 条
                CheckEq(legend.Count, 4, "只采到 0x44 的 4 条曲线（0x42 没有数值量）");
                foreach (SeriesBuffer buffer in legend)
                    CheckEq(buffer.Count, 3, buffer.Name + " 采到 3 个点");

                // 回显防线（端到端）：设备回显的命令行里也有一个校验和正确的帧，
                // 一旦被当成响应，功率就会变成 0xFFFF×0.1 = 6553.5 W。
                SeriesBuffer power = byKey["0x44 端口0.PowerW"];
                SeriesSample last;
                Check(power.TryGetLast(out last), "功率曲线有最后的采样");
                CheckNear(last.Value, 30.0, 1e-9, "功率 = 30 W（若回显被误解析则会是 6553.5 W）");
                Check(MaxOf(power) < 1000, "功率量程正常，说明回显没有被当成响应");
                // 绘图计算：4 种单位 → 4 个子图，量程取自各自的曲线
                DateTime to = DateTime.Now.AddSeconds(1);
                DateTime from = to.AddMinutes(-1);
                ChartLayout layout = ChartMath.Build(legend, from, to, 60, 8, 500, 260, 400);

                CheckEq(layout.Bands.Count, 4, "mV / mA / ℃ / W 各一个子图");
                Check(layout.HasData, "布局里确实有数据");

                SeriesBuffer voltage = byKey["0x44 端口0.VoltageMv"];
                CheckNear(voltage[0].Value, 12890.0, 1e-6, "电压点 = 12890 mV");
                Check(MaxOf(voltage) < 100000, "电压量程不会把其它单位压扁");
            }

            Console.WriteLine();
        }
    }
}
