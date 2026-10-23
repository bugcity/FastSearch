using System;

namespace FastSearch.Core;

// Bounded, operation-local sharing. Unlike string.Intern, this never roots names for process lifetime.
internal sealed class IndexNamePool
{
    private readonly string?[] _names = new string?[8192];
    public string Share(string name)
    {
        int slot = StringComparer.Ordinal.GetHashCode(name) & (_names.Length - 1);
        string? previous = _names[slot];
        if (string.Equals(previous, name, StringComparison.Ordinal)) return previous!;
        _names[slot] = name;
        return name;
    }
}
