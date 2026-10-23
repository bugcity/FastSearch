using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FastSearch.Diagnostics;

/// <summary>Opt-in Private Bytes sampling; no WPF references or UI polling while resident.</summary>
public sealed class MemoryDiagnostics : IDisposable
{
    private readonly Channel<string> _samples = Channel.CreateBounded<string>(new BoundedChannelOptions(64)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer, _periodic;
    private string _state = "startup";
    private int _indexCount, _resultCount, _realizedRows;
    private int _disposed;
    private readonly Func<int>? _workerProcessId;
    public string Path { get; }
    public Exception? WriteError { get; private set; }

    public MemoryDiagnostics(string path, TimeSpan? interval = null, Func<int>? workerProcessId = null)
    {
        Path = path;
        _workerProcessId = workerProcessId;
        _writer = Task.Run(WriteAsync);
        _periodic = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(interval ?? TimeSpan.FromMinutes(5));
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false)) Sample("periodic:" + Volatile.Read(ref _state));
            }
            catch (OperationCanceledException) { }
        });
    }

    public void SetIndexCount(int count) => Volatile.Write(ref _indexCount, count);
    public IDisposable SampleDuringSearch(string state = "searching") => new Timer(_ =>
    {
        if (Volatile.Read(ref _state) == state) Sample(state);
    }, null, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));

    public void Record(string state, int resultCount = 0, int realizedRows = 0)
    {
        Volatile.Write(ref _state, state);
        Volatile.Write(ref _resultCount, resultCount);
        Volatile.Write(ref _realizedRows, realizedRows);
        Sample(state);
    }

    private void Sample(string state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            long privateBytes = process.PrivateMemorySize64;
            int workerPid = _workerProcessId?.Invoke() ?? 0;
            long? workerBytes = workerPid == 0 ? 0 : null;
            if (workerPid != 0)
            {
                try { using var worker = Process.GetProcessById(workerPid); workerBytes = worker.PrivateMemorySize64; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            long? totalBytes = workerBytes is long bytes ? privateBytes + bytes : null;
            string safeState = state.Replace('"', '\'');
            _samples.Writer.TryWrite(string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:O},{Environment.ProcessId},\"{safeState}\",{Volatile.Read(ref _indexCount)},{Volatile.Read(ref _resultCount)},{privateBytes},{GC.GetTotalMemory(false)},{Volatile.Read(ref _realizedRows)},{workerPid},{workerBytes},{totalBytes}"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { WriteError = ex; }
    }

    private async Task WriteAsync()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            StreamWriter? writer = null;
            try
            {
                long written = 0;
                await foreach (var sample in _samples.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (writer is null)
                    {
                        writer = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read));
                        await writer.WriteLineAsync("timestamp,pid,state,index_count,result_count,private_bytes,managed_bytes,realized_rows,worker_pid,worker_private_bytes,total_private_bytes").ConfigureAwait(false);
                    }
                    await writer.WriteLineAsync(sample).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    written += sample.Length;
                    if (written > 8 * 1024 * 1024)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                        writer = null;
                        File.Move(Path, Path + ".previous.csv", true);
                        written = 0;
                    }
                }
            }
            finally { if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteError = ex; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _samples.Writer.TryComplete();
        var completion = Task.WhenAll(_periodic, _writer);
        try { completion.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        if (completion.IsCompleted) _stop.Dispose();
        else _ = completion.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
