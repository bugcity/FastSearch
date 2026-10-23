using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FastSearch.Ipc;

namespace FastSearch.Native;

internal static class ServiceControl
{
    internal static int RunningProcessId()
    {
        IntPtr manager = OpenSCManagerW(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenServiceW(manager, ServiceProtocol.ServiceName, 4);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "索引サービスがありません。「設定」からインストールしてください。");
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (status.State != 4 || status.ProcessId == 0) throw new IOExceptionForService();
                return checked((int)status.ProcessId);
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private sealed class IOExceptionForService() : System.IO.IOException("索引サービスが停止しています。サービスを開始するか「設定」から再インストールしてください。");
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint Type, State, Accepted, Win32Exit, SpecificExit, Checkpoint, WaitHint, ProcessId, Flags;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
