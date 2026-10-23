using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FastSearch.Core;
using FastSearch.Ipc;

internal static class ServiceUiProbe
{
    internal static void Run()
    {
        var entries = new List<IndexEntry> { new(5, 5, ".", true, null, null) };
        for (int i = 0; i < 300000; i++) entries.Add(new((ulong)(100 + i), 5, $"report_{i:D6}.txt", false, i, DateTime.UnixEpoch));
        using var backend = new ServiceProbe.Backend(new("service-ui", 5, 123, 100, IndexTable.FromEntries(entries)));
        using var index = new ServiceIndexSource(backend.ConnectAsync);
        index.Start();
        var window = new FastSearch.MainWindow(index)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 900, Height = 500, ShowInTaskbar = false };
        try
        {
            window.ShowSearch();
            var search = (TextBox)window.FindName("SearchBox");
            var grid = (DataGrid)window.FindName("Results");
            PumpUntil(() => index.IsReady && search.IsKeyboardFocusWithin);
            search.Text = "report";
            PumpUntil(() => window.DisplayedResultCount == 300000 && grid.ItemsSource is PagedSearchResults);
            window.UpdateLayout();
            var results = (PagedSearchResults)grid.ItemsSource;
            int rows = CountRows(grid);
            if (rows < 1 || rows > 100 || results.CachedRowCount > 1024 || results.CreatedRowCount > 4096)
                throw new Exception($"Paged UI was not virtualized: created={results.CreatedRowCount}, cached={results.CachedRowCount}");
            var target = results[7000];
            grid.ScrollIntoView(target); window.UpdateLayout();
            PumpUntil(() => target.IsLoaded);
            if (target.Name != "report_007000.txt") throw new Exception("Scrolled page did not update WPF row bindings");
            grid.SelectedItem = target;
            grid.CurrentCell = new DataGridCellInfo(target, grid.Columns[0]);
            grid.UpdateLayout();
            if (grid.ItemContainerGenerator.ContainerFromItem(target) is DataGridRow row)
                row.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            else throw new Exception("Selected virtual row was not realized");
            Console.WriteLine($"SERVICE UI BEFORE SORT: active={window.IsActive}, focus={grid.IsKeyboardFocusWithin}, selected={(grid.SelectedItem as SearchResult)?.Id}, current={(grid.CurrentItem as SearchResult)?.Id}");
            FastSearch.MainWindow.SortByColumnCommand.Execute(SortColumn.Size, window);
            PumpUntil(() => !ReferenceEquals(grid.ItemsSource, results) && window.DisplayedResultCount == 300000);
            if (!grid.IsKeyboardFocusWithin || grid.SelectedItem is not SearchResult current || current.Id != target.Id || !current.IsLoaded)
                throw new Exception($"Remote sort failed: active={window.IsActive}, focus={grid.IsKeyboardFocusWithin}, selected={(grid.SelectedItem as SearchResult)?.Id}, expected={target.Id}, loaded={(grid.SelectedItem as SearchResult)?.IsLoaded}");
            var beforeHide = (PagedSearchResults)grid.ItemsSource;
            window.Hide();
            if (window.DisplayedResultCount != 0 || beforeHide.CachedRowCount != 0) throw new Exception("Hidden UI retained result pages");
            window.ShowSearch();
            PumpUntil(() => search.IsKeyboardFocusWithin && window.DisplayedResultCount == 300000);
            Console.WriteLine($"SERVICE UI: 300,000 results, {rows} realized rows, initially {results.CreatedRowCount} row models, async scrolling, remote sorting/selection/focus and hide/reopen passed");
        }
        finally { window.StopSearch(); window.Hide(); index.StopAsync(false).GetAwaiter().GetResult(); }
        CheckPendingStatus();
    }
    private static void CheckPendingStatus()
    {
        var source = new PendingSource();
        var window = new FastSearch.MainWindow(source)
        { WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000,Width=900,Height=500,ShowInTaskbar=false };
        try
        {
            window.ShowSearch();
            var search=(TextBox)window.FindName("SearchBox");
            var hint=(TextBlock)window.FindName("EmptyHint");
            search.Text="report";
            const string error="索引サービスとの通信が応答の途中で切れました。";
            Task.Run(()=>source.Report(error)).GetAwaiter().GetResult();
            PumpUntil(()=>hint.Text==error);
            search.Text="";
            if(hint.Text!=error || hint.Visibility!=Visibility.Visible)
                throw new Exception("Connection errors were hidden behind the generic indexing prompt");
            Console.WriteLine("SERVICE STATUS: connection errors replace the generic center prompt, with and without a query");
        }
        finally{window.StopSearch();window.Hide();}
    }
    private sealed class PendingSource : IIndexSource
    {
        public IndexSnapshot? Snapshot=>null;
        public event Action<string>? StatusChanged;
        public event Action? Changed{add{}remove{}}
        internal void Report(string text)=>StatusChanged?.Invoke(text);
    }
    private static int CountRows(DependencyObject parent)
    {
        int count = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); count += child is DataGridRow ? 1 : CountRows(child); }
        return count;
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Paged WPF test timed out");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => frame.Continue = false;
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        }
    }
}
