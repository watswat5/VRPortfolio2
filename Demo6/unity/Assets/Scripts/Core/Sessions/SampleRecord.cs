using System;
using System.Buffers.Binary;
using System.Numerics;

namespace WifiHeatmap.Core
{
    /// <summary>Quality flags stored with each sample. Flagged samples are kept but can be excluded from maps.</summary>
    [Flags]
    public enum SampleFlags : byte
    {
        None = 0,
        TrackingDegraded = 1 << 0,
        PoseExtrapolated = 1 << 1,
        ClockUncertain = 1 << 2,
        AnchorNotLocalized = 1 << 3,
    }

    /// <summary>
    /// One recorded measurement, 48 bytes little-endian on disk: where the antenna was (relative to
    /// an anchor from the session's anchor table), when, and what the probe measured there.
    /// </summary>
    public struct SampleRecord
    {
        public const int Size = 48;

        /// <summary>Probe sequence number; detects dropped or duplicated reports.</summary>
        public uint Sequence;
        /// <summary>Measurement midpoint on the headset monotonic clock, µs.</summary>
        public long TimeUs;
        /// <summary>Antenna position relative to the anchor, metres (Unity axes).</summary>
        public Vector3 Position;
        /// <summary>Antenna orientation relative to the anchor (Unity axes).</summary>
        public Quaternion Rotation;
        /// <summary>Window mean RSSI, hundredths of a dBm.</summary>
        public short RssiCentiDbm;
        /// <summary>In-window standard deviation, hundredths of a dB.</summary>
        public ushort StdDevCentiDb;
        /// <summary>Wi-Fi frames in the window (saturates at 65535).</summary>
        public ushort Frames;
        /// <summary>Row of the session header's anchor table.</summary>
        public byte AnchorIndex;
        public SampleFlags Flags;

        public double RssiDbm => RssiCentiDbm / 100.0;
        public double StdDevDb => StdDevCentiDb / 100.0;

        public static SampleRecord Create(ulong sequence, long timeUs, Vector3 position, Quaternion rotation,
            double meanDbm, double stdDevDb, ulong frames, int anchorIndex, SampleFlags flags)
        {
            if (anchorIndex < 0 || anchorIndex > byte.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(anchorIndex));
            }
            return new SampleRecord
            {
                Sequence = (uint)sequence,
                TimeUs = timeUs,
                Position = position,
                Rotation = rotation,
                RssiCentiDbm = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(meanDbm * 100))),
                StdDevCentiDb = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, Math.Round(stdDevDb * 100))),
                Frames = (ushort)Math.Min(ushort.MaxValue, frames),
                AnchorIndex = (byte)anchorIndex,
                Flags = flags,
            };
        }

        public void Write(Span<byte> destination)
        {
            if (destination.Length < Size)
            {
                throw new ArgumentException("Destination is smaller than a record.", nameof(destination));
            }
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(4, 8), TimeUs);
            WriteSingle(destination, 12, Position.X);
            WriteSingle(destination, 16, Position.Y);
            WriteSingle(destination, 20, Position.Z);
            WriteSingle(destination, 24, Rotation.X);
            WriteSingle(destination, 28, Rotation.Y);
            WriteSingle(destination, 32, Rotation.Z);
            WriteSingle(destination, 36, Rotation.W);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(40, 2), RssiCentiDbm);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(42, 2), StdDevCentiDb);
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(44, 2), Frames);
            destination[46] = AnchorIndex;
            destination[47] = (byte)Flags;
        }

        public static SampleRecord Read(ReadOnlySpan<byte> source)
        {
            if (source.Length < Size)
            {
                throw new ArgumentException("Source is smaller than a record.", nameof(source));
            }
            return new SampleRecord
            {
                Sequence = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(0, 4)),
                TimeUs = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(4, 8)),
                Position = new Vector3(ReadSingle(source, 12), ReadSingle(source, 16), ReadSingle(source, 20)),
                Rotation = new Quaternion(ReadSingle(source, 24), ReadSingle(source, 28), ReadSingle(source, 32), ReadSingle(source, 36)),
                RssiCentiDbm = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(40, 2)),
                StdDevCentiDb = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(42, 2)),
                Frames = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(44, 2)),
                AnchorIndex = source[46],
                Flags = (SampleFlags)source[47],
            };
        }

        private static void WriteSingle(Span<byte> destination, int offset, float value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), BitConverter.SingleToInt32Bits(value));
        }

        private static float ReadSingle(ReadOnlySpan<byte> source, int offset)
        {
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, 4)));
        }
    }

    /// <summary>CRC-32 (IEEE 802.3 polynomial, as used by zip and PNG).</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return ~crc;
        }

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[i] = c;
            }
            return table;
        }
    }
}
