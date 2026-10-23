using System;
using System.Collections.Generic;
using System.Threading;
using FastSearch.Native;

namespace FastSearch.Core;

/// <summary>The privileged C: reader. No search index is retained by its implementation.</summary>
public interface IIndexVolume : IDisposable
{
    string Identity { get; }
    ulong RootId { get; }
    JournalState QueryJournal();
    IEnumerable<IndexEntry> EnumerateEntries(CancellationToken token);
    JournalBatch ReadJournal(ulong journalId, long startUsn);
    IReadOnlyList<IndexEntry> ReadMetadata(IReadOnlyList<UsnRecord> records);
}
