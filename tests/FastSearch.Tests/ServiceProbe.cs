using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Security.Principal;
using System.Diagnostics;
using FastSearch.Core;
using FastSearch.Ipc;

internal static class ServiceProbe
{
    internal static void Run(Action<bool, string> check)
    {
        var entries = new List<IndexEntry> { new(5, 5, ".", true, null, null), new(10, 5, "myvar", true, null, null), new(20, 10, "data", true, null, null) };
        for (int i = 0; i < 20000; i++) entries.Add(new((ulong)(100 + i), 20, $"report_{i:D5}=日本語.txt", false, i, DateTime.UnixEpoch.AddSeconds(i)));
        var snapshot = new IndexSnapshot("service-fixture", 5, 123, 100, IndexTable.FromEntries(entries), 1);
        using var backend = new Backend(snapshot);
        using var ui = new ServiceIndexSource(backend.ConnectAsync);
        ui.Start();
        Wait(() => ui.IsReady);
        check(ui.Snapshot is null && ui.EntryCount == 20003 && ui.WorkerProcessId == Environment.ProcessId,
            "service UI retains only status/count, not a copy of the index");
        var context = new UiContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            using var results = (PagedSearchResults)ui.SearchAsync("var/data/port_", new(SortColumn.Size, true), 100, default).GetAwaiter().GetResult();
            check(results.Count == 20000 && results[0].SizeText.Length > 0 && results[0].Name == "report_19999=日本語.txt" && results[0].Folder == @"C:\myvar\data",
                "service hierarchy search, numerical sort, Unicode and initial page round trip");
            check(results.FindById(100)?.Name == "report_00000=日本語.txt", "service preloads the selected file's page after sorting");
            check(results.CachedRowCount <= 256, "20,000 search hits initially retain at most two result pages in the UI");
            var row = results[7000];
            check(!row.IsLoaded && row.Name == "読み込み中…", "uncached row lookup returns immediately without blocking the UI on pipe I/O");
            Wait(() => { context.Drain(); return row.IsLoaded; });
            check(row.Id == 13099 && results.IndexOf(row) == 7000 && row.Name == "report_12999=日本語.txt",
                "async page arrival updates the same row and preserves selection identity");
            for (int i = 0; i < 24; i++)
            {
                var distant = results[i * 700];
                Wait(() => { context.Drain(); return distant.IsLoaded; });
            }
            check(results.CachedRowCount <= 1024, "scrolling evicts old pages and keeps the UI row cache bounded to 1,024");
            ui.StopAsync(false).GetAwaiter().GetResult();
            backend.Update(snapshot with { Entries = IndexTable.FromEntries(entries.Append(new IndexEntry(99999, 20, "report_added.txt", false, 42, null))), Revision = 2 });
            using var reopened = new ServiceIndexSource(backend.ConnectAsync);
            reopened.Start(); Wait(() => reopened.IsReady);
            using var changed = reopened.SearchAsync("report_added", new(SortColumn.Name), null, default).GetAwaiter().GetResult();
            check(changed.Count == 1 && changed[0].Id == 99999 && reopened.EntryCount == 20004,
                "closing and reopening the UI reuses the running backend and sees changes made while it was closed");
            using var none = reopened.SearchAsync("not-present", new(SortColumn.Name), null, default).GetAwaiter().GetResult();
            check(none.Count == 0, "service returns empty result sets without attempting to fetch a page");
            reopened.StopAsync(false).GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(null); }

        using (var stream = backend.ConnectAsync(default).GetAwaiter().GetResult())
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            ServiceProtocol.WriteHeader(writer, 2); VolumeProtocol.WriteString(writer, "report");
            writer.Write(99); writer.Write(false); writer.Write(0UL); writer.Flush();
            bool closed = false;
            try { reader.ReadByte(); } catch (IOException) { closed = true; }
            check(closed, "service rejects malformed sort columns by closing the session");
        }
        using (var stream = backend.ConnectAsync(default).GetAwaiter().GetResult())
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            ServiceProtocol.WriteHeader(writer, 2); VolumeProtocol.WriteString(writer, "report");
            writer.Write(0); writer.Write(false); writer.Write(0UL); writer.Flush();
            VolumeProtocol.CheckResponse(reader); reader.ReadInt32(); reader.ReadInt32();
            writer.Write(-128); writer.Flush();
            bool closed = false;
            try { reader.ReadInt32(); } catch (IOException) { closed = true; }
            check(closed, "service rejects negative and out-of-range page requests");
        }
        check(backend.ActiveQueries == 0 || SpinWait.SpinUntil(() => backend.ActiveQueries == 0, 3000),
            "disposing results and UI connections releases backend query sessions");
        NativePipeProbe(snapshot, check);
        int connects = 0;
        using var recovering = new ServiceIndexSource(token => Interlocked.Increment(ref connects) == 1
            ? Task.FromException<NamedPipeClientStream>(new TimeoutException("service starting")) : backend.ConnectAsync(token));
        recovering.Start(); Wait(() => recovering.IsReady);
        check(connects >= 2 && recovering.EntryCount == 20004, "UI retries a timed-out service connection instead of permanently stopping its monitor");
        recovering.StopAsync(false).GetAwaiter().GetResult();
    }
    private static void NativePipeProbe(IndexSnapshot snapshot, Action<bool, string> check)
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        string name = "FastSearch.Service.NativeTest." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = new FastSearch.Worker.ServicePipeServer(new TestIndexSource(snapshot), sid, 1, name);
        var serving = server.RunAsync(stop.Token);
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
                    attempt == 4 ? TokenImpersonationLevel.Anonymous : TokenImpersonationLevel.Identification);
                pipe.Connect(3000);
                using var writer = new BinaryWriter(pipe, Encoding.UTF8, true);
                using var reader = new BinaryReader(pipe, Encoding.UTF8, true);
                if (attempt == 3)
                {
                    foreach (byte value in new byte[] { 0x46,0x46,0x53,0x32,2,0,0,0,1 })
                    { writer.Write(value); writer.Flush(); Thread.Sleep(20); }
                }
                else { ServiceProtocol.WriteHeader(writer, 1); writer.Flush(); }
                if (attempt == 2) Thread.Sleep(200); // A separate UI process may not read immediately.
                VolumeProtocol.CheckResponse(reader);
                VolumeProtocol.ReadString(reader, 4096);
                check(reader.ReadBoolean() && reader.ReadInt32() == 20003,
                    attempt == 0 ? "production pipe ACL allows the owner and validates the connected process token" :
                    "production pipe listener accepts a fresh connection after the first disconnect");
                check(reader.ReadInt64() == snapshot.Revision && reader.ReadInt32() == Environment.ProcessId,
                    "production status response includes its complete revision and process ID");
            }
            var start = new ProcessStartInfo("dotnet") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
            start.ArgumentList.Add(typeof(ServiceProbe).Assembly.Location);
            start.ArgumentList.Add("--service-client-probe"); start.ArgumentList.Add(name);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var child = Process.Start(start)!;
            if (!child.WaitForExit(8000)) { child.Kill(); throw new TimeoutException("Separate IPC client did not terminate"); }
            check(child.ExitCode == 0, "production pipe authenticates a separate client process without impersonation: " + child.StandardError.ReadToEnd());
        }
        finally { stop.Cancel(); serving.GetAwaiter().GetResult(); }
        string other = "S-1-5-21-100-200-300-9999";
        using var denyStop = new CancellationTokenSource();
        var deniedServer = new FastSearch.Worker.ServicePipeServer(new TestIndexSource(snapshot), other, 1, name + ".deny");
        var denying = deniedServer.RunAsync(denyStop.Token);
        try
        {
            using var client = new NamedPipeClientStream(".", name + ".deny", PipeDirection.InOut, PipeOptions.Asynchronous);
            bool denied = false;
            try { client.Connect(1000); } catch (UnauthorizedAccessException) { denied = true; }
            check(denied, "production pipe ACL rejects a caller whose SID differs from the installed owner");
        }
        finally { denyStop.Cancel(); denying.GetAwaiter().GetResult(); }
    }
    internal static void RunClient(string name,int expectedServer)
    {
        using var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
        pipe.Connect(3000);
        using var writer=new BinaryWriter(pipe,Encoding.UTF8,true);
        using var reader=new BinaryReader(pipe,Encoding.UTF8,true);
        ServiceProtocol.WriteHeader(writer,1);writer.Flush();
        VolumeProtocol.CheckResponse(reader);VolumeProtocol.ReadString(reader,4096);
        if(!reader.ReadBoolean() || reader.ReadInt32()!=20003)throw new Exception("Separate client received incomplete status");
        reader.ReadInt64();if(reader.ReadInt32()!=expectedServer)throw new Exception("Separate client received the wrong server PID");
    }
    private static void Wait(Func<bool> condition)
    { if (!SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(8))) throw new TimeoutException("Service integration timed out"); }
    private sealed class UiContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<Action> _work = new();
        public override void Post(SendOrPostCallback callback, object? state) => _work.Enqueue(() => callback(state));
        internal void Drain() { while (_work.TryDequeue(out var action)) action(); }
    }
    internal sealed class Backend(IndexSnapshot snapshot) : IIndexSource, IDisposable
    {
        private IndexSnapshot _snapshot = snapshot;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _sessions = new();
        private int _active;
        public int ActiveQueries => Volatile.Read(ref _active);
        public IndexSnapshot Snapshot => Volatile.Read(ref _snapshot);
        public event Action<string>? StatusChanged { add { } remove { } }
        public event Action? Changed { add { } remove { } }
        internal void Update(IndexSnapshot next) => Volatile.Write(ref _snapshot, next);
        internal async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
        {
            string name = "FastSearch.Service.Test." + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _sessions.Add(Task.Run(async () =>
            {
                using (server)
                using (_stop.Token.Register(() => server.Dispose()))
                {
                    Interlocked.Increment(ref _active);
                    try { await server.WaitForConnectionAsync(_stop.Token); ServiceSession.Serve(server, this, () => "ready", _stop.Token); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
                    finally { Interlocked.Decrement(ref _active); }
                }
            }));
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await client.ConnectAsync(3000, token).ConfigureAwait(false); return client; }
            catch { client.Dispose(); throw; }
        }
        public void Dispose()
        { _stop.Cancel(); if (!Task.WhenAll(_sessions).Wait(TimeSpan.FromSeconds(4))) throw new TimeoutException("Service fixture did not stop"); _stop.Dispose(); }
    }
}
