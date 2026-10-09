using System;

namespace PoECommandTool.Serial
{
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
    public sealed class SerialPortSettings : TransportSettings
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

        public override TransportKind Kind
        {
            get { return TransportKind.Serial; }
        }

        /// <summary>形如 "COM3 115200 8-N-1"，用于界面状态栏。</summary>
        public override string Describe()
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
}
