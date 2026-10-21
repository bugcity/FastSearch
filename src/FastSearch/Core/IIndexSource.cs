using System;

namespace FastSearch.Core;

public interface IIndexSource
{
    IndexSnapshot? Snapshot { get; }
    event Action<string>? StatusChanged;
    event Action? Changed;
}
