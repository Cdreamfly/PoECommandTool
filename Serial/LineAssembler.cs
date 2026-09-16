using System;
using System.Collections.Generic;
using System.Text;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 把串口收到的字节流增量拆成文本行，支持 CR / LF / CRLF 三种换行。
    ///
    /// 设备控制台输出的是 ASCII 文本，这里按 Latin-1 逐字节转字符（不做编码探测，
    /// 免得把二进制内容猜成多字节编码）。超过 <see cref="MaxLineLength"/> 还没有
    /// 行结束符时整段丢弃，避免「一直不换行」的数据把内存吃光。
    /// </summary>
    public sealed class LineAssembler
    {
        /// <summary>单行最大长度（字符），超过就丢弃该行。</summary>
        public const int DefaultMaxLineLength = 8192;

        private readonly StringBuilder _pending = new StringBuilder();
        private bool _lastWasCr;
        private bool _discarding;

        public LineAssembler()
        {
            MaxLineLength = DefaultMaxLineLength;
        }

        /// <summary>单行最大长度（字符）。</summary>
        public int MaxLineLength { get; set; }

        /// <summary>因超长被丢弃的行数（用于日志提示）。</summary>
        public int DroppedLineCount { get; private set; }

        /// <summary>当前还没组完一行的字符数。</summary>
        public int PendingLength
        {
            get { return _pending.Length; }
        }

        /// <summary>追加一段收到的字节，返回这次凑齐的完整行（可能为空列表）。</summary>
        public IList<string> Append(byte[] data, int count)
        {
            var lines = new List<string>();
            if (data == null || count <= 0)
                return lines;

            if (count > data.Length)
                count = data.Length;

            for (int i = 0; i < count; i++)
            {
                char c = (char)data[i];

                if (c == '\n')
                {
                    // CRLF 里的 LF：CR 那边已经结过一行了
                    if (_lastWasCr)
                    {
                        _lastWasCr = false;
                        continue;
                    }
                    EndLine(lines);
                    continue;
                }

                if (c == '\r')
                {
                    EndLine(lines);
                    _lastWasCr = true;
                    continue;
                }

                _lastWasCr = false;
                AppendChar(c);
            }

            return lines;
        }

        /// <summary>取出还没终结的残行（例如设备最后一行没有换行）；没有则返回 null。</summary>
        public string Flush()
        {
            _lastWasCr = false;
            _discarding = false;
            if (_pending.Length == 0)
                return null;

            string line = _pending.ToString();
            _pending.Clear();
            return line;
        }

        /// <summary>丢弃已缓冲的内容（例如刚打开串口、或判定数据已错位时）。</summary>
        public void Reset()
        {
            _pending.Clear();
            _discarding = false;
            _lastWasCr = false;
        }

        private void AppendChar(char c)
        {
            if (_discarding)
                return;

            if (_pending.Length >= MaxLineLength)
            {
                _pending.Clear();
                _discarding = true;
                DroppedLineCount++;
                return;
            }

            _pending.Append(c);
        }

        // 空行不产出（控制台的空行对解析没有意义）
        private void EndLine(ICollection<string> lines)
        {
            _discarding = false;
            if (_pending.Length == 0)
                return;

            lines.Add(_pending.ToString());
            _pending.Clear();
        }
    }
}
