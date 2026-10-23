#Requires -Version 5.1
<#
.SYNOPSIS
Interactively previews, reboots, or installs WSUS updates on AD workstations.

.DESCRIPTION
Run without -Action for a menu: preview, reboot only, WSUS updates only, or
WSUS updates with a reboot when needed. The menu asks for optional exempt
computer names and alternate credentials, then shows the targets. Type RUN
to confirm the batch. Enter at the menu selects preview; Enter at the final
confirmation cancels. Each invocation performs one operation and exits.

Targets enabled U-LT* and U-WS* computer accounts in the selected AD domain
or SearchBase. The executing computer is always excluded by actual host
identity and DNS addresses. Known server accounts are skipped. Additional
exemptions are exact, case-insensitive short names or FQDNs, never wildcards.
An exempt FQDN also exempts that short computer name throughout the search.
In a remote PowerShell session, the executing computer is the session host.

Restarts are forced by default and can discard unsaved work. Choose an
orderly restart in the menu or pass -ForceRestart:$false. Updates-only never
requests a reboot; independent WSUS/GPO deadlines may still enforce one.

AD discovery uses the identity running this script. Alternate credentials
are used only for remote restart requests and WSUS task deployment/result
collection. The script does not serialize or save the credential. Update
tasks run as SYSTEM on each client.

WSUS uses native Windows Update Agent and the client's configured managed
server, with no public Windows Update fallback or policy changes. Only
assigned, non-hidden, uninstalled software installation deployments are
selected. Drivers, feature upgrades and interactive updates are excluded;
selected update EULAs are accepted. WinRM must already be enabled on clients.
Each run does one scan/download/install pass. Prerequisites or exclusive
updates may require another pass. Failed or partial runs do not auto-reboot.
A pending reboot before installation requires a rerun after the reboot.

Interactive update operations wait for results by default. For command-line
use, supply -Wait to collect results. Queued means Task Scheduler accepted
the job, not that updates installed. TimeoutMinutes starts after queueing;
one in-flight WinRM call may extend it. Timeout does not cancel installation.
Clients retain protected worker/configuration, Result.json and Updates.csv
under %ProgramData%\ADWSUSMaintenance-<DeployingUserSID>\<RunId>. Completed
workers remove their task. An existing ADWorkstation-WSUS-Updates task
blocks another deployment; inspect its state and logs before removing it.

Requires Windows PowerShell 5.1, the ActiveDirectory module, DNS client
tools, and an account with permission to administer the remote workstations.
Reboot-only uses DCOM by default; -Protocol WSMan selects WinRM instead.
No remoting, firewall, execution policy or WSUS approval settings are changed.
Offline machines are not queued or retried. RestartRequested confirms only
that the request returned successfully, not that the computer rebooted.

All modes write a UTF-8 CSV without overwriting an existing file. -WhatIf
prevents remote tasks and restarts, while still discovering targets and
writing the local log. AD inventory or log errors stop the operation;
individual DNS, restart and task deployment failures are logged and skipped.

.PARAMETER Action
Omit to use the interactive menu. Preview, Restart, InstallUpdates, and
InstallUpdatesAndRestart are available for noninteractive operation.
Active command-line operations use PowerShell confirmation prompts; pass
-Confirm:$false to suppress them or -WhatIf to preview the requests.

.PARAMETER ExcludeComputer
Additional exact short names or FQDNs to exempt from every operation.
Accepts an array, or comma/semicolon-separated names. Local exclusion is
always applied separately and cannot be disabled.

.PARAMETER Credential
Optional PSCredential for remote operations only. AD queries always use
the launch identity. The menu offers a
Get-Credential prompt when this parameter is omitted. Cancelling that
credential prompt stops the operation.

.PARAMETER ForceRestart
Force permitted restarts. Defaults to true. Has no effect in updates-only
or preview mode. Pass -ForceRestart:$false for orderly restarts.

.PARAMETER Wait
Collect WSUS worker results. Defaults to true in the interactive menu and
false when -Action is supplied. Valid only for an update operation.

.PARAMETER TimeoutMinutes
Maximum result-tracking period after queueing. Defaults to 120 minutes.

.PARAMETER Server
Optional domain DNS name or domain controller for AD discovery.

.PARAMETER SearchBase
Optional OU distinguished name. Defaults to the selected domain root.

.PARAMETER Protocol
DCOM or WSMan for reboot-only requests. WSUS operations always use WinRM.

.PARAMETER LogPath
CSV destination in an existing directory. Defaults to a timestamped file
in the current filesystem directory. Existing logs are never overwritten.

.EXAMPLE
.\Invoke-ADWorkstationMaintenance.ps1
Opens the menu and confirms the target list before performing maintenance.

.EXAMPLE
.\Invoke-ADWorkstationMaintenance.ps1 -ExcludeComputer 'U-LT001' -Credential (Get-Credential)
Opens the menu with a preconfigured exemption and alternate credentials.

.EXAMPLE
.\Invoke-ADWorkstationMaintenance.ps1 -Action Restart -ExcludeComputer 'U-WS002','U-LT001' -WhatIf
Previews forced restart requests while exempting two computers and the host.

.EXAMPLE
.\Invoke-ADWorkstationMaintenance.ps1 -Action InstallUpdatesAndRestart -Wait -Credential (Get-Credential) -Confirm:$false
Installs approved WSUS updates and permits forced restarts when needed.

.EXAMPLE
.\Invoke-ADWorkstationMaintenance.ps1 -Action InstallUpdates -Wait -Confirm:$false
Installs approved WSUS updates without requesting any reboot.
#>
[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='High')]
param(
    [ValidateSet('Preview','Restart','InstallUpdates','InstallUpdatesAndRestart')]
    [string]$Action,
    [string[]]$ExcludeComputer=@(),
    [ValidateNotNull()][pscredential]$Credential,
    [switch]$ForceRestart=$true,
    [switch]$Wait,
    [ValidateRange(1,1440)][int]$TimeoutMinutes=120,
    [ValidateNotNullOrEmpty()][string]$Server,
    [ValidateNotNullOrEmpty()][string]$SearchBase,
    [ValidateSet('DCOM','WSMan')][string]$Protocol='DCOM',
    [ValidateNotNullOrEmpty()][string]$LogPath=(Join-Path (Get-Location).Path ('WorkstationMaintenance-{0}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff')))
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
            if ($null -ne $Configuration.PSObject.Properties['ExcludedComputerNames'] -and
                $system.Name -in @($Configuration.ExcludedComputerNames)) {
                throw 'Worker computer matches the exemption list. No updates will be installed.'
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
    if ($null -ne $options.PSObject.Properties['ExcludedComputerNames'] -and $system.Name -in @($options.ExcludedComputerNames)) {
        return [pscustomobject]@{Status='SkippedExempt';RunDirectory='';Detail='Remote computer matches the exemption list.';Result=$null}
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
        ExcludedComputerNames=@($(if ($null -ne $options.PSObject.Properties['ExcludedComputerNames']) { $options.ExcludedComputerNames }));
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

function Read-YesNo([string]$Prompt,[bool]$Default) {
    $suffix=' [y/N]';if($Default){$suffix=' [Y/n]'}
    while($true) {
        $answer=Read-Host ($Prompt+$suffix)
        if([string]::IsNullOrWhiteSpace($answer)){return $Default}
        switch($answer.Trim().ToLowerInvariant()) {
            'y' {return $true}; 'yes' {return $true}
            'n' {return $false}; 'no' {return $false}
            default {Write-Host 'Enter Y or N.'}
        }
    }
}

$interactive=-not $PSBoundParameters.ContainsKey('Action')
if($interactive) {
    Write-Host 'AD Workstation Maintenance (U-LT* / U-WS*)'
    Write-Host '  1. Preview eligible computers (default)'
    Write-Host '  2. Reboot computers only'
    Write-Host '  3. Install approved WSUS updates; do not request a reboot'
    Write-Host '  4. Install approved WSUS updates and reboot when needed'
    Write-Host '  0. Exit'
    while([string]::IsNullOrWhiteSpace($Action)) {
        $choice=Read-Host 'Choose an operation [1]'
        if([string]::IsNullOrWhiteSpace($choice)){$choice='1'}
        switch($choice.Trim()) {
            '0' {return}
            '1' {$Action='Preview'}
            '2' {$Action='Restart'}
            '3' {$Action='InstallUpdates'}
            '4' {$Action='InstallUpdatesAndRestart'}
            default {Write-Host 'Enter 0, 1, 2, 3 or 4.'}
        }
    }
    $extra=Read-Host 'Additional computers to exempt (comma separated; Enter for none)'
    if(-not [string]::IsNullOrWhiteSpace($extra)){$ExcludeComputer=@($ExcludeComputer)+@($extra)}
    if($Action -ne 'Preview' -and $null -eq $Credential -and (Read-YesNo 'Use alternate credentials for remote operations? AD uses the launch identity.' $false)) {
        $Credential=Get-Credential -Message 'Enter an account permitted to administer the target workstations.'
        if($null -eq $Credential){throw 'Credential entry was cancelled. No maintenance was started.'}
    }
    if($Action -in @('Restart','InstallUpdatesAndRestart') -and -not $PSBoundParameters.ContainsKey('ForceRestart')) {
        $ForceRestart=Read-YesNo 'Force restarts? Unsaved work may be lost.' $true
    }
    if($Action -in @('InstallUpdates','InstallUpdatesAndRestart') -and -not $PSBoundParameters.ContainsKey('Wait')) {
        $Wait=Read-YesNo 'Wait for update results?' $true
    }
}
$install=$Action -in @('InstallUpdates','InstallUpdatesAndRestart')
$restartIfNeeded=$Action -eq 'InstallUpdatesAndRestart'
$forceRestartEnabled=[bool]($ForceRestart -and ($restartIfNeeded -or $Action -eq 'Restart'))
if($Wait -and -not $install){throw '-Wait is only valid for update operations.'}

$exemptNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $ExcludeComputer) {
    foreach($part in ($entry -split '[,;\r\n]+')) {
        if([string]::IsNullOrWhiteSpace($part)){continue}
        $name=$part.Trim().TrimEnd('.')
        if($name.Length -gt 253 -or $name -notmatch '^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$') {
            throw "Invalid exempt computer name '$part'. Use exact short names or FQDNs, without wildcards."
        }
        [void]$exemptNames.Add($name)
        [void]$exemptNames.Add($name.Split('.')[0])
    }
}
function Test-ExemptName([string]$Name) {
    if([string]::IsNullOrWhiteSpace($Name)){return $false}
    $normalized=$Name.TrimEnd('.')
    return ($exemptNames.Contains($normalized) -or $exemptNames.Contains($normalized.Split('.')[0]))
}
function Get-AddressKey([Net.IPAddress]$Address) {
    if($Address.IsIPv4MappedToIPv6){$Address=$Address.MapToIPv4()}
    return [BitConverter]::ToString($Address.GetAddressBytes())
}
function Test-LocalName([string]$Name) {
    if([string]::IsNullOrWhiteSpace($Name)){return $false}
    $normalized=$Name.TrimEnd('.')
    return ($Name -eq '.' -or $normalized -ieq 'localhost' -or $localNames.Contains($normalized) -or $localNames.Contains($normalized.Split('.')[0]))
}
function Test-LocalDnsTarget([string]$Target) {
    $found=$false
    foreach($record in (Resolve-DnsName -Name $Target -Type A_AAAA -DnsOnly -QuickTimeout -ErrorAction Stop)) {
        if($null -eq $record.PSObject.Properties['IPAddress']){continue}
        $address=[Net.IPAddress]::Parse([string]$record.IPAddress)
        if($address.IsIPv4MappedToIPv6){$address=$address.MapToIPv4()}
        $found=$true
        if([Net.IPAddress]::IsLoopback($address) -or $localAddresses.Contains((Get-AddressKey $address))){return $true}
    }
    if(-not $found){throw 'DNS returned no IP addresses.'}
    return $false
}
function Write-ControllerLog([object]$Computer,[string]$Target,[string]$Status,[string]$Detail,[string]$RunDirectory='', [object]$Summary=$null) {
    $installed='';$failed='';$deferred='';$rebootRequired='';$rebootAction=''
    if($null -ne $Summary){$installed=$Summary.Installed;$failed=$Summary.Failed;$deferred=$Summary.Deferred;$rebootRequired=$Summary.RebootRequired;$rebootAction=$Summary.RebootAction}
    $row=[pscustomobject][ordered]@{
        TimestampUtc=[datetime]::UtcNow.ToString('o');ComputerName=$Computer.Name;Target=$Target;RunId=$runId
        Action=$Action;ForceRestart=$forceRestartEnabled;Protocol=$Protocol;Status=$Status;Installed=$installed;Failed=$failed
        Deferred=$deferred;RebootRequired=$rebootRequired;RebootAction=$rebootAction;RunDirectory=$RunDirectory;Detail=$Detail
    }
    $writer.WriteLine(($row|ConvertTo-Csv -NoTypeInformation)[1]);$writer.Flush();$logStream.Flush($true)
    Write-Host ('{0}: {1}' -f $Computer.Name,$Status)
}

$localMachine=[Environment]::MachineName
if([string]::IsNullOrWhiteSpace($localMachine)){throw 'Cannot determine local computer identity.'}
$localNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[void]$localNames.Add($localMachine)
$hostName=[Net.Dns]::GetHostName().TrimEnd('.')
[void]$localNames.Add($hostName);[void]$localNames.Add($hostName.Split('.')[0])
$networkProperties=[Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
if(-not [string]::IsNullOrWhiteSpace($networkProperties.HostName)) {
    [void]$localNames.Add($networkProperties.HostName)
    if(-not [string]::IsNullOrWhiteSpace($networkProperties.DomainName)){[void]$localNames.Add($networkProperties.HostName+'.'+$networkProperties.DomainName)}
}
$localAddresses=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    foreach($address in $adapter.GetIPProperties().UnicastAddresses){[void]$localAddresses.Add((Get-AddressKey $address.Address))}
}
[void]$localAddresses.Add((Get-AddressKey ([Net.IPAddress]::Any)))
[void]$localAddresses.Add((Get-AddressKey ([Net.IPAddress]::IPv6Any)))
Import-Module ActiveDirectory -ErrorAction Stop
$connection=@{ErrorAction='Stop'}
if($PSBoundParameters.ContainsKey('Server')){$connection['Server']=$Server}
$rootDse=Get-ADRootDSE @connection
if([string]::IsNullOrWhiteSpace($rootDse.dnsHostName) -or [string]::IsNullOrWhiteSpace($rootDse.defaultNamingContext)){throw 'AD did not return a controller and domain root.'}
$connection['Server']=$rootDse.dnsHostName
if(-not $PSBoundParameters.ContainsKey('SearchBase')){$SearchBase=$rootDse.defaultNamingContext}
# Finish inventory before sending any maintenance request.
$computers=@(Get-ADComputer -Filter 'Enabled -eq $true -and (Name -like "U-LT*" -or Name -like "U-WS*")' `
    -SearchBase $SearchBase -SearchScope Subtree -Properties DNSHostName,OperatingSystem @connection | Sort-Object Name)
$runId=[guid]::NewGuid().ToString('N')
$remoteParameters=@{ErrorAction='Stop'}
if($install) {
    $workerDefinition="function Invoke-WSUSUpdateWorker {`r`n"+${function:Invoke-WSUSUpdateWorker}.ToString()+"`r`n}"
    $remoteAction=${function:Invoke-WSUSRemoteAction}
    $remoteParameters['SessionOption']=New-PSSessionOption -OpenTimeout 15000 -OperationTimeout 30000
}
if($null -ne $Credential){$remoteParameters['Credential']=$Credential}
$logAbsolute=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
if([IO.Path]::GetExtension($logAbsolute) -ine '.csv'){throw 'LogPath must end in .csv.'}
$logStream=[IO.File]::Open($logAbsolute,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
$writer=$null
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$eligible=[Collections.Generic.List[object]]::new()
$queued=[Collections.Generic.List[object]]::new()
$requestedCount=0;$failedCount=0
try {
    $writer=[IO.StreamWriter]::new($logStream,[Text.UTF8Encoding]::new($true))
    $writer.WriteLine('"TimestampUtc","ComputerName","Target","RunId","Action","ForceRestart","Protocol","Status","Installed","Failed","Deferred","RebootRequired","RebootAction","RunDirectory","Detail"')
    $writer.Flush();$logStream.Flush($true)
    Write-Host ('Always excluded: {0}. AD search base: {1}' -f $localMachine,$SearchBase)
    if($exemptNames.Count -gt 0){Write-Host ('Additional exemptions: '+(($exemptNames|Sort-Object)-join ', '))}
    foreach($computer in $computers) {
        if(-not $computer.Enabled -or -not ($computer.Name -like 'U-LT*' -or $computer.Name -like 'U-WS*')){continue}
        $target=[string]$computer.DNSHostName
        if([string]::IsNullOrWhiteSpace($target)){$target=[string]$computer.Name}
        $target=$target.TrimEnd('.')
        if((Test-LocalName $computer.Name) -or (Test-LocalName $target)){Write-ControllerLog $computer $target 'SkippedLocal' 'Hostname matches local computer.';continue}
        if((Test-ExemptName $computer.Name) -or (Test-ExemptName $target)){Write-ControllerLog $computer $target 'SkippedExempt' 'Computer matches the exemption list.';continue}
        if($computer.OperatingSystem -like '*Server*'){Write-ControllerLog $computer $target 'SkippedServer' 'Known server account.';continue}
        if([string]::IsNullOrWhiteSpace($target) -or $target -match '[\s\\/]'){Write-ControllerLog $computer $target 'InvalidTarget' 'No usable hostname.';continue}
        if(-not $seen.Add($target)){Write-ControllerLog $computer $target 'SkippedDuplicate' 'Target already processed.';continue}
        try{$isLocal=Test-LocalDnsTarget $target}
        catch{Write-ControllerLog $computer $target 'DNSFailed' $_.Exception.Message;continue}
        if($isLocal){Write-ControllerLog $computer $target 'SkippedLocal' 'DNS points to a local address.';continue}
        $eligible.Add([pscustomobject]@{Computer=$computer;Target=$target})
        if($Action -eq 'Preview'){Write-ControllerLog $computer $target 'EligiblePreview' 'No maintenance requested; no update scan performed.'}
        else{Write-ControllerLog $computer $target 'Eligible' 'Target passed inventory checks.'}
    }
    Write-Host ('Operation: {0}. Eligible targets: {1}. Forced restarts: {2}.' -f $Action,$eligible.Count,$forceRestartEnabled)
    if($eligible.Count -gt 0) {
        $eligible|ForEach-Object{[pscustomobject]@{ComputerName=$_.Computer.Name;Target=$_.Target}}|Format-Table -AutoSize|Out-Host
    }
    $cancelled=$false
    if($interactive -and $Action -ne 'Preview' -and $eligible.Count -gt 0 -and -not $WhatIfPreference) {
        if($forceRestartEnabled){Write-Host 'Forced restarts can discard unsaved work.'}
        $confirmation=Read-Host 'Type RUN to perform this operation on all listed computers; Enter cancels'
        $cancelled=($null -eq $confirmation -or $confirmation.Trim() -ine 'RUN')
        # The typed batch confirmation replaces default per-target prompts.
        # Explicit -Confirm still requests the standard PowerShell prompts.
        if(-not $cancelled -and -not $PSBoundParameters.ContainsKey('Confirm')){$ConfirmPreference='None'}
    }
    foreach($entry in $eligible) {
        $computer=$entry.Computer;$target=$entry.Target
        if($Action -eq 'Preview'){continue}
        if($cancelled){Write-ControllerLog $computer $target 'Cancelled' 'Batch confirmation was not supplied.';continue}
        $description='Request computer restart'
        if($install){$description='Install approved WSUS software updates in a SYSTEM task'}
        if($restartIfNeeded){$description+=' and restart when needed'}
        if($forceRestartEnabled){$description+='; forced restart can discard unsaved work'}
        if(-not $PSCmdlet.ShouldProcess($target,$description)) {
            $status='Declined';if($WhatIfPreference){$status='WhatIf'}
            Write-ControllerLog $computer $target $status 'No maintenance request was sent.';continue
        }
        try{$isLocal=Test-LocalDnsTarget $target}
        catch{Write-ControllerLog $computer $target 'DNSFailed' $_.Exception.Message;continue}
        if($isLocal){Write-ControllerLog $computer $target 'SkippedLocal' 'DNS now points to a local address.';continue}
        if(-not $install) {
            $restartParameters=@{ComputerName=$target;Protocol=$Protocol;ErrorAction='Stop';Confirm=$false}
            if($forceRestartEnabled){$restartParameters['Force']=$true}
            if($null -ne $Credential){$restartParameters['Credential']=$Credential}
            Write-ControllerLog $computer $target 'RestartStarting' 'Restart request is about to be sent.'
            $requestError=$null
            try{Restart-Computer @restartParameters}catch{$requestError=$_}
            if($null -ne $requestError){$failedCount++;Write-ControllerLog $computer $target 'RestartFailed' $requestError.Exception.Message;continue}
            $requestedCount++
            Write-ControllerLog $computer $target 'RestartRequested' 'Request returned without error; reboot completion is not verified.'
            continue
        }
        $options=[ordered]@{
            ExpectedComputerName=$computer.Name;ExcludedComputerName=$localMachine
            ExcludedComputerNames=@($exemptNames);RestartIfNeeded=[bool]$restartIfNeeded;ForceRestart=$forceRestartEnabled
        }|ConvertTo-Json -Compress
        Write-ControllerLog $computer $target 'QueueStarting' 'Remote queue request is about to be sent.'
        $queueError=$null;$reply=$null
        try{$reply=Invoke-Command -ComputerName $target -ScriptBlock $remoteAction -ArgumentList @('Queue',$runId,$workerDefinition,$options) @remoteParameters}
        catch{$queueError=$_}
        if($null -ne $queueError){Write-ControllerLog $computer $target 'QueueFailed' ($queueError.Exception.Message+' Inspect the task before retrying an uncertain request.');continue}
        Write-ControllerLog $computer $target $reply.Status $reply.Detail $reply.RunDirectory
        if($reply.Status -eq 'Queued'){$queued.Add([pscustomobject]@{Computer=$computer;Target=$target;RunDirectory=$reply.RunDirectory;LastPhase='';LastError=''})}
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
finally{if($null -ne $writer){$writer.Dispose()};$logStream.Dispose()}
Write-Host ('Eligible: {0}; restart requests accepted: {1}; restart requests failed: {2}; update jobs queued: {3}.' -f $eligible.Count,$requestedCount,$failedCount,$queued.Count)
Write-Host ('Controller log: '+$logAbsolute)
if($Action -eq 'Preview'){Write-Host 'Preview complete. No maintenance was requested.'}
