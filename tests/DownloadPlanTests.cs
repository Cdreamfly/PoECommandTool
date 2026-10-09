using System;
using System.Collections.Generic;
using PoECommandTool;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        // =================================================================
        //  下载帧规划（SEC-01 / 审查发现 C-2）
        //
        //  固件（0xCA）的镜像偏移字段只有 16 位，即最大 64KB。超过 64KB 时原实现是
        //  (ushort)(off & 0xFFFF) —— 静默回绕，把后面的数据写到 flash 开头，而且输出行
        //  打印的是「声明偏移」而不是「编码偏移」，把回绕藏了起来。
        //  修复后必须**拒绝**而不是回绕：生成的帧无法被本工具复核（帧长可变，手动发送
        //  只接受 12 字节帧），它是最后一道防线。
        //
        //  注意尺寸必须 32 字节对齐，否则会先被对齐检查拦下、测不到尺寸检查。
        // =================================================================
        private static void DownloadPlanTests()
        {
            Console.WriteLine("下载帧规划 DownloadPlan（SEC-01：固件偏移 16 位上限）");

            // ---- 尺寸表：两种模式各自的上限 ----
            int[] sizes = { 32, 4 * 1024, 64 * 1024, 64 * 1024 + 32, 128 * 1024, 256 * 1024, 256 * 1024 + 32 };
            foreach (int size in sizes)
            {
                byte[] data = new byte[size];
                for (int i = 0; i < size; i++) data[i] = (byte)(i & 0xFF);

                // App 模式：上限 256KB（4 个 64K 块）
                string appError = ThrownBy(delegate { Rtl8239DownloadPlan.Build(data, 0x01, DownloadMode.App); });
                Check(size <= Rtl8239DownloadPlan.AppImageLimit || appError.StartsWith("InvalidOperationException"),
                    "App 模式 " + size + " 字节：" + (size <= Rtl8239DownloadPlan.AppImageLimit ? "接受" : "拒绝 -> " + appError));

                // Firmware 模式：上限 64KB（16 位镜像偏移）
                string fwError = ThrownBy(delegate { Rtl8239DownloadPlan.Build(data, 0x01, DownloadMode.Firmware); });
                Check(size <= Rtl8239DownloadPlan.FirmwareImageLimit || fwError.StartsWith("InvalidOperationException"),
                    "Firmware 模式 " + size + " 字节：" + (size <= Rtl8239DownloadPlan.FirmwareImageLimit ? "接受" : "拒绝 -> " + fwError));
            }

            // ---- 偏移必须严格递增、绝不回绕（修复前 128KB 固件会产出 0x0000 的重复偏移）----
            // 修复后 128KB 应被拒绝，所以这里用合法的 64KB 验证「不回绕」这一性质本身。
            IList<DownloadFrame> fw64 = Rtl8239DownloadPlan.Build(new byte[64 * 1024], 0x01, DownloadMode.Firmware);
            int previous = -1;
            bool strictlyIncreasing = true;
            bool allEncodedOffsetsMatch = true;
            foreach (DownloadFrame f in fw64)
            {
                if (f.Offset <= previous) strictlyIncreasing = false;
                if (f.ImageOffset != (ushort)f.Offset) allEncodedOffsetsMatch = false;
                previous = f.Offset;
            }
            Check(strictlyIncreasing, "Firmware 64KB：偏移严格递增（" + fw64.Count + " 帧）");
            Check(allEncodedOffsetsMatch, "Firmware 64KB：编码进帧的偏移 == 声明偏移（无回绕、无隐藏）");
            CheckEq(fw64.Count, 64 * 1024 / 32, "Firmware 64KB：帧数 = 尺寸/32");
            CheckEq(fw64[0].ImageOffset, (ushort)0, "Firmware 首帧偏移为 0");
            CheckEq(fw64[fw64.Count - 1].Offset + fw64[fw64.Count - 1].DataLength, 64 * 1024,
                    "Firmware 末帧覆盖到文件末尾（不丢字节）");

            // ---- App 模式的分块：SUB 配对与块内偏移归零 ----
            IList<DownloadFrame> app128 = Rtl8239DownloadPlan.Build(new byte[128 * 1024], 0x01, DownloadMode.App);
            int perBlock = 0x10000 / 32;
            for (int block = 0; block < 2; block++)
            {
                DownloadFrame head = app128[block * perBlock];
                CheckEq(head.Sub, 0x80 + block, "App 第 " + block + " 块的 SUB = 0x" + (0x80 + block).ToString("X2"));
                CheckEq(head.ImageOffset, (ushort)0, "App 第 " + block + " 块首帧的块内偏移归零");
            }

            // ---- 对齐规则：Firmware 只接受 4/8/16/32；App 必须 4 字节对齐 ----
            Check(ThrownBy(delegate { Rtl8239DownloadPlan.Build(new byte[33], 0x01, DownloadMode.Firmware); })
                      .StartsWith("InvalidOperationException"), "Firmware 33 字节（非 32 对齐）被拒绝");
            Check(ThrownBy(delegate { Rtl8239DownloadPlan.Build(new byte[34], 0x01, DownloadMode.App); })
                      .StartsWith("InvalidOperationException"), "App 34 字节（非 4 对齐）被拒绝");

            // ---- 空输入不产生帧，也不抛 ----
            CheckEq(Rtl8239DownloadPlan.Build(new byte[0], 0x01, DownloadMode.Firmware).Count, 0, "空文件生成 0 帧");

            // ---- 边界正例：App 恰好 256KB 应被接受 ----
            CheckEq(Rtl8239DownloadPlan.Build(new byte[256 * 1024], 0x01, DownloadMode.App).Count,
                    256 * 1024 / 32, "App 恰好 256KB 被接受（4 个块）");
        }
    }
}
