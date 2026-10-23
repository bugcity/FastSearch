param(
    [ValidateSet('Install', 'Uninstall')][string]$Mode = 'Install',
    [string]$OwnerSid = ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
)
$ErrorActionPreference = 'Stop'
if ($OwnerSid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$') { throw 'Invalid owner SID.' }
$administrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $administrator) {
    $escapedScript = $PSCommandPath.Replace("'", "''")
    $command = "& '$escapedScript' -Mode '$Mode' -OwnerSid '$OwnerSid'; exit `$LASTEXITCODE"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $child = Start-Process -FilePath $shell -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -Verb RunAs -WindowStyle Hidden -PassThru -Wait
    exit $child.ExitCode
}
$serviceName = 'FastSearch.IndexService'
$installRoot = Join-Path $env:ProgramFiles 'FastSearch'
$serviceRoot = Join-Path $installRoot 'Service'
$dataRoot = Join-Path $env:ProgramData 'FastSearch'
$names = @('FastSearch.Worker.exe', 'FastSearch.Worker.dll', 'FastSearch.Worker.deps.json', 'FastSearch.Worker.runtimeconfig.json')
$binary = '"' + (Join-Path $serviceRoot 'FastSearch.Worker.exe') + '" --service'
$transcribing = $false
$staging = $null
function Assert-NoReparse([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while ($candidate) {
        if (Test-Path -LiteralPath $candidate) {
            if ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not permitted: $candidate" }
        }
        $candidate = [IO.Path]::GetDirectoryName($candidate)
    }
}
function Set-ProtectedDirectory([string]$Path) {
    Assert-NoReparse $Path
    if (Test-Path -LiteralPath $Path) {
        $owner = (Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @('S-1-5-18', 'S-1-5-32-544')) { throw "Existing directory must be owned by SYSTEM or Administrators: $Path" }
    }
    [void](New-Item -ItemType Directory -Path $Path -Force)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $identity = New-Object Security.Principal.SecurityIdentifier($sid)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Invoke-ServiceCommand([string[]]$Arguments) {
    $output = & (Join-Path $env:SystemRoot 'System32\sc.exe') @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join [Environment]::NewLine) }
}
function Wait-ServiceState([string]$State) {
    $service = Get-Service -Name $serviceName
    $desired = [Enum]::Parse($service.Status.GetType(), $State)
    $service.WaitForStatus($desired, [TimeSpan]::FromSeconds(35))
}
try {
    Set-ProtectedDirectory $installRoot
    Set-ProtectedDirectory $serviceRoot
    Set-ProtectedDirectory $dataRoot
    foreach ($name in @('owner.sid', 'index.bin', 'setup.log')) { Assert-NoReparse (Join-Path $dataRoot $name) }
    Start-Transcript -LiteralPath (Join-Path $dataRoot 'setup.log') -Force | Out-Null
    $transcribing = $true
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        $registered = (Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $serviceName)).ImagePath
        if ($registered -ne $binary) { throw 'An unexpected service uses this name. No service was changed.' }
    }
    if ($Mode -eq 'Uninstall') {
        if ($existing) {
            if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName; Wait-ServiceState 'Stopped' }
            Invoke-ServiceCommand @('delete', $serviceName)
        }
        Write-Output 'Service uninstalled. The saved index and UI settings are retained.'
        exit 0
    }
    $ownerPath = Join-Path $dataRoot 'owner.sid'
    if ((Test-Path -LiteralPath $ownerPath) -and (Get-Content -LiteralPath $ownerPath -Raw).Trim() -ne $OwnerSid) {
        throw 'This service belongs to a different user. Remove its saved owner/index before assigning another user.'
    }
    $sourceRoot = $PSScriptRoot
    # Running the repository copy is supported after publishing the application.
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'FastSearch.Worker.exe'))) {
        $sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\FastSearch'))
    }
    $staging = Join-Path $installRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
    Set-ProtectedDirectory $staging
    foreach ($name in $names) {
        $sourceFile = Join-Path $sourceRoot $name
        if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { throw "Missing release file: $sourceFile" }
        Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $staging $name)
    }
    $runtime = Get-Content -LiteralPath (Join-Path $staging 'FastSearch.Worker.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ((@($runtime.runtimeOptions.framework) + @($runtime.runtimeOptions.frameworks)).name -contains 'Microsoft.WindowsDesktop.App') {
        throw 'The service must not depend on WindowsDesktop.'
    }
    $backupRoot = Join-Path $installRoot 'Service.Backup'
    Set-ProtectedDirectory $backupRoot
    if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName; Wait-ServiceState 'Stopped' }
    $backedUp = @()
    try {
        foreach ($name in $names) {
            $destination = Join-Path $serviceRoot $name
            Assert-NoReparse $destination
            Assert-NoReparse (Join-Path $backupRoot $name)
            if (Test-Path -LiteralPath $destination) {
                Copy-Item -LiteralPath $destination -Destination (Join-Path $backupRoot $name) -Force
                $backedUp += $name
            }
        }
        foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $staging $name) -Destination (Join-Path $serviceRoot $name) -Force }
        [IO.File]::WriteAllText($ownerPath, $OwnerSid, (New-Object Text.UTF8Encoding($false)))
        if ($existing) { Invoke-ServiceCommand @('config', $serviceName, 'start=', 'delayed-auto') }
        else {
            New-Service -Name $serviceName -BinaryPathName $binary -StartupType Automatic -DisplayName 'FastSearch Index Service' | Out-Null
            Invoke-ServiceCommand @('config', $serviceName, 'start=', 'delayed-auto')
        }
        Invoke-ServiceCommand @('description', $serviceName, 'Maintains the local C: NTFS file-name index for FastSearch.')
        Invoke-ServiceCommand @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')
        Invoke-ServiceCommand @('failureflag', $serviceName, '1')
        Start-Service -Name $serviceName
        Wait-ServiceState 'Running'
    }
    catch {
        $installError = $_
        $failedService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($failedService -and $failedService.Status -ne 'Stopped') { Stop-Service -Name $serviceName; Wait-ServiceState 'Stopped' }
        foreach ($name in $backedUp) { Copy-Item -LiteralPath (Join-Path $backupRoot $name) -Destination (Join-Path $serviceRoot $name) -Force }
        if ($existing) { Start-Service -Name $serviceName }
        elseif (-not $existing -and $failedService) { Invoke-ServiceCommand @('delete', $serviceName) }
        throw $installError
    }
    Write-Output 'FastSearch index service installed and started. Normal UI launches do not require UAC.'
    exit 0
}
catch { Write-Error $_ -ErrorAction Continue; exit 1 }
finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        # Delete only these four known files in this verified, protected staging directory.
        $resolved = [IO.Path]::GetFullPath($staging)
        if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($installRoot) -or [IO.Path]::GetFileName($resolved) -notmatch '^\.staging-[0-9a-f]{32}$') { throw 'Unexpected staging path.' }
        Assert-NoReparse $resolved
        foreach ($name in $names) {
            $stagedFile = Join-Path $resolved $name
            if (Test-Path -LiteralPath $stagedFile) { Remove-Item -LiteralPath $stagedFile -Force }
        }
        Remove-Item -LiteralPath $resolved -Force
    }
}
