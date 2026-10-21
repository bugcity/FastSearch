using System;
using System.Collections.Immutable;

namespace FastSearch.Core;

public sealed record IndexEntry(ulong Id, ulong ParentId, string Name, bool IsDirectory,
    long? Size, DateTime? ModifiedUtc);

public sealed record IndexSnapshot(string VolumeIdentity, ulong RootId, ulong JournalId,
    long NextUsn, ImmutableDictionary<ulong, IndexEntry> Entries, long Revision = 0);

public enum SortColumn { Name, Folder, Size, Modified }
public readonly record struct SortOrder(SortColumn Column, bool Descending = false);

public sealed record SearchResult(IndexEntry Entry, string Folder)
{
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
