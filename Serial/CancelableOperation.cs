using System;
using System.Threading;
using System.Threading.Tasks;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 一次「可取消的后台操作」的生命周期：忙标志、取消源、任务。
    ///
    /// 抽出来是因为这套东西在串口页被抄了**三份**（设备信息读取、命令组装发送、固件下载），
    /// 而其中一条关键理由只在一份里被注释过——
    /// **Cancel 掐不断一个已经卡在驱动写入里的调用**，所以等它停下必须带上限。
    /// 修一份漏两份，另外两条关闭路径就会挂到驱动返回为止。
    ///
    /// 线程约定：只在 **UI 线程**上调用（这套东西的背后全是 WPF 控件状态）。
    /// </summary>
    public sealed class CancelableOperation
    {
        private CancellationTokenSource _cts;

        public bool IsBusy
        {
            get { return _cts != null; }
        }

        /// <summary>当前这一轮的取消令牌；不在忙时是 <see cref="CancellationToken.None"/>。</summary>
        public CancellationToken Token
        {
            get
            {
                CancellationTokenSource cts = _cts;
                return cts == null ? CancellationToken.None : cts.Token;
            }
        }

        /// <summary>正在跑的那一笔任务；没在跑时为 null。由发起方在开始时赋值。</summary>
        public Task CurrentTask { get; set; }

        /// <summary>
        /// 开始一次。已经在跑时返回 false——调用方据此决定是「忽略」还是「把这次点击当成取消」。
        /// </summary>
        public bool TryBegin()
        {
            if (_cts != null)
                return false;

            _cts = new CancellationTokenSource();
            return true;
        }

        /// <summary>通知停下（不等待）。幂等；取消源已经被释放时也不抛。</summary>
        public void Cancel()
        {
            CancellationTokenSource cts = _cts;
            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经收尾了，没事
            }
        }

        /// <summary>
        /// 等它退干净，**带超时**。
        ///
        /// 不设上限会怎样：Cancel 掐不断一个已经卡在驱动写入里的调用，而这里通常是从
        /// 关闭流程（UI 线程）调过来的——无限等就等于关不掉窗口。
        /// </summary>
        public async Task AwaitStoppedAsync(int timeoutMs)
        {
            Task task = CurrentTask;
            if (task == null)
                return;

            try
            {
                await Task.WhenAny(task, Task.Delay(timeoutMs));
            }
            catch (Exception)
            {
                // 关闭流程里不往外抛
            }
        }

        /// <summary>收尾并释放取消源。放在 finally 里调。</summary>
        public void Finish()
        {
            CancellationTokenSource cts = _cts;
            _cts = null;

            if (cts != null)
                cts.Dispose();
        }
    }
}
