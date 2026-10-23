param([string]$Package = (Join-Path $PSScriptRoot '..\artifacts\FastSearch'))
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class FastSearchManifestReader {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    public static string Read(string path) {
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 2);
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            IntPtr resource = FindResourceW(module, new IntPtr(1), new IntPtr(24));
            if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] bytes = new byte[SizeofResource(module, resource)];
            Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).Trim('\0', '\uFEFF');
        } finally { FreeLibrary(module); }
    }
}
'@
$packageRoot = (Resolve-Path -LiteralPath $Package).Path
foreach ($role in @(@('FastSearch', 'asInvoker'), @('FastSearch.Worker', 'asInvoker'))) {
    foreach ($extension in @('.exe', '.dll', '.deps.json', '.runtimeconfig.json')) {
        $filePath = Join-Path $packageRoot ($role[0] + $extension)
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Missing: $filePath" }
    }
    [xml]$manifest = [FastSearchManifestReader]::Read((Join-Path $packageRoot ($role[0] + '.exe')))
    $execution = $manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
    if ($execution.level -ne $role[1] -or $execution.uiAccess -ne 'false') { throw "Incorrect privilege manifest: $($role[0])" }
    Write-Output "$($role[0]): $($execution.level), uiAccess=false"
}
$workerRuntime = Get-Content -LiteralPath (Join-Path $packageRoot 'FastSearch.Worker.runtimeconfig.json') -Raw | ConvertFrom-Json
$workerFrameworks = @($workerRuntime.runtimeOptions.framework) + @($workerRuntime.runtimeOptions.frameworks)
if ($workerFrameworks.name -contains 'Microsoft.WindowsDesktop.App') { throw 'Worker must not load WPF/WinForms runtime' }
Write-Output 'Worker: no WindowsDesktop framework dependency'
$frontendRuntime = Get-Content -LiteralPath (Join-Path $packageRoot 'FastSearch.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($frontendRuntime.runtimeOptions.configProperties.'System.GC.ConserveMemory' -ne 5) { throw 'Missing frontend memory conservation policy' }
Write-Output 'Frontend: GC.ConserveMemory=5'
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'Setup-Service.ps1'))) { throw 'Missing service installer' }
Write-Output 'Service installer: present'
