using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FastSearch.Core;
using FastSearch.Diagnostics;

internal static class MemoryProfile
{
    public static void Run(string path, int fileCount = 300000, bool uniqueNames = false, int cycles = 30)
    {
        if (fileCount < 1 || fileCount > 20_000_000 || cycles < 1 || cycles > 1000) throw new ArgumentOutOfRangeException(nameof(fileCount));
        using var diagnostics = new MemoryDiagnostics(path);
        diagnostics.Record("startup-synthetic");
        var index = CreateIndex(fileCount, uniqueNames);
        diagnostics.SetIndexCount(index.Entries.Count);
        diagnostics.Record("index-before-collection-synthetic");
        Collect();
        diagnostics.Record("index-ready-synthetic");
        diagnostics.Record("searching-synthetic");
        SearchResults? results;
        using (diagnostics.SampleDuringSearch("searching-synthetic"))
            results = SearchEngine.Search(index, "file-", new(SortColumn.Name), default);
        diagnostics.Record("results-materialized-synthetic", results.Length);
        ShowUiPhase(results, diagnostics);
        results = null;
        Collect();
        diagnostics.Record("resident-after-release-synthetic");
        for (int i = 1; i <= cycles; i++)
        {
            SearchAndRelease(index, fileCount);
            Collect();
            diagnostics.Record($"resident-cycle-{i:D2}-synthetic");
        }
        diagnostics.Record("profile-complete-synthetic");
        Console.WriteLine($"MEMORY PROFILE: {index.Entries.Count:N0} indexed entries, {fileCount:N0} search results, {cycles} search/release cycles, unique={uniqueNames}");
        Console.WriteLine($"Private Bytes samples: {path}");
        GC.KeepAlive(index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IndexSnapshot CreateIndex(int fileCount, bool uniqueNames)
    {
        var entries = IndexTable.CreateBulkBuilder(fileCount + 1001);
        entries.Add(new(5, 5, ".", true, null, null));
        ulong id = 100;
        int remaining = fileCount;
        for (int folder = 0; folder < 1000 && remaining != 0; folder++)
        {
            ulong parent = id++;
            entries.Add(new(parent, 5, $"folder-{folder:D4}", true, null, null));
            int count = Math.Min((fileCount + 999) / 1000, remaining);
            for (int file = 0; file < count; file++)
            {
                ulong fileId = id++;
                string name = uniqueNames ? $"file-{fileId:D12}.txt" : $"file-{file:D4}.txt";
                entries.Add(new(fileId, parent, name, false, file * 10, DateTime.UnixEpoch));
            }
            remaining -= count;
        }
        return new("synthetic", 5, 1, 0, entries.ToImmutable());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SearchAndRelease(IndexSnapshot index, int fileCount)
    {
        var results = SearchEngine.Search(index, "file-", new(SortColumn.Size, true), default);
        if (results.Length != fileCount) throw new Exception("Profile search returned the wrong count");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ShowUiPhase(SearchResults results, MemoryDiagnostics diagnostics)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new FastSearch.App();
                app.InitializeComponent();
                using var manager = new IndexManager(System.IO.Path.Combine(AppContext.BaseDirectory, "unused-profile-index.bin"));
                var window = new FastSearch.MainWindow(manager);
                var grid = (DataGrid)window.FindName("Results");
                grid.ItemsSource = results;
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1080, 640));
                root.Arrange(new Rect(0, 0, 1080, 640));
                root.UpdateLayout();
                int rows = CountRows(grid);
                if (rows == 0 || rows > 200) throw new Exception("Virtualized row count is outside expected bounds");
                diagnostics.Record("large-results-ui-synthetic", results.Length, rows);
                window.StopSearch();
                diagnostics.Record("ui-results-cleared-synthetic");
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static int CountRows(DependencyObject parent)
    {
        int count = parent is DataGridRow ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) count += CountRows(VisualTreeHelper.GetChild(parent, i));
        return count;
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
