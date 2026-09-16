using System;

namespace WpfApp1.Serial
{
    /// <summary>串口故障类型。</summary>
    public enum SerialFaultKind
    {
        OpenFailed = 0,
        ReadFailed = 1,
        WriteFailed = 2,
        DeviceRemoved = 3,
        Closed = 4,
    }

    /// <summary>一次串口故障（带可读提示，供界面显示）。</summary>
    public sealed class SerialFault
    {
        public SerialFaultKind Kind { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
    }

    /// <summary>校验位。取值顺序与 System.IO.Ports.Parity 一致，便于在适配层直接转换。</summary>
    public enum SerialParity
    {
        None = 0,
        Odd = 1,
        Even = 2,
        Mark = 3,
        Space = 4,
    }

    /// <summary>停止位。取值顺序与 System.IO.Ports.StopBits 一致。</summary>
    public enum SerialStopBits
    {
        None = 0,
        One = 1,
        Two = 2,
        OnePointFive = 3,
    }

    /// <summary>
    /// 串口配置。刻意不引用 System.IO.Ports，这样整套串口逻辑都能在没有该程序集的环境里编译测试。
    /// </summary>
    public sealed class SerialPortSettings
    {
        public SerialPortSettings()
        {
            PortName = string.Empty;
            BaudRate = 115200;      // 手册 2.1 节
            DataBits = 8;
            Parity = SerialParity.None;
            StopBits = SerialStopBits.One;
        }

        public string PortName { get; set; }
        public int BaudRate { get; set; }
        public int DataBits { get; set; }
        public SerialParity Parity { get; set; }
        public SerialStopBits StopBits { get; set; }

        /// <summary>形如 "COM3 115200 8-N-1"，用于界面状态栏。</summary>
        public string Describe()
        {
            string parity;
            switch (Parity)
            {
                case SerialParity.Odd: parity = "O"; break;
                case SerialParity.Even: parity = "E"; break;
                case SerialParity.Mark: parity = "M"; break;
                case SerialParity.Space: parity = "S"; break;
                default: parity = "N"; break;
            }

            string stop;
            switch (StopBits)
            {
                case SerialStopBits.Two: stop = "2"; break;
                case SerialStopBits.OnePointFive: stop = "1.5"; break;
                default: stop = "1"; break;
            }

            return string.Format("{0} {1} {2}-{3}-{4}",
                string.IsNullOrEmpty(PortName) ? "(未选择)" : PortName,
                BaudRate, DataBits, parity, stop);
        }
    }

    /// <summary>
    /// 串口传输层抽象。
    ///
    /// 契约刻意做成「同步 + 短超时」：<see cref="Read"/> 在超时后返回 0 而不是死等，
    /// 这样读循环每过一个超时周期就必然回到取消检查点，停止/关窗不会被卡住。
    /// （net48 上 BaseStream.ReadAsync 的 CancellationToken 并不能真正中断挂起的 ReadFile。）
    ///
    /// 「超时」与「已关闭」必须分开报告：超时是正常空闲（循环继续等），关闭是终止条件。
    /// 曾经两者都返回 0，而端口关闭时的 0 是**立刻**返回的（不走超时），
    /// 于是读循环在「端口已关、令牌还没取消」的窗口里满速空转，一个核心 100%。
    /// </summary>
    public interface ISerialTransport : IDisposable
    {
        bool IsOpen { get; }

        void Open(SerialPortSettings settings);
        void Close();

        /// <summary>
        /// 读一段数据。
        /// 返回值 &gt; 0：读到的字节数；返回 0：本次读超时（无数据），不是错误，调用方应继续等；
        /// 返回负数：传输层已关闭（端口没打开），调用方应**终止读循环**，绝不能继续重试。
        /// </summary>
        /// <exception cref="System.IO.IOException">设备断开或链路错误。</exception>
        int Read(byte[] buffer, int offset, int count);

        void Write(byte[] data, int offset, int count);

        void DiscardInBuffer();
        void DiscardOutBuffer();

        /// <summary>当前系统上可用的串口名（打开之前也能调用）。</summary>
        string[] GetPortNames();
    }
}
