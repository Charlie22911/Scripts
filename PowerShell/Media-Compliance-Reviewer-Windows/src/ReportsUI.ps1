function Update-UiReportControls {
    $u=$script:reviewUi;$w=$u.ReportWindow;if(-not $w){return};$ready=$u.Engine -and $u.Engine.Ready -and -not $u.Opening -and -not $u.Closing -and -not $u.MaintenancePending -and -not $u.Engine.Status.RunningScan -and -not $u.Pending.Count -and -not ($u.DetailsWindow -and $u.DetailsWindow.Tag.Saving)
    $w.FindName('Export').IsEnabled=$ready;$w.FindName('Unlock').IsEnabled=$ready -and -not $u.Engine.VaultUnlocked;$w.FindName('Unlock').Content=if($u.Engine.VaultUnlocked){'Originals unlocked'}else{'Unlock originals...'}
}
function Start-UiReport {
    $u=$script:reviewUi;$w=$u.ReportWindow;if(-not $w -or $u.MaintenancePending){return}
    try{if($u.Pending.Count -or ($u.DetailsWindow -and $u.DetailsWindow.Tag.Saving)){throw 'Wait for the pending review to commit before exporting.'};$options=[MediaReviewer.ReportOptions]::new();$options.Destination=$w.FindName('Destination').Text;$options.Format=if($w.FindName('Format').SelectedIndex -eq 1){'HtmlDirectory'}else{'Csv'};$options.IncludeEvidence=[bool]$w.FindName('IncludeEvidence').IsChecked;$options.IncludeSavedPreviews=[bool]$w.FindName('Previews').IsChecked;$options.SpreadsheetSafeText=[bool]$w.FindName('SafeText').IsChecked
        $u.BackgroundOperationId=$u.Engine.RequestReport($options);$u.MaintenancePending=$true;Set-UiMessage 'Report queued. Review changes are paused; cached inspection remains available. Use Cancel task to stop.';$w.Close();Update-UiControls
    }catch{$w.FindName('Status').Text=$_.Exception.Message;Update-UiReportControls}
}
function Show-UiReport {
    $u=$script:reviewUi;if(-not $u.Engine -or -not $u.Engine.Ready -or $u.Opening -or $u.MaintenancePending){return}
    if(-not $u.ReportWindow){$markup=@'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Title="Export review report" Width="680" Height="590" MinWidth="580" MinHeight="540" WindowStartupLocation="CenterOwner" FontFamily="Segoe UI" FontSize="14">
  <Grid Margin="20"><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
    <TextBlock Text="Export all Non-compliant items and their recorded locations. Reports use saved data without reading source folders. Finish or cancel scanning first. Review changes pause while export runs; cached inspection stays available." TextWrapping="Wrap" Margin="0,0,0,14"/>
    <WrapPanel Grid.Row="1" Margin="0,0,0,12"><TextBlock Text="Format" VerticalAlignment="Center" Margin="0,0,10,0"/><ComboBox x:Name="Format" Width="270" SelectedIndex="0" MinHeight="32"><ComboBoxItem Content="CSV file"/><ComboBoxItem Content="Offline HTML folder + complete CSV"/></ComboBox></WrapPanel>
    <StackPanel Grid.Row="2"><TextBlock Text="New destination (existing files and folders are preserved)" Margin="0,0,0,5"/><Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions><TextBox x:Name="Destination" MinHeight="32" Padding="6"/><Button Grid.Column="1" x:Name="Browse" Content="Browse..." Padding="12,5" Margin="8,0,0,0"/></Grid></StackPanel>
    <StackPanel Grid.Row="3" Margin="0,14,0,0"><CheckBox x:Name="Previews" Content="Include saved thumbnails in HTML pages" IsChecked="True" Margin="0,0,0,10"/><CheckBox x:Name="SafeText" IsChecked="True"><TextBlock Text="Protect spreadsheet text from formulas (adds an apostrophe where needed)" TextWrapping="Wrap"/></CheckBox></StackPanel>
    <StackPanel Grid.Row="4" Margin="0,16,0,0"><WrapPanel><CheckBox x:Name="IncludeEvidence" Content="Also include Evidence details" VerticalAlignment="Center" Margin="0,0,12,0"/><Button x:Name="Unlock" Content="Unlock originals..." Padding="12,5"/></WrapPanel><TextBlock Text="Reports are unencrypted. Including Evidence authenticates stored originals and puts their saved details and optional thumbnails into the plaintext report. Original media bytes are not included." TextWrapping="Wrap" Margin="0,8,0,0" Foreground="#526477"/></StackPanel>
    <TextBlock Grid.Row="5" x:Name="Status" TextWrapping="Wrap" Margin="0,14,0,10"/>
    <StackPanel Grid.Row="6" Orientation="Horizontal" HorizontalAlignment="Center"><Button x:Name="Export" Content="Export report" IsDefault="True" Padding="20,7" Margin="6"/><Button x:Name="Close" Content="Close" IsCancel="True" Padding="20,7" Margin="6"/></StackPanel>
  </Grid>
</Window>
'@
        $w=[Windows.Markup.XamlReader]::Parse($markup);$w.Owner=$u.Window;$u.ReportWindow=$w;$w.FindName('Destination').Text=Join-Path ([IO.Path]::GetDirectoryName($u.Engine.DatabasePath)) 'Media-Review-report.csv'
        $w.FindName('Browse').Add_Click({$w=$script:reviewUi.ReportWindow;if($w.FindName('Format').SelectedIndex -eq 1){$picker=[Windows.Forms.FolderBrowserDialog]::new();$picker.Description='Choose a parent folder. A new report folder will be created inside it.';try{if($picker.ShowDialog() -eq [Windows.Forms.DialogResult]::OK){$w.FindName('Destination').Text=Join-Path $picker.SelectedPath ('Media-Review-report-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss'))}}finally{$picker.Dispose()}}else{$picker=[Microsoft.Win32.SaveFileDialog]::new();$picker.Filter='CSV report (*.csv)|*.csv';$picker.FileName='Media-Review-report.csv';$picker.OverwritePrompt=$false;if($picker.ShowDialog($w)){$w.FindName('Destination').Text=$picker.FileName}}})
        $w.FindName('Format').Add_SelectionChanged({$w=$script:reviewUi.ReportWindow;if($w){$path=$w.FindName('Destination').Text;if($w.FindName('Format').SelectedIndex -eq 1 -and $path.EndsWith('.csv',[StringComparison]::OrdinalIgnoreCase)){$w.FindName('Destination').Text=$path.Substring(0,$path.Length-4)}elseif($w.FindName('Format').SelectedIndex -eq 0 -and -not $path.EndsWith('.csv',[StringComparison]::OrdinalIgnoreCase)){$w.FindName('Destination').Text=$path+'.csv'}}})
        $w.FindName('Unlock').Add_Click({Show-UiVaultDialog});$w.FindName('Export').Add_Click({Start-UiReport});$w.FindName('Close').Add_Click({$script:reviewUi.ReportWindow.Close()});$w.Add_Closed({param($sender,$event) if($script:reviewUi.ReportWindow -eq $sender){$script:reviewUi.ReportWindow=$null}})
    };Update-UiReportControls;$u.ReportWindow.Show();$u.ReportWindow.Activate() | Out-Null
}
function Add-UiReportControls {
    $u=$script:reviewUi;$u | Add-Member -NotePropertyName ReportWindow -NotePropertyValue $null
    $item=[Windows.Controls.MenuItem]::new();$item.Header='Export review report...';$item.Add_Click({Show-UiReport});$u.Controls.MaintenanceButton.ContextMenu.Items.Add($item) | Out-Null
}
