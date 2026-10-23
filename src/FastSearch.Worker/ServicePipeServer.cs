using System;
using System.IO;
using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using FastSearch.Core;
using FastSearch.Ipc;
using Microsoft.Win32.SafeHandles;

namespace FastSearch.Worker;

internal sealed class ServicePipeServer(IIndexSource index, string ownerSid, int maxConnections = 8, string? pipeName = null)
{
    private string _status = "索引を準備しています…";
    internal async Task RunAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        index.StatusChanged += text => Volatile.Write(ref _status, text);
        var pipes = new NamedPipeServerStream[maxConnections];
        try
        {
            // Reserve the pipe namespace before accepting any clients.
            for (int i = 0; i < pipes.Length; i++) pipes[i] = Create(i == 0);
            var tasks = new Task[pipes.Length];
            for (int i = 0; i < tasks.Length; i++) tasks[i] = LoopAsync(pipes[i], lifetime.Token);
            await Task.WhenAny(tasks).ConfigureAwait(false);
            lifetime.Cancel();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally { lifetime.Cancel(); foreach (var pipe in pipes) pipe?.Dispose(); }
    }
    private NamedPipeServerStream Create(bool first)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(ownerSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName ?? ServiceProtocol.PipeName(ownerSid), PipeDirection.InOut, maxConnections,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0),
            16 * 1024, 16 * 1024, security);
    }
    private async Task LoopAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool authenticated = false;
                try
                {
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    // A stalled or abandoned session cannot retain an old snapshot indefinitely.
                    deadline.CancelAfter(TimeSpan.FromMinutes(10));
                    using var cancel = deadline.Token.Register(() => pipe.Dispose());
                    // Identify the actual local client process without impersonating it or
                    // consuming a protocol byte. This works across the SYSTEM/UI boundary.
                    string sid = ClientSid(pipe);
                    if (sid != ownerSid) throw new UnauthorizedAccessException("サービスの利用ユーザーが一致しません。");
                    authenticated = true;
                    await Task.Run(() => ServiceSession.Serve(pipe, index, () => Volatile.Read(ref _status), deadline.Token), deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or Win32Exception or System.Security.SecurityException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
                {
                    // EOF, broken pipe and cancellation are normal when a query is replaced.
                    if (ex is UnauthorizedAccessException or InvalidDataException or ArgumentException or Win32Exception or System.Security.SecurityException) IndexManager.Log(ex);
                    if (!authenticated && pipe.IsConnected && !token.IsCancellationRequested)
                    {
                        try
                        {
                            using var error = new BinaryWriter(pipe, Encoding.UTF8, true);
                            error.Write((byte)2);
                            string message = ex is Win32Exception native ? $"{ex.Message} (Win32 {native.NativeErrorCode})" : ex.Message;
                            VolumeProtocol.WriteString(error, "サービスの接続認証に失敗しました: " + message);
                            error.Flush();
                        }
                        catch (Exception sendError) when (sendError is IOException or ObjectDisposedException) { }
                    }
                }
                pipe.Dispose();
                if (!token.IsCancellationRequested) pipe = Create(false);
            }
        }
        finally { pipe.Dispose(); }
    }
    private static string ClientSid(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "パイプの接続元プロセスを確認できません。");
        using var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "接続元プロセスを参照できません。");
        if (!OpenProcessToken(process, 8, out var token)) // TOKEN_QUERY only; never impersonate.
            throw new Win32Exception(Marshal.GetLastWin32Error(), "接続元ユーザーのトークンを参照できません。");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new UnauthorizedAccessException("接続元のユーザーSIDがありません。");
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
