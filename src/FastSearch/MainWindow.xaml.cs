using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FastSearch.Core;
using FastSearch.Diagnostics;
using FastSearch.Native;

namespace FastSearch;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand SortByColumnCommand = new("列でソート", nameof(SortByColumnCommand), typeof(MainWindow));
    private readonly IIndexSource _index;
    private readonly MemoryDiagnostics? _memory;
    private SortOrder _sort = new(SortColumn.Name);
    private CancellationTokenSource? _searchCancellation;
    private int _generation;
    private bool _searchRunning, _shellMenuOpen;
    private volatile bool _refreshPending, _closed;
    private int _uiActive, _statusQueued, _changeQueued, _displayedResultCount;
    private string _latestStatus = "索引を準備しています…";
    private readonly FilePointerGesture _gesture = new();
    private SearchResult? _pressedItem;
    private bool _deferredSelection;
    public int DisplayedResultCount => Volatile.Read(ref _displayedResultCount);
    internal SortOrder CurrentSort => _sort;

    public MainWindow(IIndexSource index, MemoryDiagnostics? memory = null)
    {
        _index = index;
        _memory = memory;
        InitializeComponent();
        Results.Columns[0].SortDirection = ListSortDirection.Ascending;
        CommandBindings.Add(new CommandBinding(SortByColumnCommand, (_, e) =>
        {
            if (e.Parameter is SortColumn column) ChangeSort(column);
            e.Handled = true;
        }));
        for (int i = 0; i < 4; i++)
        {
            var column = (SortColumn)i;
            InputBindings.Add(new KeyBinding(SortByColumnCommand, new KeyGesture(Key.D1 + i, ModifierKeys.Control)) { CommandParameter = column });
            InputBindings.Add(new KeyBinding(SortByColumnCommand, new KeyGesture(Key.NumPad1 + i, ModifierKeys.Control)) { CommandParameter = column });
        }
        _index.StatusChanged += OnStatus;
        _index.Changed += OnIndexChanged;
        Loaded += (_, _) => SearchBox.Focus();
        IsVisibleChanged += (_, _) => UpdateVisibility();
        StateChanged += (_, _) => UpdateVisibility();
        Deactivated += (_, _) => ResetPointer();
    }

    private void OnStatus(string text)
    {
        Volatile.Write(ref _latestStatus, text);
        if (_closed || Volatile.Read(ref _uiActive) == 0 || Interlocked.Exchange(ref _statusQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _statusQueued, 0);
            if (!_closed && Volatile.Read(ref _uiActive) != 0) IndexStatus.Text = Volatile.Read(ref _latestStatus);
        });
    }
    private void OnIndexChanged()
    {
        if (_closed) return;
        if (Volatile.Read(ref _uiActive) == 0) { _refreshPending = true; return; }
        if (Interlocked.Exchange(ref _changeQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _changeQueued, 0);
            if (_closed) return;
            if (Volatile.Read(ref _uiActive) == 0 || _searchRunning || _shellMenuOpen) { _refreshPending = true; return; }
            QueueSearch(false);
        });
    }

    private void UpdateVisibility()
    {
        bool active = IsVisible && WindowState != WindowState.Minimized && !_closed;
        if (Interlocked.Exchange(ref _uiActive, active ? 1 : 0) == (active ? 1 : 0)) return;
        if (active)
        {
            IndexStatus.Text = Volatile.Read(ref _latestStatus);
            _memory?.Record("window-visible");
            QueueSearch(false);
        }
        else
        {
            ++_generation;
            _searchCancellation?.Cancel();
            _searchCancellation = null;
            _searchRunning = false;
            ResetPointer();
            ReleaseResults();
            _memory?.Record("resident");
        }
    }

    private void ReleaseResults()
    {
        Results.SelectedItems.Clear();
        Results.CurrentCell = default;
        Results.ItemsSource = Array.Empty<SearchResult>();
        Volatile.Write(ref _displayedResultCount, 0);
        ResultStatus.Text = "0 件";
    }

    public void ShowSearch()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        FocusSearch(true);
    }

    private void FocusSearch(bool selectAll = false)
    {
        SearchBox.Focus();
        if (selectAll) SearchBox.SelectAll();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (((App)Application.Current).IsExiting) return;
        e.Cancel = true;
        Hide();
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Results is not null) QueueSearch(true);
    }
    private async void QueueSearch(bool debounce)
    {
        if (_closed || Volatile.Read(ref _uiActive) == 0) return;
        if (_shellMenuOpen) { _refreshPending = true; return; }
        int generation = ++_generation;
        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        string query = SearchBox.Text;
        var snapshot = _index.Snapshot;
        var sort = _sort;
        if (debounce) ReleaseResults();
        _refreshPending = false;
        _searchRunning = true;
        try
        {
            if (query.Length == 0 || snapshot is null)
            {
                ReleaseResults();
                EmptyHint.Text = query.Length == 0 ? "検索する名前を入力してください" : "索引を準備しています…";
                EmptyHint.Visibility = Visibility.Visible;
                return;
            }
            ResultStatus.Text = "検索中…";
            if (debounce) await Task.Delay(100, token);
            _memory?.Record("searching", DisplayedResultCount, CountRealizedRows(Results));
            using var memorySampling = _memory?.SampleDuringSearch();
            var watch = Stopwatch.StartNew();
            var results = await Task.Run(() => SearchEngine.Search(snapshot, query, sort, token), token);
            if (generation != _generation || token.IsCancellationRequested || _closed || Volatile.Read(ref _uiActive) == 0) return;
            if (_shellMenuOpen) { _refreshPending = true; return; }
            ApplyResults(results);
            Volatile.Write(ref _displayedResultCount, results.Length);
            ResultStatus.Text = $"{results.Length:N0} 件 · {watch.ElapsedMilliseconds:N0} ms";
            EmptyHint.Text = "一致するファイルはありません";
            EmptyHint.Visibility = results.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_memory is not null) _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (generation == _generation && Volatile.Read(ref _uiActive) != 0 && !_closed)
                    _memory.Record("results-visible", DisplayedResultCount, CountRealizedRows(Results));
            }));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _generation && !_closed) ResultStatus.Text = "検索を完了できませんでした";
            IndexManager.Log(ex);
        }
        finally
        {
            if (generation == _generation)
            {
                _searchRunning = false;
                _searchCancellation = null;
                if (_refreshPending && Volatile.Read(ref _uiActive) != 0 && !_closed && !_shellMenuOpen) _ = Dispatcher.BeginInvoke(() => QueueSearch(false));
            }
            cancellation.Dispose();
        }
    }

    internal void ApplyResults(SearchResult[] results)
    {
        // Inspect focus at publication time so an older sort cannot steal focus after the user moves.
        bool restoreRowFocus = IsActive && Results.IsKeyboardFocusWithin && GetRow(Keyboard.FocusedElement as DependencyObject) is not null;
        ulong? currentId = (Results.CurrentItem as SearchResult ?? Results.SelectedItem as SearchResult)?.Id;
        var column = Results.CurrentCell.Column ?? Results.Columns[0];
        Results.ItemsSource = results;
        if (!restoreRowFocus) return;
        SearchResult? selected = currentId is ulong id ? Array.Find(results, item => item.Id == id) : null;
        selected ??= results.FirstOrDefault();
        if (selected is null) { Results.Focus(); return; }
        Results.SelectedItem = selected;
        Results.CurrentCell = new DataGridCellInfo(selected, column);
        Results.ScrollIntoView(selected, column);
        Results.UpdateLayout();
        if (Results.ItemContainerGenerator.ContainerFromItem(selected) is DataGridRow row)
            row.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        else Results.Focus();
    }

    private static int CountRealizedRows(DependencyObject parent)
    {
        int count = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            count += child is DataGridRow ? 1 : CountRealizedRows(child);
        }
        return count;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down) return;
        e.Handled = true;
        if (Results.Items.Count == 0) return;
        Results.SelectedIndex = 0;
        Results.CurrentCell = new DataGridCellInfo(Results.Items[0], Results.Columns[0]);
        Results.ScrollIntoView(Results.Items[0]);
        Results.UpdateLayout();
        Results.Focus();
        if (Results.ItemContainerGenerator.ContainerFromIndex(0) is DataGridRow row)
            row.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        int currentIndex = GetRow(Keyboard.FocusedElement as DependencyObject)?.GetIndex() ?? Results.SelectedIndex;
        bool slash = e.Key == Key.Divide || (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.None);
        if ((e.Key == Key.Up && currentIndex == 0) || slash)
        {
            e.Handled = true;
            FocusSearch();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Results.CurrentItem is SearchResult current) OpenFile(current);
            else if (Results.SelectedItem is SearchResult selected) OpenFile(selected);
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            if (Results.SelectedItem is SearchResult result)
            {
                var row = Results.ItemContainerGenerator.ContainerFromItem(result) as DataGridRow;
                ShowShellMenu(result, (row as Visual ?? Results).PointToScreen(new Point(20, 20)));
            }
        }
    }

    private void Results_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != "/") return;
        e.Handled = true;
        FocusSearch();
    }

    private void Results_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        ChangeSort(ColumnFor(e.Column));
    }

    private static SortColumn ColumnFor(DataGridColumn column) => column.SortMemberPath switch
        {
            "Folder" => SortColumn.Folder, "Size" => SortColumn.Size,
            "Modified" => SortColumn.Modified, _ => SortColumn.Name
        };

    private void ChangeSort(SortColumn column)
    {
        _sort = new(column, _sort.Column == column && !_sort.Descending);
        foreach (var item in Results.Columns)
            item.SortDirection = ColumnFor(item) == column
                ? (_sort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
        QueueSearch(false);
    }

    private static DataGridRow? GetRow(DependencyObject? source)
    {
        while (source is not null && source is not DataGridRow)
        {
            if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
                source = VisualTreeHelper.GetParent(source);
            else source = LogicalTreeHelper.GetParent(source);
        }
        return source as DataGridRow;
    }

    private void Results_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ResetPointer();
        var row = GetRow(e.OriginalSource as DependencyObject);
        if (row?.Item is not SearchResult result) return;
        _pressedItem = result;
        var point = e.GetPosition(Results);
        _gesture.Begin(result.Id, point.X, point.Y, Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1);
        _deferredSelection = row.IsSelected && Keyboard.Modifiers == ModifierKeys.None;
        // WPF otherwise collapses a multiselection before DragDrop sees it.
        if (_deferredSelection) e.Handled = true;
    }
    private void Results_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var released = GetRow(e.OriginalSource as DependencyObject)?.Item as SearchResult;
        var pressed = _pressedItem;
        bool open = _gesture.End(released?.Id);
        if (_deferredSelection && pressed is not null && released?.Id == pressed.Id)
        {
            Results.SelectedItems.Clear();
            Results.SelectedItem = pressed;
            Results.Focus();
        }
        ResetPointer();
        if (open && pressed is not null) OpenFile(pressed);
    }
    private void Results_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { ResetPointer(); return; }
        Point point = e.GetPosition(Results);
        if (!_gesture.Move(point.X, point.Y, SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance)) return;
        _deferredSelection = false;
        _pressedItem = null;
        // CF_HDROP uses full filesystem paths; the shell/drop target owns copy/move/link semantics.
        var paths = new StringCollection();
        paths.AddRange(Results.SelectedItems.OfType<SearchResult>().Select(r => r.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        if (paths.Count == 0) return;
        var data = new DataObject();
        data.SetFileDropList(paths);
        try { DragDrop.DoDragDrop(Results, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch (COMException ex) { IndexManager.Log(ex); }
        catch (ExternalException ex) { IndexManager.Log(ex); }
        finally { ResetPointer(); }
    }

    private void ResetPointer()
    {
        _gesture.Cancel();
        _pressedItem = null;
        _deferredSelection = false;
    }

    private void OpenFile(SearchResult result)
    {
        try { ShellFileActions.Open(result.FullPath); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException)
        { ReportShellError("ファイルを開けませんでした", ex); }
    }

    private void Results_RightClick(object sender, MouseButtonEventArgs e)
    {
        ResetPointer();
        if (GetRow(e.OriginalSource as DependencyObject)?.Item is not SearchResult result) return;
        e.Handled = true;
        Results.SelectedItems.Clear();
        Results.SelectedItem = result;
        Results.CurrentCell = new DataGridCellInfo(result, Results.Columns[0]);
        Results.Focus();
        ShowShellMenu(result, PointToScreen(e.GetPosition(this)));
    }

    private void ShowShellMenu(SearchResult result, Point point)
    {
        if (_shellMenuOpen) return;
        _shellMenuOpen = true;
        try { ShellFileActions.ShowContextMenu(this, result.FullPath, point); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ExternalException or InvalidOperationException or ArgumentException)
        { ReportShellError("シェルメニューを表示・実行できませんでした", ex); }
        finally
        {
            _shellMenuOpen = false;
            if (_refreshPending && !_closed && Volatile.Read(ref _uiActive) != 0) QueueSearch(false);
        }
    }

    private void ReportShellError(string title, Exception ex)
    {
        IndexManager.Log(ex);
        MessageBox.Show(this, $"{title}。\n{ex.Message}", "FastSearch", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void StopSearch()
    {
        _closed = true;
        Interlocked.Exchange(ref _uiActive, 0);
        _searchCancellation?.Cancel();
        ResetPointer();
        ReleaseResults();
        _index.StatusChanged -= OnStatus;
        _index.Changed -= OnIndexChanged;
    }
}
