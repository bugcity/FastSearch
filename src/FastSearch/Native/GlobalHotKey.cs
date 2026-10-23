using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FastSearch.Settings;

namespace FastSearch.Native;

public sealed class GlobalHotKey : IDisposable, IHotKeyRegistration
{
    private const int Id = 0x4653;
    private readonly HwndSource _source;
    private readonly Action _show;
    private bool _registered;
    public bool IsRegistered => _registered;
    public int RegistrationError { get; }

    public GlobalHotKey(Action show) : this(show, HotKeySetting.Default) { }
    public GlobalHotKey(Action show, HotKeySetting setting)
    {
        setting.Validate();
        _show = show;
        // A message-only window remains alive when the search window is hidden.
        _source = new HwndSource(new HwndSourceParameters("FastSearch.HotKey") { ParentWindow = new IntPtr(-3) });
        _source.AddHook(HandleMessage);
        uint modifiers = (uint)setting.Modifiers | 0x4000; // MOD_NOREPEAT
        _registered = RegisterHotKey(_source.Handle, Id, modifiers, setting.VirtualKey);
        if (!_registered) RegistrationError = Marshal.GetLastWin32Error();
    }

    private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == Id)
        {
            handled = true;
            _show();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) { UnregisterHotKey(_source.Handle, Id); _registered = false; }
        _source.RemoveHook(HandleMessage);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
