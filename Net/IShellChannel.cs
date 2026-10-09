using System;

namespace PoECommandTool.Net
{
    /// <summary>
    /// 一条「壳」通道：把对端的输出当成**文本**取回来。
    ///
    /// 抽这一层是为了让读取泵（<see cref="ShellReadPump"/>）能在没有 SSH.NET 的环境里
    /// 被断言——真实的实现包在 SshTransport 里，依赖 Renci.SshNet，Linux 上的断言工程编不到。
    /// </summary>
    public interface IShellChannel : IDisposable
    {
        bool IsOpen { get; }

        /// <summary>
        /// 取回当前**已经缓冲**的文本；没有新数据时返回 null 或空串（不要阻塞等待）。
        /// </summary>
        string Read();

        void Write(byte[] data, int offset, int count);
    }
}
