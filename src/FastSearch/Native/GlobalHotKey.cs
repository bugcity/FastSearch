using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FastSearch.Native;

public sealed class GlobalHotKey : IDisposable
{
    private const int Id = 0x4653;
    private readonly HwndSource _source;
    private readonly Action _show;
    private bool _registered;
    public bool IsRegistered => _registered;
    public int RegistrationError { get; }

    public GlobalHotKey(Action show)
    {
        _show = show;
        // A message-only window remains alive when the search window is hidden.
        _source = new HwndSource(new HwndSourceParameters("FastSearch.HotKey") { ParentWindow = new IntPtr(-3) });
        _source.AddHook(HandleMessage);
        // Translate the character for the active keyboard layout (US/Japanese layouts included).
        short key = VkKeyScanW('/');
        if (key == -1) { RegistrationError = 87; return; }
        uint modifiers = 0x0008 | 0x4000; // MOD_WIN | MOD_NOREPEAT
        int shift = (key >> 8) & 0xff;
        if ((shift & 1) != 0) modifiers |= 0x0004;
        if ((shift & 2) != 0) modifiers |= 0x0002;
        if ((shift & 4) != 0) modifiers |= 0x0001;
        _registered = RegisterHotKey(_source.Handle, Id, modifiers, (uint)(key & 0xff));
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char character);
}
