using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FastSearch.Core;
using FastSearch.Native;

namespace FastSearch.Worker;

internal static class Program
{
    private static int Main(string[] args) => args.Length == 1 && args[0] == "--service" ? WindowsServiceHost.Run() : 2;
}

internal sealed class LocalIndexVolume : IIndexVolume
{
    private readonly NtfsVolume _volume = new();
    private readonly CancellationToken _token;
    internal LocalIndexVolume(CancellationToken token) => _token = token;
    public string Identity => _volume.Identity;
    public ulong RootId => _volume.RootId;
    public JournalState QueryJournal() => _volume.QueryJournal();
    public JournalBatch ReadJournal(ulong journalId, long startUsn) => _volume.ReadJournal(journalId, startUsn);
    public IEnumerable<IndexEntry> EnumerateEntries(CancellationToken token)
    {
        // Keep only one native enumeration block and one small attribute batch in this process.
        var block = new List<UsnRecord>(512);
        foreach (var record in _volume.EnumerateMft(token))
        {
            block.Add(record);
            if (block.Count == 512)
            {
                foreach (var entry in ReadMetadata(block, token)) yield return entry;
                block.Clear();
            }
        }
        if (block.Count != 0) foreach (var entry in ReadMetadata(block, token)) yield return entry;
    }
    public IReadOnlyList<IndexEntry> ReadMetadata(IReadOnlyList<UsnRecord> records) => ReadMetadata(records, _token);
    private IndexEntry[] ReadMetadata(IReadOnlyList<UsnRecord> records, CancellationToken token)
    {
        var entries = new IndexEntry[records.Count];
        Parallel.For(0, records.Count, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 4 },
            i => entries[i] = _volume.ReadMetadata(records[i]));
        return entries;
    }
    public void Dispose() => _volume.Dispose();
}
