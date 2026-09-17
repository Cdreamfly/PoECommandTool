using System;
using WpfApp1.Serial;

namespace Rtl8239Verify
{
    /// <summary>
    /// 命令键名的解析，以及两条跟着命令走的规则（子命令比对、全空响应的超时解释）。
    ///
    /// 这两条规则都写错过一次，而且都是**静默**的：一个把 0xC0-40 的后缀按十进制读成 0x28，
    /// 一个把 0x4B 的 Bank ID 当成子命令。所以这里逐个钉住。
    /// </summary>
    internal static partial class Program
    {
        private static CommandKey Key(string s)
        {
            return CommandKey.Parse(s);
        }

        private static FrameEvent Rsp(params byte[] head)
        {
            return new FrameEvent { Raw = head };
        }

        private static void CommandKeyTests()
        {
            Console.WriteLine("CommandKey 键名解析");

            // --- 键名每一段都按十六进制读 ---
            // 这一条是回归：ParseNumber 对既无 0x 前缀又不含 A-F 的串按十进制读，
            // "40" 会变成 0x28，而构造器写进去的是 0x40。
            CheckEq(Key("0xC0-40").SubCommand, (byte)0x40, "0xC0-40 的后缀按十六进制 → 0x40（不是十进制 40=0x28）");
            CheckEq(Key("0xC0-04").SubCommand, (byte)0x04, "0xC0-04 → 0x04");
            CheckEq(Key("0xC0-00").SubCommand, (byte)0x00, "0xC0-00 → 0x00");
            CheckEq(Key("0xC0-0A").SubCommand, (byte)0x0A, "0xC0-0A → 0x0A（含 A-F 的串）");
            CheckEq(Key("0xC0-83").SubCommand, (byte)0x83, "0xC0-83 → 0x83");

            CheckEq(Key("0xC0-40").CommandId, (byte)0xC0, "0xC0-40 的命令号是 0xC0");
            CheckEq(Key("0x44").CommandId, (byte)0x44, "0x44 的命令号");
            Check(Key("0xC0-40").HasSubCommand, "带 '-' 的键名有子命令");
            Check(!Key("0x44").HasSubCommand, "0x44 没有子命令");
            Check(!Key("0x4B").HasSubCommand, "0x4B 没有子命令（它的 Byte1 是 Bank ID，不是子命令）");

            // --- 序列号可配对性沿用的是会话的既有判据 ---
            Check(Key("0x44").SequenceCorrelatable, "0x44 可按序列号配对");
            Check(!Key("0xC0-04").SequenceCorrelatable, "0xC0 系不可按序列号配对");
            Check(!Key("0x4B").SequenceCorrelatable, "0x4B 不可按序列号配对");

            // --- 非法键名要立刻炸，不能悄悄算出一个错值 ---
            Check(ThrownBy(delegate { CommandKey.Parse(""); }).StartsWith("ArgumentException", StringComparison.Ordinal),
                "空键名抛 ArgumentException");
            Check(ThrownBy(delegate { CommandKey.Parse("0xZZ"); }).StartsWith("FormatException", StringComparison.Ordinal),
                "非十六进制字符抛 FormatException");
            Check(ThrownBy(delegate { CommandKey.Parse("0x1FF"); }).StartsWith("FormatException", StringComparison.Ordinal),
                "超过 0xFF 抛 FormatException");
            Check(ThrownBy(delegate { CommandKey.Parse("0xC0-"); }).StartsWith("FormatException", StringComparison.Ordinal),
                "子命令为空抛 FormatException");
        }

        private static void CommandKeyMismatchTests()
        {
            Console.WriteLine("CommandKey 子命令复核");

            CommandKey version = Key("0xC0-04");

            CheckEq(version.DescribeMismatch(Rsp(0xC0, 0x04, 0x19, 0x08, 0x19, 0x01, 0x00)), null,
                "子命令对上 → 没有问题");

            string bad = version.DescribeMismatch(Rsp(0xC0, 0x01, 0x19));
            Check(bad != null && bad.IndexOf("子命令", StringComparison.Ordinal) >= 0,
                "子命令对不上 → 报出「子命令」字样（实际：" + bad + "）");
            Check(bad != null && bad.IndexOf("0x01", StringComparison.Ordinal) >= 0
                  && bad.IndexOf("0x04", StringComparison.Ordinal) >= 0,
                "报出实际值与期望值");

            // 回归：0x4B 的 Byte1 是 Bank ID。拿它当子命令比对，会把完全正确的响应判成错的。
            CommandKey pm = Key("0x4B");
            CheckEq(pm.DescribeMismatch(Rsp(0x4B, 0x01)), null, "0x4B 的 Byte1 是 Bank ID，不得当成子命令不符");
            CheckEq(pm.DescribeMismatch(Rsp(0x4B, 0x07)), null, "Bank ID 7 同样不报不符");

            CheckEq(Key("0x44").DescribeMismatch(Rsp(0x44, 0x05, 0x00)), null,
                "无子命令的命令不做这项比对（序列号已在会话层比过）");
            CheckEq(version.DescribeMismatch(null), null, "空帧事件不炸");
            CheckEq(version.DescribeMismatch(Rsp(0xC0)), null, "不足两字节不炸");
        }

        private static void CommandKeyHintTests()
        {
            Console.WriteLine("CommandKey 全空响应的超时解释");

            CheckEq(CommandKey.EmptyResponseHint(0x50, false), string.Empty, "没超时就不加解释");

            string chip = CommandKey.EmptyResponseHint(0x50, true);
            Check(chip.IndexOf("回显", StringComparison.Ordinal) >= 0, "0x50 超时提示点明是被当成回显丢掉的");
            Check(chip.IndexOf("一颗芯片都没有", StringComparison.Ordinal) >= 0, "并点明可能是真的没有芯片");

            Check(CommandKey.EmptyResponseHint(0xC0, true).IndexOf("没有配置版本", StringComparison.Ordinal) >= 0,
                "0xC0 超时提示点明可能是没有配置版本");
            Check(CommandKey.EmptyResponseHint(0x4C, true).IndexOf("该块可能整个是空的", StringComparison.Ordinal) >= 0,
                "0x4C 超时提示点明该块可能是空的");
            CheckEq(CommandKey.EmptyResponseHint(0x44, true), string.Empty,
                "其它命令的超时没有这类歧义，不加解释");
        }
    }
}
