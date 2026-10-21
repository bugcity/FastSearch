using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastSearch.Native;

namespace FastSearch.Core;

public sealed class IndexManager : IIndexSource, IDisposable
{
    private readonly IndexStorage _storage;
    private readonly CancellationTokenSource _stop = new();
    private IndexSnapshot? _snapshot;
    private Task? _worker;
    public IndexSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public event Action<string>? StatusChanged;
    public event Action? Changed;
    public IndexManager(string storagePath) => _storage = new(storagePath);
    public void Start() => _worker = Task.Run(() => RunAsync(_stop.Token));
    private void Status(string text) => StatusChanged?.Invoke(text);
    private void Publish(IndexSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        Changed?.Invoke();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var volume = new NtfsVolume();
            Status("保存済み索引を検証しています…");
            var snapshot = _storage.Load(token);
            snapshot = Recover(volume, snapshot, token);
            Publish(snapshot);
            Save(snapshot, token);
            var lastSave = Stopwatch.StartNew();
            long savedUsn = snapshot.NextUsn;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var journal = volume.QueryJournal();
                if (!journal.CanResume(snapshot, volume.Identity))
                {
                    Status("Journalの継続性が失われたため索引を再構築しています…");
                    snapshot = Recover(volume, null, token);
                    Publish(snapshot);
                }
                else if (journal.NextUsn != snapshot.NextUsn)
                {
                    try
                    {
                        var next = CatchUp(volume, snapshot, token);
                        if (next.NextUsn != snapshot.NextUsn)
                        {
                            snapshot = next;
                            Publish(snapshot);
                        }
                    }
                    catch (JournalUnavailableException)
                    {
                        snapshot = Recover(volume, null, token);
                        Publish(snapshot);
                    }
                }
                if (lastSave.Elapsed >= TimeSpan.FromMinutes(2) && savedUsn != snapshot.NextUsn)
                {
                    Save(snapshot, token);
                    savedUsn = snapshot.NextUsn;
                    lastSave.Restart();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Status($"索引更新を停止しました: {ex.Message}");
            Log(ex);
        }
    }

    private IndexSnapshot Recover(NtfsVolume volume, IndexSnapshot? candidate, CancellationToken token)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var state = volume.QueryJournal();
                if (candidate is null || candidate.RootId != volume.RootId || !state.CanResume(candidate, volume.Identity))
                    candidate = Build(volume, token);
                Status("停止中の変更を反映しています…");
                return CatchUp(volume, candidate, token);
            }
            catch (JournalUnavailableException) { candidate = null; }
        }
        throw new IOException("USN Journalの継続性を確保できませんでした。少し待ってから再起動してください。");
    }

    private IndexSnapshot Build(NtfsVolume volume, CancellationToken token)
    {
        // If wrap happens during a long build, discard it and start with a new checkpoint.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var start = volume.QueryJournal();
            Status("C:\\ のMFTを読み込んでいます…");
            var records = new Dictionary<ulong, UsnRecord>();
            foreach (var record in volume.EnumerateMft(token))
            {
                records[record.Id] = record;
                if (records.Count % 50_000 == 0) Status($"MFT読み込み中: {records.Count:N0} 件");
            }
            var source = records.Values.ToArray();
            records = null!;
            var entries = new IndexEntry[source.Length];
            int completed = 0;
            Status($"属性取得中: 0 / {source.Length:N0} 件");
            Parallel.For(0, source.Length, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 }, i =>
            {
                entries[i] = volume.ReadMetadata(source[i]);
                int count = Interlocked.Increment(ref completed);
                if (count % 25_000 == 0) Status($"属性取得中: {count:N0} / {source.Length:N0} 件");
            });
            var snapshot = new IndexSnapshot(volume.Identity, volume.RootId, start.Id, start.NextUsn,
                entries.ToImmutableDictionary(e => e.Id), (Snapshot?.Revision ?? 0) + 1);
            if (volume.QueryJournal().CanResume(snapshot, volume.Identity)) return snapshot;
        }
        throw new IOException("索引構築中にUSN Journalが繰り返し切り替わりました。Journalの容量を確認して再起動してください。");
    }

    private IndexSnapshot CatchUp(NtfsVolume volume, IndexSnapshot snapshot, CancellationToken token)
    {
        var state = volume.QueryJournal();
        if (!state.CanResume(snapshot, volume.Identity)) throw new JournalUnavailableException("Journalの継続位置が無効です。");
        long target = state.NextUsn;
        while (snapshot.NextUsn < target)
        {
            token.ThrowIfCancellationRequested();
            var batch = volume.ReadJournal(snapshot.JournalId, snapshot.NextUsn);
            if (batch.NextUsn <= snapshot.NextUsn) throw new JournalUnavailableException("Journalの継続位置が進みません。");
            snapshot = ApplyBatch(snapshot, batch, volume.ReadMetadata, token);
        }
        if (!volume.QueryJournal().CanResume(snapshot, volume.Identity))
            throw new JournalUnavailableException("差分取得中にJournalが切り替わりました。");
        Status($"{snapshot.Entries.Count:N0} 件の索引 · C:\\ · 更新中");
        return snapshot;
    }

    public static IndexSnapshot ApplyBatch(IndexSnapshot snapshot, JournalBatch batch,
        Func<UsnRecord, IndexEntry> metadata, CancellationToken token)
    {
        // A checkpoint and all the mutations preceding it are published/saved as one immutable value.
        var latest = new Dictionary<ulong, UsnRecord>();
        foreach (var record in batch.Records)
        {
            token.ThrowIfCancellationRequested();
            if ((record.Reason & 0x1000) != 0 && (record.Reason & ~0x80001000u) == 0) continue; // rename-old only
            latest[record.Id] = record;
        }
        var entries = snapshot.Entries.ToBuilder();
        foreach (var record in latest.Values)
        {
            token.ThrowIfCancellationRequested();
            if ((record.Reason & 0x200) != 0) entries.Remove(record.Id);
            else entries[record.Id] = metadata(record);
        }
        return snapshot with { Entries = entries.ToImmutable(), NextUsn = batch.NextUsn, Revision = snapshot.Revision + 1 };
    }

    private void Save(IndexSnapshot snapshot, CancellationToken token)
    {
        try { _storage.Save(snapshot, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"索引を保存できません（検索は継続）: {ex.Message}");
            Log(ex);
        }
    }

    public async Task StopAsync(bool sessionEnding)
    {
        _stop.Cancel();
        if (_worker is not null)
        {
            if (sessionEnding)
            {
                // Never delay Windows shutdown for an unbounded filesystem operation.
                if (await Task.WhenAny(_worker, Task.Delay(800)) != _worker) return;
            }
            else await _worker;
        }
        var snapshot = Snapshot;
        if (snapshot is null) return;
        using var timeout = new CancellationTokenSource(sessionEnding ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(20));
        var save = Task.Run(() => Save(snapshot, timeout.Token));
        try
        {
            if (sessionEnding) await save.WaitAsync(TimeSpan.FromMilliseconds(600));
            else await save;
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
    }

    public static void Log(Exception ex)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastSearch");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "error.log"), $"{DateTime.Now:O} {ex}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() { _stop.Dispose(); }
}
