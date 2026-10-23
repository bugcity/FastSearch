using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FastSearch.Core;
using FastSearch.Native;

namespace FastSearch.Ipc;

/// <summary>The UI never owns an index. Each query owns a cancellable, paged result session.</summary>
internal sealed class ServiceIndexSource : IIndexSource, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly string _sid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly Func<CancellationToken, Task<NamedPipeClientStream>>? _connect;
    private Task? _monitor;
    private int _count, _ready, _process;
    public IndexSnapshot? Snapshot => null;
    public bool IsReady => Volatile.Read(ref _ready) != 0;
    public int EntryCount => Volatile.Read(ref _count);
    public int WorkerProcessId => Volatile.Read(ref _process);
    public event Action<string>? StatusChanged;
    public event Action? Changed;
    internal ServiceIndexSource(Func<CancellationToken, Task<NamedPipeClientStream>>? connect = null) => _connect = connect;
    public void Start() => _monitor = Task.Run(MonitorAsync);
    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        if (_connect is not null) return await _connect(token).ConfigureAwait(false);
        int expected = ServiceControl.RunningProcessId();
        var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName(_sid), PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(1500, token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual) || actual != (uint)expected)
                throw new IOException("索引サービスの接続先を確認できません。");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }
    private async Task MonitorAsync()
    {
        StatusChanged?.Invoke("索引サービスへ接続しています…");
        string? previous = null;
        string? loggedError = null;
        long revision = -1;
        int pid = 0;
        while (!_stop.IsCancellationRequested)
        {
            string status;
            bool changed = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                using var pipe = await ConnectAsync(timeout.Token).ConfigureAwait(false);
                using var cancel = timeout.Token.Register(() => pipe.Dispose());
                using var writer = new BinaryWriter(pipe, Encoding.UTF8, true);
                using var reader = new BinaryReader(pipe, Encoding.UTF8, true);
                ServiceProtocol.WriteHeader(writer, 1); writer.Flush();
                VolumeProtocol.CheckResponse(reader);
                status = VolumeProtocol.ReadString(reader, 4096);
                bool ready = reader.ReadBoolean(); int count = reader.ReadInt32();
                long next = reader.ReadInt64(); int process = reader.ReadInt32();
                if (count < 0 || count > ServiceProtocol.MaxResults || process <= 0) throw new InvalidDataException("サービス状態が不正です。");
                changed = next != revision || pid != process || ready != IsReady;
                revision = next; pid = process;
                Volatile.Write(ref _count, count); Volatile.Write(ref _process, process); Volatile.Write(ref _ready, ready ? 1 : 0);
                loggedError = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            {
                changed = Interlocked.Exchange(ref _ready, 0) != 0;
                Volatile.Write(ref _process, 0);
                status = ex is EndOfStreamException
                    ? "索引サービスとの通信が応答の途中で切れました。サービス側の error.log を確認してください。"
                    : ex is Win32Exception or IOException ? ex.Message : "索引サービスへ接続できません。再接続しています…";
                string key = ex.GetType().FullName + ":" + ex.Message;
                if (!_stop.IsCancellationRequested && key != loggedError) { IndexManager.Log(ex); loggedError = key; }
            }
            if (_stop.IsCancellationRequested) break;
            if (status != previous) { previous = status; StatusChanged?.Invoke(status); }
            if (changed) Changed?.Invoke();
            try { await Task.Delay(1000, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
    public async Task<ISearchResults> SearchAsync(string query, SortOrder sort, ulong? selectedId, CancellationToken token)
    {
        // Capture WPF's context before moving pipe reads to the thread pool.
        var context = SynchronizationContext.Current ?? throw new InvalidOperationException("UIコンテキストがありません。");
        var pipe = await ConnectAsync(token).ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            try
            {
                var results = await PagedSearchResults.CreateAsync(pipe, query, sort, selectedId, context, token).ConfigureAwait(false);
                results.ConnectionLost += () => { StatusChanged?.Invoke("検索結果をサービスから再取得しています…"); Changed?.Invoke(); };
                return (ISearchResults)results;
            }
            catch { pipe.Dispose(); throw; }
        }, CancellationToken.None).ConfigureAwait(false);
    }
    public async Task StopAsync(bool sessionEnding)
    {
        _stop.Cancel();
        if (_monitor is not null) await _monitor.ConfigureAwait(false);
    }
    public void Dispose() { _stop.Cancel(); }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}
