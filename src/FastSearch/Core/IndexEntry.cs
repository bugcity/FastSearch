using System;
using System.ComponentModel;

namespace FastSearch.Core;

public readonly record struct IndexEntry
{
    public ulong Id { get; init; }
    public ulong ParentId { get; init; }
    public string Name { get; init; }
    private readonly long _size, _modifiedAndDirectory;
    public bool IsDirectory
    {
        get => (_modifiedAndDirectory & long.MinValue) != 0;
        init => _modifiedAndDirectory = (_modifiedAndDirectory & long.MaxValue) | (value ? long.MinValue : 0);
    }
    public long? Size { get => _size < 0 ? null : _size; init => _size = value ?? -1; }
    public DateTime? ModifiedUtc
    {
        get => (_modifiedAndDirectory & long.MaxValue) is long ticks && ticks != 0
            ? new DateTime(ticks - 1, DateTimeKind.Utc) : null;
        init => _modifiedAndDirectory = (_modifiedAndDirectory & long.MinValue) | (value?.Ticks + 1 ?? 0);
    }
    public IndexEntry(ulong id, ulong parentId, string name, bool isDirectory, long? size, DateTime? modifiedUtc)
    {
        Id = id; ParentId = parentId; Name = name;
        _size = -1; _modifiedAndDirectory = 0; IsDirectory = isDirectory;
        Size = size; ModifiedUtc = modifiedUtc;
    }
}

public sealed record IndexSnapshot(string VolumeIdentity, ulong RootId, ulong JournalId,
    long NextUsn, IndexTable Entries, long Revision = 0);

public enum SortColumn { Name, Folder, Size, Modified }
public readonly record struct SortOrder(SortColumn Column, bool Descending = false);

public sealed class SearchResult : INotifyPropertyChanged
{
    public IndexEntry Entry { get; private set; }
    public string Folder { get; private set; }
    public bool IsLoaded { get; private set; }
    internal object? Owner { get; set; }
    internal int ResultIndex { get; set; } = -1;
    public event PropertyChangedEventHandler? PropertyChanged;
    public SearchResult(IndexEntry entry, string folder) { Entry = entry; Folder = folder; IsLoaded = true; }
    internal SearchResult() { Entry = new(0, 0, "読み込み中…", false, null, null); Folder = ""; }
    internal void Load(SearchResult value)
    {
        Entry = value.Entry; Folder = value.Folder; IsLoaded = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    public string Name => Entry.Name;
    public ulong Id => Entry.Id;
    public string FullPath => System.IO.Path.Combine(Folder, Name);
    public string SizeText => Entry.IsDirectory ? "" : FormatSize(Entry.Size);
    public string ModifiedText => Entry.ModifiedUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "";
    public static string FormatSize(long? size)
    {
        if (size is null) return "";
        double value = size.Value;
        int unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:N0} {SizeUnits[unit]}" : $"{value:N1} {SizeUnits[unit]}";
    }
}
