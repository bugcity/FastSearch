using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using FastSearch.Core;
using FastSearch.Native;
using FastSearch.Diagnostics;

if (args.Length == 3 && args[0] == "--service-client-probe")
{
    ServiceProbe.RunClient(args[1], int.Parse(args[2]));
    return;
}

if (args.Contains("--focus-probe"))
{
    FocusSortProbe.Run();
    return;
}

if (args.Contains("--memory-profile"))
{
    string output = Path.GetFullPath(args.SkipWhile(x => x != "--memory-profile").Skip(1).FirstOrDefault()
        ?? Path.Combine(AppContext.BaseDirectory, "memory-profile.csv"));
    int Option(string name, int fallback) => int.TryParse(args.SkipWhile(x => x != name).Skip(1).FirstOrDefault(), out int value) ? value : fallback;
    MemoryProfile.Run(output, Option("--profile-files", 300000), args.Contains("--unique-names"), Option("--profile-cycles", 30));
    return;
}

int passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    passed++;
    Console.WriteLine("PASS: " + message);
}
var time = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
var source = new[]
{
    new IndexEntry(5, 5, ".", true, null, time),
    new IndexEntry(10, 5, "Documents", true, null, time),
    new IndexEntry(11, 10, "Report.TXT", false, 2048, time),
    new IndexEntry(12, 10, "report.csv", false, 100, time.AddDays(1)),
    new IndexEntry(13, 5, "日本語の資料", true, null, time),
    new IndexEntry(14, 13, "テスト.txt", false, 0, time),
    new IndexEntry(15, 10, "Report.TXT", false, 2048, time),
    new IndexEntry(16, 999, "orphan.txt", false, null, null),
    new IndexEntry(17, 18, "cycle.txt", true, null, null),
    new IndexEntry(18, 17, "cycle", true, null, null),
    new IndexEntry(19, 10, "unknown.txt", false, null, null)
};
var snapshot = new IndexSnapshot("test-volume", 5, 123, 100, IndexTable.FromEntries(source));
var tableBuilder = IndexTable.CreateBuilder();
var expectedTable = new Dictionary<ulong, IndexEntry>();
var frozenTables = new List<(IndexTable Table, Dictionary<ulong, IndexEntry> Expected)>();
var random = new Random(42);
for (int i = 0; i < 1000; i++)
{
    ulong id = (ulong)(1 + random.Next(64) * 4096); // Deliberate same-shard collisions.
    if (random.Next(3) == 0)
    {
        tableBuilder.Remove(id);
        expectedTable.Remove(id);
    }
    else
    {
        var entry = new IndexEntry(id, 5, $"item-{i}", false, i, time);
        tableBuilder[id] = entry;
        expectedTable[id] = entry;
    }
    if (i % 50 == 0) frozenTables.Add((tableBuilder.ToImmutable(), new(expectedTable)));
}
Check(frozenTables.All(pair => pair.Table.Count == pair.Expected.Count && pair.Expected.All(entry =>
    pair.Table.TryGetValue(entry.Key, out var actual) && actual == entry.Value) && pair.Table.Values.Count() == pair.Table.Count),
    "compact index: collisions, add/update/delete and reused builder preserve all previous snapshots");
var sharedNames = IndexTable.FromEntries([
    new IndexEntry(1,5,new string("shared.txt".ToCharArray()),false,1,null),
    new IndexEntry(2,5,new string("shared.txt".ToCharArray()),false,2,null),
    new IndexEntry(3,5,"SHARED.TXT",false,3,null)]);
Check(ReferenceEquals(sharedNames[1].Name, sharedNames[2].Name) && sharedNames[3].Name == "SHARED.TXT",
    "repeated names share storage without changing displayed case");
SearchResults Search(string query, SortColumn column = SortColumn.Name, bool desc = false) =>
    SearchEngine.Search(snapshot, query, new(column, desc), CancellationToken.None);
Check(Search("").Length == 0, "empty query has no results");
Check(Search("rEpOrT").Length == 3, "case-insensitive filename substring");
Check(Search("Documents").Length == 0 && Search("日本語").Length == 0, "folders remain indexed but are excluded from results");
Check(Search("資料/テスト").Single().Folder == @"C:\日本語の資料", "Japanese folder suffix and file substring");
Check(Search("テスト").Single().Folder == @"C:\日本語の資料", "parent path reconstruction");
Check(Search("Documents\\Report").Length == 0, "query matches name only, not parent path");
Check(Search("orphan").Length == 0 && Search("cycle").Length == 0, "missing parents and cycles do not hang or produce false paths");
Check(Search("report").Select(x => x.Id).SequenceEqual(new ulong[] {12,11,15}), "name ascending with stable ID tie break");
Check(Search("report", SortColumn.Name, true).Select(x => x.Id).SequenceEqual(new ulong[] {11,15,12}), "name descending with stable ID tie break");
Check(Search("report", SortColumn.Size).Select(x => x.Id).SequenceEqual(new ulong[] {12,11,15}), "size compares numeric bytes");
Check(Search("report", SortColumn.Size, true).Select(x => x.Id).SequenceEqual(new ulong[] {11,15,12}), "size descending");
Check(Search("report", SortColumn.Modified).First().Id == 11 && Search("report", SortColumn.Modified, true).First().Id == 12, "date sorting both directions");
Check(Search(".txt", SortColumn.Folder).Last().Folder == @"C:\日本語の資料" &&
      Search(".txt", SortColumn.Folder, true).First().Folder == @"C:\日本語の資料", "parent folder sorting both directions");
Check(new SearchResult(source[1], @"C:\").SizeText == "" && SearchResult.FormatSize(2048).Contains("KB") &&
      Search("unknown").Single().SizeText == "", "directory/unknown sizes blank and file sizes have units");
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    try { SearchEngine.Search(snapshot, "report", new(SortColumn.Name), canceled.Token); Check(false, "cancel"); }
    catch (OperationCanceledException) { Check(true, "search honors cancellation"); }
}
var moved = snapshot with { Entries = snapshot.Entries.SetItem(10, source[1] with { Name = "Renamed", ParentId = 13 }) };
Check(SearchEngine.Search(moved, "Report.TXT", new(SortColumn.Name), default).First().Folder == @"C:\日本語の資料\Renamed",
    "ancestor rename/move updates descendants without rewriting them");

string[] hierarchyFiles =
[
    @"var\data\report.xlsx", @"work\myvar\data\repository.cs", @"projects\var\data\repo.txt",
    @"var\other\data\report.xlsx", @"var\database\report.xlsx", @"var\data\myreport.xlsx",
    @"work\var\data\repository.cs", @"myvar\data\report.xlsx", @"var\data\sub\report.xlsx",
    @"work\svar\repo.txt", @"work\var\report.CS", @"repo-root.txt", @"var\data\repo"
];
var hierarchyEntries = IndexTable.CreateBuilder();
hierarchyEntries[5] = source[0];
var folderIds = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { [""] = 5 };
ulong nextId = 100;
foreach (string relative in hierarchyFiles)
{
    string[] parts = relative.Split('\\');
    ulong parent = 5;
    string path = "";
    for (int i = 0; i < parts.Length - 1; i++)
    {
        path = path.Length == 0 ? parts[i] : path + "\\" + parts[i];
        if (!folderIds.TryGetValue(path, out ulong id))
        {
            id = nextId++;
            folderIds[path] = id;
            hierarchyEntries[id] = new(id, parent, parts[i], true, null, time);
        }
        parent = id;
    }
    ulong fileId = nextId++;
    hierarchyEntries[fileId] = new(fileId, parent, parts[^1], false, 50, time);
}
var hierarchy = snapshot with { Entries = hierarchyEntries.ToImmutable() };
string[] HierarchySearch(string query) => SearchEngine.Search(hierarchy, query, new(SortColumn.Name), default)
    .Select(x => x.FullPath[3..]).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
void MatchPaths(string query, params string[] expected) => Check(HierarchySearch(query)
    .SequenceEqual(expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), "hierarchy: " + query);
MatchPaths("repo", hierarchyFiles);
MatchPaths("var/repo", @"work\svar\repo.txt", @"work\var\report.CS");
string[] suffixPaths = [@"var\data\report.xlsx", @"work\myvar\data\repository.cs", @"projects\var\data\repo.txt",
    @"work\var\data\repository.cs", @"myvar\data\report.xlsx", @"var\data\repo", @"var\data\myreport.xlsx"];
MatchPaths("var/data/repo", suffixPaths);
MatchPaths("VaR/DaTa/RePo", suffixPaths);
string[] exactPaths = [@"var\data\report.xlsx", @"projects\var\data\repo.txt", @"work\var\data\repository.cs", @"var\data\repo", @"var\data\myreport.xlsx"];
MatchPaths("/var/data/repo", exactPaths);
MatchPaths("/var/repo", @"work\var\report.CS");
MatchPaths("/var/data/", exactPaths);
MatchPaths("var/data/myrepo", @"var\data\myreport.xlsx");
MatchPaths("/repo", hierarchyFiles);
MatchPaths("/var/data/REPORT", @"var\data\report.xlsx", @"var\data\myreport.xlsx");
MatchPaths("/var/data/xlsx", @"var\data\report.xlsx", @"var\data\myreport.xlsx");
MatchPaths("/");
MatchPaths("//repo");
MatchPaths("var//repo");
MatchPaths("var/data /repo");
Check(SearchEngine.Search(hierarchy, "data", new(SortColumn.Name), default).Length == 0,
    "folder conditions never leak directories into file results");

var gesture = new FilePointerGesture();
gesture.Begin(11, 10, 10, true);
Check(!gesture.Move(11, 11, 4, 4) && gesture.End(11), "plain click opens on release of same row");
gesture.Begin(11, 10, 10, true);
Check(gesture.Move(14, 10, 4, 4) && !gesture.Move(20, 10, 4, 4) && !gesture.End(11),
    "drag starts once at threshold and can never open a file");
gesture.Begin(11, 0, 0, false);
Check(!gesture.End(11), "Ctrl/Shift selection does not open a file");
gesture.Begin(11, 0, 0, true);
Check(!gesture.End(12) && !gesture.End(11), "release on another row cancels opening");
gesture.Begin(11, 0, 0, true);
gesture.Cancel();
Check(!gesture.End(11), "deactivation, right-click or canceled drag cannot open a file");
var batch = new JournalBatch(200, new UsnRecord[]
{
    new(11,10,"Report.TXT",false,0x200),
    new(12,10,"report.csv",false,0x1000),
    new(12,13,"updated.csv",false,0x2000),
    new(20,10,"new.txt",false,0x100),
    new(20,10,"new.txt",false,0x80000001)
});
int metadataReads = 0;
var updated = IndexManager.ApplyBatch(snapshot, batch, record =>
{
    metadataReads++;
    return new(record.Id, record.ParentId, record.Name, record.IsDirectory, 8192, time.AddHours(1));
}, default);
Check(!updated.Entries.ContainsKey(11) && updated.Entries[12].ParentId == 13 && updated.Entries[12].Name == "updated.csv" &&
      updated.Entries[20].Size == 8192 && updated.NextUsn == 200 && metadataReads == 2, "atomic journal delete, rename, create, size update, checkpoint and coalescing");
Check(snapshot.Entries.ContainsKey(11) && snapshot.NextUsn == 100, "old snapshot stays immutable during indexing");
var oldNameOnly = IndexManager.ApplyBatch(snapshot, new JournalBatch(201, [new(11,10,"old",false,0x80001000)]), _ => throw new Exception(), default);
Check(oldNameOnly.Entries[11].Name == "Report.TXT", "rename-old record cannot overwrite a current name");
var journal = new JournalState(123, 50, 300, 80);
Check(journal.CanResume(snapshot, "test-volume") && !journal.CanResume(snapshot, "other-volume") &&
      !journal.CanResume(snapshot with { JournalId = 124 }, "test-volume") &&
      !journal.CanResume(snapshot with { NextUsn = 79 }, "test-volume") &&
      !journal.CanResume(snapshot with { NextUsn = 301 }, "test-volume"), "volume, journal ID and USN continuity validation");

byte[] RecordBuffer(string name)
{
    byte[] nameBytes = Encoding.Unicode.GetBytes(name);
    int length = (60 + nameBytes.Length + 7) & ~7;
    byte[] buffer = new byte[8 + length];
    BinaryPrimitives.WriteInt64LittleEndian(buffer, 200);
    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), length);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), 2);
    BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16), 42);
    BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(24), 5);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(48), 0x100);
    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60), 0x10);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(64), (ushort)nameBytes.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(66), 60);
    nameBytes.CopyTo(buffer, 68);
    return buffer;
}
byte[] validBuffer = RecordBuffer("資料");
var parsed = NtfsVolume.ParseRecords(validBuffer, validBuffer.Length).Single();
Check(parsed.Id == 42 && parsed.ParentId == 5 && parsed.Name == "資料" && parsed.IsDirectory && parsed.Reason == 0x100,
    "USN_RECORD_V2 layout and non-terminated Unicode name");
foreach (int mutation in new[] { 0, 1, 2, 3 })
{
    byte[] broken = (byte[])validBuffer.Clone();
    if (mutation == 0) BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(8), 0);
    if (mutation == 1) BinaryPrimitives.WriteUInt16LittleEndian(broken.AsSpan(12), 3);
    if (mutation == 2) BinaryPrimitives.WriteUInt16LittleEndian(broken.AsSpan(64), ushort.MaxValue);
    int count = mutation == 3 ? broken.Length - 1 : broken.Length;
    try { NtfsVolume.ParseRecords(broken, count); Check(false, "invalid buffer"); }
    catch (InvalidDataException) { Check(true, "malformed/unsupported USN records rejected: " + mutation); }
}

string temp = Path.Combine(AppContext.BaseDirectory, "FastSearchTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    string path = Path.Combine(temp, "index.bin");
    var storage = new IndexStorage(path);
    Check(storage.Load(default) is null, "missing index triggers rebuild");
    storage.Save(snapshot, default);
    var loaded = storage.Load(default)!;
    Check(loaded.VolumeIdentity == snapshot.VolumeIdentity && loaded.JournalId == snapshot.JournalId &&
          loaded.NextUsn == snapshot.NextUsn && loaded.RootId == snapshot.RootId &&
          loaded.Entries.Count == snapshot.Entries.Count && loaded.Entries[11] == snapshot.Entries[11], "index/checkpoint roundtrip");
    storage.Save(updated, default);
    Check(storage.Load(default)!.NextUsn == 200, "atomic replacement of existing index");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try { storage.Save(snapshot, canceled.Token); Check(false, "save cancel"); }
        catch (OperationCanceledException) { }
        Check(storage.Load(default)!.NextUsn == 200 && Directory.GetFiles(temp, "*.tmp").Length == 0,
            "interrupted save preserves previous valid checkpoint and cleans temporary file");
    }
    byte[] data = File.ReadAllBytes(path);
    data[^1] ^= 0x40;
    File.WriteAllBytes(path, data);
    Check(storage.Load(default) is null, "checksum detects corrupted index");
    File.WriteAllBytes(path, data[..20]);
    Check(storage.Load(default) is null, "truncated index triggers rebuild");
    string memoryPath = Path.Combine(temp, "memory.csv");
    using (var diagnostics = new MemoryDiagnostics(memoryPath))
    {
        diagnostics.SetIndexCount(100);
        diagnostics.Record("index-ready");
        diagnostics.Record("results-visible", 20, 5);
        diagnostics.Record("resident");
    }
    string[] measurements = File.ReadAllLines(memoryPath);
    var fields = measurements[2].Split(',');
    Check(measurements.Length == 4 && fields[3] == "100" && fields[4] == "20" && long.Parse(fields[5]) > 0 &&
        fields[7] == "5" && measurements[3].Contains("\"resident\",100,0,"), "Private Bytes CSV records state, counts, UI rows, resident release and drains on exit");
    try { ShellFileActions.Open(Path.Combine(temp, "missing.txt")); Check(false, "open missing"); }
    catch (FileNotFoundException) { Check(true, "missing file opening fails safely before Shell execution"); }
}
finally { Directory.Delete(temp, true); }

// Synthetic benchmark measures the actual search/sort implementation, with no drive scan.
var builder = IndexTable.CreateBuilder();
builder[5] = source[0];
for (ulong i = 100; i < 300100; i++) builder[i] = new(i, 5, $"file-{i:D8}.txt", false, (long)i, time);
var large = snapshot with { Entries = builder.ToImmutable() };
var watch = Stopwatch.StartNew();
var matches = SearchEngine.Search(large, "file-", new(SortColumn.Size, true), default);
Check(matches.CachedRowCount == 0, "large search does not create one presentation object per result");
Check(matches.Length == 300000 && matches[0].Entry.Size == 300099, "300,000-result search and numeric sort");
var originalFirst = matches[0];
for (int i = 0; i < matches.Length; i += 257) _ = matches[i];
Check(matches.CachedRowCount <= 512 && ((System.Collections.IList)matches).IndexOf(originalFirst) == 0 &&
    ((System.Collections.IList)matches).IndexOf(matches[^1]) == matches.Length - 1,
    "lazy results retain all matches, bound the row cache, and find items after eviction");
Console.WriteLine($"BENCHMARK: 300,000 entries, {matches.Length:N0} matches, {watch.ElapsedMilliseconds} ms, managed heap {GC.GetTotalMemory(false) / 1024 / 1024} MB");

Exception? uiError = null;
var uiThread = new Thread(() =>
{
    try
    {
        var app = new FastSearch.App();
        app.InitializeComponent();
        var manager = new TestIndexSource(large);
        var window = new FastSearch.MainWindow(manager);
        var grid = (System.Windows.Controls.DataGrid)window.FindName("Results");
        var searchBox = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
        for (int columnIndex = 0; columnIndex < 4; columnIndex++)
        {
            foreach (var key in new[] { System.Windows.Input.Key.D1 + columnIndex, System.Windows.Input.Key.NumPad1 + columnIndex })
            {
                var binding = window.InputBindings.OfType<System.Windows.Input.KeyBinding>().Single(b =>
                    b.Key == key && b.Modifiers == System.Windows.Input.ModifierKeys.Control);
                if (!Equals(binding.CommandParameter, (SortColumn)columnIndex)) throw new Exception("Sort key mapping failed");
            }
            var targetColumn = (SortColumn)columnIndex;
            var old = window.CurrentSort;
            FastSearch.MainWindow.SortByColumnCommand.Execute(targetColumn, searchBox);
            bool descending = old.Column == targetColumn && !old.Descending;
            if (window.CurrentSort != new SortOrder(targetColumn, descending) ||
                grid.Columns[columnIndex].SortDirection != (descending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending) ||
                grid.Columns.Where((_, i) => i != columnIndex).Any(c => c.SortDirection is not null))
                throw new Exception("Sort command/header direction mismatch");
            FastSearch.MainWindow.SortByColumnCommand.Execute(targetColumn, grid);
            if (window.CurrentSort != new SortOrder(targetColumn, !descending)) throw new Exception("Repeated key did not toggle direction");
            var sorting = TestInput.RaiseHeaderSort(grid, grid.Columns[columnIndex]);
            if (!sorting.Handled || window.CurrentSort != new SortOrder(targetColumn, descending)) throw new Exception("Header sort did not share shortcut behavior");
        }
        var retainedSort = window.CurrentSort;
        searchBox.Text = "changed query";
        if (window.CurrentSort != retainedSort) throw new Exception("Query change reset sort state");
        searchBox.Text = "";
        Console.WriteLine("SHORTCUT SORT: Ctrl+1..4/keypad bindings, both input targets, toggles, header parity and query persistence passed");
        int posted = 0;
        System.Windows.Threading.DispatcherHookEventHandler onPosted = (_, _) => Interlocked.Increment(ref posted);
        window.Dispatcher.Hooks.OperationPosted += onPosted;
        Task.Run(() => { for (int i = 0; i < 1000; i++) manager.Notify(); }).GetAwaiter().GetResult();
        window.Dispatcher.Hooks.OperationPosted -= onPosted;
        if (posted != 0) throw new Exception($"Hidden index notifications queued {posted} unnecessary UI operations");
        Console.WriteLine("HIDDEN UI: 1,000 index/status updates caused 0 dispatcher operations");
        if (grid.Columns.Count != 4 || !grid.EnableRowVirtualization ||
            !System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(grid) ||
            grid.SelectionMode != System.Windows.Controls.DataGridSelectionMode.Extended ||
            app.ShutdownMode != System.Windows.ShutdownMode.OnExplicitShutdown ||
            System.Windows.Media.RenderOptions.ProcessRenderMode != System.Windows.Interop.RenderMode.SoftwareOnly)
            throw new Exception("WPF configuration did not match the specification");
        var keyboard = System.Windows.Input.Keyboard.PrimaryDevice;
        using var keySource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("KeyTest") { ParentWindow = new IntPtr(-3) });
        var downEmpty = new System.Windows.Input.KeyEventArgs(keyboard, keySource, 0, System.Windows.Input.Key.Down)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        searchBox.RaiseEvent(downEmpty);
        if (!downEmpty.Handled || grid.SelectedIndex != -1) throw new Exception("Empty Down navigation failed");
        grid.ItemsSource = matches;
        var root = (System.Windows.FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(1080, 640));
        root.Arrange(new System.Windows.Rect(0, 0, 1080, 640));
        root.UpdateLayout();
        int CountRows(System.Windows.DependencyObject element)
        {
            int count = element is System.Windows.Controls.DataGridRow ? 1 : 0;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                count += CountRows(System.Windows.Media.VisualTreeHelper.GetChild(element, i));
            return count;
        }
        int rows = CountRows(grid);
        if (rows == 0 || rows > 200) throw new Exception($"Virtualization failed: {rows} rows for 300,000 matches");
        Console.WriteLine($"UI VIRTUALIZATION: {rows} realized rows for {matches.Length:N0} results");
        var down = new System.Windows.Input.KeyEventArgs(keyboard, downEmpty.InputSource, 0, System.Windows.Input.Key.Down)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        searchBox.RaiseEvent(down);
        if (!down.Handled || grid.SelectedIndex != 0) throw new Exception("Down to first result failed");
        var up = new System.Windows.Input.KeyEventArgs(keyboard, downEmpty.InputSource, 0, System.Windows.Input.Key.Up)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(up);
        if (!up.Handled) throw new Exception("Up from first result failed");
        searchBox.Text = "var/data/repo";
        var text = new System.Windows.Input.TextCompositionEventArgs(keyboard,
            new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, grid, "/"))
            { RoutedEvent = System.Windows.Input.TextCompositionManager.PreviewTextInputEvent };
        grid.RaiseEvent(text);
        if (!text.Handled || searchBox.Text != "var/data/repo") throw new Exception("Slash navigation altered query");
        var slash = new System.Windows.Input.KeyEventArgs(keyboard, keySource, 0, System.Windows.Input.Key.OemQuestion)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        grid.RaiseEvent(slash);
        if (slash.Handled || searchBox.Text != "var/data/repo") throw new Exception("Raw slash key must not preempt remapped text input");
        bool registeredFirst;
        using (var firstHotKey = new GlobalHotKey(() => { }))
        {
            registeredFirst = firstHotKey.IsRegistered;
            using var secondHotKey = new GlobalHotKey(() => { });
            if (firstHotKey.IsRegistered && secondHotKey.IsRegistered) throw new Exception("Conflicting hotkey registration should fail");
            Console.WriteLine($"HOTKEY: Win + / registered={firstHotKey.IsRegistered}, competing registration={secondHotKey.IsRegistered}, error={secondHotKey.RegistrationError}");
        }
        if (registeredFirst)
        {
            using var reRegistered = new GlobalHotKey(() => { });
            if (!reRegistered.IsRegistered) throw new Exception("Hotkey did not unregister on disposal");
            Console.WriteLine("HOTKEY: registration released and successfully re-registered");
        }
        if (args.Contains("--shell-probe"))
        {
            string shellFixture = Path.Combine(AppContext.BaseDirectory, "shell-fixture.txt");
            File.WriteAllText(shellFixture, "FastSearch shell binding probe");
            try
            {
                int commands = ShellFileActions.ProbeContextMenu(shellFixture);
                if (commands <= 0) throw new Exception("No Windows shell menu commands were returned");
                Console.WriteLine($"SHELL PROBE: file-specific IContextMenu returned {commands} command IDs; no command invoked");
            }
            finally { File.Delete(shellFixture); }
        }
        window.StopSearch();
        if (grid.Items.Count != 0 || window.DisplayedResultCount != 0) throw new Exception("Result references were not released");
        app.Shutdown();
    }
    catch (Exception ex) { uiError = ex; }
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
uiThread.Join();
if (uiError is not null) throw new Exception("WPF smoke test failed", uiError);
Check(true, "WPF software rendering, 4 columns, actual row virtualization, Down/Up/slash navigation, result release and hotkey conflict fallback");

if (args.Contains("--ntfs"))
{
    using var volume = new NtfsVolume();
    var state = volume.QueryJournal();
    Check(volume.Identity.Length > 0 && volume.RootId != 0 && state.Id != 0, "live C: NTFS identity, root FRN and journal");
    var records = volume.EnumerateMft(default).Take(200).ToArray();
    Check(records.Length > 0, "live MFT enumeration");
    var entry = volume.ReadMetadata(records.First(x => !x.IsDirectory));
    Check(entry.Size is not null && entry.ModifiedUtc is not null, "live metadata lookup by file ID");
    var changes = volume.ReadJournal(state.Id, state.NextUsn);
    Check(changes.NextUsn >= state.NextUsn, "live journal continuation");
}
IpcProbe.Run(Check);
ServiceProbe.Run(Check);
SettingsProbe.Run(Check);
PackedIndexProbe.Run(Check);
Console.WriteLine($"All {passed} checks passed.");
