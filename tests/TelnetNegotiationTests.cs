using System;
using System.Collections.Generic;
using System.Text;
using PoECommandTool.Net;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        /// <summary>把 "41 FF FD 01" 这样的 hex 串变成字节。</summary>
        private static byte[] Bytes(string hex)
        {
            string[] parts = hex.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var b = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                b[i] = Convert.ToByte(parts[i], 16);
            return b;
        }

        private static string HexOf(IList<byte> bytes)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < bytes.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Telnet 选项协商。全拒、只答应 SUPPRESS-GO-AHEAD。
        /// 这些断言是必要的：协商方向搞反在真机上只表现为「连上去没反应」。
        /// </summary>
        private static void TelnetNegotiationTests()
        {
            Console.WriteLine("Telnet 选项协商");

            var negotiator = new TelnetNegotiation();
            var payload = new List<byte>();
            var reply = new List<byte>();

            // ---- 普通数据原样通过 ----
            negotiator.Process(Bytes("68 65 6C 6C 6F"), 5, payload, reply);
            CheckEq(HexOf(payload), "68 65 6C 6C 6F", "普通数据原样通过");
            CheckEq(reply.Count, 0, "普通数据不产生应答");

            // ---- IAC IAC 是转义的字面 0xFF ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF FF"), 2, payload, reply);
            CheckEq(HexOf(payload), "FF", "IAC IAC 还原成字面 0xFF");
            CheckEq(reply.Count, 0, "IAC IAC 不产生应答");

            // ---- DO SGA -> WILL SGA（唯一答应的选项） ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF FD 03"), 3, payload, reply);
            CheckEq(HexOf(reply), "FF FB 03", "对方 DO SGA，我们回 WILL SGA");
            CheckEq(payload.Count, 0, "协商字节不进数据流");

            // ---- DO ECHO -> WONT ECHO（回显由设备控制台给，不由 Telnet 给） ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF FD 01"), 3, payload, reply);
            CheckEq(HexOf(reply), "FF FC 01", "对方 DO ECHO，我们回 WONT ECHO");

            // ---- DO NAWS(31) -> WONT（不要窗口尺寸） ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF FD 1F"), 3, payload, reply);
            CheckEq(HexOf(reply), "FF FC 1F", "对方 DO NAWS，我们回 WONT NAWS");

            // ---- WILL SGA -> DO SGA ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF FB 03"), 3, payload, reply);
            CheckEq(HexOf(reply), "FF FD 03", "对方 WILL SGA，我们回 DO SGA");

            // ---- WILL ECHO -> DONT ECHO ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF FB 01"), 3, payload, reply);
            CheckEq(HexOf(reply), "FF FE 01", "对方 WILL ECHO，我们回 DONT ECHO");

            // ---- DONT / WONT 不需要应答 ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF FE 01 FF FC 03"), 6, payload, reply);
            CheckEq(reply.Count, 0, "DONT / WONT 不需要应答");

            // ---- 子协商整段丢弃 ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF FA 18 00 50 00 FF F0"), 8, payload, reply);
            CheckEq(payload.Count, 0, "子协商内容被丢弃");
            CheckEq(reply.Count, 0, "子协商不产生应答");

            // ---- 拆到两次调用里的协商序列 ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF"), 1, payload, reply);
            CheckEq(reply.Count, 0, "只收到半个 IAC 时先不回应");
            CheckEq(payload.Count, 0, "半个 IAC 也不当作数据");
            negotiator.Process(Bytes("FD 03"), 2, payload, reply);
            CheckEq(HexOf(reply), "FF FB 03", "协商序列跨两次读也要处理正确");

            // ---- 数据与协商交错 ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("41 FF FD 01 42"), 5, payload, reply);
            CheckEq(HexOf(payload), "41 42", "交错时只把数据交给上层");
            CheckEq(HexOf(reply), "FF FC 01", "交错时协商照样应答");

            // ---- 单字节命令忽略 ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("41 FF F1 42"), 4, payload, reply);
            CheckEq(HexOf(payload), "41 42", "单字节命令（NOP）被忽略");

            // ---- 子协商里的 IAC IAC 不提前结束 ----
            negotiator = new TelnetNegotiation();
            payload.Clear(); reply.Clear();
            negotiator.Process(Bytes("FF FA 01 FF FF 41 FF F0 42"), 9, payload, reply);
            CheckEq(HexOf(payload), "42", "子协商里的 IAC IAC 不提前结束");
        }
    }
}
