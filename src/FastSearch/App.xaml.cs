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
    private IndexManager? _index;
    private MainWindow? _window;
    private GlobalHotKey? _hotKey;
    private MemoryDiagnostics? _memory;
    private int _indexMeasured;
    public bool IsExiting { get; private set; }

    public App() => RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FastSearch.Show." + sid);
        _mutex = new Mutex(true, @"Local\FastSearch.Instance." + sid, out _ownsMutex);
        if (!_ownsMutex)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("FastSearchは管理者権限で起動してください。", "FastSearch", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        string storage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastSearch", "index.bin");
        if (Array.IndexOf(e.Args, "--measure-memory") >= 0)
        {
            _memory = new MemoryDiagnostics(Path.Combine(Path.GetDirectoryName(storage)!, $"memory-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.csv"));
            _memory.Record("startup");
        }
        _index = new IndexManager(storage);
        _index.Changed += () =>
        {
            _memory?.SetIndexCount(_index.Snapshot?.Entries.Count ?? 0);
            if (Interlocked.Exchange(ref _indexMeasured, 1) == 0) _memory?.Record("index-ready");
        };
        _window = new MainWindow(_index, _memory);
        MainWindow = _window;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("検索画面を開く", null, (_, _) => Dispatcher.BeginInvoke(() => _window.ShowSearch()));
        menu.Items.Add("終了", null, (_, _) => Dispatcher.BeginInvoke(() => ExitNormally()));
        using (var iconStream = GetResourceStream(new Uri("/FastSearch;component/Assets/FastSearch.ico", UriKind.Relative)).Stream)
        using (var icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize))
            _trayIcon = (System.Drawing.Icon)icon.Clone();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "FastSearch — Cドライブのファイル名検索",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => _window.ShowSearch());
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => { if (!IsExiting) _window.ShowSearch(); }), null, Timeout.Infinite, false);
        _window.ShowSearch();
        try
        {
            _hotKey = new GlobalHotKey(() => { if (!IsExiting) _window.ShowSearch(); });
            if (!_hotKey.IsRegistered)
            {
                IndexManager.Log(new System.ComponentModel.Win32Exception(_hotKey.RegistrationError, "Win + / の登録に失敗しました。トレイから操作できます。"));
                _tray.ShowBalloonTip(5000, "FastSearch", "Win + / を登録できませんでした。トレイから検索画面を開けます。", Forms.ToolTipIcon.Info);
            }
        }
        catch (Exception ex) { IndexManager.Log(ex); }
        _index.Start();
    }

    private async void ExitNormally()
    {
        if (IsExiting) return;
        IsExiting = true;
        _window?.StopSearch();
        _window?.Hide();
        if (_tray?.ContextMenuStrip is not null) _tray.ContextMenuStrip.Enabled = false;
        try { if (_index is not null) await _index.StopAsync(false); }
        catch (Exception ex) { IndexManager.Log(ex); }
        finally { Shutdown(); }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        _window?.StopSearch();
        // A previous atomic checkpoint is always usable if Windows ends this bounded save early.
        if (_index is not null)
        {
            try { Task.Run(() => _index.StopAsync(true)).Wait(TimeSpan.FromMilliseconds(1500)); }
            catch (Exception ex) { IndexManager.Log(ex); }
        }
        e.Cancel = false;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRegistration?.Unregister(null);
        _hotKey?.Dispose();
        _memory?.Record("exit");
        _memory?.Dispose();
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
