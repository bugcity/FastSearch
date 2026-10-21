using System;
using System.Collections.Generic;
using System.Threading;

namespace FastSearch.Core;

public static class SearchEngine
{
    // A snapshot is immutable: indexing never holds a lock while a search runs.
    public static SearchResult[] Search(IndexSnapshot snapshot, string query, SortOrder order,
        CancellationToken cancellationToken)
    {
        var condition = new SearchQuery(query);
        if (!condition.IsValid) return [];
        var results = new List<SearchResult>();
        var paths = new Dictionary<ulong, string?> { [snapshot.RootId] = @"C:\" };
        var folderMatches = condition.HasHierarchy ? new Dictionary<ulong, bool>() : null;
        int visited = 0;
        foreach (var entry in snapshot.Entries.Values)
        {
            if ((visited++ & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!condition.MatchesName(entry)) continue;
            if (folderMatches is not null)
            {
                if (!folderMatches.TryGetValue(entry.ParentId, out bool matches))
                    folderMatches[entry.ParentId] = matches = condition.MatchesFolders(snapshot, entry.ParentId);
                if (!matches) continue;
            }
            string? parent = ResolveDirectory(snapshot, entry.ParentId, paths);
            if (parent is not null) results.Add(new SearchResult(entry, parent));
        }
        var array = results.ToArray();
        int comparisons = 0;
        try
        {
            Array.Sort(array, (a, b) =>
            {
                if ((comparisons++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                int value = order.Column switch
                {
                    SortColumn.Folder => StringComparer.OrdinalIgnoreCase.Compare(a.Folder, b.Folder),
                    SortColumn.Size => Nullable.Compare(a.Entry.IsDirectory ? null : a.Entry.Size,
                        b.Entry.IsDirectory ? null : b.Entry.Size),
                    SortColumn.Modified => Nullable.Compare(a.Entry.ModifiedUtc, b.Entry.ModifiedUtc),
                    _ => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name)
                };
                if (value != 0) return order.Descending ? -Math.Sign(value) : Math.Sign(value);
                return a.Id.CompareTo(b.Id);
            });
        }
        catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return array;
    }

    public static string? ResolveDirectory(IndexSnapshot snapshot, ulong id, Dictionary<ulong, string?> cache)
    {
        if (cache.TryGetValue(id, out var known)) return known;
        var chain = new List<IndexEntry>();
        var seen = new HashSet<ulong>();
        ulong current = id;
        string? path;
        while (!cache.TryGetValue(current, out path))
        {
            if (!seen.Add(current) || !snapshot.Entries.TryGetValue(current, out var node) || !node.IsDirectory)
            {
                path = null;
                break;
            }
            chain.Add(node);
            current = node.ParentId;
        }
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            path = path is null ? null : System.IO.Path.Combine(path, chain[i].Name);
            cache[chain[i].Id] = path;
        }
        cache[id] = path;
        return path;
    }
}
