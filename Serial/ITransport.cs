using System;

namespace PoECommandTool.Serial
{
    /// <summary>链路类型：工具支持的三条通往设备的路。</summary>
    public enum TransportKind
    {
        /// <summary>本机串口（直连 UART）。</summary>
        Serial = 0,

        /// <summary>Telnet 登录设备调试控制台。</summary>
        Telnet = 1,

        /// <summary>SSH 登录设备调试控制台。</summary>
        Ssh = 2,
    }

    /// <summary>
    /// 传输配置的基类。<see cref="SerialPortSettings"/> 与网络端点设置都从这里派生。
    ///
    /// 做成基类、而不是「一堆可空字段拼起来的联合体」，是为了让 <c>SerialSession.Open</c>
    /// 只认这一个参数类型：已有的串口设置、以及它在界面与断言里的构造方式，都不用动。
    /// </summary>
    public abstract class TransportSettings
    {
        /// <summary>本设置属于哪条链路。</summary>
        public abstract TransportKind Kind { get; }

        /// <summary>状态栏上显示的一行，例如 "COM3 115200 8-N-1" 或 "ssh user@host:22"。</summary>
        /// <remarks>**不得包含口令**——这一行会进日志与状态栏。</remarks>
        public abstract string Describe();
    }

    /// <summary>链路故障类型。</summary>
    public enum TransportFaultKind
    {
        OpenFailed = 0,
        ReadFailed = 1,
        WriteFailed = 2,

        /// <summary>链路断了：串口被拔，或套接字被对端关闭。</summary>
        LinkLost = 3,
    }

    /// <summary>一次链路故障（带可读提示，供界面显示）。</summary>
    public sealed class TransportFault
    {
        public TransportFaultKind Kind { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
    }

    /// <summary>
    /// 传输层抽象：串口、Telnet、SSH 共用同一套契约。
    ///
    /// 契约刻意做成「同步 + 短超时」：<see cref="Read"/> 在超时后返回 0 而不是死等，
    /// 这样读循环每过一个超时周期就必然回到取消检查点，停止/关窗不会被卡住。
    /// （net48 上 BaseStream.ReadAsync 的 CancellationToken 并不能真正中断挂起的 ReadFile。）
    ///
    /// 「超时」与「已关闭」必须分开报告：超时是正常空闲（循环继续等），关闭是终止条件。
    /// 曾经两者都返回 0，而端口关闭时的 0 是**立刻**返回的（不走超时），
    /// 于是读循环在「端口已关、令牌还没取消」的窗口里满速空转，一个核心 100%。
    /// </summary>
    public interface ITransport : IDisposable
    {
        bool IsOpen { get; }

        void Open(TransportSettings settings);
        void Close();

        /// <summary>
        /// 读一段数据。
        /// 返回值 &gt; 0：读到的字节数；返回 0：本次读超时（无数据），不是错误，调用方应继续等；
        /// 返回负数：传输层已关闭（端口没打开），调用方应**终止读循环**，绝不能继续重试。
        /// </summary>
        /// <exception cref="System.IO.IOException">链路断开或错误。</exception>
        int Read(byte[] buffer, int offset, int count);

        void Write(byte[] data, int offset, int count);

        // 实现约定：Close() 必须**解除阻塞中的 Read**（让它在 1 秒内返回负数）。
        // 会话在关闭时会等旧读循环退出，超时就拒绝重开——卡住的 Read 会让链路再也关不掉、
        // 或者重开后两条读循环抢同一个链路、把每个字节劈成两半。
    }
}
