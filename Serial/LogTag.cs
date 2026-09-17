using System;
using System.Collections.Generic;

namespace PoECommandTool.Serial
{
    /// <summary>
    /// 日志行的类别标签。行形如 <c>14:23:05.123  [设备] 收到的一行原文</c>。
    ///
    /// 这套标签在代码里被生产了很久，却**从来没有被消费过**——筛选、着色、导出都无从谈起。
    /// 放到纯逻辑层是为了让「这一行属于哪一类」成为可断言的事实，而不是各处再写一遍
    /// 字符串搜索（那样只消改一处标签写法就会静默失配）。
    /// </summary>
    public static class LogTag
    {
        /// <summary>全部类别，顺序即界面上复选框的顺序。</summary>
        public static readonly string[] All = { "设备", "发送", "识别", "解析", "工具" };

        /// <summary>
        /// 取出一行开头的类别标签（不含方括号）；没有标签返回 null。
        ///
        /// 只认**第一个**方括号：行内后面可能还有方括号（例如设备原文里就带），
        /// 那些不属于我们的标签体系。
        /// </summary>
        public static string Extract(string line)
        {
            if (string.IsNullOrEmpty(line))
                return null;

            int open = line.IndexOf('[');
            if (open < 0)
                return null;

            int close = line.IndexOf(']', open + 1);
            if (close < 0)
                return null;

            string tag = line.Substring(open + 1, close - open - 1).Trim();
            return tag.Length == 0 ? null : tag;
        }

        /// <summary>
        /// 这一行该不该显示。<paramref name="enabled"/> 为 null 或空表示全都显示；
        /// **没有标签的行**（例如工具自己的分隔线）在开了筛选之后仍然显示——
        /// 它们不属于任何一类，被筛掉只会让日志变得莫名其妙。
        /// </summary>
        public static bool ShouldShow(string line, ICollection<string> enabled)
        {
            if (enabled == null || enabled.Count == 0)
                return true;

            string tag = Extract(line);
            if (tag == null)
                return true;

            foreach (string candidate in enabled)
                if (string.Equals(candidate, tag, StringComparison.Ordinal))
                    return true;

            return false;
        }

        /// <summary>这一行属于哪一类；未知类别原样返回，便于界面如实显示。</summary>
        public static string Describe(string line)
        {
            string tag = Extract(line);
            return tag == null ? "(无类别)" : tag;
        }
    }
}
