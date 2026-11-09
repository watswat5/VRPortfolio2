using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using NUnit.Framework;

namespace WifiHeatmap.Core.Tests
{
    /// <summary>The session file format: records, blocks, header, crash recovery and the session store.</summary>
    public sealed class SessionFileTests
    {
        private string root;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "wifiheatmap-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private static SessionHeader NewHeader(string id = "20261029T141502Z-7f3a")
        {
            SessionHeader header = new SessionHeader { SessionId = id, StartedUtc = new DateTime(2026, 10, 29, 14, 15, 2, DateTimeKind.Utc) };
            header.AccessPoint.Bssid = "AA:BB:CC:DD:EE:FF";
            header.AccessPoint.Channel = 36;
            header.AccessPoint.Ssid = "Home \"WiFi\"";
            header.AddAnchor(SessionHeader.AnchorEntry.MrukRoom, Guid.Parse("11111111-2222-3333-4444-555555555555"), "Living room");
            header.AddAnchor(SessionHeader.AnchorEntry.SpatialAnchor, Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"), "Hallway");
            return header;
        }

        private static SampleRecord Record(int i)
        {
            return SampleRecord.Create((ulong)i, 1_000_000L + i * 100_000L, new Vector3(i * 0.01f, 1.2f, -i * 0.02f),
                Quaternion.CreateFromYawPitchRoll(i * 0.01f, 0.1f, 0), -60.0 - i % 20 * 0.37, 1.5, (ulong)(10 + i), i % 2,
                i % 7 == 0 ? SampleFlags.ClockUncertain : SampleFlags.None);
        }

        private static void AssertSame(SampleRecord expected, SampleRecord actual)
        {
            Assert.AreEqual(expected.Sequence, actual.Sequence);
            Assert.AreEqual(expected.TimeUs, actual.TimeUs);
            Assert.AreEqual(expected.Position, actual.Position);
            Assert.AreEqual(expected.Rotation, actual.Rotation);
            Assert.AreEqual(expected.RssiCentiDbm, actual.RssiCentiDbm);
            Assert.AreEqual(expected.StdDevCentiDb, actual.StdDevCentiDb);
            Assert.AreEqual(expected.Frames, actual.Frames);
            Assert.AreEqual(expected.AnchorIndex, actual.AnchorIndex);
            Assert.AreEqual(expected.Flags, actual.Flags);
        }

        private List<SampleRecord> WriteSession(SessionHeader header, int records, int flushEvery, bool complete)
        {
            List<SampleRecord> written = new List<SampleRecord>();
            SessionFileWriter writer = SessionFileWriter.Create(root, header);
            for (int i = 0; i < records; i++)
            {
                SampleRecord r = Record(i);
                writer.Append(r);
                written.Add(r);
                if (flushEvery > 0 && (i + 1) % flushEvery == 0)
                {
                    writer.Flush(toDisk: i % 2 == 0);
                }
            }
            writer.AppendClockExchange(new PingExchange(10, 30, 5, 6, 42));
            if (complete)
            {
                writer.Complete(new DateTime(2026, 10, 29, 15, 0, 0, DateTimeKind.Utc));
            }
            else
            {
                writer.Dispose();   // like a crash: data flushed, header never finalised
            }
            return written;
        }

        [Test]
        public void RecordIsFortyEightBytesAndRoundTrips()
        {
            SampleRecord r = SampleRecord.Create(uint.MaxValue, long.MinValue + 1, new Vector3(-1e6f, float.Epsilon, 3.5f),
                new Quaternion(0.1f, -0.2f, 0.3f, 0.9f), -127.99, 655.35, 70_000, 255,
                SampleFlags.TrackingDegraded | SampleFlags.AnchorNotLocalized);
            byte[] bytes = new byte[SampleRecord.Size];
            r.Write(bytes);
            AssertSame(r, SampleRecord.Read(bytes));
            Assert.AreEqual(48, SampleRecord.Size);
            Assert.AreEqual(ushort.MaxValue, r.Frames, "frames saturate");
            Assert.AreEqual(-12799, r.RssiCentiDbm);
        }

        [Test]
        public void WriterAndReaderRoundTripAcrossBlocks()
        {
            SessionHeader header = NewHeader();
            List<SampleRecord> written = WriteSession(header, 150, flushEvery: 25, complete: true);

            SessionData data = SessionStore.Read(Path.Combine(root, header.SessionId));
            Assert.AreEqual(150, data.Records.Count);
            Assert.AreEqual(0, data.DroppedBytes);
            for (int i = 0; i < written.Count; i++)
            {
                AssertSame(written[i], data.Records[i]);
            }
            Assert.IsTrue(data.Header.Records.Complete);
            Assert.AreEqual(150, data.Header.Records.Count);
            Assert.AreEqual(new DateTime(2026, 10, 29, 15, 0, 0, DateTimeKind.Utc), data.Header.EndedUtc);
            Assert.AreEqual(1, File.ReadAllLines(Path.Combine(data.Folder, SessionFiles.ClockFileName)).Length);
        }

        [Test]
        public void HeaderJsonRoundTrips()
        {
            SessionHeader header = NewHeader();
            SessionHeader copy = SessionHeader.FromJson(header.ToJson());
            Assert.AreEqual(header.SessionId, copy.SessionId);
            Assert.AreEqual("Home \"WiFi\"", copy.AccessPoint.Ssid);
            Assert.AreEqual(2, copy.Anchors.Count);
            Assert.AreEqual(header.Anchors[1].Uuid, copy.Anchors[1].Uuid);
            Assert.AreEqual(SessionHeader.AnchorEntry.SpatialAnchor, copy.Anchors[1].Kind);
            StringAssert.Contains("\"sessionId\"", header.ToJson(), "camelCase keys");
        }

        [Test]
        public void AddingAnExistingAnchorReturnsItsIndex()
        {
            SessionHeader header = NewHeader();
            Assert.AreEqual(1, header.AddAnchor(SessionHeader.AnchorEntry.SpatialAnchor, header.Anchors[1].Uuid, "again"));
            Assert.AreEqual(2, header.Anchors.Count);
        }

        [Test]
        public void RefusesAnchorIndexOutsideTheTable()
        {
            SessionFileWriter writer = SessionFileWriter.Create(root, NewHeader());
            SampleRecord bad = Record(0);
            bad.AnchorIndex = 2;
            Assert.Throws<ArgumentOutOfRangeException>(() => writer.Append(bad));
            writer.Dispose();
        }

        [Test]
        public void AnchorsAddedMidSessionArePersisted()
        {
            SessionHeader header = NewHeader();
            SessionFileWriter writer = SessionFileWriter.Create(root, header);
            int index = writer.AddAnchor(SessionHeader.AnchorEntry.MrukRoom, Guid.NewGuid(), "Kitchen");
            SampleRecord r = Record(0);
            r.AnchorIndex = (byte)index;
            writer.Append(r);
            writer.Dispose();   // crash before Complete: the new row must already be on disk

            SessionData data = SessionStore.Read(Path.Combine(root, header.SessionId));
            Assert.AreEqual(3, data.Header.Anchors.Count);
            Assert.AreEqual(1, data.Records.Count);
            Assert.AreEqual(2, data.Records[0].AnchorIndex);
        }

        [Test]
        public void TornTailIsDroppedAndRecovered()
        {
            SessionHeader header = NewHeader();
            List<SampleRecord> written = WriteSession(header, 100, flushEvery: 30, complete: false);
            string folder = Path.Combine(root, header.SessionId);
            string samples = Path.Combine(folder, SessionFiles.SamplesFileName);

            // Simulate a crash in the middle of writing the next block.
            long intact = new FileInfo(samples).Length;
            using (FileStream stream = new FileStream(samples, FileMode.Append))
            {
                stream.Write(new byte[] { 12, 0, 0, 0, 1, 2, 3 }, 0, 7);
            }

            SessionData before = SessionStore.Read(folder);
            Assert.AreEqual(100, before.Records.Count);
            Assert.AreEqual(7, before.DroppedBytes);
            Assert.IsFalse(before.Header.Records.Complete);

            List<string> recovered = SessionStore.RecoverIncomplete(root);
            CollectionAssert.AreEqual(new[] { folder }, recovered);
            Assert.AreEqual(intact, new FileInfo(samples).Length, "torn bytes truncated");

            SessionData after = SessionStore.Read(folder);
            Assert.AreEqual(0, after.DroppedBytes);
            Assert.AreEqual(100, after.Records.Count);
            Assert.IsTrue(after.Header.Records.Complete);
            Assert.IsTrue(after.Header.Records.Recovered);
            Assert.AreEqual(100, after.Header.Records.Count);
            AssertSame(written[99], after.Records[99]);
            Assert.IsEmpty(SessionStore.RecoverIncomplete(root), "second pass finds nothing to do");
        }

        [Test]
        public void CorruptBlockStopsReadingThere()
        {
            SessionHeader header = NewHeader();
            WriteSession(header, 90, flushEvery: 30, complete: true);   // three blocks of 30
            string samples = Path.Combine(root, header.SessionId, SessionFiles.SamplesFileName);

            byte[] bytes = File.ReadAllBytes(samples);
            int secondBlock = SessionFiles.FileHeaderSize + 4 + 30 * SampleRecord.Size + 4;
            bytes[secondBlock + 4 + 10] ^= 0x5A;   // flip a bit inside block 2's first record
            File.WriteAllBytes(samples, bytes);

            SessionData data = SessionStore.Read(Path.Combine(root, header.SessionId));
            Assert.AreEqual(30, data.Records.Count, "only block 1 is trusted");
            Assert.AreEqual(bytes.Length - secondBlock, data.DroppedBytes);
        }

        [Test]
        public void RefusesUnsupportedVersions()
        {
            SessionHeader header = NewHeader();
            WriteSession(header, 5, flushEvery: 0, complete: true);
            string folder = Path.Combine(root, header.SessionId);
            string samples = Path.Combine(folder, SessionFiles.SamplesFileName);

            byte[] bytes = File.ReadAllBytes(samples);
            bytes[4] = 99;   // file format version
            File.WriteAllBytes(samples, bytes);
            Assert.Throws<InvalidDataException>(() => SessionStore.Read(folder));

            string json = File.ReadAllText(Path.Combine(folder, SessionFiles.HeaderFileName)).Replace("\"formatVersion\": 1", "\"formatVersion\": 7");
            Assert.Throws<FormatException>(() => SessionHeader.FromJson(json));
        }

        [Test]
        public void RefusesSamplesFromAnotherSession()
        {
            SessionHeader a = NewHeader("20261029T100000Z-aaaa");
            SessionHeader b = NewHeader("20261029T110000Z-bbbb");
            WriteSession(a, 3, 0, true);
            WriteSession(b, 3, 0, true);
            File.Copy(Path.Combine(root, a.SessionId, SessionFiles.SamplesFileName),
                Path.Combine(root, b.SessionId, SessionFiles.SamplesFileName), true);
            Assert.Throws<InvalidDataException>(() => SessionStore.Read(Path.Combine(root, b.SessionId)));
        }

        [Test]
        public void ListsNewestFirstAndDeletesOnlySessionFolders()
        {
            WriteSession(NewHeader("20261028T090000Z-0001"), 2, 0, true);
            WriteSession(NewHeader("20261029T090000Z-0002"), 2, 0, true);
            Directory.CreateDirectory(Path.Combine(root, "not-a-session"));

            List<string> folders = SessionStore.ListSessionFolders(root);
            Assert.AreEqual(2, folders.Count);
            StringAssert.EndsWith("20261029T090000Z-0002", folders[0]);

            SessionStore.Delete(folders[0]);
            Assert.AreEqual(1, SessionStore.ListSessionFolders(root).Count);
            Assert.Throws<IOException>(() => SessionStore.Delete(Path.Combine(root, "not-a-session")));
            Assert.IsTrue(Directory.Exists(Path.Combine(root, "not-a-session")));
        }

        [Test]
        public void SessionIdsSortByTime()
        {
            string earlier = SessionFiles.NewSessionId(new DateTime(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc), new Random(1));
            string later = SessionFiles.NewSessionId(new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc), new Random(2));
            Assert.Less(string.CompareOrdinal(earlier, later), 0);
            StringAssert.StartsWith("20261029T090000Z-", earlier);
        }
    }

    public sealed class PoseHistoryTests
    {
        private static readonly Quaternion Turned = Quaternion.CreateFromYawPitchRoll(1.0f, 0, 0);

        [Test]
        public void InterpolatesBetweenBracketingPoses()
        {
            PoseHistory history = new PoseHistory();
            history.Add(1_000, new Vector3(0, 0, 0), Quaternion.Identity, true);
            history.Add(2_000, new Vector3(1, 2, 3), Turned, true);

            Assert.IsTrue(history.TryGet(1_250, 0, out PoseHistory.Lookup lookup));
            Assert.That(Vector3.Distance(lookup.Position, new Vector3(0.25f, 0.5f, 0.75f)), Is.LessThan(1e-5f));
            Quaternion expected = Quaternion.Slerp(Quaternion.Identity, Turned, 0.25f);
            Assert.That(Math.Abs(Quaternion.Dot(lookup.Rotation, expected)), Is.GreaterThan(0.99999f));
            Assert.IsFalse(lookup.Extrapolated);
            Assert.IsFalse(lookup.Degraded);
        }

        [Test]
        public void ExtrapolatesOnlyWithinTheLimit()
        {
            PoseHistory history = new PoseHistory();
            history.Add(0, Vector3.Zero, Quaternion.Identity, true);
            history.Add(10_000, new Vector3(0.01f, 0, 0), Quaternion.Identity, true);   // 1 m/s

            Assert.IsTrue(history.TryGet(30_000, 30_000, out PoseHistory.Lookup ahead));
            Assert.IsTrue(ahead.Extrapolated);
            Assert.AreEqual(0.03f, ahead.Position.X, 1e-5f);
            Assert.IsFalse(history.TryGet(45_000, 30_000, out _), "beyond the limit");
            Assert.IsFalse(history.TryGet(-1, 30_000, out _), "older than the history");
        }

        [Test]
        public void FlagsDegradedTracking()
        {
            PoseHistory history = new PoseHistory();
            history.Add(0, Vector3.Zero, Quaternion.Identity, true);
            history.Add(10, Vector3.One, Quaternion.Identity, false);
            history.Add(20, Vector3.One, Quaternion.Identity, true);
            history.Add(30, Vector3.One, Quaternion.Identity, true);

            Assert.IsTrue(history.TryGet(5, 0, out PoseHistory.Lookup a));
            Assert.IsTrue(a.Degraded);
            Assert.IsTrue(history.TryGet(25, 0, out PoseHistory.Lookup b));
            Assert.IsFalse(b.Degraded);
        }

        [Test]
        public void RingBufferKeepsTheNewestPoses()
        {
            PoseHistory history = new PoseHistory(capacity: 8);
            for (int i = 0; i < 20; i++)
            {
                history.Add(i * 100, new Vector3(i, 0, 0), Quaternion.Identity, true);
            }
            Assert.AreEqual(8, history.Count);
            Assert.AreEqual(1_200, history.OldestUs);
            Assert.AreEqual(1_900, history.NewestUs);
            Assert.IsTrue(history.TryGet(1_850, 0, out PoseHistory.Lookup lookup));
            Assert.AreEqual(18.5f, lookup.Position.X, 1e-4f);
            Assert.IsFalse(history.TryGet(1_000, 0, out _), "dropped from the buffer");
        }

        [Test]
        public void IgnoresOutOfOrderPoses()
        {
            PoseHistory history = new PoseHistory();
            history.Add(100, Vector3.Zero, Quaternion.Identity, true);
            history.Add(50, Vector3.One, Quaternion.Identity, true);
            Assert.AreEqual(1, history.Count);
        }

        [TestCase(-75.0, SignalScale.Band.Weak)]
        [TestCase(-70.0, SignalScale.Band.Moderate)]
        [TestCase(-60.01, SignalScale.Band.Moderate)]
        [TestCase(-60.0, SignalScale.Band.Strong)]
        public void ClassifiesIntoBands(double dbm, SignalScale.Band band)
        {
            Assert.AreEqual(band, new SignalScale().Classify(dbm));
        }

        [Test]
        public void ScaleRejectsCrossedThresholds()
        {
            Assert.Throws<ArgumentException>(() => new SignalScale(-60, -60.5));
        }
    }
}
