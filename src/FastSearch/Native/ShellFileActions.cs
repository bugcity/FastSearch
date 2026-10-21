using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FastSearch.Native;

public static class ShellFileActions
{
    public static void Open(string path)
    {
        EnsureFileExists(path);
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
    }

    public static void EnsureFileExists(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("ファイルが見つからないか、アクセスできません。", path);
    }

    // Diagnostic probe: bind/query/release the real handler without displaying or invoking a command.
    internal static int ProbeContextMenu(string path)
    {
        EnsureFileExists(path);
        Guid itemId = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out var item));
        object? menuObject = null;
        IntPtr popup = IntPtr.Zero;
        try
        {
            Guid handler = new("3981e225-f559-11d3-8e3a-00c04f6837d5");
            Guid menuId = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref handler, ref menuId, out menuObject));
            popup = CreatePopupMenu();
            if (popup == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            int result = ((IContextMenu)menuObject).QueryContextMenu(popup, 0, 1, 0x7fff, 0);
            Marshal.ThrowExceptionForHR(result);
            return result & 0xffff;
        }
        finally
        {
            if (popup != IntPtr.Zero) DestroyMenu(popup);
            if (menuObject is not null && Marshal.IsComObject(menuObject)) Marshal.ReleaseComObject(menuObject);
            Marshal.ReleaseComObject(item);
        }
    }

    // Uses the file's own shell object, so registered file-type handlers supply the commands.
    public static void ShowContextMenu(Window owner, string path, Point screenPoint)
    {
        EnsureFileExists(path);
        var hwnd = new WindowInteropHelper(owner).EnsureHandle();
        var source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("ウィンドウを取得できません。");
        Guid itemId = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out var item));
        object? menuObject = null;
        IntPtr popup = IntPtr.Zero;
        HwndSourceHook? hook = null;
        try
        {
            Guid handler = new("3981e225-f559-11d3-8e3a-00c04f6837d5"); // BHID_SFUIObject
            Guid menuId = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref handler, ref menuId, out menuObject));
            var menu = (IContextMenu)menuObject;
            var menu3 = menuObject as IContextMenu3;
            var menu2 = menuObject as IContextMenu2;
            hook = (IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (message is not (0x0117 or 0x002B or 0x002C or 0x0120)) return IntPtr.Zero;
                if (menu3 is not null)
                {
                    int hr = menu3.HandleMenuMsg2((uint)message, w, l, out IntPtr result);
                    if (hr == 0) { handled = true; return result; }
                }
                else if (menu2 is not null && message != 0x0120 && menu2.HandleMenuMsg((uint)message, w, l) == 0)
                    handled = true;
                return IntPtr.Zero;
            };
            source.AddHook(hook);
            popup = CreatePopupMenu();
            if (popup == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            Marshal.ThrowExceptionForHR(menu.QueryContextMenu(popup, 0, 1, 0x7FFF, shift ? 0x100u : 0));
            uint command = TrackPopupMenuEx(popup, 0x0102, (int)screenPoint.X, (int)screenPoint.Y, hwnd, IntPtr.Zero);
            if (command == 0) return;
            var invocation = new InvokeCommandInfo
            {
                Size = Marshal.SizeOf<InvokeCommandInfo>(),
                Mask = 0x00004000 | 0x20000000 | (shift ? 0x10000000u : 0) | (control ? 0x40000000u : 0),
                Owner = hwnd,
                Verb = new IntPtr(command - 1),
                VerbUnicode = new IntPtr(command - 1),
                Show = 1,
                Point = new NativePoint { X = (int)screenPoint.X, Y = (int)screenPoint.Y }
            };
            Marshal.ThrowExceptionForHR(menu.InvokeCommand(ref invocation));
        }
        finally
        {
            if (hook is not null) source.RemoveHook(hook);
            if (popup != IntPtr.Zero) DestroyMenu(popup);
            if (menuObject is not null && Marshal.IsComObject(menuObject)) Marshal.ReleaseComObject(menuObject);
            Marshal.ReleaseComObject(item);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct InvokeCommandInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Owner, Verb, Parameters, Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon, Title, VerbUnicode, ParametersUnicode, DirectoryUnicode, TitleUnicode;
        public NativePoint Point;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint type, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    // Explicitly declare every vtable slot on each interface to keep COM inheritance layout unambiguous.
    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint position, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeCommandInfo command);
        [PreserveSig] int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint length);
    }
    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint position, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeCommandInfo command);
        [PreserveSig] int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint length);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint position, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeCommandInfo command);
        [PreserveSig] int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint length);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);
}
