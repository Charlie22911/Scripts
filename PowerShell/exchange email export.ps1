<#
KEYWORD EMAIL COLLECTION AND PST EXPORT
ON-PREMISES EXCHANGE MANAGEMENT SHELL

Searches designated mailboxes for keyword-responsive messages, copies
matching items to a Discovery Mailbox, and exports selected collection
folders, including their descendants, to a PST.

Requires Exchange mailbox search and mailbox import/export permissions.
The Exchange Trusted Subsystem requires write access to the PST UNC share.
Run in the Exchange Management Shell on an authorized administrative host.

Menu:
  1. Estimate, collect, and export
  2. Export an existing collection
  3. Inspect collection folders
  Q. Quit

Source mailboxes are never modified or deleted by this script.
#>

# =====================================================================
# CONFIGURATION
# =====================================================================

$CollectionId = "KEYWORD_COLLECTION_YYYYMMDD_R1"
$TargetDiscoveryMailbox = "DiscoveryMailbox@contoso.mil"
$PstExportDirectory = "\\FILESERVER\RestrictedShare\ApprovedCaseFolder"

$SourceMailboxes = @(
    "custodian1@contoso.mil",
    "custodian2@contoso.mil",
    "custodian3@contoso.mil"
)

$StartDateText = "01/01/2026"
$EndDateText   = "12/31/2026"

$KeywordPhrases = @(
    "KEYWORD PHRASE ONE",
    "KEYWORD PHRASE TWO",
    "KEYWORD PHRASE THREE",
    "KEYWORD PHRASE FOUR"
)

$MaximumItemsPerMailbox = 10000
$ExportPollingIntervalSeconds = 20
$FolderStatisticsRetryCount = 6
$FolderStatisticsRetrySeconds = 5

# =====================================================================
# SETTINGS
# =====================================================================

$ErrorActionPreference = "Stop"

$FolderMap = @(
    foreach ($SourceMailbox in $SourceMailboxes) {
        [PSCustomObject]@{
            SourceMailbox = $SourceMailbox
            TargetFolder  = "$CollectionId - $SourceMailbox"
        }
    }
)

$KeywordQuery = ($KeywordPhrases | ForEach-Object { '"' + $_ + '"' }) -join " OR "
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
    param([string]$Title)
    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host $Title -ForegroundColor Cyan
    Write-Host "================================================================" -ForegroundColor Cyan
}

function Confirm-Action {
    param(
        [string]$Message,
        [string]$Confirmation
    )

    Write-Host ""
    Write-Host $Message -ForegroundColor Yellow
    Write-Host "Type exactly $Confirmation to proceed, or press Enter to cancel." -ForegroundColor Yellow
    return ((Read-Host "Confirmation") -ceq $Confirmation)
}

function Test-RequiredCmdlets {
    param([bool]$NeedSearch)

    $Names = @(
        "Get-Mailbox",
        "Get-MailboxFolderStatistics",
        "New-MailboxExportRequest",
        "Get-MailboxExportRequestStatistics"
    )

    if ($NeedSearch) {
        $Names += "Search-Mailbox"
    }

    $Missing = @($Names | Where-Object { -not (Get-Command -Name $_ -ErrorAction SilentlyContinue) })
    if ($Missing.Count -gt 0) {
        throw "Required Exchange cmdlets unavailable: $($Missing -join ', ')"
    }
}

function Test-DiscoveryMailbox {
    $Mailbox = Get-Mailbox -Identity $TargetDiscoveryMailbox -ErrorAction Stop
    if ($Mailbox.RecipientTypeDetails -ne "DiscoveryMailbox") {
        throw "Target $TargetDiscoveryMailbox is not a DiscoveryMailbox (type: $($Mailbox.RecipientTypeDetails))."
    }
    Write-Host "Discovery Mailbox: $($Mailbox.PrimarySmtpAddress)" -ForegroundColor Green
}

function Resolve-SourceMailboxes {
    $Resolved = @()

    foreach ($Entry in $FolderMap) {
        $Mailbox = Get-Mailbox -Identity $Entry.SourceMailbox -ErrorAction Stop
        if ($Mailbox.RecipientTypeDetails -notin @("UserMailbox", "SharedMailbox")) {
            throw "Unsupported source mailbox type for $($Entry.SourceMailbox): $($Mailbox.RecipientTypeDetails)."
        }

        $Resolved += [PSCustomObject]@{
            SourceMailbox = [string]$Mailbox.PrimarySmtpAddress
            TargetFolder  = $Entry.TargetFolder
        }
    }

    return $Resolved
}

function Get-NormalizedFolderPath {
    param([string]$Path)
    return ('/' + $Path.Replace('\', '/').Trim('/'))
}

function Get-CollectionSummary {
    param([object[]]$ExpectedFolders)

    $Statistics = @(Get-MailboxFolderStatistics -Identity $TargetDiscoveryMailbox -ErrorAction Stop)
    $Summary = @()

    foreach ($Entry in $ExpectedFolders) {
        $Roots = @(
            $Statistics | Where-Object {
                $_.Name -eq $Entry.TargetFolder
            }
        )

        if ($Roots.Count -gt 1) {
            $TopRoots = @(
                $Roots | Where-Object {
                    (Get-NormalizedFolderPath ([string]$_.FolderPath)) -eq ("/" + $Entry.TargetFolder)
                }
            )
            if ($TopRoots.Count -eq 1) {
                $Roots = $TopRoots
            }
        }

        if ($Roots.Count -gt 1) {
            throw "Multiple folders named '$($Entry.TargetFolder)' exist in the Discovery Mailbox. Inspect folder paths before exporting."
        }

        if ($Roots.Count -eq 0) {
            $Summary += [PSCustomObject]@{
                SourceMailbox  = $Entry.SourceMailbox
                TargetFolder   = $Entry.TargetFolder
                FolderPath     = "(not found)"
                RootItems      = 0
                ChildItems     = 0
                TotalItems     = 0
                Exists         = $false
            }
            continue
        }

        $Root = $Roots[0]
        $RootPath = Get-NormalizedFolderPath ([string]$Root.FolderPath)
        $Prefix = $RootPath + '/'
        $Descendants = @(
            $Statistics | Where-Object {
                (Get-NormalizedFolderPath ([string]$_.FolderPath)).StartsWith(
                    $Prefix,
                    [System.StringComparison]::OrdinalIgnoreCase
                )
            }
        )

        $ChildItemCount = 0L
        foreach ($Child in $Descendants) {
            $ChildItemCount += [long]$Child.ItemsInFolder
        }

        $RootCount = [long]$Root.ItemsInFolder

        $Summary += [PSCustomObject]@{
            SourceMailbox  = $Entry.SourceMailbox
            TargetFolder   = $Entry.TargetFolder
            FolderPath     = $RootPath
            RootItems      = $RootCount
            ChildItems     = $ChildItemCount
            TotalItems     = ($RootCount + $ChildItemCount)
            Exists         = $true
        }
    }

    return $Summary
}

function Show-CollectionSummary {
    param([object[]]$ExpectedFolders)

    $Summary = @(Get-CollectionSummary -ExpectedFolders $ExpectedFolders)
    Write-Host ""
    $Summary |
        Select-Object SourceMailbox, TargetFolder, Exists, RootItems, ChildItems, TotalItems |
        Format-Table -AutoSize | Out-Host

    Write-Host "RootItems may include Exchange search-report messages." -ForegroundColor Yellow
    Write-Host "ChildItems counts items within the collection folder's descendants." -ForegroundColor Yellow
    return $Summary
}

function Assert-CollectionHasMessages {
    param([object[]]$ExpectedFolders)

    $Final = @()
    for ($Attempt = 1; $Attempt -le $FolderStatisticsRetryCount; $Attempt++) {
        $Final = @(Get-CollectionSummary -ExpectedFolders $ExpectedFolders)
        $MissingFolders = @($Final | Where-Object { -not $_.Exists })
        $EmptyChildren = @($Final | Where-Object { $_.Exists -and $_.ChildItems -le 0 })

        if ($MissingFolders.Count -eq 0 -and $EmptyChildren.Count -eq 0) {
            break
        }

        if ($Attempt -lt $FolderStatisticsRetryCount) {
            Start-Sleep -Seconds $FolderStatisticsRetrySeconds
        }
    }

    $Final |
        Select-Object SourceMailbox, TargetFolder, Exists, RootItems, ChildItems, TotalItems |
        Format-Table -AutoSize | Out-Host

    $MissingFolders = @($Final | Where-Object { -not $_.Exists })
    $EmptyChildren = @($Final | Where-Object { $_.Exists -and $_.ChildItems -le 0 })

    if ($MissingFolders.Count -gt 0) {
        throw "One or more expected collection folders are missing. No PST export was started."
    }
    if ($EmptyChildren.Count -gt 0) {
        throw "One or more collection folders contain no items in subfolders. Only search-report messages may be present. Inspect the Discovery Mailbox before exporting."
    }

    return $Final
}

function Start-CollectionExport {
    param(
        [object[]]$ExpectedFolders,
        [bool]$ExistingCollection
    )

    Write-Section "VALIDATE COLLECTION CONTENT"
    $Validated = @(Assert-CollectionHasMessages -ExpectedFolders $ExpectedFolders)

    if (-not (Test-Path -LiteralPath $PstExportDirectory -PathType Container)) {
        throw "Export directory is unavailable: $PstExportDirectory"
    }

    if ($ExistingCollection) {
        $Stamp = Get-Date -Format "yyyyMMdd_HHmmss"
        $PstName = "${CollectionId}_Export_${Stamp}.pst"
        $RequestName = "${CollectionId}_Export_${Stamp}"
    }
    else {
        $PstName = "$CollectionId.pst"
        $RequestName = "${CollectionId}_Export"
    }

    $PstFilePath = Join-Path $PstExportDirectory $PstName
    if (Test-Path -LiteralPath $PstFilePath) {
        throw "PST already exists; no files will be overwritten: $PstFilePath"
    }

    $ExportFolders = @(
        $ExpectedFolders | ForEach-Object {
            "$($_.TargetFolder)/*"
        }
    )

    Write-Section "PST EXPORT SETTINGS"
    Write-Host "Target mailbox: $TargetDiscoveryMailbox"
    Write-Host "Destination:    $PstFilePath"
    Write-Host "Folders and descendants:"
    $ExportFolders | ForEach-Object { Write-Host "  $_" }

    if (-not (Confirm-Action -Message "Export the identified collection content to the PST?" -Confirmation "EXPORT COLLECTION")) {
        Write-Host "Export cancelled." -ForegroundColor Yellow
        return
    }

    Write-Section "CREATE PST EXPORT REQUEST"

    # Exclude unrelated Recoverable Items from the Discovery Mailbox.
    # Items already copied into collection folders remain in scope.
    New-MailboxExportRequest `
        -Name $RequestName `
        -Mailbox $TargetDiscoveryMailbox `
        -FilePath $PstFilePath `
        -IncludeFolders $ExportFolders `
        -ExcludeDumpster `
        -ErrorAction Stop | Out-Null

    $RequestIdentity = "$TargetDiscoveryMailbox\$RequestName"
    $RunningStatuses = @("Queued", "InProgress", "CompletionInProgress")

    Write-Section "MONITOR PST EXPORT"

    do {
        Start-Sleep -Seconds $ExportPollingIntervalSeconds

        $Report = Get-MailboxExportRequestStatistics `
            -Identity $RequestIdentity `
            -IncludeReport `
            -ErrorAction Stop

        # Out-Host prevents formatting objects from corrupting the return value.
        $Report |
            Select-Object Status, PercentComplete, BytesTransferred, ItemsTransferred,
                FailureCode, FailureSide, Message |
            Format-List | Out-Host

        $Status = [string]$Report.Status
    }
    while ($Status -in $RunningStatuses)

    if ($Status -ne "Completed") {
        throw "PST export finished with status '$Status'. Review the report and do not use the PST as a complete collection."
    }

    $ExpectedChildItems = [long](($Validated | Measure-Object -Property ChildItems -Sum).Sum)
    $TransferredItems = [long]$Report.ItemsTransferred
    if ($TransferredItems -lt $ExpectedChildItems) {
        throw "Export reported $TransferredItems transferred items, fewer than the $ExpectedChildItems items in collection subfolders. Verify the export report and PST contents."
    }

    Write-Host "PST export completed: $PstFilePath" -ForegroundColor Green
    Write-Host "Transferred items: $TransferredItems" -ForegroundColor Green
    Write-Host "Verify PST contents in Outlook before using or releasing the collection." -ForegroundColor Yellow
}

function Start-NewCollection {
    Write-Section "SOURCE VALIDATION"
    $Resolved = @(Resolve-SourceMailboxes)
    $Resolved | Format-Table -AutoSize | Out-Host

    $Existing = @(Get-CollectionSummary -ExpectedFolders $Resolved)
    if (@($Existing | Where-Object { $_.Exists }).Count -gt 0) {
        throw "Collection folders already exist. Use menu option 2 to export them, or use a new CollectionId for a new collection."
    }

    Write-Section "SEARCH QUERY"
    Write-Host $SearchQuery

    $Estimates = @()
    foreach ($Entry in $Resolved) {
        Write-Host "Estimating $($Entry.SourceMailbox)..." -ForegroundColor Yellow
        $Result = Search-Mailbox `
            -Identity $Entry.SourceMailbox `
            -SearchQuery $SearchQuery `
            -SearchDumpster `
            -EstimateResultOnly `
            -ErrorAction Stop

        $Estimates += [PSCustomObject]@{
            SourceMailbox  = $Entry.SourceMailbox
            TargetFolder   = $Entry.TargetFolder
            EstimatedItems = [long]$Result.ResultItemsCount
            EstimatedSize  = $Result.ResultItemsSize
        }
    }

    Write-Section "ESTIMATED SEARCH RESULTS"
    $Estimates | Format-Table -AutoSize | Out-Host

    $OverLimit = @($Estimates | Where-Object { $_.EstimatedItems -ge $MaximumItemsPerMailbox })
    if ($OverLimit.Count -gt 0) {
        throw "At least one mailbox has 10,000 or more matches. Refine the collection scope before copying."
    }

    $Active = @($Estimates | Where-Object { $_.EstimatedItems -gt 0 })
    if ($Active.Count -eq 0) {
        Write-Host "No responsive items were found." -ForegroundColor Yellow
        return
    }

    if (-not (Confirm-Action -Message "Copy matching items into the Discovery Mailbox?" -Confirmation "COPY COLLECTION")) {
        Write-Host "Collection cancelled." -ForegroundColor Yellow
        return
    }

    Write-Section "COPY SEARCH RESULTS"
    $CopyResults = @()
    foreach ($Entry in $Active) {
        Write-Host "Copying $($Entry.SourceMailbox)..." -ForegroundColor Yellow
        $Copy = Search-Mailbox `
            -Identity $Entry.SourceMailbox `
            -SearchQuery $SearchQuery `
            -SearchDumpster `
            -TargetMailbox $TargetDiscoveryMailbox `
            -TargetFolder $Entry.TargetFolder `
            -LogLevel Basic `
            -ErrorAction Stop

        $CopyResults += [PSCustomObject]@{
            SourceMailbox = $Entry.SourceMailbox
            TargetFolder  = $Entry.TargetFolder
            CopiedItems   = [long]$Copy.ResultItemsCount
            CopiedSize    = $Copy.ResultItemsSize
        }
    }

    $CopyResults | Format-Table -AutoSize | Out-Host
    if (@($CopyResults | Where-Object { $_.CopiedItems -le 0 }).Count -gt 0) {
        throw "At least one mailbox reported zero copied items. PST export was not started."
    }

    Start-CollectionExport -ExpectedFolders $Active -ExistingCollection $false
}

# =====================================================================
# MAIN
# =====================================================================

try {
    Write-Section "KEYWORD EMAIL COLLECTION AND PST EXPORT"
    Write-Host "Collection ID:     $CollectionId"
    Write-Host "Discovery Mailbox: $TargetDiscoveryMailbox"
    Write-Host "PST directory:     $PstExportDirectory"
    Write-Host "Date range:        $StartDateText through $EndDateText"
    Write-Host ""
    Write-Host "[1] Estimate, collect, and export"
    Write-Host "[2] Export existing collection folders"
    Write-Host "[3] Inspect collection folders"
    Write-Host "[Q] Quit"
    $Choice = (Read-Host "Selection").Trim().ToUpperInvariant()

    if ($Choice -eq "Q") { return }
    if ($Choice -notin @("1", "2", "3")) {
        throw "Invalid menu selection."
    }

    Test-RequiredCmdlets -NeedSearch ($Choice -eq "1")
    Test-DiscoveryMailbox

    if ($Choice -eq "1") {
        Start-NewCollection
    }
    elseif ($Choice -eq "2") {
        Write-Section "EXISTING COLLECTION"
        $Current = @(Show-CollectionSummary -ExpectedFolders $FolderMap)
        $Present = @($Current | Where-Object { $_.Exists })
        $Missing = @($Current | Where-Object { -not $_.Exists })

        if ($Present.Count -eq 0) {
            throw "No configured collection folders were found in the Discovery Mailbox."
        }
        if ($Missing.Count -gt 0) {
            Write-Host "Missing collection folders will not be exported:" -ForegroundColor Yellow
            $Missing | ForEach-Object { Write-Host "  $($_.TargetFolder)" -ForegroundColor Yellow }
        }

        Start-CollectionExport -ExpectedFolders $Present -ExistingCollection $true
    }
    else {
        Write-Section "COLLECTION FOLDER CONTENTS"
        $Summary = @(Show-CollectionSummary -ExpectedFolders $FolderMap)
        if (@($Summary | Where-Object { $_.Exists -and $_.ChildItems -gt 0 }).Count -eq 0) {
            Write-Host "No copied messages were detected in collection subfolders." -ForegroundColor Yellow
        }
    }
}
catch {
    Write-Host ""
    Write-Host "SCRIPT STOPPED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
}
