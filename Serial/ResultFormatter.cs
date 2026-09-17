using System;
using System.Reflection;
using System.Text;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 把解析结果（<see cref="Rtl8239ResponseParser"/> 返回的那些结构体）渲染成可读文本。
    ///
    /// 用反射而不是手写格式化，是为了**加新命令时不用再写一遍**——代价是字段名原样出现、
    /// 没有单位后缀、没有本地化，`Description` 字段混在其它字段里照原样打印。
    /// 要那种「一行摘要」请看 <see cref="WpfApp1.ResponseSummarizer"/>。
    ///
    /// 放在纯逻辑层是有原因的：它原先只存在于 WPF 那半边，于是断言工程只好抄一份副本，
    /// 结果就是**改真代码、测试照旧全绿**。现在两边用的是同一份。
    /// </summary>
    public static class ResultFormatter
    {
        public static string Format(object result)
        {
            var builder = new StringBuilder();
            Append(builder, result, 0);
            return builder.ToString();
        }

        public static void Append(StringBuilder sb, object obj, int indent)
        {
            string pad = new string(' ', indent);
            if (obj == null)
            {
                sb.AppendLine(pad + "null");
                return;
            }

            Type type = obj.GetType();

            if (obj is Array array)
            {
                for (int i = 0; i < array.Length; i++)
                {
                    object item = array.GetValue(i);
                    if (item == null)
                    {
                        sb.AppendLine(pad + "[" + i + "] null");
                        continue;
                    }

                    if (IsSimpleValue(item))
                    {
                        sb.AppendLine(pad + "[" + i + "] " + item);
                    }
                    else
                    {
                        // 嵌套的数组 / 结构体：另起一行递归展开，否则这里只能打印类型名
                        sb.AppendLine(pad + "[" + i + "]");
                        Append(sb, item, indent + 4);
                    }
                }
                return;
            }

            if (type.IsPrimitive || obj is string || obj is bool)
            {
                sb.AppendLine(pad + obj);
                return;
            }

            if (type.IsEnum)
            {
                sb.AppendLine(pad + type.Name + "." + obj);
                return;
            }

            // 结构体：按公共字段展开
            sb.AppendLine(pad + type.Name + ":");
            foreach (FieldInfo field in type.GetFields())
            {
                object value = field.GetValue(obj);
                sb.Append(pad + "  " + field.Name + ": ");
                if (value == null)
                {
                    sb.AppendLine("null");
                    continue;
                }

                if (IsSimpleValue(value))
                {
                    sb.AppendLine(value.ToString());
                }
                else
                {
                    // 数组 / 嵌套结构体：另起一行递归展开，否则这里只能打印类型名
                    sb.AppendLine();
                    Append(sb, value, indent + 4);
                }
            }
        }

        /// <summary>能一行打完的值。</summary>
        public static bool IsSimpleValue(object value)
        {
            Type type = value.GetType();
            return type.IsPrimitive || type.IsEnum || value is string || value is decimal;
        }
    }
}
