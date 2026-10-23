using System.ComponentModel;
using System.IO;
using FastSearch.Settings;
using FastSearch.Core;
using Microsoft.Win32;

internal static class SettingsProbe
{
    internal static void Run(Action<bool, string> check)
    {
        var original = new AppSettings();
        check(original.KeepRunningInTray && original.ShouldStartHidden(true) && !original.ShouldStartHidden(false) &&
            !(original with { KeepRunningInTray = false }).ShouldStartHidden(true),
            "residency defaults on, while non-resident startup always shows a recoverable window");
        var first = new HotKeySetting(HotKeyModifiers.Control | HotKeyModifiers.Alt, 0x78); // F9
        var second = new HotKeySetting(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x79); // F10
        first.Validate();
        check(first.DisplayText == "Ctrl + Alt + F9" && HotKeySetting.Default.DisplayText.Contains("/"),
            "settings display custom modifiers and the layout-derived default shortcut");
        foreach (var invalid in new[] { new HotKeySetting(HotKeyModifiers.None, 65), new(HotKeyModifiers.Shift, 65),
            new((HotKeyModifiers)32, 65), new(HotKeyModifiers.Control, 0), new(HotKeyModifiers.Control, 0x11),
            new(HotKeyModifiers.Control, 0x31), new(HotKeyModifiers.Control, 0x64) })
        {
            bool rejected = false;
            try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
            check(rejected, "settings reject unusable hotkey " + invalid.Modifiers + "/" + invalid.VirtualKey);
        }
        string directory = Path.Combine(Path.GetTempPath(), "FastSearch-Settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            check(store.Load() == original, "missing settings use defaults without enabling startup");
            File.WriteAllText(path, "{\"Version\":1,\"StartWithWindows\":false}");
            check(store.Load().KeepRunningInTray, "existing settings without a residency field retain tray behavior");
            check(store.Load().LastSort == new SortOrder(SortColumn.Name), "existing settings without sort use name ascending");
            check(store.Load().Theme == AppTheme.LightGray, "existing settings without a theme use light gray");
            store.Save(original with { Theme = AppTheme.Charcoal });
            check(new SettingsStore(path).Load().Theme == AppTheme.Charcoal, "charcoal theme persists for the next process startup");
            var configured = original with { Shortcut = first, StartWithWindows = true };
            store.Save(configured);
            store.Save(configured with { Shortcut = second, KeepRunningInTray = false });
            check(store.Load() == configured with { Shortcut = second, KeepRunningInTray = false } && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "settings atomically replace and restore the complete shortcut/startup configuration");
            using (var sorting = new SettingsController(store, new MemoryStartup(), _ => new FakeHotKey(true)))
            {
                var staleDialog = sorting.ReadForEditing();
                var desiredSort = new SortOrder(SortColumn.Size, true);
                sorting.SaveSortAsync(desiredSort).GetAwaiter().GetResult();
                check(new SettingsStore(path).Load().LastSort == desiredSort,
                    "sort column and descending direction persist for the next process startup");
                sorting.Apply(staleDialog);
                check(store.Load().LastSort == desiredSort && sorting.Current.LastSort == desiredSort,
                    "saving other settings cannot overwrite the latest sort with a stale dialog value");
                var pending = new List<Task>();
                for (int i = 0; i < 20; i++) pending.Add(sorting.SaveSortAsync(new((SortColumn)(i % 4), i % 2 == 0)));
                Task.WhenAll(pending).GetAwaiter().GetResult();
                sorting.FlushAsync().GetAwaiter().GetResult();
                check(store.Load().LastSort == sorting.Current.LastSort && store.Load().LastSort == new SortOrder(SortColumn.Modified, false),
                    "rapid asynchronous sort saves cannot restore an older value after the latest sort");
            }
            File.WriteAllText(path, "{ bad json");
            check(store.Load() == original, "corrupt settings fall back without preventing startup");
            File.WriteAllText(path, "{\"Version\":99}");
            check(store.Load() == original, "unsupported settings versions fall back safely");
            File.WriteAllText(path, "{\"Shortcut\":null}");
            check(store.Load() == original, "null shortcut settings cannot crash initialization");
            File.WriteAllText(path, "{\"LastSort\":{\"Column\":999,\"Descending\":true}}");
            check(store.Load() == original, "invalid saved sort columns fall back safely");
            File.WriteAllText(path, "{\"Theme\":999}");
            check(store.Load() == original, "invalid saved theme values fall back safely");
        }
        finally { Directory.Delete(directory, true); }

        var registration = new StartupRegistration(@"C:\Program Files\FastSearch\FastSearch.exe");
        check(registration.EnabledEntry.Value as string == "\"C:\\Program Files\\FastSearch\\FastSearch.exe\" --background",
            "startup command quotes the executable and starts the frontend in the tray");

        var memoryStore = new MemoryStore(original);
        var startup = new MemoryStartup();
        var registrations = new List<FakeHotKey>();
        HotKeySetting? blocked = null;
        using var controller = new SettingsController(memoryStore, startup, key =>
        {
            var result = new FakeHotKey(key != blocked);
            registrations.Add(result);
            return result;
        });
        controller.RegisterInitial();
        blocked = first;
        bool conflict = false;
        try { controller.Apply(original with { Shortcut = first, StartWithWindows = true }); }
        catch (Win32Exception) { conflict = true; }
        check(conflict && controller.Current == original && startup.Entry.Value is null && !registrations[0].Disposed && registrations[1].Disposed,
            "conflicting shortcut leaves the original hotkey, saved settings and startup registration intact");
        blocked = null;
        var changed = original with { Shortcut = first, StartWithWindows = true };
        controller.Apply(changed);
        check(controller.Current == changed && memoryStore.Value == changed && startup.Entry == startup.EnabledEntry && registrations[0].Disposed,
            "successful settings save installs the new shortcut and startup before retiring the old shortcut");
        int count = registrations.Count;
        controller.Apply(changed with { StartWithWindows = false });
        check(registrations.Count == count && startup.Entry.Value is null,
            "startup-only changes do not conflict with or re-register the current hotkey");

        var previousStartup = new StartupEntry("%OLD%\\FastSearch.exe", RegistryValueKind.ExpandString);
        startup.Entry = previousStartup;
        check(controller.ReadForEditing().StartWithWindows, "startup registered at an older location remains visible as enabled");
        memoryStore.Fail = true;
        var previousSettings = controller.Current;
        bool saveFailed = false;
        try { controller.Apply(changed with { Shortcut = second }); } catch (IOException) { saveFailed = true; }
        check(saveFailed && controller.Current == previousSettings && startup.Entry == previousStartup &&
            registrations[^1].Disposed && !registrations[^2].Disposed,
            "disk failure restores the exact previous startup value/type and preserves the active hotkey");
        memoryStore.Fail = false;
        startup.Entry = new(null);
        startup.FailNextWrite = true;
        bool registryFailed = false;
        try { controller.Apply(changed with { Shortcut = second }); } catch (UnauthorizedAccessException) { registryFailed = true; }
        check(registryFailed && controller.Current == previousSettings && startup.Entry.Value is null && registrations[^1].Disposed,
            "startup registration failure rolls back partial changes and releases only the candidate shortcut");

        startup.Entry = startup.EnabledEntry;
        check(controller.ReadForEditing().StartWithWindows, "settings read the actual startup registration rather than a stale JSON flag");
        startup.Entry = new(null);
        check(!controller.ReadForEditing().StartWithWindows, "external removal of startup is reflected on opening settings");
        var retryCalls = 0;
        using var retry = new SettingsController(new MemoryStore(original), new MemoryStartup(), _ => new FakeHotKey(++retryCalls > 1));
        retry.RegisterInitial();
        retry.Apply(original);
        check(retry.IsHotKeyRegistered && retryCalls == 2, "saving retries a hotkey that failed to register at startup");
    }

    private sealed class FakeHotKey(bool registered) : IHotKeyRegistration
    {
        public bool IsRegistered => registered && !Disposed;
        public int RegistrationError => registered ? 0 : 1409;
        internal bool Disposed;
        public void Dispose() => Disposed = true;
    }
    private sealed class MemoryStore(AppSettings value) : ISettingsStore
    {
        internal AppSettings Value = value;
        internal bool Fail;
        public AppSettings Load() => Value;
        public void Save(AppSettings settings)
        { if (Fail) throw new IOException("disk full"); Value = settings; }
    }
    private sealed class MemoryStartup : IStartupRegistration
    {
        internal StartupEntry Entry = new(null);
        internal bool FailNextWrite;
        public StartupEntry EnabledEntry { get; } = new("\"C:\\FastSearch\\FastSearch.exe\" --background");
        public StartupEntry Read() => Entry;
        public void Write(StartupEntry entry)
        {
            Entry = entry;
            if (FailNextWrite) { FailNextWrite = false; throw new UnauthorizedAccessException("registry denied"); }
        }
    }
}
