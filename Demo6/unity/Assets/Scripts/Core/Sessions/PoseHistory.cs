using System;
using System.Numerics;

namespace WifiHeatmap.Core
{
    /// <summary>
    /// Recent antenna poses keyed by headset monotonic time, for looking up where the antenna was
    /// when a probe window was measured (which is before its report arrives). Interpolates between
    /// bracketing poses; extrapolates past the newest pose only up to a caller-given limit.
    /// </summary>
    public sealed class PoseHistory
    {
        public readonly struct Entry
        {
            public Entry(long timeUs, Vector3 position, Quaternion rotation, bool tracked)
            {
                TimeUs = timeUs;
                Position = position;
                Rotation = rotation;
                Tracked = tracked;
            }

            public long TimeUs { get; }
            public Vector3 Position { get; }
            public Quaternion Rotation { get; }
            public bool Tracked { get; }
        }

        public readonly struct Lookup
        {
            public Lookup(Vector3 position, Quaternion rotation, bool extrapolated, bool degraded)
            {
                Position = position;
                Rotation = rotation;
                Extrapolated = extrapolated;
                Degraded = degraded;
            }

            public Vector3 Position { get; }
            public Quaternion Rotation { get; }
            public bool Extrapolated { get; }
            /// <summary>Tracking was lost at a bracketing pose.</summary>
            public bool Degraded { get; }
        }

        private readonly Entry[] entries;
        private int start;
        private int count;

        public PoseHistory(int capacity = 512)
        {
            if (capacity < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            entries = new Entry[capacity];
        }

        public int Count => count;
        public long OldestUs => count == 0 ? long.MaxValue : At(0).TimeUs;
        public long NewestUs => count == 0 ? long.MinValue : At(count - 1).TimeUs;

        /// <summary>Adds a pose; times must increase (equal or older times are ignored).</summary>
        public void Add(long timeUs, Vector3 position, Quaternion rotation, bool tracked)
        {
            if (count > 0 && timeUs <= NewestUs)
            {
                return;
            }
            Entry entry = new Entry(timeUs, position, Quaternion.Normalize(rotation), tracked);
            if (count < entries.Length)
            {
                entries[(start + count) % entries.Length] = entry;
                count++;
            }
            else
            {
                entries[start] = entry;
                start = (start + 1) % entries.Length;
            }
        }

        public void Clear()
        {
            start = 0;
            count = 0;
        }

        /// <summary>
        /// Pose at <paramref name="timeUs"/>. False if the time is older than the history or newer than
        /// the newest pose by more than <paramref name="maxExtrapolationUs"/>.
        /// </summary>
        public bool TryGet(long timeUs, long maxExtrapolationUs, out Lookup lookup)
        {
            lookup = default;
            if (count == 0 || timeUs < OldestUs)
            {
                return false;
            }

            Entry newest = At(count - 1);
            if (timeUs >= newest.TimeUs)
            {
                long ahead = timeUs - newest.TimeUs;
                if (ahead > maxExtrapolationUs)
                {
                    return false;
                }
                if (ahead == 0 || count < 2)
                {
                    lookup = new Lookup(newest.Position, newest.Rotation, ahead > 0, !newest.Tracked);
                    return true;
                }
                // Linear in position only; rotation holds (a few tens of ms at most).
                Entry previous = At(count - 2);
                float span = newest.TimeUs - previous.TimeUs;
                Vector3 velocity = (newest.Position - previous.Position) / span;
                lookup = new Lookup(newest.Position + velocity * ahead, newest.Rotation, true,
                    !newest.Tracked || !previous.Tracked);
                return true;
            }

            // Binary search for the last entry at or before timeUs.
            int low = 0, high = count - 1;
            while (high - low > 1)
            {
                int mid = (low + high) / 2;
                if (At(mid).TimeUs <= timeUs)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }
            Entry a = At(low), b = At(high);
            float t = a.TimeUs == b.TimeUs ? 0f : (float)(timeUs - a.TimeUs) / (b.TimeUs - a.TimeUs);
            lookup = new Lookup(Vector3.Lerp(a.Position, b.Position, t), Quaternion.Slerp(a.Rotation, b.Rotation, t), false,
                !a.Tracked || !b.Tracked);
            return true;
        }

        private Entry At(int index)
        {
            return entries[(start + index) % entries.Length];
        }
    }

    /// <summary>The three signal bands used for colouring, with user-adjustable thresholds (dBm).</summary>
    public sealed class SignalScale
    {
        public enum Band
        {
            Weak,
            Moderate,
            Strong,
        }

        public SignalScale(double weakBelowDbm = -70, double strongFromDbm = -60)
        {
            Set(weakBelowDbm, strongFromDbm);
        }

        /// <summary>Readings below this are weak.</summary>
        public double WeakBelowDbm { get; private set; }
        /// <summary>Readings at or above this are strong.</summary>
        public double StrongFromDbm { get; private set; }

        public void Set(double weakBelowDbm, double strongFromDbm)
        {
            if (strongFromDbm - weakBelowDbm < 1)
            {
                throw new ArgumentException("The strong threshold must be at least 1 dB above the weak threshold.");
            }
            WeakBelowDbm = weakBelowDbm;
            StrongFromDbm = strongFromDbm;
        }

        public Band Classify(double dbm)
        {
            return dbm >= StrongFromDbm ? Band.Strong : dbm < WeakBelowDbm ? Band.Weak : Band.Moderate;
        }
    }
}
