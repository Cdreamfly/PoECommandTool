using System;
using System.Collections.Generic;
using System.Text;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 十六进制文本与字节数组互转。
    ///
    /// 解析是宽松的：字段之间可以用空格 / 逗号 / 分号 / 制表符 / 换行分隔，
    /// 每个字段可以带 0x 前缀，也可以不带分隔符直接连写（"4201 00FF"）。
    /// </summary>
    public static class HexUtil
    {
        private static readonly char[] FieldSeparators = { ' ', ',', '\t', '\r', '\n', ';' };

        /// <summary>把十六进制文本解析为字节数组；输入为空时返回空数组。</summary>
        /// <exception cref="FormatException">字段长度为奇数，或含非十六进制字符。</exception>
        public static byte[] ParseBytes(string text)
        {
            byte[] bytes;
            string error;
            if (!TryParseBytes(text, out bytes, out error))
                throw new FormatException(error);
            return bytes;
        }

        /// <summary>宽松解析；失败时返回 false 并给出可读原因。</summary>
        public static bool TryParseBytes(string text, out byte[] bytes, out string error)
        {
            bytes = Array.Empty<byte>();
            error = null;

            if (string.IsNullOrEmpty(text))
                return true;

            string[] fields = text.Split(FieldSeparators, StringSplitOptions.RemoveEmptyEntries);
            var result = new List<byte>(fields.Length);

            for (int i = 0; i < fields.Length; i++)
            {
                string field = fields[i];
                string bare = field.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? field.Substring(2)
                    : field;

                if (bare.Length == 0)
                {
                    error = string.Format("第 {0} 个字段“{1}”里没有十六进制数字。", i + 1, field);
                    return false;
                }
                if (bare.Length % 2 != 0)
                {
                    error = string.Format("第 {0} 个字段“{1}”的长度是奇数，没法按字节切分。", i + 1, field);
                    return false;
                }

                for (int p = 0; p < bare.Length; p += 2)
                {
                    int hi = HexValue(bare[p]);
                    int lo = HexValue(bare[p + 1]);
                    if (hi < 0 || lo < 0)
                    {
                        error = string.Format("第 {0} 个字段“{1}”含非十六进制字符。", i + 1, field);
                        return false;
                    }
                    result.Add((byte)((hi << 4) | lo));
                }
            }

            bytes = result.ToArray();
            return true;
        }

        /// <summary>格式化为 "AA BB CC" 形式（复用命令组装库的同一实现，保证显示一致）。</summary>
        public static string ToHex(byte[] data)
        {
            return Rtl8239CommandBuilder.ToHex(data);
        }

        /// <summary>单个十六进制字符的数值；不是十六进制字符时返回 -1。</summary>
        public static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
