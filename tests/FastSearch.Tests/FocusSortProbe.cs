using System.Collections.Immutable;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FastSearch.Core;

internal static class FocusSortProbe
{
    private sealed class ProbeApplication : FastSearch.App
    {
        // Pumping the dispatcher must not start the production tray/index/UAC flow.
        protected override void OnStartup(StartupEventArgs e) { }
    }
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            FastSearch.App? app = null;
            FastSearch.MainWindow? window = null;
            try
            {
                app = new ProbeApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var entries = new IndexEntry[]
                {
                    new(5,5,".",true,null,null), new(10,5,"z-folder",true,null,null), new(20,5,"a-folder",true,null,null),
                    new(11,10,"report-b.txt",false,200,DateTime.UnixEpoch.AddDays(2)),
                    new(12,20,"report-a.txt",false,10,DateTime.UnixEpoch),
                    new(13,10,"report-c.txt",false,50,DateTime.UnixEpoch.AddDays(1))
                };
                var snapshot = new IndexSnapshot("focus-test",5,1,0,entries.ToImmutableDictionary(x => x.Id));
                window = new FastSearch.MainWindow(new TestIndexSource(snapshot))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000, Top = -20000, Width = 800, Height = 480, ShowInTaskbar = false
                };
                window.Show();
                window.Activate();
                var grid = (DataGrid)window.FindName("Results");
                var search = (TextBox)window.FindName("SearchBox");
                search.Text = "report";
                PumpUntil(() => window.DisplayedResultCount == 3);
                Console.WriteLine("FOCUS SORT: initial search ready");
                using var source = new HwndSource(new HwndSourceParameters("SortFocusTest") { ParentWindow = new IntPtr(-3) });
                void AwaitSort(Action trigger)
                {
                    var previous = grid.ItemsSource;
                    trigger();
                    PumpUntil(() => !ReferenceEquals(previous, grid.ItemsSource));
                    var actual = grid.Items.Cast<SearchResult>().Select(x => x.Id);
                    var expected = SearchEngine.Search(snapshot, search.Text, window.CurrentSort, default).Select(x => x.Id);
                    if (!actual.SequenceEqual(expected)) throw new Exception("Published results did not use the active sort");
                }
                for (int i = 0; i < 4; i++)
                {
                    var column = (SortColumn)i;
                    Keyboard.Focus(search);
                    if (!search.IsKeyboardFocusWithin) throw new Exception("Cannot acquire textbox keyboard focus in this environment");
                    AwaitSort(() => FastSearch.MainWindow.SortByColumnCommand.Execute(column, search));
                    if (!search.IsKeyboardFocusWithin) throw new Exception("Sorting lost textbox focus");
                    search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    if (!grid.IsKeyboardFocusWithin) throw new Exception("Cannot acquire results keyboard focus");
                    ulong selected = ((SearchResult)grid.SelectedItem).Id;
                    AwaitSort(() => FastSearch.MainWindow.SortByColumnCommand.Execute(column, grid));
                    if (!grid.IsKeyboardFocusWithin || ((SearchResult)grid.SelectedItem).Id != selected)
                        throw new Exception("Sorting lost results focus or the selected file");
                    AwaitSort(() => TestInput.RaiseHeaderSort(grid, grid.Columns[i]));
                    if (!grid.IsKeyboardFocusWithin) throw new Exception("Header sort lost results focus");
                    Console.WriteLine($"FOCUS SORT: {column} preserves textbox/list focus and selected file");
                }
                AwaitSort(() =>
                {
                    FastSearch.MainWindow.SortByColumnCommand.Execute(SortColumn.Name, grid);
                    Keyboard.Focus(search);
                });
                if (!search.IsKeyboardFocusWithin) throw new Exception("Completed sort stole focus after user moved to search");
                Keyboard.Focus(search);
                var savedSort = window.CurrentSort;
                var oldResults = grid.ItemsSource;
                search.Text = "report-";
                PumpUntil(() => window.DisplayedResultCount == 3 && !ReferenceEquals(oldResults, grid.ItemsSource));
                if (window.CurrentSort != savedSort || !search.IsKeyboardFocusWithin) throw new Exception("Query update lost sort or focus");
                Console.WriteLine("FOCUS SORT: all 4 columns, textbox/list focus after async replacement, selected file, header parity and query persistence passed");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.StopSearch(); app?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Focus sort probe failed", failure);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("WPF sort did not complete");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => frame.Continue = false;
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }
}
