using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FastSearch.Core;

namespace FastSearch.Ipc;

/// <summary>Bounded page cache; the WPF indexer only schedules I/O and never waits for it.</summary>
internal sealed class PagedSearchResults : ISearchResults
{
    private sealed class Page(int start, int count)
    {
        internal readonly int Start = start;
        internal readonly SearchResult[] Rows = new SearchResult[count];
        internal bool Queued, Loaded;
        internal long Used;
    }
    private const int CachePages = 8;
    private readonly Stream _pipe;
    private readonly BinaryReader _reader;
    private readonly BinaryWriter _writer;
    private readonly SynchronizationContext _context;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Page> _queue = Channel.CreateBounded<Page>(8);
    private readonly Dictionary<int, Page> _pages = new();
    private long _clock;
    private int _selectedPage = -1;
    private int _disposed;
    internal event Action? ConnectionLost;
    public int Count { get; }
    public int Length => Count;
    internal int CachedRowCount => _pages.Values.Sum(p => p.Rows.Length);
    internal int CreatedRowCount { get; private set; }
    private PagedSearchResults(Stream pipe, int count, SynchronizationContext context)
    {
        _pipe = pipe; Count = count; _context = context;
        _reader = new(pipe, Encoding.UTF8, true); _writer = new(pipe, Encoding.UTF8, true);
    }
    internal static async Task<PagedSearchResults> CreateAsync(Stream pipe, string query, SortOrder sort, ulong? selectedId,
        SynchronizationContext context, CancellationToken token)
    {
        using var registration = token.Register(() => pipe.Dispose());
        using var reader = new BinaryReader(pipe, Encoding.UTF8, true);
        using var writer = new BinaryWriter(pipe, Encoding.UTF8, true);
        ServiceProtocol.WriteHeader(writer, 2);
        VolumeProtocol.WriteString(writer, query);
        writer.Write((int)sort.Column); writer.Write(sort.Descending); writer.Write(selectedId ?? 0); writer.Flush();
        VolumeProtocol.CheckResponse(reader);
        int count = reader.ReadInt32(), selected = reader.ReadInt32();
        if (count < 0 || count > ServiceProtocol.MaxResults || selected < -1 || selected >= count)
            throw new InvalidDataException("検索結果件数が不正です。");
        var results = new PagedSearchResults(pipe, count, context);
        if (selected >= 0) results._selectedPage = selected / ServiceProtocol.PageSize * ServiceProtocol.PageSize;
        try
        {
            if (count > 0) results.Prime(0);
            if (selected >= ServiceProtocol.PageSize) results.Prime(selected / ServiceProtocol.PageSize * ServiceProtocol.PageSize);
            token.ThrowIfCancellationRequested();
            _ = Task.Run(results.LoadPagesAsync);
            await Task.CompletedTask;
            return results;
        }
        catch { results.Dispose(); throw; }
    }
    private Page GetPage(int start)
    {
        if (!_pages.TryGetValue(start, out var page))
        {
            if (_pages.Count == CachePages)
            {
                // WPF may probe other positions while replacing ItemsSource. Keep the first
                // and explicitly restored selection pages stable during those probes.
                var oldest = _pages.Values.Where(p => p.Start != 0 && p.Start != _selectedPage).MinBy(p => p.Used)!;
                _pages.Remove(oldest.Start);
            }
            page = new Page(start, Math.Min(ServiceProtocol.PageSize, Count - start));
            CreatedRowCount += page.Rows.Length;
            for (int i = 0; i < page.Rows.Length; i++) page.Rows[i] = new SearchResult { Owner = this, ResultIndex = start + i };
            _pages.Add(start, page);
        }
        page.Used = ++_clock;
        return page;
    }
    private SearchResult[] ReadPage(Page page)
    {
        _writer.Write(page.Start); _writer.Flush();
        int count = _reader.ReadInt32();
        if (count != page.Rows.Length) throw new InvalidDataException("ページの件数が一致しません。");
        var rows = new SearchResult[count];
        for (int i = 0; i < count; i++) rows[i] = ServiceProtocol.ReadRow(_reader);
        return rows;
    }
    private void Prime(int start)
    {
        var page = GetPage(start);
        var rows = ReadPage(page);
        for (int i = 0; i < rows.Length; i++) page.Rows[i].Load(rows[i]);
        page.Loaded = true;
    }
    private async Task LoadPagesAsync()
    {
        try
        {
            await foreach (var page in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var rows = ReadPage(page);
                _context.Post(_ =>
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    for (int i = 0; i < rows.Length; i++) page.Rows[i].Load(rows[i]);
                    page.Loaded = true;
                }, null);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException)
        {
            if (Volatile.Read(ref _disposed) == 0)
                _context.Post(_ => { if (Volatile.Read(ref _disposed) == 0) ConnectionLost?.Invoke(); }, null);
        }
    }
    public SearchResult this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            var page = GetPage(index / ServiceProtocol.PageSize * ServiceProtocol.PageSize);
            if (!page.Loaded && !page.Queued && Volatile.Read(ref _disposed) == 0)
                page.Queued = _queue.Writer.TryWrite(page);
            return page.Rows[index - page.Start];
        }
    }
    public SearchResult? FindById(ulong id) => _pages.Values.SelectMany(p => p.Rows).FirstOrDefault(r => r.IsLoaded && r.Id == id);
    public int IndexOf(object? value) => value is SearchResult row && ReferenceEquals(row.Owner, this) ? row.ResultIndex : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator<SearchResult> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
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
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _queue.Writer.TryComplete(); _pipe.Dispose();
        _pages.Clear();
    }
}
