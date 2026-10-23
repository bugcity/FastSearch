using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using FastSearch.Core;
using FastSearch.Diagnostics;
using FastSearch.Native;
using FastSearch.Settings;
using FastSearch.Ipc;
using FastSearch.Themes;
using Forms = System.Windows.Forms;

namespace FastSearch;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private ServiceIndexSource? _index;
    private MainWindow? _window;
    private SettingsController? _settings;
    private SettingsWindow? _settingsWindow;
    private MemoryDiagnostics? _memory;
    private int _indexMeasured;
    public bool IsExiting { get; private set; }

    public App() => RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("FastSearchの検索画面は通常権限で起動してください。\n索引はWindowsサービスが管理します。", "FastSearch", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FastSearch.Show." + sid);
            _mutex = new Mutex(true, @"Local\FastSearch.Instance." + sid, out _ownsMutex);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("管理者権限の旧版が常駐している場合は、トレイから終了してから起動してください。", "FastSearch", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        if (!_ownsMutex)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }
        string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastSearch", "index.bin");
        if (Array.IndexOf(e.Args, "--measure-memory") >= 0)
        {
            _memory = new MemoryDiagnostics(Path.Combine(Path.GetDirectoryName(storage)!, $"memory-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.csv"),
                workerProcessId: () => _index?.WorkerProcessId ?? 0);
            _memory.Record("startup");
        }
        _index = new ServiceIndexSource();
        _index.Changed += () =>
        {
            _memory?.SetIndexCount(_index.EntryCount);
            if (_index.IsReady && Interlocked.Exchange(ref _indexMeasured, 1) == 0) _memory?.Record("index-ready");
        };
        _settings = new SettingsController(new SettingsStore(Path.Combine(Path.GetDirectoryName(storage)!, "settings.json")),
            new StartupRegistration(Path.Combine(AppContext.BaseDirectory, "FastSearch.exe")),
            shortcut => new GlobalHotKey(() =>
            {
                if (IsExiting) return;
                if (_settingsWindow?.IsVisible == true) _settingsWindow.Activate();
                else _window?.ShowSearch();
            }, shortcut));
        ThemeManager.Apply(_settings.Current.Theme);
        _window = new MainWindow(_index, _memory,
            () => _settings.Current.KeepRunningInTray, RequestExit, _settings.Current.LastSort);
        _window.SettingsRequested += ShowSettings;
        _window.SortOrderChanged += async sort =>
        {
            try { await _settings.SaveSortAsync(sort); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { IndexManager.Log(ex); }
        };
        MainWindow = _window;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("検索画面を開く", null, (_, _) => Dispatcher.BeginInvoke(() => _window.ShowSearch()));
        menu.Items.Add("設定…", null, (_, _) => Dispatcher.BeginInvoke(ShowSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => Dispatcher.BeginInvoke(() => RequestExit()));
        using (var iconStream = GetResourceStream(new Uri("/FastSearch;component/Assets/FastSearch.ico", UriKind.Relative)).Stream)
        using (var icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize))
            _trayIcon = (System.Drawing.Icon)icon.Clone();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "FastSearch — Cドライブのファイル名検索",
            ContextMenuStrip = menu,
            Visible = _settings.Current.KeepRunningInTray
        };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => _window.ShowSearch());
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => { if (!IsExiting) _window.ShowSearch(); }), null, Timeout.Infinite, false);
        if (!_settings.Current.ShouldStartHidden(Array.IndexOf(e.Args, "--background") >= 0)) _window.ShowSearch();
        try
        {
            _settings.RegisterInitial();
            if (!_settings.IsHotKeyRegistered)
            {
                string message = $"{_settings.Current.Shortcut.DisplayText} を登録できませんでした。「設定」から変更できます。";
                IndexManager.Log(new System.ComponentModel.Win32Exception(_settings.RegistrationError, message));
                _tray.ShowBalloonTip(5000, "FastSearch", message, Forms.ToolTipIcon.Info);
            }
        }
        catch (Exception ex) { IndexManager.Log(ex); }
        _index.Start();
    }

    private void ShowSettings()
    {
        if (IsExiting || _settings is null) return;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        try
        {
            _settingsWindow = new SettingsWindow(_settings.ReadForEditing(), settings =>
            {
                if (IsExiting) throw new InvalidOperationException("終了処理中です。");
                _settings.Apply(settings);
                ThemeManager.Apply(_settings.Current.Theme);
            });
            if (_window?.IsVisible == true) _settingsWindow.Owner = _window;
            else _settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (_settingsWindow.ShowDialog() == true && !IsExiting)
            {
                if (_tray is not null) _tray.Visible = _settings.Current.KeepRunningInTray;
                if (!_settings.Current.KeepRunningInTray && _window?.IsVisible == false) _window.ShowSearch();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { MessageBox.Show("設定を開けませんでした。\n" + ex.Message, "FastSearch", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { _settingsWindow = null; }
    }

    internal async void RequestExit()
    {
        if (IsExiting) return;
        IsExiting = true;
        _window?.StopSearch();
        _window?.Hide();
        if (_tray?.ContextMenuStrip is not null) _tray.ContextMenuStrip.Enabled = false;
        try
        {
            try { if (_settings is not null) await _settings.FlushAsync(); }
            catch (Exception ex) { IndexManager.Log(ex); }
            if (_index is not null) await _index.StopAsync(false);
        }
        catch (Exception ex) { IndexManager.Log(ex); }
        finally { Shutdown(); }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        _window?.StopSearch();
        // A previous atomic checkpoint is always usable if Windows ends this bounded save early.
        if (_index is not null || _settings is not null)
        {
            try
            {
                Task.Run(() => Task.WhenAll(_index?.StopAsync(true) ?? Task.CompletedTask,
                    _settings?.FlushAsync() ?? Task.CompletedTask)).Wait(TimeSpan.FromMilliseconds(1500));
            }
            catch (Exception ex) { IndexManager.Log(ex); }
        }
        e.Cancel = false;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRegistration?.Unregister(null);
        _settings?.Dispose();
        _memory?.Record("exit");
        _memory?.Dispose();
        _index?.Dispose();
        if (_memory?.WriteError is Exception memoryError) IndexManager.Log(memoryError);
        _tray?.Dispose();
        _trayIcon?.Dispose();
        _tray?.ContextMenuStrip?.Dispose();
        _showEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
