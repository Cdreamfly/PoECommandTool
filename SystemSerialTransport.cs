using System;
using System.IO.Ports;

namespace WpfApp1.Serial
{
    /// <summary>
    /// <see cref="ISerialTransport"/> 的真实实现——整个工程里**唯一**碰 System.IO.Ports 的文件。
    ///
    /// net48 下 SerialPort 位于 System.dll（csproj 已引用），不需要额外的 NuGet 包。
    /// </summary>
    public sealed class SystemSerialTransport : ISerialTransport
    {
        /// <summary>读超时。既是「没数据」的判定，也是读循环检查取消的周期（见 SerialSession）。</summary>
        private const int ReadTimeoutMs = 200;

        private const int WriteTimeoutMs = 1000;

        private SerialPort _port;

        public bool IsOpen
        {
            get { return _port != null && _port.IsOpen; }
        }

        public void Open(SerialPortSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");

            Close();

            var port = new SerialPort(
                settings.PortName,
                settings.BaudRate,
                ToParity(settings.Parity),
                settings.DataBits,
                ToStopBits(settings.StopBits));

            port.ReadTimeout = ReadTimeoutMs;
            port.WriteTimeout = WriteTimeoutMs;
            port.Handshake = Handshake.None;
            // 多数 USB 转串口线要靠 DTR/RTS 拉起电平转换芯片
            port.DtrEnable = true;
            port.RtsEnable = true;

            try
            {
                port.Open();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();
            }
            catch (Exception)
            {
                port.Dispose();
                throw;
            }

            _port = port;
        }

        public void Close()
        {
            SerialPort port = _port;
            _port = null;
            if (port == null)
                return;

            try
            {
                if (port.IsOpen)
                    port.Close();
            }
            catch (Exception)
            {
                // 设备可能已经不在了，关闭异常没有意义
            }
            finally
            {
                port.Dispose();
            }
        }

        /// <summary>
        /// 读一段数据；读超时返回 0（不是错误），端口未打开返回 -1（终止条件，见 <see cref="ISerialTransport.Read"/>）。
        /// </summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            SerialPort port = _port;
            if (port == null || !port.IsOpen)
                return -1;      // 已关闭 ≠ 超时：这里必须让读循环停下来，返回 0 会变成满速空转

            try
            {
                return port.Read(buffer, offset, count);
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }

        public void Write(byte[] data, int offset, int count)
        {
            SerialPort port = _port;
            if (port == null || !port.IsOpen)
                throw new InvalidOperationException("串口未打开。");

            port.Write(data, offset, count);
        }

        public string[] GetPortNames()
        {
            try
            {
                return SerialPort.GetPortNames();
            }
            catch (Exception)
            {
                return new string[0];     // 取不到就当作没有可用串口
            }
        }

        public void Dispose()
        {
            Close();
        }

        private static Parity ToParity(SerialParity parity)
        {
            switch (parity)
            {
                case SerialParity.Odd: return Parity.Odd;
                case SerialParity.Even: return Parity.Even;
                case SerialParity.Mark: return Parity.Mark;
                case SerialParity.Space: return Parity.Space;
                default: return Parity.None;
            }
        }

        private static StopBits ToStopBits(SerialStopBits stopBits)
        {
            switch (stopBits)
            {
                case SerialStopBits.Two: return StopBits.Two;
                case SerialStopBits.OnePointFive: return StopBits.OnePointFive;
                case SerialStopBits.None: return StopBits.None;
                default: return StopBits.One;
            }
        }
    }
}
