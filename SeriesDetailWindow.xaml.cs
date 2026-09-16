using System.Windows;

namespace WpfApp1
{
    /// <summary>
    /// 曲线详情窗口：显示某条曲线在当前时间窗内的统计量（最大/最小/平均/峰峰值/标准差等）。
    /// 由图例双击或图上双击曲线打开，只读。
    /// </summary>
    public partial class SeriesDetailWindow : Window
    {
        public SeriesDetailWindow()
        {
            InitializeComponent();
        }

        public void SetContent(string title, string text)
        {
            Title = title;
            DetailBox.Text = text;
            DetailBox.ScrollToHome();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
