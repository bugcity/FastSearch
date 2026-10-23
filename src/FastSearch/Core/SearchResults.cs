using System;
using System.Collections;
using System.Collections.Generic;

namespace FastSearch.Core;

public interface ISearchResults : IReadOnlyList<SearchResult>, IList, IDisposable
{
    new int Count { get; }
    new SearchResult this[int index] { get; }
    int Length { get; }
    SearchResult? FindById(ulong id);
}

internal readonly record struct SearchHit(EntryPosition Position, string Folder);

/// <summary>Complete compact result data, with a bounded cache of lazily created display models.</summary>
public sealed class SearchResults : ISearchResults
{
    private const int CacheSize = 512;
    private readonly SearchHit[] _hits;
    private readonly IndexTable _entries;
    private readonly SearchResult?[] _rows = new SearchResult?[CacheSize];
    private readonly int[] _indices = new int[CacheSize];
    public static SearchResults Empty { get; } = new(IndexTable.Empty, []);
    internal SearchResults(IndexTable entries, SearchHit[] hits) { _entries = entries; _hits = hits; }
    public int Count => _hits.Length;
    public int Length => Count;
    public void Dispose() { }
    public SearchResult this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_hits.Length) throw new ArgumentOutOfRangeException(nameof(index));
            int slot = index & (CacheSize - 1);
            lock (_rows)
            {
                if (_rows[slot] is { } existing && _indices[slot] == index) return existing;
                var hit = _hits[index];
                var row = new SearchResult(_entries.At(hit.Position), hit.Folder);
                _indices[slot] = index;
                _rows[slot] = row;
                return row;
            }
        }
    }
    public SearchResult? FindById(ulong id)
    {
        for (int i = 0; i < _hits.Length; i++) if (_entries.At(_hits[i].Position).Id == id) return this[i];
        return null;
    }
    public int IndexOf(object? value)
    {
        if (value is not SearchResult row) return -1;
        for (int i = 0; i < _hits.Length; i++)
            if (_entries.At(_hits[i].Position).Id == row.Id && _entries.At(_hits[i].Position) == row.Entry && _hits[i].Folder == row.Folder) return i;
        return -1;
    }
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator<SearchResult> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Rank != 1 || index < 0 || array.Length - index < Count) throw new ArgumentException("Invalid destination array.");
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }
    internal int CachedRowCount
    {
        get
        {
            lock (_rows)
            {
                int count = 0;
                foreach (var row in _rows) if (row is not null) count++;
                return count;
            }
        }
    }
}
