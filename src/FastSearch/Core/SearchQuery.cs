using System;

namespace FastSearch.Core;

/// <summary>Parsed once per search; folder conditions end at the file's immediate parent.</summary>
public sealed class SearchQuery
{
    private readonly string[] _parts;
    private readonly int _firstFolder;
    public bool IsValid { get; }
    public bool HasHierarchy { get; }
    public bool ExactFirstFolder { get; }
    public string FileCondition => _parts[^1];

    public SearchQuery(string text)
    {
        HasHierarchy = text.Contains('/');
        ExactFirstFolder = text.StartsWith('/');
        _parts = text.Split('/');
        _firstFolder = ExactFirstFolder ? 1 : 0;
        IsValid = text.Length > 0 && text != "/";
        // A trailing slash is a useful empty filename condition; an empty folder is invalid.
        for (int i = _firstFolder; i < _parts.Length - 1; i++)
            if (_parts[i].Length == 0) IsValid = false;
    }

    public bool MatchesName(IndexEntry entry) => IsValid && !entry.IsDirectory
        && entry.Name.Contains(FileCondition, StringComparison.OrdinalIgnoreCase);

    public bool MatchesFolders(IndexSnapshot snapshot, ulong parentId)
    {
        for (int i = _parts.Length - 2; i >= _firstFolder; i--)
        {
            if (parentId == snapshot.RootId || !snapshot.Entries.TryGetValue(parentId, out var folder) || !folder.IsDirectory)
                return false;
            bool matches = i == _firstFolder && !ExactFirstFolder
                ? folder.Name.EndsWith(_parts[i], StringComparison.OrdinalIgnoreCase)
                : folder.Name.Equals(_parts[i], StringComparison.OrdinalIgnoreCase);
            if (!matches) return false;
            parentId = folder.ParentId;
        }
        return true;
    }
}
