using System;
using System.Collections.Generic;
using PoECommandTool.Serial;

namespace Rtl8239Verify
{
    /// <summary>
    /// 裸帧扫描器的 Loader 应答模式（固件下载期间）。
    ///
    /// 4 字节应答**没有校验和**，同步规则只能靠命令 ID 前缀，所以这些断言是在钉
    /// 「什么会被当成一帧、什么会被丢掉」——错了的话表现为下载中途偏移对不上，
    /// 而现场很难看出是这里的问题。
    /// </summary>
    internal static partial class Program
    {
        private static void LoaderAckScanTests()
        {
            Console.WriteLine("RawFrameScanner Loader 应答模式");

            // 默认（非下载）模式下，4 字节应答不会被同步成帧——12 字节规则不变
            var normal = new RawFrameScanner();
            CheckEq(normal.Append(new byte[] { 0xC0, 0x81, 0x00, 0x00 }, 4).Count, 0,
                "默认模式下 4 字节应答不成帧");

            // 下载模式：0xC0 / 0xCA 开头的 4 字节被同步出来
            var scanner = new RawFrameScanner { LoaderAckMode = true };

            IList<byte[]> one = scanner.Append(new byte[] { 0xC0, 0x81, 0x20, 0x00 }, 4);
            CheckEq(one.Count, 1, "一个应答 → 一帧");
            CheckEq(one[0].Length, 4, "长度是 4");
            CheckEq(one[0][1], (byte)0x81, "子命令原样保留");
            CheckEq(one[0][2], (byte)0x20, "偏移低字节");
            CheckEq(one[0][3], (byte)0x00, "偏移高字节");

            // 帧可能被拆在两次读之间
            var split = new RawFrameScanner { LoaderAckMode = true };
            CheckEq(split.Append(new byte[] { 0xCA, 0x05 }, 2).Count, 0, "半个应答不成帧");
            IList<byte[]> rest = split.Append(new byte[] { 0x10, 0x00 }, 2);
            CheckEq(rest.Count, 1, "补齐后成帧");
            CheckEq(rest[0][0], (byte)0xCA, "命令 ID 是 0xCA");

            // 非 0xC0/0xCA 开头的字节被跳过并计入丢弃
            var scan = new RawFrameScanner { LoaderAckMode = true };
            IList<byte[]> mixed = scan.Append(new byte[] { 0x11, 0x22, 0xC0, 0x82, 0x00, 0x00 }, 6);
            CheckEq(mixed.Count, 1, "噪声之后仍能同步出应答");
            CheckEq(scan.DroppedByteCount, 2, "两个噪声字节计入丢弃：" + scan.DroppedByteCount);

            // 连续多个应答
            var many = new RawFrameScanner { LoaderAckMode = true };
            IList<byte[]> batch = many.Append(new byte[]
            {
                0xC0, 0x80, 0x00, 0x00,
                0xC0, 0x80, 0x20, 0x00,
                0xCA, 0x01, 0x00, 0x01,
            }, 12);
            CheckEq(batch.Count, 3, "一次读进来三个应答 → 三帧");
            CheckEq(batch[1][2], (byte)0x20, "第二帧的偏移");
            CheckEq(batch[2][0], (byte)0xCA, "第三帧是 0xCA");

            // 复位
            many.Reset();
            CheckEq(many.Append(new byte[] { 0xC0, 0x80, 0x00 }, 3).Count, 0, "复位后残缺字节不成帧");

            Check(RawFrameScanner.IsShortAckId(0xC0) && RawFrameScanner.IsShortAckId(0xCA),
                "0xC0 / 0xCA 是应答命令");
            Check(!RawFrameScanner.IsShortAckId(0x44), "0x44 不是");
            CheckEq(RawFrameScanner.LoaderAckLength, 4, "应答长度是 4");
        }
    }
}
