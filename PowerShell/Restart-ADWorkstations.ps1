#Requires -Version 5.1
<#
.SYNOPSIS
Previews or requests restarts of AD workstations, excluding the local computer.

.DESCRIPTION
Finds enabled computer accounts named U-LT-* or U-WS-* throughout the selected
AD domain, or within SearchBase if supplied. Known Windows Server accounts
are skipped. The computer executing the script is excluded by short name,
DNS hostname, and any DNS address matching a local network interface or
loopback address. In a remote PowerShell session, the excluded computer is
the session host where the script executes.

The default is a preview. Supply -Restart to request restarts. PowerShell
prompts before each restart; use Yes to All or -Confirm:$false for a batch.
-WhatIf also prevents restart requests. Restarts are forced by default and
can discard unsaved work. Pass -Force:$false to request an orderly restart.

Writes a UTF-8 CSV log in all modes, including -WhatIf. Existing logs are not
overwritten. DNS failures and individual restart failures are logged, and
processing continues with the next computer. AD inventory or logging errors
stop the script. ICMP/ping availability is not required.

RestartRequested means the restart command returned without error; it does
not confirm the machine rebooted or returned online. Offline computers are
not queued or retried. Run from a machine with the ActiveDirectory module
and Windows DNS client tools, using credentials permitted to restart the
remote computers. Remote WMI/DCOM or WinRM access must be allowed, depending
on Protocol. The script does not change firewall or remoting configuration.

.PARAMETER Restart
Enables restart requests. Without it, only the target preview and log are made.

.PARAMETER Force
Forces restarts, potentially discarding unsaved work. On by default.
Pass -Force:$false to disable forced closure of applications.

.PARAMETER Server
Optional domain DNS name or domain controller. Defaults to the current domain.

.PARAMETER SearchBase
Optional OU distinguished name. Defaults to the selected domain's root.

.PARAMETER Protocol
DCOM (default) or WSMan, as supported by Windows PowerShell 5.1 Restart-Computer.

.PARAMETER Credential
Optional alternate credentials for both AD queries and remote restarts.

.PARAMETER LogPath
CSV destination. Its parent directory must exist. Defaults to a timestamped
file in the current filesystem directory.

.EXAMPLE
.\Restart-ADWorkstations.ps1
Previews the eligible computers without restarting anything.

.EXAMPLE
.\Restart-ADWorkstations.ps1 -Restart -WhatIf
Shows the restart operations that would be requested.

.EXAMPLE
.\Restart-ADWorkstations.ps1 -Restart
Requests forced restarts after confirmation. Unsaved work may be lost.

.EXAMPLE
.\Restart-ADWorkstations.ps1 -Restart -Confirm:$false
Requests forced restarts without individual confirmation prompts.

.EXAMPLE
.\Restart-ADWorkstations.ps1 -Restart -Force:$false
Requests orderly restarts without forcing applications to close.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param (
    [switch]$Restart,

    [switch]$Force = $true,

    [ValidateNotNullOrEmpty()]
    [string]$Server,

    [ValidateNotNullOrEmpty()]
    [string]$SearchBase,

    [ValidateSet('DCOM', 'WSMan')]
    [string]$Protocol = 'DCOM',

    [ValidateNotNull()]
    [pscredential]$Credential,

    [ValidateNotNullOrEmpty()]
    [string]$LogPath = (Join-Path (Get-Location).Path (
        'WorkstationRestarts-{0}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    ))
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-AddressKey([Net.IPAddress]$Address) {
    if ($Address.IsIPv4MappedToIPv6) { $Address = $Address.MapToIPv4() }
    return [BitConverter]::ToString($Address.GetAddressBytes())
}

function Test-LocalComputerName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    $normalized = $Name.TrimEnd('.')
    if ($Name -eq '.' -or $normalized -ieq 'localhost') { return $true }
    return ($localNames.Contains($normalized) -or $localNames.Contains($normalized.Split('.')[0]))
}

function Test-LocalDnsTarget([string]$Target) {
    $records = @(Resolve-DnsName -Name $Target -Type A_AAAA -DnsOnly -QuickTimeout -ErrorAction Stop)
    $foundAddress = $false
    foreach ($record in $records) {
        # DNS results can also contain CNAME records without an IPAddress field.
        if ($null -eq $record.PSObject.Properties['IPAddress']) { continue }
        $address = [Net.IPAddress]::Parse([string]$record.IPAddress)
        if ($address.IsIPv4MappedToIPv6) { $address = $address.MapToIPv4() }
        $foundAddress = $true
        if ([Net.IPAddress]::IsLoopback($address) -or $localAddresses.Contains((Get-AddressKey $address))) {
            return $true
        }
    }
    if (-not $foundAddress) { throw "DNS returned no IP addresses for '$Target'." }
    return $false
}

function Write-RestartLog([object]$Computer, [string]$Target, [string]$Status, [string]$Detail) {
    $row = [pscustomobject][ordered]@{
        TimestampUtc = [datetime]::UtcNow.ToString('o')
        ComputerName = $Computer.Name
        DNSHostName  = $Computer.DNSHostName
        Target       = $Target
        Protocol     = $Protocol
        Force        = [bool]$Force
        Status       = $Status
        Detail       = $Detail
    }
    $writer.WriteLine(($row | ConvertTo-Csv -NoTypeInformation)[1])
    $writer.Flush()
    $logStream.Flush($true)
    Write-Host ('{0}: {1}' -f $Computer.Name, $Status)
}

# Read the actual host identity rather than relying on an editable COMPUTERNAME
# environment variable. Include all local interface addresses for alias checks.
$localNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$localMachine = [Environment]::MachineName
if ([string]::IsNullOrWhiteSpace($localMachine)) { throw 'Cannot determine the local computer name.' }
[void]$localNames.Add($localMachine)
$dnsHost = [Net.Dns]::GetHostName().TrimEnd('.')
[void]$localNames.Add($dnsHost)
[void]$localNames.Add($dnsHost.Split('.')[0])
$networkProperties = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
if (-not [string]::IsNullOrWhiteSpace($networkProperties.HostName)) {
    [void]$localNames.Add($networkProperties.HostName)
    if (-not [string]::IsNullOrWhiteSpace($networkProperties.DomainName)) {
        [void]$localNames.Add($networkProperties.HostName + '.' + $networkProperties.DomainName)
    }
}
$localAddresses = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    foreach ($address in $adapter.GetIPProperties().UnicastAddresses) {
        [void]$localAddresses.Add((Get-AddressKey $address.Address))
    }
}
[void]$localAddresses.Add((Get-AddressKey ([Net.IPAddress]::Any)))
[void]$localAddresses.Add((Get-AddressKey ([Net.IPAddress]::IPv6Any)))

Import-Module ActiveDirectory -ErrorAction Stop
$connection = @{ ErrorAction = 'Stop' }
if ($PSBoundParameters.ContainsKey('Server')) { $connection['Server'] = $Server }
if ($PSBoundParameters.ContainsKey('Credential')) { $connection['Credential'] = $Credential }
$rootDse = Get-ADRootDSE @connection
if ([string]::IsNullOrWhiteSpace($rootDse.dnsHostName) -or
    [string]::IsNullOrWhiteSpace($rootDse.defaultNamingContext)) {
    throw 'AD did not return a domain controller and default domain naming context.'
}
$connection['Server'] = $rootDse.dnsHostName
if (-not $PSBoundParameters.ContainsKey('SearchBase')) { $SearchBase = $rootDse.defaultNamingContext }

# Materialize the complete AD result before sending any restart requests.
$computers = @(Get-ADComputer -Filter 'Enabled -eq $true -and (Name -like "U-LT-*" -or Name -like "U-WS-*")' `
    -SearchBase $SearchBase -SearchScope Subtree -Properties DNSHostName, OperatingSystem @connection |
    Sort-Object Name)

$logAbsolute = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
if ([IO.Path]::GetExtension($logAbsolute) -ine '.csv') { throw 'LogPath must end in .csv.' }
$logStream = [IO.File]::Open($logAbsolute, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = $null
$seenTargets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$eligibleCount = 0
$requestedCount = 0
$failedCount = 0
try {
    $writer = [IO.StreamWriter]::new($logStream, [Text.UTF8Encoding]::new($true))
    $writer.WriteLine('"TimestampUtc","ComputerName","DNSHostName","Target","Protocol","Force","Status","Detail"')
    $writer.Flush()
    $logStream.Flush($true)
    Write-Host ('Local computer excluded: {0}. AD search base: {1}' -f $localMachine, $SearchBase)

    foreach ($computer in $computers) {
        if (-not $computer.Enabled -or
            -not ($computer.Name -like 'U-LT-*' -or $computer.Name -like 'U-WS-*')) { continue }
        $target = [string]$computer.DNSHostName
        if ([string]::IsNullOrWhiteSpace($target)) { $target = [string]$computer.Name }
        $target = $target.TrimEnd('.')
        if (Test-LocalComputerName $computer.Name) {
            Write-RestartLog $computer $target 'SkippedLocal' 'AD computer name matches the local computer.'
            continue
        }
        if (Test-LocalComputerName $target) {
            Write-RestartLog $computer $target 'SkippedLocal' 'DNS hostname matches the local computer.'
            continue
        }
        if ($computer.OperatingSystem -like '*Server*') {
            Write-RestartLog $computer $target 'SkippedServer' 'AD identifies the operating system as Windows Server.'
            continue
        }
        if ([string]::IsNullOrWhiteSpace($target) -or $target -match '[\s\\/]') {
            Write-RestartLog $computer $target 'SkippedInvalidTarget' 'AD did not provide a usable computer hostname.'
            continue
        }
        if (-not $seenTargets.Add($target)) {
            Write-RestartLog $computer $target 'SkippedDuplicate' 'This target was already processed.'
            continue
        }
        try { $isLocal = Test-LocalDnsTarget $target }
        catch {
            Write-RestartLog $computer $target 'DNSFailed' $_.Exception.Message
            continue
        }
        if ($isLocal) {
            Write-RestartLog $computer $target 'SkippedLocal' 'A DNS address matches a local interface or loopback address.'
            continue
        }
        $eligibleCount++
        if (-not $Restart) {
            Write-RestartLog $computer $target 'EligiblePreview' 'No restart: -Restart was not supplied.'
            continue
        }
        $action = 'Request computer restart'
        if ($Force) { $action = 'Force computer restart; unsaved work may be lost' }
        if (-not $PSCmdlet.ShouldProcess($target, $action)) {
            $status = 'Declined'
            if ($WhatIfPreference) { $status = 'WhatIf' }
            Write-RestartLog $computer $target $status 'No restart request was sent.'
            continue
        }

        # Refresh the DNS check after any confirmation delay, before requesting
        # a restart by hostname. DNS must remain stable during the operation.
        try { $isLocal = Test-LocalDnsTarget $target }
        catch {
            Write-RestartLog $computer $target 'DNSFailed' $_.Exception.Message
            continue
        }
        if ($isLocal) {
            Write-RestartLog $computer $target 'SkippedLocal' 'DNS now points to a local address.'
            continue
        }
        $restartParameters = @{
            ComputerName = $target
            Protocol     = $Protocol
            ErrorAction  = 'Stop'
            Confirm      = $false
        }
        if ($Force) { $restartParameters['Force'] = $true }
        if ($PSBoundParameters.ContainsKey('Credential')) { $restartParameters['Credential'] = $Credential }
        Write-RestartLog $computer $target 'RestartStarting' 'Restart request is about to be sent.'
        $requestFailure = $null
        try { Restart-Computer @restartParameters }
        catch { $requestFailure = $_ }
        if ($null -ne $requestFailure) {
            $failedCount++
            Write-RestartLog $computer $target 'RestartFailed' $requestFailure.Exception.Message
            continue
        }
        $requestedCount++
        Write-RestartLog $computer $target 'RestartRequested' 'Command returned without error; reboot completion is not verified.'
    }
}
finally {
    if ($null -ne $writer) { $writer.Dispose() }
    $logStream.Dispose()
}

Write-Host ('Eligible targets: {0}; restart requests accepted: {1}; restart requests failed: {2}.' -f $eligibleCount, $requestedCount, $failedCount)
Write-Host ('Log: {0}' -f $logAbsolute)
if (-not $Restart) { Write-Host 'Preview only. Supply -Restart to enable restart requests.' }
