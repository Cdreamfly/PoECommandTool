using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfApp1.Chart
{
    /// <summary>
    /// 把 <see cref="ChartMath"/> 算好的布局画到屏幕上。
    ///
    /// 这里只做「画」——量程、刻度、像素坐标、降采样全在 ChartMath 里算好了（那边是纯函数、可测）。
    /// 所以这个文件虽然没法在 Linux 上编译验证，但里面没有任何需要推敲的逻辑。
    /// </summary>
    public sealed class ChartPlotElement : FrameworkElement
    {
        private const double LabelFontSize = 11;
        private const double LeftMargin = 62;
        private const double RightMargin = 10;
        private const double TopMargin = 8;
        private const double BottomMargin = 20;
        private const int MaxPointsPerLine = 400;

        private static readonly Pen AxisPen = CreatePen(Color.FromRgb(0x99, 0x99, 0x99), 1);
        private static readonly Pen GridPen = CreatePen(Color.FromRgb(0xEE, 0xEE, 0xEE), 1);
        private static readonly Pen CrosshairPen = CreateDashedPen(Color.FromRgb(0x88, 0x88, 0x88), 1);
        private static readonly Brush LabelBrush = CreateBrush(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly Brush UnitBrush = CreateBrush(Color.FromRgb(0x1A, 0x73, 0xE8));

        /// <summary>阈值线用的笔：红色虚线，和网格线、曲线都区分得开。</summary>
        private static readonly Pen ThresholdPen = CreateThresholdPen();

        private static Pen CreateThresholdPen()
        {
            var pen = new Pen(CreateBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)), 1.4);
            pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            return pen;
        }

        /// <summary>
        /// 阈值参考线：按单位给一条水平线（"W" → 30 表示功率带里画在 30W）。
        /// 数值用**显示单位**，也就是用户在图例上看到的那个单位。
        /// </summary>
        public Dictionary<string, double> Thresholds { get; set; }
        private static readonly Brush RangeBrush = CreateBrush(Color.FromRgb(0x66, 0x66, 0x66));
        private static readonly Brush ReadoutBrush = CreateBrush(Color.FromRgb(0x22, 0x22, 0x22));
        private static readonly Brush ReadoutBackground = CreateBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        private static readonly Typeface LabelTypeface = new Typeface("Consolas");
        private static readonly Dictionary<string, Pen> LinePens = new Dictionary<string, Pen>();

        private TimeSpan _timeOffset = TimeSpan.Zero;
        private DateTime _anchor = DateTime.Now;
        private Point _cursor;
        private bool _cursorInside;

        /// <summary>要画的曲线（对象本身会被持续追加采样，所以只需要设置一次）。</summary>
        public IList<SeriesBuffer> Series { get; set; }

        /// <summary>显示最近多长时间的采样。</summary>
        public TimeSpan Window { get; set; }

        /// <summary>纵轴缩放的上下限与每档倍率。</summary>
        public const double MinValueZoom = 0.1;
        public const double MaxValueZoom = 50.0;
        public const double ZoomStep = 1.25;

        /// <summary>普通曲线、选中曲线的线宽。</summary>
        private const double NormalThickness = 1.4;
        private const double SelectedThickness = 2.8;

        /// <summary>双击选中曲线时，离得多近才算点中（像素）。</summary>
        private const double HitTolerance = 14;

        /// <summary>没有数据时显示的文字。</summary>
        public string EmptyMessage { get; set; }

        /// <summary>当前被选中的曲线 key；null 表示没有选中（选中的那条会加粗并画在最上面）。</summary>
        public string SelectedKey { get; private set; }

        /// <summary>选中变化。</summary>
        public event Action<string> SelectionChanged;

        /// <summary>双击到某条曲线（参数是它的 key）。</summary>
        public event Action<string> CurveActivated;

        /// <summary>最近一次渲染用的布局，供命中测试与统计取窗口用。</summary>
        public ChartLayout CurrentLayout { get; private set; }

        /// <summary>纵轴缩放倍数：1 = 按数据铺满（默认），&gt;1 放大，&lt;1 缩小。</summary>
        public double ValueZoom { get; private set; }

        /// <summary>
        /// 回看历史的时间偏移：0 = 跟着最新数据，&gt;0 = 把画面钉在「设置这个值时」的那一刻往回偏移的位置。
        /// </summary>
        public TimeSpan TimeOffset
        {
            get { return _timeOffset; }
            set
            {
                _timeOffset = value;
                _anchor = DateTime.Now;     // 拖动滑块时把窗口钉在此刻，画面才不会继续往前滑
                InvalidateLayout();
            }
        }

        /// <summary>纵轴缩放倍数变化（滚轮或按钮都会触发，用于更新界面上的显示）。</summary>
        public event Action ValueZoomChanged;

        public ChartPlotElement()
        {
            Window = TimeSpan.FromMinutes(1);
            ValueZoom = 1.0;
            EmptyMessage = "开始轮询后，解析出的电压 / 电流 / 功率 / 温度会在这里画成曲线。";

            // 兜底：无论怎么放大，内容都不许画到这个控件之外
            ClipToBounds = true;
        }

        /// <summary>按倍率调整纵轴缩放。滚轮和按钮都走这里。</summary>
        public void ZoomValue(double factor)
        {
            SetValueZoom(ValueZoom * factor);
        }

        /// <summary>恢复成「按数据自动铺满」。</summary>
        public void ResetValueZoom()
        {
            SetValueZoom(1.0);
        }

        private void SetValueZoom(double zoom)
        {
            if (double.IsNaN(zoom) || double.IsInfinity(zoom))
                zoom = 1.0;

            if (zoom < MinValueZoom) zoom = MinValueZoom;
            if (zoom > MaxValueZoom) zoom = MaxValueZoom;
            if (Math.Abs(zoom - ValueZoom) < 1e-9)
                return;

            ValueZoom = zoom;
            Action handler = ValueZoomChanged;
            if (handler != null)
                handler();
            InvalidateLayout();
        }

        /// <summary>鼠标滚轮：直接缩放纵轴（时间轴用上面的时间窗下拉控制）。</summary>
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            ZoomValue(e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep);
            e.Handled = true;
        }

        /// <summary>选中某条曲线（传 null 取消选中）。</summary>
        public void SelectCurve(string key)
        {
            if (SelectedKey == key)
                return;

            SelectedKey = key;
            Action<string> handler = SelectionChanged;
            if (handler != null)
                handler(key);
            InvalidateVisual();
        }

        /// <summary>当前画面显示的时间范围（和 OnRender 用的是同一套算法）。</summary>
        public void ResolveVisibleWindow(out DateTime from, out DateTime to)
        {
            ChartMath.ResolveWindow(Window, TimeOffset, DateTime.Now, _anchor, out from, out to);
        }

        /// <summary>双击曲线：找出离光标最近的那条（够近才算）。</summary>
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            // FrameworkElement 没有 OnMouseDoubleClick（那是 Control 的），这里按连击次数判断
            if (e.ClickCount < 2)
                return;

            string key = HitTestCurve(e.GetPosition(this));
            if (key != null)
            {
                Action<string> handler = CurveActivated;
                if (handler != null)
                    handler(key);
            }
            e.Handled = true;
        }

        private string HitTestCurve(Point cursor)
        {
            ChartLayout layout = CurrentLayout;
            if (layout == null)
                return null;

            string best = null;
            double bestDistance = HitTolerance;

            for (int b = 0; b < layout.Bands.Count; b++)
            {
                ChartBand band = layout.Bands[b];
                if (cursor.Y < band.PlotTop - HitTolerance
                    || cursor.Y > band.PlotTop + band.PlotHeight + HitTolerance)
                    continue;

                for (int i = 0; i < band.Lines.Count; i++)
                {
                    ChartLine line = band.Lines[i];
                    if (!line.HasData)
                        continue;

                    int nearest = NearestIndex(line.Xs, cursor.X);
                    double distance = Math.Abs(line.Ys[nearest] - cursor.Y);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = line.Key;
                    }
                }
            }

            return best;
        }

        /// <summary>上一次算出来的布局。鼠标移动时直接复用它，不重算。</summary>
        private ChartLayout _cachedLayout;

        /// <summary>布局失效：数据、时间窗、缩放或尺寸变了，下一帧必须重算。</summary>
        private bool _layoutDirty = true;

        /// <summary>
        /// 数据 / 时间窗 / 缩放 / 尺寸变了 —— 下一帧要重算布局。
        ///
        /// 与 <see cref="UIElement.InvalidateVisual"/> 的区别就在这里：后者只重画，
        /// 而重画时如果不复用布局，就会在**每一次鼠标移动**上把 <see cref="ChartMath.Build"/>
        /// 重跑一遍。那个函数按时间窗从最旧一个采样扫起，满缓冲下 192 条曲线约
        /// 3.8M 次操作 ≈ 50–150ms，指针每秒 60–125 次事件——界面会被它拖住，
        /// 而轮询的续体是 dispatcher 投递的，于是采样节拍也跟着抖。
        /// </summary>
        public void InvalidateLayout()
        {
            _layoutDirty = true;
            InvalidateVisual();
        }

        /// <summary>阈值线变了：只需重画，布局不用重算。</summary>
        public void InvalidateThresholds()
        {
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            _layoutDirty = true;
            base.OnRenderSizeChanged(sizeInfo);
        }

        /// <summary>鼠标移动时画出十字准线，直接显示那一刻每条曲线的数值。</summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            _cursor = e.GetPosition(this);
            _cursorInside = true;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _cursorInside = false;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            drawingContext.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

            double plotLeft = LeftMargin;
            double plotWidth = width - LeftMargin - RightMargin;
            double plotTop = TopMargin;
            double plotHeight = height - TopMargin - BottomMargin;
            if (plotWidth <= 10 || plotHeight <= 10)
                return;

            ChartLayout layout = _cachedLayout;
            if (_layoutDirty || layout == null)
            {
                DateTime from;
                DateTime to;
                ChartMath.ResolveWindow(Window, TimeOffset, DateTime.Now, _anchor, out from, out to);

                layout = ChartMath.Build(Series, from, to,
                    plotLeft, plotTop, plotWidth, plotHeight, MaxPointsPerLine, ValueZoom);
                _cachedLayout = layout;
                _layoutDirty = false;
            }

            CurrentLayout = layout;     // 命中测试与统计取窗口都要用

            if (layout.Bands.Count == 0)
            {
                DrawLabel(drawingContext, EmptyMessage, width / 2, height / 2, 1.0, TextAlignment.Center);
                return;
            }

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            for (int b = 0; b < layout.Bands.Count; b++)
            {
                ChartBand band = layout.Bands[b];

                // 量级够大就把 mV/mA 换成 V/A，读数才好看
                double bandLow;
                double bandHigh;
                bool hasRange = TryGetBandRange(band, out bandLow, out bandHigh);

                double displayScale = 1.0;
                string displayUnit = band.Unit;
                if (hasRange)
                    ChartMath.TryScaleUnit(band.Unit, Math.Max(Math.Abs(bandLow), Math.Abs(bandHigh)),
                        out displayScale, out displayUnit);

                double step = ChartMath.StepOf(band.TickValues) * displayScale;

                // 矮的时候少标几个刻度：全标会叠成一团，反而什么都看不清
                bool hasHeader = band.HeaderHeight > 0;

                for (int t = 0; t < band.TickValues.Length; t++)
                {
                    double y = ChartMath.MapY(band.TickValues[t], band.Min, band.Max, band.PlotTop, band.PlotHeight);
                    drawingContext.DrawLine(GridPen, new Point(plotLeft, y), new Point(plotLeft + plotWidth, y));

                    if (!ChartMath.ShouldLabelTick(t, band.TickValues.Length, band.PlotHeight))
                        continue;

                    DrawLabel(drawingContext, ChartMath.FormatTick(band.TickValues[t] * displayScale, step),
                        plotLeft - 6, y, pixelsPerDip, TextAlignment.Right);
                }

                // 阈值参考线：落在本带显示范围内的才画（超出范围时画在带外没有意义）
                double? threshold = ThresholdSet.For(Thresholds, displayUnit);
                if (threshold.HasValue)
                {
                    double raw = threshold.Value / displayScale;
                    if (raw >= band.Min && raw <= band.Max)
                    {
                        double ty = ChartMath.MapY(raw, band.Min, band.Max, band.PlotTop, band.PlotHeight);
                        drawingContext.DrawLine(ThresholdPen,
                            new Point(plotLeft, ty), new Point(plotLeft + plotWidth, ty));

                        // 标签放**左侧**：右侧是「变化 x」读数常驻的地方，压上去会两边都看不清
                        DrawLabel(drawingContext,
                            ChartMath.FormatTick(threshold.Value, step) + displayUnit,
                            plotLeft + 4, ty, pixelsPerDip, TextAlignment.Left);
                    }
                }

                // 单位：有页眉就放页眉左侧；挤得没页眉时放右上角，避免和刻度标签重叠
                var unitText = new FormattedText(displayUnit, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, LabelTypeface, LabelFontSize, UnitBrush, pixelsPerDip);
                drawingContext.DrawText(unitText, new Point(
                    hasHeader ? plotLeft + 4 : plotLeft + plotWidth - unitText.Width - 4,
                    band.Top + 1));

                // 「变化」文本只占页眉那一行，绝不压住曲线
                if (!hasRange || !hasHeader)
                    continue;

                string rangeText = "变化 " + ChartMath.FormatTick((bandHigh - bandLow) * displayScale, step)
                    + " " + displayUnit;
                var rangeFormatted = new FormattedText(rangeText, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, LabelTypeface, LabelFontSize, RangeBrush, pixelsPerDip);
                drawingContext.DrawText(rangeFormatted,
                    new Point(plotLeft + plotWidth - rangeFormatted.Width - 4, band.Top + 1));
            }

            // 时间轴
            for (int t = 0; t < layout.TimeTickXs.Length; t++)
            {
                double x = layout.TimeTickXs[t];
                drawingContext.DrawLine(GridPen, new Point(x, plotTop), new Point(x, plotTop + plotHeight));
                DrawLabel(drawingContext, layout.TimeTickLabels[t], x, plotTop + plotHeight + 10,
                    pixelsPerDip, TextAlignment.Center);
            }

            drawingContext.DrawLine(AxisPen, new Point(plotLeft, plotTop), new Point(plotLeft, plotTop + plotHeight));
            drawingContext.DrawLine(AxisPen, new Point(plotLeft, plotTop + plotHeight),
                new Point(plotLeft + plotWidth, plotTop + plotHeight));

            // 先画没选中的，再画选中的：选中的那条加粗并压在最上面，一眼能找到。
            // 每画一个子图都要把它裁在自己的绘图区里——放大后超出量程的点会被映射到格子外面，
            // WPF 自绘默认不裁剪，那样曲线就会盖住别的子图、甚至画到控件外面去。
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantSelected = pass == 1;
                for (int b = 0; b < layout.Bands.Count; b++)
                {
                    ChartBand band = layout.Bands[b];

                    drawingContext.PushClip(new RectangleGeometry(new Rect(
                        plotLeft, band.PlotTop, plotWidth, Math.Max(0, band.PlotHeight))));
                    try
                    {
                        for (int i = 0; i < band.Lines.Count; i++)
                        {
                            ChartLine line = band.Lines[i];
                            bool selected = SelectedKey != null && line.Key == SelectedKey;
                            if (selected != wantSelected)
                                continue;

                            DrawLine(drawingContext, line, selected ? SelectedThickness : NormalThickness);
                        }
                    }
                    finally
                    {
                        drawingContext.Pop();
                    }
                }
            }

            if (_cursorInside)
                DrawCrosshair(drawingContext, layout, pixelsPerDip, plotLeft, plotTop, plotWidth, plotHeight);
        }

        /// <summary>子图里所有可见曲线的取值范围（用来看波动）。</summary>
        private static bool TryGetBandRange(ChartBand band, out double low, out double high)
        {
            low = double.MaxValue;
            high = double.MinValue;
            bool any = false;

            for (int i = 0; i < band.Lines.Count; i++)
            {
                ChartLine line = band.Lines[i];
                if (!line.HasData)
                    continue;
                if (line.Min < low) low = line.Min;
                if (line.Max > high) high = line.Max;
                any = true;
            }

            return any && high >= low;
        }

        /// <summary>
        /// 十字准线：竖线 + 每条可见曲线上最近的采样点 + 该点的数值读数 + 光标处的时间。
        /// 这是「图上到底是多少」最直接的答案。
        /// </summary>
        private void DrawCrosshair(DrawingContext drawingContext, ChartLayout layout, double pixelsPerDip,
            double plotLeft, double plotTop, double plotWidth, double plotHeight)
        {
            double x = _cursor.X;
            if (x < plotLeft || x > plotLeft + plotWidth)
                return;

            drawingContext.DrawLine(CrosshairPen,
                new Point(x, plotTop), new Point(x, plotTop + plotHeight));

            for (int b = 0; b < layout.Bands.Count; b++)
            {
                ChartBand band = layout.Bands[b];

                double bandLow;
                double bandHigh;
                double displayScale = 1.0;
                string displayUnit = band.Unit;
                if (TryGetBandRange(band, out bandLow, out bandHigh))
                    ChartMath.TryScaleUnit(band.Unit, Math.Max(Math.Abs(bandLow), Math.Abs(bandHigh)),
                        out displayScale, out displayUnit);

                double step = ChartMath.StepOf(band.TickValues) * displayScale;

                for (int i = 0; i < band.Lines.Count; i++)
                {
                    ChartLine line = band.Lines[i];
                    if (!line.HasData)
                        continue;

                    int nearest = NearestIndex(line.Xs, x);
                    double px = line.Xs[nearest];
                    double py = line.Ys[nearest];

                    // 放大后这个点可能已经被挤出本子图，那就没什么好标的了
                    if (py < band.PlotTop - 2 || py > band.PlotTop + band.PlotHeight + 2)
                        continue;

                    drawingContext.DrawEllipse(GetPen(line.ColorHex, NormalThickness).Brush, null, new Point(px, py), 3, 3);

                    string text = ChartMath.FormatTick(line.Values[nearest] * displayScale, step) + " " + displayUnit;
                    var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, LabelTypeface, LabelFontSize,
                        GetPen(line.ColorHex, NormalThickness).Brush, pixelsPerDip);

                    double tx = px + 7;
                    if (tx + formatted.Width > plotLeft + plotWidth)
                        tx = px - 7 - formatted.Width;
                    double ty = py - formatted.Height - 3;
                    if (ty < band.PlotTop)
                        ty = py + 4;

                    drawingContext.DrawRectangle(ReadoutBackground, null,
                        new Rect(tx - 2, ty - 1, formatted.Width + 4, formatted.Height + 2));
                    drawingContext.DrawText(formatted, new Point(tx, ty));
                }
            }

            // 顶部的时刻读数
            double ratio = (x - plotLeft) / plotWidth;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            long spanTicks = (layout.To - layout.From).Ticks;
            var moment = new DateTime(layout.From.Ticks + (long)(spanTicks * ratio), layout.From.Kind);

            var stamp = new FormattedText(moment.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, LabelFontSize, ReadoutBrush, pixelsPerDip);

            double sx = x + 7;
            if (sx + stamp.Width > plotLeft + plotWidth)
                sx = x - 7 - stamp.Width;
            drawingContext.DrawRectangle(ReadoutBackground, null,
                new Rect(sx - 2, plotTop + 2, stamp.Width + 4, stamp.Height + 2));
            drawingContext.DrawText(stamp, new Point(sx, plotTop + 3));
        }

        private static int NearestIndex(double[] xs, double target)
        {
            int best = 0;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < xs.Length; i++)
            {
                double distance = Math.Abs(xs[i] - target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            return best;
        }

        private static void DrawLine(DrawingContext drawingContext, ChartLine line, double thickness)
        {
            int count = line.Xs.Length;
            if (count < 1)
                return;

            if (count == 1)
            {
                drawingContext.DrawEllipse(null, GetPen(line.ColorHex, thickness),
                    new Point(line.Xs[0], line.Ys[0]), 1.5, 1.5);
                return;
            }

            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new Point(line.Xs[0], line.Ys[0]), false, false);
                for (int i = 1; i < count; i++)
                    context.LineTo(new Point(line.Xs[i], line.Ys[i]), true, false);
            }
            geometry.Freeze();      // 冻结后 WPF 可以缓存几何，重绘开销小很多
            drawingContext.DrawGeometry(null, GetPen(line.ColorHex, thickness), geometry);
        }

        private void DrawLabel(DrawingContext drawingContext, string text, double x, double y,
            double pixelsPerDip, TextAlignment alignment)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelTypeface, LabelFontSize, LabelBrush, pixelsPerDip);

            double offsetX = x;
            if (alignment == TextAlignment.Right)
                offsetX = x - formatted.Width;
            else if (alignment == TextAlignment.Center)
                offsetX = x - formatted.Width / 2;

            drawingContext.DrawText(formatted, new Point(offsetX, y - formatted.Height / 2));
        }

        private static Pen GetPen(string colorHex, double thickness)
        {
            string cacheKey = colorHex + "|" + thickness.ToString(CultureInfo.InvariantCulture);

            Pen pen;
            if (LinePens.TryGetValue(cacheKey, out pen))
                return pen;

            Color color;
            try
            {
                color = (Color)ColorConverter.ConvertFromString(colorHex);
            }
            catch (Exception)
            {
                color = Colors.SteelBlue;
            }

            pen = CreatePen(color, thickness);
            LinePens[cacheKey] = pen;
            return pen;
        }

        private static Pen CreatePen(Color color, double thickness)
        {
            var pen = new Pen(CreateBrush(color), thickness);
            pen.Freeze();
            return pen;
        }

        private static Pen CreateDashedPen(Color color, double thickness)
        {
            var pen = new Pen(CreateBrush(color), thickness)
            {
                DashStyle = new DashStyle(new double[] { 3, 3 }, 0),
            };
            pen.Freeze();
            return pen;
        }

        private static Brush CreateBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
