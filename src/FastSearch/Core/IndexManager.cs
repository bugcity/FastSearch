using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastSearch.Native;

namespace FastSearch.Core;

public sealed class IndexManager : IIndexSource, IDisposable
{
    internal static string? LogDirectoryOverride { get; set; }
    private readonly IndexStorage _storage;
    private readonly CancellationTokenSource _stop = new();
    private IndexSnapshot? _snapshot;
    private Task? _worker;
    private readonly Func<CancellationToken, IIndexVolume> _openVolume;
    private readonly string _openingStatus;
    public IndexSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public event Action<string>? StatusChanged;
    public event Action? Changed;
    public IndexManager(string storagePath) : this(storagePath, _ => throw new InvalidOperationException("索引ボリュームが指定されていません。")) { }
    internal IndexManager(string storagePath, Func<CancellationToken, IIndexVolume> openVolume,
        string openingStatus = "索引ボリュームに接続しています…")
    { _storage = new(storagePath); _openVolume = openVolume; _openingStatus = openingStatus; }
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
            Status(_openingStatus);
            using var volume = _openVolume(token);
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
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Status($"索引更新を停止しました: {ex.Message}");
            Log(ex);
        }
    }

    private IndexSnapshot Recover(IIndexVolume volume, IndexSnapshot? candidate, CancellationToken token)
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

    private IndexSnapshot Build(IIndexVolume volume, CancellationToken token)
    {
        // If wrap happens during a long build, discard it and start with a new checkpoint.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var start = volume.QueryJournal();
            Status("C:\\ のMFTを読み込んでいます…");
            var entries = IndexTable.CreateBulkBuilder(rejectDuplicates: false);
            foreach (var entry in volume.EnumerateEntries(token))
            {
                entries.Add(entry);
                if (entries.Count % 25_000 == 0) Status($"MFT・属性読み込み中: {entries.Count:N0} 件");
            }
            var snapshot = new IndexSnapshot(volume.Identity, volume.RootId, start.Id, start.NextUsn,
                entries.ToImmutable(token), (Snapshot?.Revision ?? 0) + 1);
            if (volume.QueryJournal().CanResume(snapshot, volume.Identity)) return snapshot;
        }
        throw new IOException("索引構築中にUSN Journalが繰り返し切り替わりました。Journalの容量を確認して再起動してください。");
    }

    private IndexSnapshot CatchUp(IIndexVolume volume, IndexSnapshot snapshot, CancellationToken token)
    {
        var state = volume.QueryJournal();
        if (!state.CanResume(snapshot, volume.Identity)) throw new JournalUnavailableException("Journalの継続位置が無効です。");
        long target = state.NextUsn;
        while (snapshot.NextUsn < target)
        {
            token.ThrowIfCancellationRequested();
            var batch = volume.ReadJournal(snapshot.JournalId, snapshot.NextUsn);
            if (batch.NextUsn <= snapshot.NextUsn) throw new JournalUnavailableException("Journalの継続位置が進みません。");
            // Coalesce before IPC so each live file's attributes are requested only once.
            var latest = new Dictionary<ulong, UsnRecord>();
            foreach (var record in batch.Records)
            {
                token.ThrowIfCancellationRequested();
                if ((record.Reason & 0x1000) != 0 && (record.Reason & ~0x80001000u) == 0) continue;
                latest[record.Id] = record;
            }
            var attributes = volume.ReadMetadata(latest.Values.Where(r => (r.Reason & 0x200) == 0).ToArray())
                .ToDictionary(entry => entry.Id);
            snapshot = ApplyBatch(snapshot, batch, record => attributes[record.Id] with { Name = record.Name }, token);
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
            else
            {
                var current = record;
                if (snapshot.Entries.TryGetValue(record.Id, out var previous) && previous.Name == record.Name)
                    current = record with { Name = previous.Name };
                entries[record.Id] = metadata(current);
            }
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
            string folder = LogDirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastSearch");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "error.log"), $"{DateTime.Now:O} {ex}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
