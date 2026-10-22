#Requires -Version 5.1
<#
.SYNOPSIS
Previews or starts installation of approved WSUS updates on AD workstations.

.DESCRIPTION
Targets enabled U-LT* and U-WS* computers, excluding the executing computer
by hostname and local IP addresses. Known server accounts are skipped, and
the remote host identity is checked again before task creation.

The default is an AD target preview; it does not scan individual PCs for
updates. -Install creates and starts a one-time SYSTEM scheduled task on each
reachable target through WinRM. The worker uses native Windows Update Agent
APIs, explicitly selects the configured managed WSUS server, and searches for
assigned, non-hidden, uninstalled software updates deployed for installation.
Drivers, feature upgrades, and updates requiring user input are excluded.
Update EULAs are accepted for selected approved updates. No third-party module,
public Windows Update fallback, WSUS approval change, or policy change is used.

-RestartIfNeeded permits a reboot after successful installation, or when a
pending reboot prevents installation. These reboots are forced by default
and can discard unsaved work. Pass -ForceRestart:$false for an orderly reboot.
Without -RestartIfNeeded this script does not request a reboot; existing
WSUS/GPO deadlines can independently enforce restarts.
If a reboot is needed before installation, rerun after the reboot. Each run
does one scan/download/install pass; prerequisite updates can require another
pass after restarting. Failed or partially successful runs do not auto-reboot.

Without -Wait, Queued means only that Task Scheduler accepted the job. -Wait
polls completed summaries until TimeoutMinutes after all jobs are queued.
An in-flight WinRM call can finish after that tracking deadline.
A tracking timeout does not stop or cancel installation on the remote PC.
Offline PCs are not queued. Failed or uncertain queue requests are not retried.

The controller CSV logs targeting, queueing, and collected summaries. Each PC
also keeps Result.json, Updates.csv, and its worker/configuration beneath
%ProgramData%\ADWSUSMaintenance-<DeployingUserSID>\<RunId>. That directory is restricted to SYSTEM,
Administrators, and the account deploying the job. Completed workers unregister
their task and retain logs.
A task named ADWorkstation-WSUS-Updates left after interruption prevents a new
job; inspect its logs and state before manually removing that task.

Requires Windows PowerShell 5.1, AD and DNS tools on the controller, working
WinRM to the clients, and credentials with remote administrative access.
The script does not enable WinRM or change firewall settings. -WhatIf prevents
remote task creation; the local CSV is still written. Existing CSVs are not
overwritten. In a remote session, the excluded computer is the session host.

.PARAMETER RestartIfNeeded
Permits reboots when needed by the update operation. Off by default.

.PARAMETER ForceRestart
Forces permitted reboots, potentially discarding unsaved work. On by default.
Pass -ForceRestart:$false for an orderly reboot. Requires -RestartIfNeeded
when explicitly enabled.

.EXAMPLE
.\Install-WSUSWorkstationUpdates.ps1
Previews the target computers without creating remote tasks.

.EXAMPLE
.\Install-WSUSWorkstationUpdates.ps1 -Install -Wait
Starts WSUS update jobs after confirmation and collects their results.

.EXAMPLE
.\Install-WSUSWorkstationUpdates.ps1 -Install -Wait -RestartIfNeeded
Also permits a forced reboot when needed. Unsaved work may be lost.
Use Yes to All for the confirmation prompts.

.EXAMPLE
.\Install-WSUSWorkstationUpdates.ps1 -Install -Wait -RestartIfNeeded -Confirm:$false
Installs approved updates and permits forced reboots without individual prompts.

.EXAMPLE
.\Install-WSUSWorkstationUpdates.ps1 -Install -Wait -RestartIfNeeded -ForceRestart:$false
Installs approved updates and permits orderly reboots when needed.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param (
    [switch]$Install,
    [switch]$RestartIfNeeded,
    [switch]$ForceRestart = $true,
    [switch]$Wait,
    [ValidateRange(1,1440)][int]$TimeoutMinutes = 120,
    [ValidateNotNullOrEmpty()]
    [string]$Server,
    [ValidateNotNullOrEmpty()]
    [string]$SearchBase,
    [ValidateNotNull()]
    [pscredential]$Credential,
    [ValidateNotNullOrEmpty()]
    [string]$LogPath = (Join-Path (Get-Location).Path ('WSUSUpdates-{0}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff')))
)

function Invoke-WSUSUpdateWorker {
    param([object]$Configuration)
    Set-StrictMode -Version 2.0
    $ErrorActionPreference = 'Stop'
    $rows = [Collections.Generic.List[object]]::new()
    $result = [ordered]@{
        RunId=$Configuration.RunId; ComputerName=$Configuration.ExpectedComputerName
        Status='Starting'; Complete=$false; StartedUtc=[datetime]::UtcNow.ToString('o'); CompletedUtc=''
        WSUSServer=''; Found=0; Selected=0; Installed=0; Failed=0; Skipped=0; Deferred=0
        RebootRequired=$false; RebootAction='None'; BootTimeBeforeRebootUtc=''; RebootVerifiedUtc=''; Note=''; Error=''; CleanupError=''
    }
    $resultPath = Join-Path $Configuration.RunDirectory 'Result.json'
    function Save-WorkerResult {
        $temporary = $resultPath + '.tmp'
        [IO.File]::WriteAllText($temporary, ($result | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($true))
        if ([IO.File]::Exists($resultPath)) { [IO.File]::Replace($temporary, $resultPath, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $resultPath) }
    }
    Save-WorkerResult
    try {
        # The inner block allows one-pass early exits while retaining the
        # shared final logging, task cleanup, and optional reboot handling.
        & {
            $system = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
            $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
            if ($system.Name -ieq $Configuration.ExcludedComputerName -or
                $system.Name -ine $Configuration.ExpectedComputerName -or
                $system.Name -notmatch '^U-(LT|WS)' -or $os.ProductType -ne 1) {
                throw 'Worker identity check failed: expected a different, matching Windows workstation.'
            }
            $result.BootTimeBeforeRebootUtc=$os.LastBootUpTime.ToUniversalTime().ToString('o')
            $policyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
            $policy = Get-ItemProperty -LiteralPath $policyPath -ErrorAction Stop
            $auPolicy = Get-ItemProperty -LiteralPath ($policyPath + '\AU') -ErrorAction Stop
            $wsusUri = $null
            if ($auPolicy.UseWUServer -ne 1 -or
                -not [uri]::TryCreate([string]$policy.WUServer, [UriKind]::Absolute, [ref]$wsusUri) -or
                $wsusUri.Scheme -notin @('http', 'https')) {
                throw 'This workstation is not configured to use a WSUS server. No fallback is allowed.'
            }
            $result.WSUSServer = [string]$policy.WUServer
            $systemInfo = New-Object -ComObject Microsoft.Update.SystemInfo
            if ($systemInfo.RebootRequired) {
                $result.Status = 'RebootRequiredBeforeInstall'
                $result.RebootRequired = $true
                return
            }
            $session = New-Object -ComObject Microsoft.Update.Session
            $session.ClientApplicationID = 'AD WSUS Workstation Maintenance'
            $searcher = $session.CreateUpdateSearcher()
            $searcher.ServerSelection = 1 # ssManagedServer: never public Windows Update.
            $searcher.Online = $true
            $result.Status = 'Searching'
            Save-WorkerResult
            $search = $searcher.Search("IsInstalled=0 and IsHidden=0 and IsAssigned=1 and Type='Software' and DeploymentAction='Installation'")
            if ([int]$search.ResultCode -ne 2) { throw "WSUS search did not succeed completely (result $($search.ResultCode))." }
            $result.Found = $search.Updates.Count
            $selected = New-Object -ComObject Microsoft.Update.UpdateColl
            $selectedRows = [Collections.Generic.List[object]]::new()
            for ($index=0; $index -lt $search.Updates.Count; $index++) {
                $update = $search.Updates.Item($index)
                $row = [pscustomobject][ordered]@{
                    UpdateID=$update.Identity.UpdateID; KB=($update.KBArticleIDs -join ';'); Title=$update.Title
                    Status='Selected'; HResult=''; RebootRequired=$false
                }
                $rows.Add($row)
                $isUpgrade = $false
                for ($category=0; $category -lt $update.Categories.Count; $category++) {
                    if ($update.Categories.Item($category).CategoryID -ieq '3689bdc8-b205-4af4-8d4a-a63924c5e9d5') { $isUpgrade=$true }
                }
                if ($isUpgrade) { $row.Status='SkippedFeatureUpgrade'; $result.Skipped++; continue }
                if ($update.InstallationBehavior.CanRequestUserInput) { $row.Status='SkippedInteractive'; $result.Skipped++; continue }
                if (-not $update.EulaAccepted) { [void]$update.AcceptEula() }
                [void]$selected.Add($update)
                $selectedRows.Add($row)
            }
            $result.Selected = $selected.Count
            if ($selected.Count -eq 0) { $result.Status='NoApplicableUpdates'; return }

            $installer = $session.CreateUpdateInstaller()
            if ($installer.IsBusy) { throw 'Windows Update is already installing updates. This run will not interfere.' }
            $result.Status = 'Downloading'
            Save-WorkerResult
            $downloader = $session.CreateUpdateDownloader()
            $downloader.Updates = $selected
            $download = $downloader.Download()
            $ready = New-Object -ComObject Microsoft.Update.UpdateColl
            $readyRows = [Collections.Generic.List[object]]::new()
            for ($index=0; $index -lt $selected.Count; $index++) {
                $update = $selected.Item($index)
                $downloadItem = $download.GetUpdateResult($index)
                $row = $selectedRows[$index]
                $row.HResult = '0x{0:X8}' -f $downloadItem.HResult
                if ([int]$downloadItem.ResultCode -eq 2 -and $update.IsDownloaded) {
                    $row.Status='Downloaded'
                    [void]$ready.Add($update)
                    $readyRows.Add($row)
                }
                else { $row.Status='DownloadFailed'; $result.Failed++ }
            }
            if ($ready.Count -eq 0) { $result.Status='Failed'; $result.Error='No selected updates downloaded successfully.'; return }
            # WUA rejects an exclusive update combined with any other update.
            # Install one such update alone in this pass and retain explicit
            # deferred results for the remainder, including other exclusives.
            $exclusiveIndex=-1
            for ($index=0;$index -lt $ready.Count;$index++) {
                if ([int]$ready.Item($index).InstallationBehavior.Impact -eq 2) { $exclusiveIndex=$index;break }
            }
            if ($exclusiveIndex -ge 0 -and $ready.Count -gt 1) {
                $exclusive=New-Object -ComObject Microsoft.Update.UpdateColl
                [void]$exclusive.Add($ready.Item($exclusiveIndex))
                $exclusiveRow=$readyRows[$exclusiveIndex]
                for ($index=0;$index -lt $readyRows.Count;$index++) {
                    if ($index -ne $exclusiveIndex) { $readyRows[$index].Status='DeferredExclusive';$result.Deferred++ }
                }
                $ready=$exclusive
                $readyRows=[Collections.Generic.List[object]]::new()
                $readyRows.Add($exclusiveRow)
                $result.Note='Other downloaded updates were deferred for a separate pass because an exclusive update was selected.'
            }
            $installer.Updates = $ready
            $installer.AllowSourcePrompts = $false
            $installer.ForceQuiet = $true
            if ($installer.RebootRequiredBeforeInstallation) {
                $result.Status='RebootRequiredBeforeInstall'; $result.RebootRequired=$true; return
            }
            $result.Status = 'Installing'
            Save-WorkerResult
            $installation = $installer.Install()
            for ($index=0; $index -lt $ready.Count; $index++) {
                $installedItem = $installation.GetUpdateResult($index)
                $row = $readyRows[$index]
                $row.HResult = '0x{0:X8}' -f $installedItem.HResult
                $row.RebootRequired = [bool]$installedItem.RebootRequired
                if ([int]$installedItem.ResultCode -eq 2) { $row.Status='Installed'; $result.Installed++ }
                else { $row.Status='InstallFailed'; $result.Failed++ }
            }
            $result.RebootRequired = [bool]($installation.RebootRequired -or (New-Object -ComObject Microsoft.Update.SystemInfo).RebootRequired)
            if ($result.Failed -gt 0 -or [int]$installation.ResultCode -ne 2) {
                if ($result.Installed -gt 0) { $result.Status='PartialFailure' }
                else { $result.Status='Failed' }
                $result.Error = "One or more update operations failed (installation result $($installation.ResultCode))."
            }
            elseif ($result.Deferred -gt 0) { $result.Status='MoreUpdatesPending' }
            else { $result.Status='Installed' }
        }
    }
    catch { $result.Status='Failed'; $result.Error=$_.Exception.Message }
    finally {
        try {
            $updatesPath = Join-Path $Configuration.RunDirectory 'Updates.csv'
            if ($rows.Count -gt 0) { $rows | Export-Csv -LiteralPath $updatesPath -NoTypeInformation -Encoding UTF8 -ErrorAction Stop }
            else { '"UpdateID","KB","Title","Status","HResult","RebootRequired"' | Out-File -LiteralPath $updatesPath -Encoding UTF8 -ErrorAction Stop }
        }
        catch { $result.Status='Failed'; $result.Error='Cannot write per-update results: ' + $_.Exception.Message }
        try { Unregister-ScheduledTask -TaskName $Configuration.TaskName -Confirm:$false -ErrorAction Stop }
        catch { $result.CleanupError=$_.Exception.Message }
        if ($Configuration.RestartIfNeeded -and $result.RebootRequired -and $result.Failed -eq 0 -and
            $result.Status -in @('Installed', 'MoreUpdatesPending', 'RebootRequiredBeforeInstall')) { $result.RebootAction='RequestStarting' }
        $result.Complete = ($result.RebootAction -ne 'RequestStarting')
        if ($result.Complete) { $result.CompletedUtc = [datetime]::UtcNow.ToString('o') }
        Save-WorkerResult
    }
    if ($result.RebootAction -eq 'RequestStarting') {
        try {
            $restartParameters = @{ ComputerName=$Configuration.ExpectedComputerName; Confirm=$false; ErrorAction='Stop' }
            if ($Configuration.ForceRestart) { $restartParameters['Force']=$true }
            Restart-Computer @restartParameters
            $result.RebootAction='Requested'
        }
        catch { $result.RebootAction='Failed'; $result.Status='RebootFailed'; $result.Error='Reboot request failed: ' + $_.Exception.Message }
        $result.Complete=$true
        $result.CompletedUtc=[datetime]::UtcNow.ToString('o')
        Save-WorkerResult
    }
}

function Invoke-WSUSRemoteAction {
    param([string]$Action, [string]$RunId, [string]$WorkerDefinition, [string]$OptionsJson)
    Set-StrictMode -Version 2.0
    $ErrorActionPreference='Stop'
    $parsedId=[guid]::Empty
    if (-not [guid]::TryParseExact($RunId, 'N', [ref]$parsedId)) { throw 'Invalid run identifier.' }
    $taskName='ADWorkstation-WSUS-Updates'
    $deployingIdentity=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $root=Join-Path $env:ProgramData ('ADWSUSMaintenance-'+$deployingIdentity.Value)
    $runDirectory=Join-Path $root $RunId
    if ($Action -eq 'Collect') {
        $resultFile=Join-Path $runDirectory 'Result.json'
        if (Test-Path -LiteralPath $resultFile) {
            $data=Get-Content -LiteralPath $resultFile -Raw -ErrorAction Stop | ConvertFrom-Json
            if ($data.RunId -ne $RunId) { throw 'Worker result does not match this run.' }
            if (-not $data.Complete -and $data.RebootAction -eq 'RequestStarting') {
                # A successful reboot can terminate PowerShell before the
                # worker records the return from Restart-Computer. Verify a
                # later OS boot rather than treating that snapshot as success.
                $before=[datetime]::Parse($data.BootTimeBeforeRebootUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)
                $currentBoot=(Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime.ToUniversalTime()
                if ($currentBoot -gt $before.AddSeconds(1)) {
                    $data.RebootAction='RebootObserved';$data.Complete=$true
                    $data.RebootVerifiedUtc=[datetime]::UtcNow.ToString('o');$data.CompletedUtc=$data.RebootVerifiedUtc
                    $data.Note=($data.Note+' A later OS boot was observed after the reboot request.').Trim()
                    $temporary=$resultFile+'.collector.tmp'
                    [IO.File]::WriteAllText($temporary,($data|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($true))
                    [IO.File]::Replace($temporary,$resultFile,[System.Management.Automation.Language.NullString]::Value)
                }
            }
            return [pscustomobject]@{Status='Result';RunDirectory=$runDirectory;Detail='';Result=$data}
        }
        $task=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($null -ne $task -and [string]$task.State -ne 'Running') {
            $information=Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction Stop
            if ($information.LastRunTime -gt [datetime]'2000-01-01' -and $information.LastTaskResult -ne 267011) {
                return [pscustomobject]@{Status='TaskFailed';RunDirectory=$runDirectory;Detail=('Task stopped without a result; last task code: '+$information.LastTaskResult);Result=$null}
            }
        }
        return [pscustomobject]@{Status='Pending';RunDirectory=$runDirectory;Detail='No worker result yet.';Result=$null}
    }
    if ($Action -ne 'Queue') { throw 'Unknown remote action.' }
    $options=$OptionsJson | ConvertFrom-Json
    $system=Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
    $os=Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    if ($system.Name -ieq $options.ExcludedComputerName) {
        return [pscustomobject]@{Status='SkippedLocal';RunDirectory='';Detail='Remote session is the excluded computer.';Result=$null}
    }
    if ($os.ProductType -ne 1 -or $system.Name -notmatch '^U-(LT|WS)' -or $system.Name -ine $options.ExpectedComputerName) {
        throw 'Remote identity does not match the expected Windows workstation.'
    }
    $policy=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' -ErrorAction Stop
    $au=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -ErrorAction Stop
    if ($au.UseWUServer -ne 1 -or [string]::IsNullOrWhiteSpace($policy.WUServer)) { throw 'Client is not WSUS-managed.' }
    if ($null -ne (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) {
        return [pscustomobject]@{Status='ExistingTask';RunDirectory='';Detail='An existing update task must finish or be inspected first.';Result=$null}
    }

    # Create the payload location with restricted permissions before writing
    # any script that SYSTEM will execute. Reject untrusted precreated roots.
    $administrators=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    if (Test-Path -LiteralPath $root) {
        $directory=Get-Item -LiteralPath $root -Force -ErrorAction Stop
        if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The maintenance directory is not a normal directory.'
        }
        $owner=(Get-Acl -LiteralPath $root -ErrorAction Stop).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @($administrators.Value,$systemSid.Value,$deployingIdentity.Value)) { throw 'The maintenance directory has an untrusted owner.' }
    }
    $security=[Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true,$false)
    $security.SetOwner($deployingIdentity)
    $inheritance=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach ($sid in @($administrators,$systemSid,$deployingIdentity)) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid,[Security.AccessControl.FileSystemRights]::FullControl,$inheritance,
            [Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    }
    [void][IO.Directory]::CreateDirectory($root,$security)
    [IO.Directory]::SetAccessControl($root,$security)
    if (Test-Path -LiteralPath $runDirectory) { throw 'Run directory already exists; refusing an uncertain replay.' }
    [void][IO.Directory]::CreateDirectory($runDirectory,$security)
    $workerFile=Join-Path $runDirectory 'Worker.ps1'
    $configFile=Join-Path $runDirectory 'Configuration.json'
    $configuration=[ordered]@{
        RunId=$RunId;TaskName=$taskName;RunDirectory=$runDirectory;ExpectedComputerName=$options.ExpectedComputerName
        ExcludedComputerName=$options.ExcludedComputerName;RestartIfNeeded=[bool]$options.RestartIfNeeded;ForceRestart=[bool]$options.ForceRestart
    }
    [IO.File]::WriteAllText($workerFile,$WorkerDefinition,[Text.UTF8Encoding]::new($true))
    [IO.File]::WriteAllText($configFile,($configuration|ConvertTo-Json),[Text.UTF8Encoding]::new($true))
    $workerLiteral="'"+$workerFile.Replace("'","''")+"'"
    $configLiteral="'"+$configFile.Replace("'","''")+"'"
    $bootstrap=". ([scriptblock]::Create([IO.File]::ReadAllText($workerLiteral))); Invoke-WSUSUpdateWorker -Configuration (Get-Content -LiteralPath $configLiteral -Raw | ConvertFrom-Json)"
    $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
    $executable=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $taskAction=New-ScheduledTaskAction -Execute $executable -Argument ('-NoProfile -NonInteractive -EncodedCommand '+$encoded)
    $principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings=New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([timespan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    # No trigger and no retry: start just this requested run. Registration is
    # deliberately not forced, so a concurrent controller cannot replace a job.
    Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $principal -Settings $settings -Description ('WSUS workstation updates; run '+$RunId) -ErrorAction Stop | Out-Null
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    return [pscustomobject]@{Status='Queued';RunDirectory=$runDirectory;Detail='Task Scheduler accepted the job; installation is not yet verified.';Result=$null}
}

Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
if ($PSBoundParameters.ContainsKey('ForceRestart') -and $ForceRestart -and -not $RestartIfNeeded) { throw '-ForceRestart requires -RestartIfNeeded.' }
if ($Wait -and -not $Install) { throw '-Wait requires -Install.' }
$forceRestartEnabled=[bool]($RestartIfNeeded -and $ForceRestart)

function Get-AddressKey([Net.IPAddress]$Address) {
    if ($Address.IsIPv4MappedToIPv6) {$Address=$Address.MapToIPv4()}
    return [BitConverter]::ToString($Address.GetAddressBytes())
}
function Test-LocalName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) {return $false}
    $nameValue=$Name.TrimEnd('.')
    return ($Name -eq '.' -or $nameValue -ieq 'localhost' -or $localNames.Contains($nameValue) -or $localNames.Contains($nameValue.Split('.')[0]))
}
function Test-LocalDnsTarget([string]$Target) {
    $found=$false
    foreach ($record in (Resolve-DnsName -Name $Target -Type A_AAAA -DnsOnly -QuickTimeout -ErrorAction Stop)) {
        if ($null -eq $record.PSObject.Properties['IPAddress']) {continue}
        $address=[Net.IPAddress]::Parse([string]$record.IPAddress)
        if ($address.IsIPv4MappedToIPv6) {$address=$address.MapToIPv4()}
        $found=$true
        if ([Net.IPAddress]::IsLoopback($address) -or $localAddresses.Contains((Get-AddressKey $address))) {return $true}
    }
    if (-not $found) {throw 'DNS returned no IP addresses.'}
    return $false
}
function Write-ControllerLog([object]$Computer,[string]$Target,[string]$Status,[string]$Detail,[string]$RunDirectory='', [object]$Summary=$null) {
    $installed='';$failed='';$deferred='';$rebootRequired='';$rebootAction=''
    if ($null -ne $Summary) {$installed=$Summary.Installed;$failed=$Summary.Failed;$deferred=$Summary.Deferred;$rebootRequired=$Summary.RebootRequired;$rebootAction=$Summary.RebootAction}
    $row=[pscustomobject][ordered]@{
        TimestampUtc=[datetime]::UtcNow.ToString('o');ComputerName=$Computer.Name;Target=$Target;RunId=$runId
        Status=$Status;Installed=$installed;Failed=$failed;Deferred=$deferred;RebootRequired=$rebootRequired;RebootAction=$rebootAction;RunDirectory=$RunDirectory;Detail=$Detail
    }
    $writer.WriteLine(($row|ConvertTo-Csv -NoTypeInformation)[1]);$writer.Flush();$logStream.Flush($true)
    Write-Host ('{0}: {1}' -f $Computer.Name,$Status)
}

$localMachine=[Environment]::MachineName
if ([string]::IsNullOrWhiteSpace($localMachine)) {throw 'Cannot determine local computer identity.'}
$localNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$localNames.Add($localMachine)
$hostName=[Net.Dns]::GetHostName().TrimEnd('.')
[void]$localNames.Add($hostName);[void]$localNames.Add($hostName.Split('.')[0])
$localAddresses=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    foreach ($address in $adapter.GetIPProperties().UnicastAddresses) {[void]$localAddresses.Add((Get-AddressKey $address.Address))}
}
[void]$localAddresses.Add((Get-AddressKey ([Net.IPAddress]::Any)))
[void]$localAddresses.Add((Get-AddressKey ([Net.IPAddress]::IPv6Any)))
Import-Module ActiveDirectory -ErrorAction Stop
$connection=@{ErrorAction='Stop'}
if ($PSBoundParameters.ContainsKey('Server')) {$connection['Server']=$Server}
if ($PSBoundParameters.ContainsKey('Credential')) {$connection['Credential']=$Credential}
$rootDse=Get-ADRootDSE @connection
if ([string]::IsNullOrWhiteSpace($rootDse.dnsHostName) -or [string]::IsNullOrWhiteSpace($rootDse.defaultNamingContext)) {throw 'AD did not return a controller and domain root.'}
$connection['Server']=$rootDse.dnsHostName
if (-not $PSBoundParameters.ContainsKey('SearchBase')) {$SearchBase=$rootDse.defaultNamingContext}
$computers=@(Get-ADComputer -Filter 'Enabled -eq $true -and (Name -like "U-LT*" -or Name -like "U-WS*")' `
    -SearchBase $SearchBase -SearchScope Subtree -Properties DNSHostName,OperatingSystem @connection | Sort-Object Name)
$runId=[guid]::NewGuid().ToString('N')
$workerDefinition="function Invoke-WSUSUpdateWorker {`r`n"+${function:Invoke-WSUSUpdateWorker}.ToString()+"`r`n}"
$remoteAction=${function:Invoke-WSUSRemoteAction}
$remoteParameters=@{ErrorAction='Stop';SessionOption=(New-PSSessionOption -OpenTimeout 15000 -OperationTimeout 30000)}
if ($PSBoundParameters.ContainsKey('Credential')) {$remoteParameters['Credential']=$Credential}
$logAbsolute=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
if ([IO.Path]::GetExtension($logAbsolute) -ine '.csv') {throw 'LogPath must end in .csv.'}
$logStream=[IO.File]::Open($logAbsolute,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$writer=$null
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$queued=[Collections.Generic.List[object]]::new()
try {
    $writer=[IO.StreamWriter]::new($logStream,[Text.UTF8Encoding]::new($true))
    $writer.WriteLine('"TimestampUtc","ComputerName","Target","RunId","Status","Installed","Failed","Deferred","RebootRequired","RebootAction","RunDirectory","Detail"')
    $writer.Flush();$logStream.Flush($true)
    Write-Host ('Excluded local computer: {0}. Run ID: {1}' -f $localMachine,$runId)
    foreach ($computer in $computers) {
        if (-not $computer.Enabled -or -not ($computer.Name -like 'U-LT*' -or $computer.Name -like 'U-WS*')) {continue}
        $target=[string]$computer.DNSHostName
        if ([string]::IsNullOrWhiteSpace($target)) {$target=[string]$computer.Name}
        $target=$target.TrimEnd('.')
        if ((Test-LocalName $computer.Name) -or (Test-LocalName $target)) {Write-ControllerLog $computer $target 'SkippedLocal' 'Hostname matches local computer.';continue}
        if ($computer.OperatingSystem -like '*Server*') {Write-ControllerLog $computer $target 'SkippedServer' 'Known server account.';continue}
        if ([string]::IsNullOrWhiteSpace($target) -or $target -match '[\s\\/]') {Write-ControllerLog $computer $target 'InvalidTarget' 'No usable hostname.';continue}
        if (-not $seen.Add($target)) {Write-ControllerLog $computer $target 'SkippedDuplicate' 'Target already processed.';continue}
        try {$isLocal=Test-LocalDnsTarget $target}
        catch {Write-ControllerLog $computer $target 'DNSFailed' $_.Exception.Message;continue}
        if ($isLocal) {Write-ControllerLog $computer $target 'SkippedLocal' 'DNS points to a local address.';continue}
        if (-not $Install) {Write-ControllerLog $computer $target 'EligiblePreview' 'No remote task created; update scan not performed.';continue}
        $action='Install approved WSUS software updates in a SYSTEM task'
        if ($RestartIfNeeded) {$action+=' and restart when needed'}
        if ($forceRestartEnabled) {$action+='; unsaved work may be lost'}
        if (-not $PSCmdlet.ShouldProcess($target,$action)) {
            $status='Declined';if ($WhatIfPreference) {$status='WhatIf'}
            Write-ControllerLog $computer $target $status 'No remote task created.';continue
        }
        try {$isLocal=Test-LocalDnsTarget $target}
        catch {Write-ControllerLog $computer $target 'DNSFailed' $_.Exception.Message;continue}
        if ($isLocal) {Write-ControllerLog $computer $target 'SkippedLocal' 'DNS now points to a local address.';continue}
        $options=[ordered]@{ExpectedComputerName=$computer.Name;ExcludedComputerName=$localMachine;RestartIfNeeded=[bool]$RestartIfNeeded;ForceRestart=$forceRestartEnabled}|ConvertTo-Json -Compress
        Write-ControllerLog $computer $target 'QueueStarting' 'Remote queue request is about to be sent.'
        $queueError=$null;$reply=$null
        try {$reply=Invoke-Command -ComputerName $target -ScriptBlock $remoteAction -ArgumentList @('Queue',$runId,$workerDefinition,$options) @remoteParameters}
        catch {$queueError=$_}
        if ($null -ne $queueError) {Write-ControllerLog $computer $target 'QueueFailed' ($queueError.Exception.Message+' Inspect the task before retrying an uncertain request.');continue}
        Write-ControllerLog $computer $target $reply.Status $reply.Detail $reply.RunDirectory
        if ($reply.Status -eq 'Queued') {$queued.Add([pscustomobject]@{Computer=$computer;Target=$target;RunDirectory=$reply.RunDirectory;LastPhase='';LastError=''})}
    }
    if ($Wait -and $queued.Count -gt 0) {
        $deadline=[datetime]::UtcNow.AddMinutes($TimeoutMinutes)
        $pending=[Collections.Generic.List[object]]::new()
        foreach ($job in $queued) {$pending.Add($job)}
        while ($pending.Count -gt 0 -and [datetime]::UtcNow -lt $deadline) {
            foreach ($job in $pending.ToArray()) {
                if ([datetime]::UtcNow -ge $deadline) {break}
                $reply=$null
                try {$reply=Invoke-Command -ComputerName $job.Target -ScriptBlock $remoteAction -ArgumentList @('Collect',$runId,'','') @remoteParameters}
                catch {$job.LastError=$_.Exception.Message;continue}
                if ($reply.Status -eq 'TaskFailed') {
                    Write-ControllerLog $job.Computer $job.Target 'TaskFailed' $reply.Detail $job.RunDirectory
                    [void]$pending.Remove($job);continue
                }
                if ($null -ne $reply.Result) {
                    $summary=$reply.Result
                    if ($summary.RunId -ne $runId) {throw 'Collected result belongs to another run.'}
                    if ($summary.Complete) {
                        $detail=($summary.Note+' '+$summary.Error+' '+$summary.CleanupError).Trim()
                        Write-ControllerLog $job.Computer $job.Target $summary.Status $detail $job.RunDirectory $summary
                        [void]$pending.Remove($job)
                    }
                    elseif ($job.LastPhase -ne $summary.Status) {
                        $job.LastPhase=$summary.Status
                        Write-ControllerLog $job.Computer $job.Target $summary.Status 'Worker still running.' $job.RunDirectory $summary
                    }
                }
            }
            if ($pending.Count -gt 0) {
                $remainingMilliseconds=[math]::Max(0,($deadline-[datetime]::UtcNow).TotalMilliseconds)
                if ($remainingMilliseconds -gt 0) {Start-Sleep -Milliseconds ([int][math]::Min(10000,$remainingMilliseconds))}
            }
        }
        foreach ($job in $pending) {Write-ControllerLog $job.Computer $job.Target 'TrackingTimedOut' ('Installation was not cancelled. Last collection error: '+$job.LastError) $job.RunDirectory}
    }
}
finally {if ($null -ne $writer) {$writer.Dispose()};$logStream.Dispose()}
Write-Host ('Queued jobs: {0}. Controller log: {1}' -f $queued.Count,$logAbsolute)
if (-not $Install) {Write-Host 'Preview only. Supply -Install to create update tasks.'}
