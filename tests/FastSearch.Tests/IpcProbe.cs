using System.IO;
using System.IO.Pipes;
using System.Text;
using FastSearch.Core;
using FastSearch.Ipc;
using FastSearch.Native;

internal static class IpcProbe
{
    internal static void Run(Action<bool, string> check)
    {
        using (var volume = new FixtureVolume(9_000))
        using (var session = new Session(volume))
        {
            var remote = session.Remote;
            check(remote.Identity == volume.Identity && remote.RootId == 5, "IPC version handshake and fixed volume identity");
            var entries = remote.EnumerateEntries(default).ToArray();
            check(entries.Length == 9_001 && entries[1].Name == "日本語_=.txt" && entries[1].ModifiedUtc?.Kind == DateTimeKind.Utc &&
                entries[2].Size is null, "IPC bounded MFT streaming retains Unicode, punctuation and unknown attributes");
            volume.SetChanges(101, Enumerable.Range(0, 8_500).Select(i => new UsnRecord((ulong)(i + 20), 5, $"new-{i}.txt", false, 0x100)).ToArray());
            var batch = remote.ReadJournal(123, 100);
            check(batch.NextUsn == 101 && batch.Records.Count == 8_500, "IPC journal response crosses multiple bounded blocks");
            var metadata = remote.ReadMetadata(batch.Records);
            check(metadata.Count == 8_500 && metadata[^1].Id == batch.Records[^1].Id && metadata[^1].Size == 42,
                "IPC metadata batching crosses request limits without losing entries");
            bool journalError = false;
            volume.FailNextQuery = true;
            try { remote.QueryJournal(); } catch (JournalUnavailableException) { journalError = true; }
            check(journalError && remote.QueryJournal().NextUsn == 101, "IPC journal discontinuity is typed and leaves RPC synchronized");
        }

        using (var buffer = new MemoryStream())
        {
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true)) writer.Write(VolumeProtocol.BatchLimit + 1);
            buffer.Position = 0;
            bool rejected = false;
            try { VolumeProtocol.ReadCount(new BinaryReader(buffer)); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "IPC rejects oversized message counts");
        }
        using (var buffer = new MemoryStream())
        {
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
            { writer.Write((ulong)1); writer.Write((ulong)5); VolumeProtocol.WriteString(writer, "..\\outside"); }
            buffer.Position = 0;
            bool rejected = false;
            try { VolumeProtocol.ReadRecord(new BinaryReader(buffer)); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "IPC rejects paths in filename fields");
        }

        using (var buffer = new MemoryStream())
        using (var volume = new FixtureVolume(0))
        {
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
            { writer.Write((byte)VolumeCommand.Hello); writer.Write(VolumeProtocol.Version + 1); }
            buffer.Position = 0;
            bool rejected = false;
            try { VolumeProtocol.Serve(buffer, volume, default); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "IPC rejects incompatible versions before exposing volume data");
        }

        // A real named pipe, rather than a mocked RPC implementation, is used by IndexManager.
        string folder = Path.Combine(Path.GetTempPath(), "FastSearch-IPC-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string storage = Path.Combine(folder, "index.bin");
            using var source = new FixtureVolume(200);
            using (var session = new Session(source))
            using (var manager = new IndexManager(storage, _ => session.Remote))
            {
                manager.Start();
                Wait(() => manager.Snapshot?.Entries.Count == 201);
                check(source.Enumerations == 1 && manager.Snapshot!.NextUsn == 100, "frontend builds its sole index from streamed worker records");
                source.SetChanges(102, [new(6, 5, "deleted.txt", false, 0x200), new(999, 5, "renamed_.txt", false, 0x2000)]);
                Wait(() => manager.Snapshot?.NextUsn == 102);
                check(!manager.Snapshot!.Entries.ContainsKey(6) && manager.Snapshot.Entries[999].Name == "renamed_.txt",
                    "frontend applies worker deletion/rename and commits the USN with its snapshot");
                source.JournalId = 456;
                Wait(() => manager.Snapshot?.JournalId == 456);
                check(source.Enumerations == 2, "worker journal replacement triggers complete frontend recovery");
                manager.StopAsync(false).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                check(session.Server.Wait(TimeSpan.FromSeconds(3)) && new IndexStorage(storage).Load(default)?.JournalId == 456,
                    "frontend shutdown closes worker RPC and atomically saves the index");
            }
            using (var session = new Session(source))
            using (var manager = new IndexManager(storage, _ => session.Remote))
            {
                int before = source.Enumerations;
                source.SetChanges(103, [new(1000, 5, "offline_.txt", false, 0x100)]);
                manager.Start();
                Wait(() => manager.Snapshot?.NextUsn == 103);
                check(source.Enumerations == before && manager.Snapshot!.Entries.ContainsKey(1000),
                    "frontend restart reuses the persisted index and replays offline changes over IPC");
                manager.StopAsync(false).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            using (var failed = new IndexManager(storage, _ => throw new IOException("UAC canceled")))
            {
                string? status = null;
                failed.StatusChanged += text => Volatile.Write(ref status, text);
                failed.Start();
                Wait(() => Volatile.Read(ref status)?.Contains("UAC canceled") == true);
                failed.StopAsync(false).GetAwaiter().GetResult();
                check(new IndexStorage(storage).Load(default)?.NextUsn == 103, "worker startup failure leaves the existing checkpoint intact");
            }
            source.JournalId = 789;
            source.FailureAfter = 30;
            using (var session = new Session(source))
            using (var manager = new IndexManager(storage, _ => session.Remote))
            {
                string? status = null;
                manager.StatusChanged += text => Volatile.Write(ref status, text);
                manager.Start();
                Wait(() => Volatile.Read(ref status)?.StartsWith("索引更新を停止しました") == true);
                manager.StopAsync(false).GetAwaiter().GetResult();
                check(manager.Snapshot is null && new IndexStorage(storage).Load(default)?.NextUsn == 103,
                    "interrupted rebuild never publishes a partial index or overwrites the valid checkpoint");
            }
        }
        finally { Directory.Delete(folder, true); }

        using (var source = new FixtureVolume(10_000))
        using (var session = new Session(source))
        {
            using var iterator = session.Remote.EnumerateEntries(default).GetEnumerator();
            iterator.MoveNext();
            session.Remote.Dispose();
            check(session.Server.Wait(TimeSpan.FromSeconds(3)), "disconnect during MFT streaming terminates the worker loop");
        }
        using (var source = new FixtureVolume(0))
        using (var session = new Session(source))
        {
            session.Stop.Cancel();
            check(session.Server.Wait(TimeSpan.FromSeconds(3)), "worker cancellation interrupts an idle IPC read");
        }
    }

    private static void Wait(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(8))) throw new TimeoutException("IPC integration timed out");
    }
    private sealed class Session : IDisposable
    {
        internal CancellationTokenSource Stop { get; } = new(TimeSpan.FromSeconds(30));
        internal RemoteIndexVolume Remote { get; }
        internal Task Server { get; }
        private readonly NamedPipeServerStream _server;
        internal Session(IIndexVolume source)
        {
            string name = "FastSearch.Test." + Guid.NewGuid().ToString("N");
            _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Server = Task.Run(async () =>
            {
                try
                {
                    await _server.WaitForConnectionAsync(Stop.Token);
                    VolumeProtocol.Serve(_server, source, Stop.Token);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
                finally { _server.Dispose(); }
            });
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(3000);
            Remote = new(client, Stop.Token);
        }
        public void Dispose()
        {
            Remote.Dispose(); Stop.Cancel(); _server.Dispose();
            if (!Server.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("IPC worker did not terminate");
            Stop.Dispose();
        }
    }
    private sealed class FixtureVolume(int files) : IIndexVolume
    {
        private readonly object _gate = new();
        private long _next = 100;
        private UsnRecord[] _changes = [];
        internal int Enumerations;
        internal bool FailNextQuery;
        internal int FailureAfter = int.MaxValue;
        internal ulong JournalId = 123;
        public string Identity => "test-C-volume";
        public ulong RootId => 5;
        public JournalState QueryJournal()
        {
            lock (_gate)
            {
                if (FailNextQuery) { FailNextQuery = false; throw new JournalUnavailableException("journal reset"); }
                return new(JournalId, 0, _next, 0);
            }
        }
        internal void SetChanges(long next, UsnRecord[] changes)
        { lock (_gate) { _changes = changes; _next = next; } }
        public IEnumerable<IndexEntry> EnumerateEntries(CancellationToken token)
        {
            Interlocked.Increment(ref Enumerations);
            yield return new(5, 5, ".", true, null, null);
            for (int i = 0; i < files; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i == FailureAfter) throw new IOException("worker disconnected mid-build");
                yield return new((ulong)(6 + i), 5, i == 0 ? "日本語_=.txt" : $"file-{i}.txt", false,
                    i == 1 ? null : i, new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc));
            }
        }
        public JournalBatch ReadJournal(ulong journalId, long startUsn)
        {
            lock (_gate)
            {
                if (journalId != JournalId) throw new JournalUnavailableException("journal reset");
                return new(_next, _changes);
            }
        }
        public IReadOnlyList<IndexEntry> ReadMetadata(IReadOnlyList<UsnRecord> records) =>
            records.Select(record => new IndexEntry(record.Id, record.ParentId, record.Name, record.IsDirectory, 42, null)).ToArray();
        public void Dispose() { }
    }
}
