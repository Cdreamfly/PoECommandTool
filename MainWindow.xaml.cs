using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;

namespace WpfApp1
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private CommandDef _current;

        // 非「端口」字段的输入框（按字段出现顺序存放）。
        private readonly List<TextBox> _fixedBoxes = new List<TextBox>();

        // 端口字段相关：单端口 / 端口列表两种模式。
        private bool _listMode;
        private TextBox _portSingleBox;
        private TextBox _portStartBox;
        private TextBox _portEndBox;
        private StackPanel _singleRow;
        private StackPanel _listRow;

        // 固件下载：选中的文件路径。
        private string _downloadFilePath;

        private static readonly FontFamily Mono = new FontFamily("Consolas");
        private static readonly SolidColorBrush Gray = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        public MainWindow()
        {
            InitializeComponent();

            CommandList.ItemsSource = Rtl8239Catalog.All;
            ICollectionView view = CollectionViewSource.GetDefaultView(CommandList.ItemsSource);
            view.GroupDescriptions.Add(new PropertyGroupDescription("Category"));

            Title = AppVersion.Title;      // 标题栏带上版本号，方便区分手上是哪个版本
            InitializeSerialTab();         // 串口页的接线，见 MainWindow.SerialTab.cs
        }

        // =================================================================
        //  命令组装
        // =================================================================

        private void CommandList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _current = CommandList.SelectedItem as CommandDef;
            if (_current == null) return;

            CmdTitle.Text = $"{_current.Name}   ({_current.Key})";
            DescText.Text = _current.Description;
            ResultBox.Text = string.Empty;
            BuildParams(_current);
        }

        private void BuildParams(CommandDef cmd)
        {
            ParamPanel.Children.Clear();
            _fixedBoxes.Clear();
            _portSingleBox = _portStartBox = _portEndBox = null;
            _singleRow = _listRow = null;
            _listMode = false;

            foreach (FieldDef field in cmd.Fields)
            {
                if (field.Kind == FieldKind.Port)
                {
                    BuildPortField(field.Name);
                }
                else
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                    row.Children.Add(new TextBlock
                    {
                        Text = field.Name,
                        Width = 260,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    });
                    var box = new TextBox
                    {
                        Text = field.DefaultValue.ToString(),
                        Width = 160,
                        FontFamily = Mono,
                    };
                    row.Children.Add(box);
                    ParamPanel.Children.Add(row);
                    _fixedBoxes.Add(box);
                }
            }

            if (cmd.Fields.Length == 0)
            {
                ParamPanel.Children.Add(new TextBlock
                {
                    Text = "（该命令无参数，为固定帧）",
                    Foreground = Gray,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
        }

        private void BuildPortField(string label)
        {
            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
            modeRow.Children.Add(new TextBlock { Text = "端口模式：", VerticalAlignment = VerticalAlignment.Center });
            var rSingle = new RadioButton
            {
                Content = "单端口",
                IsChecked = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            var rList = new RadioButton
            {
                Content = "端口列表",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            modeRow.Children.Add(rSingle);
            modeRow.Children.Add(rList);
            ParamPanel.Children.Add(modeRow);

            _singleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            _singleRow.Children.Add(new TextBlock
            {
                Text = label,
                Width = 260,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            _portSingleBox = new TextBox { Text = "00", Width = 80, FontFamily = Mono };
            _singleRow.Children.Add(_portSingleBox);
            ParamPanel.Children.Add(_singleRow);

            _listRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
            _listRow.Children.Add(new TextBlock { Text = "起始端口", Width = 76, VerticalAlignment = VerticalAlignment.Center });
            _portStartBox = new TextBox { Text = "00", Width = 64, FontFamily = Mono };
            _listRow.Children.Add(_portStartBox);
            _listRow.Children.Add(new TextBlock
            {
                Text = "结束端口",
                Width = 76,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            });
            _portEndBox = new TextBox { Text = "00", Width = 64, FontFamily = Mono };
            _listRow.Children.Add(_portEndBox);
            ParamPanel.Children.Add(_listRow);

            rSingle.Checked += (s, e2) => { _listMode = false; _singleRow.Visibility = Visibility.Visible; _listRow.Visibility = Visibility.Collapsed; };
            rList.Checked += (s, e2) => { _listMode = true; _singleRow.Visibility = Visibility.Collapsed; _listRow.Visibility = Visibility.Visible; };
        }

        /// <summary>一条组装好的命令：端口、实际用的序列号、帧，以及「生成命令」页面上那一行的原文。</summary>
        private sealed class PlannedFrame
        {
            public bool HasPort;
            public long Port;
            public byte Sequence;
            public byte[] Frame;
            public string Line;
        }

        /// <summary>
        /// 把当前界面上的参数组装成帧。越界与非法输入在这里抛，由调用方决定怎么呈现。
        ///
        /// 「生成命令」与「发送并解析」都走这一个方法，两者的帧因此**必然逐字节一致**——
        /// 这也是「原有功能一行不变」的根据。
        ///
        /// <paramref name="stepSequence"/>：多帧时序列号是否逐帧递增。
        /// 「生成命令」传 false（所有帧同一个序列号，与改动前完全一致）；
        /// 发送时必须传 true——回包只按「命令号 + Byte1」配对，N 帧同号的话，
        /// 第 3 帧迟到的回包会被第 5 帧的等待认领，把别人的数据显示成自己的。
        /// </summary>
        private List<PlannedFrame> BuildFramePlan(byte firstSequence, bool stepSequence)
        {
            var ports = new List<long>();
            bool hasPort = _current.Fields.Any(f => f.Kind == FieldKind.Port);
            if (hasPort)
            {
                const long MaxPort = 0x2F; // 端口 0-47
                if (_listMode)
                {
                    long start = Rtl8239Catalog.ParseNumber(_portStartBox.Text);
                    long end = Rtl8239Catalog.ParseNumber(_portEndBox.Text);
                    if (start < 0 || start > MaxPort || end < 0 || end > MaxPort)
                        throw new InvalidOperationException("端口必须在 0x00-0x2F（0-47）范围内。");
                    if (end < start) throw new InvalidOperationException("结束端口不能小于起始端口。");
                    for (long p = start; p <= end; p++) ports.Add(p);
                }
                else
                {
                    long port = Rtl8239Catalog.ParseNumber(_portSingleBox.Text);
                    if (port < 0 || port > MaxPort)
                        throw new InvalidOperationException("端口必须在 0x00-0x2F（0-47）范围内。");
                    ports.Add(port);
                }
            }
            else
            {
                ports.Add(0);   // 无端口字段的命令只生成一条
            }

            var plan = new List<PlannedFrame>();
            byte sequence = firstSequence;
            for (int index = 0; index < ports.Count; index++)
            {
                long port = ports[index];
                if (index > 0 && stepSequence)
                    sequence = Serial.PollingScheduler.NextSequence(sequence);

                var values = new long[_current.Fields.Length];
                int fixedIdx = 0;
                for (int f = 0; f < _current.Fields.Length; f++)
                {
                    FieldDef fd = _current.Fields[f];
                    values[f] = fd.Kind == FieldKind.Port
                        ? port
                        : Rtl8239Catalog.ParseNumber(_fixedBoxes[fixedIdx++].Text);
                }

                // BuildChecked 在组装前按 FieldKind 校验每个值：越界必须在这里被拦下，
                // 因为 Build lambda 里的 (byte)/(int) 转换会静默截断
                //（300 → 0x2C，70000 → 446.4W），那是会写进真实硬件配置的错误值。
                byte[] frame = Rtl8239Catalog.BuildChecked(_current, sequence, values);

                var planned = new PlannedFrame();
                planned.HasPort = hasPort;
                planned.Port = port;
                planned.Sequence = sequence;
                planned.Frame = frame;
                planned.Line = hasPort
                    ? string.Format("端口 0x{0:X2}:  {1}  ({2} 字节)",
                        port, Rtl8239CommandBuilder.ToHex(frame), frame.Length)
                    : string.Format("{0}  ({1} 字节)",
                        Rtl8239CommandBuilder.ToHex(frame), frame.Length);
                plan.Add(planned);
            }

            return plan;
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null)
            {
                ResultBox.Text = "请先在左侧选择一个命令。";
                return;
            }

            try
            {
                // 序列号会被塞进帧的 Byte1，越界必须在这里拦下（300 → 0x2C 会让回包永远对不上）
                byte seq = Rtl8239Catalog.ParseByte(SeqText.Text, "序列号");
                List<PlannedFrame> plan = BuildFramePlan(seq, false);

                var sb = new StringBuilder();
                for (int i = 0; i < plan.Count; i++)
                    sb.AppendLine(plan[i].Line);

                sb.AppendLine($"—— 共生成 {plan.Count} 条命令 ——");
                ResultBox.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                ResultBox.Text = "参数解析失败：" + ex.Message;
            }
        }

        // =================================================================
        //  响应解析
        // =================================================================

        private void ParseResponse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                byte[] frame = ParseHexBytes(RespInput.Text);
                if (frame.Length < Rtl8239ResponseParser.LoaderAckLength)
                    throw new Exception("响应帧至少 4 字节。");

                object result = Rtl8239ResponseParser.Parse(frame, SelectedByteOrder(RespByteOrderCombo));
                RespOutput.Text = CheckFrameExpectations(frame) + FormatObject(result);
            }
            catch (Exception ex)
            {
                RespOutput.Text = "解析失败：" + ex.Message;
            }
        }

        // 响应 Byte2 回显请求端口的命令。
        private static readonly byte[] PortEchoCommands = { 0x42, 0x44, 0x45, 0x48, 0x49, 0x4E, 0x4F };

        // 可选的一致性核对：界面上填了「期望序列号 / 期望端口」时才检查响应帧里回显的值，
        // 只提示不阻断。只在解析成功后调用，因此不会把校验和错误误报成序列号不符。
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
            // 与序列号走同一套边界判断，避免这里成为第二份会各自漂移的副本
            return Rtl8239Catalog.ParseByte(text, label);
        }

        private static byte[] ParseHexBytes(string s)
        {
            var tokens = (s ?? string.Empty).Split(
                new[] { ' ', ',', '\t', '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i].Trim();
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
                bytes[i] = Convert.ToByte(t, 16);
            }
            return bytes;
        }

        // 用反射把解析结果结构体渲染成可读文本（便于新命令无需手写格式化）。
        private static string FormatObject(object obj)
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

        // =================================================================
        //  固件下载
        // =================================================================

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "固件文件 (*.bin;*.hex;*.img;*.fw;*.*)|*.bin;*.hex;*.img;*.fw;*.*|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog() == true)
            {
                _downloadFilePath = dlg.FileName;
                FilePathBox.Text = dlg.FileName;
            }
        }

        // 生成下载帧序列。帧生成的规划逻辑（分帧/对齐/块号/偏移）全部在
        // Rtl8239DownloadPlan 里（纯 C#，可在无 WPF 环境下断言），这里只负责读文件与渲染文本。
        private void GenDownload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_downloadFilePath))
                    throw new InvalidOperationException("请先选择固件文件。");

                byte seq = Rtl8239Catalog.ParseByte(DlSeqText.Text, "下载序列号");
                DownloadMode mode = DownloadTypeApp.IsChecked == true ? DownloadMode.App : DownloadMode.Firmware;

                // 先按协议上限检查文件尺寸，再读入内存：ReadAllBytes 对任意大小的文件都没有上限，
                // 而输出是「每 32 字节一行十六进制」，选错一个大文件会直接吃满内存并卡死界面。
                var info = new FileInfo(_downloadFilePath);
                if (!info.Exists)
                    throw new FileNotFoundException("固件文件不存在。", _downloadFilePath);

                long limit = mode == DownloadMode.App
                    ? Rtl8239DownloadPlan.AppImageLimit
                    : Rtl8239DownloadPlan.FirmwareImageLimit;
                if (info.Length > limit)
                    throw new InvalidOperationException(
                        $"固件 {info.Length} 字节超出 {mode} 模式上限 {limit} 字节" +
                        (mode == DownloadMode.Firmware ? "（镜像偏移字段只有 16 位）" : "（4 个 64K 块）") + "。");

                byte[] data = File.ReadAllBytes(_downloadFilePath);
                if (data.Length == 0) throw new InvalidOperationException("固件文件为空。");

                IList<DownloadFrame> frames = Rtl8239DownloadPlan.Build(data, seq, mode);

                var sb = new StringBuilder();
                foreach (DownloadFrame f in frames)
                {
                    // 打印的是帧里实际编码的偏移（f.Offset 与 f.ImageOffset 在修复后必然相等；
                    // 分开取用是为了让任何回绕都能在输出里直接看出来）。
                    if (f.Sub >= 0)
                        sb.AppendLine($"偏移 0x{f.Offset:X4}  SUB 0x{f.Sub:X2}:  " + Rtl8239CommandBuilder.ToHex(f.Bytes));
                    else
                        sb.AppendLine($"偏移 0x{f.Offset:X4}:  " + Rtl8239CommandBuilder.ToHex(f.Bytes));
                }

                sb.AppendLine($"—— 共生成 {frames.Count} 条下载帧，固件 {data.Length} 字节 ——");
                DlOutput.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                DlOutput.Text = "生成失败：" + ex.Message;
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AboutWindow { Owner = this };
            dialog.ShowDialog();
        }
    }
}
