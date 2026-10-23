#Requires -Version 5.1
<#
.SYNOPSIS
Previews or dispatches concurrent forced restarts to Active Directory workstations.
.DESCRIPTION
Run in Windows PowerShell 5.1 (powershell.exe). The interactive menu contains
Preview and Force reboot. Enabled U-LT* and U-WS* accounts are selected from
AD using the identity running the script. Optional credentials apply only
to outbound reboot requests. Passwords are not saved or put on a command line.

Reboots use actual shutdown.exe /m \\IPv4 /r /f /t 300 commands, with an
adjustable countdown and warning. There are 64 concurrent workers by default.
Slow requests occupy individual slots; completed results are processed without
waiting for earlier targets. RequestTimeoutSeconds limits each shutdown.exe
process, not the countdown. A timeout is uncertain: a client may already have
accepted the request. No automatic retries are made.

The executing computer is always excluded by hostname and all local IPs.
Additional exemptions accept exact short names/FQDNs, ignoring case. Known
servers, disabled accounts, duplicates and DNS failures are skipped. DNS/local
address checks run concurrently and repeat immediately before every request.
ComputerName restricts the inventory to selected names. Subnet restricts
resolved addresses to selected IPv4 CIDRs, useful when running from a relay
in each authorized subnet. No addresses or routes are guessed.

Run from an authorized management server that can reach the client subnets,
or from an authorized host within each subnet. Remote Shutdown firewall/network
access and the Force shutdown from a remote system user right are required.
Moving to a server alone does not bypass blocked network paths. This script
does not change firewall, routing, Group Policy, or network configuration.
Reboot-only requires neither WMI nor WinRM.

Forced restarts can discard unsaved work after the countdown. Default delay
is 300 seconds; zero is immediate. RestartRequested records that shutdown.exe
returned success, not that a physical reboot occurred. Already-pending reboot
requests are recorded separately, without cancelling or replacing them.

The menu requires RUN before dispatch. Explicit -Action Restart uses one
batch confirmation; -Confirm:$false suppresses it. -WhatIf writes inventory
and the CSV without sending reboot requests. Existing CSVs are not overwritten.
AD/logging failures stop the operation; individual request failures are logged.

.PARAMETER Action
Preview or Restart. Omit for the interactive menu. Restart always forces closure.
.PARAMETER ComputerName
Optional exact short names/FQDNs to include. Other targeting/exemption rules apply.
.PARAMETER ExcludeComputer
Additional exact computer names/FQDNs to exempt. Arrays or comma-separated lists.
.PARAMETER Subnet
Optional IPv4 CIDRs to include, such as 192.0.2.0/24. Does not change routing.
.PARAMETER Credential
Optional remote PSCredential. AD discovery always uses the launch identity.
.PARAMETER ThrottleLimit
Maximum parallel workers. Default 64; range 1-256.
.PARAMETER RestartDelaySeconds
Restart warning/countdown on each client. Default 300; zero is immediate.
.PARAMETER RequestTimeoutSeconds
Maximum wait for each shutdown.exe process. Default 20; range 5-300.
.PARAMETER RestartMessage
Warning shown on each client. Defaults to a message including the countdown.
.PARAMETER Server
Optional AD domain or domain controller.
.PARAMETER SearchBase
Optional OU distinguished name; defaults to the domain root.
.PARAMETER LogPath
CSV destination in an existing directory. Defaults to a timestamped local file.

.EXAMPLE
powershell.exe -NoProfile -File ".\Invoke-ADWorkstationMaintenance.ps1"
Opens the preview/forced reboot menu.
.EXAMPLE
.\Invoke-ADWorkstationMaintenance.ps1 -Action Restart -ComputerName U-LT001 -Credential (Get-Credential)
Requests a forced restart on one selected workstation, after confirmation.
.EXAMPLE
.\Invoke-ADWorkstationMaintenance.ps1 -Action Restart -Subnet 192.0.2.0/24 -ExcludeComputer U-WS002 -Credential (Get-Credential) -Confirm:$false
Dispatches concurrent requests to the selected subnet with a five-minute warning.
.EXAMPLE
.\Invoke-ADWorkstationMaintenance.ps1 -Action Restart -RestartDelaySeconds 0 -WhatIf
Previews immediate forced restart requests without sending them.
#>
[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='High')]
param(
    [ValidateSet('Preview','Restart')][string]$Action,
    [string[]]$ComputerName=@(),
    [string[]]$ExcludeComputer=@(),
    [string[]]$Subnet=@(),
    [ValidateNotNull()][pscredential]$Credential,
    [ValidateRange(1,256)][int]$ThrottleLimit=64,
    [ValidateRange(0,86400)][int]$RestartDelaySeconds=300,
    [ValidateRange(5,300)][int]$RequestTimeoutSeconds=20,
    [ValidateLength(1,512)][string]$RestartMessage,
    [ValidateNotNullOrEmpty()][string]$Server,
    [ValidateNotNullOrEmpty()][string]$SearchBase,
    [ValidateNotNullOrEmpty()][string]$LogPath=(Join-Path (Get-Location).Path ('WorkstationRestarts-{0}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff')))
)
if($PSVersionTable.PSEdition -ne 'Desktop') {
    throw 'Use Windows PowerShell 5.1 (powershell.exe). Run: powershell.exe -NoProfile -File ".\Invoke-ADWorkstationMaintenance.ps1"'
}

function Initialize-NativeShutdown {
    if('ADWorkstationMaintenance.NativeShutdown' -as [type]){return}
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
namespace ADWorkstationMaintenance {
 public static class NativeShutdown {
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
  struct StartupInfo {
   public int cb; public string reserved,desktop,title; public int x,y,xSize,ySize,xChars,yChars,fill,flags;
   public short show,reservedSize; public IntPtr reserved2,input,output,error;
  }
  [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process,thread; public int processId,threadId; }
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  static extern bool CreateProcessWithLogonW(string user,string domain,IntPtr password,uint logonFlags,string application,StringBuilder command,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo information);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr handle,out uint exit);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(IntPtr handle,uint exit);
  static string Quote(string text) {
   StringBuilder result=new StringBuilder("\""); int slashes=0;
   foreach(char c in text) {
    if(c=='\\'){slashes++;continue;}
    if(c=='"') result.Append('\\',slashes*2+1).Append(c);
    else result.Append('\\',slashes).Append(c);
    slashes=0;
   }
   return result.Append('\\',slashes*2).Append('"').ToString();
  }
  public static string BuildArguments(string target,string message,uint delay) {
   return "/m \\\\"+target+" /r /f /t "+delay+" /d p:4:2 /c "+Quote(message);
  }
  public static void Run(string target,string message,uint delay,int timeoutSeconds,string user,string domain,SecureString password) {
   IPAddress address;
   if(!IPAddress.TryParse(target,out address) || address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork ||
      IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
    throw new ArgumentException("An explicit remote IPv4 address is required.");
   foreach(NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
    foreach(UnicastIPAddressInformation local in adapter.GetIPProperties().UnicastAddresses)
     if(local.Address.Equals(address)) throw new ArgumentException("The executing computer cannot be restarted.");
   string application=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"shutdown.exe");
   string arguments=BuildArguments(address.ToString(),message,delay);
   if(password==null) {
    ProcessStartInfo start=new ProcessStartInfo(application,arguments);
    start.UseShellExecute=false; start.CreateNoWindow=true; start.RedirectStandardOutput=true; start.RedirectStandardError=true;
    using(Process process=Process.Start(start)) {
     System.Threading.Tasks.Task<string> output=process.StandardOutput.ReadToEndAsync();
     System.Threading.Tasks.Task<string> error=process.StandardError.ReadToEndAsync();
     if(!process.WaitForExit(timeoutSeconds*1000)) {
      try {process.Kill();} catch(InvalidOperationException) {}
      throw new TimeoutException("shutdown.exe request timed out. The target may have accepted it; do not automatically retry.");
     }
     string detail=(output.Result+" "+error.Result).Trim();
     if(process.ExitCode!=0) throw new Win32Exception(process.ExitCode,"shutdown.exe exit "+process.ExitCode+": "+detail);
    }
    return;
   }
   IntPtr secret=IntPtr.Zero; ProcessInfo information=new ProcessInfo();
   try {
    secret=Marshal.SecureStringToGlobalAllocUnicode(password);
    StartupInfo startup=new StartupInfo();startup.cb=Marshal.SizeOf(typeof(StartupInfo));
    // LOGON_NETCREDENTIALS_ONLY preserves the launch identity locally and
    // uses the supplied account only for outbound network authentication.
    if(!CreateProcessWithLogonW(user,domain,secret,2,application,new StringBuilder(Quote(application)+" "+arguments),0x08000000,IntPtr.Zero,Path.GetDirectoryName(application),ref startup,out information))
     throw new Win32Exception(Marshal.GetLastWin32Error());
    Marshal.ZeroFreeGlobalAllocUnicode(secret);secret=IntPtr.Zero;
    uint wait=WaitForSingleObject(information.process,(uint)timeoutSeconds*1000u);
    if(wait==258) {
     TerminateProcess(information.process,1460);
     throw new TimeoutException("shutdown.exe request timed out. The target may have accepted it; do not automatically retry.");
    }
    if(wait!=0) throw new Win32Exception(Marshal.GetLastWin32Error());
    uint exit;
    if(!GetExitCodeProcess(information.process,out exit)) throw new Win32Exception(Marshal.GetLastWin32Error());
    if(exit!=0) throw new Win32Exception((int)exit,"shutdown.exe exit "+exit+": "+new Win32Exception((int)exit).Message);
   }
   finally {
    if(secret!=IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(secret);
    if(information.thread!=IntPtr.Zero) CloseHandle(information.thread);
    if(information.process!=IntPtr.Zero) CloseHandle(information.process);
   }
  }
 }
}
'@ -ErrorAction Stop
}

function Invoke-NativeRestart {
    param([string]$Address,[string]$Message,[int]$DelaySeconds,[int]$RequestTimeoutSeconds,[pscredential]$Credential)
    $account=$null;$domain=$null;$password=$null
    if($null -ne $Credential) {
        $account=$Credential.UserName;$password=$Credential.Password
        if($account.Contains('\')){$domain,$account=$account.Split('\',2)}
        elseif(-not $account.Contains('@')){$domain=[Environment]::UserDomainName}
    }
    try{[ADWorkstationMaintenance.NativeShutdown]::Run($Address,$Message,[uint32]$DelaySeconds,$RequestTimeoutSeconds,$account,$domain,$password)}
    catch {
        $exception=$_.Exception
        while($null -ne $exception.InnerException){$exception=$exception.InnerException}
        if($exception -is [TimeoutException]){return [pscustomobject]@{Status='RequestTimedOut';Detail=$exception.Message}}
        if($exception -is [ComponentModel.Win32Exception]) {
            if($exception.NativeErrorCode -eq 1115){return [pscustomobject]@{Status='RestartAlreadyPending';Detail='Windows reports a restart is already pending; it was not replaced.'}}
            throw ('Shutdown request failed with error {0}: {1}' -f $exception.NativeErrorCode,$exception.Message)
        }
        throw
    }
    return [pscustomobject]@{Status='RestartRequested';Detail=('shutdown.exe accepted the request for {0}; countdown {1} seconds. Physical reboot is not verified.' -f $Address,$DelaySeconds)}
}

function Invoke-ComputerMaintenanceTask {
    param([object]$Item,[object]$Settings)
    $ErrorActionPreference='Stop'
    $ipv4=''
    try {
        foreach($record in (Resolve-DnsName -Name $Item.Target -Type A_AAAA -DnsOnly -QuickTimeout -ErrorAction Stop)) {
            if($null -eq $record.PSObject.Properties['IPAddress']){continue}
            $address=[Net.IPAddress]::Parse([string]$record.IPAddress)
            if($address.IsIPv4MappedToIPv6){$address=$address.MapToIPv4()}
            $key=[BitConverter]::ToString($address.GetAddressBytes())
            if([Net.IPAddress]::IsLoopback($address) -or $key -in $Settings.LocalAddresses) {
                return [pscustomobject]@{Status='SkippedLocal';Detail='DNS points to the executing computer or a loopback address.';Address=''}
            }
            if($address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and $ipv4 -eq ''){$ipv4=$address.ToString()}
        }
        if($ipv4 -eq ''){throw 'DNS returned no IPv4 address. No restart was sent.'}
        if($Settings.Subnets.Count -gt 0) {
            $bytes=[Net.IPAddress]::Parse($ipv4).GetAddressBytes()
            $number=[uint64]$bytes[0]*16777216+[uint64]$bytes[1]*65536+[uint64]$bytes[2]*256+[uint64]$bytes[3]
            $matched=$false
            foreach($subnet in $Settings.Subnets){if(($number -band $subnet.Mask) -eq $subnet.Network){$matched=$true;break}}
            if(-not $matched){return [pscustomobject]@{Status='SkippedSubnet';Detail='Resolved IP is outside the selected subnets.';Address=$ipv4}}
        }
    }
    catch{return [pscustomobject]@{Status='DNSFailed';Detail=$_.Exception.Message;Address=''}}
    if($Settings.Operation -eq 'Inventory'){return [pscustomobject]@{Status='Ready';Detail='DNS and local-address checks passed.';Address=$ipv4}}
    try {
        $outcome=Invoke-NativeRestart -Address $ipv4 -Message $Settings.RestartMessage -DelaySeconds $Settings.RestartDelaySeconds -RequestTimeoutSeconds $Settings.RequestTimeoutSeconds -Credential $Settings.Credential
        return [pscustomobject]@{Status=$outcome.Status;Detail=$outcome.Detail;Address=$ipv4}
    }
    catch{return [pscustomobject]@{Status='RestartFailed';Detail=$_.Exception.Message;Address=$ipv4}}
}

function Invoke-MaintenanceParallel {
    param([object[]]$Items,[scriptblock]$ScriptBlock,[object]$Context,[int]$ThrottleLimit=64,[string[]]$FunctionDefinitions=@(),[string[]]$Modules=@())
    if($Items.Count -eq 0){return}
    $state=[Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    if($Modules.Count -gt 0){$state.ImportPSModule($Modules)}
    foreach($definition in $FunctionDefinitions) {
        $tokens=$null;$errors=$null
        $ast=[Management.Automation.Language.Parser]::ParseInput($definition,[ref]$tokens,[ref]$errors)
        if($errors.Count -gt 0){throw 'Invalid parallel worker function definition.'}
        $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)
        $state.Commands.Add([Management.Automation.Runspaces.SessionStateFunctionEntry]::new($node.Name,($node.Body.Extent.Text.Substring(1,$node.Body.Extent.Text.Length-2))))
    }
    $pool=[RunspaceFactory]::CreateRunspacePool(1,$ThrottleLimit,$state,$Host)
    $pool.ApartmentState='MTA';$pool.ThreadOptions='ReuseThread'
    $active=[Collections.Generic.List[object]]::new();$next=0
    try {
        $pool.Open()
        while($next -lt $Items.Count -or $active.Count -gt 0) {
            while($next -lt $Items.Count -and $active.Count -lt $ThrottleLimit) {
                $pipeline=[PowerShell]::Create();$pipeline.RunspacePool=$pool
                [void]$pipeline.AddScript($ScriptBlock.ToString()).AddArgument($Items[$next]).AddArgument($Context)
                $handle=$pipeline.BeginInvoke()
                $active.Add([pscustomobject]@{Pipeline=$pipeline;Handle=$handle;Item=$Items[$next]});$next++
            }
            $completed=$false
            foreach($job in $active.ToArray()) {
                if(-not $job.Handle.IsCompleted){continue}
                $result=$null;$failure=''
                try {
                    $output=@($job.Pipeline.EndInvoke($job.Handle))
                    if($job.Pipeline.HadErrors){$failure=($job.Pipeline.Streams.Error|Out-String).Trim()}
                    elseif($output.Count -eq 1){$result=$output[0]}
                    elseif($output.Count -gt 1){$result=$output}
                }
                catch{$failure=$_.Exception.Message}
                finally{$job.Pipeline.Dispose();[void]$active.Remove($job)}
                [pscustomobject]@{Item=$job.Item;Result=$result;Error=$failure}
                $completed=$true
            }
            if(-not $completed -and $active.Count -gt 0){[Threading.Thread]::Sleep(20)}
        }
    }
    finally {
        foreach($job in $active){try{$job.Pipeline.Stop()}finally{$job.Pipeline.Dispose()}}
        $pool.Dispose()
    }
}
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
function Read-YesNo([string]$Prompt) {
    while($true){$answer=Read-Host ($Prompt+' [y/N]');if([string]::IsNullOrWhiteSpace($answer)){return $false};switch($answer.Trim().ToLowerInvariant()){'y'{return $true};'yes'{return $true};'n'{return $false};'no'{return $false};default{Write-Host 'Enter Y or N.'}}}
}
$interactive=-not $PSBoundParameters.ContainsKey('Action')
if($interactive) {
    Write-Host 'AD Workstation Maintenance — forced reboots'
    Write-Host '  1. Preview eligible computers (default)'
    Write-Host '  2. Force reboot computers'
    Write-Host '  0. Exit'
    while([string]::IsNullOrWhiteSpace($Action)) {
        $choice=Read-Host 'Choose an operation [1]';if([string]::IsNullOrWhiteSpace($choice)){$choice='1'}
        switch($choice.Trim()){'0'{return};'1'{$Action='Preview'};'2'{$Action='Restart'};default{Write-Host 'Enter 0, 1 or 2.'}}
    }
    if(-not $PSBoundParameters.ContainsKey('ComputerName')){$names=Read-Host 'Only these computers (comma separated; Enter for all matching AD computers)';if(-not [string]::IsNullOrWhiteSpace($names)){$ComputerName=@($names)}}
    $extra=Read-Host 'Additional computers to exempt (comma separated; Enter for none)'
    if(-not [string]::IsNullOrWhiteSpace($extra)){$ExcludeComputer=@($ExcludeComputer)+@($extra)}
    if($Action -eq 'Restart' -and $null -eq $Credential -and (Read-YesNo 'Supply remote credentials? AD uses the launch identity.')) {
        $Credential=Get-Credential -Message 'Enter the remote shutdown account (DOMAIN\username or user@domain).'
        if($null -eq $Credential){throw 'Credential entry was cancelled. No restart was requested.'}
    }
    if($Action -eq 'Restart' -and -not $PSBoundParameters.ContainsKey('RestartDelaySeconds')) {
        while($true){$delay=Read-Host 'Countdown in seconds; 0 is immediate [300]';if([string]::IsNullOrWhiteSpace($delay)){$RestartDelaySeconds=300;break};$parsed=0;if([int]::TryParse($delay,[ref]$parsed) -and $parsed -ge 0 -and $parsed -le 86400){$RestartDelaySeconds=$parsed;break};Write-Host 'Enter a number from 0 through 86400.'}
    }
}
if(-not $PSBoundParameters.ContainsKey('RestartMessage')){$RestartMessage='Your computer will restart in {0} seconds to support maintenance. Please save your work.' -f $RestartDelaySeconds}
function New-NameSet([string[]]$Entries) {
    $set=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $Entries){foreach($part in ($entry -split '[,;\r\n]+')){
        if([string]::IsNullOrWhiteSpace($part)){continue};$name=$part.Trim().TrimEnd('.')
        if($name.Length -gt 253 -or $name -notmatch '^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$'){throw "Invalid computer name '$part'. Use exact names without wildcards."}
        [void]$set.Add($name);[void]$set.Add($name.Split('.')[0])
    }}
    return ,$set
}
function Test-NameSet([string]$Name,[object]$Set) {
    if([string]::IsNullOrWhiteSpace($Name)){return $false};$name=$Name.TrimEnd('.')
    return ($Set.Contains($name) -or $Set.Contains($name.Split('.')[0]))
}
function Get-AddressKey([Net.IPAddress]$Address) {
    if($Address.IsIPv4MappedToIPv6){$Address=$Address.MapToIPv4()};return [BitConverter]::ToString($Address.GetAddressBytes())
}
$included=New-NameSet $ComputerName;$exempt=New-NameSet $ExcludeComputer
$subnets=@()
foreach($cidr in $Subnet) {
    $parts=$cidr.Split('/');$address=$null;$prefix=0
    if($parts.Count -ne 2 -or -not [Net.IPAddress]::TryParse($parts[0],[ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or -not [int]::TryParse($parts[1],[ref]$prefix) -or $prefix -lt 0 -or $prefix -gt 32){throw "Invalid IPv4 CIDR '$cidr'. Example: 192.0.2.0/24."}
    $bytes=$address.GetAddressBytes();$number=[uint64]$bytes[0]*16777216+[uint64]$bytes[1]*65536+[uint64]$bytes[2]*256+[uint64]$bytes[3]
    $mask=[uint64]4294967295-([uint64][math]::Pow(2,32-$prefix)-1)
    $subnets+=[pscustomobject]@{Network=($number -band $mask);Mask=$mask}
}
$localMachine=[Environment]::MachineName
if([string]::IsNullOrWhiteSpace($localMachine)){throw 'Cannot determine the executing computer identity.'}
$localNames=New-NameSet @($localMachine,[Net.Dns]::GetHostName(),'localhost')
$localAddresses=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()){foreach($address in $adapter.GetIPProperties().UnicastAddresses){[void]$localAddresses.Add((Get-AddressKey $address.Address))}}
[void]$localAddresses.Add((Get-AddressKey ([Net.IPAddress]::Any)));[void]$localAddresses.Add((Get-AddressKey ([Net.IPAddress]::IPv6Any)))
Import-Module ActiveDirectory -ErrorAction Stop
$connection=@{ErrorAction='Stop'};if($PSBoundParameters.ContainsKey('Server')){$connection['Server']=$Server}
$rootDse=Get-ADRootDSE @connection
if([string]::IsNullOrWhiteSpace($rootDse.dnsHostName) -or [string]::IsNullOrWhiteSpace($rootDse.defaultNamingContext)){throw 'AD did not return a controller and domain root.'}
$connection['Server']=$rootDse.dnsHostName
if(-not $PSBoundParameters.ContainsKey('SearchBase')){$SearchBase=$rootDse.defaultNamingContext}
$computers=@(Get-ADComputer -Filter 'Enabled -eq $true -and (Name -like "U-LT*" -or Name -like "U-WS*")' -SearchBase $SearchBase -SearchScope Subtree -Properties DNSHostName,OperatingSystem @connection|Sort-Object Name)
$definitions=@();foreach($name in @('Invoke-NativeRestart','Invoke-ComputerMaintenanceTask')){$definitions+=('function '+$name+" {`r`n"+(Get-Item -LiteralPath ('Function:\'+$name)).Definition+"`r`n}")}
$settings=[pscustomobject]@{Operation='Inventory';LocalAddresses=@($localAddresses);Subnets=@($subnets);Credential=$Credential;RestartMessage=$RestartMessage;RestartDelaySeconds=$RestartDelaySeconds;RequestTimeoutSeconds=$RequestTimeoutSeconds}
$parallel=@{ScriptBlock={param($Item,$Settings) Invoke-ComputerMaintenanceTask -Item $Item -Settings $Settings};ThrottleLimit=$ThrottleLimit;FunctionDefinitions=$definitions;Modules=@('DnsClient')}
$logAbsolute=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
if([IO.Path]::GetExtension($logAbsolute) -ine '.csv'){throw 'LogPath must end in .csv.'}
$stream=[IO.File]::Open($logAbsolute,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read);$writer=$null
function Write-RestartLog([object]$Entry,[string]$Status,[string]$Detail,[string]$Address='') {
    $row=[pscustomobject][ordered]@{TimestampUtc=[datetime]::UtcNow.ToString('o');ComputerName=$Entry.Computer.Name;Target=$Entry.Target;IPv4=$Address;Action=$Action;CountdownSeconds=$RestartDelaySeconds;Status=$Status;Detail=$Detail}
    $writer.WriteLine(($row|ConvertTo-Csv -NoTypeInformation)[1]);$writer.Flush();Write-Host ('{0}: {1}' -f $Entry.Computer.Name,$Status)
}
$candidates=[Collections.Generic.List[object]]::new();$eligible=[Collections.Generic.List[object]]::new();$seen=New-NameSet @();$requested=0;$failed=0
try {
    $writer=[IO.StreamWriter]::new($stream,[Text.UTF8Encoding]::new($true));$writer.WriteLine('"TimestampUtc","ComputerName","Target","IPv4","Action","CountdownSeconds","Status","Detail"');$writer.Flush()
    Write-Host ('Always excluded: {0}. Parallel workers: {1}. Request timeout: {2} seconds.' -f $localMachine,$ThrottleLimit,$RequestTimeoutSeconds)
    foreach($computer in $computers) {
        if(-not $computer.Enabled -or -not ($computer.Name -like 'U-LT*' -or $computer.Name -like 'U-WS*')){continue}
        $target=[string]$computer.DNSHostName;if([string]::IsNullOrWhiteSpace($target)){$target=[string]$computer.Name};$target=$target.TrimEnd('.')
        $entry=[pscustomobject]@{Computer=$computer;Target=$target}
        if($included.Count -gt 0 -and -not ((Test-NameSet $computer.Name $included) -or (Test-NameSet $target $included))){continue}
        if((Test-NameSet $computer.Name $localNames) -or (Test-NameSet $target $localNames)){Write-RestartLog $entry 'SkippedLocal' 'Hostname matches the executing computer.';continue}
        if((Test-NameSet $computer.Name $exempt) -or (Test-NameSet $target $exempt)){Write-RestartLog $entry 'SkippedExempt' 'Computer matches the exemption list.';continue}
        if($computer.OperatingSystem -like '*Server*'){Write-RestartLog $entry 'SkippedServer' 'AD identifies a server operating system.';continue}
        if([string]::IsNullOrWhiteSpace($target) -or $target -match '[\s\\/]'){Write-RestartLog $entry 'InvalidTarget' 'AD did not provide a usable hostname.';continue}
        if(-not $seen.Add($target)){Write-RestartLog $entry 'SkippedDuplicate' 'This target was already processed.';continue}
        $candidates.Add($entry)
    }
    Invoke-MaintenanceParallel -Items $candidates.ToArray() -Context $settings @parallel|ForEach-Object {
        $entry=$_.Item;$reply=$_.Result
        if($_.Error -ne ''){Write-RestartLog $entry 'InventoryFailed' $_.Error}
        elseif($reply.Status -eq 'Ready'){$eligible.Add($entry);$status='Eligible';if($Action -eq 'Preview'){$status='EligiblePreview'};Write-RestartLog $entry $status $reply.Detail $reply.Address}
        else{Write-RestartLog $entry $reply.Status $reply.Detail $reply.Address}
    }
    Write-Host ('Eligible: {0}. Countdown: {1} seconds. Forced restarts may discard unsaved work.' -f $eligible.Count,$RestartDelaySeconds)
    if($eligible.Count -gt 0){$eligible|Sort-Object {$_.Computer.Name}|ForEach-Object{[pscustomobject]@{ComputerName=$_.Computer.Name;Target=$_.Target}}|Format-Table -AutoSize|Out-Host}
    $approved=$false
    if($Action -eq 'Restart' -and $eligible.Count -gt 0) {
        $cancelled=$false
        if($interactive -and -not $WhatIfPreference){$answer=Read-Host 'Type RUN to force reboot every listed computer; Enter cancels';$cancelled=($null -eq $answer -or $answer.Trim() -ine 'RUN');if(-not $cancelled -and -not $PSBoundParameters.ContainsKey('Confirm')){$ConfirmPreference='None'}}
        if(-not $cancelled){$approved=$PSCmdlet.ShouldProcess(($eligible.Count.ToString()+' listed workstations'),('Dispatch parallel shutdown.exe /r /f requests with '+$RestartDelaySeconds+' second countdown'))}
        if(-not $approved){$status='Declined';if($cancelled){$status='Cancelled'};if($WhatIfPreference){$status='WhatIf'};foreach($entry in $eligible){Write-RestartLog $entry $status 'No restart request was sent.'}}
    }
    if($approved) {
        Initialize-NativeShutdown;$settings.Operation='Restart'
        Write-Host 'Dispatching parallel shutdown.exe requests; reboot completion is not awaited.'
        Invoke-MaintenanceParallel -Items $eligible.ToArray() -Context $settings @parallel|ForEach-Object {
            $entry=$_.Item;$reply=$_.Result
            if($_.Error -ne ''){$failed++;Write-RestartLog $entry 'DispatchFailed' ($_.Error+' Request outcome is uncertain; inspect the target before retrying.')}
            else{Write-RestartLog $entry $reply.Status $reply.Detail $reply.Address;if($reply.Status -in @('RestartRequested','RestartAlreadyPending')){$requested++}elseif($reply.Status -in @('RestartFailed','RequestTimedOut','DNSFailed')){$failed++}}
        }
    }
}
finally{if($null -ne $writer){$writer.Dispose()};$stream.Dispose()}
Write-Host ('Finished dispatch: {0} accepted/already pending; {1} failed or uncertain. Log: {2}' -f $requested,$failed,$logAbsolute)
if($Action -eq 'Preview'){Write-Host 'Preview only. No reboot requests were sent.'}
