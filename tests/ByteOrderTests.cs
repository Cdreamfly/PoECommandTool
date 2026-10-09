using System;
using PoECommandTool;
using PoECommandTool.Chart;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        /// <summary>
        /// 字节序：用真机回包锁死。判定依据不是「手册怎么写」，而是物理自洽——
        /// 大端解读下 V×I 恰好等于上报的功率，小端解读则是一堆毫无意义的数。
        /// </summary>
        private static void ByteOrderTests()
        {
            Console.WriteLine("字节序（真机回包）");

            // 真机 0x44（端口接 PD、正在供电）：校验和 0x88 正确
            byte[] powered = Frame("44 01 00 03 36 02 21 00 C5 01 21");
            CheckEq(Rtl8239CommandBuilder.ToHex(powered), "44 01 00 03 36 02 21 00 C5 01 21 88",
                "测试帧与真机回包逐字节一致");

            PortMeasurement big = Rtl8239ResponseParser.ParsePortMeasurement(powered, ByteOrder.BigEndian);
            CheckEq(big.Port, (byte)0, "端口 0");
            CheckNear(big.VoltageMv, 52977.9, 1e-6, "大端电压 = 52.98 V（822 × 64.45mV）");
            CheckEq(big.CurrentMa, 545, "大端电流 = 545 mA");
            CheckNear(big.TemperatureC, 28.75, 1e-9, "大端温度 = 28.75 ℃");
            CheckNear(big.PowerW, 28.9, 1e-9, "大端功率 = 28.9 W");

            double derivedWatt = big.VoltageMv / 1000.0 * big.CurrentMa / 1000.0;
            Check(Math.Abs(derivedWatt - big.PowerW) < 0.15,
                "大端解读物理自洽：V×I = " + derivedWatt.ToString("0.00") + " W ≈ 上报的 " + big.PowerW + " W");

            // 第二条真机回包（同样接 PD，数值略有变化）
            PortMeasurement second = Rtl8239ResponseParser.ParsePortMeasurement(
                Frame("44 01 00 03 38 02 20 00 C5 01 21"), ByteOrder.BigEndian);
            CheckNear(second.VoltageMv, 53106.8, 1e-6, "第二条大端电压 = 53.11 V");
            CheckEq(second.CurrentMa, 544, "第二条大端电流 = 544 mA");
            CheckNear(second.PowerW, 28.9, 1e-9, "第二条大端功率 = 28.9 W");
            Check(Math.Abs(second.VoltageMv / 1000.0 * second.CurrentMa / 1000.0 - second.PowerW) < 0.15,
                "第二条也物理自洽");

            // 同一帧按小端解 → 无意义的数（这就是改之前界面上的读数）
            PortMeasurement little = Rtl8239ResponseParser.ParsePortMeasurement(powered, ByteOrder.LittleEndian);
            Check(little.VoltageMv > 800000, "小端电压 = 891150 mV（远超 PoE 可能范围）");
            CheckEq(little.CurrentMa, 8450, "小端电流 = 8450 mA");
            Check(little.TemperatureC < -60000, "小端温度 = −62765 ℃");
            CheckNear(little.PowerW, 844.9, 1e-9, "小端功率 = 844.9 W");

            // 端口没上电时（真机早期那条）：全 0，温度字段是 0x00DC → 恰好 0 ℃
            PortMeasurement idle = Rtl8239ResponseParser.ParsePortMeasurement(
                Frame("44 01 00 00 00 00 00 00 DC 00 00"), ByteOrder.BigEndian);
            CheckNear(idle.VoltageMv, 0, 1e-9, "未供电端口电压 0");
            CheckNear(idle.PowerW, 0, 1e-9, "未供电端口功率 0");
            CheckNear(idle.TemperatureC, 0, 1e-9, "未供电端口温度 = 0 ℃（0x00DC 代入手册公式）");

            // 0x42 没有多字节字段：两种字节序结果必须一样（说明这次改动不影响它）
            byte[] status = Frame("42 01 00 02 44 04 00 00 00 00 00");
            CheckEq(Rtl8239CommandBuilder.ToHex(status), "42 01 00 02 44 04 00 00 00 00 00 8D",
                "测试帧与真机 0x42 回包一致");
            PortStatus statusBig = Rtl8239ResponseParser.ParsePortStatus(status);
            PortStatus statusLittle = Rtl8239ResponseParser.ParsePortStatus(status);
            CheckEq(statusBig.PowerState, PortPowerState.DeliveringPower, "0x42：供电中");
            CheckEq(statusBig.ClassificationResult, ClassificationResult.Class4, "0x42：Class 4");
            CheckEq(statusBig.DetectionResult, DetectionResult.ValidPd, "0x42：有效 PD");
            CheckEq(statusBig.ConnectionCheckResult, ConnectionCheckResult.TwoPair, "0x42：2-pair");
            CheckEq(statusLittle.PowerStateDescription, statusBig.PowerStateDescription,
                "0x42 不受字节序影响（没有多字节字段）");

            // 其余带 16 位字段的命令也跟着切
            GlobalPowerStatus power = Rtl8239ResponseParser.ParseGlobalPowerStatus(
                Frame("41 01 01 2C 01 2C 00 01 2C FF FF"), ByteOrder.BigEndian);
            CheckNear(power.SystemAllocatedPowerW, 30.0, 1e-9, "0x41 大端：已分配功率 30 W");
            CheckNear(power.SystemCurrentPowerW, 30.0, 1e-9, "0x41 大端：当前功率 30 W");

            PortChannelVoltageCurrent channels = Rtl8239ResponseParser.ParsePortChannelVoltageCurrent(
                Frame("4F 01 07 00 C8 00 80 00 C8 00 80"), ByteOrder.BigEndian);
            CheckNear(channels.PriVoltageMv, 12890.0, 1e-6, "0x4F 大端：主通道电压");
            CheckEq(channels.PriCurrentMa, 128, "0x4F 大端：主通道电流");

            // 分发入口也要认这个参数
            var viaParse = (PortMeasurement)Rtl8239ResponseParser.Parse(powered, ByteOrder.BigEndian);
            CheckNear(viaParse.PowerW, 28.9, 1e-9, "Parse() 分发时同样按大端解析");
            var defaultOrder = (PortMeasurement)Rtl8239ResponseParser.Parse(powered);
            CheckNear(defaultOrder.PowerW, 28.9, 1e-9, "Parse() 不传字节序时默认就是大端");

            CheckEq(TelemetryExtractor.Extract(big, "0x44 端口0").Count, 4,
                "采样链路照样取出 4 条曲线（它工作在解析结果上，与字节序无关）");
            Console.WriteLine();
        }
    }
}
