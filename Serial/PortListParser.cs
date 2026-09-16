using System;
using System.Collections.Generic;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 解析界面上的端口输入，支持多种写法，用来把一条命令展开成多个端口的轮询项：
    /// <c>0</c>、<c>0,1,2</c>、<c>0-3</c>、<c>0x00-0x03</c>、<c>0,2-4,7</c>(混用)。
    /// 数字按 <see cref="Rtl8239Catalog.ParseNumber"/> 的规则：带 0x 或含 A-F 按十六进制，否则十进制。
    /// </summary>
    public static class PortListParser
    {
        /// <summary>逻辑端口索引上限（手册：0x00-0x2F 即 0-47 有效）。</summary>
        public const int MaxPort = 0x2F;

        private static readonly char[] Separators = { ',', '，', ';', '；', '、', ' ', '\t' };

        /// <summary>解析成端口列表（去重、保持输入顺序）；失败时给出可读原因。</summary>
        public static bool TryParse(string text, out byte[] ports, out string error)
        {
            ports = new byte[0];
            error = null;

            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                error = "端口不能为空，请填 0x00-0x2F（0-47），多个用逗号分隔或写成 0-3。";
                return false;
            }

            var result = new List<byte>();
            string[] tokens = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                error = "端口不能为空。";
                return false;
            }

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.Length == 0)
                    continue;

                int dash = token.IndexOf('-');
                if (dash < 0)
                {
                    int single;
                    if (!TryParsePort(token, out single, out error))
                        return false;
                    AddUnique(result, (byte)single);
                    continue;
                }

                string fromText = token.Substring(0, dash).Trim();
                string toText = token.Substring(dash + 1).Trim();

                int from;
                if (!TryParsePort(fromText, out from, out error))
                    return false;

                int to;
                if (!TryParsePort(toText, out to, out error))
                    return false;

                if (to < from)
                {
                    error = string.Format("范围“{0}”的起始端口比结束端口大。", token);
                    return false;
                }

                for (int port = from; port <= to; port++)
                    AddUnique(result, (byte)port);
            }

            if (result.Count == 0)
            {
                error = "没有解析出任何端口。";
                return false;
            }

            ports = result.ToArray();
            return true;
        }

        private static bool TryParsePort(string text, out int port, out string error)
        {
            port = 0;
            error = null;

            if (string.IsNullOrEmpty(text))
            {
                error = "端口里有空的一段，请检查逗号和减号。";
                return false;
            }

            long value;
            try
            {
                value = Rtl8239Catalog.ParseNumber(text);
            }
            catch (Exception)
            {
                error = string.Format("端口“{0}”无法识别。", text);
                return false;
            }

            if (value < 0 || value > MaxPort)
            {
                error = string.Format("端口“{0}”超出范围，必须在 0x00-0x2F（0-47）。", text);
                return false;
            }

            port = (int)value;
            return true;
        }

        private static void AddUnique(List<byte> ports, byte port)
        {
            if (!ports.Contains(port))
                ports.Add(port);
        }
    }
}
