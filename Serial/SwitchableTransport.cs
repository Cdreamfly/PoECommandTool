using System;

namespace PoECommandTool.Serial
{
    /// <summary>
    /// 一个「可换芯」的传输层：会话在构造时拿到它就终身不变，真正用哪条链路由
    /// <see cref="Install"/> 决定。
    ///
    /// 为什么要有它：<see cref="SerialSession"/> 在界面初始化时就被注入传输层并一直持有，
    /// 而「用串口还是 Telnet/SSH」是用户点连接时才定的。若为此每换一次链路就重建一个会话，
    /// 事件接线（Fault/LineReceived/FrameReceived/LineRejected）都得重来一遍；
    /// 而这个转发器只有几十行，且**完全不碰会话里的读循环**——那里曾经出过僵尸读循环的故障。
    ///
    /// 约定：**只在没有连接的时候装芯**（装的时候会把旧的关掉并释放）。
    /// </summary>
    public sealed class SwitchableTransport : ITransport
    {
        private ITransport _inner;

        /// <summary>换上要真正干活的传输层；旧的会被关掉并释放。</summary>
        public void Install(ITransport inner)
        {
            if (inner == null) throw new ArgumentNullException("inner");

            if (_inner != null)
            {
                _inner.Close();
                _inner.Dispose();
            }

            _inner = inner;
        }

        public bool IsOpen
        {
            get { return _inner != null && _inner.IsOpen; }
        }

        public void Open(TransportSettings settings)
        {
            if (_inner == null)
                throw new InvalidOperationException("还没有选择连接方式。");

            _inner.Open(settings);
        }

        public void Close()
        {
            if (_inner != null)
                _inner.Close();
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            // 没装芯就当作「已关闭」：返回负数让读循环立刻终止。
            // 这里绝不能返回 0——那是「本次读超时」，读循环会满速空转。
            return _inner == null ? -1 : _inner.Read(buffer, offset, count);
        }

        public void Write(byte[] data, int offset, int count)
        {
            if (_inner == null)
                throw new InvalidOperationException("还没有选择连接方式。");

            _inner.Write(data, offset, count);
        }

        public void Dispose()
        {
            if (_inner != null)
            {
                _inner.Dispose();
                _inner = null;
            }
        }
    }
}
