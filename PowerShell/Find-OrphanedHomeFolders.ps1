#Requires -Version 5.1
<#
.SYNOPSIS
Exports home folders without a matching Active Directory user to a CSV file.

.DESCRIPTION
Compares each immediate child folder's name with AD sAMAccountName using an
exact, case-insensitive match. Both enabled and disabled accounts count as
existing accounts. By default, only names containing a period with text on
both sides are checked (for example, first.last).

Searches the selected domain, including all of its OUs. It does not search
other domains automatically, inspect folder contents, or change folders or AD.
The CSV opens in Excel; Excel and third-party PowerShell modules are not needed.

Run with an account that can read users throughout the selected domain and
list all folders in each share. Access-based enumeration can hide folders
from accounts without sufficient access.

If an AD query or folder enumeration fails, the script stops before exporting.
An existing output file is never overwritten. A successful scan with no
unmatched folders produces a CSV containing just the column headers.

.PARAMETER HomeSharePath
One or more home-share roots, not individual user folders.

.PARAMETER OutputPath
CSV destination. Its parent directory must already exist. Defaults to a
timestamped file in the current filesystem directory.

.PARAMETER Server
Optional domain DNS name or domain controller. Defaults to the current domain.

.PARAMETER IncludeAllFolders
Checks every immediate child folder, including names without a period.

.EXAMPLE
.\Find-OrphanedHomeFolders.ps1 -HomeSharePath '\\fs01\HomeShare' -OutputPath 'C:\Reports\OrphanedHomeFolders.csv'

.EXAMPLE
.\Find-OrphanedHomeFolders.ps1 -HomeSharePath '\\fs01\HomeShare', '\\fs02\HomeShare' -Server 'contoso.local'

.NOTES
Requires the Microsoft ActiveDirectory PowerShell module (RSAT-AD-PowerShell).
A reported folder has no matching sAMAccountName in the queried domain.
Renamed accounts and different folder/account naming conventions need review.
#>
[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$HomeSharePath,

    [ValidateNotNullOrEmpty()]
    [string]$OutputPath = (Join-Path -Path (Get-Location).Path -ChildPath (
        'OrphanedHomeFolders-{0}.csv' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    )),

    [ValidateNotNullOrEmpty()]
    [string]$Server,

    [switch]$IncludeAllFolders
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ([System.IO.Path]::GetExtension($OutputPath) -ine '.csv') {
    throw 'OutputPath must end in .csv. The report is an Excel-compatible CSV file.'
}

if (Test-Path -LiteralPath $OutputPath) {
    throw "The output file already exists: $OutputPath. Choose a new filename."
}

try {
    Import-Module ActiveDirectory -ErrorAction Stop
}
catch {
    throw "Cannot load the ActiveDirectory module. Install RSAT-AD-PowerShell on the reporting server. $($_.Exception.Message)"
}

$adParameters = @{ ErrorAction = 'Stop' }
if ($PSBoundParameters.ContainsKey('Server')) {
    $adParameters['Server'] = $Server
}

$accountNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)

Write-Verbose 'Reading Active Directory users, including disabled accounts.'
try {
    # An explicit domain root prevents the current AD: drive location from
    # accidentally limiting the search to one OU.
    $domainRoot = (Get-ADRootDSE @adParameters).defaultNamingContext
    if ([string]::IsNullOrWhiteSpace($domainRoot)) {
        throw 'Active Directory did not return a default domain naming context.'
    }

    Get-ADUser -Filter * -SearchBase $domainRoot -SearchScope Subtree @adParameters |
        ForEach-Object {
            if (-not [string]::IsNullOrWhiteSpace($_.SamAccountName)) {
                [void]$accountNames.Add($_.SamAccountName)
            }
        }
}
catch {
    throw "Active Directory lookup failed; no report was exported. $($_.Exception.Message)"
}

if ($accountNames.Count -eq 0) {
    throw 'Active Directory returned no account names; no report was exported. Check the domain and read permissions.'
}

$report = [System.Collections.Generic.List[object]]::new()
$checkedCount = 0
$skippedCount = 0

foreach ($sharePath in ($HomeSharePath | Select-Object -Unique)) {
    Write-Verbose "Listing folders directly under $sharePath"
    try {
        $share = Get-Item -LiteralPath $sharePath -Force -ErrorAction Stop
        if ($share.PSProvider.Name -ne 'FileSystem' -or -not $share.PSIsContainer) {
            throw 'The share root must be a filesystem directory.'
        }

        # No recursion: only the user folders directly under this share root.
        $folders = @(Get-ChildItem -LiteralPath $sharePath -Directory -Force -ErrorAction Stop)
    }
    catch {
        throw "Cannot completely list '$sharePath'; no report was exported. $($_.Exception.Message)"
    }

    foreach ($folder in $folders) {
        if (-not $IncludeAllFolders -and $folder.Name -notmatch '^.+\..+$') {
            $skippedCount++
            continue
        }

        $checkedCount++
        if (-not $accountNames.Contains($folder.Name)) {
            $report.Add([pscustomobject][ordered]@{
                FolderName    = $folder.Name
                FolderPath    = $folder.FullName
                LastWriteTime = $folder.LastWriteTime.ToString('o')
            })
        }
    }
}

if ($report.Count -gt 0) {
    $report | Sort-Object FolderPath |
        Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding UTF8 -NoClobber -ErrorAction Stop
}
else {
    # Keep the same report schema even when the scan finds no unmatched folders.
    '"FolderName","FolderPath","LastWriteTime"' |
        Out-File -LiteralPath $OutputPath -Encoding UTF8 -NoClobber -ErrorAction Stop
}

Write-Host ("AD accounts: {0}; folders checked: {1}; folders without matching accounts: {2}." -f
    $accountNames.Count, $checkedCount, $report.Count)
if ($skippedCount -gt 0) {
    Write-Host ("Skipped {0} folder(s) outside the first.last naming pattern. Use -IncludeAllFolders to check those too." -f $skippedCount)
}
Write-Host ("Report: {0}" -f (Get-Item -LiteralPath $OutputPath).FullName)
