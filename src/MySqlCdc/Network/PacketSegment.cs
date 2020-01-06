using System;
using System.Buffers;

namespace MySqlCdc.Network
{
    public class PacketSegment : ReadOnlySequenceSegment<byte>
    {
        public PacketSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public PacketSegment Add(ReadOnlyMemory<byte> memory)
        {
            var segment = new PacketSegment(memory);
            segment.RunningIndex = RunningIndex + this.Memory.Length;
            Next = segment;
            return segment;
        }
    }
}