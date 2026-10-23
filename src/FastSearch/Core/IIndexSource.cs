using System;
using System.Threading;
using System.Threading.Tasks;

namespace FastSearch.Core;

public interface IIndexSource
{
    IndexSnapshot? Snapshot { get; }
    event Action<string>? StatusChanged;
    event Action? Changed;
    bool IsReady => Snapshot is not null;
    int EntryCount => Snapshot?.Entries.Count ?? 0;
    Task<ISearchResults> SearchAsync(string query, SortOrder sort, ulong? selectedId, CancellationToken token) =>
        Task.Run<ISearchResults>(() => Snapshot is { } snapshot ? SearchEngine.Search(snapshot, query, sort, token) : SearchResults.Empty, token);
}
