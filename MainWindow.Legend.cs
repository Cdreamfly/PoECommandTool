using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PoECommandTool.Chart;
using PoECommandTool.Serial;

namespace PoECommandTool
{
    /// <summary>
    /// 「串口读写」页的图例与曲线管理。
    ///
    /// 从 MainWindow.SerialChart.cs 里拆出来的（原文件超了 800 行的软上限）：
    /// 这个文件管「有哪些曲线、图例怎么显示、勾选怎么同步」，
    /// 采样与绘图留在 SerialChart，日志留在 SerialLog。
    /// </summary>
    public partial class MainWindow
    {
        // =================================================================
        //  图例
        // =================================================================

        /// <summary>
        /// 重建图例。按「参数」分组（功率 / 电流 / 电压 / 温度 …）：多端口轮询时同一个参数会有好几条
        /// （每端口一条），分到一组里就能一键全显/全隐，也方便折叠起来。
        /// </summary>
        private void BuildLegend()
        {
            LegendPanel.Children.Clear();
            _legendLabels.Clear();
            _legendBoxes.Clear();
            _legendGroupBoxes.Clear();

            if (_legend.Count == 0)
            {
                LegendPanel.Children.Add(new TextBlock
                {
                    Text = "（还没有数据）",
                    Foreground = Gray,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            var groupOrder = new List<string>();
            var groups = new Dictionary<string, List<SeriesBuffer>>();
            for (int i = 0; i < _legend.Count; i++)
            {
                SeriesBuffer buffer = _legend[i];
                string label = ParameterLabel(buffer);
                List<SeriesBuffer> bucket;
                if (!groups.TryGetValue(label, out bucket))
                {
                    bucket = new List<SeriesBuffer>();
                    groups[label] = bucket;
                    groupOrder.Add(label);
                }
                bucket.Add(buffer);
            }

            for (int g = 0; g < groupOrder.Count; g++)
            {
                string label = groupOrder[g];
                List<SeriesBuffer> buffers = groups[label];

                var children = new StackPanel
                {
                    Margin = new Thickness(20, 1, 0, 2),
                    Visibility = _collapsedGroups.Contains(label) ? Visibility.Collapsed : Visibility.Visible,
                    Tag = label,        // 折叠按钮据此记住/恢复分组状态
                };

                var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

                var collapseButton = new Button
                {
                    Content = _collapsedGroups.Contains(label) ? "▸" : "▾",
                    Width = 18,
                    Height = 18,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = children,
                    ToolTip = "折叠 / 展开这一组",
                };
                collapseButton.Click += LegendCollapse_Click;
                header.Children.Add(collapseButton);

                var groupBox = new CheckBox
                {
                    IsThreeState = true,          // 部分选中时显示半选
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 4, 0),
                    Tag = buffers,
                    ToolTip = "全显 / 全隐这一组（点一下切换）",
                };
                // 用 Click 而不是 Checked/Unchecked：程序改状态时不会误触发，也不会被三态循环绊住
                groupBox.Click += LegendGroup_Click;
                header.Children.Add(groupBox);
                _legendGroupBoxes.Add(groupBox);

                header.Children.Add(new TextBlock
                {
                    Text = string.Format("{0} · {1} 条", label, buffers.Count),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 180,
                });
                LegendPanel.Children.Add(header);

                for (int i = 0; i < buffers.Count; i++)
                    children.Children.Add(BuildLegendRow(buffers[i]));

                LegendPanel.Children.Add(children);
            }

            UpdateLegendGroups();
        }

        private StackPanel BuildLegendRow(SeriesBuffer buffer)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

            var checkBox = new CheckBox
            {
                IsChecked = buffer.Visible,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = buffer,
            };
            checkBox.Checked += LegendCheckBox_Toggled;
            checkBox.Unchecked += LegendCheckBox_Toggled;
            row.Children.Add(checkBox);
            _legendBoxes[buffer] = checkBox;

            row.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(4, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = BrushFrom(buffer.ColorHex),
                BorderBrush = Gray,
                BorderThickness = new Thickness(1),
            });

            var label = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 156,
                Tag = buffer,
                Cursor = Cursors.Hand,
                ToolTip = "单击选中（图上加粗、再点一次取消）；双击查看详细统计（最大/最小/平均/峰峰值等）",
            };
            label.MouseLeftButtonUp += LegendRow_Click;
            row.Children.Add(label);
            _legendLabels[buffer] = label;
            return row;
        }

        private void LegendCollapse_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
                return;

            var children = button.Tag as StackPanel;
            if (children == null)
                return;

            bool expanding = children.Visibility != Visibility.Visible;
            children.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
            button.Content = expanding ? "▾" : "▸";

            // 记住折叠状态：新曲线出现时会重建图例，别把用户折叠过的又展开
            string label = children.Tag as string;
            if (label == null)
                return;
            if (expanding)
                _collapsedGroups.Remove(label);
            else
                _collapsedGroups.Add(label);
        }

        /// <summary>按组内可见情况刷新组标题的三态复选框。</summary>
        private void UpdateLegendGroups()
        {
            _updatingLegend = true;
            try
            {
                for (int i = 0; i < _legendGroupBoxes.Count; i++)
                {
                    CheckBox box = _legendGroupBoxes[i];
                    var buffers = box.Tag as List<SeriesBuffer>;
                    if (buffers == null || buffers.Count == 0)
                        continue;

                    int visible = 0;
                    for (int k = 0; k < buffers.Count; k++)
                        if (buffers[k].Visible)
                            visible++;

                    box.IsChecked = visible == 0
                        ? (bool?)false
                        : (visible == buffers.Count ? (bool?)true : null);
                }
            }
            finally
            {
                _updatingLegend = false;
            }
        }

        /// <summary>点组标题：组内有隐藏的就全显，全是显示的就全隐。</summary>
        private void LegendGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingLegend)
                return;

            var box = sender as CheckBox;
            var buffers = box == null ? null : box.Tag as List<SeriesBuffer>;
            if (buffers == null || buffers.Count == 0)
                return;

            bool anyHidden = false;
            for (int i = 0; i < buffers.Count; i++)
                if (!buffers[i].Visible)
                    anyHidden = true;

            bool target = anyHidden;
            _updatingLegend = true;
            try
            {
                for (int i = 0; i < buffers.Count; i++)
                {
                    buffers[i].Visible = target;
                    CheckBox childBox;
                    if (_legendBoxes.TryGetValue(buffers[i], out childBox))
                        childBox.IsChecked = target;
                }
            }
            finally
            {
                _updatingLegend = false;
            }

            UpdateLegendGroups();
            Plot.InvalidateLayout();
        }

        /// <summary>新建一条曲线缓冲，并分配颜色。</summary>
        private SeriesBuffer CreateSeries(SeriesCandidate candidate)
        {
            var buffer = new SeriesBuffer(candidate.Key, candidate.Name, candidate.Unit)
            {
                ColorHex = NextColorFor(candidate.Unit),
            };
            _seriesByKey[candidate.Key] = buffer;
            _legend.Add(buffer);
            return buffer;
        }

        /// <summary>
        /// 取「同一个子图里的下一种颜色」。按单位而不是按全局序号分配：
        /// 不同子图里的同色不会混淆（它们本来就分格里画），同一个子图里的曲线则保证各不相同。
        /// </summary>
        private string NextColorFor(string unit)
        {
            int sameUnit = 0;
            for (int i = 0; i < _legend.Count; i++)
                if (_legend[i].Unit == unit)
                    sameUnit++;
            return SeriesColor.For(sameUnit);
        }

        /// <summary>按「本次要轮询的命令 × 端口」预建曲线条目：这样一开始轮询，图例里就有对应条目
        /// （值先显示 --），而不是等第一条数据到了才一条条冒出来。
        /// </summary>
        private void EnsureSeriesFor(IList<SeriesCandidate> wanted)
        {
            if (wanted == null || wanted.Count == 0)
                return;

            bool added = false;
            for (int i = 0; i < wanted.Count; i++)
            {
                SeriesCandidate candidate = wanted[i];
                if (_seriesByKey.ContainsKey(candidate.Key))
                    continue;

                CreateSeries(candidate);
                added = true;
            }

            if (added)
            {
                BuildLegend();
                UpdateLegendValues();
            }
        }

        /// <summary>
        /// 把图例同步成「本次勾选」：预建缺失的条目；本次用不到的曲线取消勾选（数据保留，想看再勾回来）。
        /// 本次仍然要画的曲线保留用户自己的勾选状态，不会被强行打开。
        /// </summary>
        private void SyncSeriesWithSelection(IList<SeriesCandidate> wanted)
        {
            EnsureSeriesFor(wanted);

            var keys = new HashSet<string>();
            for (int i = 0; i < wanted.Count; i++)
                keys.Add(wanted[i].Key);

            bool changed = false;
            for (int i = 0; i < _legend.Count; i++)
            {
                SeriesBuffer buffer = _legend[i];
                if (keys.Contains(buffer.Key) || !buffer.Visible)
                    continue;

                buffer.Visible = false;
                changed = true;
            }

            if (changed)
                BuildLegend();
            Plot.InvalidateLayout();
        }

        private void LegendCheckBox_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingLegend)
                return;

            var checkBox = sender as CheckBox;
            if (checkBox == null)
                return;

            var buffer = checkBox.Tag as SeriesBuffer;
            if (buffer == null)
                return;

            buffer.Visible = checkBox.IsChecked == true;
            UpdateLegendGroups();
            Plot.InvalidateLayout();
        }

        /// <summary>单击图例项：选中/取消选中该曲线；双击：弹出详细统计。</summary>
        private void LegendRow_Click(object sender, MouseButtonEventArgs e)
        {
            var label = sender as TextBlock;
            var buffer = label == null ? null : label.Tag as SeriesBuffer;
            if (buffer == null)
                return;

            if (e.ClickCount >= 2)
            {
                ShowSeriesDetail(buffer);
                return;
            }

            Plot.SelectCurve(Plot.SelectedKey == buffer.Key ? null : buffer.Key);
            e.Handled = true;
        }

        /// <summary>双击图上的曲线：选中它并弹出详情。</summary>
        private void Plot_CurveActivated(string key)
        {
            SeriesBuffer buffer;
            if (key == null || !_seriesByKey.TryGetValue(key, out buffer))
                return;

            Plot.SelectCurve(key);
            ShowSeriesDetail(buffer);
        }

        /// <summary>选中变化：图例字重同步 + 刷新统计块（曲线本身由绘图元素加粗）。</summary>
        private void Plot_SelectionChanged(string key)
        {
            UpdateSelectionStats();
        }

        /// <summary>弹出某条曲线在「当前时间窗」内的详细统计。</summary>
        private void ShowSeriesDetail(SeriesBuffer buffer)
        {
            DateTime from;
            DateTime to;
            Plot.ResolveVisibleWindow(out from, out to);

            SeriesStatistics stats;
            string text = SeriesStats.TryCompute(buffer, from, to, out stats)
                ? SeriesStats.FormatDetail(buffer.Name, stats, buffer.Unit)
                : "当前时间窗内没有这条曲线的数据。"
                  + Environment.NewLine
                  + "当前时间窗：" + from.ToString("HH:mm:ss") + " ~ " + to.ToString("HH:mm:ss")
                  + "（数据可能已滑出窗口，或是刚预建、还没采到点）";

            var dialog = new SeriesDetailWindow { Owner = this };
            dialog.SetContent("曲线详情 - " + buffer.Name, text);
            dialog.ShowDialog();
        }

        /// <summary>刷新「选中曲线」的统计块；没选中时整块隐藏，不占地方。</summary>
        private void UpdateSelectionStats()
        {
            if (Plot == null || SelectionStatsPanel == null || SelectionStatsText == null)
                return;

            string key = Plot.SelectedKey;

            // 选中的那条在图例里也加粗，和图上对应起来
            for (int i = 0; i < _legend.Count; i++)
            {
                TextBlock rowLabel;
                if (!_legendLabels.TryGetValue(_legend[i], out rowLabel))
                    continue;

                rowLabel.FontWeight = key != null && _legend[i].Key == key
                    ? FontWeights.Bold
                    : FontWeights.Normal;
            }

            SeriesBuffer selected;
            if (key == null || !_seriesByKey.TryGetValue(key, out selected))
            {
                SelectionStatsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            DateTime from;
            DateTime to;
            Plot.ResolveVisibleWindow(out from, out to);

            SeriesStatistics stats;
            SelectionStatsPanel.Visibility = Visibility.Visible;
            SelectionStatsText.Text = SeriesStats.TryCompute(selected, from, to, out stats)
                ? selected.Name + Environment.NewLine + SeriesStats.FormatSummary(stats, selected.Unit)
                : selected.Name + Environment.NewLine + "（当前时间窗内没有数据）";
        }

        /// <summary>曲线名 "0x44 端口0 功率" → 分组用的 "功率"。</summary>
        /// <summary>
        /// 阈值线输入框变了就重画。写错时把原因显示在框旁边，**不静默忽略**——
        /// 静默的话用户会以为阈值功能坏了。
        /// </summary>
        private void ThresholdBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Plot == null || ThresholdBox == null)
                return;

            try
            {
                Plot.Thresholds = ThresholdSet.Parse(ThresholdBox.Text);
                Plot.InvalidateThresholds();

                if (ThresholdHint != null)
                {
                    ThresholdHint.Foreground = Gray;
                    ThresholdHint.Text = Plot.Thresholds.Count == 0
                        ? string.Empty
                        : "（已画 " + ThresholdSet.Describe(Plot.Thresholds) + "）";
                }
            }
            catch (Exception ex)
            {
                Plot.Thresholds = null;
                Plot.InvalidateThresholds();

                if (ThresholdHint != null)
                {
                    ThresholdHint.Foreground = Brushes.Firebrick;
                    ThresholdHint.Text = ex.Message;
                }
            }
        }

        private static string ParameterLabel(SeriesBuffer buffer)
        {
            return SeriesLabel.Parameter(buffer.Key, buffer.Name, buffer.Unit);
        }

        /// <summary>曲线 key "0x44 端口0.PowerW" → 来源 "0x44 端口0"。</summary>
        private static string SourceLabel(SeriesBuffer buffer)
        {
            return SeriesLabel.Source(buffer.Key);
        }
    }
}
