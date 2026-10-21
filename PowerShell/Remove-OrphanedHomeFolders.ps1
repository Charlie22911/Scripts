#Requires -Version 5.1
<#
.SYNOPSIS
Previews or deletes old home folders without a matching Active Directory user.

.DESCRIPTION
Matches immediate child folder names to AD sAMAccountName, ignoring case.
Enabled and disabled accounts both count as existing accounts. Searches all
OUs in one domain; other domains are not searched automatically.

OlderThanDays is based on the NEWEST LastWriteTimeUtc of the folder and every
file and subfolder within it. Everything must be strictly older than the
cutoff. This measures modification age, not time since an AD account was
deleted. Empty folders use their own modification time.

The default is a preview. Actual deletion requires -Delete. With -Delete,
PowerShell prompts for each eligible folder unless you pass -Confirm:$false.
The standard -WhatIf switch also prevents deletion. A CSV audit log is written
in every mode, including -WhatIf. Existing logs are never overwritten.

Before deletion, the script rechecks the folder's age, path, and AD account
against the same domain controller used for the initial lookup. It skips
folders containing reparse points (including junctions and symbolic links).
All share roots must be listed successfully before any deletion can start.
AD, enumeration, logging, or deletion errors stop further processing.

Run with an account that can read all users in the selected domain, list all
share contents, and delete the selected folders. Access-based enumeration
can hide folders or files if permissions are insufficient. Folder names must
match sAMAccountName; renamed accounts or a different naming convention need
review. Use a quiet maintenance window: checks and deletion are not atomic,
and a deletion failure can leave a partially removed folder.

.PARAMETER HomeSharePath
One or more home-share roots, not individual user folders. Root paths must
be filesystem directories, must not be reparse points, and must not overlap.

.PARAMETER OlderThanDays
Required positive age in days. A value of 180 retains any folder with a
modification timestamp within the last 180 days, including its contents.

.PARAMETER LogPath
CSV audit destination outside all specified share roots. Its parent directory
must exist. Defaults to a timestamped CSV in the current filesystem directory.

.PARAMETER Server
Optional domain DNS name or domain controller. Defaults to the current domain.
The responding domain controller is used for all subsequent AD checks.

.PARAMETER Delete
Enables deletion. Without this switch the script only previews candidates.

.PARAMETER IncludeAllFolders
Checks all immediate child folders. By default, names must contain a period
with text on both sides, such as first.last.

.EXAMPLE
.\Remove-OrphanedHomeFolders.ps1 -HomeSharePath '\\fs01\HomeShare' -OlderThanDays 180
Previews candidates and writes a CSV log without deleting folders.

.EXAMPLE
.\Remove-OrphanedHomeFolders.ps1 -HomeSharePath '\\fs01\HomeShare' -OlderThanDays 180 -Delete -WhatIf
Exercises the deletion selection in WhatIf mode and writes a CSV log.

.EXAMPLE
.\Remove-OrphanedHomeFolders.ps1 -HomeSharePath '\\fs01\HomeShare' -OlderThanDays 180 -Delete
Prompts before permanently deleting each eligible folder and its contents.

.NOTES
Requires Windows PowerShell 5.1 and the Microsoft ActiveDirectory module.
Deletion does not use the Recycle Bin. DeleteStarting without a later Deleted
entry means the outcome needs inspection, for example after an interruption.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param (
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$HomeSharePath,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 36500)]
    [int]$OlderThanDays,

    [ValidateNotNullOrEmpty()]
    [string]$LogPath = (Join-Path (Get-Location).Path (
        'HomeFolderCleanup-{0}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    )),

    [ValidateNotNullOrEmpty()]
    [string]$Server,

    [switch]$Delete,

    [switch]$IncludeAllFolders
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$cutoffUtc = [datetime]::UtcNow.AddDays(-$OlderThanDays)

function Get-NormalizedPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Assert-DirectChild([string]$Path, [string]$Root) {
    $target = Get-NormalizedPath $Path
    $parent = Get-NormalizedPath ([IO.Path]::GetDirectoryName($target))
    if (-not $parent.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -or
        $target.Equals($Root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a target outside the immediate children of '$Root': $Path"
    }
}

function Get-TreeState([string]$Path) {
    $top = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $top.PSIsContainer -or $top.PSProvider.Name -ne 'FileSystem') {
        throw "The candidate is no longer a filesystem directory: $Path"
    }
    $state = [pscustomobject]@{
        NewestWriteTimeUtc = $top.LastWriteTimeUtc
        CreationTimeUtc    = $top.CreationTimeUtc
        ReparsePointPath   = ''
    }
    $basePath = Get-NormalizedPath $top.FullName
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($top.FullName)
    while ($pending.Count -gt 0) {
        $directory = Get-Item -LiteralPath $pending.Pop() -Force -ErrorAction Stop
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            $state.ReparsePointPath = $directory.FullName
            return $state
        }
        if ($directory.LastWriteTimeUtc -gt $state.NewestWriteTimeUtc) {
            $state.NewestWriteTimeUtc = $directory.LastWriteTimeUtc
        }
        # Enumerate one level at a time so Windows PowerShell 5.1 never
        # recursively follows a junction or symbolic link during age checks.
        foreach ($child in (Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction Stop)) {
            $childPath = Get-NormalizedPath $child.FullName
            if (-not $childPath.StartsWith($basePath + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw "A child path escaped the candidate directory: $($child.FullName)"
            }
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $state.ReparsePointPath = $child.FullName
                return $state
            }
            if ($child.LastWriteTimeUtc -gt $state.NewestWriteTimeUtc) {
                $state.NewestWriteTimeUtc = $child.LastWriteTimeUtc
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
    return $state
}

function Write-CleanupLog(
    [object]$Folder,
    [string]$Status,
    [string]$Detail,
    [string]$NewestWriteTimeUtc = ''
) {
    $row = [pscustomobject][ordered]@{
        TimestampUtc       = [datetime]::UtcNow.ToString('o')
        FolderName         = $Folder.Name
        FolderPath         = $Folder.FullName
        NewestWriteTimeUtc = $NewestWriteTimeUtc
        CutoffUtc          = $cutoffUtc.ToString('o')
        ADServer           = $adServerName
        Status             = $Status
        Detail             = $Detail
    }
    $writer.WriteLine(($row | ConvertTo-Csv -NoTypeInformation)[1])
    $writer.Flush()
    $logStream.Flush($true)
}

# Validate and list every share before processing any deletion candidates.
$roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$inventory = [Collections.Generic.List[object]]::new()
foreach ($path in $HomeSharePath) {
    $root = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($root.PSProvider.Name -ne 'FileSystem' -or -not $root.PSIsContainer -or
        ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The share root must be a filesystem directory without a reparse point: $path"
    }
    if ($root.FullName -match '^[A-Za-z]:\\$') {
        throw "Specify a home-share directory rather than an entire drive: $path"
    }
    $rootPath = Get-NormalizedPath $root.FullName
    if (-not $roots.Add($rootPath)) { continue }
    foreach ($folder in (Get-ChildItem -LiteralPath $root.FullName -Directory -Force -ErrorAction Stop)) {
        Assert-DirectChild $folder.FullName $rootPath
        $inventory.Add([pscustomobject]@{ Folder = $folder; Root = $rootPath })
    }
}
foreach ($firstRoot in $roots) {
    foreach ($secondRoot in $roots) {
        if ($secondRoot.StartsWith($firstRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Overlapping share roots are not allowed: '$firstRoot' and '$secondRoot'"
        }
    }
}

$logAbsolute = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
if ([IO.Path]::GetExtension($logAbsolute) -ine '.csv') { throw 'LogPath must end in .csv.' }
$logParent = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($logAbsolute)) -Force -ErrorAction Stop
if ($logParent.PSProvider.Name -ne 'FileSystem' -or -not $logParent.PSIsContainer) {
    throw 'The log parent must be an existing filesystem directory.'
}
foreach ($rootPath in $roots) {
    if ($logAbsolute.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The audit log must be outside all specified home-share roots.'
    }
}

Import-Module ActiveDirectory -ErrorAction Stop
$connection = @{ ErrorAction = 'Stop' }
if ($PSBoundParameters.ContainsKey('Server')) { $connection['Server'] = $Server }
$rootDse = Get-ADRootDSE @connection
$adServerName = [string]$rootDse.dnsHostName
$domainRoot = [string]$rootDse.defaultNamingContext
if ([string]::IsNullOrWhiteSpace($adServerName) -or [string]::IsNullOrWhiteSpace($domainRoot)) {
    throw 'AD did not return a domain naming context and domain controller hostname.'
}
$userParameters = @{
    Server      = $adServerName
    SearchBase  = $domainRoot
    SearchScope = 'Subtree'
    ErrorAction = 'Stop'
}
$accountNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
Get-ADUser -Filter * @userParameters | ForEach-Object {
    if (-not [string]::IsNullOrWhiteSpace($_.SamAccountName)) {
        [void]$accountNames.Add($_.SamAccountName)
    }
}
if ($accountNames.Count -eq 0) {
    throw 'AD returned no account names. No folders were deleted; check domain access and permissions.'
}

# CreateNew prevents overwrites. Keep the log open and flush each entry before
# deleting so an unavailable audit destination stops further deletion.
$logStream = [IO.File]::Open($logAbsolute, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = $null
$eligibleCount = 0
$deletedCount = 0
try {
    $writer = [IO.StreamWriter]::new($logStream, [Text.UTF8Encoding]::new($true))
    $writer.WriteLine('"TimestampUtc","FolderName","FolderPath","NewestWriteTimeUtc","CutoffUtc","ADServer","Status","Detail"')
    $writer.Flush()
    $logStream.Flush($true)

    foreach ($entry in $inventory) {
        $folder = $entry.Folder
        if (-not $IncludeAllFolders -and $folder.Name -notmatch '^.+\..+$') { continue }
        if ($accountNames.Contains($folder.Name)) { continue }
        $newest = ''
        try {
            Assert-DirectChild $folder.FullName $entry.Root
            $state = Get-TreeState $folder.FullName
            $newest = $state.NewestWriteTimeUtc.ToString('o')
            if ($state.ReparsePointPath) {
                Write-CleanupLog $folder 'SkippedReparsePoint' $state.ReparsePointPath $newest
                continue
            }
            if ($state.NewestWriteTimeUtc -ge $cutoffUtc) {
                Write-CleanupLog $folder 'SkippedRecent' 'Folder or contents are newer than the cutoff.' $newest
                continue
            }
            $eligibleCount++
            if (-not $Delete) {
                Write-CleanupLog $folder 'EligiblePreview' 'No deletion: -Delete was not supplied.' $newest
                continue
            }
            if (-not $PSCmdlet.ShouldProcess($folder.FullName, 'Permanently delete orphaned home folder and all contents')) {
                $status = 'Declined'
                if ($WhatIfPreference) { $status = 'WhatIf' }
                Write-CleanupLog $folder $status 'Deletion was not performed.' $newest
                continue
            }

            # Recheck after confirmation: the prompt may have been open for a
            # while, and the folder or account may have changed in the meantime.
            $current = Get-Item -LiteralPath $folder.FullName -Force -ErrorAction Stop
            Assert-DirectChild $current.FullName $entry.Root
            $freshState = Get-TreeState $current.FullName
            $newest = $freshState.NewestWriteTimeUtc.ToString('o')
            if ($freshState.ReparsePointPath) {
                Write-CleanupLog $folder 'SkippedReparsePoint' $freshState.ReparsePointPath $newest
                continue
            }
            if ($freshState.CreationTimeUtc -ne $state.CreationTimeUtc) {
                Write-CleanupLog $folder 'SkippedChanged' 'Folder was replaced during this run.' $newest
                continue
            }
            if ($freshState.NewestWriteTimeUtc -ge $cutoffUtc) {
                Write-CleanupLog $folder 'SkippedRecent' 'Folder or contents changed before deletion.' $newest
                continue
            }
            # Escape LDAP filter metacharacters rather than interpolating a
            # folder name into a PowerShell AD filter expression.
            $escapedName = $folder.Name.Replace('\', '\5c').Replace('*', '\2a').Replace('(', '\28').Replace(')', '\29').Replace([string][char]0, '\00')
            $existing = @(Get-ADUser -LDAPFilter ('(sAMAccountName={0})' -f $escapedName) @userParameters)
            if ($existing.Count -gt 0) {
                Write-CleanupLog $folder 'SkippedAccountExists' 'AD account exists at the final recheck.' $newest
                continue
            }

            # The resolved absolute target was checked above as an immediate
            # child of the explicitly supplied share root.
            Assert-DirectChild $current.FullName $entry.Root
            Write-CleanupLog $folder 'DeleteStarting' 'Recursive deletion is about to start.' $newest
            Remove-Item -LiteralPath $current.FullName -Recurse -Force -Confirm:$false -ErrorAction Stop
            if (Test-Path -LiteralPath $current.FullName -ErrorAction Stop) {
                throw 'The folder still exists after deletion.'
            }
            $deletedCount++
            Write-CleanupLog $folder 'Deleted' 'Folder and contents were removed.' $newest
        }
        catch {
            $failure = $_
            try { Write-CleanupLog $folder 'Error' $failure.Exception.Message $newest } catch { }
            throw $failure
        }
    }
}
finally {
    if ($null -ne $writer) { $writer.Dispose() }
    $logStream.Dispose()
}

Write-Host ("Eligible during scan: {0}; deleted: {1}. Audit log: {2}" -f $eligibleCount, $deletedCount, $logAbsolute)
if (-not $Delete) { Write-Host 'Preview only. Run with -Delete to enable deletion.' }
