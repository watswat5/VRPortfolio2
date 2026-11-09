using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WifiHeatmap.Core
{
    /// <summary>
    /// On-disk layout of one session folder:
    ///   session.json  – <see cref="SessionHeader"/>
    ///   samples.bin   – 32-byte file header, then blocks:
    ///                   [uint16 record count][uint16 reserved][count × 48-byte record][uint32 CRC-32 of count..records]
    ///   clock.jsonl   – one PING/PONG exchange per line
    /// Blocks are closed whenever the writer flushes, so every flushed sample is CRC-checked; a crash
    /// can only tear the block being written, which readers detect and drop.
    /// </summary>
    public static class SessionFiles
    {
        public const string HeaderFileName = "session.json";
        public const string SamplesFileName = "samples.bin";
        public const string ClockFileName = "clock.jsonl";
        public const int FileHeaderSize = 32;
        public const ushort FileVersion = 1;
        public const int MaxRecordsPerBlock = 64;
        public const int BlockOverhead = 8;
        private static readonly byte[] Magic = { (byte)'W', (byte)'H', (byte)'S', (byte)'B' };

        internal static void WriteFileHeader(Stream stream, string sessionId)
        {
            byte[] header = new byte[FileHeaderSize];
            Magic.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), FileVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), SampleRecord.Size);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8, 8), Hash(sessionId));
            stream.Write(header, 0, header.Length);
        }

        internal static void ValidateFileHeader(ReadOnlySpan<byte> header, string sessionId)
        {
            if (header.Length < FileHeaderSize || !header.Slice(0, 4).SequenceEqual(Magic))
            {
                throw new InvalidDataException("samples.bin is not a WiFi heatmap sample file.");
            }
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
            if (version != FileVersion)
            {
                throw new InvalidDataException($"samples.bin version {version} is not supported (expected {FileVersion}).");
            }
            ushort recordSize = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
            if (recordSize != SampleRecord.Size)
            {
                throw new InvalidDataException($"samples.bin record size {recordSize} does not match {SampleRecord.Size}.");
            }
            if (BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(8, 8)) != Hash(sessionId))
            {
                throw new InvalidDataException("samples.bin belongs to a different session than session.json.");
            }
        }

        /// <summary>FNV-1a, to tie samples.bin to its session.json.</summary>
        internal static ulong Hash(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(text ?? string.Empty))
            {
                hash = (hash ^ b) * 1099511628211UL;
            }
            return hash;
        }

        /// <summary>Writes the header to a temporary file, then swaps it in, so a crash never leaves half a header.</summary>
        internal static void WriteHeaderAtomically(string folder, SessionHeader header)
        {
            string path = Path.Combine(folder, HeaderFileName);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, header.ToJson(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }

        public static string NewSessionId(DateTime utcNow, Random random = null)
        {
            random ??= new Random();
            return utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" +
                   random.Next(0, 0x10000).ToString("x4", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Appends records to a session. Thread-safe: the recorder appends on the main thread while a
    /// background task forces data to disk. Call <see cref="Complete"/> to finalise.
    /// </summary>
    public sealed class SessionFileWriter : IDisposable
    {
        private readonly object sync = new object();
        private readonly FileStream samples;
        private readonly StreamWriter clock;
        private readonly byte[] block = new byte[SessionFiles.MaxRecordsPerBlock * SampleRecord.Size + SessionFiles.BlockOverhead];
        private int recordsInBlock;
        private bool closed;

        private SessionFileWriter(string folder, SessionHeader header)
        {
            Folder = folder;
            Header = header;
            samples = new FileStream(Path.Combine(folder, SessionFiles.SamplesFileName), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 1 << 16);
            SessionFiles.WriteFileHeader(samples, header.SessionId);
            clock = new StreamWriter(new FileStream(Path.Combine(folder, SessionFiles.ClockFileName), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 1 << 12), new UTF8Encoding(false));
        }

        public string Folder { get; }
        public SessionHeader Header { get; }
        public long RecordCount { get; private set; }

        /// <summary>Creates &lt;root&gt;/&lt;sessionId&gt;/ and writes an incomplete header.</summary>
        public static SessionFileWriter Create(string sessionsRoot, SessionHeader header)
        {
            if (string.IsNullOrEmpty(header.SessionId))
            {
                throw new ArgumentException("The header needs a session ID.", nameof(header));
            }
            string folder = Path.Combine(sessionsRoot, header.SessionId);
            if (Directory.Exists(folder))
            {
                throw new IOException($"Session folder {folder} already exists.");
            }
            Directory.CreateDirectory(folder);
            header.Records.Count = 0;
            header.Records.Complete = false;
            SessionFiles.WriteHeaderAtomically(folder, header);
            return new SessionFileWriter(folder, header);
        }

        public void Append(in SampleRecord record)
        {
            lock (sync)
            {
                ThrowIfClosed();
                if (record.AnchorIndex >= Header.Anchors.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(record),
                        $"Anchor index {record.AnchorIndex} is not in the session's anchor table ({Header.Anchors.Count} rows).");
                }
                record.Write(block.AsSpan(4 + recordsInBlock * SampleRecord.Size, SampleRecord.Size));
                recordsInBlock++;
                RecordCount++;
                if (recordsInBlock == SessionFiles.MaxRecordsPerBlock)
                {
                    CloseBlock();
                }
            }
        }

        public void AppendClockExchange(PingExchange exchange)
        {
            lock (sync)
            {
                ThrowIfClosed();
                clock.Write(string.Format(CultureInfo.InvariantCulture,
                    "{{\"hostSendUs\":{0},\"hostReceiveUs\":{1},\"probeReceiveUs\":{2},\"probeSendUs\":{3},\"bootId\":{4}}}\n",
                    exchange.HostSendUs, exchange.HostReceiveUs, exchange.ProbeReceiveUs, exchange.ProbeSendUs, exchange.BootId));
            }
        }

        /// <summary>Adds an anchor to the table if new, rewriting the header so the index is never orphaned.</summary>
        public int AddAnchor(string kind, Guid uuid, string label)
        {
            lock (sync)
            {
                ThrowIfClosed();
                int before = Header.Anchors.Count;
                int index = Header.AddAnchor(kind, uuid, label);
                if (Header.Anchors.Count != before)
                {
                    SessionFiles.WriteHeaderAtomically(Folder, Header);
                }
                return index;
            }
        }

        /// <summary>
        /// Closes the current block and hands buffered data to the OS. With <paramref name="toDisk"/>,
        /// also waits for the storage device (slow; call it off the main thread).
        /// </summary>
        public void Flush(bool toDisk)
        {
            lock (sync)
            {
                if (closed)
                {
                    return;
                }
                CloseBlock();
                samples.Flush(toDisk);
                clock.Flush();
                if (toDisk)
                {
                    ((FileStream)clock.BaseStream).Flush(true);
                }
            }
        }

        /// <summary>Flushes to disk and marks the session complete.</summary>
        public void Complete(DateTime endedUtc)
        {
            lock (sync)
            {
                if (closed)
                {
                    return;
                }
                CloseBlock();
                samples.Flush(true);
                clock.Flush();
                ((FileStream)clock.BaseStream).Flush(true);
                Header.EndedUtc = endedUtc;
                Header.Records.Count = RecordCount;
                Header.Records.Complete = true;
                SessionFiles.WriteHeaderAtomically(Folder, Header);
                closed = true;
                samples.Dispose();
                clock.Dispose();
            }
        }

        /// <summary>Releases the files without finalising (as a crash would); recovery completes it later.</summary>
        public void Dispose()
        {
            lock (sync)
            {
                if (closed)
                {
                    return;
                }
                CloseBlock();
                closed = true;
                samples.Dispose();
                clock.Dispose();
            }
        }

        private void CloseBlock()
        {
            if (recordsInBlock == 0)
            {
                return;
            }
            int payload = 4 + recordsInBlock * SampleRecord.Size;
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0, 2), (ushort)recordsInBlock);
            BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2, 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(payload, 4), Crc32.Compute(block.AsSpan(0, payload)));
            samples.Write(block, 0, payload + 4);
            recordsInBlock = 0;
        }

        private void ThrowIfClosed()
        {
            if (closed)
            {
                throw new ObjectDisposedException(nameof(SessionFileWriter));
            }
        }
    }

    /// <summary>A loaded session: header, every intact record, and what (if anything) was dropped.</summary>
    public sealed class SessionData
    {
        public SessionHeader Header;
        public string Folder;
        public List<SampleRecord> Records = new List<SampleRecord>();
        /// <summary>Byte offset just past the last intact block (where a recovered file is truncated).</summary>
        public long ValidLength;
        /// <summary>Bytes after the last intact block: a torn write or corruption. 0 for a clean file.</summary>
        public long DroppedBytes;
        /// <summary>Records whose anchor index is outside the anchor table (should never happen).</summary>
        public int InvalidAnchorRecords;
    }

    /// <summary>Reads, validates, recovers, lists and deletes sessions.</summary>
    public static class SessionStore
    {
        public static SessionHeader ReadHeader(string folder)
        {
            return SessionHeader.FromJson(File.ReadAllText(Path.Combine(folder, SessionFiles.HeaderFileName)));
        }

        /// <summary>
        /// Reads every intact block. Stops at the first short or CRC-failing block (a torn final write
        /// or corruption) and reports the remainder as dropped instead of guessing.
        /// </summary>
        public static SessionData Read(string folder)
        {
            SessionData data = new SessionData { Folder = folder, Header = ReadHeader(folder) };
            byte[] bytes = File.ReadAllBytes(Path.Combine(folder, SessionFiles.SamplesFileName));
            SessionFiles.ValidateFileHeader(bytes, data.Header.SessionId);

            int offset = SessionFiles.FileHeaderSize;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < SessionFiles.BlockOverhead)
                {
                    break;
                }
                int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
                int payload = 4 + count * SampleRecord.Size;
                if (count == 0 || count > SessionFiles.MaxRecordsPerBlock || bytes.Length - offset < payload + 4)
                {
                    break;
                }
                uint stored = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + payload, 4));
                if (stored != Crc32.Compute(bytes.AsSpan(offset, payload)))
                {
                    break;
                }
                for (int i = 0; i < count; i++)
                {
                    SampleRecord record = SampleRecord.Read(bytes.AsSpan(offset + 4 + i * SampleRecord.Size, SampleRecord.Size));
                    if (record.AnchorIndex >= data.Header.Anchors.Count)
                    {
                        data.InvalidAnchorRecords++;
                        continue;
                    }
                    data.Records.Add(record);
                }
                offset += payload + 4;
            }
            data.ValidLength = offset;
            data.DroppedBytes = bytes.Length - offset;
            return data;
        }

        /// <summary>Folders under <paramref name="root"/> that have a session header, newest first.</summary>
        public static List<string> ListSessionFolders(string root)
        {
            List<string> folders = new List<string>();
            if (!Directory.Exists(root))
            {
                return folders;
            }
            foreach (string folder in Directory.GetDirectories(root))
            {
                if (File.Exists(Path.Combine(folder, SessionFiles.HeaderFileName)))
                {
                    folders.Add(folder);
                }
            }
            // Session IDs start with a UTC timestamp, so name order is time order.
            folders.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));
            return folders;
        }

        /// <summary>
        /// Finalises sessions left incomplete by a crash or a killed app: drops the torn tail after the
        /// last intact block, records the real count, and marks them complete and recovered.
        /// Returns the folders it recovered. Unreadable sessions are left untouched.
        /// </summary>
        public static List<string> RecoverIncomplete(string root, Func<string, bool> isInUse = null)
        {
            List<string> recovered = new List<string>();
            foreach (string folder in ListSessionFolders(root))
            {
                if (isInUse != null && isInUse(folder))
                {
                    continue;
                }
                SessionHeader header;
                try
                {
                    header = ReadHeader(folder);
                }
                catch (Exception)
                {
                    continue;
                }
                if (header.Records.Complete)
                {
                    continue;
                }

                SessionData data;
                try
                {
                    data = Read(folder);
                }
                catch (Exception)
                {
                    continue;
                }
                if (data.DroppedBytes > 0)
                {
                    using (FileStream stream = new FileStream(Path.Combine(folder, SessionFiles.SamplesFileName), FileMode.Open, FileAccess.Write))
                    {
                        stream.SetLength(data.ValidLength);
                    }
                }
                header.Records.Count = data.Records.Count + data.InvalidAnchorRecords;
                header.Records.Complete = true;
                header.Records.Recovered = true;
                SessionFiles.WriteHeaderAtomically(folder, header);
                recovered.Add(folder);
            }
            return recovered;
        }

        public static void Delete(string folder)
        {
            if (!File.Exists(Path.Combine(folder, SessionFiles.HeaderFileName)))
            {
                throw new IOException($"{folder} is not a session folder; refusing to delete it.");
            }
            Directory.Delete(folder, true);
        }
    }
}
