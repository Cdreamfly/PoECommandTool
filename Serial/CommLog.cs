using System;
using System.Collections.Generic;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 通讯日志的存储，以及「某个视图现在该怎么更新」。
    ///
    /// 为什么要有它：日志现在有两个视图（串口页底部那个框、独立的日志窗口）。原来那套
    /// 「一个 List + 一个 _logCompacted 标志」在**满 1000 行之后每来一行都触发一次全量重建**
    /// ——轮询期间每秒 10 次 `string.Join` 1000 行再重赋 `TextBox.Text`。加第二个视图只会更糟，
    /// 所以先换成按**绝对行号**做增量。
    ///
    /// 线程约定：**只在 UI 线程上使用**，无锁。读线程来的日志仍走 MainWindow 的
    /// `_pendingLines` 队列，那一段没有改动，也不该改。
    ///
    /// 行内容不含时间戳——时间戳由调用方拼好再放进来，呈现不归这里管。
    /// 也刻意不碰 `Environment.NewLine`，好让 Linux 上的断言工程能按行比对。
    /// </summary>
    public sealed class CommLog
    {
        private readonly List<string> _lines = new List<string>();
        private readonly int _maxLines;
        private long _firstIndex;

        public CommLog(int maxLines)
        {
            if (maxLines < 1)
                throw new ArgumentOutOfRangeException("maxLines", "日志上限至少要 1 行。");

            _maxLines = maxLines;
        }

        /// <summary>当前第一行的绝对编号。日志被截断时向前推进。</summary>
        public long FirstIndex { get { return _firstIndex; } }

        /// <summary>下一个待分配的编号，也就是累计写入过的行数（含已被丢弃的）。</summary>
        public long NextIndex { get { return _firstIndex + _lines.Count; } }

        public int Count { get { return _lines.Count; } }

        /// <summary>追加一行；超出上限时丢最旧的，并把 <see cref="FirstIndex"/> 推进。</summary>
        public void Append(string text)
        {
            _lines.Add(text ?? string.Empty);

            while (_lines.Count > _maxLines)
            {
                _lines.RemoveAt(0);
                _firstIndex++;
            }
        }

        /// <summary>
        /// 某个视图（它自己记着「已经显示到哪一行」）现在该怎么更新。
        ///
        /// * `rebuild = true`  → 调用方整体重建，内容是从 <see cref="FirstIndex"/> 起的全部行；
        /// * `rebuild = false` → 从 `from` 行开始追加到 <see cref="NextIndex"/> 之前；
        ///   `from == NextIndex` 表示没有新行，调用方什么都不用做。
        ///
        /// `shown == 0`（视图还没显示过任何东西）一律走整体重建：这既让中途打开的窗口能拿到完整历史，
        /// 也避免了「第一次就当增量」而在开头多出一个分隔符（也就是一个空行）。
        /// 视图落后到 `FirstIndex` 之前（中间的行已经被丢掉了）同样整体重建——它看到的内容必须自洽。
        /// </summary>
        public void GetView(long shown, out bool rebuild, out long from)
        {
            if (shown <= 0 || shown < _firstIndex)
            {
                rebuild = _lines.Count > 0;
                from = _firstIndex;
                return;
            }

            rebuild = false;
            from = shown > NextIndex ? NextIndex : shown;
        }

        /// <summary>取某一行的内容；越界返回 null（视图落后时可以据此跳过）。</summary>
        public string LineAt(long index)
        {
            long offset = index - _firstIndex;
            if (offset < 0 || offset >= _lines.Count)
                return null;

            return _lines[(int)offset];
        }
    }
}
