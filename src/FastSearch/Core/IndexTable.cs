using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace FastSearch.Core;

internal readonly record struct EntryPosition(int Shard, int Offset);

/// <summary>Packed immutable rows. Only shards changed by an update are rebuilt.</summary>
public sealed class IndexTable : IReadOnlyDictionary<ulong, IndexEntry>
{
    private const int ShardCount = 4096;
    private static readonly IComparer<IndexEntry> ById = Comparer<IndexEntry>.Create(static (a, b) => a.Id.CompareTo(b.Id));
    private readonly IndexEntry[]?[] _shards;
    private readonly int[] _counts;
    public int Count { get; }
    private IndexTable(IndexEntry[]?[] shards, int[] counts, int count) { _shards = shards; _counts = counts; Count = count; }
    public static IndexTable Empty { get; } = new(new IndexEntry[]?[ShardCount], new int[ShardCount], 0);
    private static int Shard(ulong id) => (int)((id ^ (id >> 32)) & (ShardCount - 1));
    private static int Find(IndexEntry[]? entries, int count, ulong id)
    {
        int lower = 0, upper = count - 1;
        while (lower <= upper)
        {
            int middle = lower + (upper - lower) / 2;
            ulong found = entries![middle].Id;
            if (found == id) return middle;
            if (found < id) lower = middle + 1;
            else upper = middle - 1;
        }
        return -1;
    }
    public IndexEntry this[ulong key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();
    public bool ContainsKey(ulong key) => Find(_shards[Shard(key)], _counts[Shard(key)], key) >= 0;
    public bool TryGetValue(ulong key, out IndexEntry value)
    {
        var rows = _shards[Shard(key)];
        int offset = Find(rows, _counts[Shard(key)], key);
        value = offset >= 0 ? rows![offset] : default;
        return offset >= 0;
    }
    internal ref readonly IndexEntry At(EntryPosition position) => ref _shards[position.Shard]![position.Offset];
    internal IEnumerable<(EntryPosition Position, IndexEntry Entry)> PositionedEntries
    {
        get
        {
            for (int shard = 0; shard < _shards.Length; shard++)
                if (_shards[shard] is { } rows)
                    for (int offset = 0; offset < _counts[shard]; offset++)
                        yield return (new(shard, offset), rows[offset]);
        }
    }
    public IEnumerable<ulong> Keys { get { foreach (var entry in Values) yield return entry.Id; } }
    public IEnumerable<IndexEntry> Values
    {
        get
        {
            for (int shard = 0; shard < _shards.Length; shard++)
                if (_shards[shard] is { } rows)
                    for (int i = 0; i < _counts[shard]; i++) yield return rows[i];
        }
    }
    public IEnumerator<KeyValuePair<ulong, IndexEntry>> GetEnumerator()
    { foreach (var entry in Values) yield return new(entry.Id, entry); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public static Builder CreateBuilder() => new(Empty);
    public static BulkBuilder CreateBulkBuilder(int expectedCount = 0, bool rejectDuplicates = true) => new(expectedCount, rejectDuplicates);
    public Builder ToBuilder() => new(this);
    public static IndexTable FromEntries(IEnumerable<IndexEntry> entries)
    {
        int expected = entries is ICollection<IndexEntry> collection ? collection.Count :
            entries is IReadOnlyCollection<IndexEntry> readOnly ? readOnly.Count : 0;
        var builder = CreateBulkBuilder(expected);
        foreach (var entry in entries) builder.Add(entry);
        return builder.ToImmutable();
    }
    public IndexTable SetItem(ulong key, IndexEntry value)
    {
        var builder = ToBuilder(); builder[key] = value;
        return builder.ToImmutable();
    }

    public sealed class Builder
    {
        private IndexTable _source;
        private readonly Dictionary<ulong, IndexEntry>?[] _changes = new Dictionary<ulong, IndexEntry>?[ShardCount];
        private readonly IndexNamePool _names = new();
        public int Count { get; private set; }
        internal Builder(IndexTable source) { _source = source; Count = source.Count; }
        private Dictionary<ulong, IndexEntry> Writable(int shard)
        {
            if (_changes[shard] is { } changed) return changed;
            var rows = _source._shards[shard];
            var writable = new Dictionary<ulong, IndexEntry>(_source._counts[shard]);
            if (rows is not null)
                for (int i = 0; i < _source._counts[shard]; i++) writable.Add(rows[i].Id, rows[i]);
            return _changes[shard] = writable;
        }
        public bool ContainsKey(ulong key) => _changes[Shard(key)] is { } changed
            ? changed.ContainsKey(key) : _source.ContainsKey(key);
        private IndexEntry ShareName(IndexEntry entry) => entry with { Name = _names.Share(entry.Name) };
        public IndexEntry this[ulong key]
        {
            get => _changes[Shard(key)] is { } changed ? changed[key] : _source[key];
            set
            {
                if (value.Id != key) throw new ArgumentException("Entry ID must match its key.");
                var shard = Writable(Shard(key));
                if (!shard.ContainsKey(key)) Count++;
                shard[key] = ShareName(value);
            }
        }
        public void Add(ulong key, IndexEntry value)
        {
            if (value.Id != key) throw new ArgumentException("Entry ID must match its key.");
            Writable(Shard(key)).Add(key, ShareName(value)); Count++;
        }
        public bool Remove(ulong key)
        {
            if (!ContainsKey(key)) return false;
            bool removed = Writable(Shard(key)).Remove(key);
            if (removed) Count--;
            return removed;
        }
        public IndexTable ToImmutable()
        {
            var shards = (IndexEntry[]?[])_source._shards.Clone();
            var counts = (int[])_source._counts.Clone();
            for (int shard = 0; shard < ShardCount; shard++)
            {
                if (_changes[shard] is not { } changed) continue;
                var rows = new IndexEntry[changed.Count];
                changed.Values.CopyTo(rows, 0);
                Array.Sort(rows, ById);
                shards[shard] = rows.Length == 0 ? null : rows;
                counts[shard] = rows.Length;
                _changes[shard] = null;
            }
            return _source = new IndexTable(shards, counts, Count);
        }
    }

    /// <summary>Initial load streams directly into its final arrays, without per-file hash tables.</summary>
    public sealed class BulkBuilder
    {
        private readonly IndexEntry[]?[] _rows = new IndexEntry[]?[ShardCount];
        private readonly int[] _counts = new int[ShardCount];
        private readonly IndexNamePool _names = new();
        private readonly int _initialCapacity;
        private readonly bool _rejectDuplicates;
        private bool _complete;
        public int Count { get; private set; }
        internal BulkBuilder(int expectedCount, bool rejectDuplicates)
        {
            if (expectedCount < 0 || expectedCount > 20_000_000) throw new ArgumentOutOfRangeException(nameof(expectedCount));
            _initialCapacity = expectedCount == 0 ? 64 : Math.Max(4, (expectedCount + ShardCount - 1) / ShardCount);
            _rejectDuplicates = rejectDuplicates;
        }
        public void Add(IndexEntry entry)
        {
            if (_complete) throw new InvalidOperationException("A published bulk builder cannot be reused.");
            if (Count == 20_000_000) throw new InvalidOperationException("Index entry limit exceeded.");
            int shard = Shard(entry.Id), offset = _counts[shard];
            var rows = _rows[shard] ??= new IndexEntry[_initialCapacity];
            if (offset == rows.Length)
            {
                Array.Resize(ref rows, checked(rows.Length * 2));
                _rows[shard] = rows;
            }
            rows[offset] = entry with { Name = _names.Share(entry.Name) };
            _counts[shard]++; Count++;
        }
        public IndexTable ToImmutable(CancellationToken token = default)
        {
            if (_complete) throw new InvalidOperationException("A published bulk builder cannot be reused.");
            _complete = true;
            int total = 0;
            for (int shard = 0; shard < ShardCount; shard++)
            {
                token.ThrowIfCancellationRequested();
                if (_rows[shard] is not { } rows) continue;
                int count = _counts[shard];
                Array.Sort(rows, 0, count, ById);
                int unique = 0;
                for (int i = 0; i < count; i++)
                {
                    if (unique != 0 && rows[unique - 1].Id == rows[i].Id)
                    {
                        if (_rejectDuplicates) throw new ArgumentException("Duplicate index ID.");
                        rows[unique - 1] = rows[i];
                    }
                    else rows[unique++] = rows[i];
                }
                Array.Clear(rows, unique, count - unique);
                if (unique < rows.Length * 3 / 4) Array.Resize(ref rows, unique);
                _rows[shard] = rows; _counts[shard] = unique;
                total += unique;
            }
            Count = total;
            return new IndexTable(_rows, _counts, total);
        }
    }
}
