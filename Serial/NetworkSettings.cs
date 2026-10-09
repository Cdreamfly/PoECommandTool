using System;
using System.Globalization;

namespace PoECommandTool.Serial
{
    /// <summary>
    /// Telnet / SSH 的端点与登录信息。
    ///
    /// 两种网络链路**共用一个类型**：它们的形状是同一套（主机、端口、账号、口令、私钥），
    /// 差别只在 <see cref="TransportSettings.Kind"/> 与默认端口。分成两个类只会让
    /// 「连接方式」下拉框的每一处分支都要写两遍。
    /// </summary>
    /// <remarks>
    /// **口令只活在内存里**：本类型不进配置文件、不进日志。<see cref="Describe"/> 也刻意
    /// 不含口令——那一行会出现在状态栏、启动日志和断开提示里。默认的 ToString()
    /// 只打印类型名，同样不泄漏。
    /// </remarks>
    public sealed class NetworkSettings : TransportSettings
    {
        private readonly TransportKind _kind;

        public NetworkSettings(TransportKind kind)
        {
            if (kind != TransportKind.Telnet && kind != TransportKind.Ssh)
                throw new ArgumentException("网络设置只用于 Telnet 或 SSH。", "kind");

            _kind = kind;
            Host = string.Empty;
            Port = DefaultPortFor(kind);
            User = string.Empty;
            Password = string.Empty;
            KeyFile = string.Empty;
        }

        public override TransportKind Kind
        {
            get { return _kind; }
        }

        public string Host { get; set; }
        public int Port { get; set; }

        /// <summary>登录账号。**留空表示假定设备控制台不要登录**，连上就直接收发。</summary>
        public string User { get; set; }

        /// <summary>登录口令。不落盘、不进日志。</summary>
        public string Password { get; set; }

        /// <summary>SSH 私钥文件（留空则用口令认证）。Telnet 不用。</summary>
        public string KeyFile { get; set; }

        /// <summary>Telnet 23、SSH 22。</summary>
        public static int DefaultPortFor(TransportKind kind)
        {
            return kind == TransportKind.Ssh ? 22 : 23;
        }

        /// <summary>口令是否需要：SSH 用私钥时可以没有口令。</summary>
        public bool UsesKeyFile
        {
            get { return !string.IsNullOrEmpty(KeyFile); }
        }

        /// <summary>形如 "telnet 10.0.0.5:23" 或 "ssh admin@10.0.0.5:22"。**不含口令。**</summary>
        public override string Describe()
        {
            string scheme = _kind == TransportKind.Ssh ? "ssh" : "telnet";
            string who = string.IsNullOrEmpty(User) ? string.Empty : User + "@";
            string where = string.IsNullOrEmpty(Host) ? "(未填主机)" : Host;

            return string.Format(CultureInfo.InvariantCulture, "{0} {1}{2}:{3}",
                scheme, who, where, Port);
        }
    }
}
