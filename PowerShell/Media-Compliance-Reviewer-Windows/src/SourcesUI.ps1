function Update-UiPreviewRecoveryControls {
    $u=$script:reviewUi;if(-not $u -or -not $u.Controls.RetryPreviewsButton){return};$ready=$u.Engine -and $u.Engine.Ready -and -not $u.Opening -and -not $u.Closing
    $selected=@($u.Cards | Where-Object {$_.IsSelected -and -not $_.IsPending});$u.Controls.RetryPreviewsButton.IsEnabled=$ready -and -not $u.MaintenancePending -and $selected.Count -gt 0 -and $selected.Count -le 200 -and $u.Bucket -ne 'EVIDENCE';$u.Controls.PreviewTasksButton.IsEnabled=$ready
    $w=$u.RecoveryWindow;if($w -and $w.Tag){$eligible=$ready -and -not $u.MaintenancePending -and $w.Tag.Batch -and $w.Tag.Batch.State -in @('queued','interrupted');$w.FindName('Resume').IsEnabled=$eligible;$w.FindName('Discard').IsEnabled=$eligible;$w.FindName('Refresh').IsEnabled=$ready}
}
function Start-UiPreviewRetry {
    $u=$script:reviewUi;if($u.MaintenancePending){return};$cards=@($u.Cards | Where-Object {$_.IsSelected -and -not $_.IsPending});if(-not $cards.Count){return}
    try{$id=$u.Engine.RequestPreviewRetry([long[]]@($cards | ForEach-Object {$_.Record.GroupId}));$u.BackgroundOperationId=$id;$u.MaintenancePending=$true;if($u.RecoveryWindow){$u.RecoveryWindow.Tag.BatchId=$id};Set-UiMessage ('Queued preview retry for '+$cards.Count+' selected item(s). Saved previews remain available.');Update-UiControls}catch{Set-UiMessage ('Preview work was not queued: '+$_.Exception.Message)}
}
function Request-UiPreviewHistory {
    $u=$script:reviewUi;$w=$u.RecoveryWindow;if(-not $w){return};$w.FindName('Status').Text='Loading saved preview tasks...';try{$w.Tag.HistoryRequest=$u.Engine.RequestPreviewRetryHistory()}catch{$w.FindName('Status').Text=$_.Exception.Message}
}
function Request-UiPreviewStatus {
    $u=$script:reviewUi;$w=$u.RecoveryWindow;if(-not $w -or -not $w.FindName('Batches').SelectedItem){return};$w.Tag.BatchId=$w.FindName('Batches').SelectedItem.Id;$w.Tag.Batch=$null;$w.FindName('Items').ItemsSource=$null;$w.FindName('Status').Text='Loading recorded outcomes...';Update-UiPreviewRecoveryControls
    try{$w.Tag.StatusRequest=$u.Engine.RequestPreviewRetryStatus($w.Tag.BatchId)}catch{$w.FindName('Status').Text=$_.Exception.Message}
}
function Apply-UiPreviewRecoveryResult($Result) {
    $u=$script:reviewUi
    if($Result.Kind -eq 'PreviewRetry'){
        if($Result.RequestId -ne $u.BackgroundOperationId){return $false};$u.BackgroundOperationId='';$u.MaintenancePending=$false;Set-UiMessage $(if($Result.Success){$Result.Value}else{'Preview task stopped: '+$Result.Error+' Open Preview tasks to resume unfinished work.'});if($u.RecoveryWindow){Request-UiPreviewHistory};if(-not $u.PagePending){Request-UiPage};Update-UiControls;return $true
    }
    $w=$u.RecoveryWindow;if(-not $w -or -not $w.Tag){return $false}
    if($Result.Kind -eq 'PreviewRetryHistory'){
        if($Result.RequestId -ne $w.Tag.HistoryRequest){return $false};if(-not $Result.Success){$w.FindName('Status').Text=$Result.Error;return $true}
        $s=$w.Tag;$s.Loading=$true;$w.FindName('Batches').ItemsSource=$Result.PreviewRetries;$selected=$Result.PreviewRetries | Where-Object {$_.Id -eq $s.BatchId} | Select-Object -First 1;if(-not $selected){$selected=$Result.PreviewRetries | Select-Object -First 1};$w.FindName('Batches').SelectedItem=$selected;$s.Loading=$false
        if($selected){Request-UiPreviewStatus}else{$s.Batch=$null;$s.BatchId='';$w.FindName('Items').ItemsSource=$null;$w.FindName('Status').Text='No saved preview tasks. Select gallery cards and choose Retry previews.';Update-UiPreviewRecoveryControls};return $true
    }
    if($Result.Kind -eq 'PreviewRetryStatus'){
        if($Result.RequestId -ne $w.Tag.StatusRequest){return $false};if(-not $Result.Success){$w.FindName('Status').Text=$Result.Error;return $true};if($Result.PreviewRetry.Id -ne $w.Tag.BatchId){return $false}
        $w.Tag.Batch=$Result.PreviewRetry;$w.FindName('Items').ItemsSource=$Result.PreviewRetry.Items;$w.FindName('Status').Text=('{0}: {1:n0} saved, {2:n0} failed, {3:n0} selected. Resume processes unfinished rows only. Use Refresh for current outcomes; Cancel task is on the main window.' -f $Result.PreviewRetry.State,$Result.PreviewRetry.Saved,$Result.PreviewRetry.Failed,$Result.PreviewRetry.Total);Update-UiPreviewRecoveryControls;return $true
    };return $false
}
function Show-UiPreviewRecovery {
    $u=$script:reviewUi;if(-not $u.RecoveryWindow){
        $markup=@'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Preview recovery" Width="900" Height="640" MinWidth="560" MinHeight="400" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI">
  <Grid Margin="16"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="75"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <TextBlock Text="Retry up to 200 selected gallery items. Good saved previews, fingerprints, review decisions and originals stay intact. Resume unfinished tasks after reopening. Failed sources require a fresh selected retry; changed contents need a rescan." TextWrapping="Wrap" Margin="0,0,0,10"/>
    <WrapPanel Grid.Row="1" Margin="0,0,0,10"><ComboBox x:Name="Batches" Width="460" DisplayMemberPath="Label" Margin="0,0,8,6"/><Button x:Name="Refresh" Content="Refresh" Padding="12,5" Margin="0,0,8,6"/><Button x:Name="Resume" Content="Resume unfinished" Padding="12,5" Margin="0,0,8,6"/><Button x:Name="Discard" Content="Discard pending" Padding="12,5" Margin="0,0,0,6"/></WrapPanel>
    <DataGrid Grid.Row="2" x:Name="Items" IsReadOnly="True" AutoGenerateColumns="False" CanUserAddRows="False" CanUserDeleteRows="False"><DataGrid.Columns><DataGridTextColumn Header="Item" Binding="{Binding GroupId}" Width="80"/><DataGridTextColumn Header="Outcome" Binding="{Binding State}" Width="100"/><DataGridTextColumn Header="Details (affected paths are also in Log)" Binding="{Binding Error}" Width="*"/></DataGrid.Columns></DataGrid>
    <TextBlock Grid.Row="3" x:Name="Status" TextWrapping="Wrap" Margin="0,8"/>
    <Button Grid.Row="4" x:Name="Close" Content="Close" HorizontalAlignment="Center" Padding="24,6" IsCancel="True"/>
  </Grid>
</Window>
'@
        $w=[Windows.Markup.XamlReader]::Parse($markup);$w.Owner=$u.Window;$w.Tag=[pscustomobject]@{HistoryRequest='';StatusRequest='';BatchId='';Batch=$null;Loading=$false};$u.RecoveryWindow=$w
        $w.FindName('Batches').Add_SelectionChanged({$w=$script:reviewUi.RecoveryWindow;if($w -and -not $w.Tag.Loading){Request-UiPreviewStatus}});$w.FindName('Refresh').Add_Click({Request-UiPreviewHistory})
        $w.FindName('Resume').Add_Click({$u=$script:reviewUi;try{$u.BackgroundOperationId=$u.Engine.RequestResumePreviewRetry($u.RecoveryWindow.Tag.BatchId);$u.MaintenancePending=$true;Set-UiMessage 'Resuming unfinished preview work...';Update-UiControls}catch{$u.RecoveryWindow.FindName('Status').Text=$_.Exception.Message}})
        $w.FindName('Discard').Add_Click({$u=$script:reviewUi;try{$u.BackgroundOperationId=$u.Engine.RequestDiscardPreviewRetry($u.RecoveryWindow.Tag.BatchId);$u.MaintenancePending=$true;Set-UiMessage 'Discarding pending preview work; saved results remain.';Update-UiControls}catch{$u.RecoveryWindow.FindName('Status').Text=$_.Exception.Message}})
        $w.FindName('Close').Add_Click({$script:reviewUi.RecoveryWindow.Close()});$w.Add_Closed({param($sender,$event) $sender.FindName('Batches').ItemsSource=$null;$sender.FindName('Items').ItemsSource=$null;$sender.Tag=$null;if($script:reviewUi.RecoveryWindow -eq $sender){$script:reviewUi.RecoveryWindow=$null}})
    };$u.RecoveryWindow.Show();$u.RecoveryWindow.Activate() | Out-Null;Request-UiPreviewHistory;Update-UiPreviewRecoveryControls
}
function Add-UiPreviewRecoveryControls($Window) {
    $u=$script:reviewUi;$u | Add-Member -NotePropertyName RecoveryWindow -NotePropertyValue $null
    foreach($name in @('PreviewTasksButton','RetryPreviewsButton')){$u.Controls[$name]=$Window.FindName($name)};$u.Controls.PreviewTasksButton.Add_Click({Show-UiPreviewRecovery});$u.Controls.RetryPreviewsButton.Add_Click({Start-UiPreviewRetry});Update-UiPreviewRecoveryControls
    $u | Add-Member -NotePropertyName SourcesWindow -NotePropertyValue $null;$u | Add-Member -NotePropertyName SourceApplyPath -NotePropertyValue '';$u | Add-Member -NotePropertyName SourceRefreshRequired -NotePropertyValue $false
    $item=[Windows.Controls.MenuItem]::new();$item.Header='Sources / reconnect...';$item.Add_Click({Show-UiSources});$u.Controls.MaintenanceButton.ContextMenu.Items.Add($item) | Out-Null
}
function Update-UiSourceControls {
    $u=$script:reviewUi;$w=$u.SourcesWindow;if(-not $w -or -not $w.Tag){return};$s=$w.Tag;$ready=$u.Engine -and $u.Engine.Ready -and -not $u.Opening -and -not $u.Closing;$idle=$ready -and -not $u.MaintenancePending -and -not $u.Engine.Status.RunningScan -and -not $s.Loading;$check=$s.Check
    $w.FindName('Check').IsEnabled=$idle -and $s.Root -and (-not $check -or $check.State -in @('applied','superseded'))
    $w.FindName('Resume').IsEnabled=$idle -and $check -and $check.State -eq 'interrupted';$w.FindName('Discard').IsEnabled=$idle -and $check -and $check.State -in @('interrupted','ready','discarding')
    $w.FindName('Apply').IsEnabled=$idle -and $check -and $check.State -eq 'ready' -and $check.Failed -eq 0 -and ($check.Full+$check.Candidates) -gt 0 -and (($check.Candidates+$check.Missing) -eq 0 -or $w.FindName('Accept').IsChecked)
    $w.FindName('NewPath').IsEnabled=$idle;$w.FindName('Browse').IsEnabled=$idle;$w.FindName('Roots').IsEnabled=$ready -and -not $s.Loading;$w.FindName('Next').IsEnabled=$ready -and -not $s.Loading -and $s.HasMore
}
function Request-UiSourceRoots([switch]$First,[switch]$Next) {
    $u=$script:reviewUi;$w=$u.SourcesWindow;if(-not $w){return};$s=$w.Tag;if($First){$s.After=''};if($Next){$s.After=$s.Next};if($s.Root){$s.Hint=$s.Root.Id};$s.Root=$null;$s.Check=$null;$s.StatusRequest='';$s.Loading=$true;$w.FindName('Files').ItemsSource=$null;$w.FindName('Status').Text='Loading recorded sources...';Update-UiSourceControls
    try{$s.RootsRequest=$u.Engine.RequestSourceRoots($s.After)}catch{$s.Loading=$false;$w.FindName('Status').Text=$_.Exception.Message;Update-UiSourceControls}
}
function Select-UiSourceRoot {
    $u=$script:reviewUi;$w=$u.SourcesWindow;if(-not $w -or $w.Tag.Loading){return};$s=$w.Tag;$s.Root=$w.FindName('Roots').SelectedItem;$s.Check=$null;$s.StatusRequest='';$w.FindName('Files').ItemsSource=$null;$w.FindName('Accept').IsChecked=$false
    if($s.Root){$w.FindName('Current').Text='Current: '+$s.Root.Path+"`nRecorded: "+$s.Root.RecordedPath;$w.FindName('NewPath').Text=$s.Root.Path
        if($s.Root.ConnectionId){try{$s.StatusRequest=$u.Engine.RequestSourceConnectionStatus($s.Root.ConnectionId);$s.Loading=$true;$w.FindName('Status').Text='Loading the recorded source check...'}catch{$w.FindName('Status').Text=$_.Exception.Message}}else{$w.FindName('Status').Text='Choose the replacement root, then Check folder. Nothing changes until you apply a completed check.'}
    }else{$w.FindName('Current').Text='No sources on this page.';$w.FindName('Status').Text='Scan or open a database with recorded locations first.'};Update-UiSourceControls
}
function Start-UiSourceOperation([string]$Action) {
    $u=$script:reviewUi;$w=$u.SourcesWindow;if(-not $w -or $u.MaintenancePending){return};$s=$w.Tag
    try{$id=switch($Action){'Check'{$u.Engine.RequestRootRemap($s.Root.Id,$w.FindName('NewPath').Text,$false)}'Resume'{$u.Engine.RequestResumeSourceConnection($s.Check.Id)}'Apply'{$u.SourceApplyPath=$s.Check.Path;$u.Engine.RequestApplySourceConnection($s.Check.Id,[bool]$w.FindName('Accept').IsChecked)}'Discard'{$u.Engine.RequestDiscardSourceConnection($s.Check.Id)}}
        $u.BackgroundOperationId=$id;$u.MaintenancePending=$true;Set-UiMessage ('Source '+$Action.ToLowerInvariant()+' queued. Cached previews remain available.');$w.FindName('Status').Text='Working in the background. Use Cancel task on the main window to stop unfinished work.';Update-UiControls
    }catch{$w.FindName('Status').Text=$_.Exception.Message}
}
function Apply-UiSourceResult($Result) {
    $u=$script:reviewUi
    if($Result.Kind -in @('SourceCheck','SourceApply','SourceDiscard')){
        if($Result.RequestId -ne $u.BackgroundOperationId){return $false};$u.BackgroundOperationId='';$u.MaintenancePending=$false;Set-UiMessage $(if($Result.Success){$Result.Value}else{'Source task stopped: '+$Result.Error+' Refresh Sources to inspect or resume its saved work.'})
        if($Result.Kind -eq 'SourceApply'){if($u.Inspector){$u.Inspector.Close()};if($Result.Success){$u.Controls.SourceBox.Text=$u.SourceApplyPath};[void]$u.InspectView.Advance();$u.Loader.CancelInspect($u.InspectView.Current);$u.SourceRefreshRequired=$true;Refresh-UiDetailsSource;[void]$u.View.Advance();$u.PageRequest='';$u.PagePending=$false;Request-UiPage}
        if($u.SourcesWindow){Request-UiSourceRoots};Update-UiControls;return $true
    }
    $w=$u.SourcesWindow;if(-not $w -or -not $w.Tag){return $false};$s=$w.Tag
    if($Result.Kind -eq 'SourceRoots'){
        if($Result.RequestId -ne $s.RootsRequest){return $false};if(-not $Result.Success){$s.Loading=$false;$w.FindName('Status').Text=$Result.Error;Update-UiSourceControls;return $true};$s.Next=$Result.SourceRoots.Next;$s.HasMore=$Result.SourceRoots.HasMore;$w.FindName('Roots').ItemsSource=$Result.SourceRoots.Rows;$chosen=$Result.SourceRoots.Rows | Where-Object {$_.Id -eq $s.Hint} | Select-Object -First 1;if(-not $chosen){$chosen=$Result.SourceRoots.Rows | Select-Object -First 1};$w.FindName('Roots').SelectedItem=$chosen;$s.Loading=$false;Select-UiSourceRoot;return $true
    }
    if($Result.Kind -eq 'SourceConnectionStatus'){
        if($Result.RequestId -ne $s.StatusRequest){return $false};$s.Loading=$false;if(-not $Result.Success){$w.FindName('Status').Text=$Result.Error;Update-UiSourceControls;return $true};if(-not $s.Root -or $Result.SourceConnection.RootId -ne $s.Root.Id){return $false};$s.Check=$Result.SourceConnection;$w.FindName('Files').ItemsSource=$s.Check.Rows;$w.FindName('Status').Text=('{0}: {1:n0} full matches, {2:n0} quick candidates, {3:n0} missing, {4:n0} failed. {5:n0} recorded files. Only the latest 20 checked paths appear below; Log includes failures and retries. A connection requires at least one match and no failures.' -f $s.Check.State,$s.Check.Full,$s.Check.Candidates,$s.Check.Missing,$s.Check.Failed,$s.Check.Total);Update-UiSourceControls;return $true
    };return $false
}
function Show-UiSources {
    $u=$script:reviewUi;if(-not $u.SourcesWindow){
        $markup=@'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Sources / reconnect" Width="940" Height="700" MinWidth="600" MinHeight="560" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI">
  <Grid Margin="16"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="95"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <TextBlock Text="Check a replacement folder before reconnecting saved media. Recorded identities, reviews, Evidence and completed scan users stay intact. Checks can take time on large collections; cancel and resume unfinished work. Pause scanning first." TextWrapping="Wrap" Margin="0,0,0,10"/>
    <Grid Grid.Row="1"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><ComboBox x:Name="Roots" DisplayMemberPath="Label" MinHeight="30"/><Button Grid.Column="1" x:Name="Refresh" Content="Refresh" Padding="10,5" Margin="6,0,0,0"/><Button Grid.Column="2" x:Name="First" Content="First roots" Padding="10,5" Margin="6,0,0,0"/><Button Grid.Column="3" x:Name="Next" Content="Next roots" Padding="10,5" Margin="6,0,0,0"/></Grid>
    <TextBlock Grid.Row="2" x:Name="Current" TextWrapping="Wrap" MaxHeight="68" Margin="0,8"/>
    <Grid Grid.Row="3"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><TextBox x:Name="NewPath" MinHeight="30" Padding="6" ToolTip="Replacement root: local folder, mapped drive or UNC share"/><Button Grid.Column="1" x:Name="Browse" Content="Browse..." Padding="10,5" Margin="6,0,0,0"/></Grid>
    <StackPanel Grid.Row="4" Margin="0,8"><WrapPanel><Button x:Name="Check" Content="Check folder" Padding="12,5" Margin="0,0,8,5"/><Button x:Name="Resume" Content="Resume check" Padding="12,5" Margin="0,0,8,5"/><Button x:Name="Discard" Content="Discard check" Padding="12,5" Margin="0,0,8,5"/><Button x:Name="Apply" Content="Apply connection" Padding="12,5" Margin="0,0,8,5"/></WrapPanel><CheckBox x:Name="Accept"><TextBlock Text="I accept quick candidate matches and missing files. A quick match does not prove identical full contents." TextWrapping="Wrap"/></CheckBox></StackPanel>
    <DataGrid Grid.Row="5" x:Name="Files" AutoGenerateColumns="False" IsReadOnly="True" CanUserAddRows="False" CanUserDeleteRows="False"><DataGrid.Columns><DataGridTextColumn Header="Recorded path (latest 20)" Binding="{Binding Path}" Width="2*"/><DataGridTextColumn Header="Outcome" Binding="{Binding State}" Width="100"/><DataGridTextColumn Header="Details" Binding="{Binding Error}" Width="3*"/></DataGrid.Columns></DataGrid>
    <TextBlock Grid.Row="6" x:Name="Status" TextWrapping="Wrap" Margin="0,8"/><Button Grid.Row="7" x:Name="Close" Content="Close" Padding="24,6" HorizontalAlignment="Center" IsCancel="True"/>
  </Grid>
</Window>
'@
        $w=[Windows.Markup.XamlReader]::Parse($markup);$w.Owner=$u.Window;$w.Tag=[pscustomobject]@{Root=$null;Check=$null;After='';Next='';Hint='';HasMore=$false;RootsRequest='';StatusRequest='';Loading=$false};$u.SourcesWindow=$w
        $w.FindName('Roots').Add_SelectionChanged({Select-UiSourceRoot});$w.FindName('Refresh').Add_Click({Request-UiSourceRoots});$w.FindName('First').Add_Click({Request-UiSourceRoots -First});$w.FindName('Next').Add_Click({Request-UiSourceRoots -Next});$w.FindName('Accept').Add_Checked({Update-UiSourceControls});$w.FindName('Accept').Add_Unchecked({Update-UiSourceControls})
        foreach($name in @('Check','Resume','Apply','Discard')){$w.FindName($name).Tag=$name;$w.FindName($name).Add_Click({param($sender,$event) Start-UiSourceOperation $sender.Tag})}
        $w.FindName('Browse').Add_Click({$picker=[Windows.Forms.FolderBrowserDialog]::new();$picker.Description='Choose the replacement root or paste a UNC path.';try{if($picker.ShowDialog() -eq [Windows.Forms.DialogResult]::OK){$script:reviewUi.SourcesWindow.FindName('NewPath').Text=$picker.SelectedPath}}finally{$picker.Dispose()}})
        $w.FindName('Close').Add_Click({$script:reviewUi.SourcesWindow.Close()});$w.Add_Closed({param($sender,$event) $sender.FindName('Roots').ItemsSource=$null;$sender.FindName('Files').ItemsSource=$null;$sender.Tag=$null;if($script:reviewUi.SourcesWindow -eq $sender){$script:reviewUi.SourcesWindow=$null}})
    };$u.SourcesWindow.Show();$u.SourcesWindow.Activate() | Out-Null;Request-UiSourceRoots
}
