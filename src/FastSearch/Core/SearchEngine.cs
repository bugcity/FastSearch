using System;
using System.Collections.Generic;
using System.Threading;

namespace FastSearch.Core;

public static class SearchEngine
{
    // A snapshot is immutable: indexing never holds a lock while a search runs.
    public static SearchResults Search(IndexSnapshot snapshot, string query, SortOrder order,
        CancellationToken cancellationToken)
    {
        var condition = new SearchQuery(query);
        if (!condition.IsValid) return SearchResults.Empty;
        var results = new HitBuffer();
        var paths = new Dictionary<ulong, string?> { [snapshot.RootId] = @"C:\" };
        var folderMatches = condition.HasHierarchy ? new Dictionary<ulong, bool>() : null;
        int visited = 0;
        foreach (var (position, entry) in snapshot.Entries.PositionedEntries)
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
            if (parent is not null) results.Add(new SearchHit(position, parent));
        }
        var array = results.ToArray(cancellationToken);
        int comparisons = 0;
        try
        {
            Array.Sort(array, (a, b) =>
            {
                if ((comparisons++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                ref readonly var left = ref snapshot.Entries.At(a.Position);
                ref readonly var right = ref snapshot.Entries.At(b.Position);
                int value = order.Column switch
                {
                    SortColumn.Folder => StringComparer.OrdinalIgnoreCase.Compare(a.Folder, b.Folder),
                    SortColumn.Size => Nullable.Compare(left.IsDirectory ? null : left.Size,
                        right.IsDirectory ? null : right.Size),
                    SortColumn.Modified => Nullable.Compare(left.ModifiedUtc, right.ModifiedUtc),
                    _ => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name)
                };
                if (value != 0) return order.Descending ? -Math.Sign(value) : Math.Sign(value);
                return left.Id.CompareTo(right.Id);
            });
        }
        catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new SearchResults(snapshot.Entries, array);
    }

    private sealed class HitBuffer
    {
        private const int BlockSize = 1024;
        private readonly List<SearchHit[]> _blocks = new();
        private int _count;
        public void Add(SearchHit hit)
        {
            int offset = _count % BlockSize;
            if (offset == 0) _blocks.Add(new SearchHit[BlockSize]);
            _blocks[^1][offset] = hit;
            _count++;
        }
        public SearchHit[] ToArray(CancellationToken token)
        {
            var hits = new SearchHit[_count];
            int copied = 0;
            foreach (var block in _blocks)
            {
                token.ThrowIfCancellationRequested();
                int length = Math.Min(BlockSize, _count - copied);
                Array.Copy(block, 0, hits, copied, length);
                copied += length;
            }
            _blocks.Clear();
            _count = 0;
            return hits;
        }
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
