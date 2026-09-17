using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using WpfApp1;
using WpfApp1.Serial;

namespace Rtl8239Verify
{
    internal static partial class Program
    {
        private static int _passed;
        private static int _failed;

        private static void Check(bool condition, string name)
        {
            if (condition) { _passed++; Console.WriteLine("  PASS  " + name); }
            else { _failed++; Console.WriteLine("  FAIL  " + name); }
        }

        private static void CheckEq(object actual, object expected, string name)
        {
            if (Equals(actual, expected)) { _passed++; Console.WriteLine("  PASS  " + name); }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL  " + name + "  expected=[" + expected + "] actual=[" + actual + "]");
            }
        }

        // 由 11 个字节（hex 串）构造一条校验和合法的 12 字节 App 响应帧。
        private static byte[] Frame(string hex11)
        {
            string[] parts = hex11.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 11) throw new ArgumentException("需要 11 个字节：" + hex11);
            byte[] f = new byte[12];
            for (int i = 0; i < 11; i++) f[i] = Convert.ToByte(parts[i], 16);
            f[11] = Rtl8239CommandBuilder.Checksum(f, 11);
            return f;
        }

        private static string ThrownBy(Action action)
        {
            try { action(); return "<no exception>"; }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        private static async Task<int> Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            GroupStatusTests();
            PortStatusTests();
            DispatcherTests();
            ConfigurationTests();
            ValidationTests();
            RenderingTests();
            ExpectationTests();
            HexUtilTests();
            LineAssemblerTests();
            CommandTemplateTests();
            ResponseLineExtractorTests();
            await SessionTests();
            PollPlanTests();
            PollingSchedulerTests();
            await PollingRunnerTests();
            TelemetryExtractorTests();
            SeriesBufferTests();
            ChartMathTests();
            SeriesLabelTests();
            await ConsoleEndToEndTests();
            await SerialHardeningTests();
            await ReadLoopRaceTests();
            PortListParserTests();
            ResponseSummarizerTests();
            ByteOrderTests();
            DescribeSeriesTests();
            LivePipelineTests();
            SeriesColorTests();
            SeriesStatsTests();
            DownloadPlanTests();
            FieldRangeTests();
            DeviceInfoCoverageTests();
            await DeviceInfoPagingTests();
            await DeviceInfoIsolationTests();
            await DeviceInfoFormatTests();
            await DeviceInfoClearFlagTests();
            await DeviceInfoStaleFrameTests();
            await TransactLockTests();
            await DeviceInfoWithPollingTests();
            CommandKeyTests();
            CommandKeyMismatchTests();
            CommandKeyHintTests();
            await CommandExchangeTests();
            CommLogTests();

            Console.WriteLine();
            Console.WriteLine(_failed == 0
                ? "ALL PASS (" + _passed + " checks)"
                : _failed + " FAILED / " + _passed + " passed");
            return _failed == 0 ? 0 : 1;
        }

        // ---- 0x43 端口组状态：数组字段与故障/分级分流 ----
        private static void GroupStatusTests()
        {
            Console.WriteLine("0x43 PortGroupStatus");
            // port0: det=4 ValidPd / pwr=2 DeliveringPower, flt=0, cls=3
            // port1: 全 0
            // port2: det=6 OpenCircuit / pwr=1 Searching, flt=1, cls=0xE
            // port3: det=4 / pwr=4 Fault, flt=5 ThermalShutdown, cls=2
            PortGroupStatus g = Rtl8239ResponseParser.ParsePortGroupStatus(
                Frame("43 01 00 42 03 00 00 61 1E 44 52"));

            CheckEq(g.Group, (byte)0, "Group == 0");
            CheckEq(g.Ports.Length, 4, "解析出 4 个端口（此前整段数组不会显示）");
            CheckEq(g.Ports[0].AbsolutePort, (byte)0, "Ports[0].AbsolutePort == 0");
            CheckEq(g.Ports[1].AbsolutePort, (byte)1, "Ports[1].AbsolutePort == 1");
            CheckEq(g.Ports[0].DetectionResult, DetectionResult.ValidPd, "Ports[0] 检测 = ValidPd");
            CheckEq(g.Ports[0].PowerState, PortPowerState.DeliveringPower, "Ports[0] 电源 = 供电中");
            CheckEq(g.Ports[0].ClassificationResult, ClassificationResult.Class3, "Ports[0] 分级 = Class3");
            Check(g.Ports[0].FaultType == null, "非故障端口的 FaultType 为 null（此前会假报故障）");
            CheckEq(g.Ports[0].FaultTypeDescription, "无故障", "Ports[0] 故障描述 = 无故障");
            CheckEq(g.Ports[2].DetectionResult, DetectionResult.OpenCircuit, "Ports[2] 检测 = 开路");
            CheckEq(g.Ports[3].PowerState, PortPowerState.Fault, "Ports[3] 电源 = 故障");
            CheckEq(g.Ports[3].FaultType, FaultType.ThermalShutdown, "故障端口 Ports[3] 错误类型 = 过温关断");
            CheckEq(g.Ports[3].ClassificationResult, ClassificationResult.Class2, "Ports[3] 分级 = Class2");
            Console.WriteLine();
        }

        // ---- 0x42 端口状态：STS2 二义字段按 STS1 分流 ----
        private static void PortStatusTests()
        {
            Console.WriteLine("0x42 PortStatus");

            PortStatus ok = Rtl8239ResponseParser.ParsePortStatus(
                Frame("42 01 05 02 44 FF FF 00 FF FF FF"));
            CheckEq(ok.Port, (byte)5, "Port == 5");
            Check(!ok.IsFault, "供电中端口 IsFault == false");
            Check(ok.FaultType == null, "非故障端口 FaultType == null（此前 = 0x44 显示未知/假故障）");
            CheckEq(ok.FaultTypeDescription, "无故障", "FaultTypeDescription == 无故障");
            CheckEq(ok.DetectionResult, DetectionResult.ValidPd, "检测 = ValidPd");
            CheckEq(ok.ClassificationResult, ClassificationResult.Class4, "分级 = Class4");
            CheckEq(ok.ConnectionCheckResult, ConnectionCheckResult.TwoPair, "连接检查 = 2-pair");

            PortStatus bad = Rtl8239ResponseParser.ParsePortStatus(
                Frame("42 01 05 04 0E FF FF 00 FF FF FF"));
            Check(bad.IsFault, "故障端口 IsFault == true");
            CheckEq(bad.FaultType, FaultType.Gotp, "故障端口错误类型 = GOTP");
            Check(bad.DetectionResult == null, "故障端口 DetectionResult == null（此前会解出 0x0E 保留）");
            Check(bad.ClassificationResult == null, "故障端口 ClassificationResult == null（此前会解出 Class 0）");
            Check(bad.DetectionResultDescription.Contains("不适用"), "故障端口检测描述写明不适用");
            Console.WriteLine();
        }

        // ---- 分发与新增命令 ----
        private static void DispatcherTests()
        {
            Console.WriteLine("Parse() dispatcher");

            Check(Rtl8239ResponseParser.Parse(Frame("43 01 00 42 03 00 00 61 1E 44 52")) is PortGroupStatus,
                "0x43 分发到 PortGroupStatus");

            object addrObj = Rtl8239ResponseParser.Parse(Frame("4C 01 00 20 22 FF FF 20 24 FF FF"));
            Check(addrObj is GlobalDeviceAddress, "0x4C 分发到 GlobalDeviceAddress");
            var addr = (GlobalDeviceAddress)addrObj;
            CheckEq(addr.PresentCount, 4, "0x4C 在位数 == 4");
            Check(addr.AddressesHex.Contains("20"), "0x4C 地址十六进制串：" + addr.AddressesHex);

            object chipObj = Rtl8239ResponseParser.Parse(Frame("F1 01 20 00 00 00 10 12 34 56 78"));
            Check(chipObj is ChipRegisterValue, "0xF1 分发到 ChipRegisterValue");
            var chip = (ChipRegisterValue)chipObj;
            CheckEq(chip.ChipAddr, (byte)0x20, "0xF1 ChipAddr == 0x20");
            CheckEq(chip.RegisterAddress, 0x10u, "0xF1 寄存器地址（32 位小端）");
            CheckEq(chip.RegisterValue, 0x12345678u, "0xF1 寄存器值（32 位小端）");
            CheckEq(chip.RegisterValueHex, "0x12345678", "0xF1 寄存器值 hex");

            object ackObj = Rtl8239ResponseParser.Parse(new byte[] { 0xC0, 0x81, 0x40, 0x00 });
            Check(ackObj is DownloadAck, "0xC0-81 的 4 字节 Loader 应答可解析（此前报长度不足）");
            CheckEq(((DownloadAck)ackObj).ImageOffset, (ushort)0x4000, "Loader 应答镜像偏移（按全局字节序，Loader 路径未实测）");

            object caObj = Rtl8239ResponseParser.Parse(new byte[] { 0xCA, 0x07, 0x00, 0x20 });
            Check(caObj is DownloadAck, "0xCA 的 4 字节应答可解析");
            CheckEq(((DownloadAck)caObj).ImageOffset, (ushort)0x0020, "0xCA 镜像偏移（按全局字节序）");
            Console.WriteLine();
        }

        // ---- 0xC0 系与 0xF1 ----
        private static void ConfigurationTests()
        {
            Console.WriteLine("0xC0 sub commands");

            object verObj = Rtl8239ResponseParser.Parse(Frame("C0 04 19 08 19 01 07 FF FF FF FF"));
            Check(verObj is ConfigurationVersionInfo, "0xC0-04 分发到 ConfigurationVersionInfo");
            var ver = (ConfigurationVersionInfo)verObj;
            CheckEq(ver.Year, (byte)0x19, "0xC0-04 Year == 0x19");
            CheckEq(ver.HighVersion, (byte)1, "0xC0-04 高版本 == 1");
            CheckEq(ver.LowVersion, (byte)7, "0xC0-04 低版本 == 7");
            CheckEq(ver.VersionText, "1.7", "0xC0-04 版本串");

            object saveObj = Rtl8239ResponseParser.Parse(Frame("C0 01 00 00 FF FF FF FF FF FF FF"));
            Check(saveObj is ConfigurationSaveStatus, "0xC0-01 分发到 ConfigurationSaveStatus");
            var save = (ConfigurationSaveStatus)saveObj;
            Check(save.SaveSuccess && save.VersionSuccess, "0xC0-01 保存与版本均成功");

            object jmpObj = Rtl8239ResponseParser.Parse(Frame("C0 40 00 01 FF FF FF FF FF FF FF"));
            Check(jmpObj is JumpToAppStatus, "0xC0-40 分发到 JumpToAppStatus");
            var jmp = (JumpToAppStatus)jmpObj;
            Check(jmp.AppImageValid, "0xC0-40 App 镜像有效");
            Check(!jmp.FirmwareImageValid, "0xC0-40 Firmware 镜像无效");

            object stsObj = Rtl8239ResponseParser.Parse(Frame("C0 02 01 FF FF FF FF FF FF FF FF"));
            Check(stsObj is ConfigurationCommandStatus, "0xC0-02 分发到 ConfigurationCommandStatus");
            var sts = (ConfigurationCommandStatus)stsObj;
            Check(!sts.Success, "0xC0-02 STS=0x01 判为失败");
            CheckEq(sts.StatusDescription, "失败", "0xC0-02 状态描述");
            Console.WriteLine();
        }

        // ---- 帧身份校验 ----
        private static void ValidationTests()
        {
            Console.WriteLine("validation");

            string wrongId = ThrownBy(() => Rtl8239ResponseParser.ParsePortStatus(
                Frame("43 01 00 42 03 00 00 61 1E 44 52")));
            Check(wrongId.StartsWith("InvalidOperationException") && wrongId.Contains("命令 ID 不匹配"),
                "0x43 帧交给 0x42 解析器 -> 命令 ID 不匹配：" + wrongId);

            byte[] corrupt = Frame("42 01 05 02 44 FF FF 00 FF FF FF");
            corrupt[11] = (byte)(corrupt[11] ^ 0xFF);
            string badSum = ThrownBy(() => Rtl8239ResponseParser.Parse(corrupt));
            Check(badSum.Contains("校验和错误"), "校验和错误被拒绝：" + badSum);

            string unsupported = ThrownBy(() => Rtl8239ResponseParser.Parse(Frame("99 01 00 FF FF FF FF FF FF FF FF")));
            Check(unsupported.StartsWith("NotSupportedException"), "未知命令 -> NotSupportedException：" + unsupported);

            string shortFrame = ThrownBy(() => Rtl8239ResponseParser.Parse(new byte[] { 0x40, 0x01 }));
            Check(shortFrame.Contains("长度"), "截断的 App 帧被拒绝：" + shortFrame);
            Console.WriteLine();
        }

        // ---- 渲染：数组字段必须展开 ----
        private static void RenderingTests()
        {
            Console.WriteLine("FormatValue rendering");

            string group = Formatter.Format(
                Rtl8239ResponseParser.ParsePortGroupStatus(Frame("43 01 00 42 03 00 00 61 1E 44 52")));
            Check(!group.Contains("PortGroupPortStatus[]"), "0x43 结果不再打印类型名");
            Check(group.Contains("AbsolutePort"), "0x43 结果展开到端口字段");
            Check(group.Contains("过温关断"), "0x43 结果包含故障描述");

            string addr = Formatter.Format(
                Rtl8239ResponseParser.ParseGlobalDeviceAddress(Frame("4C 01 00 20 22 FF FF 20 24 FF FF")));
            Check(!addr.Contains("System.Byte[]"), "0x4C 结果不再打印 System.Byte[]");
            Check(addr.Contains("PresentCount"), "0x4C 结果包含在位数");

            string port = Formatter.Format(
                Rtl8239ResponseParser.ParsePortStatus(Frame("42 01 05 04 0E FF FF 00 FF FF FF")));
            Check(port.Contains("FaultType: Gotp"), "0x42 故障端口 FaultType 可读");
            Check(port.Contains("DetectionResult: null"), "0x42 故障端口不适用的检测字段显示 null");
            Console.WriteLine();
        }

        // ---- 期望序列号 / 期望端口核对 ----
        private static void ExpectationTests()
        {
            Console.WriteLine("CheckFrameExpectations");

            byte[] statusFrame = Frame("42 01 05 02 44 FF FF 00 FF FF FF");   // seq=01, port=05
            var checker = new ExpectationChecker();

            CheckEq(checker.Check(statusFrame, "", ""), "", "未填期望值时不提示");
            CheckEq(checker.Check(statusFrame, "01", "05"), "", "序列号与端口都相符时不提示");

            string seqBad = checker.Check(statusFrame, "02", "");
            Check(seqBad.Contains("序列号不符"), "序列号不符时提示：" + seqBad.Trim());

            string portBad = checker.Check(statusFrame, "", "07");
            Check(portBad.Contains("端口回显不符"), "端口回显不符时提示：" + portBad.Trim());

            string notPortCmd = checker.Check(Frame("40 01 00 08 01 39 00 12 00 00 10"), "", "05");
            Check(notPortCmd.Contains("不含端口回显"), "非端口命令跳过端口核对");

            string bankIdCmd = checker.Check(Frame("4B 00 02 2C 01 2C 01 2C 01 2C 01"), "01", "");
            Check(bankIdCmd.Contains("跳过序列号核对"), "0x4B 跳过序列号核对");

            string badValue = checker.Check(statusFrame, "zz", "");
            Check(badValue.Contains("期望值无效"), "非法期望值给出提示：" + badValue.Trim());
            Console.WriteLine();
        }

        // MainWindow.xaml.cs 依赖 WPF，这里把 TextBox 换成 Stub，其余代码逐字一致。
        internal sealed class TextBoxStub { public string Text = ""; }

        internal sealed class ExpectationChecker
        {
            private readonly TextBoxStub ExpectSeqText = new TextBoxStub();
            private readonly TextBoxStub ExpectPortText = new TextBoxStub();

            internal string Check(byte[] frame, string seqText, string portText)
            {
                ExpectSeqText.Text = seqText;
                ExpectPortText.Text = portText;
                return CheckFrameExpectations(frame);
            }

            // ---- 以下与 MainWindow.xaml.cs 逐字一致 ----
            private static readonly byte[] PortEchoCommands = { 0x42, 0x44, 0x45, 0x48, 0x49, 0x4E, 0x4F };

            private string CheckFrameExpectations(byte[] frame)
            {
                var notes = new List<string>();
                bool isAppFrame = frame.Length >= Rtl8239CommandBuilder.AppFrameLength;
                try
                {
                    string seqText = (ExpectSeqText.Text ?? string.Empty).Trim();
                    if (seqText.Length > 0)
                    {
                        if (!isAppFrame)
                            notes.Add("响应帧不足 12 字节，无法核对序列号");
                        else if (frame[0] == 0x4B)
                            notes.Add("0x4B 响应 Byte1 为 Bank ID，跳过序列号核对");
                        else
                        {
                            byte expected = ParseExpectationByte(seqText, "期望序列号");
                            if (!Rtl8239CommandBuilder.IsResponseValid(frame, expected))
                                notes.Add($"序列号不符：期望 0x{expected:X2}，实际 0x{frame[1]:X2}");
                        }
                    }

                    string portText = (ExpectPortText.Text ?? string.Empty).Trim();
                    if (portText.Length > 0)
                    {
                        if (!PortEchoCommands.Contains(frame[0]))
                            notes.Add($"命令 0x{frame[0]:X2} 的响应不含端口回显，跳过端口核对");
                        else if (!isAppFrame)
                            notes.Add("响应帧不足 12 字节，无法核对端口");
                        else
                        {
                            byte expected = ParseExpectationByte(portText, "期望端口");
                            if (frame[2] != expected)
                                notes.Add($"端口回显不符：期望 0x{expected:X2}，实际 0x{frame[2]:X2}");
                        }
                    }
                }
                catch (FormatException ex) { notes.Add("期望值无效：" + ex.Message); }
                catch (OverflowException) { notes.Add("期望值超出可解析范围。"); }

                return notes.Count == 0
                    ? string.Empty
                    : "⚠ " + string.Join("；", notes) + Environment.NewLine + Environment.NewLine;
            }

            private static byte ParseExpectationByte(string text, string label)
            {
                // 与 MainWindow.xaml.cs 一致：现在两处都委托给 Rtl8239Catalog.ParseByte
                return Rtl8239Catalog.ParseByte(text, label);
            }
        }

        // =================================================================
        //  以下 FormatValue / IsSimpleValue 与 MainWindow.xaml.cs 中的实现
        //  逐字一致（该文件依赖 WPF，无法在本验证工程中编译）。
        // =================================================================
        internal static class Formatter
        {
            internal static string Format(object obj)
            {
                var sb = new StringBuilder();
                FormatValue(sb, obj, 0);
                return sb.ToString();
            }

            private static void FormatValue(StringBuilder sb, object obj, int indent)
            {
                string pad = new string(' ', indent);
                if (obj == null) { sb.AppendLine(pad + "null"); return; }

                Type t = obj.GetType();

                if (obj is Array arr)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        object item = arr.GetValue(i);
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
                            // 嵌套的数组 / 结构体：另起一行递归展开
                            sb.AppendLine(pad + "[" + i + "]");
                            FormatValue(sb, item, indent + 4);
                        }
                    }
                    return;
                }

                if (t.IsPrimitive || obj is string || obj is bool)
                {
                    sb.AppendLine(pad + obj.ToString());
                    return;
                }

                if (t.IsEnum)
                {
                    sb.AppendLine(pad + t.Name + "." + obj);
                    return;
                }

                // 结构体：按公共字段展开
                sb.AppendLine(pad + t.Name + ":");
                foreach (FieldInfo f in t.GetFields())
                {
                    object v = f.GetValue(obj);
                    sb.Append(pad + "  " + f.Name + ": ");
                    if (v == null)
                    {
                        sb.AppendLine("null");
                        continue;
                    }
                    if (IsSimpleValue(v))
                    {
                        sb.AppendLine(v.ToString());
                    }
                    else
                    {
                        // 数组 / 嵌套结构体：另起一行递归展开，否则这里只能打印类型名
                        sb.AppendLine();
                        FormatValue(sb, v, indent + 4);
                    }
                }
            }

            // 能在一行内打印完的值；其余（数组、嵌套结构体）需要递归展开。
            private static bool IsSimpleValue(object v)
            {
                Type t = v.GetType();
                return t.IsPrimitive || t.IsEnum || v is string || v is decimal;
            }
        }
    }
}
