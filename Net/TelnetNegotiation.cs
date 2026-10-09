using System;
using System.Collections.Generic;

namespace PoECommandTool.Net
{
    /// <summary>
    /// Telnet 选项协商（RFC 854 / 855）的字节流状态机。
    ///
    /// 为什么单独做成一个纯类：协商部分**不碰套接字**，因此能编进 Linux 上的断言工程、
    /// 用真断言把每种 IAC 序列的应答钉死。位序或应答方向搞反了，在真机上只表现为
    /// 「连上去没反应」或者「偶尔丢几个字节」，极难现场定位。
    ///
    /// 策略是**一切从简、全部拒绝**：我们只需要一个通往设备 `debug#` 控制台的字节管道，
    /// 不要终端类型（TTYPE）、不要窗口尺寸（NAWS）、不要行模式（LINEMODE），
    /// 回显也该由设备自己的控制台给，而不是由 Telnet 的 ECHO 选项给。
    /// 唯一的例外是 **SUPPRESS-GO-AHEAD**——它几乎到处都被协商，答应下来可以省掉
    /// 一来一回的确认停顿。
    ///
    /// 状态是**跨调用**保留的：一段协商序列被分到两次 Read 里也必须处理正确。
    /// 本类**永不抛异常**——收到垃圾字节最多是忽略，不能让读循环因为协商出错而死。
    /// </summary>
    public sealed class TelnetNegotiation
    {
        private const byte Iac = 0xFF;      // Interpret As Command
        private const byte Sb = 0xFA;       // 子协商开始
        private const byte Se = 0xF0;       // 子协商结束
        private const byte Will = 0xFB;
        private const byte Wont = 0xFC;
        private const byte Do = 0xFD;
        private const byte Dont = 0xFE;

        /// <summary>SUPPRESS-GO-AHEAD：唯一答应的选项。</summary>
        private const byte OptSuppressGoAhead = 3;

        private enum State
        {
            /// <summary>普通数据。</summary>
            Data = 0,

            /// <summary>刚读到 IAC，等下一个字节决定这是哪种序列。</summary>
            SawIac = 1,

            /// <summary>刚读完 WILL/WONT/DO/DONT，等选项号。</summary>
            WaitOption = 2,

            /// <summary>子协商中（其内容整段丢弃）。</summary>
            InSubnegotiation = 3,

            /// <summary>子协商中又读到 IAC，看是不是 SE。</summary>
            SubnegotiationIac = 4,
        }

        private State _state = State.Data;
        private byte _verb;

        /// <summary>
        /// 处理一段收到的字节。
        /// </summary>
        /// <param name="input">收到的原始字节。</param>
        /// <param name="count">有效长度。</param>
        /// <param name="payload">
        /// 输出：剥掉全部 IAC 序列之后的净数据，调用方把它当普通字节流处理。
        /// 内容**追加**到传入的表里，调用方负责先清空。
        /// </param>
        /// <param name="reply">
        /// 输出：需要回写给对端的协商字节（可能为空）。同样是追加。
        /// </param>
        public void Process(byte[] input, int count, IList<byte> payload, IList<byte> reply)
        {
            if (input == null) throw new ArgumentNullException("input");
            if (payload == null) throw new ArgumentNullException("payload");
            if (reply == null) throw new ArgumentNullException("reply");
            if (count < 0 || count > input.Length) throw new ArgumentOutOfRangeException("count");

            for (int i = 0; i < count; i++)
                Step(input[i], payload, reply);
        }

        private void Step(byte b, IList<byte> payload, IList<byte> reply)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Iac)
                        _state = State.SawIac;
                    else
                        payload.Add(b);
                    return;

                case State.SawIac:
                    if (b == Iac)
                    {
                        // IAC IAC = 转义的 0xFF，是数据
                        payload.Add(Iac);
                        _state = State.Data;
                    }
                    else if (b == Will || b == Wont || b == Do || b == Dont)
                    {
                        _verb = b;
                        _state = State.WaitOption;
                    }
                    else if (b == Sb)
                    {
                        _state = State.InSubnegotiation;
                    }
                    else
                    {
                        // 单字节命令（NOP/GA/EC…）：不需要，忽略
                        _state = State.Data;
                    }
                    return;

                case State.WaitOption:
                    NegotiateResponse(_verb, b, reply);
                    _state = State.Data;
                    return;

                case State.InSubnegotiation:
                    if (b == Iac)
                        _state = State.SubnegotiationIac;
                    return;

                case State.SubnegotiationIac:
                    // 只有 IAC SE 才是结束；子协商里的 IAC IAC 是它的数据，一并丢弃。
                    _state = b == Se ? State.Data : State.InSubnegotiation;
                    return;
            }
        }

        /// <summary>
        /// 对一条 WILL/WONT/DO/DONT 给出应答。全拒，除了 SUPPRESS-GO-AHEAD。
        ///
        /// 方向别搞反：对方 DO x =「请你启用 x」，我们若同意要回 WILL x；
        /// 对方 WILL x =「我要启用 x」，我们若同意要回 DO x。
        /// </summary>
        private static void NegotiateResponse(byte verb, byte option, IList<byte> reply)
        {
            if (verb == Do)
            {
                // 对方要求我们启用某选项
                reply.Add(Iac);
                reply.Add(option == OptSuppressGoAhead ? Will : Wont);
                reply.Add(option);
            }
            else if (verb == Will)
            {
                // 对方宣告它要启用某选项
                reply.Add(Iac);
                reply.Add(option == OptSuppressGoAhead ? Do : Dont);
                reply.Add(option);
            }
            // DONT / WONT 是对我们自己提议的拒绝（我们从没提议过），不需要应答
        }
    }
}
