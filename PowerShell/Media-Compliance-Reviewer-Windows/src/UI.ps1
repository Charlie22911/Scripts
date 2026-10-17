$script:uiSourceDirectory=$PSScriptRoot
function Set-UiMessage([string]$Text) { $script:reviewUi.Message=$Text; $script:reviewUi.Controls.MessageText.Text=$Text }
function Get-UiScanExtensions {
    $types=[Collections.Generic.List[string]]::new()
    foreach($family in [MediaReviewer.Formats]::Families){if($script:reviewUi.FormatChoices[$family.Key].IsChecked){$types.AddRange([string[]]$family.Extensions)}}
    return $types.ToArray()
}
function Update-UiFormatSummary {
    $types=@(Get-UiScanExtensions);$script:reviewUi.Controls.FormatsButton.Content=('File types ({0})…' -f $types.Count);$script:reviewUi.Controls.FormatsButton.ToolTip=($types -join ', ')
}
function Set-UiFormatPreset {
    $u=$script:reviewUi
    if($u.Controls.ModeBox.SelectedIndex -ne 2){$preset=if($u.Controls.ModeBox.SelectedIndex -eq 1){[MediaReviewer.Formats]::Deep}else{[MediaReviewer.Formats]::Quick};foreach($family in [MediaReviewer.Formats]::Families){$u.FormatChoices[$family.Key].IsChecked=$family.Extensions[0] -in $preset}}
    if($u.Controls.AppDataOption -and $u.Controls.ModeBox.SelectedIndex -ne 2){$u.Controls.AppDataOption.IsChecked=$u.Controls.ModeBox.SelectedIndex -eq 1}
    Update-UiFormatSummary
}
function Update-UiControls {
    $u=$script:reviewUi; if(-not $u){return}
    $ready=$u.Engine -and $u.Engine.Ready -and -not $u.Opening -and -not $u.Closing
    $ready=$ready -and -not $u.Preferences.Exit;$running=$ready -and $u.Engine.Status.RunningScan
    foreach($name in @('CreateButton','OpenButton')){$u.Controls[$name].IsEnabled=-not $u.Opening -and -not $u.Closing -and -not $running -and -not $u.MaintenancePending -and -not $u.Preferences.Exit}
    foreach($name in @('StartButton','BrowseButton','SourceBox','ModeBox','FormatsButton','JpegBox','PngBox','WorkerBox','AgeBox','OptionsButton','AppDataOption','RootFilesOption')){$u.Controls[$name].IsEnabled=$ready -and -not $running -and -not $u.MaintenancePending}
    $u.Controls.ResumeButton.IsEnabled=$ready -and -not $running -and [bool]$u.Engine.Status.ResumeId -and -not $u.Engine.ReportActive
    $u.Controls.CancelButton.IsEnabled=$ready -and $running -and -not $u.Engine.Status.CancelRequested
    $u.Controls.MaintenanceButton.IsEnabled=$ready -and -not $u.MaintenancePending
    $u.Controls.LogButton.IsEnabled=$ready
    $selected=@($u.Cards | Where-Object {$_.IsSelected}); $available=@($selected | Where-Object {-not $_.IsPending})
    $u.Controls.SelectionLabel.Text=('{0} selected' -f $selected.Count)
    foreach($name in @('CompliantButton','NonCompliantButton','UnreviewedButton')){$u.Controls[$name].IsEnabled=$ready -and $available.Count -gt 0 -and -not $u.Engine.ReportActive -and ($u.Bucket -ne 'EVIDENCE' -or -not $u.MaintenancePending)}
    $u.Controls.VerifyButton.IsEnabled=$ready -and -not $u.MaintenancePending -and $available.Count -eq 1 -and $available[0].Record.Algorithm -ne 'sha256' -and $u.Bucket -ne 'EVIDENCE'
    $u.Controls.IdentityHistoryButton.IsEnabled=$ready -and -not $u.MaintenancePending -and $available.Count -eq 1
    $u.Controls.CancelWorkButton.Visibility=if($u.MaintenancePending -or ($u.Opening -and $u.Engine) -or ($u.Preferences.Exit -and $u.Preferences.Exit.Kind -eq 'Open')){'Visible'}else{'Collapsed'}
    $u.Controls.CancelWorkButton.Content=if($u.Opening -or ($u.Preferences.Exit -and $u.Preferences.Exit.Kind -eq 'Open')){'Cancel opening'}else{'Cancel task'}
    $u.Controls.CancelWorkButton.IsEnabled=-not $u.CancelOpening
    foreach($name in @('SearchBox','FolderBox','TypeBox','SortBox','FitBox','FilterButton','ResetButton','PageSizeBox','PageBox','GoButton','ReviewerBox','NotesBox','CardList')){$u.Controls[$name].IsEnabled=$ready}
    Update-UiPreviewRecoveryControls;Update-UiSourceControls;Update-UiDetailsControls;Update-UiReportControls
    $u.Controls.ClearSelectionButton.IsEnabled=$selected.Count -gt 0
    $u.Controls.SelectPageButton.IsEnabled=$ready -and $u.Cards.Count -gt 0 -and -not $u.PagePending
    $u.Controls.PreviousButton.IsEnabled=$ready -and $u.Page -gt 1 -and -not $u.PagePending
    $u.Controls.NextButton.IsEnabled=$ready -and $u.Page -lt [Math]::Max(1,[Math]::Ceiling($u.Total/[double]$u.PageSize)) -and -not $u.PagePending
    if($u.Controls.VaultButton){$u.Controls.VaultButton.IsEnabled=$ready;$u.Controls.VaultButton.Content=if($ready -and $u.Engine.VaultUnlocked){'Lock originals'}else{'Unlock originals'};$u.Controls.CaptureEvidenceButton.IsEnabled=$ready -and -not $u.MaintenancePending -and $available.Count -gt 0 -and $u.Bucket -ne 'EVIDENCE';foreach($name in @('StoredOriginalButton','ExportOriginalButton')){$u.Controls[$name].IsEnabled=$ready -and -not $u.MaintenancePending -and $available.Count -eq 1};$u.Controls.NonCompliantButton.IsEnabled=$u.Controls.NonCompliantButton.IsEnabled -and -not $u.MaintenancePending}
}
function New-UiRequest {
    $u=$script:reviewUi; $request=[MediaReviewer.PageRequest]::new()
    $request.Generation=$u.View.Current; $request.Bucket=$u.Bucket; $request.Page=$u.Page; $request.PageSize=$u.PageSize
    $request.Search=$u.Controls.SearchBox.Text; $request.Folder=$u.Controls.FolderBox.Text
    $request.Type=[string]$u.Controls.TypeBox.SelectedItem.Tag; $request.Sort=[string]$u.Controls.SortBox.SelectedItem.Tag
    return $request
}
function Request-UiPage([switch]$Reset,[switch]$Refill) {
    $u=$script:reviewUi; if(-not $u.Engine -or -not $u.Engine.Ready -or $u.Closing){return}
    if($Reset){[void]$u.View.Advance();$u.Cards.Clear();$u.PagePending=$false}
    $request=New-UiRequest
    if($Refill -and $u.Cards.Count){$request.ExistingIds=[long[]]@($u.Cards | ForEach-Object {$_.Record.GroupId})}
    try {$u.PageRequest=$u.Engine.RequestPage($request);$u.PagePending=$true;Set-UiMessage 'Loading results…'} catch {Set-UiMessage ('Results request failed: '+$_.Exception.Message);$u.PagePending=$false}
    Update-UiControls
}
function Add-UiCard($Record) {
    $card=[MediaReviewer.CardItem]::new($Record)
    $card.add_PropertyChanged({param($sender,$event) if($event.PropertyName -eq 'IsSelected'){$script:reviewUi.SelectionGeneration++};if($event.PropertyName -in @('IsSelected','IsPending')){Update-UiControls}})
    $script:reviewUi.Cards.Add($card)
}
function Apply-UiResult($Result) {
    if($Result.Kind -in @('Preferences','PreferencesSaved')){return (Apply-UiPreferencesResult $Result)}
    if($Result.Kind -in @('SourceCheck','SourceApply','SourceDiscard','SourceRoots','SourceConnectionStatus')){return (Apply-UiSourceResult $Result)}
    $u=$script:reviewUi
    if($Result.Kind -in @('PreviewRetry','PreviewRetryHistory','PreviewRetryStatus')){return (Apply-UiPreviewRecoveryResult $Result)}
    if($Result.Kind -in @('Inspector','Locations','Notes')){return (Apply-UiDetailsResult $Result)}
    if($Result.Kind -eq 'Page') {
        if(-not $u.View.Accept($Result.Generation) -or $Result.RequestId -ne $u.PageRequest){return $false}
        $u.PagePending=$false
        if(-not $Result.Success){Set-UiMessage ('Page load failed: '+$Result.Error);return $false}
        if(-not $Result.Page.Refill){$u.SourceRefreshRequired=$false};$data=$Result.Page; $u.Page=$data.Page; $u.PageSize=$data.PageSize;$u.Total=$data.Total;$u.LastRevision=$data.Revision
        if($data.Refill){foreach($row in $data.Rows){if(-not @($u.Cards | Where-Object {$_.Record.GroupId -eq $row.GroupId}).Count){Add-UiCard $row}}}
        else {
            $existing=@{};foreach($card in $u.Cards){$existing[$card.Record.GroupId]=$card}
            $wanted=[Collections.Generic.HashSet[long]]::new();foreach($row in $data.Rows){[void]$wanted.Add($row.GroupId)}
            for($i=$u.Cards.Count-1;$i -ge 0;$i--){if(-not $wanted.Contains($u.Cards[$i].Record.GroupId)){$u.Cards.RemoveAt($i)}}
            $position=0;foreach($row in $data.Rows){if($existing.ContainsKey($row.GroupId)){$card=$existing[$row.GroupId];$card.Update($row);$current=$u.Cards.IndexOf($card);if($current -ne $position){$u.Cards.Move($current,$position)}}else{Add-UiCard $row;$u.Cards.Move($u.Cards.Count-1,$position)};$position++}
        }
        $labels=@('To be reviewed','Compliant','Non-compliant','Evidence');$tabs=@('ToReviewTab','CompliantTab','NonCompliantTab','EvidenceTab')
        for($i=0;$i -lt 4;$i++){$u.Controls[$tabs[$i]].Content=('{0} ({1:n0})' -f $labels[$i],$data.Totals[$i])}
        $u.Controls.PageBox.Text=[string]$u.Page;$u.Controls.PageLabel.Text=('Page {0:n0} of {1:n0} · {2:n0} matching items · {3} shown' -f $u.Page,[Math]::Max(1,[Math]::Ceiling($u.Total/[double]$u.PageSize)),$u.Total,$u.Cards.Count)
        $u.Controls.EmptyPanel.Visibility=if($u.Cards.Count){[Windows.Visibility]::Collapsed}else{[Windows.Visibility]::Visible}
        $u.Controls.EmptyTitle.Text=if($u.Bucket -eq 'EVIDENCE'){'Evidence is locked'}else{'No matching media'}
        $u.Controls.EmptyText.Text=if($u.Bucket -eq 'EVIDENCE'){'Protected originals use placeholder cards. Clear filters, select a card, and use Stored original to unlock and authenticate it.'}else{'Choose another bucket or Reset view. You can scan while reviewing.'}
        Update-UiDetailsControls;if($u.Inspector){Update-UiItemNavigation $u.Inspector $u.InspectGroup 'PreviousItem' 'NextItem' 'Position'}
        Set-UiMessage 'Results loaded.';Update-UiControls;return $true
    }
    if($Result.Kind -eq 'Review') {
        if(-not $u.Pending.ContainsKey($Result.RequestId)){return $false}
        $operation=$u.Pending[$Result.RequestId];$u.Pending.Remove($Result.RequestId)
        if($Result.RequestId -eq $u.BackgroundOperationId){$u.MaintenancePending=$false;$u.BackgroundOperationId=''}
        foreach($card in $u.Cards){if($operation.Ids -contains $card.Record.GroupId){$card.IsPending=$false}}
        if(-not $Result.Success){if($operation.Capture){$u.RetentionRetry=[pscustomobject]@{Database=$u.Engine.DatabasePath;Request=$operation.Request}};Set-UiMessage ('Review failed; selection retained: '+$Result.Error);Update-UiControls;return $false}
        if($operation.Capture){$u.RetentionRetry=$null}
        if($operation.Target -eq 'EVIDENCE' -and $u.Inspector -and $operation.Ids -contains $u.InspectGroup){$u.Inspector.Close()}
        if($u.Bucket -eq $operation.Bucket -and $u.Bucket -ne $operation.Target){for($i=$u.Cards.Count-1;$i -ge 0;$i--){if($operation.Ids -contains $u.Cards[$i].Record.GroupId){$u.Cards.RemoveAt($i)}}}
        else{foreach($card in $u.Cards){if($operation.Ids -contains $card.Record.GroupId){$card.IsSelected=$false}}}
        [void]$u.View.Advance();$u.LastRevision=$u.Engine.Status.Revision
        Set-UiMessage ('Committed · '+$u.Engine.Status.LastCommitUtc.ToLocalTime().ToString('HH:mm:ss'))
        if($u.Bucket -eq $operation.Bucket -and $u.Bucket -ne $operation.Target){Request-UiPage -Refill}else{Request-UiPage}
        Update-UiControls;return $true
    }
    if($Result.Kind -eq 'Log') {
        if($u.LogWindow -and $Result.RequestId -eq $u.LogRequest){$u.LogText.Text=if($Result.Success){$Result.Value}else{'Log read failed: '+$Result.Error}}
        return $Result.Success
    }
    if(Apply-UiEvidenceResult $Result){return $Result.Success}
    if($Result.Kind -in @('FullVerification','IdentityResolution','IdentityHistory')){
        if(-not $u.IdentityTask -or $Result.RequestId -ne $u.IdentityTask.RequestId){return $false}
        $task=$u.IdentityTask;$u.IdentityTask=$null;$u.MaintenancePending=$false;$u.BackgroundOperationId=''
        foreach($card in $u.Cards){if($card.Record.GroupId -eq $task.GroupId){$card.IsPending=$false}}
        if(-not $Result.Success){Set-UiMessage ($Result.Kind+' failed; selection retained: '+$Result.Error);if($u.IdentityWindow){$u.IdentityWindow.FindName('Status').Text=$Result.Error;$u.IdentityWindow.FindName('Apply').IsEnabled=$true};Update-UiControls;return $false}
        if($Result.Kind -eq 'FullVerification'){
            $u.IdentityPreview=$Result.IdentityPreview
            if($task.View -eq $u.View.Current -and $task.Selection -eq $u.SelectionGeneration -and $Result.IdentityPreview.Revision -eq $u.Engine.Status.Revision){Show-UiIdentityPreview $Result.IdentityPreview}else{Set-UiMessage 'Verification finished. Select its card and click Verify full to review the result.'}
        }elseif($Result.Kind -eq 'IdentityHistory'){
            if($task.View -eq $u.View.Current -and $task.Selection -eq $u.SelectionGeneration){if($u.IdentityHistoryWindow){$u.IdentityHistoryWindow.Close()};$u.IdentityHistoryWindow=New-TextWindow 'Identity history' $Result.Value;$u.IdentityHistoryWindow.Add_Closed({$script:reviewUi.IdentityHistoryWindow=$null});$u.IdentityHistoryWindow.Show();Set-UiMessage 'Identity history ready.'}else{Set-UiMessage 'History read finished; selection changed.'}
        }else{
            if($u.IdentityWindow){$u.IdentityWindow.Close()};$u.IdentityPreview=$null
            Invalidate-UiDetailsGroup $task.GroupId;if($u.Inspector -and $u.InspectGroup -eq $task.GroupId){$u.Inspector.Close()};[void]$u.View.Advance();$u.LastRevision=$u.Engine.Status.Revision
            Request-UiPage;Set-UiMessage $Result.Value
        };Update-UiControls;return $true
    }
    if($Result.Kind -in @('Backup','Integrity','Import','Export','Report','EvidencePackageExport')){if($u.BackgroundOperationId -and $Result.RequestId -ne $u.BackgroundOperationId){return $false};$u.MaintenancePending=$false;$u.BackgroundOperationId='';Set-UiMessage $(if($Result.Success){$Result.Value}else{$Result.Kind+': '+$Result.Error});Update-UiControls;return $Result.Success}
    return $false
}
function Invoke-UiReview([string]$Status) {
    $u=$script:reviewUi;$cards=@($u.Cards | Where-Object {$_.IsSelected -and -not $_.IsPending});if(-not $cards.Count){return}
    $request=[MediaReviewer.ReviewRequest]::new();$request.OperationId=[Guid]::NewGuid().ToString('N');$request.GroupIds=[long[]]@($cards | ForEach-Object {$_.Record.GroupId});$request.Status=$Status;$request.Reviewer=$u.Controls.ReviewerBox.Text;$request.Notes=$u.Controls.NotesBox.Text
    if($Status -eq 'NON_COMPLIANT' -or $u.Bucket -eq 'EVIDENCE'){Start-UiOriginalRetention $request $u.Bucket;return}
    Set-UiMessage ('Saving '+$cards.Count+' selected item(s)…')
    try{$requestId=$u.Engine.RequestReview($request);$u.Pending[$requestId]=[pscustomobject]@{Ids=$request.GroupIds;Bucket=$u.Bucket;Target=$Status;OperationId=$request.OperationId};foreach($card in $cards){$card.IsPending=$true}}
    catch{Set-UiMessage ('Review failed; selection retained: '+$_.Exception.Message)}
    Update-UiControls
}
function New-TextWindow([string]$Title,[string]$Text) {
    $markup='<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="820" Height="590" MinWidth="500" MinHeight="350" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI"><Grid Margin="16"><Grid.RowDefinitions><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions><TextBox x:Name="Body" IsReadOnly="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto" Padding="10"/><StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,12,0,0"><Button x:Name="Save" Content="Save text…" Padding="14,6" Margin="6"/><Button x:Name="Close" Content="Close" Padding="20,6" Margin="6"/></StackPanel></Grid></Window>'
    $dialog=[Windows.Markup.XamlReader]::Parse($markup);$dialog.Title=$Title;$dialog.Owner=$script:reviewUi.Window;$dialog.FindName('Body').Text=$Text
    $dialog.FindName('Close').Add_Click({param($sender,$event) $sourceWindow=[Windows.Window]::GetWindow($sender);$sourceWindow.Close()})
    $dialog.FindName('Save').Add_Click({param($sender,$event) $sourceWindow=[Windows.Window]::GetWindow($sender);$picker=[Microsoft.Win32.SaveFileDialog]::new();$picker.Filter='Text file (*.txt)|*.txt';$picker.FileName='Media-Reviewer-log.txt';if($picker.ShowDialog($sourceWindow)){[IO.File]::WriteAllText($picker.FileName,$sourceWindow.FindName('Body').Text,[Text.UTF8Encoding]::new($false))}})
    return $dialog
}
function Invoke-UiIdentity([switch]$History) {
    $u=$script:reviewUi;$cards=@($u.Cards | Where-Object {$_.IsSelected -and -not $_.IsPending});if($cards.Count -ne 1 -or $u.MaintenancePending){return};$card=$cards[0]
    if(-not $History -and $u.IdentityPreview -and $u.IdentityPreview.Remaining -eq 0 -and $u.IdentityPreview.GroupId -eq $card.Record.GroupId -and $u.IdentityPreview.Revision -eq $u.Engine.Status.Revision){Show-UiIdentityPreview $u.IdentityPreview;return}
    try{
        $id=if($History){$u.Engine.RequestIdentityHistory($card.Record.GroupId)}else{$u.Engine.RequestFullVerification($card.Record.GroupId,$null)}
        $u.IdentityTask=[pscustomobject]@{RequestId=$id;GroupId=$card.Record.GroupId;View=$u.View.Current;Selection=$u.SelectionGeneration};$u.MaintenancePending=$true;$u.BackgroundOperationId=$id;$card.IsPending=$true
        Set-UiMessage $(if($History){'Reading identity history…'}else{'Verifying full contents of current source locations…'});Update-UiControls
    }catch{Set-UiMessage ('Verification request failed; selection retained: '+$_.Exception.Message)}
}
function Apply-UiMediaResult($Image) {
    $u=$script:reviewUi
    if($Image.Kind -eq 'Thumbnail' -and $u.MediaView.Accept($Image.Generation)){$card=$u.Cards | Where-Object {$_.Record.GroupId -eq $Image.GroupId} | Select-Object -First 1;if($card -and $card.Record.ContentId -eq $Image.ContentId -and $card.Record.PreviewRevision -eq $Image.PreviewRevision){$card.SetImage($Image.Image,$Image.Error);Update-UiInspectorThumbnail $card;return $true}}
    if($Image.Kind -eq 'Inspect'){return (Apply-UiInspectorImage $Image)}
    return $false
}
function Show-UiIdentityPreview([MediaReviewer.IdentityResolutionPreview]$Preview) {
    $u=$script:reviewUi;if($u.IdentityWindow){$u.IdentityWindow.Close()}
    $markup='<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Review full verification" Width="780" Height="610" MinWidth="520" MinHeight="420" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI"><Grid Margin="16"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/><RowDefinition Height="58"/><RowDefinition Height="Auto"/></Grid.RowDefinitions><TextBlock x:Name="Summary" TextWrapping="Wrap" Margin="0,0,0,10"/><TextBox Grid.Row="1" x:Name="Body" IsReadOnly="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto" Padding="8"/><CheckBox Grid.Row="2" x:Name="Accept" Content="Keep the existing verified decisions and preserve the candidate review in history" Margin="0,10"/><TextBlock Grid.Row="3" x:Name="Status" TextWrapping="Wrap"/><StackPanel Grid.Row="4" Orientation="Horizontal" HorizontalAlignment="Center"><Button x:Name="Apply" Content="Apply verified identities" Padding="14,7" Margin="6"/><Button x:Name="Close" Content="Close" Padding="24,7" Margin="6"/></StackPanel></Grid></Window>'
    $dialog=[Windows.Markup.XamlReader]::Parse($markup);$dialog.Owner=$u.Window;$dialog.Tag=[pscustomobject]@{Preview=$Preview;OperationId=[Guid]::NewGuid().ToString('N')};$u.IdentityWindow=$dialog
    $dialog.FindName('Summary').Text=('Group {0} · {1} locations · {2} distinct full identities. {3}' -f $Preview.GroupId,$Preview.Locations,$Preview.Outcomes.Count,$Preview.Message)
    $lines=[Collections.Generic.List[string]]::new();$lines.Add(('Candidate review: {0}; reviewer: {1}; notes (first 2048 characters): {2}' -f $Preview.SourceStatus,$Preview.SourceReviewer,$Preview.SourceNotes))
    foreach($outcome in $Preview.Outcomes){$destination=if($outcome.ExistingGroupId){'Existing group '+$outcome.ExistingGroupId}elseif($outcome.Anchor){'Original card; decision retained'}else{'New card; To be reviewed'};$lines.Add("`r`n$destination`r`nSHA-256: $($outcome.Hash)`r`nExample location (first 512 characters): $($outcome.ExamplePath)`r`nLocations: $($outcome.Locations) · Decision: $($outcome.Status)`r`nReviewer: $($outcome.Reviewer)`r`nNotes (first 512 characters): $($outcome.Notes)")};$dialog.FindName('Body').Text=$lines -join "`r`n"
    $dialog.FindName('Accept').Visibility=if($Preview.HasConflicts){'Visible'}else{'Collapsed'};$dialog.FindName('Apply').IsEnabled=$Preview.CanApply -and -not $Preview.HasConflicts
    $dialog.FindName('Accept').Add_Click({param($sender,$event) $window=[Windows.Window]::GetWindow($sender);$window.FindName('Apply').IsEnabled=$window.Tag.Preview.CanApply -and [bool]$sender.IsChecked})
    $dialog.FindName('Apply').Add_Click({param($sender,$event)
        $u=$script:reviewUi;$window=[Windows.Window]::GetWindow($sender);if($u.MaintenancePending){return};$preview=$window.Tag.Preview;$preview.AcceptConflicts=[bool]$window.FindName('Accept').IsChecked
        try{$id=$u.Engine.RequestIdentityResolution($preview,$window.Tag.OperationId);$u.IdentityTask=[pscustomobject]@{RequestId=$id;GroupId=$preview.GroupId;View=$u.View.Current;Selection=$u.SelectionGeneration};$u.MaintenancePending=$true;$u.BackgroundOperationId=$id;$sender.IsEnabled=$false;$window.FindName('Accept').IsEnabled=$false;$window.FindName('Status').Text='Preparing bounded batches; the visible identity changes only after publication commits.';foreach($card in $u.Cards){if($card.Record.GroupId -eq $preview.GroupId){$card.IsPending=$true}};Set-UiMessage 'Applying verified identities…';Update-UiControls}catch{$window.FindName('Status').Text=$_.Exception.Message}
    })
    $dialog.FindName('Close').Add_Click({param($sender,$event) [Windows.Window]::GetWindow($sender).Close()});$dialog.Add_Closed({$script:reviewUi.IdentityWindow=$null})
    $dialog.Show();Set-UiMessage 'Full verification is ready for review; changes have not been applied.'
}
function New-AboutWindow {
    $markup='<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="About and licenses" Width="820" Height="620" MinWidth="500" MinHeight="360" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI"><Grid Margin="16"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions><TextBlock x:Name="Credits" TextWrapping="Wrap" Margin="0,0,0,12" FontSize="14"/><ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto"><StackPanel x:Name="LicenseSections"/></ScrollViewer><Button Grid.Row="2" x:Name="Close" Content="Close" IsCancel="True" HorizontalAlignment="Center" Padding="24,7" Margin="0,12,0,0"/></Grid></Window>'
    $dialog=[Windows.Markup.XamlReader]::Parse($markup);if($script:reviewUi.Window.IsLoaded){$dialog.Owner=$script:reviewUi.Window}
    $dialog.FindName('Credits').Text="Media Compliance Reviewer for Windows`r`nMade by $([MediaReviewer.Product]::Author)`r`nDate: $([MediaReviewer.Product]::RevisionDate)    Version: $([MediaReviewer.Product]::Version)`r`nPortable offline Windows PowerShell 5.1 / .NET Framework / WPF"
    $sections=$script:RuntimeLicenseSections
    if(-not $sections){$sections=[IO.File]::ReadAllText((Join-Path ([MediaReviewer.Native]::RuntimeDirectory) 'LicenseSections.json'),[Text.Encoding]::UTF8) | ConvertFrom-Json}
    foreach($section in $sections){
        $expander=[Windows.Controls.Expander]::new();$expander.Header=[string]$section.Title;$expander.Margin='0,4,0,4';$expander.Padding='6'
        $body=[Windows.Controls.TextBox]::new();$body.IsReadOnly=$true;$body.TextWrapping='Wrap';$body.VerticalScrollBarVisibility='Auto';$body.MaxHeight=320;$body.Padding='8';$body.Text=[string]$section.Text
        $expander.Content=$body;$dialog.FindName('LicenseSections').Children.Add($expander) | Out-Null
    }
    $dialog.FindName('Close').Add_Click({param($sender,$event) [Windows.Window]::GetWindow($sender).Close()})
    return $dialog
}
function Get-UiNativeDestination([string]$Source) {
    $picker=[Microsoft.Win32.SaveFileDialog]::new();$picker.Title='HTML database detected — choose a new local Windows copy; the original is preserved'
    $picker.Filter='Native SQLite database|*.sqlite';$picker.FileName=[IO.Path]::GetFileNameWithoutExtension($Source)+'-Windows.sqlite';$picker.OverwritePrompt=$false
    if($picker.ShowDialog($script:reviewUi.Window)){return $picker.FileName}
    return $null
}
function Open-UiDatabase([string]$Path,[bool]$Create,[string]$ImportSource='') {
    if(-not $Create -and -not $ImportSource -and [IO.Path]::GetExtension($Path) -ieq '.mcrevidence'){Show-UiPackage $Path;return}
    if($script:packageWindow){$script:packageWindow.Close()}
    if($script:reviewUi.DetailsWindow){$script:reviewUi.DetailsWindow.Close();if($script:reviewUi.DetailsWindow){return}}
    if($script:reviewUi.ReportWindow){$script:reviewUi.ReportWindow.Close()};Clear-UiStoredView;[void]$script:reviewUi.InspectView.Advance();$script:reviewUi.InspectGroup=0
    if($script:reviewUi.Inspector){$script:reviewUi.Inspector.Close();$script:reviewUi.Inspector=$null}
    if(-not (Test-UiPreferencesExit ([pscustomobject]@{Kind='Open';Path=$Path;Create=$Create;ImportSource=$ImportSource}))){Update-UiControls;return};Initialize-UiPreferences
    if($script:reviewUi.RecoveryWindow){$script:reviewUi.RecoveryWindow.Close()};if($script:reviewUi.SourcesWindow){$script:reviewUi.SourcesWindow.Close()}
    $u=$script:reviewUi;$u.Opening=$true;$u.CancelOpening=$false;$u.SourceRefreshRequired=$false;$u.Cards.Clear();[void]$u.View.Advance();[void]$u.MediaView.Advance();$u.Pending.Clear();$u.Initialized=$false;$u.PagePending=$false;$u.PageRequest='';$u.BackgroundOperationId='';$u.MaintenancePending=$false
    if($u.IdentityWindow){$u.IdentityWindow.Close()};if($u.IdentityHistoryWindow){$u.IdentityHistoryWindow.Close()};$u.IdentityTask=$null;$u.IdentityPreview=$null
    $u.Controls.EmptyPanel.Visibility=[Windows.Visibility]::Visible;$u.Controls.EmptyTitle.Text=if($Create){'Creating database'}else{'Opening database'};$u.Controls.EmptyText.Text='Database controls stay unavailable until initialization succeeds.'
    $u.NextOpen=[pscustomobject]@{Path=$Path;Create=$Create;ImportSource=$ImportSource}
    if($u.Engine){$u.Engine.CloseAsync()}else{$u.HtmlPrompted=$false;$u.Engine=if($ImportSource){[MediaReviewer.ReviewerEngine]::ImportHtml($ImportSource,$Path)}else{[MediaReviewer.ReviewerEngine]::Open($Path,$Create)};$u.NextOpen=$null}
    Update-UiControls
}
function Invoke-UiTick {
    $u=$script:reviewUi;if(-not $u){return}
    try {
        if($u.Closing){
            if($u.ClosingPackage){if(-not $u.ClosingPackage.Tag.Session.Closed){$u.Controls.ActivityText.Text='Closing protected package; waiting for owned export cleanup.';return};Invoke-PackageTick $u.ClosingPackage;$u.ClosingPackage=$null}
            if(-not $u.Engine -or $u.Engine.Closed){$u.Loader.CloseAsync();if($u.Loader.Closed){$u.AllowClose=$true;if($u.Timer){$u.Timer.Stop()};$u.Window.Close()}}
            $u.Controls.ActivityText.Text='Closing owned workers; committed work is retained.';return
        }
        if($u.CancelOpening){
            if(-not $u.Engine -or $u.Engine.Closed){
                $u.Engine=$null;$u.NextOpen=$null;$u.CancelOpening=$false;$u.Opening=$false;$u.Initialized=$false;$u.PagePending=$false
                $u.Controls.DatabaseLabel.Text='No database open';$u.Controls.ActivityText.Text='Ready'
                $u.Controls.EmptyPanel.Visibility=[Windows.Visibility]::Visible;$u.Controls.EmptyTitle.Text='Opening cancelled';$u.Controls.EmptyText.Text='Choose Create or Open to continue.'
                Set-UiMessage 'Initialization cancelled; original files and completed commits were retained.'
            }else{$u.Controls.ActivityText.Text='Cancelling initialization; waiting for owned workers.'}
            Update-UiControls;return
        }
        if($u.NextOpen){
            if($u.Engine -and -not $u.Engine.Closed){$u.Controls.ActivityText.Text='Closing the previous database; waiting for owned workers.';Update-UiControls;return}
            $next=$u.NextOpen;$u.NextOpen=$null;$u.HtmlPrompted=$false;$u.Engine=if($next.ImportSource){[MediaReviewer.ReviewerEngine]::ImportHtml($next.ImportSource,$next.Path)}else{[MediaReviewer.ReviewerEngine]::Open($next.Path,$next.Create)}
        }
        if($u.Engine){
            $status=$u.Engine.Status
            if($u.Engine.DetectedHtml -and -not $u.HtmlPrompted){
                $u.HtmlPrompted=$true;$source=$u.Engine.DatabasePath;$destination=Get-UiNativeDestination $source
                if($destination){Open-UiDatabase -Path $destination -Create $true -ImportSource $source}else{$u.CancelOpening=$true;$u.NextOpen=$null;$u.Engine.CloseAsync();Set-UiMessage 'Conversion cancelled; the original database was retained.'}
                return
            }
            if(-not $u.Initialized -and $u.Engine.Ready -and -not $u.Preferences.LoadRequest -and -not $u.Preferences.LoadFailed){try{$u.Preferences.LoadRequest=$u.Engine.RequestPreferences();$u.Controls.PreferencesText.Text='Loading saved preferences...'}catch{$u.Preferences.LoadFailed=$true;$u.Controls.PreferencesText.Text=$_.Exception.Message;$u.Controls.RetryPreferencesButton.Visibility='Visible'}}
            if($u.Engine.Faulted){$u.Opening=$false;$u.Controls.EmptyPanel.Visibility=[Windows.Visibility]::Visible;$u.Controls.EmptyTitle.Text='Database unavailable';$u.Controls.EmptyText.Text=$status.Error;Set-UiMessage ('Database stopped: '+$status.Error)}
            $stage=$status.Stage;if($u.BackgroundOperationId){$background=$u.Engine.GetOperationProgress($u.BackgroundOperationId);if($background){$stage=$background.Stage}}
            $u.Controls.ActivityText.Text=('{0} · user {1}/{2} · {3:n0}/{4:n0} files processed · {5:n0} errors · {6:n0} queued · WAL {7:n1} MiB' -f $stage,$status.CompletedUsers,$status.TotalUsers,$status.ProcessedFiles,$status.DiscoveredFiles,$status.FailedFiles,$status.PendingWork,($status.WalBytes/1MB))
            [MediaReviewer.UiResult]$reply=$null;$drained=0
            while($drained -lt 8 -and $u.Engine.TryTakeResult([ref]$reply)){Apply-UiResult $reply | Out-Null;$drained++}
            Invoke-UiPreferencesTick;Complete-UiPreferencesExit
            if($u.Initialized -and -not $u.Opening -and -not $u.PagePending -and $status.Revision -ne $u.LastRevision -and ([DateTime]::UtcNow-$u.LastRefresh).TotalSeconds -ge 1 -and -not $u.Pending.Count){$u.LastRefresh=[DateTime]::UtcNow;Request-UiPage}
        }
        [MediaReviewer.MediaLoadResult]$image=$null;$drained=0
        while($drained -lt 8 -and $u.Loader.TryTakeResult([ref]$image)){
            Apply-UiMediaResult $image | Out-Null
            $drained++
        }
        $queued=0
        foreach($card in $u.Cards){if($queued -ge 20){break};if($card.CanLoadImage){if($u.Loader.QueueThumbnail($card.Record.GroupId,$card.Record.ContentId,$u.MediaView.Current,$card.Record.PreviewBytes,$card.Record.PreviewRevision)){$card.ImageQueued=$true;$card.Record.PreviewBytes=$null;$queued++}else{break}}}
        Update-UiControls
    } catch{Set-UiMessage ('Interface operation failed: '+$_.Exception.Message)}
}
function New-ReviewerWindow([MediaReviewer.ReviewerEngine]$Engine=$null) {
    Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml,System.Windows.Forms
    $markup=if($script:EmbeddedMainWindowXaml){$script:EmbeddedMainWindowXaml}else{[IO.File]::ReadAllText((Join-Path $script:uiSourceDirectory 'MainWindow.xaml'),[Text.Encoding]::UTF8)}
    $window=[Windows.Markup.XamlReader]::Parse($markup);$controls=@{}
    foreach($name in @('DatabaseLabel','CreateButton','OpenButton','MaintenanceButton','LogButton','HelpButton','AboutButton','SourceBox','BrowseButton','StartButton','ResumeButton','CancelButton','ModeBox','FormatsButton','JpegBox','PngBox','WorkerBox','AgeBox','OptionsButton','SelectPageButton','ToReviewTab','CompliantTab','NonCompliantTab','EvidenceTab','SearchBox','FolderBox','TypeBox','SortBox','FitBox','FilterButton','ResetButton','ActionBar','SelectionLabel','ReviewerBox','NotesBox','CompliantButton','NonCompliantButton','UnreviewedButton','ClearSelectionButton','GalleryScroll','CardList','EmptyPanel','EmptyTitle','EmptyText','PageSizeBox','PreviousButton','NextButton','PageBox','GoButton','PageLabel','StatusPanel','ActivityText','MessageText')){$controls[$name]=$window.FindName($name)}
    $script:reviewUi=[pscustomobject]@{Window=$window;Controls=$controls;Engine=$Engine;Loader=[MediaReviewer.MediaLoader]::new();Cards=[Collections.ObjectModel.ObservableCollection[MediaReviewer.CardItem]]::new();View=[MediaReviewer.ViewGeneration]::new();MediaView=[MediaReviewer.ViewGeneration]::new();InspectView=[MediaReviewer.ViewGeneration]::new();InspectGroup=0L;Inspector=$null;Page=1;PageSize=60;Bucket='TO_REVIEW';Total=0L;LastRevision=-1L;LastRefresh=[DateTime]::UtcNow;PagePending=$false;PageRequest='';Pending=@{};Message='';Opening=[bool]$Engine;Initialized=$false;NextOpen=$null;Closing=$false;AllowClose=$false;Timer=$null;LogWindow=$null;LogText=$null;LogRequest='';MaintenancePending=$false;SuppressEvents=$false}
    $script:reviewUi | Add-Member -NotePropertyName DetailsWindow -NotePropertyValue $null
    $script:reviewUi | Add-Member -NotePropertyName ClosingPackage -NotePropertyValue $null
    $script:reviewUi | Add-Member -NotePropertyName DetailsGeneration -NotePropertyValue ([MediaReviewer.ViewGeneration]::new())
    $controls.CardList.ItemsSource=$script:reviewUi.Cards
    foreach($name in @('VerifyButton','IdentityHistoryButton')){$controls[$name]=$window.FindName($name)}
    foreach($name in @('IdentityTask','IdentityPreview','IdentityWindow','IdentityHistoryWindow')){$script:reviewUi | Add-Member -NotePropertyName $name -NotePropertyValue $null}
    $script:reviewUi | Add-Member -NotePropertyName SelectionGeneration -NotePropertyValue 0L
    $controls.VerifyButton.Add_Click({Invoke-UiIdentity});$controls.IdentityHistoryButton.Add_Click({Invoke-UiIdentity -History})
    $controls['CancelWorkButton']=$window.FindName('CancelWorkButton')
    $script:reviewUi | Add-Member -NotePropertyName BackgroundOperationId -NotePropertyValue ''
    $script:reviewUi | Add-Member -NotePropertyName CancelOpening -NotePropertyValue $false
    $script:reviewUi | Add-Member -NotePropertyName HtmlPrompted -NotePropertyValue $false
    $controls.CancelWorkButton.Add_Click({$u=$script:reviewUi;if($u.Opening -or ($u.Preferences.Exit -and $u.Preferences.Exit.Kind -eq 'Open')){$u.Preferences.Exit=$null;$u.Opening=$true;$u.CancelOpening=$true;$u.NextOpen=$null;if($u.Engine){$u.Engine.CloseAsync()};Set-UiMessage 'Cancelling initialization; waiting for owned work to stop…';Update-UiControls}elseif($u.BackgroundOperationId){$u.Engine.CancelOperation($u.BackgroundOperationId);Set-UiMessage 'Cancellation requested; waiting for the current bounded operation…'}})
    $script:reviewUi | Add-Member -NotePropertyName FormatChoices -NotePropertyValue @{}
    $controls['FormatsButton']=$window.FindName('FormatsButton');$formatMenu=[Windows.Controls.ContextMenu]::new()
    foreach($family in [MediaReviewer.Formats]::Families){
        $choice=[Windows.Controls.MenuItem]::new();$choice.Header=$family.Label;$choice.ToolTip=($family.Extensions -join ', ');$choice.IsCheckable=$true;$choice.StaysOpenOnClick=$true;$choice.IsChecked=$family.Extensions[0] -in [MediaReviewer.Formats]::Quick
        $choice.Add_Click({$script:reviewUi.Controls.ModeBox.SelectedIndex=2;Update-UiFormatSummary});$script:reviewUi.FormatChoices[$family.Key]=$choice;$formatMenu.Items.Add($choice) | Out-Null
        $filter=[Windows.Controls.ComboBoxItem]::new();$filter.Content=$family.Label;$filter.Tag=$family.Key;$controls.TypeBox.Items.Add($filter) | Out-Null
    }
    $controls['JpegBox']=$script:reviewUi.FormatChoices['jpeg'];$controls['PngBox']=$script:reviewUi.FormatChoices['png'];$controls.FormatsButton.ContextMenu=$formatMenu
    $controls.FormatsButton.Add_Click({param($sender,$event) $sender.ContextMenu.PlacementTarget=$sender;$sender.ContextMenu.IsOpen=$true});Update-UiFormatSummary
    $optionsMenu=[Windows.Controls.ContextMenu]::new()
    foreach($option in @(@('AppDataOption','Include AppData / Application Data'),@('RootFilesOption','Include root-level files'))){
        $entry=[Windows.Controls.MenuItem]::new();$entry.Header=$option[1];$entry.IsCheckable=$true;$entry.StaysOpenOnClick=$true
        $entry.Add_Click({$script:reviewUi.Controls.ModeBox.SelectedIndex=2});$controls[$option[0]]=$entry;$optionsMenu.Items.Add($entry) | Out-Null
    }
    $controls.OptionsButton.ContextMenu=$optionsMenu;$controls.OptionsButton.Add_Click({param($sender,$event) $sender.ContextMenu.PlacementTarget=$sender;$sender.ContextMenu.IsOpen=$true})
    $controls.CreateButton.Add_Click({$picker=[Microsoft.Win32.SaveFileDialog]::new();$picker.Filter='SQLite database (*.sqlite)|*.sqlite';$picker.FileName='Media-Review.sqlite';$picker.OverwritePrompt=$false;if($picker.ShowDialog($script:reviewUi.Window)){Open-UiDatabase $picker.FileName $true}})
    $controls.OpenButton.Add_Click({$picker=[Microsoft.Win32.OpenFileDialog]::new();$picker.Filter='Review database or protected package|*.sqlite;*.db;*.mcrevidence|All files|*.*';if($picker.ShowDialog($script:reviewUi.Window)){Open-UiDatabase $picker.FileName $false}})
    $controls.BrowseButton.Add_Click({$picker=[Windows.Forms.FolderBrowserDialog]::new();$picker.Description='Choose the home-share root. You can also paste a mapped-drive or UNC path into Source root.';if($picker.ShowDialog() -eq [Windows.Forms.DialogResult]::OK){$script:reviewUi.Controls.SourceBox.Text=$picker.SelectedPath};$picker.Dispose()})
    $controls.StartButton.Add_Click({try{$u=$script:reviewUi;$ageValue=0;if(-not [int]::TryParse($u.Controls.AgeBox.Text,[ref]$ageValue)){throw 'Enter a whole number of years; zero includes all ages.'};$options=[MediaReviewer.ScanOptions]::new();$options.RootPath=$u.Controls.SourceBox.Text;$options.WorkerCount=[int]$u.Controls.WorkerBox.SelectedItem;$options.ModifiedAgeYears=$ageValue;$options.ExcludeApplicationData=-not [bool]$u.Controls.AppDataOption.IsChecked;$options.IncludeRootFiles=[bool]$u.Controls.RootFilesOption.IsChecked;$options.Extensions=[string[]]@(Get-UiScanExtensions);$u.Engine.StartScan($options) | Out-Null;Set-UiMessage 'Scan started; review is available as results commit.';Update-UiControls}catch{Set-UiMessage ('Scan could not start: '+$_.Exception.Message)}})
    $controls.ResumeButton.Add_Click({try{$u=$script:reviewUi;$u.Engine.ResumeScan($u.Engine.Status.ResumeId,[int]$u.Controls.WorkerBox.SelectedItem);Set-UiMessage 'Resuming saved pending work.';Update-UiControls}catch{Set-UiMessage ('Resume failed: '+$_.Exception.Message)}})
    $controls.CancelButton.Add_Click({$script:reviewUi.Engine.CancelScan();Set-UiMessage 'Cancellation requested; waiting for the existing owned workers.';Update-UiControls})
    foreach($name in @('ToReviewTab','CompliantTab','NonCompliantTab','EvidenceTab')){$controls[$name].Add_Click({param($sender,$event) $u=$script:reviewUi;$u.Bucket=[string]$sender.Tag;$u.Page=1;Request-UiPage -Reset})}
    $controls.FilterButton.Add_Click({$script:reviewUi.Page=1;Request-UiPage -Reset})
    $controls.SearchBox.Add_KeyDown({if($args[1].Key -eq [Windows.Input.Key]::Return){$script:reviewUi.Page=1;Request-UiPage -Reset}})
    $controls.ResetButton.Add_Click({$u=$script:reviewUi;$u.SuppressEvents=$true;$u.Controls.SearchBox.Clear();$u.Controls.FolderBox.Clear();$u.Controls.TypeBox.SelectedIndex=0;$u.Controls.SortBox.SelectedIndex=0;$u.SuppressEvents=$false;$u.Page=1;Request-UiPage -Reset})
    $controls.PageSizeBox.Add_SelectionChanged({$u=$script:reviewUi;if(-not $u.SuppressEvents -and $u.Controls.PageSizeBox.SelectedItem){$u.PageSize=[int]$u.Controls.PageSizeBox.SelectedItem;$u.Page=1;Request-UiPage -Reset}})
    $controls.PreviousButton.Add_Click({$script:reviewUi.Page=[Math]::Max(1,$script:reviewUi.Page-1);Request-UiPage -Reset})
    $controls.NextButton.Add_Click({$script:reviewUi.Page++;Request-UiPage -Reset})
    $go={try{$number=0;if(-not [int]::TryParse($script:reviewUi.Controls.PageBox.Text,[ref]$number) -or $number -lt 1){throw 'Enter a positive page number.'};$script:reviewUi.Page=$number;Request-UiPage -Reset}catch{Set-UiMessage $_.Exception.Message}}
    $controls.GoButton.Add_Click($go);$controls.PageBox.Add_KeyDown({if($args[1].Key -eq [Windows.Input.Key]::Return){$script:reviewUi.Controls.GoButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))}})
    $controls.CompliantButton.Add_Click({Invoke-UiReview 'COMPLIANT'});$controls.NonCompliantButton.Add_Click({Invoke-UiReview 'NON_COMPLIANT'});$controls.UnreviewedButton.Add_Click({Invoke-UiReview 'TO_REVIEW'})
    $controls.ClearSelectionButton.Add_Click({foreach($card in $script:reviewUi.Cards){if(-not $card.IsPending){$card.IsSelected=$false}};Update-UiControls})
    $controls.SelectPageButton.Add_Click({foreach($card in $script:reviewUi.Cards){if(-not $card.IsPending){$card.IsSelected=$true}};Update-UiControls})
    $controls.FitBox.Add_SelectionChanged({$u=$script:reviewUi;$style=[Windows.Style]::new([Windows.Controls.Image]);$style.Setters.Add([Windows.Setter]::new([Windows.Controls.Image]::StretchProperty,[Windows.Media.Stretch][Enum]::Parse([Windows.Media.Stretch],[string]$u.Controls.FitBox.SelectedItem.Tag)));$u.Window.Resources['ThumbnailStyle']=$style})
    $controls.CardList.AddHandler([Windows.Controls.Button]::ClickEvent,[Windows.RoutedEventHandler]{param($sender,$event) if($event.Source -is [Windows.Controls.Button] -and $event.Source.Name -eq 'CardDetailsButton'){Show-UiDetails $event.Source.DataContext;$event.Handled=$true};if($event.Source -is [Windows.Controls.Button] -and $event.Source.Name -eq 'CardInspectButton'){Show-UiInspector $event.Source.DataContext;$event.Handled=$true}})
    $controls.CardList.Add_MouseDoubleClick({$origin=$args[1].OriginalSource;while($origin -and -not ($origin.DataContext -is [MediaReviewer.CardItem])){try{$origin=[Windows.Media.VisualTreeHelper]::GetParent($origin)}catch{$origin=$null}};if($origin -and $origin.DataContext -is [MediaReviewer.CardItem]){Show-UiInspector $origin.DataContext}})
    $controls.ModeBox.Add_SelectionChanged({Set-UiFormatPreset})
    $controls.HelpButton.Add_Click({$dialog=New-TextWindow 'Help' "Create or open a local SQLite database. Keep the active WAL database off network and synchronized shares. Media sources may be local, mapped drives, or UNC shares.`r`n`r`nEnter the source root whose immediate subfolders are user homes. Select types and exclusions, then Start scan. Open recognizes saved databases from the final HTML build (schema 4) and asks for a new local Windows copy. The original is preserved. Cancel opening stops initialization; Cancel task stops backup/integrity work. The saved alphabetical manifest is used for Resume; a fresh scan finds later additions to completed users. Reparse points are skipped.`r`n`r`nSelect cards, or use Select page for the visible page, then click a review action. Saving means queued; Committed means the write succeeded. Inspector uses the saved preview immediately and requests a bounded source preview separately. Fit shows the whole image; Fill crops around the center. The inspector sizes source previews for the window and zoom, with bounded pixel memory. Use +/-, Ctrl+wheel, 100% and dragging/scrolling; source and preview dimensions identify sampling. Previous/next follows the visible gallery page. Details / notes on each card shows saved metadata and locations in pages of up to 100. Save notes changes reviewer text without changing buckets. Extended imported text is read-only; failed saves retain the draft.`r`n`r`nA failed preview retains its fingerprinted record. Select cards and choose Retry previews to regenerate saved thumbnails or posters without changing reviews. Preview tasks shows outcomes and resumes interrupted work. Existing good previews survive failures. Schema 7 upgrades create a validated pre-upgrade backup. Open Log for affected paths and retries. Ordinary records and thumbnails are not encrypted. Non-compliant and Evidence actions retain encrypted originals. Create or unlock the originals vault when prompted. Stored original authenticates saved bytes; Export original writes a new full-resolution file you explicitly choose. Lock originals clears protected views. Maintenance offers retention guardrails and bounded cleanup of unfinished captures.`r`n`r`nQuick selects common image and video types. Quick includes HEIC/HEIF and other common images. Deep adds RAW and includes AppData. Options controls AppData and root-level files. File types edits a Custom selection. Videos use saved posters; RAW uses an embedded JPEG when available. Preview limits/failures do not discard fingerprints. Both scan modes use quick fingerprints above 1 MiB; small files and previously full-hashed paths receive full SHA-256. Candidate matches do not prove identical files. Select one candidate and use Verify full, then review and apply its full identities. Existing verified decisions require explicit conflict acknowledgement. Identity history retains archived reviews. Sources and the database must still match the prepared result. Collisions must be resolved before retaining originals. Ordinary metadata and thumbnails remain unencrypted. Select Evidence cards and choose a review bucket to authenticate and reclassify them. Maintenance offers Change originals password; earlier database copies retain their old password. Maintenance offers Sources / reconnect to check moved or copied media before applying a connection. Maintenance offers Export review report for CSV and offline HTML pages. Reports are plaintext; explicitly included Evidence details require an unlocked originals vault and authentication. Maintenance exports selected encrypted originals as protected packages. Open .mcrevidence packages read only with the originals password; Export original writes plaintext only to your chosen new file. Full playback and ZIP remain unavailable.";$dialog.ShowDialog() | Out-Null})
    $controls.AboutButton.Add_Click({$dialog=New-AboutWindow;$dialog.ShowDialog() | Out-Null})
    $controls.LogButton.Add_Click({try{$u=$script:reviewUi;if($u.LogWindow){$u.LogWindow.Activate() | Out-Null;return};$u.LogWindow=New-TextWindow 'Log' 'Loading recent scan summaries and retry details…';$u.LogText=$u.LogWindow.FindName('Body');$u.LogWindow.Add_Closed({$script:reviewUi.LogWindow=$null;$script:reviewUi.LogText=$null});$u.LogWindow.Show();$u.LogRequest=$u.Engine.RequestLog()}catch{Set-UiMessage $_.Exception.Message}})
    $menu=[Windows.Controls.ContextMenu]::new()
    $backupItem=[Windows.Controls.MenuItem]::new();$backupItem.Header='Portable backup…';$backupItem.Add_Click({try{$u=$script:reviewUi;$picker=[Microsoft.Win32.SaveFileDialog]::new();$picker.Filter='SQLite backup (*.sqlite)|*.sqlite';$picker.FileName='Media-Review-backup.sqlite';$picker.OverwritePrompt=$false;if($picker.ShowDialog($u.Window)){$u.BackgroundOperationId=$u.Engine.RequestBackup($picker.FileName);$u.MaintenancePending=$true;Set-UiMessage 'Backup queued…';Update-UiControls}}catch{Set-UiMessage ('Backup failed: '+$_.Exception.Message)}});$menu.Items.Add($backupItem) | Out-Null
    $checkItem=[Windows.Controls.MenuItem]::new();$checkItem.Header='Check database integrity';$checkItem.Add_Click({try{$u=$script:reviewUi;$u.BackgroundOperationId=$u.Engine.RequestIntegrity();$u.MaintenancePending=$true;Set-UiMessage 'Integrity check queued…';Update-UiControls}catch{Set-UiMessage $_.Exception.Message}});$menu.Items.Add($checkItem) | Out-Null
    $controls.MaintenanceButton.ContextMenu=$menu;$controls.MaintenanceButton.Add_Click({param($sender,$event) $sender.ContextMenu.PlacementTarget=$sender;$sender.ContextMenu.IsOpen=$true})
    $window.Add_Closing({param($sender,$event) $u=$script:reviewUi;if(-not $u.AllowClose){$event.Cancel=$true;if($u.DetailsWindow){$u.DetailsWindow.Close();if($u.DetailsWindow){return}};if(-not (Test-UiPreferencesExit ([pscustomobject]@{Kind='Close'}))){Update-UiControls;return};$u.Closing=$true;if($script:packageWindow){$u.ClosingPackage=$script:packageWindow;$u.ClosingPackage.Close()};if($u.ReportWindow){$u.ReportWindow.Close()};if($u.RecoveryWindow){$u.RecoveryWindow.Close()};if($u.SourcesWindow){$u.SourcesWindow.Close()};if($u.Inspector){$u.Inspector.Close()};if($u.Engine){$u.Engine.CloseAsync()};Update-UiControls}})
    Update-UiControls
    Add-UiPreferencesControls;Add-UiEvidenceControls $window
    Add-UiPreviewRecoveryControls $window;Add-UiReportControls;Add-UiPackageControls
    Update-UiControls
    return $window
}
function Start-ReviewerInterface {
    $window=New-ReviewerWindow
    $timer=[Windows.Threading.DispatcherTimer]::new();$timer.Interval=[TimeSpan]::FromMilliseconds(100);$timer.Add_Tick({Invoke-UiTick});$script:reviewUi.Timer=$timer;$timer.Start()
    $work=[Windows.SystemParameters]::WorkArea;$window.Width=[Math]::Min($window.Width,$work.Width);$window.Height=[Math]::Min($window.Height,$work.Height)
    [void]$window.ShowDialog()
}

if(-not $script:EmbeddedMainWindowXaml -and (Test-Path -LiteralPath (Join-Path $script:uiSourceDirectory 'EvidenceUI.ps1'))){. (Join-Path $script:uiSourceDirectory 'EvidenceUI.ps1')}

if(-not $script:EmbeddedMainWindowXaml -and (Test-Path -LiteralPath (Join-Path $script:uiSourceDirectory 'InspectorUI.ps1'))){. (Join-Path $script:uiSourceDirectory 'InspectorUI.ps1')}

if(-not $script:EmbeddedMainWindowXaml -and (Test-Path -LiteralPath (Join-Path $script:uiSourceDirectory 'ReviewDetailsUI.ps1'))){. (Join-Path $script:uiSourceDirectory 'ReviewDetailsUI.ps1')}

if(-not $script:EmbeddedMainWindowXaml -and (Test-Path -LiteralPath (Join-Path $script:uiSourceDirectory 'SourcesUI.ps1'))){. (Join-Path $script:uiSourceDirectory 'SourcesUI.ps1')}

if(-not $script:EmbeddedMainWindowXaml -and (Test-Path -LiteralPath (Join-Path $script:uiSourceDirectory 'PreferencesUI.ps1'))){. (Join-Path $script:uiSourceDirectory 'PreferencesUI.ps1')}

if(-not $script:EmbeddedMainWindowXaml -and (Test-Path -LiteralPath (Join-Path $script:uiSourceDirectory 'ReportsUI.ps1'))){. (Join-Path $script:uiSourceDirectory 'ReportsUI.ps1')}
if(-not $script:EmbeddedMainWindowXaml -and (Test-Path -LiteralPath (Join-Path $script:uiSourceDirectory 'PackageUI.ps1'))){. (Join-Path $script:uiSourceDirectory 'PackageUI.ps1')}
