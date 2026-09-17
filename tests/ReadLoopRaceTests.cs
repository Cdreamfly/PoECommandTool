using System;
using System.Threading;
using System.Threading.Tasks;
using PoECommandTool.Serial;

namespace Rtl8239Verify
{
    /// <summary>
    /// 读循环的生命周期竞态：<c>Open → Close（很快）→ Open</c>。
    ///
    /// 这是审查 T-H2 / A-C1 / P-C1 指出的危险序列，此前一条断言都没有——现有测试只覆盖了
    /// 「关串口」和「读故障后重开」这两种良性顺序，旧循环在这两种情况下都会自己退出。
    ///
    /// 修复前的机制：旧循环每到循环顶部就重读 _readCts 字段，于是「关—开」挨得很近时
    /// 它会读到**新一代**的、没被取消的取消源，永不退出；两条循环抢同一个端口，
    /// 每个字节被各读走一半。端口关闭时 Read 又是立刻返回的，于是还伴随满核空转。
    /// </summary>
    internal static partial class Program
    {
        private static volatile int _raceFrames;
        private static volatile SerialFault _raceFault;

        private static async Task ReadLoopRaceTests()
        {
            Console.WriteLine("读循环竞态（Open → Close → Open）");

            RaceReopenLeavesNoZombie();
            await RaceReopenDoesNotSplitOneFrame();
            RaceClosedTransportIsNotEmptySpin();
            RaceTransportClosedReportsFault();
            RaceCloseAfterReadFaultIsHarmless();
        }

        /// <summary>关—开后，旧循环必须已经退出。</summary>
        private static void RaceReopenLeavesNoZombie()
        {
            var fake = new FakeSerialTransport();
            using (var session = new SerialSession(fake))
            {
                session.Open(TestSettings());
                Thread.Sleep(50);           // 让第一个循环进到 Read 的轮询里，这正是竞态窗口

                Task first = session.ReadLoopTask;

                session.Close();
                session.Open(TestSettings());

                Check(first.IsCompleted,
                    "关—开后旧读循环已经退出（未退出 = 两条循环抢同一个端口）");

                session.Close();
            }
        }

        /// <summary>重开之后，一整行必须只被解析一次。</summary>
        private static async Task RaceReopenDoesNotSplitOneFrame()
        {
            var fake = new FakeSerialTransport();
            using (var session = new SerialSession(fake))
            {
                _raceFrames = 0;
                session.FrameReceived += delegate(FrameEvent fe) { _raceFrames++; };

                session.Open(TestSettings());
                Thread.Sleep(50);
                session.Close();
                session.Open(TestSettings());

                // 一次投喂整行。两条循环同时在读同一个端口时，各自会叼走一部分字节，
                // 这一行就凑不齐，或者被凑成两次事件。
                fake.FeedText("[UART_TEST] received 12 bytes: 42 01 00 01 06 00 00 00 00 00 00 4A\r\n");
                WaitFor(delegate { return _raceFrames >= 1; }, 2000);
                Thread.Sleep(200);          // 给可能存在的第二条循环留出也报一次的机会

                CheckEq(_raceFrames, 1, "重开后一帧只被解析一次（字节没有被劈成两半）");

                session.Close();
            }

            await Task.FromResult(0);
        }

        /// <summary>Close() 返回之后，传输层不该再被读——僵尸循环会一直读，而且端口关了是立刻返回的。</summary>
        private static void RaceClosedTransportIsNotEmptySpin()
        {
            var fake = new FakeSerialTransport();
            using (var session = new SerialSession(fake))
            {
                session.Open(TestSettings());
                Thread.Sleep(50);
                session.Close();

                int afterClose = fake.ReadCalls;
                Thread.Sleep(300);

                CheckEq(fake.ReadCalls, afterClose,
                    "Close() 返回后不再有任何读调用（遗留循环会持续调 Read → 满核空转）");
            }
        }

        /// <summary>传输层报「已关闭」时应上报故障并退出，而不是当作空闲继续重试。</summary>
        private static void RaceTransportClosedReportsFault()
        {
            var fake = new FakeSerialTransport();
            using (var session = new SerialSession(fake))
            {
                _raceFault = null;
                session.Fault += delegate(SerialFault f) { _raceFault = f; };

                session.Open(TestSettings());
                Thread.Sleep(50);

                fake.Close();               // 绕过会话直接关端口：模拟设备侧消失

                Check(WaitFor(delegate { return _raceFault != null; }, 2000),
                    "传输层报告已关闭时上报故障（而不是当空闲继续重试）");
                Check(_raceFault != null && _raceFault.Kind == SerialFaultKind.DeviceRemoved,
                    "故障类型是 DeviceRemoved");
                Check(!session.IsOpen, "上报故障后会话标记为已关闭");

                int after = fake.ReadCalls;
                Thread.Sleep(200);
                CheckEq(fake.ReadCalls, after, "上报故障后读循环已退出，不再调用 Read");
            }
        }

        /// <summary>读故障之后 Close() 不该抛（旧循环那时已经自己退出了），并且还能重开。</summary>
        private static void RaceCloseAfterReadFaultIsHarmless()
        {
            var fake = new FakeSerialTransport();
            using (var session = new SerialSession(fake))
            {
                session.Open(TestSettings());
                Thread.Sleep(50);

                fake.ThrowOnRead = true;
                Check(WaitFor(delegate { return !session.IsOpen; }, 2000), "读异常后会话被标记为已关闭");
                fake.ThrowOnRead = false;

                CheckEq(ThrownBy(delegate { session.Close(); }), "<no exception>",
                    "读故障后 Close() 不抛（旧循环已经退出，没有可等待的僵尸）");

                session.Open(TestSettings());
                Check(session.IsOpen, "读故障后仍能重新打开");
                session.Close();
            }
        }
    }
}
