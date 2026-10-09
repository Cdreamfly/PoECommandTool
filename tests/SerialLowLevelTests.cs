using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using PoECommandTool;
using PoECommandTool.Serial;

namespace Rtl8239Verify
{
    // 串口底层纯逻辑的断言（与 Program.cs 是同一个 partial 类，可复用 Check/CheckEq/Frame）
    internal static partial class Program
    {
        // 用户给的两行真实设备日志
        private const string RealSendingLine =
            "[UART_TEST] sending 12 bytes: 42 01 00 FF FF FF FF FF FF FF FF 3B";
        private const string RealReceivedLine =
            "[UART_TEST] received 12 bytes: 42 01 00 01 06 00 00 00 00 00 00 4A";

        private static void HexUtilTests()
        {
            Console.WriteLine("HexUtil");

            byte[] expect = { 0x42, 0x01, 0x00, 0xFF };
            CheckEq(HexUtil.ToHex(HexUtil.ParseBytes("42 01 00 FF")), "42 01 00 FF", "空格分隔");
            CheckEq(HexUtil.ToHex(HexUtil.ParseBytes("42,01,00,FF")), "42 01 00 FF", "逗号分隔");
            CheckEq(HexUtil.ToHex(HexUtil.ParseBytes("42\n01\r\n00\tFF")), "42 01 00 FF", "换行/制表符分隔");
            CheckEq(HexUtil.ToHex(HexUtil.ParseBytes("0x42 0X01 0x00 0xff")), "42 01 00 FF", "0x 前缀");
            CheckEq(HexUtil.ToHex(HexUtil.ParseBytes("4201 00FF")), "42 01 00 FF", "不带分隔符连写");
            CheckEq(HexUtil.ParseBytes("").Length, 0, "空串 -> 空数组");
            CheckEq(HexUtil.ParseBytes("   ").Length, 0, "全空白 -> 空数组");

            byte[] got = HexUtil.ParseBytes("42,01,00,FF");
            bool same = got.Length == expect.Length;
            for (int i = 0; same && i < got.Length; i++) same = got[i] == expect[i];
            Check(same, "逐字节相等");

            Check(ThrownBy(() => HexUtil.ParseBytes("42 4 00")).Contains("奇数"), "奇数长度字段被拒绝");
            Check(ThrownBy(() => HexUtil.ParseBytes("GG")).Contains("非十六进制"), "非法字符被拒绝");
            Console.WriteLine();
        }

        private static void LineAssemblerTests()
        {
            Console.WriteLine("LineAssembler");

            var a = new LineAssembler();
            CheckEq(a.Append(Ascii("hel"), 3).Count, 0, "半行不产出");
            IList<string> lines = a.Append(Ascii("lo\r\n"), 4);
            CheckEq(lines.Count, 1, "补齐后产出一行");
            CheckEq(lines[0], "hello", "跨块拼接正确");

            var b = new LineAssembler();
            CheckEq(b.Append(Ascii("one\ntwo\nthree\n"), 14).Count, 3, "一个块里三行");

            var c = new LineAssembler();
            CheckEq(c.Append(Ascii("a\r\nb\nc\rd"), 9).Count, 3, "CRLF/LF/CR 混用不产生空行");
            CheckEq(c.Flush(), "d", "Flush 取出残行");
            CheckEq(c.Flush(), null, "残行取过一次就没了");

            var d = new LineAssembler();
            CheckEq(d.Append(Ascii("\r\n\r\n"), 4).Count, 0, "纯空行不产出");

            var e = new LineAssembler { MaxLineLength = 16 };
            e.Append(Ascii(new string('x', 100)), 100);
            CheckEq(e.DroppedLineCount, 1, "无换行超长行被丢弃且只计一次");
            CheckEq(e.PendingLength, 0, "被丢弃后缓冲不残留");
            CheckEq(e.Append(Ascii("\r\n"), 2).Count, 0, "超长行丢弃后不产出半行");

            var f = new LineAssembler();
            f.Append(Ascii(new string('y', 100)), 100);
            IList<string> longLines = f.Append(Ascii("\r\nok\n"), 6);
            CheckEq(longLines.Count, 2, "正常长度下长行照常产出（长行 + ok 两行）");
            CheckEq(longLines[0].Length, 100, "长行完整保留");
            CheckEq(longLines[1], "ok", "长行后面的行不受影响");
            Console.WriteLine();
        }

        private static void CommandTemplateTests()
        {
            Console.WriteLine("CommandTemplate");

            byte[] request = Frame("42 01 00 FF FF FF FF FF FF FF FF");   // 校验和算出来是 3B
            CheckEq(Rtl8239CommandBuilder.ToHex(request), "42 01 00 FF FF FF FF FF FF FF FF 3B",
                "测试用请求帧与用户样例一致");

            CheckEq(CommandTemplate.Fill(CommandTemplate.DefaultTemplate, request),
                "uart_test 42 01 00 FF FF FF FF FF FF FF FF 3B 12 500000", "默认模板展开");

            // 真机回显（2026-09-10 实测）：设备接受的就是这一条。
            // debug# 是设备自己的提示符，写进模板会被当成命令报 "Command not recognised"。
            byte[] measurement = Frame("44 01 00 FF FF FF FF FF FF FF FF");   // 校验和 3D
            CheckEq(CommandTemplate.Fill(CommandTemplate.DefaultTemplate, measurement),
                "uart_test 44 01 00 FF FF FF FF FF FF FF FF 3D 12 500000",
                "默认模板与真机接受的那条命令逐字符一致");

            CheckEq(CommandTemplate.Fill("x {FRAME} {LEN} y", measurement),
                "x 44 01 00 FF FF FF FF FF FF FF FF 3D 12 y", "{LEN} 展开成字节数");
            CheckEq(CommandTemplate.Fill("{LEN}", measurement), "12", "{LEN} 单独用也对");

            CheckEq(CommandTemplate.Fill("{FRAME} and {FRAME}", request),
                "42 01 00 FF FF FF FF FF FF FF FF 3B and 42 01 00 FF FF FF FF FF FF FF FF 3B",
                "多处占位符都替换");

            CheckEq(CommandTemplate.Fill("   ", request),
                "42 01 00 FF FF FF FF FF FF FF FF 3B", "空模板回落成只发帧");

            CheckEq(CommandTemplate.Fill("prefix", request), "prefix", "无占位符时原样返回");

            string warning;
            Check(CommandTemplate.TryValidate(CommandTemplate.DefaultTemplate, out warning), "默认模板校验通过");
            Check(!CommandTemplate.TryValidate("debug#ping", out warning) && warning.Contains("占位符"),
                "缺占位符时给出警告：" + warning);
            Check(!CommandTemplate.TryValidate("", out warning), "空模板给出警告");

            CheckEq(CommandTemplate.Terminator(LineEnding.None).Length, 0, "无行结束符");
            CheckEq(CommandTemplate.Terminator(LineEnding.Cr)[0], (byte)0x0D, "CR");
            CheckEq(CommandTemplate.Terminator(LineEnding.Lf)[0], (byte)0x0A, "LF");
            CheckEq(CommandTemplate.Terminator(LineEnding.CrLf).Length, 2, "CRLF 两个字节");

            byte[] wire = CommandTemplate.BuildTextCommand(CommandTemplate.DefaultTemplate, request, LineEnding.CrLf);
            string wireText = Encoding.ASCII.GetString(wire);
            Check(wireText.StartsWith("uart_test 42 01 00") && wire[wire.Length - 2] == 0x0D && wire[wire.Length - 1] == 0x0A,
                "拼出的线上字节 = ASCII(模板) + CRLF");
            Console.WriteLine();
        }

        private static void ResponseLineExtractorTests()
        {
            Console.WriteLine("ResponseLineExtractor");

            var ex = new ResponseLineExtractor();

            byte[] frame;
            string why;
            Check(ex.TryExtract(RealReceivedLine, out frame, out why), "真实 received 行被提取：" + why);
            CheckEq(frame.Length, 12, "提取出 12 字节");
            CheckEq(Rtl8239CommandBuilder.ToHex(frame), "42 01 00 01 06 00 00 00 00 00 00 4A", "逐字节一致");

            // 回显防线
            Check(!ex.TryExtract(RealSendingLine, out frame, out why), "真实 sending 回显行被拒绝");
            Check(why != null && why.Contains("回显"), "拒绝原因说明是回显：" + why);

            var lax = new ResponseLineExtractor { AllowFallbackScan = true };
            Check(!lax.TryExtract(RealSendingLine, out frame, out why),
                "即使打开宽松扫描，回显行也不能被吃掉");
            Check(lax.TryExtract("[UART_TEST] got: 42 01 00 01 06 00 00 00 00 00 00 4A", out frame, out why),
                "宽松扫描能应对措辞不同的设备");
            CheckEq(frame.Length, 12, "宽松扫描也凑够 12 字节");

            Check(!new ResponseLineExtractor().TryExtract(
                    "[UART_TEST] got: 42 01 00 01 06 00 00 00 00 00 00 4A", out frame, out why),
                "默认关闭宽松扫描（免得误吃回显）");

            Check(!ex.TryExtract("[UART_TEST] received 12 bytes: 42 01 00 01 06 00 00 00 00 00 00",
                    out frame, out why), "声明 12 实际 11 被拒绝");
            Check(why != null && why.Contains("11"), "拒绝原因给出实际字节数：" + why);

            Check(ex.TryExtract("2026-09-10 12:00:01 [UART_TEST] received 12 bytes: 42 01 00 01 06 00 00 00 00 00 00 4a",
                    out frame, out why), "前面有时间戳噪音 + 小写十六进制");
            CheckEq(frame[11], (byte)0x4A, "小写 4a 解析正确");

            Check(ex.TryExtract("[X] received 2 bytes: 42 01", out frame, out why) && frame.Length == 2,
                "短帧也按声明字节数提取");

            Check(!ex.TryExtract("", out frame, out why), "空行被拒绝");

            IList<byte[]> many = ex.ExtractAll(RealReceivedLine + "  转发: " + RealReceivedLine.Replace("42 01 00 01 06", "42 01 00 02 06"));
            CheckEq(many.Count, 2, "一行里两条 received 记录都被取出");

            // 规则校验与超时
            string patternError;
            Check(!new ResponseLineExtractor { Pattern = "(" }.TryValidatePattern(out patternError)
                    && !string.IsNullOrEmpty(patternError),
                "非法正则在界面上就能被拦下：" + patternError);
            Check(new ResponseLineExtractor().TryValidatePattern(out patternError), "默认规则合法");
            Check(!new ResponseLineExtractor { Pattern = "(" }.TryExtract(RealReceivedLine, out frame, out why),
                "规则非法时不抛异常，只是提取不到");
            CheckEq(why, null, "规则非法时按「这行不像响应」处理，不往日志里刷");

            // why 的语义：null = 普通控制台输出（不记日志）；非 null = 像响应但没采纳（要记）
            Check(!new ResponseLineExtractor().TryExtract("debug#", out frame, out why) && why == null,
                "提示符行不产生「未采纳」提示");
            Check(!new ResponseLineExtractor().TryExtract(
                    "Command not recognised.  Enter \"help\" to view a list of available commands.",
                    out frame, out why) && why == null,
                "控制台的普通输出不产生「未采纳」提示");

            var bomb = new ResponseLineExtractor { Pattern = @"(a+)+$" };
            var sw = Stopwatch.StartNew();
            bool bombResult = bomb.TryExtract(new string('a', 40) + "!", out frame, out why);
            sw.Stop();
            Check(!bombResult && sw.ElapsedMilliseconds < 3000,
                "恶意正则被超时拦住，不卡死（耗时 " + sw.ElapsedMilliseconds + " ms）");

            // 端到端：真实回包 -> 提取 -> 解析
            var end = new ResponseLineExtractor();
            byte[] real;
            Check(end.TryExtract(RealReceivedLine, out real, out why), "提取真实回包");
            PortStatus status = Rtl8239ResponseParser.ParsePortStatus(real);
            CheckEq(status.Port, (byte)0, "解析：端口 0");
            CheckEq(status.PowerState, PortPowerState.Searching, "解析：检测中");
            CheckEq(status.DetectionResult, DetectionResult.OpenCircuit, "解析：开路（无 PD）");
            CheckEq(status.ClassificationResult, ClassificationResult.Class0, "解析：Class 0");
            CheckEq(status.ConnectionCheckResult, ConnectionCheckResult.TwoPair, "解析：2-pair");
            Check(status.FaultType == null, "解析：非故障端口没有错误类型");
            Console.WriteLine();
        }

        private static byte[] Ascii(string s)
        {
            return Encoding.ASCII.GetBytes(s);
        }
    }
}
