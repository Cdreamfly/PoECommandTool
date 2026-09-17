using System;
using System.Collections.Generic;

namespace PoECommandTool.Serial
{
    /// <summary>轮询计划（界面上的勾选、间隔、超时、重试）。</summary>
    public sealed class PollingPlan
    {
        public PollingPlan()
        {
            Items = new List<PollItem>();
            IntervalMs = 1000;
            ResponseTimeoutMs = 1500;
            MaxRetries = 2;
            InterCommandDelayMs = 500;
        }

        /// <summary>按轮询顺序排列的命令项。</summary>
        public List<PollItem> Items { get; private set; }

        /// <summary>轮次之间的间隔（从本轮真正开始计时）。</summary>
        public int IntervalMs { get; set; }

        /// <summary>单条命令等待回包的时长。设备控制台要先转发再等回包，给得宽一点。</summary>
        public int ResponseTimeoutMs { get; set; }

        /// <summary>超时后重发几次（总尝试次数 = MaxRetries + 1）。</summary>
        public int MaxRetries { get; set; }

        /// <summary>同一轮内两条命令之间的最小间隔——调试控制台需要一点喘息时间。</summary>
        public int InterCommandDelayMs { get; set; }
    }

    public enum PollStepKind
    {
        Send = 0,
        Wait = 1,
        Finished = 2,
    }

    /// <summary>一次要发出去的请求。</summary>
    public sealed class PollRequest
    {
        public PollItem Item { get; set; }
        public byte[] Frame { get; set; }
        public byte Sequence { get; set; }

        /// <summary>这是本条命令的第几次尝试（1 起）。</summary>
        public int Attempt { get; set; }

        public byte CommandId
        {
            get { return Frame == null || Frame.Length == 0 ? (byte)0 : Frame[0]; }
        }
    }

    /// <summary>调度器给出的下一步动作。</summary>
    public sealed class PollStep
    {
        public PollStepKind Kind { get; set; }
        public PollRequest Request { get; set; }
        public DateTime DueAt { get; set; }
    }

    /// <summary>
    /// 轮询调度器：决定「什么时候发哪一帧、超时怎么重试」。
    ///
    /// 纯状态机——没有定时器、不做 IO、不 sleep，时间从外部注入的时钟读，所有决策同步返回。
    /// 这是它能被完整测试的原因：测试里用假时钟就能精确驱动每一轮。
    /// </summary>
    public sealed class PollingScheduler
    {
        private readonly PollingPlan _plan;
        private readonly List<PollItem> _items;
        private readonly Func<DateTime> _clock;

        private int _index;
        private DateTime _notBefore = DateTime.MinValue;
        private DateTime _roundStart;
        private bool _roundStarted;
        private bool _awaiting;
        private int _attempts;
        private byte _sequence;

        public PollingScheduler(PollingPlan plan, Func<DateTime> clock)
        {
            if (plan == null) throw new ArgumentNullException("plan");

            _plan = plan;
            _clock = clock ?? (() => DateTime.UtcNow);

            // 计划在构造时快照：轮询开始后再改界面上的勾选不影响本轮
            _items = new List<PollItem>();
            for (int i = 0; i < plan.Items.Count; i++)
                if (plan.Items[i] != null && plan.Items[i].Enabled)
                    _items.Add(plan.Items[i]);
        }

        /// <summary>已经跑完的轮次。</summary>
        public int RoundsCompleted { get; private set; }

        /// <summary>当前是否有一条请求在途、还没结算。</summary>
        public bool IsAwaiting
        {
            get { return _awaiting; }
        }

        /// <summary>序列号自增（1..0xFF，跳过 0）。</summary>
        public static byte NextSequence(byte current)
        {
            return current >= 0xFF ? (byte)0x01 : (byte)(current + 1);
        }

        /// <summary>
        /// 取下一步。
        /// 前提：当前没有在途请求。返回 <see cref="PollStepKind.Send"/> 之后就进入在途状态，
        /// 必须由 <see cref="OnResponse"/> 或 <see cref="OnAttemptFailed"/> 之一结算。
        /// </summary>
        public PollStep Next()
        {
            if (_awaiting)
                throw new InvalidOperationException("上一条请求还没结算，不能发下一条。");

            if (_items.Count == 0)
                return new PollStep { Kind = PollStepKind.Finished };

            DateTime now = _clock();
            if (now < _notBefore)
                return new PollStep { Kind = PollStepKind.Wait, DueAt = _notBefore };

            // 一条请求成功时，下一条与下一条之间的间隔也走 _notBefore（在结算里算好）；
            // 这里只负责「到点了就发」。
            if (_index == 0)
            {
                // 新一轮以真正开始发第一条的时刻为起点，轮次间隔就从这里算
                _roundStart = now;
                _roundStarted = true;
            }

            PollItem item = _items[_index];
            _sequence = NextSequence(_sequence);
            _attempts++;

            _awaiting = true;
            return new PollStep
            {
                Kind = PollStepKind.Send,
                Request = new PollRequest
                {
                    Item = item,
                    Frame = PollPlan.BuildFrame(item, _sequence),
                    Sequence = _sequence,
                    Attempt = _attempts,
                },
            };
        }

        /// <summary>收到匹配的响应：当前这条完成，轮到下一条；一轮结束则等轮次间隔。</summary>
        public void OnResponse(DateTime now)
        {
            if (!_awaiting)
                return;

            CompleteCurrent(now);
        }

        /// <summary>
        /// 本次尝试失败（等待超时，或发送就没成功）：没到重试上限就用**新的序列号**重发同一条，
        /// 用尽则跳过它继续下一条。
        /// 重发必须换序列号——否则迟到的旧回包会被当成本次响应，数据就张冠李戴了。
        /// </summary>
        /// <returns>需要提示给用户的信息；没有则返回 null。</returns>
        public string OnAttemptFailed(DateTime now)
        {
            if (!_awaiting)
                return null;

            string notice = null;
            if (_attempts > _plan.MaxRetries)
            {
                notice = string.Format("{0} 连发 {1} 次没有响应，本轮跳过。",
                    _items[_index].DisplayName, _attempts);
                _awaiting = false;
                _attempts = 0;
                AdvanceIndex(now);      // 放弃这条，直接轮到下一条
                return notice;
            }

            _awaiting = false;
            _notBefore = now.AddMilliseconds(_plan.InterCommandDelayMs);
            return null;
        }

        private void CompleteCurrent(DateTime now)
        {
            _awaiting = false;
            _attempts = 0;
            AdvanceIndex(now);
        }

        private void AdvanceIndex(DateTime now)
        {
            _index++;
            if (_index >= _items.Count)
            {
                // 一轮结束：轮次间隔从「本轮真正开始」算起。
                // 如果这一轮本身就跑得比间隔还久，下一轮会立即开始（不补偿、不堆积）。
                _index = 0;
                RoundsCompleted++;
                _notBefore = _roundStarted
                    ? _roundStart.AddMilliseconds(_plan.IntervalMs)
                    : now.AddMilliseconds(_plan.IntervalMs);
                return;
            }

            _notBefore = now.AddMilliseconds(_plan.InterCommandDelayMs);
        }
    }
}
