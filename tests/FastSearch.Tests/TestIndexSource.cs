using FastSearch.Core;

internal sealed class TestIndexSource(IndexSnapshot snapshot) : IIndexSource
{
    public IndexSnapshot Snapshot { get; } = snapshot;
    public event Action<string>? StatusChanged;
    public event Action? Changed;
    public void Notify() { StatusChanged?.Invoke("hidden-test-update"); Changed?.Invoke(); }
}
