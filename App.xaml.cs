using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PoECommandTool
{
    /// <summary>
    /// 应用入口。这里原先是个空类——没有任何全局异常处理，于是三处异常都会**静默消失**：
    ///
    /// * UI 线程上没被接住的：进程直接死，用户只看到窗口消失；
    /// * 读循环/线程池上没被接住的：读循环那个 Task 没人 await，异常成了「未观察的
    ///   任务异常」，要到 GC 才可能被注意到——而界面还写着「已打开」；
    /// * 被丢弃的 Task 上的：同上，但连 GC 都不一定报。
    ///
    /// 这是一个现场用的调试工具：跑了一半崩掉、或者收包线程死了却不吭声，
    /// 都比看到一句错误提示糟糕得多。所以这里统一**弹出来并尽量活下去**。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        /// <summary>UI 线程上的异常：报出来，并**不**结束进程——手上的采集不该因为一次绘制出错就全丢。</summary>
        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Report("界面线程", e.Exception);
            e.Handled = true;
        }

        /// <summary>非 UI 线程上的异常。到这里已经救不回来了，至少让用户知道发生了什么。</summary>
        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Report("后台线程", e.ExceptionObject as Exception);
        }

        /// <summary>
        /// 被丢弃的 Task 上的异常。默认会在 GC 时把进程干掉，而且现场只看到"突然没了"。
        /// 收包线程正是这条路径——标成已观察，然后如实报出来。
        /// </summary>
        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            Report("后台任务", e.Exception == null ? null : e.Exception.GetBaseException());
        }

        private static void Report(string where, Exception ex)
        {
            string detail;

            if (ex == null)
            {
                detail = "（没有异常对象）";
            }
            else
            {
                var builder = new StringBuilder();
                builder.Append(ex.GetType().FullName).Append("：").Append(ex.Message);

                // 内部异常常常才是根因（AggregateException、TargetInvocationException 之类）
                Exception inner = ex.InnerException;
                int guard = 0;
                while (inner != null && guard++ < 5)
                {
                    builder.Append(Environment.NewLine).Append("  ← ")
                        .Append(inner.GetType().FullName).Append("：").Append(inner.Message);
                    inner = inner.InnerException;
                }

                builder.Append(Environment.NewLine).Append(Environment.NewLine).Append(ex.StackTrace);
                detail = builder.ToString();
            }

            try
            {
                MessageBox.Show(
                    where + "出了未处理的错误：" + Environment.NewLine + Environment.NewLine + detail,
                    AppVersion.Title + " — 未处理的错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception)
            {
                // 连弹框都失败（例如已经进入关闭流程），不再往外抛
            }
        }
    }
}
