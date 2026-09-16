using System;
using System.Collections.Generic;

namespace WpfApp1
{
    /// <summary>下载类型：App 分区（0xC0-80~83）或 Firmware（0xCA）。</summary>
    public enum DownloadMode
    {
        App,
        Firmware,
    }

    /// <summary>一条下载帧及其规划信息。</summary>
    public sealed class DownloadFrame
    {
        /// <summary>该帧数据在原文件中的字节偏移（声明值，用于显示）。</summary>
        public int Offset { get; private set; }

        /// <summary>实际写入帧 Byte2-3 的镜像偏移（编码值）。</summary>
        public ushort ImageOffset { get; private set; }

        /// <summary>App 模式的 64K 块号 SUB（0x80+块号）；Firmware 模式无 SUB，为 -1。</summary>
        public int Sub { get; private set; }

        /// <summary>该帧携带的数据长度（4/8/16/32）。</summary>
        public int DataLength { get; private set; }

        /// <summary>完整帧字节。</summary>
        public byte[] Bytes { get; private set; }

        public DownloadFrame(int offset, ushort imageOffset, int sub, int dataLength, byte[] bytes)
        {
            Offset = offset;
            ImageOffset = imageOffset;
            Sub = sub;
            DataLength = dataLength;
            Bytes = bytes;
        }
    }

    /// <summary>
    /// 固件下载帧序列的生成规划（纯逻辑，不依赖 WPF，可在无界面环境下断言）。
    ///
    /// 注意：本工具只**生成**帧，不发送。帧长度可变（4~36 字节），而手动发送只接受 12 字节帧，
    /// 因此生成的十六进制文本需要交给外部 Loader 写入设备——它离开了本工具的校验范围，
    /// 所以这里的所有边界检查都是最后一道防线。
    /// </summary>
    public static class Rtl8239DownloadPlan
    {
        /// <summary>分帧大小。</summary>
        public const int ChunkSize = 32;

        /// <summary>App 模式镜像上限：4 个 64K 块。</summary>
        public const int AppImageLimit = 256 * 1024;

        /// <summary>Firmware 模式镜像上限：镜像偏移字段只有 16 位，即 64KB。</summary>
        public const int FirmwareImageLimit = 0x10000;

        /// <summary>
        /// 按模式生成完整的下载帧序列。任何越界都会抛异常而不是静默截断——
        /// 生成的帧无法被本工具复核，静默截断等于把损坏的镜像直接交给设备。
        /// </summary>
        public static IList<DownloadFrame> Build(byte[] data, byte seq, DownloadMode mode)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            // 先按模式检查整体尺寸。Firmware 的镜像偏移字段只有 16 位，超过 64KB 时
            // 原实现是 (ushort)(off & 0xFFFF) —— 静默回绕，会让后续数据覆盖 flash 开头，
            // 而输出行打印的是声明偏移，把回绕藏了起来。这里必须**拒绝**：生成的帧交给外部
            // Loader，本工具无法复核，是最后一道防线。
            if (mode == DownloadMode.Firmware && data.Length > FirmwareImageLimit)
                throw new InvalidOperationException(
                    $"Firmware 下载的镜像偏移字段只有 16 位，最大 {FirmwareImageLimit / 1024}KB；" +
                    $"当前文件 {data.Length} 字节，超出范围。请改用 App 下载或分包。");

            if (mode == DownloadMode.App && data.Length > AppImageLimit)
                throw new InvalidOperationException(
                    $"App 下载支持最大 {AppImageLimit / 1024}KB（4 个 64K 块），当前文件 {data.Length} 字节，超出范围。");

            var frames = new List<DownloadFrame>();

            for (int off = 0; off < data.Length; off += ChunkSize)
            {
                int len = Math.Min(ChunkSize, data.Length - off);
                byte[] chunk = new byte[len];
                Array.Copy(data, off, chunk, 0, len);

                if (mode == DownloadMode.App)
                {
                    if (len < 4 || len % 4 != 0)
                        throw new InvalidOperationException(
                            $"App 下载要求 4 字节对齐，但偏移 0x{off:X4} 处剩余 {len} 字节不满足（请确保固件按 4 字节对齐）。");

                    int block = off / 0x10000;
                    if (block > 3)
                        throw new InvalidOperationException(
                            "App 下载支持最大 256KB（4 个 64K 块），固件超出范围。");

                    byte sub = (byte)(0x80 + block);
                    ushort imageOffset = (ushort)(off % 0x10000);
                    frames.Add(new DownloadFrame(off, imageOffset, sub, len,
                        Rtl8239CommandBuilder.AppDownload(sub, imageOffset, chunk)));
                }
                else
                {
                    if (len != 4 && len != 8 && len != 16 && len != 32)
                        throw new InvalidOperationException(
                            $"Firmware 下载单帧仅支持 4/8/16/32 字节，偏移 0x{off:X4} 处剩余 {len} 字节不满足（固件需按 32 字节对齐）。");

                    // 防御性检查：即使上面的整体尺寸检查将来被改动或绕过，回绕也必须在这里暴露，
                    // 而不是被 (ushort) 转换静默吞掉。
                    if (off > 0xFFFF)
                        throw new InvalidOperationException(
                            $"Firmware 镜像偏移 0x{off:X4} 超出 16 位范围（上限 0xFFFF）。");

                    ushort imageOffset = (ushort)off;
                    frames.Add(new DownloadFrame(off, imageOffset, -1, len,
                        Rtl8239CommandBuilder.FirmwareDownload(seq, imageOffset, chunk)));
                }
            }

            return frames;
        }
    }
}
