using System;
using System.Collections.Generic;

namespace WpfApp1.Chart
{
    /// <summary>一个采样点。</summary>
    public struct SeriesSample
    {
        public DateTime Time;
        public double Value;

        public SeriesSample(DateTime time, double value)
        {
            Time = time;
            Value = value;
        }
    }

    /// <summary>
    /// 一条曲线的采样缓冲：环形数组，写入零分配，容量与时间窗双重限制，
    /// 长时间轮询也不会越跑越占内存。
    /// </summary>
    public sealed class SeriesBuffer
    {
        public const int DefaultCapacity = 20000;

        /// <summary>
        /// 一开始就分配的条数，之后按需翻倍长到 <see cref="DefaultCapacity"/>。
        ///
        /// 原先是一次性分配满 20000 条——每条 16 字节，**一条曲线 320KB**。48 端口 ×
        /// 4 个参数就是 61MB 压在**大对象堆**上，而且是在一个采样点都还没有的时候就占住了。
        /// .NET Framework 默认不压缩 LOH，「清空曲线 → 重新轮询」来回几次就会把它撕碎。
        /// </summary>
        public const int InitialCapacity = 1024;

        private SeriesSample[] _ring;
        private readonly int _maxCapacity;
        private int _start;
        private int _count;

        public SeriesBuffer(string key, string name, string unit)
            : this(key, name, unit, DefaultCapacity)
        {
        }

        public SeriesBuffer(string key, string name, string unit, int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity");

            Key = key;
            Name = name;
            Unit = unit;
            ColorHex = "#1A73E8";
            Visible = true;

            _maxCapacity = capacity;
            _ring = new SeriesSample[capacity < InitialCapacity ? capacity : InitialCapacity];
        }

        public string Key { get; private set; }
        public string Name { get; set; }
        public string Unit { get; private set; }
        public string ColorHex { get; set; }

        /// <summary>图例里勾选的状态。</summary>
        public bool Visible { get; set; }

        /// <summary>这条曲线最多能存多少个采样（不是当前数组的长度——数组是按需长上来的）。</summary>
        public int Capacity
        {
            get { return _maxCapacity; }
        }

        public int Count
        {
            get { return _count; }
        }

        /// <summary>
        /// 追加一个采样。非有限值会被拒绝（返回 false）。
        ///
        /// 时间比上一个采样还早时（夏令时回拨、NTP 校时回退）**夹紧到上一个采样点**而不是丢弃：
        /// 丢弃会让曲线在回拨期间静默冻住，夹紧顶多在图上出现一段竖线。
        /// </summary>
        public bool Add(DateTime time, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return false;

            if (_count > 0)
            {
                SeriesSample last = this[_count - 1];
                if (time < last.Time)
                    time = last.Time;
            }

            if (_count < _ring.Length)
            {
                _ring[(_start + _count) % _ring.Length] = new SeriesSample(time, value);
                _count++;
                return true;
            }

            // 装满了：还没到配置的上限就先长一倍，到了上限才覆盖最旧的
            if (_ring.Length < _maxCapacity)
            {
                Grow();
                _ring[(_start + _count) % _ring.Length] = new SeriesSample(time, value);
                _count++;
                return true;
            }

            _ring[_start] = new SeriesSample(time, value);
            _start = (_start + 1) % _ring.Length;
            return true;
        }

        /// <summary>容量翻倍，按时间顺序把已有采样搬到新数组的开头。</summary>
        private void Grow()
        {
            int grown = _ring.Length * 2;
            if (grown > _maxCapacity)
                grown = _maxCapacity;

            var next = new SeriesSample[grown];
            for (int i = 0; i < _count; i++)
                next[i] = _ring[(_start + i) % _ring.Length];

            _ring = next;
            _start = 0;
        }

        /// <summary>第 index 个样本（0 = 最旧）。</summary>
        public SeriesSample this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    throw new ArgumentOutOfRangeException("index");
                return _ring[(_start + index) % _ring.Length];
            }
        }

        public bool TryGetLast(out SeriesSample sample)
        {
            if (_count == 0)
            {
                sample = new SeriesSample();
                return false;
            }
            sample = this[_count - 1];
            return true;
        }

        /// <summary>把 [from, to] 窗口内的样本按时间顺序拷进 destination（会先清空它）。</summary>
        public void CopyRange(DateTime from, DateTime to, List<SeriesSample> destination)
        {
            if (destination == null) throw new ArgumentNullException("destination");

            destination.Clear();
            for (int i = 0; i < _count; i++)
            {
                SeriesSample sample = this[i];
                if (sample.Time < from) continue;
                if (sample.Time > to) break;        // 按时间有序，后面只会更晚
                destination.Add(sample);
            }
        }

        /// <summary>丢弃早于 cutoff 的样本，返回丢弃条数。</summary>
        public int Trim(DateTime cutoff)
        {
            int dropped = 0;
            while (_count > 0 && this[0].Time < cutoff)
            {
                _start = (_start + 1) % _ring.Length;
                _count--;
                dropped++;
            }
            return dropped;
        }

        public void Clear()
        {
            _start = 0;
            _count = 0;
        }
    }
}
