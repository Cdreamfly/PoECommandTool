using System;
using System.Collections.Generic;

namespace WpfApp1.Serial
{
    /// <summary>
    /// 裸帧模式下的帧同步器：从字节流里找出校验和正确的 RTL8239 响应帧。
    ///
    /// 串口直连 PoE 控制器时，设备回的就是二进制帧——没有换行符、也没有 received 之类的提示，
    /// 所以不能按「行」解析，只能在字节流里同步：命令 ID 合法 + 校验和正确才算一帧。
    /// 帧可能被拆在两次读之间，所以要把末尾不足一帧的字节留到下次。
    /// </summary>
    public sealed class RawFrameScanner
    {
        public const int FrameLength = 12;

        private readonly List<byte> _buffer = new List<byte>(FrameLength * 2);

        public RawFrameScanner()
        {
            MaxBufferLength = 4096;
        }

        /// <summary>缓冲上限；超过说明一直同步不上，整段丢弃防止无限增长。</summary>
        public int MaxBufferLength { get; set; }

        /// <summary>被丢弃的字节数（同步失败时的诊断依据）。</summary>
        public int DroppedByteCount { get; private set; }

        /// <summary>追加一段字节，返回这次同步出来的完整帧。</summary>
        public IList<byte[]> Append(byte[] data, int count)
        {
            var frames = new List<byte[]>();
            if (data == null || count <= 0)
                return frames;
            if (count > data.Length)
                count = data.Length;

            for (int i = 0; i < count; i++)
                _buffer.Add(data[i]);

            int consumed = 0;
            while (_buffer.Count - consumed >= FrameLength)
            {
                int start = -1;
                for (int i = consumed; i + FrameLength <= _buffer.Count; i++)
                {
                    if (IsCandidateFrame(_buffer, i))
                    {
                        start = i;
                        break;
                    }
                }

                if (start < 0)
                {
                    // 没找到完整帧：保留末尾不足一帧的字节（帧可能跨读），前面的丢掉
                    int keep = FrameLength - 1;
                    consumed = Math.Max(consumed, _buffer.Count - keep);
                    break;
                }

                var frame = new byte[FrameLength];
                for (int k = 0; k < FrameLength; k++)
                    frame[k] = _buffer[start + k];
                frames.Add(frame);

                if (start > consumed)
                    DroppedByteCount += start - consumed;
                consumed = start + FrameLength;
            }

            if (consumed > 0)
                _buffer.RemoveRange(0, consumed);

            if (_buffer.Count > MaxBufferLength)
            {
                DroppedByteCount += _buffer.Count;
                _buffer.Clear();
            }

            return frames;
        }

        public void Reset()
        {
            _buffer.Clear();
        }

        /// <summary>该字节是否可能是 RTL8239 的响应命令 ID（用来降低随机数据误同步的概率）。</summary>
        public static bool IsKnownResponseId(byte id)
        {
            if (id >= 0x40 && id <= 0x50)
                return true;
            return id == 0xC0 || id == 0xCA || id == 0xF1;
        }

        private static bool IsCandidateFrame(List<byte> buffer, int start)
        {
            if (!IsKnownResponseId(buffer[start]))
                return false;

            int sum = 0;
            for (int i = 0; i < FrameLength - 1; i++)
                sum += buffer[start + i];

            return buffer[start + FrameLength - 1] == (byte)(sum & 0xFF);
        }
    }
}
