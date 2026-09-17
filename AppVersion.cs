namespace PoECommandTool
{
    /// <summary>
    /// 程序版本号——**要改版本只改这里的 <see cref="Number"/> 一处**。
    ///
    /// 标题栏、关于框、启动日志都读它；程序集版本在 Properties/AssemblyInfo.cs 里
    /// 用 <c>[assembly: AssemblyVersion(AppVersion.Number)]</c> 引用同一个常量，
    /// 所以文件属性里的版本也不会和界面显示的对不上。
    /// </summary>
    public static class AppVersion
    {
        /// <summary>版本号本体（三段式）。</summary>
        public const string Number = "1.3.1";

        /// <summary>带 v 前缀的显示形式，例如 "v1.3.1"。</summary>
        public static string Display
        {
            get { return "v" + Number; }
        }

        /// <summary>窗口标题用的完整名称。</summary>
        public static string Title
        {
            get { return "RTL8239 PoE Command Tool " + Display; }
        }

        /// <summary>关于框里显示的形式，例如 "VER 1.3.1"。</summary>
        public static string Full
        {
            get { return "VER " + Number; }
        }
    }
}
