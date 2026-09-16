using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 从设备控制台的一行输出里抠出 RTL8239 响应帧。
    ///
    /// 默认规则匹配形如 <c>[UART_TEST] received 12 bytes: 42 01 00 … 4A</c> 的行。
    ///
    /// <para>
    /// 注意「回显陷阱」：这类控制台通常会把我们发出去的命令行原样回显，而那一行里
    /// 也含一个校验和完全正确的 12 字节帧（就是我们刚发出去的请求）。所以：
    /// 默认规则要求出现 received 字样、默认关闭「宽松扫描」、并且拒绝含 sending 之类
    /// 回显关键词的行。三道防线缺一不可，否则会把请求当成响应解析出一堆假数据。
    /// </para>
    /// </summary>
    public sealed class ResponseLineExtractor
    {
        /// <summary>默认识别规则：received &lt;N&gt; bytes: &lt;十六进制&gt;</summary>
        public const string DefaultPattern = @"received\s+(\d+)\s*bytes?\s*:\s*([0-9A-Fa-f][0-9A-Fa-f\s]*)";

        /// <summary>正则匹配超时：用户自定义规则写错（灾难性回溯）时不能把读循环卡死。</summary>
        public const int MatchTimeoutMs = 200;

        private const string DefaultEchoKeyword = "sending";

        private readonly object _sync = new object();
        private string _pattern;
        private Regex _regex;

        public ResponseLineExtractor()
        {
            _pattern = DefaultPattern;
            RequireCountMatch = true;
            AllowFallbackScan = false;
            EchoKeyword = DefaultEchoKeyword;
            FallbackFrameLength = 12;
        }

        /// <summary>识别规则（正则）。修改后会重新编译。</summary>
        public string Pattern
        {
            get { return _pattern; }
            set
            {
                lock (_sync)
                {
                    _pattern = value;
                    _regex = null;
                }
            }
        }

        /// <summary>是否核对「声明字节数」与实际解析出的字节数一致。</summary>
        public bool RequireCountMatch { get; set; }

        /// <summary>
        /// 严格规则不匹配时，是否退化成「扫描行里前 N 个十六进制字节」。
        /// 默认关闭：开启后会连回显行一起吃掉。
        /// </summary>
        public bool AllowFallbackScan { get; set; }

        /// <summary>回显关键词；含该词（且不含 received）的行视为回显，直接忽略。</summary>
        public string EchoKeyword { get; set; }

        /// <summary>宽松扫描时需要的字节数（RTL8239 App 响应帧为 12）。</summary>
        public int FallbackFrameLength { get; set; }

        /// <summary>识别规则能否编译；UI 用它做实时校验。</summary>
        public bool TryValidatePattern(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(_pattern))
            {
                error = "识别规则不能为空。";
                return false;
            }
            try
            {
                new Regex(_pattern, RegexOptions.None, TimeSpan.FromMilliseconds(MatchTimeoutMs));
                return true;
            }
            catch (ArgumentException ex)
            {
                error = "识别规则不是合法正则：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 从一行里提取一帧。
        /// <paramref name="why"/> 的约定：**返回 null 表示这一行本来就不像响应**（普通控制台输出，
        /// 调用方不必记日志）；非 null 表示「看着像响应但没采纳」，原因是给人排查用的。
        /// </summary>
        public bool TryExtract(string line, out byte[] frame, out string why)
        {
            frame = null;
            why = null;

            if (string.IsNullOrEmpty(line))
                return false;

            if (LooksLikeEcho(line, EchoKeyword))
            {
                why = "形似发送回显，已忽略";
                return false;
            }

            Regex regex = GetRegex();
            if (regex != null)
            {
                Match match;
                try
                {
                    match = regex.Match(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    why = "识别规则匹配超时（正则可能写得过于复杂）";
                    return false;
                }

                if (match.Success)
                    return TryTakeMatch(match, out frame, out why);
            }

            if (AllowFallbackScan && TryScanHexBytes(line, FallbackFrameLength, out frame))
                return true;

            return false;
        }

        /// <summary>一行里可能出现多次 received 记录（例如两次转发的日志），全部取出。</summary>
        public IList<byte[]> ExtractAll(string line)
        {
            var frames = new List<byte[]>();
            if (string.IsNullOrEmpty(line))
                return frames;
            if (LooksLikeEcho(line, EchoKeyword))
                return frames;

            Regex regex = GetRegex();
            if (regex == null)
                return frames;

            try
            {
                foreach (Match match in regex.Matches(line))
                {
                    byte[] frame;
                    string why;
                    if (TryTakeMatch(match, out frame, out why))
                        frames.Add(frame);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // 匹配超时：按「没识别到」处理，绝不阻塞调用方
            }

            return frames;
        }

        /// <summary>这一行是不是「发送回显」——含回显关键词、且没有 received 字样。</summary>
        public static bool LooksLikeEcho(string line, string echoKeyword)
        {
            if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(echoKeyword))
                return false;

            if (line.IndexOf("received", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return line.IndexOf(echoKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private Regex GetRegex()
        {
            lock (_sync)
            {
                if (_regex != null)
                    return _regex;
                if (string.IsNullOrEmpty(_pattern))
                    return null;

                try
                {
                    _regex = new Regex(_pattern, RegexOptions.None, TimeSpan.FromMilliseconds(MatchTimeoutMs));
                }
                catch (ArgumentException)
                {
                    return null;   // 规则非法：交给 TryValidatePattern 报错
                }
                return _regex;
            }
        }

        private bool TryTakeMatch(Match match, out byte[] frame, out string why)
        {
            frame = null;
            why = null;

            byte[] bytes;
            string parseError;
            if (!HexUtil.TryParseBytes(match.Groups[2].Value, out bytes, out parseError))
            {
                why = "识别到的十六进制内容无法解析：" + parseError;
                return false;
            }

            if (bytes.Length == 0)
            {
                why = "识别到的十六进制内容为空";
                return false;
            }

            if (RequireCountMatch && match.Groups[1].Success)
            {
                int declared;
                if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out declared) && declared != bytes.Length)
                {
                    why = string.Format("声明 {0} 字节，实际解析出 {1} 字节", declared, bytes.Length);
                    return false;
                }
            }

            frame = bytes;
            return true;
        }

        // 扫描行里「两端都不是十六进制字符」的 2 位十六进制字节，避免从长串中间切开
        private static bool TryScanHexBytes(string line, int needed, out byte[] bytes)
        {
            bytes = null;
            if (needed <= 0)
                return false;

            var found = new List<byte>(needed);
            int i = 0;
            while (i + 1 < line.Length && found.Count < needed)
            {
                int hi = HexUtil.HexValue(line[i]);
                int lo = HexUtil.HexValue(line[i + 1]);
                bool leftBoundary = i == 0 || HexUtil.HexValue(line[i - 1]) < 0;
                bool rightBoundary = i + 2 >= line.Length || HexUtil.HexValue(line[i + 2]) < 0;

                if (hi >= 0 && lo >= 0 && leftBoundary && rightBoundary)
                {
                    found.Add((byte)((hi << 4) | lo));
                    i += 2;
                    continue;
                }
                i++;
            }

            if (found.Count < needed)
                return false;

            bytes = found.ToArray();
            return true;
        }
    }
}
