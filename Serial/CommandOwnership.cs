namespace WpfApp1.Serial
{
    /// <summary>一条命令的归属：命令号，以及界面上用什么小标题显示它。</summary>
    public sealed class CommandOwner
    {
        public string Key { get; private set; }
        public string Title { get; private set; }

        public CommandOwner(string key, string title)
        {
            Key = key;
            Title = title;
        }
    }

    /// <summary>
    /// 哪条查询命令归哪个界面入口管。
    ///
    /// 单独放在这里、不放在任何一方内部，是为了避开依赖反转：轮询的候选筛选
    /// （<see cref="PollPlan"/>）和「设备信息」面板（<c>DeviceInfoReader</c>）都只是**读**
    /// 这份归属，谁都不该依赖谁——否则加一个面板就会连带改变轮询的可选集合，
    /// 而将来删掉面板时那些命令会静默地永远留在不可轮询状态。
    ///
    /// 这份清单也是**唯一**的一份：面板的命令表就是从这里生成的，
    /// 不另抄一遍键名（两份手维护的同一事实早晚会漂移）。
    /// </summary>
    public static class CommandOwnership
    {
        /// <summary>
        /// 由「设备信息」面板独家接管的命令，顺序即面板上的显示顺序。
        ///
        /// 这些是身份/诊断类查询：回答的是「这台设备是什么、上次为什么重启」这类
        /// 不变或低频的问题，周期性重读只会白占串口带宽。所以它们不在轮询候选里，
        /// 也不该再出现在轮询列表的置灰清单里（那会自相矛盾：一个列表说「暂不支持」，
        /// 另一个面板说「点这里读」）。
        /// </summary>
        public static readonly CommandOwner[] DeviceInfoPanel =
        {
            new CommandOwner("0x40", "设备身份"),
            new CommandOwner("0xC0-04", "配置版本"),
            new CommandOwner("0x50", "芯片类型"),
            new CommandOwner("0x4C", "芯片地址"),
            new CommandOwner("0x4A", "全局参数"),
            new CommandOwner("0x47", "复位原因"),
        };

        /// <summary>轮询列表里的置灰行是否该跳过这条命令（它已经在面板里有正经入口了）。</summary>
        public static bool OwnedByDeviceInfoPanel(string key)
        {
            for (int i = 0; i < DeviceInfoPanel.Length; i++)
                if (string.Equals(DeviceInfoPanel[i].Key, key, System.StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }
    }
}
