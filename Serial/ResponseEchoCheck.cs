using System;
using System.Collections.Generic;
using System.Globalization;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 核对一个响应帧是不是真的属于我们关心的那一帧——序列号回显与端口回显。
    ///
    /// 单独成类是因为有两个调用方，而它们的「期望值」来源完全不同：
    /// 「响应解析」页是用户手填的两个框，「命令组装」页发送后是**实际发出去的那一帧**。
    /// 两边曾经各写一份、各自一套文案，那是会漂移的；规则只该有一份。
    /// </summary>
    public static class ResponseEchoCheck
    {
        /// <summary>响应里会回显端口的命令。其它命令的 Byte2 不是端口，别拿去比。</summary>
        public static readonly byte[] PortEchoCommands = { 0x42, 0x44, 0x45, 0x48, 0x49, 0x4E, 0x4F };

        /// <summary>
        /// 核对两项回显，返回给人看的提示（可能多条）；没问题就是空列表。
        ///
        /// <paramref name="expectedSeqText"/> / <paramref name="expectedPortText"/> 为空表示不核对那一项。
        /// <paramref name="reportSkipped"/>：核对不了时要不要说明原因。
        /// 「响应解析」页是用户主动填的，要说；「命令组装」页发送后是我们自己知道的事实，说了只是噪音。
        /// </summary>
        public static List<string> NotesFromText(byte[] frame, string expectedSeqText,
            string expectedPortText, bool reportSkipped)
        {
            var notes = new List<string>();
            if (frame == null || frame.Length == 0)
                return notes;

            bool isAppFrame = frame.Length >= Rtl8239CommandBuilder.AppFrameLength;

            try
            {
                string seqText = (expectedSeqText ?? string.Empty).Trim();
                if (seqText.Length > 0)
                    CheckSequence(frame, isAppFrame, seqText, reportSkipped, notes);

                string portText = (expectedPortText ?? string.Empty).Trim();
                if (portText.Length > 0)
                    CheckPort(frame, isAppFrame, portText, reportSkipped, notes);
            }
            catch (FormatException ex)
            {
                notes.Add("期望值无效：" + ex.Message);
            }
            catch (OverflowException)
            {
                notes.Add("期望值超出可解析范围。");
            }

            return notes;
        }

        /// <summary>
        /// 「命令组装」页发送后用的重载：期望值就是实际发出去的那一帧的序列号与端口，
        /// 不是用户手填的，所以不需要「跳过核对」那类解释。
        /// </summary>
        public static List<string> NotesForSent(byte[] frame, byte sentSequence, int? sentPort)
        {
            var notes = new List<string>();
            if (frame == null || frame.Length < Rtl8239CommandBuilder.AppFrameLength)
                return notes;

            if (SerialSession.IsSequenceCorrelatable(frame[0]) && frame[1] != sentSequence)
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "序列号回显不符：期望 0x{0:X2}，实际 0x{1:X2}", sentSequence, frame[1]));

            if (sentPort.HasValue && EchoesPort(frame[0]) && frame[2] != (byte)sentPort.Value)
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "端口回显不符：期望 0x{0:X2}，实际 0x{1:X2}", (byte)sentPort.Value, frame[2]));

            return notes;
        }

        public static bool EchoesPort(byte commandId)
        {
            for (int i = 0; i < PortEchoCommands.Length; i++)
                if (PortEchoCommands[i] == commandId)
                    return true;

            return false;
        }

        /// <summary>把提示拼成一行；没有提示时返回空串。</summary>
        public static string Describe(IList<string> notes)
        {
            if (notes == null || notes.Count == 0)
                return string.Empty;

            var parts = new string[notes.Count];
            for (int i = 0; i < notes.Count; i++)
                parts[i] = notes[i];

            return "⚠ " + string.Join("；", parts);
        }

        private static void CheckSequence(byte[] frame, bool isAppFrame, string seqText,
            bool reportSkipped, List<string> notes)
        {
            if (!isAppFrame)
            {
                if (reportSkipped) notes.Add("响应帧不足 12 字节，无法核对序列号");
                return;
            }

            if (!SerialSession.IsSequenceCorrelatable(frame[0]))
            {
                if (!reportSkipped)
                    return;

                notes.Add(frame[0] == 0x4B
                    ? "0x4B 响应 Byte1 为 Bank ID，跳过序列号核对"
                    : string.Format(CultureInfo.InvariantCulture,
                        "命令 0x{0:X2} 的响应不按序列号配对，跳过序列号核对", frame[0]));
                return;
            }

            byte expected = Rtl8239Catalog.ParseByte(seqText, "期望序列号");
            if (!Rtl8239CommandBuilder.IsResponseValid(frame, expected))
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "序列号不符：期望 0x{0:X2}，实际 0x{1:X2}", expected, frame[1]));
        }

        private static void CheckPort(byte[] frame, bool isAppFrame, string portText,
            bool reportSkipped, List<string> notes)
        {
            if (!EchoesPort(frame[0]))
            {
                if (reportSkipped)
                    notes.Add(string.Format(CultureInfo.InvariantCulture,
                        "命令 0x{0:X2} 的响应不含端口回显，跳过端口核对", frame[0]));
                return;
            }

            if (!isAppFrame)
            {
                if (reportSkipped) notes.Add("响应帧不足 12 字节，无法核对端口");
                return;
            }

            byte expected = Rtl8239Catalog.ParseByte(portText, "期望端口");
            if (frame[2] != expected)
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "端口回显不符：期望 0x{0:X2}，实际 0x{1:X2}", expected, frame[2]));
        }
    }
}
