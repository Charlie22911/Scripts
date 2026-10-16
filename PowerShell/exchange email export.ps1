<#
=======================================================================
KEYWORD EMAIL COLLECTION AND PST EXPORT
ON-PREMISES EXCHANGE MANAGEMENT SHELL
=======================================================================

Purpose:
  Search specified source mailboxes for keyword-responsive email,
  including Recoverable Items, copy matches to a Discovery Mailbox,
  and export the collection to one PST.

This script:
  - Estimates responsive items before copying.
  - Includes Recoverable Items by using -SearchDumpster.
  - Copies responsive items to separate folders in a Discovery Mailbox.
  - Exports only those collection folders to one PST.
  - Does not delete or modify source-mailbox content.
  - Uses confirmation prompts before Copy and Export actions.

Prerequisites:
  - Required Exchange RBAC permissions for Search-Mailbox and
    New-MailboxExportRequest.
  - Target Discovery Mailbox exists.
  - Export UNC share is approved and reachable.
  - Exchange Trusted Subsystem has required write access to the export
    share and NTFS destination folder.
#>

# =====================================================================
# CONFIGURATION
# =====================================================================

# Unique collection/case identifier.
$CollectionId = "KEYWORD_COLLECTION_YYYYMMDD_R1"

# Approved Discovery Mailbox.
$TargetDiscoveryMailbox = "DiscoveryMailbox@contoso.mil"

# Approved UNC export folder.
$PstExportDirectory = "\\FILESERVER\RestrictedShare\ApprovedCaseFolder"

# Output PST file.
$PstFilePath = Join-Path `
    $PstExportDirectory `
    "$CollectionId.pst"

# Unique Exchange PST export request name.
$ExportRequestName = "$CollectionId`_Export"

# Export status polling interval.
$ExportPollingIntervalSeconds = 20

# Search-Mailbox query-result safety threshold.
$MaximumItemsPerMailbox = 10000

# Source mailbox identities.
$SourceMailboxes = @(
    "custodian1@contoso.mil",
    "custodian2@contoso.mil",
    "custodian3@contoso.mil"
)

# Inclusive date range.
$StartDateText = "01/01/2026"
$EndDateText   = "12/31/2026"

# Exact-phrase keywords.
$KeywordPhrases = @(
    "KEYWORD PHRASE ONE",
    "KEYWORD PHRASE TWO",
    "KEYWORD PHRASE THREE",
    "KEYWORD PHRASE FOUR"
)

# =====================================================================
# DERIVED SETTINGS
# =====================================================================

$ErrorActionPreference = "Stop"

$FolderMap = foreach ($SourceMailbox in $SourceMailboxes) {
    [PSCustomObject]@{
        SourceMailbox = $SourceMailbox
        TargetFolder  = "$CollectionId - $SourceMailbox"
    }
}

$KeywordQuery = (
    $KeywordPhrases |
    ForEach-Object { '"' + $_ + '"' }
) -join " OR "

$SearchQuery = @"
(
    (sent:$StartDateText..$EndDateText) OR
    (received:$StartDateText..$EndDateText)
)
AND
(
    $KeywordQuery
)
"@ -replace "(`r`n|`n|`r)", " "

# =====================================================================
# FUNCTIONS
# =====================================================================

function Write-Section {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Title
    )

    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host $Title -ForegroundColor Cyan
    Write-Host "================================================================" -ForegroundColor Cyan
}

function Test-RequiredCmdlets {
    $RequiredCmdlets = @(
        "Get-Mailbox",
        "Get-MailboxFolderStatistics",
        "Search-Mailbox",
        "New-MailboxExportRequest",
        "Get-MailboxExportRequest",
        "Get-MailboxExportRequestStatistics"
    )

    $MissingCmdlets = @()

    foreach ($CmdletName in $RequiredCmdlets) {
        if (-not (Get-Command -Name $CmdletName -ErrorAction SilentlyContinue)) {
            $MissingCmdlets += $CmdletName
        }
    }

    if ($MissingCmdlets.Count -gt 0) {
        throw "Required Exchange cmdlets unavailable: $($MissingCmdlets -join ', ')"
    }

    $SearchMailboxCommand = Get-Command Search-Mailbox

    if (-not $SearchMailboxCommand.Parameters.ContainsKey("EstimateResultOnly")) {
        throw "Search-Mailbox does not expose EstimateResultOnly in this environment."
    }
}

function Test-DiscoveryMailbox {
    $Mailbox = Get-Mailbox `
        -Identity $TargetDiscoveryMailbox `
        -ErrorAction Stop

    if ($Mailbox.RecipientTypeDetails -ne "DiscoveryMailbox") {
        throw @"
Configured target mailbox is not a DiscoveryMailbox.

Configured target: $TargetDiscoveryMailbox
Resolved type:    $($Mailbox.RecipientTypeDetails)
"@
    }

    Write-Host "Discovery Mailbox validated: $($Mailbox.PrimarySmtpAddress)" -ForegroundColor Green
}

function Test-SourceMailboxes {
    foreach ($Entry in $FolderMap) {
        $Mailbox = Get-Mailbox `
            -Identity $Entry.SourceMailbox `
            -ErrorAction Stop

        if ($Mailbox.RecipientTypeDetails -notin @("UserMailbox", "SharedMailbox")) {
            throw @"
Configured source is not a UserMailbox or SharedMailbox.

Configured source: $($Entry.SourceMailbox)
Resolved type:     $($Mailbox.RecipientTypeDetails)
"@
        }

        Write-Host (
            "Source mailbox validated: {0}" -f `
            ([string]$Mailbox.PrimarySmtpAddress)
        ) -ForegroundColor Green
    }
}

function Get-CollectionFolders {
    $FolderStatistics = Get-MailboxFolderStatistics `
        -Identity $TargetDiscoveryMailbox `
        -ErrorAction Stop

    $FoundFolders = @()

    foreach ($Entry in $FolderMap) {
        $FolderName = $Entry.TargetFolder

        $Matches = $FolderStatistics |
            Where-Object {
                $_.Name -eq $FolderName -or
                $_.FolderPath -eq "/$FolderName" -or
                $_.FolderPath -eq "\$FolderName"
            }

        if ($Matches) {
            $FoundFolders += $Matches
        }
    }

    return $FoundFolders
}

function Show-ExportStatus {
    $RequestIdentity = "$TargetDiscoveryMailbox\$ExportRequestName"

    $Statistics = Get-MailboxExportRequestStatistics `
        -Identity $RequestIdentity `
        -IncludeReport `
        -ErrorAction Stop

    $Statistics |
        Select-Object `
            Status,
            PercentComplete,
            BytesTransferred,
            ItemsTransferred,
            FailureCode,
            FailureSide,
            Message |
        Format-List

    return $Statistics
}

# =====================================================================
# MAIN
# =====================================================================

try {
    Write-Section "KEYWORD EMAIL COLLECTION AND PST EXPORT"

    Write-Host "Collection ID:       $CollectionId"
    Write-Host "Discovery Mailbox:   $TargetDiscoveryMailbox"
    Write-Host "PST destination:     $PstFilePath"
    Write-Host "Date range:          $StartDateText through $EndDateText"

    Write-Host ""
    Write-Host "Search query:" -ForegroundColor Cyan
    Write-Host $SearchQuery

    Write-Host ""
    Write-Host "Source-to-target folder mapping:" -ForegroundColor Cyan
    $FolderMap | Format-Table -AutoSize

    Write-Section "PRE-FLIGHT VALIDATION"

    Test-RequiredCmdlets
    Test-DiscoveryMailbox
    Test-SourceMailboxes

    if (-not (Test-Path -Path $PstExportDirectory)) {
        throw @"
The configured PST export directory is not reachable:

$PstExportDirectory
"@
    }

    if (Test-Path -Path $PstFilePath) {
        throw @"
The configured PST output file already exists:

$PstFilePath

This script will not overwrite an existing PST.
"@
    }

    $ExistingCollectionFolders = Get-CollectionFolders

    if ($ExistingCollectionFolders.Count -gt 0) {
        Write-Host ""
        Write-Host "Collection folders already exist in the Discovery Mailbox:" -ForegroundColor Red

        $ExistingCollectionFolders |
            Select-Object Name, FolderPath, ItemsInFolder, FolderSize |
            Format-Table -AutoSize

        throw @"
Collection folders already exist. The script stopped to prevent duplicate
content copies. Use a new CollectionId for a separate authorized collection.
"@
    }

    Write-Section "ESTIMATE RESPONSIVE ITEMS"

    $EstimateResults = @()

    foreach ($Entry in $FolderMap) {
        $Mailbox = Get-Mailbox `
            -Identity $Entry.SourceMailbox `
            -ErrorAction Stop

        $MailboxSearchIdentity = [string]$Mailbox.PrimarySmtpAddress

        Write-Host "Estimating: $MailboxSearchIdentity" -ForegroundColor Yellow

        $Estimate = Search-Mailbox `
            -Identity $MailboxSearchIdentity `
            -SearchQuery $SearchQuery `
            -SearchDumpster `
            -EstimateResultOnly `
            -ErrorAction Stop

        $EstimateResults += [PSCustomObject]@{
            SourceMailbox  = $MailboxSearchIdentity
            TargetFolder   = $Entry.TargetFolder
            EstimatedItems = $Estimate.ResultItemsCount
            EstimatedSize  = $Estimate.ResultItemsSize
        }
    }

    Write-Host ""
    Write-Host "ESTIMATE RESULTS" -ForegroundColor Green
    $EstimateResults | Format-Table -AutoSize

    $AtOrOverLimit = @(
        $EstimateResults |
        Where-Object {
            $_.EstimatedItems -ge $MaximumItemsPerMailbox
        }
    )

    if ($AtOrOverLimit.Count -gt 0) {
        throw @"
One or more source mailboxes have 10,000 or more matching items.

No source content was copied and no PST export was started.
Refine the authorized collection scope before continuing.
"@
    }

    $TotalEstimatedItems = (
        $EstimateResults |
        Measure-Object -Property EstimatedItems -Sum
    ).Sum

    if ($null -eq $TotalEstimatedItems) {
        $TotalEstimatedItems = 0
    }

    if ($TotalEstimatedItems -eq 0) {
        Write-Host "No responsive items were found. No collection or export was performed." -ForegroundColor Yellow
        return
    }

    Write-Host ""
    Write-Host "Total estimated responsive items: $TotalEstimatedItems" -ForegroundColor Green

    Write-Section "COPY CONFIRMATION"

    Write-Host "Responsive content, including Recoverable Items, will be copied to the Discovery Mailbox." -ForegroundColor Yellow
    Write-Host "Type exactly COPY COLLECTION to continue." -ForegroundColor Yellow
    Write-Host "Press Enter to cancel without copying content." -ForegroundColor Yellow

    $CopyConfirmation = Read-Host "Confirmation"

    if ($CopyConfirmation -cne "COPY COLLECTION") {
        Write-Host "Cancelled before copying. No content was copied or exported." -ForegroundColor Yellow
        return
    }

    Write-Section "COPY RESPONSIVE CONTENT"

    $CopyResults = @()

    foreach ($Entry in $FolderMap) {
        $Mailbox = Get-Mailbox `
            -Identity $Entry.SourceMailbox `
            -ErrorAction Stop

        $MailboxSearchIdentity = [string]$Mailbox.PrimarySmtpAddress

        Write-Host "Copying from: $MailboxSearchIdentity" -ForegroundColor Yellow
        Write-Host "Target folder: $($Entry.TargetFolder)" -ForegroundColor Yellow

        $CopyResult = Search-Mailbox `
            -Identity $MailboxSearchIdentity `
            -SearchQuery $SearchQuery `
            -SearchDumpster `
            -TargetMailbox $TargetDiscoveryMailbox `
            -TargetFolder $Entry.TargetFolder `
            -LogLevel Basic `
            -ErrorAction Stop

        $CopyResults += [PSCustomObject]@{
            SourceMailbox = $MailboxSearchIdentity
            TargetFolder  = $Entry.TargetFolder
            CopiedItems   = $CopyResult.ResultItemsCount
            CopiedSize    = $CopyResult.ResultItemsSize
        }
    }

    Write-Host ""
    Write-Host "COPY RESULTS" -ForegroundColor Green
    $CopyResults | Format-Table -AutoSize

    $CreatedCollectionFolders = Get-CollectionFolders

    if ($CreatedCollectionFolders.Count -ne $FolderMap.Count) {
        throw @"
Not all expected collection folders were created.

Expected folders: $($FolderMap.Count)
Found folders:    $($CreatedCollectionFolders.Count)

No PST export request was created.
"@
    }

    Write-Section "PST EXPORT CONFIRMATION"

    $CreatedCollectionFolders |
        Select-Object Name, FolderPath, ItemsInFolder, FolderSize |
        Format-Table -AutoSize

    Write-Host ""
    Write-Host "The collection folders will be exported to:" -ForegroundColor Yellow
    Write-Host $PstFilePath -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Type exactly EXPORT COLLECTION to continue." -ForegroundColor Yellow
    Write-Host "Press Enter to stop after collection without exporting a PST." -ForegroundColor Yellow

    $ExportConfirmation = Read-Host "Confirmation"

    if ($ExportConfirmation -cne "EXPORT COLLECTION") {
        Write-Host "PST export cancelled. Copied content remains in the Discovery Mailbox." -ForegroundColor Yellow
        return
    }

    Write-Section "CREATE PST EXPORT REQUEST"

    New-MailboxExportRequest `
        -Name $ExportRequestName `
        -Mailbox $TargetDiscoveryMailbox `
        -FilePath $PstFilePath `
        -IncludeFolders $FolderMap.TargetFolder `
        -ErrorAction Stop | Out-Null

    Write-Host "PST export request created successfully." -ForegroundColor Green

    Write-Section "MONITOR PST EXPORT"

    do {
        Start-Sleep -Seconds $ExportPollingIntervalSeconds

        $ExportStatistics = Show-ExportStatus
        $ExportStatus = [string]$ExportStatistics.Status

        $StillRunning = (
            $ExportStatus -eq "Queued" -or
            $ExportStatus -eq "InProgress" -or
            $ExportStatus -eq "CompletionInProgress"
        )
    }
    while ($StillRunning)

    Write-Section "FINAL PST EXPORT STATUS"

    $FinalStatistics = Show-ExportStatus
    $FinalStatus = [string]$FinalStatistics.Status

    if ($FinalStatus -eq "Completed") {
        Write-Host "PST export completed successfully." -ForegroundColor Green
        Write-Host "PST file: $PstFilePath" -ForegroundColor Green
    }
    elseif ($FinalStatus -eq "CompletedWithWarning") {
        Write-Host "PST export completed with warning." -ForegroundColor Yellow
        Write-Host "Review the export report before using or releasing the PST." -ForegroundColor Yellow
    }
    else {
        Write-Host "PST export did not complete successfully." -ForegroundColor Red
        Write-Host "Review the failure details displayed above." -ForegroundColor Red
    }
}
catch {
    Write-Host ""
    Write-Host "SCRIPT STOPPED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
}
