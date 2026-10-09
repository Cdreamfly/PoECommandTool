using System;
using System.Reflection;
using System.Windows;

namespace PoECommandTool
{
    /// <summary>
    /// 关于对话框：显示应用名称、版本、版权等信息。
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();

            // 版本号读 AppVersion（与标题栏、启动日志、程序集版本同一个来源）
            VersionText.Text = AppVersion.Full;

            var asm = Assembly.GetExecutingAssembly();
            var company = asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
            AuthorText.Text = "Author：" + (string.IsNullOrEmpty(company) ? "CMF" : company);

            var copyright = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
            CopyrightText.Text = copyright ?? "Copyright © 2025";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
