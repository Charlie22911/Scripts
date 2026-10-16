<#
.EXAMPLE
    .\exchange_mailbox_keyword_export_menu_v2.ps1

.DESCRIPTION
    Interactive keyword search and PST export for on-premises Exchange.
    Run in the Exchange Management Shell using Windows PowerShell.

    1. Search mailboxes, estimate results, copy to a Discovery Mailbox,
       and export the collection to PST.
    2. Export an existing collection without repeating the search.
    3. Inspect an existing collection.

    Search terms separated by commas are treated as exact phrases.
    ANY matches at least one phrase; ALL requires every phrase.
    Source mailbox contents are not changed or deleted.

.NOTES
    Requires Discovery Management and Mailbox Import Export permissions.
    Exchange Trusted Subsystem needs read/write permission on the PST
    destination's UNC share and underlying folder.
    Search-Mailbox returns at most 10,000 matches per source mailbox.
#>

$ErrorActionPreference = 'Stop'
$MaxResultsPerMailbox = 10000
$PollSeconds = 15
$script:DiscoveryMailbox = ''
$script:ExportDirectory = ''

function Section {
    param([string]$Heading)
    Write-Host ''
    Write-Host ('=' * 66) -ForegroundColor Cyan
    Write-Host $Heading -ForegroundColor Cyan
    Write-Host ('=' * 66) -ForegroundColor Cyan
}

function Prompt-Text {
    param([string]$Label, [string]$Default = '')
    do {
        if ($Default) {
            $Answer = Read-Host "$Label [$Default]"
            if ([string]::IsNullOrWhiteSpace($Answer)) { $Answer = $Default }
        } else {
            $Answer = Read-Host $Label
        }
        $Answer = $Answer.Trim()
        if ($Answer) { return $Answer }
        Write-Host 'A value is required.' -ForegroundColor Yellow
    } while ($true)
}

function Prompt-Confirm {
    param([string]$Question, [string]$RequiredText)
    Write-Host ''
    Write-Host $Question -ForegroundColor Yellow
    Write-Host "Type $RequiredText to continue. Press Enter to cancel." -ForegroundColor Yellow
    return ((Read-Host 'Confirmation') -ceq $RequiredText)
}

function Parse-CommaList {
    param([string]$Text)
    $Seen = @{}
    $Result = @()
    foreach ($Part in ($Text -split ',')) {
        $Item = $Part.Trim().Trim('"').Trim()
        if ($Item -and -not $Seen.ContainsKey($Item.ToLowerInvariant())) {
            $Result += $Item
            $Seen[$Item.ToLowerInvariant()] = $true
        }
    }
    return $Result
}

function Read-CommaList {
    param([string]$Label)
    do {
        Write-Host ''
        $Raw = Prompt-Text $Label
        $Items = @(Parse-CommaList -Text $Raw)
        if ($Items.Count -gt 0) { return $Items }
        Write-Host 'Enter one or more values separated by commas.' -ForegroundColor Yellow
    } while ($true)
}

function Read-CollectionId {
    param([bool]$NewCollection)
    $Default = ''
    if ($NewCollection) { $Default = 'EMAIL_' + (Get-Date -Format 'yyyyMMdd_HHmmss') }
    do {
        $Id = Prompt-Text -Label 'Collection ID' -Default $Default
        if ($Id -cmatch '^[a-zA-Z0-9_-]{1,55}$') { return $Id }
        Write-Host 'Use 1 to 55 letters, numbers, underscores, or hyphens.' -ForegroundColor Yellow
    } while ($true)
}

function Read-DateRange {
    Write-Host ''
    Write-Host 'Optional date filter. Leave both dates empty to search all dates.'
    Write-Host 'Dates use MM/DD/YYYY. Both sent and received dates are considered.'
    do {
        $Start = (Read-Host 'Start date (or Enter)').Trim()
        $End   = (Read-Host 'End date (or Enter)').Trim()
        if (-not $Start -and -not $End) { return $null }
        $Culture = [System.Globalization.CultureInfo]::InvariantCulture
        $Style = [System.Globalization.DateTimeStyles]::None
        $StartDate = [datetime]::MinValue
        $EndDate = [datetime]::MinValue
        $OkStart = [datetime]::TryParseExact($Start, 'MM/dd/yyyy', $Culture, $Style, [ref]$StartDate)
        $OkEnd   = [datetime]::TryParseExact($End,   'MM/dd/yyyy', $Culture, $Style, [ref]$EndDate)
        if ($OkStart -and $OkEnd -and $StartDate -le $EndDate) {
            return [PSCustomObject]@{ Start = $Start; End = $End }
        }
        Write-Host 'Enter two valid MM/DD/YYYY dates, with the start on or before the end.' -ForegroundColor Yellow
    } while ($true)
}

function Build-Query {
    param([string[]]$Terms, [string]$Mode, $Dates)
    $Parts = @()
    foreach ($Term in $Terms) {
        if ($Term.Contains('"')) { throw 'Search terms cannot contain double-quote characters.' }
        $Parts += '"' + $Term + '"'
    }
    $Joiner = if ($Mode -eq 'ALL') { ' AND ' } else { ' OR ' }
    $Query = '(' + ($Parts -join $Joiner) + ')'
    if ($null -ne $Dates) {
        $Query += ' AND ((sent:' + $Dates.Start + '..' + $Dates.End +
                  ') OR (received:' + $Dates.Start + '..' + $Dates.End + '))'
    }
    return $Query
}

function Require-Exchange {
    param([bool]$SearchRequired)
    $Commands = @('Get-Mailbox', 'Get-MailboxFolderStatistics')
    if ($SearchRequired) { $Commands += 'Search-Mailbox' }
    $Commands += 'New-MailboxExportRequest'
    $Commands += 'Get-MailboxExportRequestStatistics'
    $Missing = @($Commands | Where-Object { -not (Get-Command $_ -ErrorAction SilentlyContinue) })
    if ($Missing.Count) {
        throw 'Exchange commands missing: ' + ($Missing -join ', ') + '. Run this script in the Exchange Management Shell with the required RBAC roles.'
    }
}

function Choose-DiscoveryMailbox {
    Section 'DISCOVERY MAILBOX'
    while ($true) {
        $script:DiscoveryMailbox = Prompt-Text -Label 'Discovery Mailbox name or address' -Default $script:DiscoveryMailbox
        try {
            $Mailbox = Get-Mailbox -Identity $script:DiscoveryMailbox -ErrorAction Stop
            if ([string]$Mailbox.RecipientTypeDetails -ne 'DiscoveryMailbox') {
                Write-Host 'That mailbox is not a Discovery Mailbox.' -ForegroundColor Yellow
                $script:DiscoveryMailbox = ''
                continue
            }
            $script:DiscoveryMailbox = [string]$Mailbox.PrimarySmtpAddress
            Write-Host "Using: $script:DiscoveryMailbox" -ForegroundColor Green
            return
        }
        catch {
            Write-Host "Mailbox could not be verified: $($_.Exception.Message)" -ForegroundColor Yellow
            $script:DiscoveryMailbox = ''
        }
    }
}

function Choose-ExportDirectory {
    Section 'PST DESTINATION'
    Write-Host 'Use a UNC folder such as \\SERVER\PSTExports\CaseFolder.'
    Write-Host 'Exchange Trusted Subsystem must have write access to the share.'
    while ($true) {
        $Candidate = Prompt-Text -Label 'Export folder (UNC)' -Default $script:ExportDirectory
        if ($Candidate -notmatch '^\\\\[^\\]+\\[^\\]+') {
            Write-Host 'Enter a UNC path beginning with \\SERVER\SHARE.' -ForegroundColor Yellow
            $script:ExportDirectory = ''
            continue
        }
        if (-not (Test-Path -LiteralPath $Candidate -PathType Container)) {
            Write-Host 'That folder is not accessible from this session.' -ForegroundColor Yellow
            $script:ExportDirectory = ''
            continue
        }
        $script:ExportDirectory = $Candidate.TrimEnd('\')
        return
    }
}

function Make-FolderMap {
    param([string]$CollectionId, [string[]]$Mailboxes, [bool]$Resolve)
    $Mapped = @()
    $Seen = @{}
    foreach ($Identity in $Mailboxes) {
        $Address = $Identity
        if ($Resolve) {
            $Mailbox = Get-Mailbox -Identity $Identity -ErrorAction Stop
            if ([string]$Mailbox.RecipientTypeDetails -notin @('UserMailbox', 'SharedMailbox')) {
                throw "Source is not a user/shared mailbox: $Identity"
            }
            $Address = [string]$Mailbox.PrimarySmtpAddress
        }
        if ($Seen.ContainsKey($Address.ToLowerInvariant())) { continue }
        $Seen[$Address.ToLowerInvariant()] = $true
        $Mapped += [PSCustomObject]@{
            Mailbox = $Address
            Folder = "$CollectionId - $Address"
        }
    }
    return $Mapped
}

function Get-CollectionFolders {
    param([object[]]$Map)
    $AllFolders = @(Get-MailboxFolderStatistics -Identity $script:DiscoveryMailbox -ErrorAction Stop)
    $Summary = @()
    foreach ($Entry in $Map) {
        $Roots = @($AllFolders | Where-Object { $_.Name -eq $Entry.Folder })
        if ($Roots.Count -gt 1) {
            throw "More than one '$($Entry.Folder)' folder exists. Resolve the duplicate before exporting."
        }
        $Found = ($Roots.Count -eq 1)
        $RootCount = 0L
        $ChildCount = 0L
        $Path = ''
        if ($Found) {
            $Root = $Roots[0]
            $Path = ('/' + ([string]$Root.FolderPath).Replace('\', '/').Trim('/'))
            $RootCount = [long]$Root.ItemsInFolder
            $Prefix = $Path + '/'
            foreach ($Folder in $AllFolders) {
                $ChildPath = '/' + ([string]$Folder.FolderPath).Replace('\', '/').Trim('/')
                if ($ChildPath.StartsWith($Prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $ChildCount += [long]$Folder.ItemsInFolder
                }
            }
        }
        $Summary += [PSCustomObject]@{
            Mailbox = $Entry.Mailbox
            Folder = $Entry.Folder
            Found = $Found
            RootItems = $RootCount
            SubfolderItems = $ChildCount
            TotalItems = $RootCount + $ChildCount
        }
    }
    return $Summary
}

function Export-Collection {
    param([string]$CollectionId, [object[]]$Map, [bool]$AllowRootOnly)
    Section 'VERIFY COLLECTION BEFORE EXPORT'
    $Summary = @()
    for ($Attempt = 1; $Attempt -le 4; $Attempt++) {
        $Summary = @(Get-CollectionFolders -Map $Map)
        $Missing = @($Summary | Where-Object { -not $_.Found })
        $Empty = @($Summary | Where-Object { $_.TotalItems -le 0 })
        if ($Missing.Count -eq 0 -and $Empty.Count -eq 0) { break }
        if ($Attempt -lt 4) {
            Write-Host 'Checking mailbox folder statistics again...' -ForegroundColor Yellow
            Start-Sleep -Seconds 5
        }
    }
    $Summary | Format-Table -Property Mailbox, Found, RootItems, SubfolderItems, TotalItems -AutoSize | Out-Host
    if (@($Summary | Where-Object { -not $_.Found }).Count) {
        throw 'Expected collection folders are missing. Export cancelled.'
    }
    if (@($Summary | Where-Object { $_.TotalItems -le 0 }).Count) {
        throw 'One or more collection folders are empty. Export cancelled.'
    }
    if (-not $AllowRootOnly -and @($Summary | Where-Object { $_.SubfolderItems -eq 0 }).Count) {
        throw 'An existing collection has no items in subfolders. It may contain only search-report messages. Inspect it before exporting.'
    }

    Choose-ExportDirectory
    $Stamp = Get-Date -Format 'yyyyMMdd_HHmmssfff'
    $PstPath = Join-Path $script:ExportDirectory ("${CollectionId}_${Stamp}.pst")
    $RequestName = "KeyExport_$Stamp"
    if (Test-Path -LiteralPath $PstPath) { throw "PST already exists: $PstPath" }

    # The /* suffix includes the collection's folder hierarchy.
    $FoldersToExport = @($Map | ForEach-Object { $_.Folder + '/*' })
    Write-Host ''
    Write-Host 'PST file:' -ForegroundColor Cyan
    Write-Host $PstPath
    Write-Host 'Collection folders:' -ForegroundColor Cyan
    $FoldersToExport | ForEach-Object { Write-Host "  $_" }
    if (-not (Prompt-Confirm -Question 'Export these folders to the new PST?' -RequiredText 'EXPORT')) {
        Write-Host 'Export cancelled.' -ForegroundColor Yellow
        return
    }

    Section 'EXPORT PROGRESS'
    New-MailboxExportRequest -Name $RequestName -Mailbox $script:DiscoveryMailbox `
        -FilePath $PstPath -IncludeFolders $FoldersToExport -ExcludeDumpster -ErrorAction Stop | Out-Null

    $RequestId = "$($script:DiscoveryMailbox)\$RequestName"
    do {
        Start-Sleep -Seconds $PollSeconds
        $Stats = Get-MailboxExportRequestStatistics -Identity $RequestId -ErrorAction Stop
        Write-Host ('Status: {0,-22} {1,3}%  Items: {2}' -f `
            $Stats.Status, $Stats.PercentComplete, $Stats.ItemsTransferred)
        $Status = [string]$Stats.Status
    } while ($Status -in @('Queued', 'InProgress', 'CompletionInProgress'))

    $Final = Get-MailboxExportRequestStatistics -Identity $RequestId -IncludeReport -ErrorAction Stop
    Write-Host ''
    $Final | Select-Object Status, PercentComplete, ItemsTransferred, BytesTransferred, Message, FailureCode |
        Format-List | Out-Host

    if ([string]$Final.Status -ne 'Completed') {
        throw 'Export did not complete successfully. Review the Exchange export report.'
    }
    $MinItems = [long](($Summary | Measure-Object -Property SubfolderItems -Sum).Sum)
    if ([long]$Final.ItemsTransferred -lt $MinItems) {
        Write-Host 'WARNING: Fewer items were transferred than counted in collection subfolders.' -ForegroundColor Red
        Write-Host 'Check folder selection and the PST contents before using the export.' -ForegroundColor Red
    } else {
        Write-Host 'PST export completed. Verify messages inside Outlook.' -ForegroundColor Green
    }
    Write-Host "PST: $PstPath" -ForegroundColor Cyan
}

function Start-NewSearch {
    Require-Exchange -SearchRequired $true
    Section 'NEW KEYWORD SEARCH'
    $CollectionId = Read-CollectionId -NewCollection $true
    $Mailboxes = @(Read-CommaList 'Mailbox addresses (comma separated)')
    $Terms = @(Read-CommaList 'Search terms or phrases (comma separated)')
    if (@($Terms | Where-Object { $_.Contains('"') }).Count) {
        throw 'Do not place quotation marks inside search terms. Separate terms with commas.'
    }

    Write-Host ''
    Write-Host 'Search term matching:'
    Write-Host '  [1] ANY term matches (OR, default)'
    Write-Host '  [2] ALL terms must match (AND)'
    do {
        $Choice = (Read-Host 'Choose 1 or 2 [1]').Trim()
        if ($Choice -in @('', '1', '2')) { break }
        Write-Host 'Choose 1 or 2.' -ForegroundColor Yellow
    } while ($true)
    $Mode = if ($Choice -eq '2') { 'ALL' } else { 'ANY' }
    $Dates = Read-DateRange
    $Query = Build-Query -Terms $Terms -Mode $Mode -Dates $Dates

    Choose-DiscoveryMailbox
    $Map = @(Make-FolderMap -CollectionId $CollectionId -Mailboxes $Mailboxes -Resolve $true)
    $Existing = @(Get-CollectionFolders -Map $Map)
    if (@($Existing | Where-Object { $_.Found }).Count) {
        throw 'Some collection folders already exist. Use another Collection ID or export the existing collection.'
    }

    Section 'REVIEW SEARCH'
    Write-Host "Collection ID: $CollectionId"
    Write-Host "Discovery Mailbox: $script:DiscoveryMailbox"
    Write-Host "Term matching: $Mode"
    Write-Host 'Source mailboxes:'
    $Map | ForEach-Object { Write-Host "  $($_.Mailbox)" }
    Write-Host 'Search terms:'
    $Terms | ForEach-Object { Write-Host "  $_" }
    Write-Host "Query: $Query"
    if (-not (Prompt-Confirm -Question 'Estimate matching items in these mailboxes?' -RequiredText 'ESTIMATE')) { return }

    Section 'ESTIMATING MATCHES'
    $Estimates = @()
    foreach ($Entry in $Map) {
        Write-Host "Searching $($Entry.Mailbox)..." -ForegroundColor Yellow
        $Estimate = Search-Mailbox -Identity $Entry.Mailbox -SearchQuery $Query `
            -SearchDumpster -EstimateResultOnly -ErrorAction Stop
        $Estimates += [PSCustomObject]@{
            Mailbox = $Entry.Mailbox
            Folder = $Entry.Folder
            Matches = [long]$Estimate.ResultItemsCount
            Size = $Estimate.ResultItemsSize
        }
    }
    $Estimates | Select-Object Mailbox, Matches, Size | Format-Table -AutoSize | Out-Host
    if (@($Estimates | Where-Object { $_.Matches -ge $MaxResultsPerMailbox }).Count) {
        throw 'A mailbox has 10,000 or more matching items. Narrow the search before collecting.'
    }
    $Active = @($Estimates | Where-Object { $_.Matches -gt 0 })
    if (-not $Active.Count) {
        Write-Host 'No matching items found. Nothing copied or exported.' -ForegroundColor Yellow
        return
    }
    Write-Host ('Total estimated matches: {0}' -f (($Active | Measure-Object Matches -Sum).Sum)) -ForegroundColor Green
    if (-not (Prompt-Confirm -Question 'Copy matching items into the Discovery Mailbox?' -RequiredText 'COPY')) { return }

    Section 'COPY MATCHING MESSAGES'
    $Completed = @()
    foreach ($Entry in $Active) {
        Write-Host "Copying $($Entry.Mailbox)..." -ForegroundColor Yellow
        # Suppress prevents search-report messages being added to the collection.
        $Copy = Search-Mailbox -Identity $Entry.Mailbox -SearchQuery $Query `
            -SearchDumpster -TargetMailbox $script:DiscoveryMailbox `
            -TargetFolder $Entry.Folder -LogLevel Suppress -ErrorAction Stop
        $Count = [long]$Copy.ResultItemsCount
        Write-Host "  Exchange reported $Count copied items."
        if ($Count -le 0) {
            throw "No items copied for $($Entry.Mailbox). The collected results remain in the Discovery Mailbox."
        }
        $Completed += [PSCustomObject]@{ Mailbox = $Entry.Mailbox; Folder = $Entry.Folder }
    }

    Write-Host ''
    Write-Host 'Collection copy finished.' -ForegroundColor Green
    Write-Host "Save this collection ID to export again later: $CollectionId"
    if ((Read-Host 'Export to PST now? (Y/n)').Trim() -notmatch '^[Nn]') {
        Export-Collection -CollectionId $CollectionId -Map $Completed -AllowRootOnly $true
    }
}

function Start-ExistingCollection {
    param([bool]$Export)
    Require-Exchange -SearchRequired $false
    Section 'EXISTING COLLECTION'
    $CollectionId = Read-CollectionId -NewCollection $false
    Write-Host 'Enter the same mailbox addresses used for the original collection.'
    $Mailboxes = @(Read-CommaList 'Mailbox addresses (comma separated)')
    Choose-DiscoveryMailbox
    $Map = @(Make-FolderMap -CollectionId $CollectionId -Mailboxes $Mailboxes -Resolve $false)
    Section 'COLLECTION CONTENTS'
    $Summary = @(Get-CollectionFolders -Map $Map)
    $Summary | Format-Table Mailbox, Found, RootItems, SubfolderItems, TotalItems -AutoSize | Out-Host
    if (-not $Export) {
        Write-Host 'RootItems may be search-report messages. SubfolderItems counts copied hierarchy contents.'
        return
    }
    $Present = @($Map | Where-Object {
        $FolderName = $_.Folder
        @($Summary | Where-Object { $_.Folder -eq $FolderName -and $_.Found }).Count -gt 0
    })
    if (-not $Present.Count) { throw 'No matching collection folders were found.' }
    if ($Present.Count -ne $Map.Count) {
        Write-Host 'Only existing folders will be exported.' -ForegroundColor Yellow
    }
    Export-Collection -CollectionId $CollectionId -Map $Present -AllowRootOnly $false
}

# =====================================================================
# INTERACTIVE MENU
# =====================================================================

while ($true) {
    Section 'EXCHANGE KEYWORD SEARCH AND PST EXPORT'
    Write-Host '  [1] New search, copy results, and optionally export PST'
    Write-Host '  [2] Export an existing collection to PST'
    Write-Host '  [3] Inspect an existing collection'
    Write-Host '  [Q] Quit'
    Write-Host ''
    $Selection = (Read-Host 'Select an option').Trim().ToUpperInvariant()
    if ($Selection -eq 'Q') { break }
    try {
        switch ($Selection) {
            '1' { Start-NewSearch }
            '2' { Start-ExistingCollection -Export $true }
            '3' { Start-ExistingCollection -Export $false }
            default { Write-Host 'Select 1, 2, 3, or Q.' -ForegroundColor Yellow }
        }
    }
    catch {
        Write-Host ''
        Write-Host "Stopped: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host 'Any messages already copied remain in the Discovery Mailbox.' -ForegroundColor Yellow
    }
    Write-Host ''
    [void](Read-Host 'Press Enter to return to the menu')
}
