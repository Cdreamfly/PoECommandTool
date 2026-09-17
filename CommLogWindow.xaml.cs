using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using WpfApp1.Serial;

namespace WpfApp1
{
    /// <summary>
    /// 独立的通讯日志窗口。本仓库现有的两个次级窗口（关于、曲线详情）都是模态的，
    /// 这个是第一个**无模式**的——它的用处就是「开着它，然后去别的页签干活」。
    ///
    /// 它自己不产生日志，也不碰读取线程的那条队列：那一份由主窗口的 100ms 定时器独占消费，
    /// 再推过来。两个视图各记各的绝对行号（<c>_shownUpTo</c>），所以谁都不会漏行或重复。
    /// </summary>
    public partial class CommLogWindow : Window
    {
        private long _shownUpTo;

        public CommLogWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 按增量把日志同步到本窗口。**只允许在 UI 线程上调用**（由主窗口的日志定时器调用）。
        /// </summary>
        public void SyncFrom(CommLog log)
        {
            if (log == null)
                return;

            bool rebuild;
            long from;
            log.GetView(_shownUpTo, out rebuild, out from);

            if (rebuild)
            {
                var lines = new List<string>();
                for (long i = log.FirstIndex; i < log.NextIndex; i++)
                    lines.Add(log.LineAt(i));

                LogBox.Text = string.Join(Environment.NewLine, lines.ToArray());
            }
            else if (from < log.NextIndex)
            {
                var builder = new StringBuilder();
                for (long i = from; i < log.NextIndex; i++)
                    builder.Append(Environment.NewLine).Append(log.LineAt(i));

                LogBox.AppendText(builder.ToString());
            }
            else
            {
                return;     // 没有新行——不 Join、不重赋 Text，这正是当初改按行号增量的原因
            }

            _shownUpTo = log.NextIndex;

            // 页内那个框一直滚到底；这里给用户一个关掉的开关，方便一边看历史一边等新日志
            if (AutoScrollCheck.IsChecked == true)
                LogBox.ScrollToEnd();
        }
    }
}
