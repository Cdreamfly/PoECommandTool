using System;
using System.Collections.Generic;
using WpfApp1;
using WpfApp1.Serial;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        private static void PortListParserTests()
        {
            Console.WriteLine("PortListParser（多端口）");

            byte[] ports;
            string error;

            Check(PortListParser.TryParse("0", out ports, out error) && ports.Length == 1 && ports[0] == 0,
                "单个端口");
            Check(PortListParser.TryParse("0,1,2", out ports, out error), "逗号分隔：" + error);
            CheckEq(ports.Length, 3, "逗号分隔得到 3 个端口");
            CheckEq(ports[2], (byte)2, "顺序保持：第 3 个是 2");

            Check(PortListParser.TryParse("0-3", out ports, out error) && ports.Length == 4, "范围写法 0-3");
            CheckEq(ports[3], (byte)3, "范围右端包含在内");

            Check(PortListParser.TryParse("0x00-0x03", out ports, out error) && ports.Length == 4, "十六进制范围 0x00-0x03");
            Check(PortListParser.TryParse("0,2-4,7", out ports, out error) && ports.Length == 5, "混用写法");
            CheckEq(ports[1], (byte)2, "混用：第 2 个是 2");
            CheckEq(ports[4], (byte)7, "混用：第 5 个是 7");

            Check(PortListParser.TryParse("0 1 2", out ports, out error) && ports.Length == 3, "空格分隔");
            Check(PortListParser.TryParse("0、1、2", out ports, out error) && ports.Length == 3, "中文顿号分隔");
            Check(PortListParser.TryParse(" 0 , 1 ", out ports, out error) && ports.Length == 2, "前后留空格");

            Check(PortListParser.TryParse("1,1,2", out ports, out error) && ports.Length == 2, "重复端口去重");
            Check(PortListParser.TryParse("0-3,2", out ports, out error) && ports.Length == 4, "范围与单点重叠也去重");
            Check(PortListParser.TryParse("5,1,3", out ports, out error) && ports[0] == 5 && ports[2] == 3,
                "顺序按输入走，不排序");

            Check(PortListParser.TryParse("0-47", out ports, out error) && ports.Length == 48, "全范围 0-47 可用");
            Check(PortListParser.TryParse("a", out ports, out error) && ports[0] == 10,
                "含 A-F 时按十六进制（与命令组装页的数字规则一致）");

            Check(!PortListParser.TryParse("", out ports, out error) && error.Contains("不能为空"),
                "空输入被拒绝：" + error);
            Check(!PortListParser.TryParse("48", out ports, out error) && error.Contains("超出范围"),
                "超范围端口被拒绝：" + error);
            Check(!PortListParser.TryParse("0x30", out ports, out error), "0x30 超出范围被拒绝");
            Check(!PortListParser.TryParse("3-1", out ports, out error) && error.Contains("起始"),
                "倒序范围被拒绝：" + error);
            Check(!PortListParser.TryParse("zz", out ports, out error), "无法识别的端口被拒绝");
            Check(!PortListParser.TryParse("0-", out ports, out error), "缺右端的范围被拒绝");
            Check(!PortListParser.TryParse(",", out ports, out error), "只有分隔符被拒绝");

            // 展开成多条轮询项之后，帧要带各自的端口
            PollItem item = Item("0x44", 0x00);
            PortListParser.TryParse("0-2", out ports, out error);
            var plan = new PollingPlan();
            for (int i = 0; i < ports.Length; i++)
                plan.Items.Add(new PollItem { Command = item.Command, Port = ports[i], Enabled = true });

            CheckEq(plan.Items.Count, 3, "一条命令 + 三个端口 → 三条轮询项");
            CheckEq(Rtl8239CommandBuilder.ToHex(PollPlan.BuildFrame(plan.Items[2], 0x01)),
                Rtl8239CommandBuilder.ToHex(Rtl8239CommandBuilder.PortMeasurementGet(0x01, 0x02)),
                "第 3 条轮询项的帧用的是端口 2");
            Console.WriteLine();
        }

        private static void ResponseSummarizerTests()
        {
            Console.WriteLine("ResponseSummarizer（识别结果打印）");

            // 0x44：四个测量值
            string measurement = ResponseSummarizer.Summarize(
                Rtl8239ResponseParser.ParsePortMeasurement(Frame("44 01 03 00 C8 00 80 00 C8 01 2C")), "0x44 端口3");
            Check(measurement != null && measurement.StartsWith("0x44 端口3："), "摘要带标签：" + measurement);
            Check(measurement.Contains("电压 12.89 V"), "摘要把 mV 大数换成了 V：" + measurement);
            Check(measurement.Contains("电流 128 mA"), "摘要含电流（1000 以下不换算）");
            Check(measurement.Contains("温度 25 ℃"), "摘要含温度");
            Check(measurement.Contains("功率 30 W"), "摘要含功率");

            // 0x42：只有描述字段，没有数值量
            string healthy = ResponseSummarizer.Summarize(
                Rtl8239ResponseParser.ParsePortStatus(Frame("42 01 05 02 44 FF FF 00 FF FF FF")), "0x42 端口5");
            Check(healthy != null && healthy.Contains("供电中"), "端口状态摘要：" + healthy);
            Check(healthy.Contains("有效 PD") || healthy.Contains("有效PD"), "摘要含检测结果");
            Check(healthy.Contains("2-pair"), "摘要含连接检查结果");
            Check(!healthy.Contains("无故障"), "正常端口不刷「无故障」占位描述");

            // 故障端口要把故障类型打出来
            string faulted = ResponseSummarizer.Summarize(
                Rtl8239ResponseParser.ParsePortStatus(Frame("42 01 05 04 05 FF FF 00 FF FF FF")), "0x42 端口5");
            Check(faulted != null && faulted.Contains("故障"), "故障端口摘要带故障：" + faulted);
            Check(faulted.Contains("过温关断"), "故障类型也打出来：" + faulted);

            // 0x41：系统功率
            string power = ResponseSummarizer.Summarize(
                Rtl8239ResponseParser.ParseGlobalPowerStatus(Frame("41 01 01 2C 01 2C 00 01 2C FF FF")), "0x41");
            Check(power != null && power.Contains("系统已分配功率"), "系统功率摘要：" + power);
            Check(power.Contains("30 W"), "系统功率数值：" + power);

            // 没有可展示内容时返回 null（调用方就不打这一行）
            CheckEq(ResponseSummarizer.Summarize(null, "x"), null, "null 输入返回 null");
            CheckEq(ResponseSummarizer.Summarize(new object(), "x"), null, "无可展示字段时返回 null");
            Console.WriteLine();
        }
    }
}
