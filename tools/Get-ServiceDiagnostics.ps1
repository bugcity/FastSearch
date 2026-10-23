param()
$ErrorActionPreference = 'Stop'
$script:phase = 'startup'
$pipe = $null
Write-Output 'FastSearch service diagnostics (read-only)'
Write-Output ('Time: ' + [DateTimeOffset]::Now.ToString('O'))
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$sid = $identity.User.Value
Write-Output ('Client SID: ' + $sid)
Write-Output ('Elevated: ' + ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
try {
    $service = Get-CimInstance Win32_Service -Filter "Name='FastSearch.IndexService'"
    if ($null -eq $service) { Write-Output 'Service: not installed' }
    else {
        Write-Output ('Service state: ' + $service.State)
        Write-Output ('Service PID: ' + $service.ProcessId)
        Write-Output ('Service account: ' + $service.StartName)
        Write-Output ('Service command: ' + $service.PathName)
    }
} catch { Write-Output ('Service query: ' + $_.Exception.Message) }
$dataRoot = Join-Path $env:ProgramData 'FastSearch'
foreach ($name in @('index.bin', 'owner.sid', 'error.log', 'setup.log')) {
    try {
        $file = Get-Item -LiteralPath (Join-Path $dataRoot $name) -Force
        Write-Output ('Data file: {0}; bytes={1}; modified={2:O}' -f $name, $file.Length, $file.LastWriteTime)
        if ($name -eq 'owner.sid') { Write-Output ('Installed owner SID: ' + (Get-Content -LiteralPath $file.FullName -Raw).Trim()) }
    } catch { Write-Output ('Data file ' + $name + ': ' + $_.Exception.Message) }
}
foreach ($folder in @((Join-Path $env:ProgramFiles 'FastSearch\Service'), $PSScriptRoot)) {
    foreach ($name in @('FastSearch.Worker.dll', 'FastSearch.dll')) {
        $path = Join-Path $folder $name
        try {
            $file = Get-Item -LiteralPath $path -Force
            Write-Output ('Binary: {0}; bytes={1}; modified={2:O}; SHA256={3}' -f $file.FullName, $file.Length, $file.LastWriteTime, (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)
        } catch { Write-Output ('Binary ' + $path + ': ' + $_.Exception.Message) }
    }
}
function Read-Bytes([int]$Length, [string]$Label) {
    $script:phase = $Label
    $buffer = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $pending = $pipe.ReadAsync($buffer, $offset, $Length - $offset)
        if (-not $pending.Wait(5000)) { $pipe.Dispose(); throw ('Timed out reading ' + $Label) }
        $read = $pending.GetAwaiter().GetResult()
        if ($read -eq 0) { throw ('EOF at ' + $Label + '; received ' + $offset + '/' + $Length + ' bytes') }
        $offset += $read
    }
    return ,$buffer
}
function Read-Int32([string]$Label) { return [BitConverter]::ToInt32((Read-Bytes 4 $Label), 0) }
function Read-String([string]$Label) {
    $length = Read-Int32 ($Label + ' length')
    if ($length -lt 0 -or $length -gt 16384) { throw ('Invalid string length: ' + $length) }
    return [Text.Encoding]::UTF8.GetString((Read-Bytes $length $Label))
}
try {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class FastSearchDiagnosticPipe {
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
'@
    $script:phase = 'connect'
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', ('FastSearch.Service.' + $sid), [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous, [Security.Principal.TokenImpersonationLevel]::Identification)
    $pipe.Connect(3000)
    [uint32]$serverPid = 0
    if (-not [FastSearchDiagnosticPipe]::GetNamedPipeServerProcessId($pipe.SafePipeHandle, [ref]$serverPid)) { throw 'Cannot verify the pipe server PID' }
    Write-Output ('Pipe server PID: ' + $serverPid)
    if ($service -and $service.ProcessId -ne $serverPid) { throw 'Pipe server PID does not match the Windows service' }
    $script:phase = 'write status request'
    $writer = [IO.BinaryWriter]::new($pipe, [Text.Encoding]::UTF8, $true)
    $writer.Write([byte[]]@(0x46,0x46,0x53,0x32,2,0,0,0,1))
    $writer.Flush()
    $status = (Read-Bytes 1 'response status')[0]
    Write-Output ('Response status code: ' + $status)
    $message = Read-String 'status message'
    Write-Output ('Status message: ' + $message)
    if ($status -ne 0) { throw 'Service returned an error response' }
    $ready = (Read-Bytes 1 'ready flag')[0] -ne 0
    $count = Read-Int32 'entry count'
    $revision = [BitConverter]::ToInt64((Read-Bytes 8 'revision'), 0)
    $reportedPid = Read-Int32 'reported PID'
    Write-Output ('Ready: ' + $ready)
    Write-Output ('Indexed entries: ' + $count)
    Write-Output ('Revision: ' + $revision)
    Write-Output ('Reported PID: ' + $reportedPid)
    Write-Output 'Complete status response received.'
} catch {
    Write-Output ('FAILED PHASE: ' + $script:phase)
    Write-Output $_.Exception.ToString()
} finally { if ($null -ne $pipe) { $pipe.Dispose() } }
