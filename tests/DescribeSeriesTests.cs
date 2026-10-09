using System;
using System.Collections.Generic;
using PoECommandTool;
using PoECommandTool.Chart;
using PoECommandTool.Serial;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        private static void DescribeSeriesTests()
        {
            Console.WriteLine("DescribeSeries（开始轮询前预建图例）");

            // 关键不变式：没有数据时「预告」出来的条目，必须和真数据抽出来的完全一致，
            // 否则一开始轮询就会重复冒出另一份曲线（key 不同就是两条）
            PortMeasurement parsed = Rtl8239ResponseParser.ParsePortMeasurement(
                Frame("44 01 03 00 C8 00 80 00 C8 01 2C"));
            IList<SeriesCandidate> fromData = TelemetryExtractor.Extract(parsed, "0x44 端口3");
            IList<SeriesCandidate> described = TelemetryExtractor.DescribeSeries(0x44, "0x44 端口3");

            CheckEq(described.Count, fromData.Count, "0x44 预告条数 = 实数据条数");
            bool same = described.Count == fromData.Count;
            for (int i = 0; same && i < described.Count; i++)
            {
                if (described[i].Key != fromData[i].Key
                    || described[i].Name != fromData[i].Name
                    || described[i].Unit != fromData[i].Unit)
                    same = false;
            }
            Check(same, "预告的 key / 名字 / 单位与实数据逐条一致");

            IList<SeriesCandidate> fromData41 = TelemetryExtractor.Extract(
                Rtl8239ResponseParser.ParseGlobalPowerStatus(Frame("41 01 01 2C 01 2C 00 01 2C FF FF")), "0x41");
            IList<SeriesCandidate> described41 = TelemetryExtractor.DescribeSeries(0x41, "0x41");
            CheckEq(described41.Count, fromData41.Count, "0x41 预告条数一致");
            Check(described41.Count == fromData41.Count && described41[0].Key == fromData41[0].Key,
                "无端口命令的 key 也一致（都是 0x41.*）");

            IList<SeriesCandidate> fromData4F = TelemetryExtractor.Extract(
                Rtl8239ResponseParser.ParsePortChannelVoltageCurrent(Frame("4F 01 07 00 C8 00 80 00 C8 00 80")),
                "0x4F 端口7");
            IList<SeriesCandidate> described4F = TelemetryExtractor.DescribeSeries(0x4F, "0x4F 端口7");
            CheckEq(described4F.Count, fromData4F.Count, "0x4F 预告条数一致");
            CheckEq(described4F[3].Key, fromData4F[3].Key, "0x4F 第 4 条 key 一致");

            // 0x42 本来就没有数值量 → 预告也是空的（界面上就是个空组，不该凭空建曲线）
            CheckEq(TelemetryExtractor.DescribeSeries(0x42, "0x42 端口0").Count, 0, "0x42 预告 0 条");
            CheckEq(TelemetryExtractor.DescribeSeries(0x99, "x").Count, 0, "未知命令返回空，不抛");
            CheckEq(TelemetryExtractor.DescribeSeries(0x44, null).Count, 4, "tag 为 null 也不抛");

            // 命令号换算：目录里所有可轮询命令都要能换出正确的命令号
            bool allOk = true;
            foreach (CommandDef cmd in Rtl8239Catalog.All)
            {
                if (!PollPlan.IsPollable(cmd))
                    continue;
                byte id = PollPlan.CommandIdOf(cmd);
                if (id == 0)
                {
                    allOk = false;
                    Check(false, cmd.Key + " 命令号换算失败");
                    break;
                }
            }
            Check(allOk, "所有可轮询命令都能换出命令号");
            CheckEq(PollPlan.CommandIdOf(Item("0x44", 0).Command), (byte)0x44, "0x44 → 0x44");
            CheckEq(PollPlan.CommandIdOf(Item("0x50", 0).Command), (byte)0x50, "0x50 → 0x50");
            CheckEq(PollPlan.CommandIdOf(null), (byte)0, "null 命令返回 0");

            // 多端口展开后，每个端口都要预告出自己那一份
            var wanted = new List<SeriesCandidate>();
            for (byte port = 0; port < 3; port++)
            {
                string tag = "0x44 端口" + port;
                foreach (SeriesCandidate c in TelemetryExtractor.DescribeSeries(0x44, tag))
                    wanted.Add(c);
            }
            CheckEq(wanted.Count, 12, "3 个端口 × 4 条 = 12 条预告");
            Check(wanted[4].Key.StartsWith("0x44 端口1", StringComparison.Ordinal), "第 5 条属于端口 1");
            Console.WriteLine();
        }
    }
}
