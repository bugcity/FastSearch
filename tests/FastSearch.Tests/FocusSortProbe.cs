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
                var snapshot = new IndexSnapshot("focus-test",5,1,0,IndexTable.FromEntries(entries));
                window = new FastSearch.MainWindow(new TestIndexSource(snapshot), initialSort: new(SortColumn.Modified, true))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000, Top = -20000, Width = 800, Height = 480, ShowInTaskbar = false
                };
                window.Show();
                window.Activate();
                var grid = (DataGrid)window.FindName("Results");
                var search = (TextBox)window.FindName("SearchBox");
                int sortNotifications = 0;
                window.SortOrderChanged += _ => sortNotifications++;
                if (grid.Columns[3].Header as string != "更新日時 ↓" || window.CurrentSort != new SortOrder(SortColumn.Modified, true))
                    throw new Exception("Saved sort was not restored before the first search");
                Console.WriteLine("SORT RESTORE: saved column/direction are applied before the first search");
                search.Text = "report";
                PumpUntil(() => window.DisplayedResultCount == 3);
                PumpUntil(() => search.IsKeyboardFocusWithin);
                if (Keyboard.FocusedElement != search) throw new Exception("First window display did not acquire real keyboard focus in search");
                Console.WriteLine("INITIAL FOCUS: first display receives actual keyboard focus without a click");
                Console.WriteLine("FOCUS SORT: initial search ready");
                using var source = new HwndSource(new HwndSourceParameters("SortFocusTest") { ParentWindow = new IntPtr(-3) });
                void AwaitSort(Action trigger)
                {
                    var previous = grid.ItemsSource;
                    int oldNotifications = sortNotifications;
                    trigger();
                    PumpUntil(() => !ReferenceEquals(previous, grid.ItemsSource));
                    var actual = grid.Items.Cast<SearchResult>().Select(x => x.Id);
                    var expected = SearchEngine.Search(snapshot, search.Text, window.CurrentSort, default).Select(x => x.Id);
                    if (!actual.SequenceEqual(expected)) throw new Exception("Published results did not use the active sort");
                    if (sortNotifications != oldNotifications + 1) throw new Exception("Sort change was not published for persistence");
                    for (int columnIndex = 0; columnIndex < 4; columnIndex++)
                    {
                        string title = grid.Columns[columnIndex].Header as string ?? "";
                        string arrow = window.CurrentSort.Descending ? " ↓" : " ↑";
                        bool activeColumn = columnIndex == (int)window.CurrentSort.Column;
                        if (activeColumn ? !title.EndsWith(arrow) : title.Contains('↑') || title.Contains('↓'))
                            throw new Exception("Column header does not display exactly the active sort direction");
                    }
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
                foreach (UIElement target in new UIElement[] { search, grid })
                {
                    if (target == grid)
                        search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    target.RaiseEvent(escape);
                    if (!escape.Handled || window.IsVisible || window.DisplayedResultCount != 0 || app.IsExiting ||
                        search.Text != "report-" || window.CurrentSort != savedSort)
                        throw new Exception("Esc did not hide to tray while retaining query, sort and process lifetime");
                    window.ShowSearch();
                    PumpUntil(() => window.DisplayedResultCount == 3);
                    if (!search.IsKeyboardFocusWithin) throw new Exception("Returning from Esc did not focus search");
                }
                Console.WriteLine("ESC: textbox/list hide without exit, release results, preserve query/sort and resume search on show");
                foreach (string literal in new[] { "_", "=" })
                {
                    Keyboard.Focus(search);
                    search.SelectAll();
                    TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, search, literal));
                    if (search.Text != literal) throw new Exception($"Text input {literal} was replaced by the application");
                }
                Console.WriteLine("TEXT INPUT: underscore and equals are preserved as separate characters");
                SettingsUiProbe.Run();
                bool residentMode = true;
                int exitRequests = 0, settingsRequests = 0;
                FastSearch.MainWindow? lifecycle = null;
                lifecycle = new FastSearch.MainWindow(new TestIndexSource(snapshot),
                    keepRunningInTray: () => residentMode,
                    requestExit: () => { exitRequests++; lifecycle!.Hide(); })
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                    Width = 800, Height = 480, ShowInTaskbar = false
                };
                lifecycle.SettingsRequested += () => settingsRequests++;
                lifecycle.ShowSearch();
                PumpUntil(() => ((TextBox)lifecycle.FindName("SearchBox")).IsKeyboardFocusWithin);
                ((Button)lifecycle.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (settingsRequests != 1) throw new Exception("Search window settings button did not open settings");
                lifecycle.Close();
                if (lifecycle.IsVisible || exitRequests != 0) throw new Exception("Resident close must hide without requesting exit");
                residentMode = false;
                lifecycle.ShowSearch();
                lifecycle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                if (exitRequests != 1) throw new Exception("Non-resident Esc did not request normal shutdown");
                lifecycle.ShowSearch();
                lifecycle.Close();
                if (exitRequests != 2) throw new Exception("Non-resident close did not request normal shutdown");
                lifecycle.StopSearch();
                Console.WriteLine("RESIDENCY: live setting changes, resident close hides, non-resident Esc/close request shutdown, settings remain accessible");
                ThemeProbe.Run();
                ServiceUiProbe.Run();
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
