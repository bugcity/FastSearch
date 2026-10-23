using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FastSearch.Core;
using FastSearch.Ipc;

namespace FastSearch.Worker;

/// <summary>Minimal SCM host with no WPF, service framework, or extra runtime package.</summary>
internal static class WindowsServiceHost
{
    private static readonly ServiceMainCallback MainCallback = ServiceMain;
    private static readonly ControlCallback HandlerCallback = HandleControl;
    private static readonly CancellationTokenSource Stop = new();
    private static readonly object StatusLock = new();
    private static IntPtr _handle;
    private static bool _shutdown, _stopping, _finished;
    private static uint _checkpoint;
    internal static int Run()
    {
        var table = new[] { new ServiceTable { Name = ServiceProtocol.ServiceName, Main = MainCallback }, new ServiceTable() };
        return StartServiceCtrlDispatcherW(table) ? 0 : Marshal.GetLastWin32Error();
    }
    private static void ServiceMain(int count, IntPtr args)
    {
        _handle = RegisterServiceCtrlHandlerExW(ServiceProtocol.ServiceName, HandlerCallback, IntPtr.Zero);
        if (_handle == IntPtr.Zero) return;
        Report(2, 0, 30_000);
        uint error = 0;
        try { RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { IndexManager.Log(ex); error = 1064; }
        finally { Report(1, error, 0); }
    }
    private static async Task RunAsync()
    {
        IndexManager.LogDirectoryOverride = ServiceProtocol.DataDirectory;
        string sid = ServiceProtocol.OwnerSid;
        using var index = new IndexManager(Path.Combine(ServiceProtocol.DataDirectory, "index.bin"),
            token => new LocalIndexVolume(token), "C:\\ の索引サービスを開始しています…");
        var server = new ServicePipeServer(index, sid);
        var serving = server.RunAsync(Stop.Token);
        if (serving.IsFaulted) await serving.ConfigureAwait(false);
        index.Start();
        Report(4, 0, 0);
        try
        {
            await Task.WhenAny(serving, Task.Delay(Timeout.Infinite, Stop.Token)).ConfigureAwait(false);
            if (!Stop.IsCancellationRequested) await serving.ConfigureAwait(false);
        }
        finally
        {
            lock (StatusLock) _stopping = true;
            Stop.Cancel();
            Report(3, 0, 30_000);
            using var progress = new Timer(_ => Report(3, 0, 30_000), null, 2000, 2000);
            await index.StopAsync(_shutdown).ConfigureAwait(false);
            try { await serving.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }
    private static uint HandleControl(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control is 1 or 5)
        {
            lock (StatusLock) { _shutdown |= control == 5; _stopping = true; }
            Report(3, 0, 30_000);
            Stop.Cancel();
            return 0;
        }
        if (control == 4) { Report(_stopping ? 3u : 4u, 0, _stopping ? 30_000u : 0); return 0; }
        return 120;
    }
    private static void Report(uint state, uint error, uint waitHint)
    {
        lock (StatusLock)
        {
            if (_finished) return;
            if (state == 1) _finished = true;
            if (_stopping && state == 4) state = 3;
            var status = new ServiceStatus
            {
                Type = 0x10, State = state, Accepted = state == 4 ? 5u : 0,
                Win32Exit = error, WaitHint = waitHint,
                Checkpoint = state is 2 or 3 ? ++_checkpoint : 0
            };
            SetServiceStatus(_handle, ref status);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMainCallback(int count, IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ControlCallback(uint control, uint eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ServiceTable
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public ServiceMainCallback? Main;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint Type, State, Accepted, Win32Exit, SpecificExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW([In] ServiceTable[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, ControlCallback callback, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
