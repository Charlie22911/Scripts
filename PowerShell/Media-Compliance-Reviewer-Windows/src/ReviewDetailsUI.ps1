function Update-UiDetailsSummary {
    $w=$script:reviewUi.DetailsWindow;$d=$w.Tag.Data;if(-not $d){return}
        $match=if($d.Algorithm -eq 'sha256'){'Verified full-file identity'}else{'Quick candidate identity; equality is not proof of identical files'}
        $w.FindName('Summary').Text=("Group {0} | Publication {1} | {2} | {3:n0} bytes`nRoot: {4}`n{5}`n{6} v{7}: {8}`nSampling: {9}`nPreview: {10} {11}`nLast saved review: {12}" -f $d.GroupId,$d.Sequence,$d.Status,$d.Size,$d.RootPath,$match,$d.Algorithm,$d.AlgorithmVersion,$d.Digest,$d.Sampling,$d.PreviewState,$d.PreviewError,$d.ReviewedAt)
}
function Refresh-UiDetailsSource {
    $u=$script:reviewUi;$w=$u.DetailsWindow;if(-not $w -or -not $w.Tag){return};$s=$w.Tag
    if(-not $s.Data -and -not $s.Dirty -and -not $s.Saving){Request-UiDetailsMetadata $s.GroupId;return}
    $s.Generation=$u.DetailsGeneration.Advance();$s.MetadataRequest='';$s.SourceRequest='';$s.LocationRequest='';$s.LocationPending=$false;$s.HasMore=$false
    $w.FindName('Summary').Text='Refreshing connected source...';$w.FindName('Locations').ItemsSource=$null;$w.FindName('LocationLabel').Text='Refreshing recorded locations...';Update-UiDetailsControls
    try{$s.SourceRequest=$u.Engine.RequestInspector($s.GroupId,$s.Generation)}catch{$w.FindName('Summary').Text='Connected source summary unavailable: '+$_.Exception.Message}
}
function Get-UiPageNeighbor([long]$Group,[int]$Offset) {
    $cards=$script:reviewUi.Cards;for($i=0;$i -lt $cards.Count;$i++){if($cards[$i].Record.GroupId -eq $Group){$index=$i+$Offset;if($index -ge 0 -and $index -lt $cards.Count){return $cards[$index]};return $null}};return $null
}
function Update-UiItemNavigation($Window,[long]$Group,[string]$Previous,[string]$Next,[string]$Label,[bool]$Blocked=$false) {
    $cards=$script:reviewUi.Cards;$index=-1;for($i=0;$i -lt $cards.Count;$i++){if($cards[$i].Record.GroupId -eq $Group){$index=$i;break}}
    $Window.FindName($Previous).IsEnabled=-not $Blocked -and $index -gt 0;$Window.FindName($Next).IsEnabled=-not $Blocked -and $index -ge 0 -and $index -lt $cards.Count-1
    $Window.FindName($Label).Text=if($index -ge 0){'{0} of {1} on this page' -f ($index+1),$cards.Count}else{'Outside the current page'}
}
function Update-UiDetailsControls {
    $u=$script:reviewUi;$w=$u.DetailsWindow;if(-not $w -or -not $w.Tag){return};$s=$w.Tag;$editable=$s.Data -and -not $s.Data.TextReadOnly -and -not $s.Saving
    $w.FindName('Reviewer').IsReadOnly=-not $editable;$w.FindName('Notes').IsReadOnly=-not $editable;$w.FindName('Save').IsEnabled=$editable -and $s.Dirty -and -not $u.Engine.ReportActive;$w.FindName('Reload').IsEnabled=-not $s.Saving
    $w.FindName('MoreLocations').IsEnabled=-not $s.LocationPending -and $s.HasMore -and [bool]$s.Data
    Update-UiItemNavigation $w $s.GroupId 'Previous' 'Next' 'Position' ($s.Saving -or $s.Dirty)
}
function Request-UiDetailsLocations([switch]$Restart) {
    $u=$script:reviewUi;$w=$u.DetailsWindow;if(-not $w -or -not $w.Tag.Data){return};$s=$w.Tag;if($Restart){$s.Cursor='';$s.Shown=0}
    $s.LocationPending=$true;$s.HasMore=$false;$w.FindName('LocationLabel').Text='Loading recorded locations...';Update-UiDetailsControls
    try{$s.LocationRequest=$u.Engine.RequestLocations($s.GroupId,$s.Cursor,100)}catch{$s.LocationPending=$false;$w.FindName('LocationLabel').Text='Locations unavailable: '+$_.Exception.Message;Update-UiDetailsControls}
}
function Request-UiDetailsMetadata([long]$Group) {
    $u=$script:reviewUi;$w=$u.DetailsWindow;$s=$w.Tag;$s.Loading=$true;$s.GroupId=$Group;$s.Data=$null;$s.Dirty=$false;$s.MetadataRequest='';$s.SourceRequest='';$s.LocationRequest='';$s.LocationPending=$false;$s.HasMore=$false;$s.OperationId='';$s.LastRequest=$null
    $s.Generation=$u.DetailsGeneration.Advance();$w.FindName('Summary').Text='Loading item details...';$w.FindName('Reviewer').Clear();$w.FindName('Notes').Clear();$w.FindName('Locations').ItemsSource=$null;$w.FindName('LocationLabel').Text='';$w.FindName('Status').Text='Loading saved metadata...';$s.Loading=$false;Update-UiDetailsControls
    try{$s.MetadataRequest=$u.Engine.RequestInspector($Group,$s.Generation)}catch{$w.FindName('Status').Text=$_.Exception.Message}
}
function Apply-UiDetailsResult($Result) {
    $u=$script:reviewUi;$w=$u.DetailsWindow;if(-not $w -or -not $w.Tag){return $false};$s=$w.Tag
    if($Result.Kind -eq 'Inspector' -and $s.SourceRequest -and $Result.RequestId -eq $s.SourceRequest){
        if($Result.Generation -ne $s.Generation){return $false};$s.SourceRequest=''
        if(-not $Result.Success){$w.FindName('Summary').Text='Connected source summary unavailable: '+$Result.Error;return $true}
        $d=$Result.Inspector;if(-not $s.Data -or $d.GroupId -ne $s.GroupId -or $d.ContentId -ne $s.Data.ContentId){$w.FindName('Summary').Text='Item identity changed. Reload details before saving; your draft remains visible.';Update-UiDetailsControls;return $true}
        $s.Data.RootPath=$d.RootPath;$s.Data.LocationCount=$d.LocationCount;Update-UiDetailsSummary;Request-UiDetailsLocations -Restart;return $true
    }
    if($Result.Kind -eq 'Inspector'){
        if($Result.RequestId -ne $s.MetadataRequest -or $Result.Generation -ne $s.Generation){return $false}
        if(-not $Result.Success){$w.FindName('Summary').Text='Item details unavailable';$w.FindName('Status').Text=$Result.Error;Update-UiDetailsControls;return $true}
        $d=$Result.Inspector;if($d.GroupId -ne $s.GroupId){return $false};$s.Data=$d;$s.Loading=$true;$w.FindName('Reviewer').Text=$d.Reviewer;$w.FindName('Notes').Text=$d.Notes;$s.Loading=$false;$s.Dirty=$false
        Update-UiDetailsSummary
        $w.FindName('Status').Text=if($d.TextReadOnly){'Text exceeds the editor limits, contains unsupported characters, or is stored separately. A bounded display is shown; the complete original is preserved read-only.'}elseif($s.CommitMessage){$s.CommitMessage}else{'Edit reviewer and notes, then Save notes. The bucket stays unchanged.'}
        Update-UiDetailsControls;Request-UiDetailsLocations -Restart;return $true
    }
    if($Result.Kind -eq 'Locations'){
        if($Result.RequestId -ne $s.LocationRequest -or -not $s.Data){return $false};$s.LocationPending=$false
        if(-not $Result.Success){$w.FindName('LocationLabel').Text='Locations unavailable: '+$Result.Error;Update-UiDetailsControls;return $true}
        $p=$Result.Locations;if($p.GroupId -ne $s.GroupId -or $p.ContentId -ne $s.Data.ContentId){$w.FindName('LocationLabel').Text='Item identity changed; reload details.';Update-UiDetailsControls;return $false}
        $w.FindName('Locations').ItemsSource=$p.Rows;$start=$s.Shown+1;$s.Shown+=$p.Rows.Count;$s.Cursor=$p.NextAfter;$s.HasMore=$p.HasMore
        $w.FindName('LocationLabel').Text=if($p.Rows.Count){'Recorded locations {0:n0}-{1:n0} of {2:n0}. Historical means superseded; paths are not live availability checks.' -f $start,$s.Shown,$s.Data.LocationCount}else{'No source location is recorded. A retained original may still be available.'};Update-UiDetailsControls;return $true
    }
    if($Result.Kind -eq 'Notes'){
        if(-not $s.Saving -or $Result.RequestId -ne $s.SaveRequest){return $false};$s.Saving=$false
        if(-not $Result.Success){$w.FindName('Status').Text='Save failed; draft retained. '+$Result.Error;Update-UiDetailsControls;return $true}
        $s.Dirty=$false;$s.CommitMessage='Notes committed successfully.';Request-UiDetailsMetadata $s.GroupId;if(-not $u.PagePending){Request-UiPage};return $true
    };return $false
}
function Save-UiDetailsNotes {
    $u=$script:reviewUi;$w=$u.DetailsWindow;$s=$w.Tag;if($s.Saving -or -not $s.Data -or $s.Data.TextReadOnly -or -not $s.Dirty){return}
    $r=[MediaReviewer.NotesRequest]::new();$r.GroupId=$s.GroupId;$r.ContentId=$s.Data.ContentId;$r.ExpectedRevision=$s.Data.ReviewRevision;$r.Reviewer=$w.FindName('Reviewer').Text;$r.Notes=$w.FindName('Notes').Text
    if(-not $s.LastRequest -or $s.LastRequest.Reviewer -cne $r.Reviewer -or $s.LastRequest.Notes -cne $r.Notes){$s.OperationId=[Guid]::NewGuid().ToString('N')};$r.OperationId=$s.OperationId;$s.LastRequest=$r
    try{$s.SaveRequest=$u.Engine.RequestNotes($r);$s.Saving=$true;$w.FindName('Status').Text='Saving notes...';Update-UiDetailsControls}catch{$w.FindName('Status').Text='Save not queued; draft retained. '+$_.Exception.Message}
}
function Invalidate-UiDetailsGroup([long]$Group) {
    $u=$script:reviewUi;$w=$u.DetailsWindow;if($w -and $w.Tag -and $w.Tag.GroupId -eq $Group){$s=$w.Tag;$s.Generation=$u.DetailsGeneration.Advance();$s.Data=$null;$s.MetadataRequest='';$s.SourceRequest='';$s.LocationRequest='';$s.HasMore=$false;$w.FindName('Locations').ItemsSource=$null;$w.FindName('Summary').Text='This item identity changed. Reload details before saving.';$w.FindName('Status').Text='Any unsaved draft remains visible. Copy it if needed before Reload discards it.';Update-UiDetailsControls}
}
function Show-UiDetails($Card) {
    $u=$script:reviewUi;if($u.DetailsWindow -and ($u.DetailsWindow.Tag.Saving -or $u.DetailsWindow.Tag.Dirty)){$u.DetailsWindow.FindName('Status').Text='Save your notes, or use Reload to discard the draft, before opening another item.';$u.DetailsWindow.Activate() | Out-Null;return}
    if(-not $u.DetailsWindow){
        $markup=@'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Item details and notes" Width="900" Height="720" MinWidth="560" MinHeight="540" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI">
  <Grid Margin="16"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="150"/><RowDefinition Height="Auto"/><RowDefinition Height="110"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="64"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <WrapPanel><Button x:Name="Previous" Content="Previous item" Padding="10,5" Margin="0,0,6,6"/><Button x:Name="Next" Content="Next item" Padding="10,5" Margin="0,0,10,6"/><TextBlock x:Name="Position" VerticalAlignment="Center" TextWrapping="Wrap"/></WrapPanel>
    <TextBox Grid.Row="1" x:Name="Summary" IsReadOnly="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto" Padding="6"/>
    <WrapPanel Grid.Row="2" Margin="0,8"><TextBlock Text="Reviewer" VerticalAlignment="Center" Margin="0,0,8,0"/><TextBox x:Name="Reviewer" Width="200" MaxLength="128"/><Button x:Name="Save" Content="Save notes" Padding="12,5" Margin="10,0"/><Button x:Name="Reload" Content="Reload / discard edits" Padding="10,5"/></WrapPanel>
    <TextBox Grid.Row="3" x:Name="Notes" MaxLength="16384" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto" Padding="6" ToolTip="Current review notes; ordinary metadata is not encrypted"/>
    <DockPanel Grid.Row="4" Margin="0,8"><Button x:Name="MoreLocations" Content="Next 100 locations" DockPanel.Dock="Right" Padding="10,5" Margin="8,0,0,0"/><TextBlock x:Name="LocationLabel" TextWrapping="Wrap" VerticalAlignment="Center"/></DockPanel>
    <DataGrid Grid.Row="5" x:Name="Locations" IsReadOnly="True" AutoGenerateColumns="False" CanUserAddRows="False" CanUserDeleteRows="False" EnableRowVirtualization="True"><DataGrid.Columns><DataGridTextColumn Header="Recorded relative path" Binding="{Binding Path}" Width="*"/><DataGridTextColumn Header="Bytes" Binding="{Binding Size}" Width="100"/><DataGridCheckBoxColumn Header="Historical" Binding="{Binding Historical}" Width="75"/></DataGrid.Columns></DataGrid>
    <TextBlock Grid.Row="6" x:Name="Status" TextWrapping="Wrap" Margin="0,8"/>
    <Button Grid.Row="7" x:Name="Close" Content="Close" IsCancel="True" HorizontalAlignment="Center" Padding="24,6"/>
  </Grid>
</Window>
'@
        $w=[Windows.Markup.XamlReader]::Parse($markup);$w.Owner=$u.Window;$u.DetailsWindow=$w
        $w.FindName('Save').Add_Click({Save-UiDetailsNotes});$w.FindName('Reload').Add_Click({$w=$script:reviewUi.DetailsWindow;$w.Tag.CommitMessage='';Request-UiDetailsMetadata $w.Tag.GroupId});$w.FindName('MoreLocations').Add_Click({Request-UiDetailsLocations})
        $w.FindName('Previous').Add_Click({$w=$script:reviewUi.DetailsWindow;$card=Get-UiPageNeighbor $w.Tag.GroupId -1;if($card){Show-UiDetails $card}});$w.FindName('Next').Add_Click({$w=$script:reviewUi.DetailsWindow;$card=Get-UiPageNeighbor $w.Tag.GroupId 1;if($card){Show-UiDetails $card}})
        foreach($name in @('Reviewer','Notes')){$w.FindName($name).Add_TextChanged({$w=$script:reviewUi.DetailsWindow;if($w -and $w.Tag -and -not $w.Tag.Loading){$w.Tag.Dirty=$true;Update-UiDetailsControls}})}
        $w.FindName('Close').Add_Click({$script:reviewUi.DetailsWindow.Close()})
        $w.Add_Closing({param($sender,$event) if($sender.Tag.Saving){$event.Cancel=$true;$sender.FindName('Status').Text='Waiting for the notes commit; close after its outcome is known.'}elseif($sender.Tag.Dirty -and [Windows.MessageBox]::Show($sender,'Discard the unsaved notes?','Unsaved notes','YesNo','Question') -ne 'Yes'){$event.Cancel=$true}})
        $w.Add_Closed({param($sender,$event) $u=$script:reviewUi;$sender.Tag=$null;$sender.FindName('Notes').Clear();$sender.FindName('Reviewer').Clear();$sender.FindName('Summary').Clear();$sender.FindName('Locations').ItemsSource=$null;if($u.DetailsWindow -eq $sender){$u.DetailsWindow=$null;[void]$u.DetailsGeneration.Advance()}})
        $w.Tag=[pscustomobject]@{GroupId=0L;Generation=0L;Data=$null;MetadataRequest='';SourceRequest='';LocationRequest='';LocationPending=$false;Cursor='';HasMore=$false;Shown=0L;Loading=$true;Saving=$false;Dirty=$false;SaveRequest='';OperationId='';LastRequest=$null;CommitMessage=''}
    }
    $u.DetailsWindow.Tag.CommitMessage='';Request-UiDetailsMetadata $Card.Record.GroupId;$u.DetailsWindow.Show();$u.DetailsWindow.Activate() | Out-Null
}
