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
    /// 一条曲线的采样缓冲：预分配环形数组，写入零分配，容量与时间窗双重限制，
    /// 长时间轮询也不会越跑越占内存。
    /// </summary>
    public sealed class SeriesBuffer
    {
        public const int DefaultCapacity = 20000;

        private readonly SeriesSample[] _ring;
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
            _ring = new SeriesSample[capacity];
        }

        public string Key { get; private set; }
        public string Name { get; set; }
        public string Unit { get; private set; }
        public string ColorHex { get; set; }

        /// <summary>图例里勾选的状态。</summary>
        public bool Visible { get; set; }

        public int Capacity
        {
            get { return _ring.Length; }
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

            // 满了：覆盖最旧的一个
            _ring[_start] = new SeriesSample(time, value);
            _start = (_start + 1) % _ring.Length;
            return true;
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
