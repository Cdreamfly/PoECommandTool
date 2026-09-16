using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WpfApp1.Serial
{
    /// <summary>发送方式。</summary>
    public enum SendMode
    {
        /// <summary>按模板拼成一条文本命令（例如调试控制台的 uart_test 命令）。</summary>
        TextCommand = 0,

        /// <summary>直接把命令帧的原始字节写进串口（串口直连 PoE 控制器时用）。</summary>
        RawFrame = 1,
    }

    /// <summary>文本命令的行结束符。</summary>
    public enum LineEnding
    {
        None = 0,
        Cr = 1,
        Lf = 2,
        CrLf = 3,
    }

    /// <summary>
    /// 发送方式的完整配置（界面上一组控件对应它）。
    /// </summary>
    public sealed class SendOptions
    {
        public SendOptions()
        {
            Mode = SendMode.TextCommand;
            Template = CommandTemplate.DefaultTemplate;
            LineEnding = LineEnding.CrLf;
        }

        public SendMode Mode { get; set; }
        public string Template { get; set; }
        public LineEnding LineEnding { get; set; }

        /// <summary>按当前配置为某一帧生成发送请求。</summary>
        public SendRequest For(byte[] frame)
        {
            return new SendRequest
            {
                Mode = Mode,
                Frame = frame,
                Template = Template,
                LineEnding = LineEnding,
            };
        }
    }

    /// <summary>
    /// 把 12 字节命令帧套进一条设备命令模板。
    ///
    /// 不同设备的调试控制台命令格式不一样（提示符、尾部参数都不同），所以模板整条可配，
    /// 只要求里面出现 <see cref="FramePlaceholder"/>；<see cref="DefaultTemplate"/> 是
    /// 按 project 里给出的样例设备填的默认值。
    /// </summary>
    public static class CommandTemplate
    {
        /// <summary>命令帧的占位符，会被展开成 "42 01 00 FF … 3B" 这样的十六进制串。</summary>
        public const string FramePlaceholder = "{FRAME}";

        /// <summary>命令帧长度的占位符，展开成十进制字节数（12）。有些控制台要求带上长度。</summary>
        public const string LengthPlaceholder = "{LEN}";

        /// <summary>
        /// 默认模板，按真机回显确认过：设备接受的是
        /// <c>uart_test 44 01 00 FF FF FF FF FF FF FF FF 3D 12 500000</c>。
        /// 注意 <c>debug#</c> 是设备自己的提示符，**不能**写进模板里。
        /// </summary>
        public const string DefaultTemplate = "uart_test {FRAME} {LEN} 500000";

        /// <summary>模板为空时的回落值：只发帧本身。</summary>
        public const string FallbackTemplate = FramePlaceholder;

        /// <summary>
        /// 用帧的十六进制串替换模板里所有 {FRAME}，用字节数替换 {LEN}。
        /// 模板为空 / 空白时回落到 <see cref="FallbackTemplate"/>。
        /// </summary>
        public static string Fill(string template, byte[] frame)
        {
            if (string.IsNullOrEmpty(template) || template.Trim().Length == 0)
                template = FallbackTemplate;

            string text = template.Replace(FramePlaceholder, Rtl8239CommandBuilder.ToHex(frame));
            return text.Replace(LengthPlaceholder,
                (frame == null ? 0 : frame.Length).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 模板能否用。不能用时返回 false 并给出提示（UI 上显示黄字，但仍然允许发送，
        /// 因为有些设备确实只需要前缀、帧由别的方式给）。
        /// </summary>
        public static bool TryValidate(string template, out string warning)
        {
            warning = null;

            if (string.IsNullOrEmpty(template) || template.Trim().Length == 0)
            {
                warning = "模板为空，将只发送命令帧的十六进制串。";
                return false;
            }

            if (template.IndexOf(FramePlaceholder, StringComparison.Ordinal) < 0)
            {
                warning = "模板里没有 " + FramePlaceholder + " 占位符，命令帧不会被发出去。";
                return false;
            }

            return true;
        }

        /// <summary>行结束符对应的字节（None 返回空数组）。</summary>
        public static byte[] Terminator(LineEnding ending)
        {
            switch (ending)
            {
                case LineEnding.Cr: return new byte[] { 0x0D };
                case LineEnding.Lf: return new byte[] { 0x0A };
                case LineEnding.CrLf: return new byte[] { 0x0D, 0x0A };
                default: return Array.Empty<byte>();
            }
        }

        /// <summary>把模板内容 + 行结束符拼成实际要写进串口的字节。</summary>
        public static byte[] BuildTextCommand(string template, byte[] frame, LineEnding ending)
        {
            byte[] head = Encoding.ASCII.GetBytes(Fill(template, frame));
            byte[] tail = Terminator(ending);
            if (tail.Length == 0)
                return head;

            var all = new byte[head.Length + tail.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(tail, 0, all, head.Length, tail.Length);
            return all;
        }
    }
}
